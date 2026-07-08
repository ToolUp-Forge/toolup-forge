module AzureSpeechTranscriptionProvider

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open ToolUp.Voice
open ToolUp.Platform.Secrets

// ─── Azure AI Speech transcription provider ───────────────────────
//
// `ITranscriptionProvider` over the Azure AI Speech REST short-audio
// endpoint (`.../speech/recognition/conversation/cognitiveservices/v1`).
// Batch-only here — the REST short-audio path takes a whole clip and
// returns a `RecognitionStatus` + text in one response (audio ≤ ~60 s /
// ~10 MB); continuous streaming is a WebSocket protocol left to a
// follow-on (local streaming is available client-side via the Web Speech
// API regardless).
//
// GP 1 — no `Microsoft.CognitiveServices.Speech` NuGet dependency: the
// REST endpoint is a plain POST of the audio bytes + a subscription-key
// header, so BCL `HttpClient` + `System.Text.Json` keep the vendor SDK
// out of the dependency graph.
//
// BYOK — the subscription key + region are read from `ISecretStore` in
// the `_platform` scope on every call (`azure-speech-key` +
// `azure-speech-region`), so a rotated key flows through without
// reconstruction and no credential is read from an env var directly.

[<Literal>]
let ProviderId = "azure-speech"

/// Secret-store key for the Azure Speech subscription key.
[<Literal>]
let KeySecretName = "azure-speech-key"

/// Secret-store key for the Azure Speech region (e.g. `"westeurope"`).
[<Literal>]
let RegionSecretName = "azure-speech-region"

/// Default recognition language when the request carries no hint. Azure's
/// short-audio endpoint requires an explicit `language` query parameter.
[<Literal>]
let DefaultLanguage = "en-US"

// ─── Pure wire helpers (unit-tested without HTTP) ─────────────────

module Wire =
    /// Content types the Azure short-audio endpoint accepts, expressed as
    /// the `Content-Type` header value the POST must carry. `MediaRecorder`
    /// emits `audio/webm;codecs=opus` on the browsers ToolUp targets; WAV
    /// and OGG-Opus round out the common set.
    let acceptedContentTypes =
        set [ "audio/webm"; "audio/ogg"; "audio/wav"; "audio/x-wav" ]

    /// Bare MIME type — everything before the first `;`.
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

    let isAccepted (contentType: string) : bool =
        acceptedContentTypes.Contains(bareContentType contentType)

    /// The Azure `Content-Type` header for a captured clip. Azure keys the
    /// codec off this header; `MediaRecorder`'s Opus-in-WebM maps to the
    /// documented `audio/webm; codecs=opus` value, WAV declares its PCM
    /// sample rate. The bare `audio/wav` case declares the header Azure
    /// expects for 16 kHz mono PCM (the common `MediaRecorder`-to-WAV
    /// transcode target); non-conforming WAV still transcribes, this is
    /// only the declared header.
    let azureContentTypeHeader (contentType: string) : string =
        match bareContentType contentType with
        | "audio/webm"
        | "audio/ogg" -> "audio/webm; codecs=opus"
        | "audio/wav"
        | "audio/x-wav" -> "audio/wav; codecs=audio/pcm; samplerate=16000"
        | other -> other

    /// The recognition endpoint path + query for a region + language.
    /// `format=detailed` returns the N-best list with per-hypothesis
    /// confidence, which we map onto the segment's `Confidence`.
    let recognitionUri (region: string) (language: string) : string =
        sprintf
            "https://%s.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language=%s&format=detailed"
            region
            (Uri.EscapeDataString language)

    /// Resolve the effective recognition language from the request hint.
    let effectiveLanguage (languageHint: string option) : string =
        match languageHint with
        | Some lang when not (String.IsNullOrWhiteSpace lang) -> lang
        | _ -> DefaultLanguage

    /// Parse a `format=detailed` recognition response into a `Transcript`.
    /// Azure returns `RecognitionStatus` (`Success` / `NoMatch` /
    /// `InitialSilenceTimeout` / …), a `DisplayText`, and an `NBest` array
    /// of `{ Confidence, Display, Lexical, Offset, Duration }` (offsets in
    /// 100-ns ticks). `NoMatch` / silence map to an empty transcript (a
    /// valid "nothing was said" result); a missing status is malformed.
    let parseResponse (json: string) : Result<Transcript, TranscriptionError> =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let status =
                match root.TryGetProperty("RecognitionStatus") with
                | true, s when s.ValueKind = JsonValueKind.String -> s.GetString()
                | _ -> ""

            match status with
            | "" -> Error(TranscriptionError.MalformedResponse "response carried no RecognitionStatus")
            | "Success" ->
                // Prefer the top N-best hypothesis (it carries confidence +
                // offset/duration); fall back to the flat DisplayText.
                let best =
                    match root.TryGetProperty("NBest") with
                    | true, nbest when nbest.ValueKind = JsonValueKind.Array && nbest.GetArrayLength() > 0 ->
                        Some(nbest.[0])
                    | _ -> None

                match best with
                | Some hyp ->
                    let text =
                        match hyp.TryGetProperty("Display") with
                        | true, d when d.ValueKind = JsonValueKind.String -> d.GetString()
                        | _ ->
                            match hyp.TryGetProperty("Lexical") with
                            | true, l when l.ValueKind = JsonValueKind.String -> l.GetString()
                            | _ -> ""

                    let confidence =
                        match hyp.TryGetProperty("Confidence") with
                        | true, c when c.ValueKind = JsonValueKind.Number -> Some(c.GetDouble())
                        | _ -> None

                    // Offsets/durations are in 100-ns ticks.
                    let ticks (name: string) =
                        match hyp.TryGetProperty(name) with
                        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt64()
                        | _ -> 0L

                    let start = TimeSpan.FromTicks(ticks "Offset")
                    let duration = TimeSpan.FromTicks(ticks "Duration")

                    Ok {
                        Segments = [
                            {
                                Text = text
                                Start = start
                                End = start + duration
                                Confidence = confidence
                            }
                        ]
                        Language = None
                    }
                | None ->
                    match root.TryGetProperty("DisplayText") with
                    | true, d when d.ValueKind = JsonValueKind.String -> Ok(Transcript.ofText (d.GetString()))
                    | _ -> Ok Transcript.empty
            | "NoMatch"
            | "InitialSilenceTimeout"
            | "BabbleTimeout" ->
                // Nothing recognisable in the audio — a valid empty result,
                // not an error.
                Ok Transcript.empty
            | other -> Error(TranscriptionError.MalformedResponse(sprintf "unexpected RecognitionStatus '%s'" other))
        with ex ->
            Error(TranscriptionError.MalformedResponse ex.Message)

    /// Classify a non-success HTTP status. 429 / 5xx → `Transient`; every
    /// other status → `PermanentClient`.
    let classifyStatus (statusCode: int) (body: string) : TranscriptionError =
        if statusCode = 429 || statusCode >= 500 then
            TranscriptionError.Transient(sprintf "Azure Speech HTTP %d: %s" statusCode body)
        else
            TranscriptionError.PermanentClient(statusCode, body)

// ─── Provider implementation ──────────────────────────────────────

let private sharedClient =
    lazy
        (let c = new HttpClient()
         c.Timeout <- TimeSpan.FromMinutes(2.0)
         c)

type private AzureSpeechTranscriptionProviderImpl
    (keyFetcher: unit -> Async<string option>, regionFetcher: unit -> Async<string option>) =
    let client = sharedClient.Value

    let transcribe (request: TranscriptionRequest) : Async<Result<Transcript, TranscriptionError>> = async {
        if not (Wire.isAccepted request.ContentType) then
            return
                Error(
                    TranscriptionError.UnsupportedAudio(
                        request.ContentType,
                        "Azure short-audio accepts webm/ogg (Opus) or wav (PCM) audio"
                    )
                )
        else
            let! key = keyFetcher ()
            let! region = regionFetcher ()

            match key, region with
            | None, _ ->
                return
                    Error(
                        TranscriptionError.NotConfigured
                            "azure-speech-key is not configured in the _platform secret scope. Set it (env var TOOLUP__PLATFORM_AZURE_SPEECH_KEY / your ISecretStore) to use the Azure Speech transcription provider."
                    )
            | _, None ->
                return
                    Error(
                        TranscriptionError.NotConfigured
                            "azure-speech-region is not configured in the _platform secret scope (e.g. 'westeurope'). Set it before using the Azure Speech transcription provider."
                    )
            | Some subscriptionKey, Some regionValue ->
                try
                    let language = Wire.effectiveLanguage request.LanguageHint
                    let uri = Wire.recognitionUri regionValue language

                    use content = new ByteArrayContent(request.Audio)

                    content.Headers.ContentType <-
                        MediaTypeHeaderValue.Parse(Wire.azureContentTypeHeader request.ContentType)

                    use message = new HttpRequestMessage(HttpMethod.Post, uri)
                    message.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey)
                    message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue("application/json"))
                    message.Content <- content

                    let! response = client.SendAsync(message) |> Async.AwaitTask
                    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

                    if response.IsSuccessStatusCode then
                        return Wire.parseResponse body
                    else
                        return Error(Wire.classifyStatus (int response.StatusCode) body)
                with
                | :? OperationCanceledException ->
                    return Error(TranscriptionError.Transient "Azure Speech transcription request timed out")
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

/// Create an Azure Speech transcription provider that reads
/// `azure-speech-key` + `azure-speech-region` from the `_platform` secret
/// scope on every call (BYOK; supports key rotation without
/// reconstruction).
let create (secretStore: ISecretStore) : ITranscriptionProvider =
    AzureSpeechTranscriptionProviderImpl(
        (fun () -> secretStore.GetSecret("_platform", KeySecretName)),
        (fun () -> secretStore.GetSecret("_platform", RegionSecretName))
    )

/// Create from a directly-supplied subscription key + region (both
/// captured once).
let createWithKeyAndRegion (subscriptionKey: string) (region: string) : ITranscriptionProvider =
    AzureSpeechTranscriptionProviderImpl(
        (fun () -> async { return Some subscriptionKey }),
        (fun () -> async { return Some region })
    )

// ─── Preflight validator ──────────────────────────────────────────

/// Presence-only `IConfigValidator` — fails the deploy when either the
/// `azure-speech-key` or `azure-speech-region` secret is missing/empty,
/// rather than letting the first transcription call fail at runtime.
type AzureSpeechConfigValidator(secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout =
        defaultArg timeout ToolUp.Platform.ConfigValidation.IConfigValidator.defaultTimeout

    interface ToolUp.Platform.ConfigValidation.IConfigValidator with
        member _.Name = "azure-speech-transcription-credentials"
        member _.Timeout = timeout

        member _.Validate() = async {
            let! keyOpt = secretStore.GetSecret("_platform", KeySecretName)
            let! regionOpt = secretStore.GetSecret("_platform", RegionSecretName)

            let present (v: string option) =
                match v with
                | Some s -> not (String.IsNullOrWhiteSpace s)
                | None -> false

            match present keyOpt, present regionOpt with
            | true, true -> return ToolUp.Platform.ConfigValidation.Ok
            | false, _ ->
                return
                    ToolUp.Platform.ConfigValidation.Error
                        "Azure Speech transcription provider is configured but the `azure-speech-key` secret is missing/empty in the `_platform` scope. Set it (env var `TOOLUP__PLATFORM_AZURE_SPEECH_KEY` / your ISecretStore) before deploying."
            | _, false ->
                return
                    ToolUp.Platform.ConfigValidation.Error
                        "Azure Speech transcription provider is configured but the `azure-speech-region` secret is missing/empty in the `_platform` scope (e.g. 'westeurope'). Set it before deploying."
        }

/// Build the Azure Speech preflight validator (presence-only).
let createValidator (secretStore: ISecretStore) : ToolUp.Platform.ConfigValidation.IConfigValidator =
    AzureSpeechConfigValidator(secretStore) :> ToolUp.Platform.ConfigValidation.IConfigValidator