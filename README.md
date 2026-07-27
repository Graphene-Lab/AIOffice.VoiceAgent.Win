# AIOffice.VoiceAgent.Win

Windows-only executable providing **offline speech recognition** (WinRT OneCore dictation) and **neural text-to-speech** (KokoroSharp via ONNX runtime, with Windows SAPI fallback). Communicates with the host process via JSON Lines over stdin/stdout.

## Protocol

### stdin (host → agent)

| Command | Description |
|---------|-------------|
| `{"cmd":"start"}` or `{"cmd":"start","lang":"it"}` | Begin speech recognition for the given language (default: system language) |
| `{"cmd":"stop"}` | Stop recognition and exit |
| `{"cmd":"speak","text":"...","lang":"..."}` | Speak text, pause recognition, resume when done |

The optional `streaming` flag keeps recognition paused between consecutive calls:
- `{"cmd":"speak","text":"first","lang":"it","streaming":true}` — speak, stay paused
- `{"cmd":"speak","text":"last","lang":"it","streaming":false}` — speak, resume recognition

### stdout (agent → host)

| Type | Description |
|------|-------------|
| `{"type":"ready","tts":"kokoro"}` | Agent initialized (tts field shows engine) |
| `{"type":"transcript","text":"..."}` | User speech recognized |
| `{"type":"done"}` | Previous speak command finished |
| `{"type":"error","text":"..."}` | Error occurred |

## Language support

The `lang` field (two-letter ISO code) controls **both speech recognition and TTS voice**:

- **Recognition:** the WinRT `SpeechRecognizer` is initialized with the matching language tag (e.g. `it` → `it-IT`). When no language is provided, the system default is used.
- **TTS:** the corresponding Kokoro voice is selected for text-to-speech. When a `speak` command omits `lang`, it falls back to the language set by the previous `start` command, then to the system default.

**Language fallback chain:** speak `lang` → recognition `lang` (from `start`) → system default

Two-letter ISO code → WinRT recognition tag / Kokoro voice mapping:

| Code | Language | Recognition | TTS voice |
|------|----------|-------------|-----------|
| `it` | Italian | `it-IT` | `if_sara` (female) |
| `en` | English | `en-US` | `af_heart` (highest quality) |
| `fr` | French | `fr-FR` | `ff_siwis` |
| `es` | Spanish | `es-ES` | `ef_dora` |
| `de` | German | `de-DE` | SAPI fallback |
| `ja` | Japanese | `ja-JP` | `jf_alpha` |
| `zh` | Chinese | `zh-CN` | `zf_xiaobei` |
| `hi` | Hindi | — | `hf_alpha` |
| `pt` | Portuguese | `pt-BR` | `pf_dora` |
| `ru` | Russian | `ru-RU` | SAPI fallback |
| *other* | Unsupported | System default | Windows SAPI |

> **Note:** Recognition requires the corresponding Windows language pack to be installed (`Settings → Time & Language → Language & region → Add a language`). The `SupportedGrammarLanguages` log entry shows which languages are available on the current system.

## TTS engine selection

1. **Kokoro neural TTS** (~320MB ONNX model) — loaded on startup, auto-downloaded from GitHub Releases on first run
2. **Windows SAPI** — fallback when Kokoro model is unavailable (offline, no cache) or the language is unsupported

The `ready` message includes a `tts` field indicating which engine loaded.

## TTS method selection

| Flag | Method | Behaviour |
|------|--------|-----------|
| `--tts-method=fast` (default) | `SpeakFast` | Internal punctuation-based segmentation. First segment plays immediately while rest is inferred in background. Best for responsiveness. |
| `--tts-method=full` | `Speak` | No segmentation. Full text inferred before playback starts. Best for quality on very short phrases. |

## Debugging & logging

| Flag | Behaviour |
|------|-----------|
| `--debug` | Calls `Debugger.Launch()` to attach a Visual Studio JIT debugger |

In **Debug** builds, step-level logging is auto-enabled. Each run writes a log file to `{AppBase}/logs/{ProcessId}.txt` with the format:

```
[elapsed_seconds] [calling_method] message
[0,36] [MoveNext] DispatcherQueue STA thread created
[2,95] [MoveNext] Kokoro TTS loaded successfully
[2,96] [MoveNext] Compile result: Success
```

The log covers startup (encoding, privacy policy, microphone permission), recognizer setup (compilation, timeouts, language), every `RecognizeAsync` result, TTS events, and shutdown.

## Streaming TTS buffering (client-side)

The client (Voice.cs) accumulates LLM tokens and flushes only when sentence-ending punctuation (`.`, `!`, `?`) is found with text after it. No timeout flush — waiting for punctuation produces natural phrasing.

```
"Ciao! Oggi è una bella giornata. Non ti sembra? Cosa fai"
  → "Ciao!"                                          (primo flush)
  → "Oggi è una bella giornata. Non ti sembra?"       (secondo flush)
  → "Cosa fai"                                        (flush finale)
```

## Integration

Built automatically by AIOffice's `BuildVoiceAgentPlugin` MSBuild target and copied to the output directory along with required `voices/` and `espeak/` folders.

## Build

```bash
dotnet build -p:Configuration=Debug
```

The compiled executable targets `net10.0-windows10.0.19041.0` and requires the Windows 10 SDK (19041+).
