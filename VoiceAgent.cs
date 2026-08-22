using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KokoroSharp;
using KokoroSharp.Core;
using Whisper.net;

namespace AIOffice.VoiceAgent;

/// <summary>
/// Cross-platform voice agent executable — the platform-neutral subclass of
/// <see cref="VoiceAgentBase"/>: whisper STT + Kokoro TTS, no OS fallback. Used standalone
/// (microphone capture) and by the SIP media bridge ({"cmd":"audio"} pipe mode +
/// {"cmd":"speak","render":true} PCM output). Everything shared with the Windows agent lives in
/// the base — this class only wires whisper and the one-shot CLI commands.
///
/// Protocol (stdin):
///   {"cmd":"start","lang":…}              — begin recognition (or pipe mode with --pipe-audio)
///   {"cmd":"audio","b64":…}               — external 16 kHz PCM16 chunk (SIP media bridge)
///   {"cmd":"speak","text":…,"lang":…,"streaming":bool,"render":bool} — speak / render to PCM
///   {"cmd":"stop"}                        — stop recognition and exit
///
/// Protocol (stdout):
///   {"type":"ready"} | {"type":"transcript","text"} | {"type":"audio","b64":…,"rate":24000}
///   | {"type":"status","text"} | {"type":"done"} | {"type":"error","text"}
/// </summary>
public class VoiceAgentCross : VoiceAgentBase
{
    /// <summary>Entry point. Supports <c>--check</c> (dependency self-test), <c>--debug</c>,
    /// <c>--tts-method full|fast</c>, <c>--pipe-audio</c> (SIP media bridge), <c>--no-system-libs</c>
    /// (test switch) and the one-shot <c>--tts-file</c>/<c>--transcribe</c> commands.</summary>
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

        var agent = new VoiceAgentCross
        {
            TtsMethod = ttsMethod,
            PipeAudio = args.Contains("--pipe-audio"),
            NoSystemLibs = args.Contains("--no-system-libs"),
        };
        await agent.RunAsync();
        Log.LogStep("=== VoiceAgent exited ===");
    }

    /// <summary>whisper STT — the cross-platform recognizer (VAD + whisper.net).</summary>
    protected override IAgentRecognizer CreateRecognizer() => new WhisperRecognizer();

    /// <summary>Downloads the whisper model + native runtime on first run.</summary>
    protected override void EnsureDependencies() => Dependencies.EnsureAll();

    /// <summary>Streaming device sink on Linux/macOS (aplay/paplay over stdin); on Windows the
    /// cross agent is used only for SIP rendering (no device playback), so no sink.</summary>
    protected override IAudioSink? CreateAudioSink() =>
        OperatingSystem.IsWindows() ? null : new ProcessAudioSink();
}
