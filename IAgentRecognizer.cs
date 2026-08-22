namespace AIOffice.VoiceAgent;

/// <summary>
/// Abstraction over the speech recognizer used by <see cref="VoiceAgentBase"/>. The base main
/// loop (start/speak/stop) drives ANY recognizer through this contract — whisper on every
/// platform (cross), WinRT on Windows (AIOffice.VoiceAgent.Win) — so the two agents share the
/// whole protocol layer (architectural rule: no redundant logic between the agents).
/// </summary>
public interface IAgentRecognizer : IDisposable
{
    /// <summary>Raised when a speech utterance has been transcribed (non-empty text).</summary>
    event Action<string>? Transcript;

    /// <summary>Raised on a recognizer error.</summary>
    event Action<string>? Error;

    /// <summary>Raised when the recognizer's VAD changes state: "speech" (the caller started
    /// talking — the utterance is open) or "end" (the utterance closed and transcription began).
    /// Lets the SIP media bridge arm the processing indicator at the RIGHT moment (speech end =
    /// processing start) instead of waiting for the transcript, which can arrive seconds later
    /// (whisper inference) or never (noise kept the utterance open). Recognizers without an
    /// explicit VAD (WinRT) simply never raise it.</summary>
    event Action<string>? VadState;

    /// <summary>True when the recognizer consumes externally-fed PCM ({"cmd":"audio"}) instead of
    /// capturing from the microphone. Set by the base before <see cref="StartAsync"/>.</summary>
    bool ExternalInput { get; set; }

    /// <summary>Starts recognition (idempotent). For whisper it launches the capture chain; for
    /// WinRT it sets up the SpeechRecognizer on the STA thread and starts the recognition loop.</summary>
    Task StartAsync(string? lang);

    /// <summary>Stops recognition. Does not dispose the recognizer (a later StartAsync resumes).</summary>
    Task StopAsync();

    /// <summary>Feeds an external 16 kHz mono PCM16 chunk (used by the SIP media bridge in pipe
    /// mode). No-op for recognizers that capture from the microphone.</summary>
    void FeedExternalPcm(byte[] pcm);
}
