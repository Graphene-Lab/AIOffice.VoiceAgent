using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Base class shared by BOTH voice agents — the cross-platform one (whisper STT + Kokoro TTS,
/// used standalone and by the SIP media bridge) and the Windows one (WinRT STT + Kokoro/SAPI TTS,
/// driven by the AIOffice Voice panel). Holds everything the agents have in common so there is a
/// single copy of the protocol, the speak logic and the render path:
///
///   • JSON-Lines stdin/stdout protocol (start/audio/speak(streaming|render)/stop → ready/
///     transcript/audio/status/done/error)
///   • <see cref="SpeakAndPauseRecognitionAsync"/> — unified speak with the empty-final-chunk
///     fix (the end-of-turn signal must close the streaming session AND resume recognition)
///   • <see cref="RenderSpeakAsync"/> — playback-free PCM render for the SIP media bridge
///   • Log + WriteJson/WriteError
///
/// Subclasses provide only their platform-specific parts through the hooks below:
/// <see cref="CreateRecognizer"/> (whisper vs WinRT), <see cref="TrySpeakOsFallback"/> (SAPI on
/// Windows), <see cref="InitializeTtsAsync"/>/<see cref="ReadyPayload"/> and
/// <see cref="EnsureDependencies"/>. Architectural rule: media agents share logic, no redundancy.
/// </summary>
public abstract class VoiceAgentBase
{
    /// <summary>The active recognizer (whisper or WinRT), created by <see cref="CreateRecognizer"/>.</summary>
    protected IAgentRecognizer? Recognizer;

    /// <summary>Shared Kokoro TTS engine (model asset + voices + synthesis/playback logic).</summary>
    protected KokoroTts? Tts;

    /// <summary>Background TTS initialization task; true when the engine is ready.</summary>
    protected Task<bool>? TtsTask;

    /// <summary>True while a streaming speak session is active (recognition paused between chunks).</summary>
    protected bool StreamingSession;

    /// <summary>TTS processing mode: Fast (SpeakFast, default) or Full (Speak).</summary>
    protected string TtsMethod = "fast";

    /// <summary>Language code passed with the last "start" command (e.g. "it"). Null = auto-detect.</summary>
    protected string? RecognitionLang;

    /// <summary>External-input mode: audio arrives via {"cmd":"audio"} instead of the microphone
    /// (SIP media bridge). Only the whisper recognizer supports it.</summary>
    protected bool PipeAudio;

    /// <summary>When true, Windows system speech libraries (SAPI fallback for TTS) are skipped —
    /// the process behaves exactly as on Linux (whisper + Kokoro only). Test-only switch.</summary>
    protected bool NoSystemLibs;

    private CancellationTokenSource? _shutdownCts;

    // ─── Subclass hooks ───────────────────────────────────────────────────

    /// <summary>Creates the platform recognizer: whisper (cross) or WinRT (Windows).</summary>
    protected abstract IAgentRecognizer CreateRecognizer();

    /// <summary>Installs the speech dependencies (whisper model download on first run). The
    /// Windows agent uses WinRT recognition and needs none.</summary>
    protected virtual void EnsureDependencies() { }

    /// <summary>Initializes the TTS engine. The cross agent loads Kokoro in the background; the
    /// Windows agent waits for it (and sets up the SAPI fallback when Kokoro is unavailable).</summary>
    protected virtual async Task InitializeTtsAsync()
    {
        Tts = new KokoroTts(s => WriteJson(new { type = "status", text = s }), TtsMethod);
        TtsTask = Task.Run(Tts.InitializeAsync);
        await Task.CompletedTask;
    }

    /// <summary>OS-specific TTS fallback used when Kokoro cannot speak (unavailable engine or
    /// unsupported language). Windows returns SAPI; the base returns false (no fallback).</summary>
    protected virtual bool TrySpeakOsFallback(string text, string? langCode) => false;

    /// <summary>Creates the continuous audio sink used by STREAMING TTS (<see cref="KokoroTts.StreamToSinkAsync"/>)
    /// — the base returns null (no streaming device); the Windows agent returns the NAudio sink and
    /// Linux/macOS the aplay/paplay sink. The SIP media never uses a sink (it consumes rendered PCM
    /// through the protocol instead). The instance is created ONCE and reused across turns (a fresh
    /// WaveOutEvent per turn cost ~300 ms of time-to-first-audio); Start/EndAsync are per turn,
    /// Dispose happens when the conversation closes.</summary>
    protected virtual IAudioSink? CreateAudioSink() => null;

    private IAudioSink? _agentSink;

    /// <summary>The "ready" payload emitted after startup.</summary>
    protected virtual object ReadyPayload() =>
        new { type = "ready", tts = KokoroTts.FindModel() != null ? "kokoro" : "kokoro (downloading)" };

    /// <summary>Extra cleanup on stop/shutdown (Windows: SAPI + dispatcher). Also disposes the
    /// audio sink — the conversation is closing, the sink is not reused afterwards.</summary>
    protected virtual void StopAll()
    {
        Log.LogStep("StopAll");
        Recognizer?.StopAsync();
        _shutdownCts?.Cancel();
        try { _agentSink?.Dispose(); } catch (Exception ex) { Log.LogStep($"Audio sink dispose failed: {ex.Message}"); }
        _agentSink = null;
    }

    /// <summary>Set by a subclass when startup cannot continue (e.g. no TTS engine at all on
    /// Windows). The base emits nothing further and exits the loop.</summary>
    protected virtual bool StartupFailed => false;

    // ─── Main loop ────────────────────────────────────────────────────────

    /// <summary>Enters the stdin command loop (shared protocol). Returns when "stop" is received
    /// or stdin closes.</summary>
    public async Task RunAsync()
    {
        _shutdownCts = new CancellationTokenSource();

        EnsureDependencies();

        Recognizer = CreateRecognizer();
        Recognizer.Transcript += text => WriteJson(new { type = "transcript", text });
        Recognizer.Error += message => WriteError(message);

        await InitializeTtsAsync();

        if (StartupFailed)
            return;   // the subclass already emitted the error and nothing can run

        WriteJson(ReadyPayload());
        Log.LogStep("Ready sent");

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
                            RecognitionLang = root.TryGetProperty("lang", out var sl) ? sl.GetString() : null;
                            Log.LogStep($"Start command (lang={RecognitionLang ?? "auto"}, pipe={PipeAudio})");
                            // Pipe mode: the recognizer must consume externally-fed PCM
                            // ({"cmd":"audio"}), not the microphone — set BEFORE StartAsync, or
                            // FeedExternalPcm silently drops every chunk and STT stays silent.
                            Recognizer.ExternalInput = PipeAudio;
                            await StartRecognitionAsync(RecognitionLang);
                            break;

                        case "audio":
                            // External PCM chunk (16 kHz mono, base64) from the SIP media bridge.
                            if (root.TryGetProperty("b64", out var b64) && b64.ValueKind == JsonValueKind.String)
                            {
                                try
                                {
                                    var pcm = Convert.FromBase64String(b64.GetString()!);
                                    Recognizer?.FeedExternalPcm(pcm);
                                }
                                catch (Exception ex)
                                {
                                    Log.LogStep($"audio cmd decode failed: {ex.Message}");
                                }
                            }
                            break;

                        case "speak":
                            var text = root.TryGetProperty("text", out var t) ? t.GetString() : "";
                            var lang = root.TryGetProperty("lang", out var l) ? l.GetString() : null;
                            var streaming = root.TryGetProperty("streaming", out var s) && s.GetBoolean();
                            var render = root.TryGetProperty("render", out var r) && r.GetBoolean();
                            Log.LogStep($"Speak: lang={lang}, streaming={streaming}, render={render}, text_len={text?.Length ?? 0}");
                            if (render)
                                await RenderSpeakAsync(text, lang);
                            else
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
            Recognizer?.Dispose();
            Tts?.Dispose();
        }
    }

    private async Task StartRecognitionAsync(string? lang) => await Recognizer.StartAsync(lang);

    // ─── TTS ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops recognition, speaks the text, then restarts recognition. Three delivery modes:
    ///   • streaming chunk (text)  → rendered to the CONTINUOUS audio sink (NAudio/aplay) via
    ///     <see cref="KokoroTts.StreamToSinkAsync"/> — first sound as soon as the first sentence is
    ///     synthesized, no gaps; falls back to per-chunk playback when no sink/Kokoro is available.
    ///   • non-streaming (text)    → Kokoro playback (or the OS fallback) — the parked file-based
    ///     path, used by engines without streaming.
    ///   • EMPTY final chunk       → closes the streaming session AND restarts recognition —
    ///     otherwise the recognition paused by the first chunk never resumes and the chat dies
    ///     (the shared, fixed contract for every agent).
    ///
    /// LATENCY DATA (measured 2026-08-22, deterministic benchmarks):
    ///   • Client-side chunking gives NO gain: time-to-first-audio was 1650 ms both with a single
    ///     long speak and with 5 per-sentence chunks (e2e/benchmark-chunking.ps1) — the first
    ///     audio is bound by the FIRST-SENTENCE synthesis (~1.3 s, CPU cost of Kokoro), not by the
    ///     total reply length. Do not split the media further; it only adds IPC round-trips.
    ///   • The ~330 ms WaveOutEvent setup per turn is eliminated by SINK REUSE: turn 2 measured
    ///     790 ms faster than turn 1 (3400→2610 ms, e2e/verify-sink-reuse.ps1).
    ///   • The Kokoro FIRST-inference warm-up (real sentence at startup — a one-word "ok" does NOT
    ///     warm the phonemizer) reduced the first-message latency from 2320→1650 ms.
    /// </summary>
    protected virtual async Task SpeakAndPauseRecognitionAsync(string? text, string? langCode = null, bool streaming = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.LogStep("Speak skipped: empty text (closing streaming session)");
            if (StreamingSession)
            {
                StreamingSession = false;
                Log.LogStep("Final streaming chunk: ending streaming session");
                await EndStreamingSink();   // drains the TTS tail before recognition resumes
                await StartRecognitionAsync(RecognitionLang);
                Log.LogStep("Recognition restarted after empty final chunk");
            }
            return;
        }

        var effectiveLang = langCode ?? RecognitionLang;
        Log.LogStep($"Speak: text_len={text.Length}, lang={effectiveLang ?? "auto"}, streaming={streaming}");

        if (streaming)
        {
            if (!StreamingSession)
            {
                StreamingSession = true;
                Log.LogStep("First streaming chunk: pausing recognition");
                await Recognizer.StopAsync();
                _agentSink ??= CreateAudioSink();   // one instance for the whole conversation
                _agentSink?.Start();
            }
            if (!await TryStreamToSink(text, effectiveLang))
                await SpeakKokoroOrFallback(text, effectiveLang);
        }
        else if (StreamingSession)
        {
            StreamingSession = false;
            Log.LogStep("Final streaming chunk: ending streaming session");
            await EndStreamingSink();
        }
        else
        {
            Log.LogStep("Non-streaming speak: stopping recognition");
            await Recognizer.StopAsync();
            await SpeakKokoroOrFallback(text, effectiveLang);
            // Playback completed (SpeakAsync awaited the engine): resume recognition immediately.
            await StartRecognitionAsync(RecognitionLang);
            Log.LogStep("Recognition restarted after speak");
        }
    }

    /// <summary>Streams the text to the continuous sink (sentence by sentence). False when no
    /// sink, no Kokoro or an unsupported language — the caller falls back to playback.</summary>
    private async Task<bool> TryStreamToSink(string text, string? effectiveLang)
    {
        if (_agentSink == null || Tts == null) return false;
        try
        {
            if (TtsTask is { IsCompleted: true } && !TtsTask.Result) return false;
            return await Tts.StreamToSinkAsync(SplitSentences(text), effectiveLang, _agentSink);
        }
        catch (Exception ex)
        {
            Log.LogStep($"Streaming TTS failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>File-based/playback speak: Kokoro playback, then the OS fallback (SAPI on Windows).</summary>
    private async Task SpeakKokoroOrFallback(string text, string? effectiveLang)
    {
        if (TtsTask == null || Tts == null)
        {
            if (!TrySpeakOsFallback(text, effectiveLang))
                WriteError("TTS engine not available");
            return;
        }
        if (!await TtsTask || Tts == null)
        {
            if (!TrySpeakOsFallback(text, effectiveLang))
                WriteError("TTS engine not available");
            return;
        }
        if (!await Tts.SpeakAsync(text, effectiveLang))
        {
            if (!TrySpeakOsFallback(text, effectiveLang))
                WriteError($"No TTS voice for language '{effectiveLang}'");
        }
    }

    /// <summary>Ends a streaming turn: drains the TTS tail (so the resumed recognition never
    /// "hears" it) but KEEPS the sink instance for the next turn.</summary>
    private async Task EndStreamingSink()
    {
        try { if (_agentSink != null) await _agentSink.EndAsync(); }
        catch (Exception ex) { Log.LogStep($"Audio sink end failed: {ex.Message}"); }
    }

    // ─── TTS render (SIP media) ───────────────────────────────────────────

    /// <summary>
    /// Renders the text to PCM and pushes it to the host as {"type":"audio"} chunks (one per
    /// sentence). Playback-free: the SIP media bridge sends the PCM over RTP. TTS priority is
    /// Kokoro (neural, primary) → Windows SAPI (OS fallback, skipped with --no-system-libs) —
    /// the same chain for every media (architectural rule: media = I/O only, no engine redundancy).
    /// </summary>
    protected virtual async Task RenderSpeakAsync(string? text, string? langCode)
    {
        if (string.IsNullOrEmpty(text))
        {
            Log.LogStep("RenderSpeak skipped: empty text");
            return;
        }

        foreach (var sentence in SplitSentences(text))
        {
            var pcm = RenderKokoro(sentence, langCode);
            if (pcm == null)
            {
                pcm = RenderSap(sentence, langCode);   // OS fallback (Windows, unless --no-system-libs)
                if (pcm == null)
                {
                    WriteError($"No TTS voice for language '{langCode}'");
                    return;
                }
            }
            if (pcm.Value.Pcm.Length > 0)
                SendAudioChunk(pcm.Value.Pcm, pcm.Value.Rate);
        }
    }

    private (byte[] Pcm, int Rate)? RenderKokoro(string text, string? langCode)
    {
        if (TtsTask == null || Tts == null || !TtsTask.IsCompleted) return null;
        try
        {
            if (!TtsTask.Result || Tts == null) return null;
            var pcm = Tts.SynthesizePcm(text, langCode);
            if (pcm == null) return null;                                   // engine/voice unavailable → OS fallback
            if (pcm.Length == 0) return (Array.Empty<byte>(), Tts.SampleRate);
            return (pcm, Tts.SampleRate);
        }
        catch (Exception ex)
        {
            Log.LogStep($"Kokoro render failed: {ex.Message}");
            return null;
        }
    }

    private (byte[] Pcm, int Rate)? RenderSap(string text, string? langCode)
    {
#if WINDOWS
        if (NoSystemLibs) return null;   // test switch: behave exactly as on Linux
        try
        {
            using var synth = new System.Speech.Synthesis.SpeechSynthesizer();
            if (!string.IsNullOrWhiteSpace(langCode))
            {
                try
                {
                    synth.SelectVoiceByHints(System.Speech.Synthesis.VoiceGender.NotSet,
                        System.Speech.Synthesis.VoiceAge.NotSet, 0, CultureInfo.GetCultureInfo(langCode));
                }
                catch { }
            }
            using var ms = new MemoryStream();
            synth.SetOutputToWaveStream(ms);
            synth.Speak(text);
            ms.Position = 0;
            using var reader = new NAudio.Wave.WaveFileReader(ms);
            // Normalize to 24 kHz mono PCM16 so the SIP bridge always sends RTP at Rate24kHz
            // (same as Kokoro) — the media never branches on the engine.
            var pcm = ToPcm16AtRate(reader, 24000);
            Log.LogStep($"SAPI render: {text.Length} chars, {pcm.Length} bytes @ 24kHz");
            return (pcm, 24000);
        }
        catch (Exception ex)
        {
            Log.LogStep($"SAPI render failed: {ex.Message}");
            return null;
        }
#else
        return null;
#endif
    }

    /// <summary>Splits a reply into sentences (on ".!?"), keeping empty chunks out.</summary>
    protected static IEnumerable<string> SplitSentences(string text)
    {
        var sentences = text.Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return sentences.Length <= 1 ? new[] { text } : sentences;
    }

    /// <summary>Sends one rendered PCM chunk to the host (SIP media bridge).</summary>
    protected void SendAudioChunk(byte[] pcm, int rate)
    {
        WriteJson(new { type = "audio", b64 = Convert.ToBase64String(pcm), rate });
    }

    /// <summary>Reads a WAV stream as 16-bit mono PCM at the given sample rate (NAudio resampling).
    /// Mirrors the --transcribe normalization.</summary>
    protected static byte[] ToPcm16AtRate(NAudio.Wave.WaveFileReader reader, int targetRate)
    {
        NAudio.Wave.ISampleProvider samples = reader.WaveFormat.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat
            ? new NAudio.Wave.SampleProviders.WaveToSampleProvider(reader)
            : new NAudio.Wave.SampleProviders.Pcm16BitToSampleProvider(reader);
        if (reader.WaveFormat.Channels > 1)
            samples = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(samples);
        if (reader.WaveFormat.SampleRate != targetRate)
            samples = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(samples, targetRate);
        var pcm16 = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(samples);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int read;
        while ((read = pcm16.Read(buf, 0, buf.Length)) > 0)
            ms.Write(buf, 0, read);
        return ms.ToArray();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    protected static void WriteJson(object obj)
    {
        var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Console.WriteLine(json);
    }

    protected static void WriteError(string message)
    {
        WriteJson(new { type = "error", text = message });
    }
}
