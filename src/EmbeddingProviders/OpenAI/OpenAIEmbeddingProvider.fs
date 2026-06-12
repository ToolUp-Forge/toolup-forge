module OpenAIEmbeddingProvider

open System
open System.Net.Http
open System.Text
open System.Text.Json
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

/// `batchSize` caps per-call inputs to honour OpenAI's documented limit
/// (2048) with token-budget headroom. The default 64 keeps total tokens
/// per call comfortably below the 8192 per-input ceiling for typical
/// chunk sizes (~512 tokens) so callers don't accidentally trip the
/// per-request token budget when ingesting long documents.
type private OpenAIEmbeddingProviderImpl(secretStore: ISecretStore, model: string, dimensions: int, batchSize: int) =
    let client = new HttpClient(BaseAddress = Uri("https://api.openai.com"))

    let postEmbedding (requestJson: string) = async {
        let! apiKeyOpt = secretStore.GetSecret("_platform", "openai-api-key")

        match apiKeyOpt with
        | Some apiKey when not (String.IsNullOrWhiteSpace apiKey) ->
            let content = new StringContent(requestJson, Encoding.UTF8, "application/json")

            use request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
            request.Headers.Authorization <- Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey)
            request.Content <- content

            let! response = client.SendAsync(request) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore

            let! responseJson = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return JsonDocument.Parse(responseJson)
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
let private defaultModel = "text-embedding-3-small"

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

/// Create an `IEmbeddingProvider` that calls the OpenAI Embeddings API.
///
/// - `model`: embedding model name (default `"text-embedding-3-small"`)
/// - `dimensions`: expected output dimensions (default 1536 for text-embedding-3-small)
/// - API key is read from `ISecretStore` at `_platform / "openai-api-key"` per call.
let create (secretStore: ISecretStore) : IEmbeddingProvider =
    OpenAIEmbeddingProviderImpl(secretStore, defaultModel, defaultDimensions, defaultBatchSize) :> IEmbeddingProvider

let createWithModel (secretStore: ISecretStore) (model: string) (dimensions: int) : IEmbeddingProvider =
    validateDimensions model dimensions
    OpenAIEmbeddingProviderImpl(secretStore, model, dimensions, defaultBatchSize) :> IEmbeddingProvider

/// Override the per-call batch size used by `GenerateEmbeddings`. The OpenAI
/// embeddings endpoint accepts up to 2048 inputs per call; 64 is the SDK
/// default for token-budget headroom on typical chunk sizes. Increase for
/// short-chunk corpora, decrease if you see per-input-token-budget rejections.
let createWithBatchSize (secretStore: ISecretStore) (batchSize: int) : IEmbeddingProvider =
    OpenAIEmbeddingProviderImpl(secretStore, defaultModel, defaultDimensions, batchSize) :> IEmbeddingProvider

// ─── Preflight validator ──────────────────────────────────────────
//
// Without this, a missing `openai-api-key` secret first surfaces at
// RUNTIME as `Response status code does not indicate success: 401`
// buried inside per-chunk ingestion-failure events — far from the
// cause. This validator turns it into a clear startup error. It does
// NOT make a live embedding call: hard-failing startup because OpenAI
// had a transient blip would be worse than the problem it guards. The
// secret-presence check is what converts the opaque runtime 401 into
// an actionable deploy-time message.

/// Companion `IConfigValidator` for the OpenAI embedding provider.
/// Wire it into the deployment's config validators (alongside the
/// embedder itself) so a missing API key fails the deploy, not the
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

/// Build the OpenAI-embedding preflight validator.
let createValidator (secretStore: ISecretStore) : ToolUp.Platform.ConfigValidation.IConfigValidator =
    OpenAIEmbeddingConfigValidator(secretStore) :> ToolUp.Platform.ConfigValidation.IConfigValidator