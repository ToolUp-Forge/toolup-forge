module OpenAIEmbeddingProvider

open System
open System.Diagnostics
open System.Net.Http
open System.Text
open System.Text.Json
open ToolUp.Platform // IEventStore, Events, ModuleEvent (SDK.Shared)
open ToolUp.Platform.Metrics // IMetricsSink (Phase 9e)
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Secrets

// ─── Provider implementation ──────────────────────────────────────
//
// Request bodies are serialised from inline anonymous records with
// wire-cased field names. Named non-public records are NOT an option
// here: System.Text.Json's reflection serialiser only sees public
// property getters, so a `private` CLIMutable record serialises as
// `{}` — every embed call would post an empty body and get a 400.
// Anonymous records compile with public getters, so they serialise
// correctly regardless of where they're declared.
//
// Phase 14u — resilience. `postEmbedding` no longer calls
// `EnsureSuccessStatusCode()` (which folded 401 / 429 / 5xx / the BCL
// 100 s timeout into one opaque `HttpRequestException` with no retry,
// no telemetry). It now classifies each non-success response per the
// shared `EmbedderFailureClass` taxonomy (mirroring Phase 14t): 429 /
// 5xx / timeout / network → retry with exponential backoff + jitter
// (honouring `Retry-After` on a 429); 401 / 403 → no retry, emit a
// `KnowledgeEmbeddingProviderUnavailable` audit (when an event sink is
// wired) and raise `EmbeddingProviderUnavailableException`. Per-call
// latency is emitted as `embedder.openai.latency_ms` through the
// optional `IMetricsSink`. An optional, opt-in circuit breaker
// (`withEmbedderCircuitBreaker`) fast-fails calls while a known-down
// provider recovers.
//
// **Statefulness.** The provider is stateless per call (Phase 9c
// portability rule 4) UNLESS a circuit breaker is wired — the breaker
// keeps consecutive-failure + open-until state across calls, so a
// breaker-enabled provider is single-process (the documented rule-4
// exception, alongside `LocalEmbeddingProvider`). With no breaker
// (the default) the provider remains distributed-ready.

/// Terminal outcome of a single HTTP attempt against the embeddings
/// endpoint — success carries the parsed document; failure carries the
/// classification so the retry loop decides retry vs raise without
/// re-deriving the rule.
type private EmbedAttempt =
    | AttemptOk of JsonDocument
    | AttemptFailed of
        cls: EmbedderFailureClass *
        statusCode: int option *
        message: string *
        retryAfter: TimeSpan option

/// `batchSize` caps per-call inputs to honour OpenAI's documented limit
/// (2048) with token-budget headroom. The default 64 keeps total tokens
/// per call comfortably below the 8192 per-input ceiling for typical
/// chunk sizes (~512 tokens) so callers don't accidentally trip the
/// per-request token budget when ingesting long documents.
type private OpenAIEmbeddingProviderImpl
    (
        secretStore: ISecretStore,
        model: string,
        dimensions: int,
        batchSize: int,
        resilience: EmbedderResilience,
        metrics: IMetricsSink option,
        eventSink: IEventStore option
    ) =
    // Per-instance client. `Timeout` replaces the BCL default of 100 s
    // with the configured `RequestTimeout` (default 30 s) so a hung
    // connection surfaces as a retryable timeout instead of stalling an
    // ingest slot for a minute and a half.
    let client =
        let c = new HttpClient(BaseAddress = Uri("https://api.openai.com"))
        c.Timeout <- resilience.RequestTimeout
        c

    let latencyMetric = EmbedderMetrics.latencyMetricName "openai"

    // ─── Circuit-breaker state (only consulted when opted in) ────
    let breakerLock = obj ()
    let mutable consecutiveFailures = 0
    let mutable openUntil: DateTime option = None

    /// May a call proceed? `true` when no breaker is wired, when the
    /// breaker is CLOSED, or when its cooldown has elapsed (HALF-OPEN
    /// probe). `false` while OPEN.
    let breakerAllowsCall () =
        match resilience.CircuitBreaker with
        | None -> true
        | Some _ ->
            lock breakerLock (fun () ->
                match openUntil with
                | Some until when DateTime.UtcNow < until -> false
                | _ -> true)

    let breakerRecordSuccess () =
        match resilience.CircuitBreaker with
        | None -> ()
        | Some _ ->
            lock breakerLock (fun () ->
                consecutiveFailures <- 0
                openUntil <- None)

    // Recorded once per *call* (terminal outcome), not per attempt — a
    // single call that burned N transient retries is one failure toward
    // the threshold, not N.
    let breakerRecordFailure () =
        match resilience.CircuitBreaker with
        | None -> ()
        | Some cb ->
            lock breakerLock (fun () ->
                consecutiveFailures <- consecutiveFailures + 1

                if consecutiveFailures >= cb.FailureThreshold then
                    openUntil <- Some(DateTime.UtcNow + cb.Cooldown))

    let recordLatency (elapsedMs: int64) (outcome: string) =
        match metrics with
        | None -> ()
        | Some sink -> sink.Record(latencyMetric, float elapsedMs, Map.ofList [ "model", model; "outcome", outcome ])

    // Best-effort platform-scoped audit on a permanent auth failure. A
    // lost audit (event-store outage) must never mask the real error —
    // the tenant-scoped operator notification is Phase 14t's job from
    // the ingestion path, which has the scope context this low-level
    // provider deliberately doesn't.
    let emitUnavailableAudit (statusCode: int) (body: string) = async {
        match eventSink with
        | None -> ()
        | Some store ->
            try
                let payload =
                    JsonSerializer.Serialize {|
                        ProviderId = "openai"
                        ModelId = model
                        StatusCode = statusCode
                        Detail = body
                    |}

                let evt =
                    Events.create "_platform" "ToolUp.RAG" KnowledgeEmbeddingProviderUnavailableEvent payload

                do! store.Write evt
            with _ ->
                ()
    }

    /// One HTTP attempt. Never throws for a non-2xx response — it reads
    /// the body and returns a classified `AttemptFailed`; only the
    /// transport/timeout `catch` arms build a `Transient` failure.
    let sendOnce (apiKey: string) (requestJson: string) : Async<EmbedAttempt> = async {
        let content = new StringContent(requestJson, Encoding.UTF8, "application/json")

        use request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiKey)
        request.Content <- content

        let sw = Stopwatch.StartNew()

        try
            let! response = client.SendAsync(request) |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            sw.Stop()

            if response.IsSuccessStatusCode then
                recordLatency sw.ElapsedMilliseconds "success"
                return AttemptOk(JsonDocument.Parse body)
            else
                recordLatency sw.ElapsedMilliseconds "failure"
                let status = int response.StatusCode
                let cls = EmbedderFailureClass.ofStatus status

                // Honour a 429's `Retry-After` (delta seconds or an
                // absolute HTTP-date); `None` falls back to the policy's
                // computed backoff.
                let retryAfter =
                    match response.Headers.RetryAfter with
                    | null -> None
                    | ra ->
                        match Option.ofNullable ra.Delta with
                        | Some d -> Some d
                        | None ->
                            match Option.ofNullable ra.Date with
                            | Some date ->
                                let delta = date - DateTimeOffset.UtcNow
                                if delta > TimeSpan.Zero then Some delta else None
                            | None -> None

                return AttemptFailed(cls, Some status, body, retryAfter)
        with
        | :? OperationCanceledException ->
            // HttpClient.Timeout surfaces as a cancelled task — a
            // transient timeout, not a caller cancellation (the SDK
            // ingest path doesn't pass a token here).
            sw.Stop()
            recordLatency sw.ElapsedMilliseconds "timeout"

            return
                AttemptFailed(
                    EmbedderFailureClass.Transient,
                    None,
                    sprintf "request timed out after %g s" resilience.RequestTimeout.TotalSeconds,
                    None
                )
        | :? HttpRequestException as ex ->
            sw.Stop()
            recordLatency sw.ElapsedMilliseconds "network_error"
            return AttemptFailed(EmbedderFailureClass.Transient, None, ex.Message, None)
    }

    let postEmbedding (requestJson: string) : Async<JsonDocument> = async {
        if not (breakerAllowsCall ()) then
            return
                raise (
                    EmbeddingProviderCircuitOpenException(
                        "openai embedding provider circuit breaker is OPEN — calls are being fast-failed until the cooldown elapses (the provider has failed on consecutive calls; check the API key, rate-limit headroom, and OpenAI status)."
                    )
                )
        else
            let! apiKeyOpt = secretStore.GetSecret("_platform", "openai-api-key")

            match apiKeyOpt with
            | Some apiKey when not (String.IsNullOrWhiteSpace apiKey) ->
                let policy = resilience.Retry

                let rec loop attemptsMade = async {
                    let! attempt = sendOnce apiKey requestJson
                    let attemptsMade = attemptsMade + 1

                    match attempt with
                    | AttemptOk doc ->
                        breakerRecordSuccess ()
                        return doc
                    | AttemptFailed(EmbedderFailureClass.Permanent, statusCode, message, _) ->
                        breakerRecordFailure ()

                        match statusCode with
                        | Some sc when EmbedderFailureClass.isAuthFailure sc ->
                            // Auth failure — a revoked / wrong key. No
                            // retry (the same request fails identically),
                            // emit the platform-scoped audit, raise a
                            // typed exception the ingestion path can catch.
                            do! emitUnavailableAudit sc message
                            return raise (EmbeddingProviderUnavailableException(sc, message))
                        | Some sc ->
                            return
                                raise (
                                    InvalidOperationException(
                                        sprintf
                                            "OpenAI embeddings API returned HTTP %d (permanent — not retried; the same request would fail identically). Body: %s"
                                            sc
                                            message
                                    )
                                )
                        | None ->
                            return
                                raise (
                                    InvalidOperationException(
                                        sprintf
                                            "OpenAI embeddings call failed permanently and was not retried: %s"
                                            message
                                    )
                                )
                    | AttemptFailed(EmbedderFailureClass.Transient, _, message, retryAfter) ->
                        if attemptsMade >= policy.MaxAttempts then
                            breakerRecordFailure ()
                            return raise (EmbeddingProviderRetriesExhaustedException(attemptsMade, message))
                        else
                            // Honour `Retry-After` when the server sent
                            // one (capped at MaxBackoff so a wedged hour-
                            // long value can't park the retry); otherwise
                            // exponential backoff. Jitter both.
                            let baseDelay =
                                match retryAfter with
                                | Some ra -> min ra policy.MaxBackoff
                                | None -> EmbedderRetryPolicy.baseDelayFor policy (attemptsMade + 1)

                            let delay = EmbedderRetryPolicy.jitter policy baseDelay (Random.Shared.NextDouble())

                            do! Async.Sleep(int delay.TotalMilliseconds)
                            return! loop attemptsMade
                }

                return! loop 0
            | _ ->
                // A missing/blank key at call time previously degraded to a
                // `Bearer ` header → an opaque 401 surfaced as per-chunk
                // ingestion failures, indistinguishable from a code fault.
                // The startup `OpenAIEmbeddingConfigValidator` only checks
                // deploy-time presence; a key rotated away or a transient
                // secret-store miss slips past it. Fail with an actionable
                // message naming the cause instead.
                return
                    raise (
                        InvalidOperationException(
                            "openai-api-key is absent or blank in the _platform secret scope at embed time — the embedding call was aborted. This indicates the key was rotated away or the secret store returned no value at runtime (the startup validator only checks deploy-time presence). Restore the secret (env var TOOLUP__PLATFORM_OPENAI_API_KEY / your ISecretStore)."
                        )
                    )
    }

    let parseEmbedding (el: JsonElement) =
        el.GetProperty("embedding").EnumerateArray()
        |> Seq.map _.GetSingle()
        |> Seq.toArray

    // Reassemble a response's `data` array into input order. The API
    // documents that entries carry an `index` field and does not promise
    // arrival order; mapping positionally would silently assign wrong
    // embeddings to chunks on an out-of-order response — corpus
    // corruption that re-embedding cannot heal, because the version
    // stamps (provider/model/dim) still match. Count + range + duplicate
    // checks together guarantee every slot is filled (pigeonhole), so a
    // short or malformed response fails the batch loudly instead.
    let reassembleByIndex (inputCount: int) (doc: JsonDocument) : float32 array array =
        let data = doc.RootElement.GetProperty("data")
        let count = data.GetArrayLength()

        if count <> inputCount then
            failwith (
                sprintf
                    "OpenAI embeddings response carried %d data entries for %d inputs — refusing the batch. Accepting a short/long response would assign embeddings to the wrong chunks (silent corpus corruption that re-embedding cannot heal — version stamps still match)."
                    count
                    inputCount
            )

        let results = Array.zeroCreate<float32 array> inputCount

        for el in data.EnumerateArray() do
            let idx = el.GetProperty("index").GetInt32()

            if idx < 0 || idx >= inputCount then
                failwith (
                    sprintf
                        "OpenAI embeddings response entry carried index %d, outside the request's input range 0..%d — refusing the batch."
                        idx
                        (inputCount - 1)
                )

            if not (isNull (box results[idx])) then
                failwith (
                    sprintf
                        "OpenAI embeddings response carried duplicate entries for index %d — refusing the batch."
                        idx
                )

            results[idx] <- parseEmbedding el

        results

    interface IEmbeddingProvider with

        member _.Dimensions = dimensions

        member _.ProviderId = "openai"

        member _.ModelId = model

        member _.GenerateEmbedding(text: string) = async {
            let! doc = postEmbedding (JsonSerializer.Serialize {| model = model; input = text |})

            return reassembleByIndex 1 doc |> Array.head
        }

        // Native batched path: a single POST carrying `Input` as a string
        // array. Inputs are chunked into groups of `batchSize` so a 1000-
        // chunk document doesn't hit the per-request token budget; chunks
        // run sequentially rather than in parallel because the OpenAI
        // embedding endpoint rate-limits per-key by tokens-per-minute and
        // parallel posts would only churn the limiter without amortising
        // round-trip latency further.
        member _.GenerateEmbeddings(texts: string seq) = async {
            let inputs = texts |> Seq.toArray

            if inputs.Length = 0 then
                return Array.empty
            else
                let batches = inputs |> Array.chunkBySize (max 1 batchSize)
                let results = ResizeArray<float32 array>(inputs.Length)

                for batch in batches do
                    let! doc = postEmbedding (JsonSerializer.Serialize {| model = model; input = batch |})

                    results.AddRange(reassembleByIndex batch.Length doc)

                return results.ToArray()
        }

// ─── Factory functions ────────────────────────────────────────────

[<Literal>]
let defaultModel = "text-embedding-3-small"

[<Literal>]
let private defaultDimensions = 1536

[<Literal>]
let private defaultBatchSize = 64

/// Native output dimensionality of OpenAI's embedding models. The SDK's
/// request body never sends the optional request-level `dimensions`
/// shortening parameter, so a known model ALWAYS returns its native
/// size — declaring any other value silently corrupts the vector store
/// (mismatched-length vectors → saturated cosine distance → "no
/// relevant content" with no error and no self-heal, since `ModelId`
/// is unchanged so `ReembeddingService` never re-indexes).
let private knownModelDimensions =
    Map [
        "text-embedding-3-small", 1536
        "text-embedding-3-large", 3072
        "text-embedding-ada-002", 1536
    ]

/// Fail fast with an actionable message when a *known* model is paired
/// with a non-native dimension. Unknown models are accepted as-is —
/// the documented extensibility path for models released after this
/// build (their dimension cannot be validated offline).
let private validateDimensions (model: string) (dimensions: int) =
    match Map.tryFind model knownModelDimensions with
    | Some native when native <> dimensions ->
        invalidArg
            "dimensions"
            (sprintf
                "OpenAI embedding model %s emits %d-dimensional vectors, but createWithModel was called with dimensions = %d. The SDK does not send the request-level `dimensions` shortening parameter, so the store would index %d-length vectors tagged as %d — every query then saturates cosine distance and retrieval silently returns nothing. Pass %d, or use `create` for the default model."
                model
                native
                dimensions
                native
                dimensions
                native)
    | _ -> ()

/// Configuration for an OpenAI embedding provider, threaded through the
/// `with*` builder helpers. `Metrics` and `EventSink` are optional
/// substrate dependencies (Phase 9e metrics, audit) — a provider built
/// without them still works, just without per-call telemetry / the
/// platform-scoped unavailable audit.
type OpenAIEmbeddingOptions = {
    Model: string
    Dimensions: int
    BatchSize: int
    Resilience: EmbedderResilience
    Metrics: IMetricsSink option
    EventSink: IEventStore option
}

module OpenAIEmbeddingOptions =
    /// Default options: `text-embedding-3-small`, native 1536 dims,
    /// batch 64, `EmbedderResilience.defaults` (30 s timeout + 3-attempt
    /// retry, no circuit breaker), no metrics / audit sink.
    let defaults: OpenAIEmbeddingOptions = {
        Model = defaultModel
        Dimensions = defaultDimensions
        BatchSize = defaultBatchSize
        Resilience = EmbedderResilience.defaults
        Metrics = None
        EventSink = None
    }

/// Set an explicit model + native dimension (validated as in `createWithModel`).
let withEmbedderModel (model: string) (dimensions: int) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Model = model
        Dimensions = dimensions
}

/// Override the per-call batch size used by `GenerateEmbeddings`.
let withEmbedderBatchSize (batchSize: int) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        BatchSize = batchSize
}

/// Replace the whole resilience config (timeout + retry + circuit breaker).
let withEmbedderResilience (resilience: EmbedderResilience) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Resilience = resilience
}

/// Set only the per-call request timeout (default 30 s).
let withEmbedderTimeout (timeout: TimeSpan) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Resilience = {
            o.Resilience with
                RequestTimeout = timeout
        }
}

/// Set only the retry policy.
let withEmbedderRetryPolicy (retry: EmbedderRetryPolicy) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Resilience = { o.Resilience with Retry = retry }
}

/// Opt in to the circuit breaker (off by default). Enabling it makes
/// the provider stateful across calls — single-process only.
let withEmbedderCircuitBreaker (breaker: EmbedderCircuitBreaker) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Resilience = {
            o.Resilience with
                CircuitBreaker = Some breaker
        }
}

/// Wire an `IMetricsSink` so per-call latency is emitted as
/// `embedder.openai.latency_ms` (Phase 9e).
let withEmbedderMetrics (metrics: IMetricsSink) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        Metrics = Some metrics
}

/// Wire an `IEventStore` so a permanent auth failure emits a
/// platform-scoped `KnowledgeEmbeddingProviderUnavailable` audit.
let withEmbedderAudit (eventSink: IEventStore) (o: OpenAIEmbeddingOptions) : OpenAIEmbeddingOptions = {
    o with
        EventSink = Some eventSink
}

/// Build a provider from a fully-specified options record. The entry
/// point for resilience / telemetry / audit configuration.
let createWithOptions (secretStore: ISecretStore) (options: OpenAIEmbeddingOptions) : IEmbeddingProvider =
    validateDimensions options.Model options.Dimensions

    OpenAIEmbeddingProviderImpl(
        secretStore,
        options.Model,
        options.Dimensions,
        options.BatchSize,
        options.Resilience,
        options.Metrics,
        options.EventSink
    )
    :> IEmbeddingProvider

/// Create an `IEmbeddingProvider` that calls the OpenAI Embeddings API.
///
/// - `model`: embedding model name (default `"text-embedding-3-small"`)
/// - `dimensions`: expected output dimensions (default 1536 for text-embedding-3-small)
/// - API key is read from `ISecretStore` at `_platform / "openai-api-key"` per call.
/// - Resilience defaults apply (30 s timeout, 3-attempt retry, no circuit breaker);
///   use `createWithOptions` + the `with*` helpers to tune them or wire metrics / audit.
let create (secretStore: ISecretStore) : IEmbeddingProvider =
    createWithOptions secretStore OpenAIEmbeddingOptions.defaults

let createWithModel (secretStore: ISecretStore) (model: string) (dimensions: int) : IEmbeddingProvider =
    OpenAIEmbeddingOptions.defaults
    |> withEmbedderModel model dimensions
    |> createWithOptions secretStore

/// Override the per-call batch size used by `GenerateEmbeddings`. The OpenAI
/// embeddings endpoint accepts up to 2048 inputs per call; 64 is the SDK
/// default for token-budget headroom on typical chunk sizes. Increase for
/// short-chunk corpora, decrease if you see per-input-token-budget rejections.
let createWithBatchSize (secretStore: ISecretStore) (batchSize: int) : IEmbeddingProvider =
    OpenAIEmbeddingOptions.defaults
    |> withEmbedderBatchSize batchSize
    |> createWithOptions secretStore

// ─── Phase 671 — env-driven construction (the resolver entry point) ──
//
// Read through the `ConfigResolution` seam, so a deployment manifest can
// declare these exactly as an environment variable does. THE API KEY IS
// NOT READ HERE and never will be: it resolves through `ISecretStore` at
// embed time (see `postEmbedding`), which is the provider-authoring
// rule — a secret in the environment is a secret in every process
// listing, crash dump and container inspect on the host.

/// Parse an integer-valued config key's resolved value, refusing an
/// unparseable one rather than silently falling back. A typo in
/// `TOOLUP_EMBEDDING_DIMENSIONS` that read as "unset" would build a
/// provider with the wrong vector length, and a wrong length is the
/// failure this file's `validateDimensions` comment describes at length:
/// saturated cosine distance, "no relevant content", no error, and no
/// self-heal.
///
/// It takes the ALREADY-RESOLVED value rather than the key name, so each
/// call site carries a literal `ConfigResolution.tryValue
/// ConfigKeys.Names.*` — the shape the registry's manifest-bindability
/// check is anchored on. Resolving inside here instead would hide the
/// seam call behind one indirection and the key would read as declared
/// bindable while nothing resolved it.
let private parseConfigInt (name: string) (raw: string option) : int option =
    raw
    |> Option.map (fun value ->
        match Int32.TryParse(value.Trim()) with
        | true, v -> v
        | _ ->
            invalidOp (
                sprintf
                    "%s=%s is not an integer. Set it to a whole number, or unset it to take the default."
                    name
                    value
            ))

/// Build an OpenAI embedding provider from the `TOOLUP_EMBEDDING_*`
/// cluster — the resolver entry point for `EmbeddingProviderEnv.fromEnv`
/// (`TOOLUP_EMBEDDING_PROVIDER=openai`).
///
/// - `TOOLUP_EMBEDDING_MODEL` — default `text-embedding-3-small`.
/// - `TOOLUP_EMBEDDING_DIMENSIONS` — default: the model's native size
///   for a model this build knows. For a model it does not know there is
///   no safe default, so the variable becomes REQUIRED and its absence
///   refuses startup by name; guessing 1536 would index mis-sized
///   vectors under a matching version stamp, which no reembed can heal.
///   A value that contradicts a known model's native size is refused by
///   `createWithOptions`, as it is on every other construction path.
/// - `TOOLUP_EMBEDDING_BATCH_SIZE` — default 64.
///
/// Always `Some`: every input has a default or a refusal, so there is no
/// "configured but incomplete" state for this companion to decline on.
/// A missing API key is deliberately NOT that state — it is the
/// `OpenAIEmbeddingConfigValidator`'s to report at preflight, where an
/// operator sees why, rather than a silent fallback to a different
/// provider embedding a corpus into a different vector space.
let fromEnv (secretStore: ISecretStore) : IEmbeddingProvider option =
    let model =
        ConfigResolution.tryValue ConfigKeys.Names.embeddingModel
        |> Option.map _.Trim()
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultValue defaultModel

    let dimensions =
        match
            ConfigResolution.tryValue ConfigKeys.Names.embeddingDimensions
            |> parseConfigInt ConfigKeys.Names.embeddingDimensions
        with
        | Some d -> d
        | None ->
            match Map.tryFind model knownModelDimensions with
            | Some native -> native
            | None ->
                invalidOp (
                    sprintf
                        "TOOLUP_EMBEDDING_MODEL=%s is not a model this build knows the native output size of, and TOOLUP_EMBEDDING_DIMENSIONS is unset. Set TOOLUP_EMBEDDING_DIMENSIONS to the model's documented dimensionality. It is not defaulted because a wrong length is indexed under a matching version stamp: every query then saturates cosine distance, retrieval returns nothing, and no reembed pass repairs it."
                        model
                )

    let batchSize =
        ConfigResolution.tryValue ConfigKeys.Names.embeddingBatchSize
        |> parseConfigInt ConfigKeys.Names.embeddingBatchSize
        |> Option.defaultValue defaultBatchSize

    OpenAIEmbeddingOptions.defaults
    |> withEmbedderModel model dimensions
    |> withEmbedderBatchSize batchSize
    |> createWithOptions secretStore
    |> Some

// ─── Live API probe (shared by health check + preflight) ──────────
//
// A one-shot real `embeddings.create` call against a fixed, tiny input.
// Catches revoked keys, wrong keys, and model-access mismatches that the
// secret-presence check misses — the whole point of Phase 14u's
// strengthened health probe. Deliberately a single short input so the
// token cost is ~1 token per probe. Separate short-timeout client so a
// hung probe can't inherit a long provider timeout.

/// Outcome of a one-shot live `embeddings.create` probe.
type EmbeddingProbeResult =
    /// The call succeeded — the key is valid and the model is reachable.
    | ProbeOk
    /// No key configured in the secret store.
    | ProbeMissingKey
    /// A definitive 4xx that won't self-heal — 401 / 403 (revoked /
    /// wrong key) or e.g. 404 (model not accessible). Carries the status
    /// so callers can name it ("embeddings.create returned 401").
    | ProbePermanent of statusCode: int * body: string
    /// A transient condition (429 / 5xx / timeout / network) — the probe
    /// couldn't verify the provider but it may simply be briefly
    /// degraded, so callers should not treat it as definitively broken.
    | ProbeTransient of message: string

let private probeClient =
    lazy
        (let c = new HttpClient(BaseAddress = Uri("https://api.openai.com"))
         c.Timeout <- TimeSpan.FromSeconds 10.0
         c)

/// Issue one live `embeddings.create` against `model` using the key in
/// the `_platform / openai-api-key` secret. Never throws — transport /
/// timeout failures map to `ProbeTransient`.
let probeEmbeddingsApi (secretStore: ISecretStore) (model: string) : Async<EmbeddingProbeResult> = async {
    let! keyOpt = secretStore.GetSecret("_platform", "openai-api-key")

    match keyOpt with
    | Some apiKey when not (String.IsNullOrWhiteSpace apiKey) ->
        try
            let json = JsonSerializer.Serialize {| model = model; input = "ok" |}
            let content = new StringContent(json, Encoding.UTF8, "application/json")

            use request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiKey)
            request.Content <- content

            let! response = probeClient.Value.SendAsync(request) |> Async.AwaitTask

            if response.IsSuccessStatusCode then
                return ProbeOk
            else
                let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
                let status = int response.StatusCode

                match EmbedderFailureClass.ofStatus status with
                | EmbedderFailureClass.Transient -> return ProbeTransient(sprintf "HTTP %d: %s" status body)
                | EmbedderFailureClass.Permanent -> return ProbePermanent(status, body)
        with ex ->
            return ProbeTransient ex.Message
    | _ -> return ProbeMissingKey
}

// ─── Preflight validators ─────────────────────────────────────────
//
// Two flavours. The presence validator (`createValidator`, the default)
// checks only that the secret resolves to a non-empty value — cheap,
// deploy-time, never touches OpenAI. The live validator
// (`createLiveValidator`) additionally issues one real
// `embeddings.create`, catching a revoked-but-non-empty key. Critically
// it returns `Warning`, not `Error`, on an auth failure: a key can be
// rotated in after deploy without a restart, so a revoked key at
// preflight is a flag, not a startup abort. A genuinely absent secret
// still returns `Error` (that IS a misconfiguration).

/// Companion `IConfigValidator` (presence-only) for the OpenAI embedding
/// provider. Wire it into the deployment's config validators (alongside
/// the embedder itself) so a missing API key fails the deploy, not the
/// first ingestion.
type OpenAIEmbeddingConfigValidator(secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout =
        defaultArg timeout ToolUp.Platform.ConfigValidation.IConfigValidator.defaultTimeout

    interface ToolUp.Platform.ConfigValidation.IConfigValidator with
        member _.Name = "openai-embedding-api-key"
        member _.Timeout = timeout

        member _.Validate() = async {
            let! keyOpt = secretStore.GetSecret("_platform", "openai-api-key")

            match keyOpt with
            | Some k when not (String.IsNullOrWhiteSpace k) -> return ToolUp.Platform.ConfigValidation.Ok
            | _ ->
                return
                    ToolUp.Platform.ConfigValidation.Error
                        "OpenAI embedding provider is configured but the `openai-api-key` secret is missing/empty in the `_platform` scope. Every embedding call would fail at runtime with an opaque 401 surfaced as per-chunk ingestion failures. Set the secret (env var `TOOLUP__PLATFORM_OPENAI_API_KEY` / your ISecretStore) before deploying."
        }

/// Companion `IConfigValidator` that issues a real `embeddings.create`
/// at preflight — catches a revoked / wrong key or an inaccessible
/// model that the presence check misses. Returns `Warning` (not `Error`)
/// on an auth failure so a key rotated in after deploy isn't blocked by
/// a startup abort; only an absent secret is a hard `Error`.
type OpenAIEmbeddingLiveConfigValidator(secretStore: ISecretStore, ?model: string, ?timeout: TimeSpan) =
    let model = defaultArg model defaultModel

    let timeout =
        defaultArg timeout ToolUp.Platform.ConfigValidation.IConfigValidator.defaultTimeout

    interface ToolUp.Platform.ConfigValidation.IConfigValidator with
        member _.Name = "openai-embedding-api-key (live)"
        member _.Timeout = timeout

        member _.Validate() = async {
            let! result = probeEmbeddingsApi secretStore model

            match result with
            | ProbeOk -> return ToolUp.Platform.ConfigValidation.Ok
            | ProbeMissingKey ->
                return
                    ToolUp.Platform.ConfigValidation.Error
                        "OpenAI embedding provider is configured but the `openai-api-key` secret is missing/empty in the `_platform` scope. Set the secret (env var `TOOLUP__PLATFORM_OPENAI_API_KEY` / your ISecretStore) before deploying."
            | ProbePermanent(status, _) ->
                return
                    ToolUp.Platform.ConfigValidation.Warning(
                        sprintf
                            "OpenAI embeddings preflight: embeddings.create returned %d — the `openai-api-key` is present but rejected (revoked / wrong key, or the model is inaccessible). Deploy continues because keys can rotate in without a restart, but ingestion will fail until the key is fixed."
                            status
                    )
            | ProbeTransient msg ->
                return
                    ToolUp.Platform.ConfigValidation.Warning(
                        sprintf
                            "OpenAI embeddings preflight could not verify the `openai-api-key` (transient: %s). Deploy continues; the key may simply be briefly unreachable."
                            msg
                    )
        }

/// Build the OpenAI-embedding preflight validator (presence-only).
let createValidator (secretStore: ISecretStore) : ToolUp.Platform.ConfigValidation.IConfigValidator =
    OpenAIEmbeddingConfigValidator(secretStore) :> ToolUp.Platform.ConfigValidation.IConfigValidator

/// Build the OpenAI-embedding preflight validator that issues a real
/// `embeddings.create` (catches revoked keys; warns, never aborts).
let createLiveValidator (secretStore: ISecretStore) : ToolUp.Platform.ConfigValidation.IConfigValidator =
    OpenAIEmbeddingLiveConfigValidator(secretStore) :> ToolUp.Platform.ConfigValidation.IConfigValidator