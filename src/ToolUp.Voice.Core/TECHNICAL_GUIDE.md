# ToolUp.Voice — Technical Guide

Internals, design decisions, and the deferred set for the Voice companion family. Read
[`README.md`](README.md) first for the overview.

## Six-rule portability audit (GP-12 / Phase 9c)

`ITranscriptionProvider` (and its streaming sub-surface `ITranscriptionSession`) are audited
against the six portability rules described in the workspace
[`CLAUDE.md`](../../CLAUDE.md) ("Six portability rules for distributed implementations"). A
distributed implementation (a queue-backed batch transcriber, an Orleans grain fronting a
cloud speech service) must bind the same contract unchanged.

| Rule | `ITranscriptionProvider` | `ITranscriptionSession` |
|---|---|---|
| **1. Identity by value** | ✓ `TranscriptionRequest` (audio `byte[]` + `ContentType` + `LanguageHint`) and `Transcript` / `TranscriptSegment` are records over primitives; `ProviderId` is a `string`. No live handles cross the boundary. | ✓ The session is a *local* resource; nothing that crosses the interface is a live handle — audio goes in as `byte[]`, hypotheses/transcripts come back as records. |
| **2. Async at every boundary** | ✓ `Transcribe` / `OpenSession` return `Async<Result<_,_>>`. The `onHypothesis` callback is synchronous — the **one documented exemption**, mirroring `IAIProvider.SendMessage`'s `onStream` callback (the method is `Async`, per-hypothesis delivery stays synchronous by design). | ✓ `PushAudio` / `Complete` return `Async<Result<_,_>>`. `Dispose` is the standard `IDisposable` teardown, not a result-bearing call. |
| **3. Retry / supervision as data** | ✓ Failure flows through the `TranscriptionError` DU (`NotConfigured` / `PermanentClient` / `Transient` / `MalformedResponse` / `UnsupportedAudio` / `StreamingUnsupported`). No callback parameters carry framework semantics; `TranscriptionError.isRetryable` lets a caller drive its own retry. | ✓ Same `TranscriptionError` DU on every method. |
| **4. Stateless between calls** | ✓ Each `Transcribe` derives its result from its parameters plus the credential read per-call from `ISecretStore` — a grain that deactivates between two calls behaves identically. No in-memory state assumed across `Transcribe` calls. | The session is **explicitly stateful for its lifetime** (it owns a live socket/recogniser) — this is a scoped, single-owner resource, not cross-call state, and is disposed by the caller. Documented, bounded exception, the same shape as any streaming handle. |
| **5. No cross-shard ordering** | ✓ Each request is independent; no ordering is promised across `Transcribe` calls. | ✓ Ordering holds only *within* one session's hypothesis stream (its natural shard); nothing is promised across sessions. |
| **6. Precision at lower bound** | N/A — no scheduling/timing primitives in the interface. `TranscriptSegment` timestamps are *reported* offsets, not a scheduling contract. | N/A. |

**Companion-internal types reviewed for portability:**
- `onHypothesis: StreamingHypothesis -> unit` — a synchronous callback, not portable across a
  process boundary on its own. It is a client-local UI sink (interim text into an input), never
  serialised; the audio-in / transcript-out data path is the portable surface, the callback is
  a same-process convenience. Same discipline as `IAIProvider`'s `onStream`.

A future distributed companion binds an `ITranscriptionProviderContract` test pack against its
own factory, mirroring the `IBookingSchedulerContract` / `IShareTokenStoreContract` precedent.

## Provider matrix

| | `AzureSpeech` | `Whisper` |
|---|---|---|
| Endpoint | Azure AI Speech REST (short audio) | OpenAI `audio/transcriptions` |
| Key (`_platform` scope) | `azure-speech-key` (+ `azure-speech-region`) | `openai-api-key` |
| Vendor SDK | none — `HttpClient` only (GP 1) | none — `HttpClient` only (GP 1) |
| `SupportsStreaming` | `false` (REST short-audio path; streaming is deferred) | `false` (batch endpoint only) |
| Accepted audio | `audio/wav`, `audio/ogg`, `audio/webm`, … (per Azure) | `audio/*` the Whisper endpoint accepts (`webm`, `mp3`, `wav`, `m4a`, …) |
| Failure mapping | 401/403 → `PermanentClient`; 429/5xx/timeout → `Transient`; 400 → `PermanentClient` | same taxonomy |

Both providers isolate the vendor wire format behind the abstraction (GP 1): no
`Microsoft.CognitiveServices.Speech` / `OpenAI` NuGet dependency reaches `ToolUp.Platform.*`
or a consumer's dependency graph.

## Local vs remote capture (client)

`VoiceInput` resolves its capture mode per `VoiceCaptureMode`:

| Mode | Resolves to | When |
|---|---|---|
| `Auto` (default) | **local** if `window.SpeechRecognition` / `webkitSpeechRecognition` exists, else **remote** | The zero-friction default — no key needed where the browser supports it, graceful fallback otherwise. |
| `ForceLocal` | local (mic disabled if unsupported) | Privacy-sensitive deployments that must never ship audio to a server. |
| `ForceRemote` | remote | Consistent cross-browser accuracy via a server provider, or when the local API is unreliable. |

- **local** — the Web Speech API streams interim results; they render into the input via
  `React.useState` (transient display state, per the MVU discipline) and the final result
  commits with a single dispatch. No server round-trip, no key.
- **remote** — `MediaRecorder` captures a clip; on stop it is POSTed to the server endpoint,
  which runs the composed `ITranscriptionProvider` and returns a `Transcript`; the
  `plainText` projection commits into the input.

## Opt-in wiring into the AI chat prompt box

`ToolUp.AI.Client` exposes a `PromptAccessoryBridge` — the same global-registration idiom as
the shipped `FastPathBridge`. `ConversationPanel` renders a registered accessory in the input
row; **nothing registered → nothing renders**, so the AI chat prompt box is byte-for-byte
unchanged for a deployment that does not opt in (GP 13). `ToolUp.Voice.Client` registers its
mic with one call:

```fsharp
ToolUp.Voice.Client.VoiceInput.registerPromptMic VoiceCaptureMode.Auto
```

`ToolUp.AI.Client` takes **no** dependency on `ToolUp.Voice.Client` — the bridge is a
`(PromptAccessoryContext -> ReactElement)` slot, so the coupling is one-directional and the AI
tier stays free of voice imports.

## Deferred set

- **Streaming providers.** Both shipped providers are batch-only (`SupportsStreaming = false`).
  The `ITranscriptionSession` surface + Azure's streaming recognition path are specified and
  reserved but not implemented in this phase; local streaming is available client-side via the
  Web Speech API regardless.
- **Retry loop inside the providers.** The providers classify and surface `Transient` errors;
  the caller drives retry via `TranscriptionError.isRetryable`. A built-in backoff loop (as in
  the OpenAI embedding provider) is a follow-on.
- **Out of scope entirely** (not deferred — a different concern): text-to-speech, diarisation,
  wake words, and stored-media batch transcription pipelines.
