using System.Diagnostics;
using System.Globalization;
using System.Text;
using KokoroSharp;
using KokoroSharp.Core;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Single home of the Kokoro TTS assets and logic, shared by the cross-platform agent
/// (AIOffice.VoiceAgent) and the Windows agent (AIOffice.VoiceAgent.Win):
/// <list type="bullet">
/// <item>Model asset (kokoro.onnx, ~325 MB): build-time asset next to the agent, or auto-downloaded
/// once at runtime (same file: Lyrcaxis/KokoroSharpBinaries v2.0.0).</item>
/// <item>Voices (.npy): loaded from the <c>voices</c> folder next to the agent or in its parent
/// (the app base dir, where the app publishes a single shared voices folder).</item>
/// <item>Playback: on Linux/macOS the audio is synthesized to a WAV and played with the system
/// player (aplay/afplay) — KokoroSharp's built-in OpenAL playback writes silence there; on Windows
/// it uses KokoroSharp's native playback.</item>
/// </list>
/// </summary>
public class KokoroTts : IDisposable
{
    private readonly Action<string>? _status;
    private KokoroTTS? _playback;
    private KokoroWavSynthesizer? _synth;
    private KokoroVoice? _defaultVoice;
    private string? _modelPath;
    private bool _ready;

    /// <summary>TTS processing mode: Fast (SpeakFast, default) or Full (Speak).</summary>
    public string Method { get; init; } = "fast";

    /// <summary>True once the model and voices are loaded.</summary>
    public bool IsReady => _ready;

    /// <summary>
    /// Creates the TTS engine. <paramref name="status"/> receives progress strings (download
    /// percentage, readiness); <paramref name="method"/> selects "fast" (default) or "full".
    /// </summary>
    public KokoroTts(Action<string>? status = null, string? method = null)
    {
        _status = status;
        if (method != null) Method = method;
    }

    /// <summary>
    /// Loads the model (reusing the app asset when present, otherwise downloading it once with
    /// progress reporting) and the voices. Never throws: returns false on failure.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            var voicesDir = FindVoicesDir();
            if (voicesDir != null)
                KokoroVoiceManager.LoadVoicesFromPath(voicesDir);

            var modelPath = FindModel();
            if (modelPath != null)
            {
                Log.LogStep($"Kokoro model reused from: {modelPath}");
            }
            else
            {
                _status?.Invoke("Downloading Kokoro TTS model (~320 MB), first run only...");
                var lastReported = -1;
                modelPath = await KokoroLoader.DownloadModelAsync(KModel.float32, p =>
                {
                    var percent = (int)(p * 100);
                    if (percent != lastReported && percent % 10 == 0)
                    {
                        lastReported = percent;
                        _status?.Invoke($"Downloading Kokoro TTS model... {percent}% (first run only)");
                    }
                });
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // KokoroSharp's built-in OpenAL playback is unreliable on Linux (connects to the
                // pulse sink but writes silence); synthesize to WAV and play it through the system
                // player (aplay / afplay) instead.
                _synth = new KokoroWavSynthesizer(modelPath);
            }
            else
            {
                _playback = new KokoroTTS(modelPath);
            }

            // PRE-WARM the streaming synthesizer: the streaming path (StreamToSinkAsync) and the
            // render path (SynthesizePcm) need KokoroWavSynthesizer, which loads the ONNX model on
            // construction. Creating it here (eagerly, at startup) removes the first-message
            // latency — otherwise the FIRST chat reply pays the model load.
            if (_modelPath == null) _modelPath = modelPath;
            if (_synth == null && modelPath != null)
            {
                Log.LogStep("Kokoro TTS pre-warming the streaming synthesizer...");
                _synth = new KokoroWavSynthesizer(modelPath);
                Log.LogStep("Kokoro streaming synthesizer ready");
            }

            _defaultVoice = KokoroVoiceManager.GetVoice("af_heart");

            // WARM THE FIRST INFERENCE: the very first Synthesize() call pays the ONNX
            // session/JIT + phonemizer warm-up (measured ~1-2 s on the FIRST real sentence).
            // A one-word warm-up ("ok") is NOT enough: the phonemizer warms on real text, so
            // warm up with an actual sentence to move the whole cost out of the first reply.
            if (_synth != null && _defaultVoice != null)
            {
                try
                {
                    Log.LogStep("Kokoro TTS warming the first inference...");
                    _synth.Synthesize("Questa è una breve prova di sintesi vocale.", _defaultVoice);
                    Log.LogStep("Kokoro first-inference warm-up done");
                }
                catch (Exception ex)
                {
                    Log.LogStep($"Kokoro warm-up inference failed: {ex.Message}");
                }
            }

            _ready = true;
            Log.LogStep("Kokoro TTS loaded successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.LogStep($"Kokoro TTS failed: {ex.Message}");
            _status?.Invoke($"TTS unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Strips markdown from the text and speaks it with the voice matching <paramref name="langCode"/>
    /// (or the default voice when unsupported). Returns false when no engine or voice is available.
    /// On Linux/macOS the call completes only when the system player exits (real playback); on
    /// Windows it completes on the engine's speech-completed event.
    /// </summary>
    public async Task<bool> SpeakAsync(string text, string? langCode)
    {
        if (!_ready) return false;
        text = StripMarkdown(text);
        if (string.IsNullOrEmpty(text)) return true;

        KokoroVoice? voice = null;
        var langUnsupported = false;
        if (langCode != null)
        {
            voice = GetVoiceForLanguage(langCode);
            langUnsupported = voice == null;
        }

        if (voice == null && langUnsupported) return false;

        if (_synth != null)
        {
            var useVoice = voice ?? _defaultVoice;
            if (useVoice == null) return false;
            Log.LogStep($"Kokoro TTS starting (method={Method})");
            var audioBytes = _synth.Synthesize(text, useVoice);
            var wavPath = Path.Combine(Path.GetTempPath(), $"kokoro_{Guid.NewGuid():N}.wav");
            try
            {
                KokoroWavSynthesizer.SaveAudioToFile(audioBytes, wavPath);
                Log.LogStep($"Synthesized {audioBytes.Length} bytes, playing {wavPath}");
                var player = OperatingSystem.IsMacOS() ? "afplay" : "aplay";
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = player,
                    Arguments = $"-q \"{wavPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (proc != null)
                    await proc.WaitForExitAsync();
                Log.LogStep("Kokoro TTS completed (player exited)");
            }
            finally
            {
                try { File.Delete(wavPath); } catch { }
            }
            return true;
        }

        var tts = _playback;
        if (tts == null) return false;
        var targetVoice = voice ?? _defaultVoice;
        if (targetVoice == null) return false;
        Log.LogStep($"Kokoro TTS starting (method={Method})");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(SpeechCompletionPacket _) => tcs.TrySetResult();
        void OnCanceled(SpeechCancellationPacket _) => tcs.TrySetResult();
        tts.OnSpeechCompleted += OnCompleted;
        tts.OnSpeechCanceled += OnCanceled;
        try
        {
            if (Method == "full")
                tts.Speak(text, targetVoice);
            else
                tts.SpeakFast(text, targetVoice);
            await tcs.Task;
            Log.LogStep("Kokoro TTS completed");
        }
        finally
        {
            tts.OnSpeechCompleted -= OnCompleted;
            tts.OnSpeechCanceled -= OnCanceled;
        }
        return true;
    }

    /// <summary>
    /// Renders text to raw 16-bit PCM (no WAV header) with the voice matching
    /// <paramref name="langCode"/>. Returns null when no engine/voice is available (the caller
    /// may then fall back to the OS synthesizer) or an empty array for empty text.
    /// Used by the SIP media bridge ("speak render") — playback-free, output-only.
    /// </summary>
    public byte[]? SynthesizePcm(string text, string? langCode)
    {
        if (!_ready) return null;
        text = StripMarkdown(text);
        if (string.IsNullOrEmpty(text)) return Array.Empty<byte>();

        var useVoice = ResolveVoice(langCode, out var langUnsupported);
        if (langUnsupported) return null;   // unsupported language → OS fallback
        if (useVoice == null) return null;

        // The synthesizer may not exist yet on Windows (playback engine only): create it lazily.
        if (_synth == null)
        {
            if (_modelPath == null) return null;
            _synth = new KokoroWavSynthesizer(_modelPath);
        }
        Log.LogStep($"Kokoro TTS render: {text.Length} chars, lang={langCode ?? "default"}");
        return _synth.Synthesize(text, useVoice);
    }

    /// <summary>
    /// STREAMING synthesis (the global, OS-agnostic path for TTS engines that support streaming):
    /// renders each sentence to 24 kHz PCM via <see cref="KokoroWavSynthesizer"/> and pushes it
    /// to <paramref name="sink"/> back-to-back — ONE continuous audio stream, no file, no gaps
    /// (the media provides the sink: NAudio device on Windows, aplay/paplay stdin on Linux/macOS,
    /// RTP in the SIP bridge). Returns false when the engine/voice is unavailable (the caller may
    /// then fall back to the OS synthesizer or the parked file-based path).
    ///
    /// ARCHITECTURAL NOTE: the sentence splitting here is ONLY the driver of incremental synthesis
    /// (Kokoro cannot synthesize partial tokens) — it is NOT a delivery strategy. Benchmark
    /// (2026-08-22): feeding one long text vs many small chunks produces the SAME time-to-first-audio
    /// (1650 ms, e2e/benchmark-chunking.ps1), because the first sound is bound by the FIRST sentence
    /// synthesis. Smaller chunks would only add IPC overhead.
    /// </summary>
    public async Task<bool> StreamToSinkAsync(IEnumerable<string> sentences, string? langCode, IAudioSink sink)
    {
        if (!_ready) return false;

        var useVoice = ResolveVoice(langCode, out var langUnsupported);
        if (langUnsupported) return false;
        if (useVoice == null) return false;

        if (_synth == null)
        {
            if (_modelPath == null) return false;
            _synth = new KokoroWavSynthesizer(_modelPath);
        }

        foreach (var sentence in sentences)
        {
            var text = StripMarkdown(sentence);
            if (string.IsNullOrEmpty(text)) continue;
            var pcm = _synth.Synthesize(text, useVoice);
            if (pcm != null && pcm.Length > 0)
                sink.Write(pcm);
            await Task.Yield();   // keep the loop responsive (not a serialization barrier)
        }
        return true;
    }

    /// <summary>Resolves the voice for a language (or the default voice), reporting whether the
    /// language is unsupported (null voice + langUnsupported → OS fallback).</summary>
    private KokoroVoice? ResolveVoice(string? langCode, out bool langUnsupported)
    {
        langUnsupported = false;
        KokoroVoice? voice = null;
        if (langCode != null)
        {
            voice = GetVoiceForLanguage(langCode);
            langUnsupported = voice == null;
        }
        return voice ?? _defaultVoice;
    }

    /// <summary>Sample rate of the Kokoro synthesizer output (used to tag rendered PCM).</summary>
    public int SampleRate => KokoroPlayback.waveFormat.SampleRate;

    /// <summary>
    /// Locates the Kokoro ONNX model: the agent's own directory (standalone runs) or the parent
    /// directory (the app base dir, where the build-time <c>kokoro.onnx</c> asset is deployed).
    /// </summary>
    public static string? FindModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "kokoro.onnx"),
            Path.Combine(baseDir, "..", "kokoro.onnx"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Finds the voices folder next to the agent or in its parent (shared app folder).</summary>
    public static string? FindVoicesDir()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "voices"),
            Path.Combine(Path.GetFullPath(Path.Combine(baseDir, "..")), "voices"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

    /// <summary>Maps a two-letter ISO language code to the best available Kokoro voice.</summary>
    public static KokoroVoice? GetVoiceForLanguage(string langCode)
    {
        var voiceName = langCode switch
        {
            "it" => "if_sara",
            "en" => "af_heart",
            "fr" => "ff_siwis",
            "es" => "ef_dora",
            "ja" => "jf_alpha",
            "zh" => "zf_xiaobei",
            "hi" => "hf_alpha",
            "pt" => "pf_dora",
            _ => null,
        };
        if (voiceName == null) return null;
        try { return KokoroVoiceManager.GetVoice(voiceName); }
        catch { return null; }
    }

    /// <summary>Removes markdown syntax from text so the TTS speaks only the actual content.</summary>
    public static string StripMarkdown(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!\[([^\]]*)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\[([^\]]*)\]\([^)]+\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"~~(.+?)~~", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^>\s?", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\-\*\+]\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\d+\.\s+", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[\-\*\s_]{3,}$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\|-+\|", "");
        // Newlines not preceded by punctuation become ", "; otherwise a single \n stays as a pause.
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![.?!])\n", ", ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
        text = FilterSpeakableChars(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string FilterSpeakableChars(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(text, i);
            if (cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber
                or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.MathSymbol)
            {
                result.Append(text[i]);
            }
            else if (cat == UnicodeCategory.Surrogate)
            {
                i++;
            }
        }
        return result.ToString();
    }

    /// <summary>Releases the underlying engines.</summary>
    public void Dispose()
    {
        _playback?.Dispose();
        _synth?.Dispose();
    }
}
