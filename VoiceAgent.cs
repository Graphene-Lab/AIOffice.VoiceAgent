using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using Whisper.net;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Cross-platform voice agent executable. Communicates via JSON Lines on stdin/stdout.
///
/// Protocol (stdin):
///   {"cmd":"start"}                         — begin speech recognition
///   {"cmd":"speak","text":"…","lang":"…"}   — speak text, then resume recognition
///   {"cmd":"stop"}                          — stop recognition and exit
///
/// Protocol (stdout):
///   {"type":"ready"}              — process initialized (tts field indicates engine)
///   {"type":"transcript","text"}  — user speech recognized
///   {"type":"status","text"}      — progress of the automatic dependency setup
///   {"type":"done"}               — speak command finished
///   {"type":"error","text"}       — error occurred
///
/// Recognition chain per platform (primary first, fallback after):
/// <list type="bullet">
/// <item>Windows — WinRT agent (AIOffice.VoiceAgent.Win, external exe driven by Voice.cs), whisper as fallback.</item>
/// <item>iOS — system speech framework first (SFSpeechRecognizer binding, implemented in the
/// iOS/MAUI client), whisper as fallback (this engine).</item>
/// <item>Linux/macOS — whisper only.</item>
/// </list>
/// TTS is Kokoro (neural, cross-platform), shared with the Windows agent through
/// <see cref="KokoroTts"/>; the model asset lives in this project.
/// </summary>
public class VoiceAgent
{
    private WhisperRecognizer? _recognizer;

    /// <summary>Shared Kokoro TTS engine (model asset + voices + playback logic).</summary>
    private KokoroTts? _tts;

    /// <summary>Background TTS initialization task; true when the engine is ready.</summary>
    private Task<bool>? _ttsTask;

    /// <summary>True while a streaming speak session is active (recognition paused between chunks).</summary>
    private bool _streamingSession;

    /// <summary>TTS processing mode: Fast (SpeakFast, default) or Full (Speak).</summary>
    private string _ttsMethod = "fast";

    /// <summary>Language code passed with the last "start" command (e.g. "it"). Null = auto-detect.</summary>
    private string? _recognitionLang;

    private CancellationTokenSource? _shutdownCts;

    // ─── Entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Entry point. Supports <c>--check</c> (dependency self-test), <c>--debug</c> (attach a
    /// debugger) and <c>--tts-method full|fast</c> (TTS mode; fast is the default). Runs the
    /// automatic dependency setup, then enters the stdin command loop.
    /// </summary>
    public static async Task Main(string[] args)
    {
        Log.Initialize(Log.IsEnabled);
        Log.LogStep("=== VoiceAgent (whisper.net) starting ===");

        // Force UTF-8 for stdin/stdout — the parent process sends/receives JSON-Lines over pipes.
        Console.SetIn(new StreamReader(
            Console.OpenStandardInput(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            detectEncodingFromByteOrderMarks: true));

        if (args.Contains("--debug"))
            Debugger.Launch();

        if (args.Contains("--check"))
        {
            // Dependency self-test: verify/install everything, load the whisper model and the
            // native runtime, then exit. Used by the build verification and by users pre-warming.
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Dependencies.EnsureAll();
            using var factory = WhisperFactory.FromPath(Dependencies.ModelPath);
            using var processor = factory.CreateBuilder().WithLanguage("auto").Build();
            Console.WriteLine("CHECK OK: whisper model and native runtime load successfully");
            return;
        }

        // --tts-file <out.wav> <text> [--lang <iso2>]: synthesize speech to a wav file and exit
        // (no playback). Used by the end-to-end comprehension tests and to pre-generate audio.
        int ttsFileIdx = Array.IndexOf(args, "--tts-file");
        if (ttsFileIdx >= 0 && ttsFileIdx + 2 < args.Length)
        {
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var lang = "it";
            var langIdx = Array.IndexOf(args, "--lang");
            if (langIdx >= 0 && langIdx + 1 < args.Length) lang = args[langIdx + 1];

            var voicesDir = KokoroTts.FindVoicesDir();
            if (voicesDir != null) KokoroVoiceManager.LoadVoicesFromPath(voicesDir);
            var modelPath = KokoroTts.FindModel() ?? await KokoroLoader.DownloadModelAsync(KModel.float32);
            using var synth = new KokoroWavSynthesizer(modelPath);
            var voice = KokoroTts.GetVoiceForLanguage(lang) ?? KokoroVoiceManager.GetVoice("af_heart");
            if (voice == null)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { type = "error", text = "No TTS voice available" }));
                return;
            }
            var audio = synth.Synthesize(args[ttsFileIdx + 2], voice);
            KokoroWavSynthesizer.SaveAudioToFile(audio, args[ttsFileIdx + 1]);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "status", text = $"WAV saved: {args[ttsFileIdx + 1]} ({audio.Length} bytes)" }));
            return;
        }

        // --transcribe <file.wav> [--lang <iso2>]: transcribe a wav file and print the text.
        // Used by the end-to-end comprehension tests.
        int transcribeIdx = Array.IndexOf(args, "--transcribe");
        if (transcribeIdx >= 0 && transcribeIdx + 1 < args.Length)
        {
            Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            Dependencies.EnsureAll();
            var lang = "auto";
            var langIdx = Array.IndexOf(args, "--lang");
            if (langIdx >= 0 && langIdx + 1 < args.Length) lang = args[langIdx + 1];

            // whisper.net only accepts 16 kHz mono PCM: downmix and resample with NAudio
            // (managed, cross-platform) before feeding the processor.
            using var reader = new NAudio.Wave.WaveFileReader(args[transcribeIdx + 1]);
            NAudio.Wave.ISampleProvider samples = reader.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
                ? new NAudio.Wave.SampleProviders.WaveToSampleProvider(reader)
                : new NAudio.Wave.SampleProviders.Pcm16BitToSampleProvider(reader);
            if (reader.WaveFormat.Channels > 1)
                samples = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(samples);
            if (reader.WaveFormat.SampleRate != 16000)
                samples = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(samples, 16000);
            var pcm16 = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(samples);
            // WaveFileWriter disposes its target stream, so use a temp file instead of a MemoryStream.
            var tmpWav = Path.GetTempFileName();
            using (var writer = new NAudio.Wave.WaveFileWriter(tmpWav, new NAudio.Wave.WaveFormat(16000, 16, 1)))
            {
                var buf = new byte[8192];
                int read;
                while ((read = pcm16.Read(buf, 0, buf.Length)) > 0)
                    writer.Write(buf, 0, read);
            }

            string text;
            using (var wav16 = File.OpenRead(tmpWav))
            using (var factory = WhisperFactory.FromPath(Dependencies.ModelPath))
            using (var processor = factory.CreateBuilder().WithLanguage(lang).Build())
            {
                var sb = new StringBuilder();
                await foreach (var seg in processor.ProcessAsync(wav16))
                    sb.Append(seg.Text);
                text = sb.ToString().Trim();
            }
            // Delete AFTER every handle on the file is closed: on Windows File.Delete fails
            // while a stream is still open ("file in use") — POSIX unlink would hide the bug.
            File.Delete(tmpWav);
            Console.WriteLine(JsonSerializer.Serialize(new { type = "transcript", text }));
            return;
        }

        Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;
        Log.LogStep($"Working directory: {Environment.CurrentDirectory}");

        var ttsMethod = "fast";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--tts-method" && args[i + 1] == "full")
                ttsMethod = "full";

        var agent = new VoiceAgent { _ttsMethod = ttsMethod };
        await agent.RunAsync();
        Log.LogStep("=== VoiceAgent exited ===");
    }

    // ─── Main loop ────────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        _shutdownCts = new CancellationTokenSource();

        // Automatic dependency setup (model download on first run) before reporting ready,
        // so recognition is functional the moment Voice.cs sends "start".
        Dependencies.EnsureAll();

        _recognizer = new WhisperRecognizer();
        _recognizer.Transcript += text => WriteJson(new { type = "transcript", text });
        _recognizer.Error += message => WriteError(message);

        // TTS loads in the background (first use downloads the ~320 MB Kokoro model);
        // recognition must not wait for it.
        StartTtsBackground();

        WriteJson(new { type = "ready", tts = KokoroTts.FindModel() != null ? "kokoro" : "kokoro (downloading)" });
        Log.LogStep($"Ready sent, TTS={KokoroTts.FindModel() != null}");

        try
        {
            while (!_shutdownCts.Token.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync();
                if (line == null)
                {
                    Log.LogStep("stdin EOF, exiting main loop");
                    break;
                }

                Log.LogStep($"stdin cmd: {line}");

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch
                {
                    Log.LogStep($"Invalid JSON from stdin: {line}");
                    continue;
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;

                    switch (cmd)
                    {
                        case "start":
                            _recognitionLang = root.TryGetProperty("lang", out var sl) ? sl.GetString() : null;
                            Log.LogStep($"Start command (lang={_recognitionLang ?? "auto"})");
                            _recognizer.Start(_recognitionLang);
                            break;

                        case "speak":
                            var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                            var lang = root.TryGetProperty("lang", out var l) ? l.GetString() : null;
                            var streaming = root.TryGetProperty("streaming", out var s) && s.GetBoolean();
                            Log.LogStep($"Speak: lang={lang}, streaming={streaming}, text_len={text?.Length ?? 0}");
                            await SpeakAndPauseRecognitionAsync(text, lang, streaming);
                            WriteJson(new { type = "done" });
                            break;

                        case "stop":
                            Log.LogStep("Stop command received");
                            StopAll();
                            return;
                    }
                }
            }
        }
        finally
        {
            Log.LogStep("=== VoiceAgent shutting down ===");
            StopAll();
            _recognizer?.Dispose();
            _tts?.Dispose();
        }
    }

    private void StartTtsBackground()
    {
        _tts = new KokoroTts(s => WriteJson(new { type = "status", text = s }), _ttsMethod);
        _ttsTask = Task.Run(_tts.InitializeAsync);
    }

    // ─── TTS ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops recognition, speaks the text, then restarts recognition.
    /// Uses Kokoro neural TTS when available.
    /// </summary>
    private async Task SpeakAndPauseRecognitionAsync(string? text, string? langCode = null, bool streaming = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.LogStep("Speak skipped: empty text");
            return;
        }

        var effectiveLang = langCode ?? _recognitionLang;
        Log.LogStep($"Speak: text_len={text.Length}, lang={effectiveLang ?? "auto"}, streaming={streaming}");

        if (streaming)
        {
            if (!_streamingSession)
            {
                _streamingSession = true;
                Log.LogStep("First streaming chunk: pausing recognition");
                _recognizer?.Stop();
                await Task.Delay(300);
            }
        }
        else if (_streamingSession)
        {
            _streamingSession = false;
            Log.LogStep("Final streaming chunk: ending streaming session");
        }
        else
        {
            Log.LogStep("Non-streaming speak: stopping recognition");
            _recognizer?.Stop();
            await Task.Delay(300);
        }

        if (_ttsTask == null)
        {
            WriteError("TTS engine not available");
            return;
        }

        // First use blocks until the model download/load completes; later calls return immediately.
        if (!await _ttsTask || _tts == null)
        {
            WriteError("TTS engine not available");
            return;
        }

        if (!await _tts.SpeakAsync(text, effectiveLang))
        {
            WriteError($"No TTS voice for language '{effectiveLang}'");
        }

        if (!streaming)
        {
            Log.LogStep("Non-streaming speak: restarting recognition after 500ms delay");
            await Task.Delay(500);
            _recognizer?.Start(_recognitionLang);
            Log.LogStep("Recognition restarted after speak");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private void StopAll()
    {
        Log.LogStep("StopAll");
        _recognizer?.Stop();
        _shutdownCts?.Cancel();
    }

    private static void WriteJson(object obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    private static void WriteError(string message)
    {
        WriteJson(new { type = "error", text = message });
    }
}
