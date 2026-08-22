# AIOffice.VoiceAgent

Cross-platform voice agent executable for [AIOffice](https://github.com/Graphene-Lab/AIOffice): speech-to-text via [whisper.net](https://github.com/sandrohanea/whisper.net) and text-to-speech via [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp).

- **Recognition**: whisper.net (CPU, ggml models). Primary engine on Linux/macOS, automatic fallback on Windows when the WinRT agent (`AIOffice.VoiceAgent.Win`) fails.
- **Microphone**: NAudio on Windows, `arecord` subprocess on Linux (alsa-utils, auto-installed via apt-get when missing).
- **TTS**: Kokoro neural voices (managed phonemization, no native espeak). Reuses the `kokoro.onnx` model asset shipped by the app when present, otherwise downloads it once.
- **Dependencies**: verified and installed automatically at startup — whisper ggml model download (Hugging Face), `arecord`/`libstdc++6` via apt on Linux, VC++ Redistributable on Windows. The user does nothing.

## Architecture (shared base)

This agent hosts **`VoiceAgentBase`** — the single implementation of the JSON-Lines protocol
loop, logging, the unified speak logic and the render path, inherited by BOTH voice agents via
C# inheritance (no duplicated code):

- **`VoiceAgentCross`** (this executable) — whisper STT; used standalone and by the SIP bridge
  (`--pipe-audio` + `render`).
- **`VoiceAgentWin`** (`AIOffice.VoiceAgent.Win` executable) — WinRT STT + SAPI fallback; driven
  by the AIOffice Voice panel.

Platform parts plug in through hooks: `CreateRecognizer()`, `TrySpeakOsFallback()`,
`CreateAudioSink()`, `InitializeTtsAsync()`/`ReadyPayload()`.

**TTS delivery**: Kokoro supports incremental synthesis — `KokoroTts.StreamToSinkAsync` renders
sentence by sentence into ONE continuous sink (`IAudioSink`: NAudio on Windows, aplay/paplay
stdin on Linux/macOS) → first sound as soon as the first sentence is ready, zero gaps. The
end-of-turn signal (`("", isLast=true)`) drains the tail and resumes recognition. Engines
without streaming (SAPI) keep the parked file-based path, used only as the OS fallback.

## Protocol

JSON Lines over stdin/stdout:

```
stdin:  {"cmd":"start","lang":"<iso2>"}            — begin recognition (mic capture)
        {"cmd":"audio","b64":"<pcm16-16k>"}        — external PCM chunk (--pipe-audio mode)
        {"cmd":"speak","text":"...","lang":"<iso2>","streaming":true|false}
        {"cmd":"speak","text":"...","lang":"<iso2>","render":true}   — render PCM, no playback
        {"cmd":"stop"}
stdout: {"type":"ready"} | {"type":"transcript","text"} | {"type":"audio","b64":"...","rate":24000}
        | {"type":"status","text"} | {"type":"done"} | {"type":"error","text"}
```

- `--pipe-audio`: no microphone; audio arrives via `{"cmd":"audio"}` (16 kHz mono PCM16, base64).
  Used by the AgentBridge SIP medium — VAD + whisper stay here (media = I/O only).
- `--no-system-libs`: skip the Windows SAPI TTS fallback — the chain runs exactly as on Linux
  (whisper + Kokoro only). Test switch.
- `render:true` on `speak`: synthesize to 24 kHz PCM (Kokoro primary → SAPI fallback on
  Windows) and push it as `{"type":"audio"}` chunks instead of playing it back.
- `streaming:true` on `speak`: render the chunk to the continuous audio sink (device playback,
  no gaps); the trailing `speak` with empty text closes the stream and resumes recognition.

## Usage

```
AIOffice.VoiceAgent [--check] [--debug] [--tts-method fast|full] [--pipe-audio] [--no-system-libs]
```

- `--check` — verify/install all dependencies, load the whisper model and exit (used by CI and to pre-warm).
