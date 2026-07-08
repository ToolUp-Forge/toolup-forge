# ToolUp.VoiceProviders.Whisper

An `ITranscriptionProvider` (from `ToolUp.Voice.Core`) over OpenAI's Whisper
`audio/transcriptions` endpoint. Batch speech-to-text: an audio clip in, a `Transcript` out.

- **BYOK** — reads `openai-api-key` from `ISecretStore` in the `_platform` scope on every
  call (the same key the OpenAI embedding provider uses); a rotated key flows through with
  no reconstruction, and the key is never read from an env var directly.
- **GP 1** — `HttpClient` + `System.Text.Json` only; no `OpenAI` NuGet dependency reaches
  your dependency graph.
- **Batch only** — `SupportsStreaming = false`. For low-latency streaming, use the client
  companion's local Web Speech mode.

## Compose

```fsharp
open ToolUp.Voice

let provider = WhisperTranscriptionProvider.create secretStore
// register the preflight validator + readiness probe alongside it:
//   |> ServerApp.withConfigValidator (WhisperTranscriptionProvider.createValidator secretStore)
//   |> ServerApp.withHealthCheck (WhisperTranscriptionProviderHealth.create secretStore)

let! result = provider.Transcribe (TranscriptionRequest.create "audio/webm" audioBytes)
match result with
| Ok transcript -> printfn "%s" (Transcript.plainText transcript)
| Error e -> eprintfn "%s" (TranscriptionError.describe e)
```

## Accepted audio

`audio/webm` (the `MediaRecorder` default), `audio/ogg`, `audio/wav`, `audio/mpeg` /
`audio/mp3`, `audio/mp4`, `audio/m4a`, `audio/flac`. Codec parameters
(`audio/webm;codecs=opus`) are accepted — only the bare MIME type is matched. Any other
content type returns `TranscriptionError.UnsupportedAudio`.

## Notes

- The request is sent as `response_format=verbose_json` so per-segment start/end timings are
  returned. Per-segment **confidence is `None`** — Whisper reports `avg_logprob` (a raw
  log-probability), not a calibrated `[0,1]` score, so surfacing it as confidence would
  mislead.
- Failure mapping: 401/403/400 → `PermanentClient`; 429 / 5xx / timeout / network →
  `Transient` (retry-worthy via `TranscriptionError.isRetryable`).

Licensed Apache-2.0.
