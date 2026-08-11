# AIOffice.VoiceAgent

Cross-platform voice agent executable for [AIOffice](https://github.com/Graphene-Lab/AIOffice): speech-to-text via [whisper.net](https://github.com/sandrohanea/whisper.net) and text-to-speech via [KokoroSharp](https://github.com/Lyrcaxis/KokoroSharp).

- **Recognition**: whisper.net (CPU, ggml models). Primary engine on Linux/macOS, automatic fallback on Windows when the WinRT agent (`AIOffice.VoiceAgent.Win`) fails.
- **Microphone**: NAudio on Windows, `arecord` subprocess on Linux (alsa-utils, auto-installed via apt-get when missing).
- **TTS**: Kokoro neural voices (managed phonemization, no native espeak). Reuses the `kokoro.onnx` model asset shipped by the app when present, otherwise downloads it once.
- **Dependencies**: verified and installed automatically at startup — whisper ggml model download (Hugging Face), `arecord`/`libstdc++6` via apt on Linux, VC++ Redistributable on Windows. The user does nothing.

## Protocol

JSON Lines over stdin/stdout:

```
stdin:  {"cmd":"start"} | {"cmd":"speak","text":"...","lang":"<iso2>"} | {"cmd":"stop"}
stdout: {"type":"ready"} | {"type":"transcript","text"} | {"type":"status","text"} | {"type":"done"} | {"type":"error","text"}
```

## Usage

```
AIOffice.VoiceAgent [--check] [--debug] [--tts-method fast|full]
```

- `--check` — verify/install all dependencies, load the whisper model and exit (used by CI and to pre-warm).
