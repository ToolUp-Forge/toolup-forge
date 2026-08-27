# ToolUp.Voice

Voice-to-text for ToolUp applications: a portable transcription abstraction, two
server-side provider implementations behind it, and a client-side mic affordance
attachable to any text input.

The family is four packages, each opt-in and composed independently:

| Package | Tier | What it is |
|---|---|---|
| `ToolUp.Voice.Core` | shared | `ITranscriptionProvider` + the `Transcript` model + `TranscriptionRequest` / `TranscriptionError`. FSharp.Core only; Fable-safe. |
| `ToolUp.VoiceProviders.AzureSpeech` | server | `ITranscriptionProvider` over the Azure AI Speech REST short-audio endpoint. BYOK via `ISecretStore`. |
| `ToolUp.VoiceProviders.Whisper` | server | `ITranscriptionProvider` over OpenAI's `audio/transcriptions` endpoint. BYOK via `ISecretStore`. |
| `ToolUp.Voice.Client` | client | A `VoiceInput` mic-toggle affordance with **local** (browser Web Speech API) and **remote** (`MediaRecorder` → server) capture modes. |

## The abstraction

```fsharp skip=fragment
type ITranscriptionProvider =
    abstract ProviderId: string
    abstract SupportsStreaming: bool
    abstract Transcribe: TranscriptionRequest -> Async<Result<Transcript, TranscriptionError>>
    abstract OpenSession:
        (StreamingHypothesis -> unit) * string option ->
            Async<Result<ITranscriptionSession, TranscriptionError>>
```

Failures are data (`TranscriptionError`), every boundary is `Async`, and the provider is
stateless between `Transcribe` calls — the six portability rules (GP 12) are audited in
[`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md).

`Transcript.plainText` is the "just give me the words" projection for dropping recognised
text straight into an input:

```fsharp skip=fragment
let words = Transcript.plainText transcript
```

## Server: composing a provider (BYOK)

Both server providers read their API key from `ISecretStore` in the `_platform` scope —
exactly the pattern the Claude / OpenAI / Gemini AI providers use — so a rotated key flows
through without reconstruction and no credential is ever read from an env var directly.

```fsharp skip=fragment
// Azure AI Speech — key at _platform / "azure-speech-key" (+ region)
let provider = AzureSpeechTranscriptionProvider.create secretStore

// OpenAI Whisper — key at _platform / "openai-api-key"
let provider = WhisperTranscriptionProvider.create secretStore

let! result = provider.Transcribe (TranscriptionRequest.create "audio/webm" audioBytes)
```

## Client: the mic affordance

`VoiceInput.micButton` attaches a mic toggle to any text input. Two capture modes:

- **local** — the browser Web Speech API (`SpeechRecognition`) when available: zero server
  round-trip, no key, interim results stream straight into the input.
- **remote** — `MediaRecorder` captures a clip, uploads it to a server endpoint backed by
  the composed `ITranscriptionProvider`, and commits the returned transcript.

Mode resolves to **local if available**, unless the config forces remote. See
[`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) for the decision table.

An opt-in mic can be wired into the AI chat prompt box with one line (default off, zero
visual change when unset) via the `ToolUp.AI.Client` prompt-accessory registration seam:

```fsharp skip=fragment
ToolUp.Voice.Client.VoiceInput.registerPromptMic VoiceCaptureMode.Auto
```

## Zero cost when unused (GP 13)

Nothing composes by default. A deployment that references none of these packages has no
voice surface, no server endpoint, and no change to the AI chat prompt box.

## Out of scope

Text-to-speech, speaker diarisation, wake-word detection, and stored-media batch
transcription pipelines. This family provides live capture into text inputs and the
`ITranscriptionProvider` seam; stored-media caption/transcript generation is a separate
media-side concern.

Licensed Apache-2.0.
