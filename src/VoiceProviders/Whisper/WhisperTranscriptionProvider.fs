module WhisperTranscriptionProvider

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open ToolUp.Voice
open ToolUp.Platform.Secrets

// ─── OpenAI Whisper transcription provider ────────────────────────
//
// `ITranscriptionProvider` over OpenAI's `POST /v1/audio/transcriptions`
// endpoint (the Whisper model family). Batch-only — the endpoint takes a
// whole audio clip as multipart/form-data and returns the transcript in
// one response; there is no streaming session surface here (local
// streaming is available client-side via the Web Speech API).
//
// GP 1 — no `OpenAI` NuGet dependency: the wire format is a small
// multipart POST + JSON response, so BCL `HttpClient` + `System.Text.Json`
// keep the vendor SDK out of the dependency graph.
//
// BYOK — the API key is read from `ISecretStore` in the `_platform` scope
// on every call (key `openai-api-key`, the same key the OpenAI embedding
// provider reads), so a rotated key flows through without reconstruction
// and no credential is ever read from an env var directly. The key
// fetcher is a thunk so the same core serves both the secret-store path
// (fetch per call) and a directly-supplied key.

[<Literal>]
let ProviderId = "whisper"

/// Default Whisper model. `whisper-1` is the generally-available
/// transcription model; deployments may override for a newer id.
[<Literal>]
let DefaultModel = "whisper-1"

// ─── Pure wire helpers (unit-tested without HTTP) ─────────────────

/// The request-shaping + response-parsing surface, factored pure so the
/// test pack can assert the form fields and JSON mapping without issuing
/// a real HTTP call.
module Wire =
    /// Content types the Whisper endpoint accepts. `MediaRecorder` on the
    /// browsers ToolUp targets emits `audio/webm`; file uploads add the
    /// common container formats. The check is a prefix/exact match on the
    /// bare MIME type (codec parameters after `;` are ignored).
    let acceptedContentTypes =
        set [
            "audio/webm"
            "audio/ogg"
            "audio/wav"
            "audio/x-wav"
            "audio/mpeg"
            "audio/mp3"
            "audio/mp4"
            "audio/m4a"
            "audio/x-m4a"
            "audio/flac"
        ]

    /// Bare MIME type — everything before the first `;` (drops the codec
    /// parameter `MediaRecorder` appends, e.g. `audio/webm;codecs=opus`).
    let bareContentType (contentType: string) : string =
        if isNull contentType then
            ""
        else
            let semi = contentType.IndexOf(';')

            (if semi >= 0 then
                 contentType.Substring(0, semi)
             else
                 contentType)
                .Trim()
                .ToLowerInvariant()

    /// Whether the endpoint accepts this content type.
    let isAccepted (contentType: string) : bool =
        acceptedContentTypes.Contains(bareContentType contentType)

    /// A plausible upload filename for the content type — Whisper's
    /// multipart endpoint requires a filename with a recognised extension
    /// to infer the container format, so a bare `blob` (which some
    /// `MediaRecorder` uploads default to) is rejected with a 400. Map the
    /// bare MIME type to the matching extension.
    let filenameFor (contentType: string) : string =
        match bareContentType contentType with
        | "audio/webm" -> "audio.webm"
        | "audio/ogg" -> "audio.ogg"
        | "audio/wav"
        | "audio/x-wav" -> "audio.wav"
        | "audio/mpeg"
        | "audio/mp3" -> "audio.mp3"
        | "audio/mp4" -> "audio.mp4"
        | "audio/m4a"
        | "audio/x-m4a" -> "audio.m4a"
        | "audio/flac" -> "audio.flac"
        | _ -> "audio.webm"

    /// The non-file form fields for a request — `model`, `response_format`
    /// (always `verbose_json` so we get per-segment timings), and the
    /// optional `language`. Kept as an ordered list so a test can assert
    /// the exact shaping.
    let formFields (model: string) (languageHint: string option) : (string * string) list = [
        "model", model
        "response_format", "verbose_json"
        match languageHint with
        | Some lang when not (String.IsNullOrWhiteSpace lang) -> "language", lang
        | _ -> ()
    ]

    /// Parse a `verbose_json` transcription response into a `Transcript`.
    /// Whisper reports `text`, `language`, and (in verbose mode) a
    /// `segments` array of `{ start, end, text }`. Per-segment confidence
    /// is deliberately `None` — Whisper reports `avg_logprob`, a raw
    /// log-probability, not a calibrated `[0,1]` confidence, so surfacing
    /// it as one would mislead. A response with no `segments` array (some
    /// models / formats) degrades to a single flat-text segment.
    let parseResponse (json: string) : Result<Transcript, TranscriptionError> =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let language =
                match root.TryGetProperty("language") with
                | true, l when l.ValueKind = JsonValueKind.String -> Some(l.GetString())
                | _ -> None

            let segments =
                match root.TryGetProperty("segments") with
                | true, segs when segs.ValueKind = JsonValueKind.Array -> [
                    for seg in segs.EnumerateArray() do
                        let text =
                            match seg.TryGetProperty("text") with
                            | true, t when t.ValueKind = JsonValueKind.String -> t.GetString()
                            | _ -> ""

                        let secs (name: string) =
                            match seg.TryGetProperty(name) with
                            | true, v when v.ValueKind = JsonValueKind.Number -> v.GetDouble()
                            | _ -> 0.0

                        {
                            Text = text
                            Start = TimeSpan.FromSeconds(secs "start")
                            End = TimeSpan.FromSeconds(secs "end")
                            Confidence = None
                        }
                  ]
                | _ ->
                    // No segment breakdown — fall back to the flat `text`.
                    match root.TryGetProperty("text") with
                    | true, t when t.ValueKind = JsonValueKind.String -> [ TranscriptSegment.ofText (t.GetString()) ]
                    | _ -> []

            Ok {
                Segments = segments
                Language = language
            }
        with ex ->
            Error(TranscriptionError.MalformedResponse ex.Message)

    /// Classify a non-success HTTP status into a `TranscriptionError`.
    /// Mirrors the AI / embedding-provider taxonomy: 429 or any 5xx is
    /// `Transient` (retry-worthy); every other status is `PermanentClient`.
    let classifyStatus (statusCode: int) (body: string) : TranscriptionError =
        if statusCode = 429 || statusCode >= 500 then
            TranscriptionError.Transient(sprintf "OpenAI transcription HTTP %d: %s" statusCode body)
        else
            TranscriptionError.PermanentClient(statusCode, body)

// ─── Provider implementation ──────────────────────────────────────

// Shared per-process HttpClient — a new client per request is the .NET
// socket-exhaustion antipattern (the ClaudeAIProvider header explains the
// TIME_WAIT / ephemeral-port failure mode). No per-request state lives on
// the client: the per-call bearer header rides the HttpRequestMessage.
let private sharedClient =
    lazy
        (let c = new HttpClient()
         c.BaseAddress <- Uri("https://api.openai.com")
         c.Timeout <- TimeSpan.FromMinutes(2.0)
         c)

type private WhisperTranscriptionProviderImpl(apiKeyFetcher: unit -> Async<string option>, model: string) =
    let client = sharedClient.Value

    let transcribe (request: TranscriptionRequest) : Async<Result<Transcript, TranscriptionError>> = async {
        if not (Wire.isAccepted request.ContentType) then
            return
                Error(
                    TranscriptionError.UnsupportedAudio(
                        request.ContentType,
                        "Whisper accepts webm/ogg/wav/mp3/mp4/m4a/flac audio"
                    )
                )
        else
            let! apiKey = apiKeyFetcher ()

            match apiKey with
            | None ->
                return
                    Error(
                        TranscriptionError.NotConfigured
                            "openai-api-key is not configured in the _platform secret scope. Set it (env var TOOLUP__PLATFORM_OPENAI_API_KEY / your ISecretStore) to use the Whisper transcription provider."
                    )
            | Some key ->
                try
                    use form = new MultipartFormDataContent()

                    let fileContent = new ByteArrayContent(request.Audio)
                    fileContent.Headers.ContentType <- MediaTypeHeaderValue(Wire.bareContentType request.ContentType)
                    form.Add(fileContent, "file", Wire.filenameFor request.ContentType)

                    for name, value in Wire.formFields model request.LanguageHint do
                        form.Add(new StringContent(value), name)

                    use message = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions")
                    message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", key)
                    message.Content <- form

                    let! response = client.SendAsync(message) |> Async.AwaitTask
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        return Wire.parseResponse body
                    else
                        return Error(Wire.classifyStatus (int response.StatusCode) body)
                with
                | :? OperationCanceledException ->
                    return Error(TranscriptionError.Transient "Whisper transcription request timed out")
                | :? HttpRequestException as ex -> return Error(TranscriptionError.Transient ex.Message)
                | ex -> return Error(TranscriptionError.Transient ex.Message)
    }

    interface ITranscriptionProvider with
        member _.ProviderId = ProviderId
        member _.SupportsStreaming = false
        member _.Transcribe(request) = transcribe request

        member _.OpenSession(_, _) =
            ITranscriptionProvider.streamingUnsupported ProviderId

// ─── Factory functions ────────────────────────────────────────────

/// Create a Whisper transcription provider that reads `openai-api-key`
/// from the `_platform` secret scope on every call (BYOK; supports key
/// rotation without reconstruction).
let create (secretStore: ISecretStore) : ITranscriptionProvider =
    WhisperTranscriptionProviderImpl((fun () -> secretStore.GetSecret("_platform", "openai-api-key")), DefaultModel)

/// Create with an explicit model id (e.g. a newer Whisper family model),
/// still reading the key from the secret store per call.
let createWithModel (secretStore: ISecretStore) (model: string) : ITranscriptionProvider =
    WhisperTranscriptionProviderImpl((fun () -> secretStore.GetSecret("_platform", "openai-api-key")), model)

/// Create from a directly-supplied API key (the key is captured once).
let createWithApiKey (apiKey: string) : ITranscriptionProvider =
    WhisperTranscriptionProviderImpl((fun () -> async { return Some apiKey }), DefaultModel)

// ─── Preflight validator ──────────────────────────────────────────

/// Presence-only `IConfigValidator` — fails the deploy when the
/// `openai-api-key` secret is missing/empty, rather than letting the
/// first transcription call fail at runtime. Wire it alongside the
/// provider.
type WhisperConfigValidator(secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout =
        defaultArg timeout ToolUp.Platform.ConfigValidation.IConfigValidator.defaultTimeout

    interface ToolUp.Platform.ConfigValidation.IConfigValidator with
        member _.Name = "whisper-transcription-api-key"
        member _.Timeout = timeout

        member _.Validate() = async {
            let! keyOpt = secretStore.GetSecret("_platform", "openai-api-key")

            match keyOpt with
            | Some k when not (String.IsNullOrWhiteSpace k) -> return ToolUp.Platform.ConfigValidation.Ok
            | _ ->
                return
                    ToolUp.Platform.ConfigValidation.Error
                        "Whisper transcription provider is configured but the `openai-api-key` secret is missing/empty in the `_platform` scope. Every transcription call would fail at runtime. Set the secret (env var `TOOLUP__PLATFORM_OPENAI_API_KEY` / your ISecretStore) before deploying."
        }

/// Build the Whisper preflight validator (presence-only).
let createValidator (secretStore: ISecretStore) : ToolUp.Platform.ConfigValidation.IConfigValidator =
    WhisperConfigValidator(secretStore) :> ToolUp.Platform.ConfigValidation.IConfigValidator