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
/// Recognition uses whisper.net (Windows primary engine stays WinRT in AIOffice.VoiceAgent.Win;
/// this agent is the Linux/macOS engine and the Windows fallback). TTS uses KokoroSharp (neural,
/// cross-platform; model is downloaded automatically on first use).
/// </summary>
public class VoiceAgent
{
    private WhisperRecognizer? _recognizer;

    /// <summary>Kokoro neural TTS engine. Loaded/downloaded in background; null if it failed.</summary>
    private Task<KokoroTTS?>? _ttsTask;
    private KokoroTTS? _tts;

    /// <summary>Default Kokoro voice ("af_heart"), used when no language is specified.</summary>
    private KokoroVoice? _voice;

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

        WriteJson(new { type = "ready", tts = FindKokoroModel() != null ? "kokoro" : "kokoro (downloading)" });
        Log.LogStep($"Ready sent, TTS={FindKokoroModel() != null}");

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
        }
    }

    private void StartTtsBackground()
    {
        _ttsTask = Task.Run(async () =>
        {
            try
            {
                var voicesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voices");
                if (Directory.Exists(voicesDir))
                    KokoroVoiceManager.LoadVoicesFromPath(voicesDir);

                var modelPath = FindKokoroModel();
                KokoroTTS tts;
                if (modelPath != null)
                {
                    // The model is a build/publish asset of the app (AIOffice base dir); reuse it
                    // instead of downloading a duplicate copy.
                    Log.LogStep($"Kokoro model reused from: {modelPath}");
                    tts = KokoroTTS.LoadModel(modelPath);
                }
                else
                {
                    Log.LogStep("Kokoro model not found, downloading (one-time, ~320 MB)...");
                    WriteJson(new { type = "status", text = "Downloading Kokoro TTS model (~320 MB), first run only..." });
                    tts = await KokoroTTS.LoadModelAsync(KModel.float32);
                }

                _voice = KokoroVoiceManager.GetVoice("af_heart");
                _tts = tts;
                Log.LogStep("Kokoro TTS loaded successfully");
                WriteJson(new { type = "status", text = "TTS ready (Kokoro)" });
                return tts;
            }
            catch (Exception ex)
            {
                Log.LogStep($"Kokoro TTS failed: {ex.Message}");
                WriteJson(new { type = "status", text = $"TTS unavailable: {ex.Message}" });
                return null;
            }
        });
    }

    /// <summary>
    /// Locates the Kokoro ONNX model: the agent's own directory (standalone runs) or the parent
    /// directory (the app base dir, where the build-time <c>kokoro.onnx</c> asset is deployed).
    /// </summary>
    private static string? FindKokoroModel()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "kokoro.onnx"),
            Path.Combine(baseDir, "..", "kokoro.onnx"),
        };
        return candidates.FirstOrDefault(File.Exists);
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

        text = StripMarkdown(text);
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
        var tts = await _ttsTask;
        if (tts == null)
        {
            WriteError("TTS engine not available");
            return;
        }

        KokoroVoice? voice = null;
        bool langUnsupported = false;
        if (effectiveLang != null)
        {
            voice = GetVoiceForLanguage(effectiveLang);
            langUnsupported = voice == null;
        }

        if (voice != null)
        {
            Log.LogStep("Speaking with Kokoro (language-specific voice)");
            await SpeakWithKokoroAsync(tts, text, voice);
        }
        else if (!langUnsupported && _voice != null)
        {
            Log.LogStep("Speaking with Kokoro (default voice)");
            await SpeakWithKokoroAsync(tts, text, _voice);
        }
        else
        {
            WriteError($"No TTS voice for language '{effectiveLang}'");
            Log.LogStep($"No voice for language {effectiveLang}");
        }

        if (!streaming)
        {
            Log.LogStep("Non-streaming speak: restarting recognition after 500ms delay");
            await Task.Delay(500);
            _recognizer?.Start(_recognitionLang);
            Log.LogStep("Recognition restarted after speak");
        }
    }

    private async Task SpeakWithKokoroAsync(KokoroTTS tts, string text, KokoroVoice voice)
    {
        Log.LogStep($"Kokoro TTS starting (method={_ttsMethod})");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(SpeechCompletionPacket _) => tcs.TrySetResult();
        void OnCanceled(SpeechCancellationPacket _) => tcs.TrySetResult();
        tts.OnSpeechCompleted += OnCompleted;
        tts.OnSpeechCanceled += OnCanceled;
        try
        {
            if (_ttsMethod == "full")
                tts.Speak(text, voice);
            else
                tts.SpeakFast(text, voice);
            await tcs.Task;
            Log.LogStep("Kokoro TTS completed");
        }
        finally
        {
            tts.OnSpeechCompleted -= OnCompleted;
            tts.OnSpeechCanceled -= OnCanceled;
        }
    }

    private static KokoroVoice? GetVoiceForLanguage(string langCode)
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

    private static string StripMarkdown(string text)
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
}
