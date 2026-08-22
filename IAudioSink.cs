namespace AIOffice.VoiceAgent;

/// <summary>
/// Output sink for STREAMING TTS (<see cref="KokoroTts.StreamToSinkAsync"/>). The shared
/// streaming implementation synthesizes sentence by sentence and pushes 24 kHz PCM16 to this
/// sink back-to-back — ONE continuous audio stream, no file, no gaps. The media/platform
/// provides the sink implementation (I/O only, no TTS logic):
///   • Windows (Voice media)   — NAudio buffered output (WaveOutEvent)
///   • Linux/macOS (Voice)     — aplay/paplay fed over stdin
///   • SIP media bridge        — the {"type":"audio"} JSON channel (the subprocess is the host)
/// </summary>
public interface IAudioSink
{
    /// <summary>Opens/starts the output (idempotent). Called before the first Write.</summary>
    void Start();

    /// <summary>Writes a 24 kHz mono PCM16 chunk (one synthesized sentence).</summary>
    void Write(byte[] pcm24k);

    /// <summary>Closes the output and returns when any buffered audio has been played out (the
    /// end-of-turn signal arrives right after the last sentence was written). The caller awaits
    /// this before resuming speech recognition, so the recognition never "hears" the TTS tail.
    /// The sink instance is REUSED across turns (Start/EndAsync are per turn); Dispose is called
    /// once when the conversation ends (the agent's StopAll).</summary>
    Task EndAsync();

    /// <summary>Releases all platform resources (device/process). Called when the conversation
    /// closes — the sink is not used afterwards. No drain: any buffered audio is dropped.</summary>
    void Dispose();
}
