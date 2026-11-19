# AIOffice.VoiceAgent — notes for coding agents

> Read this file first. This is an **executable**, not a library: the hosts start it as a subprocess
> and talk to it over JSON Lines. Nothing references it as a DLL.

## What this is

The cross-platform voice agent: whisper.net recognition + Kokoro TTS, and the **shared base class
both agents inherit**. Primary recognition on Linux/macOS, fallback on Windows.

## Who consumes it (always as a subprocess)

- **AIOffice** — target `BuildWhisperVoiceAgentPlugin` copies the output into
  `$(OutputPath)VoiceAgentWhisper/` (and `kokoro.onnx` to the app root); the Voice panel starts it.
- **AgentBridge** — target `CopySttAgentOutput` copies it into `voiceagent-stt/`; the SIP bridge
  runs it with `--pipe-audio` as a **persistent** process (JSON Lines over stdin/stdout), so the
  whisper model stays loaded.

## Read before you edit

| If you touch… | Read first |
|---|---|
| the engine/conversation split | AIOrchestrator `docs-dev/ARCHITECTURE.md` — "Voice engines live in the subprocess" and "Media as I/O" |
| the protocol | `VoiceAgentBase.cs` and the same ARCHITECTURE section |
| the TTS streaming | `KokoroTts.cs` |

## Non-negotiables

1. **The engines live only here.** No VAD, no whisper, no Kokoro, no SAPI inside a media
   implementation: a new audio medium (Telegram, WhatsApp, Signal) implements `IAudioMedia` and
   reuses `VoiceConversation` — it never re-implements the conversation.
2. **One base class.** `VoiceAgentBase` holds the JSON-Lines loop, the logging and the unified speak
   logic; the cross agent and the Windows agent inherit it. Never copy that code into the Windows
   repo.
3. **The empty-final-chunk contract.** The end of a turn is `("", isLast = true)`: the sink is
   drained and closed, then recognition restarts. Without it, the resumed recognition hears the TTS.
4. **The JSON-Lines protocol is a contract with two consumers** (AIOffice's Voice panel and
   AgentBridge's SIP bridge). Add message types; never change the meaning of an existing one.
5. **One sink per conversation, reused across turns** (`KokoroTts.StreamToSinkAsync` renders
   sentence by sentence into that single sink). Setting up a sink per turn was measured as the
   cost removed by the reuse.
6. **Never assume the app root.** The host chooses the folder (`VoiceAgentWhisper/`,
   `voiceagent-stt/`) and places `kokoro.onnx` and the voices where it decides; keep the asset
   lookup relative to what the host provides.
7. Platform parts arrive through the hooks (`CreateRecognizer`, `CreateAudioSink`,
   `InitializeTtsAsync`/`ReadyPayload`, `TrySpeakOsFallback`) — that is what keeps the two agents
   one implementation.

## Where things are

- `VoiceAgent.cs` — the entry point and the argument handling (`--pipe-audio`, …)
- `VoiceAgentBase.cs` — the shared base: protocol loop, logging, speak logic, render path
- `KokoroTts.cs` — streaming TTS into one sink
- `WhisperRecognizer.cs` — whisper.net STT
- `ProcessAudioSink.cs` — the `aplay`/`paplay` sink (Linux/macOS)
- `IAudioSink.cs`, `IAgentRecognizer.cs` — the two platform contracts
- `Dependencies.cs`, `Log.cs` — startup checks and logging

## Shipping

Publishes on a `v*` tag (`publish.yml`); the hosts take the executable from the build output, not
from the package.
