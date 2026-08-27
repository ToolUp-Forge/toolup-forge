# ToolUp.VoiceProviders.AzureSpeech

An `ITranscriptionProvider` (from `ToolUp.Voice.Core`) over the Azure AI Speech REST
short-audio endpoint. Batch speech-to-text: an audio clip in, a `Transcript` out with
per-hypothesis confidence and word-offset timing.

- **BYOK** — reads `azure-speech-key` + `azure-speech-region` from `ISecretStore` in the
  `_platform` scope on every call; a rotated key flows through with no reconstruction, and
  neither credential is read from an env var directly.
- **GP 1** — `HttpClient` + `System.Text.Json` only; no `Microsoft.CognitiveServices.Speech`
  NuGet dependency reaches your dependency graph.
- **Batch only** — `SupportsStreaming = false` (the REST short-audio path; continuous
  streaming is a WebSocket protocol left to a follow-on). For low-latency streaming, use the
  client companion's local Web Speech mode.

## Compose

```fsharp skip=fragment
open ToolUp.Voice

let provider = AzureSpeechTranscriptionProvider.create secretStore
// register the preflight validator + readiness probe alongside it:
//   |> ServerApp.withConfigValidator (AzureSpeechTranscriptionProvider.createValidator secretStore)
//   |> ServerApp.withHealthCheck (AzureSpeechTranscriptionProviderHealth.create secretStore)

let request =
    TranscriptionRequest.create "audio/webm" audioBytes
    |> TranscriptionRequest.withLanguage "en-GB"

let! result = provider.Transcribe request
```

## Secrets

| `_platform` scope key | Value |
|---|---|
| `azure-speech-key` | the Speech resource subscription key |
| `azure-speech-region` | the resource region, e.g. `westeurope` |

## Accepted audio

`audio/webm` / `audio/ogg` (Opus — the `MediaRecorder` default) and `audio/wav` (PCM). The
provider declares the matching Azure `Content-Type` header per format. Any other content
type returns `TranscriptionError.UnsupportedAudio`. The short-audio endpoint is bounded to
roughly 60 s / 10 MB per clip.

## Notes

- The request uses `format=detailed`, so the top N-best hypothesis supplies per-segment
  `Confidence` and `Offset`/`Duration` timing (Azure ticks → `TimeSpan`).
- `NoMatch` / `InitialSilenceTimeout` / `BabbleTimeout` map to an **empty transcript** — a
  valid "nothing was said" result, not an error.
- Failure mapping: 401/403/400 → `PermanentClient`; 429 / 5xx / timeout / network →
  `Transient`.

Licensed Apache-2.0.
