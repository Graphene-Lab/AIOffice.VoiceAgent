using System.Diagnostics;
using System.Text;
using Whisper.net;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Continuous speech recognition via whisper.net. Captures 16 kHz mono PCM from the microphone
/// (NAudio on Windows, an <c>arecord</c> subprocess on Linux), splits utterances with an
/// energy-based VAD (adaptive noise floor + 700 ms silence hangover) and transcribes each
/// utterance with whisper, raising <see cref="Transcript"/>.
/// </summary>
public sealed class WhisperRecognizer : IAgentRecognizer
{
    /// <summary>Raised with the recognized text of a completed utterance.</summary>
    public event Action<string>? Transcript;

    /// <summary>Raised when recognition cannot continue (mic, model or native runtime failure).</summary>
    public event Action<string>? Error;

    private const int SampleRate = 16000;
    private const int FrameSamples = 160;            // 10 ms frame
    private const int HangoverFrames = 70;           // 700 ms of silence ends an utterance
    private const int MinUtteranceMs = 350;
    private const int MaxUtteranceMs = 30_000;
    private const double MinThreshold = 0.006;

    private readonly object _sync = new();
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string _language = "auto";
    private Process? _arecord;
    private CancellationTokenSource? _captureCts;
    private volatile bool _transcribing;

    // External-input mode: the recognizer is fed PCM by the host (SIP media bridge) instead of
    // capturing from the microphone. VAD + whisper live here either way — the media never
    // re-implements them (architectural rule: media = I/O only, see ARCHITECTURE.md).
    private bool _externalInput;

    /// <summary>IAgentRecognizer — true when audio arrives via <see cref="FeedExternalPcm"/>.</summary>
    public bool ExternalInput { get => _externalInput; set => _externalInput = value; }

    /// <summary>IAgentRecognizer — starts recognition with the given language (auto-detect when
    /// null/empty), using the current <see cref="ExternalInput"/> mode.</summary>
    public Task StartAsync(string? lang) { Start(lang, ExternalInput); return Task.CompletedTask; }

    /// <summary>IAgentRecognizer — stops recognition without disposing.</summary>
    public Task StopAsync() { Stop(); return Task.CompletedTask; }

    // VAD state
    private MemoryStream _utterance = new();
    private DateTime _speechStart;
    private double _noiseFloor = 0.004;
    private int _hangover;

    /// <summary>Starts capturing and recognizing. Idempotent. Null/empty language means auto-detect.
    /// When <paramref name="externalInput"/> is true no microphone is opened: audio arrives via
    /// <see cref="FeedExternalPcm"/> (the host pushes 16 kHz mono PCM chunks).</summary>
    public void Start(string? language = "auto", bool externalInput = false)
    {
        lock (_sync)
        {
            if (_captureCts != null) return;
            _language = string.IsNullOrWhiteSpace(language) ? "auto" : language;
            _externalInput = externalInput;
            _captureCts = new CancellationTokenSource();
            Log.LogStep($"WhisperRecognizer starting (lang={_language}, externalInput={externalInput})");

            try
            {
                if (externalInput)
                {
                    // No microphone: the host feeds PCM through FeedExternalPcm.
                    Log.LogStep("External-input mode: awaiting host PCM");
                }
                else if (OperatingSystem.IsWindows())
                {
                    StartWindowsCapture();
                }
                else
                {
                    StartArecordCapture();
                }
            }
            catch (Exception ex)
            {
                Log.LogStep($"Capture start failed: {ex.Message}");
                Error?.Invoke(ex.Message);
                Stop();
            }
        }
    }

    /// <summary>Feeds an external PCM chunk (16 kHz mono, 16-bit) into the VAD → whisper chain.
    /// Only valid in external-input mode. The host (SIP bridge) sends the decoded media here.</summary>
    public void FeedExternalPcm(byte[] pcm)
    {
        if (!_externalInput || pcm == null || pcm.Length == 0) return;
        ProcessPcm(pcm, pcm.Length);
    }

    /// <summary>Stops capturing. Pending transcription of a partial utterance is dropped.</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_captureCts == null) return;
            Log.LogStep("WhisperRecognizer stopping");
            _captureCts.Cancel();
            _captureCts.Dispose();
            _captureCts = null;

            try { _arecord?.Kill(entireProcessTree: true); } catch { }
            _arecord?.Dispose();
            _arecord = null;

            _utterance.Dispose();
            _utterance = new MemoryStream();
            _hangover = 0;
            _speechStart = default;
        }
    }

    // ─── Capture ──────────────────────────────────────────────────────────

    private void StartWindowsCapture()
    {
        var waveIn = new NAudio.Wave.WaveInEvent
        {
            DeviceNumber = 0,
            WaveFormat = new NAudio.Wave.WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };
        waveIn.DataAvailable += (_, e) => ProcessPcm(e.Buffer, e.BytesRecorded);
        waveIn.RecordingStopped += (_, e) =>
        {
            waveIn.Dispose();
            if (e.Exception != null)
                Error?.Invoke($"Microphone capture stopped: {e.Exception.Message}");
        };
        waveIn.StartRecording();
        Log.LogStep("NAudio capture started");
    }

    private void StartArecordCapture()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "arecord",
            Arguments = "-q -t raw -f S16_LE -r 16000 -c 1 -D default",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        _arecord = Process.Start(psi)
            ?? throw new InvalidOperationException("arecord failed to start. Run 'sudo apt-get install -y alsa-utils'.");
        _arecord.StandardError.ReadToEndAsync(); // drain stderr to avoid pipe deadlock
        _captureTask = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            var stream = _arecord.StandardOutput.BaseStream;
            try
            {
                while (!_captureCts!.IsCancellationRequested)
                {
                    var read = await stream.ReadAsync(buffer, _captureCts.Token);
                    if (read == 0) break;
                    ProcessPcm(buffer, read);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.LogStep($"arecord read error: {ex.Message}");
                Error?.Invoke($"Microphone capture failed: {ex.Message}");
            }
        });
        Log.LogStep("arecord capture started");
    }

    private Task? _captureTask;

    // ─── VAD + utterance handling ─────────────────────────────────────────

    private void ProcessPcm(byte[] data, int count)
    {
        if (_transcribing || _captureCts == null) return;

        lock (_sync)
        {
            var pcm = data.AsSpan(0, count);
            _utterance.Write(pcm);

            // Trim to the max utterance length and force-flush if exceeded
            if (_utterance.Length > MaxUtteranceMs * SampleRate / 1000 * 2)
            {
                Log.LogStep("Utterance exceeded max duration, flushing");
                FlushUtteranceLocked();
                return;
            }

            // VAD in 10 ms frames over the newly appended bytes
            var frameBytes = FrameSamples * 2;
            var end = (int)_utterance.Length;
            var start = end - count;
            for (int offset = start; offset + frameBytes <= end; offset += frameBytes)
            {
                var rms = Rms(pcm[(offset - start)..(offset - start + frameBytes)]);
                UpdateVad(rms);
            }
        }
    }

    private double Rms(ReadOnlySpan<byte> frame)
    {
        long sumSquares = 0;
        for (int i = 0; i < frame.Length; i += 2)
        {
            var sample = (short)(frame[i] | frame[i + 1] << 8);
            sumSquares += (long)sample * sample;
        }
        return Math.Sqrt(sumSquares / (double)(frame.Length / 2)) / short.MaxValue;
    }

    private void UpdateVad(double rms)
    {
        var threshold = Math.Max(_noiseFloor * 3.5, MinThreshold);
        var now = DateTime.UtcNow;

        if (_hangover > 0) // hangover state: silence after speech
        {
            if (rms > threshold)
            {
                _hangover = 0; // speech resumed, keep collecting
            }
            else if (++_hangover >= HangoverFrames)
            {
                FlushUtteranceLocked();
            }
        }
        else if (_speechStart != default) // speech state
        {
            if (rms <= threshold)
                _hangover = 1;
            else if ((now - _speechStart).TotalMilliseconds > MaxUtteranceMs)
                FlushUtteranceLocked();
        }
        else if (rms > threshold) // idle → speech start
        {
            _speechStart = now;
        }
        else
        {
            // idle: keep the noise floor tracking ambient level
            _noiseFloor = Math.Min(_noiseFloor, Math.Max(rms, 0.0005));
            _noiseFloor = _noiseFloor * 0.995 + Math.Min(rms, threshold) * 0.005;
        }
    }

    private void FlushUtteranceLocked()
    {
        var duration = _speechStart == default ? 0 : (DateTime.UtcNow - _speechStart).TotalMilliseconds;
        var bytes = _utterance.ToArray();
        _utterance.SetLength(0);
        _utterance.Position = 0;
        _hangover = 0;
        _speechStart = default;

        if (duration < MinUtteranceMs || bytes.Length < SampleRate / 1000 * MinUtteranceMs * 2)
        {
            Log.LogStep($"Utterance too short ({duration:F0} ms), skipped");
            return;
        }

        Log.LogStep($"Utterance complete ({duration:F0} ms, {bytes.Length / 2} samples), transcribing...");
        _transcribing = true;
        _ = Task.Run(async () =>
        {
            try
            {
                var text = await TranscribeAsync(bytes);
                if (!string.IsNullOrWhiteSpace(text))
                    Transcript?.Invoke(text.Trim());
            }
            catch (Exception ex)
            {
                Log.LogStep($"Transcription failed: {ex.Message}");
                Error?.Invoke($"Transcription failed: {ex.Message}");
            }
            finally
            {
                _transcribing = false;
            }
        });
    }

    // ─── Whisper ──────────────────────────────────────────────────────────

    private async Task<string?> TranscribeAsync(byte[] pcm)
    {
        var processor = GetProcessor();
        using var wav = new MemoryStream();
        WriteWavHeader(wav, pcm.Length);
        wav.Write(pcm);
        wav.Position = 0;

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(wav))
            sb.Append(segment.Text);
        Log.LogStep($"Whisper result: '{sb}'");
        return sb.ToString();
    }

    private WhisperProcessor GetProcessor()
    {
        lock (_sync)
        {
            if (_processor != null && _processorLanguage == _language) return _processor;
            _factory?.Dispose();
            _processor?.Dispose();
            _factory = null;
            _processor = null;

            if (!File.Exists(Dependencies.ModelPath))
                throw new InvalidOperationException($"Whisper model not found at {Dependencies.ModelPath}");

            Log.LogStep("Loading whisper model...");
            _factory = WhisperFactory.FromPath(Dependencies.ModelPath);
            Log.LogStep($"Whisper runtime in use: {Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary}");
            _processor = _factory.CreateBuilder().WithLanguage(_language).Build();
            _processorLanguage = _language;
            Log.LogStep($"Whisper model loaded (lang={_language})");
            return _processor;
        }
    }

    private string? _processorLanguage;

    private static void WriteWavHeader(Stream stream, int dataLength)
    {
        var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataLength);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);          // PCM
        w.Write((short)1);          // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);    // byte rate
        w.Write((short)2);          // block align
        w.Write((short)16);         // bits per sample
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataLength);
    }

    /// <summary>Stops capture and releases the whisper model and native resources.</summary>
    public void Dispose()
    {
        Stop();
        _factory?.Dispose();
        _processor?.Dispose();
    }
}
