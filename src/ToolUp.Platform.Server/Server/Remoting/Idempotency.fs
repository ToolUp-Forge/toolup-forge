namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent
open Microsoft.FSharp.Reflection

// =============================================================================
// Phase 69f — per-method idempotency keys
// =============================================================================
//
// API record fields can carry `[<Idempotent>]` to opt into client-key-driven
// memoisation. The dispatcher requires an `X-Idempotency-Key` header on
// matching calls; the first call's response is captured and replayed on
// subsequent calls with the same key (within TTL).
//
// Key shape: `{subjectId}|{methodName}|{idempotencyKey}` so the same UUID
// submitted by two different subjects is two distinct cache slots — a
// security boundary, not a sharing one.
//
// Order in the dispatcher pre-flight chain:
//   auth (69d) → idempotency (69f) → rate-limit (69g) → handler
// Rationale: an idempotency cache hit means we're replaying a response
// that was already counted against rate-limit on its first call;
// re-counting it would double-charge the budget.

/// Phase 69f — opts a method into client-key-driven idempotency. Calls
/// against an `[<Idempotent>]` method MUST carry an `X-Idempotency-Key`
/// header; the dispatcher denies with `RemotingError.User` envelope
/// when the header is missing AND a store is composed. When no store is
/// composed, the attribute is dormant (the method runs normally on
/// every call).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type IdempotentAttribute() =
    inherit Attribute()

// -----------------------------------------------------------------------------

/// Phase 69f — default in-memory implementation of `IIdempotencyStore`.
/// Concurrent dictionary keyed on `{scope}|{key}`; TTL eviction is lazy
/// (checked on read). Bounded by `maxEntries` (default 100_000) — once
/// the cap is reached, the oldest entry (by insertion order) is evicted
/// on next `Store`. Cardinality-bounded so a burst of unique keys
/// (e.g. retry storms with fresh GUIDs) can't OOM the host.
type InMemoryIdempotencyStore(?maxEntries: int) =
    let cap = defaultArg maxEntries 100_000

    let entries =
        ConcurrentDictionary<string, struct (DateTimeOffset * MemoisedResponse)>()
    // Insertion-order queue for FIFO eviction when the dictionary
    // exceeds `cap`. ConcurrentQueue is approximately ordered; exact
    // ordering isn't required for the LRU semantics.
    let order = ConcurrentQueue<string>()

    let compositeKey scope key = scope + "|" + key

    /// Opportunistically drain stale heads from `order` — keys that
    /// were already removed from `entries` (e.g. lazy TTL expiry, or
    /// re-added keys leaving a stale FIFO marker). Keeps `order.Count`
    /// close to `entries.Count` so subsequent eviction work doesn't
    /// scan through ghost entries.
    let compactStaleHeads () =
        let mutable peeked = Unchecked.defaultof<string>
        let mutable keepCompacting = true

        while keepCompacting && order.TryPeek(&peeked) do
            if entries.ContainsKey peeked then
                keepCompacting <- false
            else
                let mutable _ignored = Unchecked.defaultof<string>
                order.TryDequeue(&_ignored) |> ignore

    let evictOldestIfFull () =
        compactStaleHeads ()

        while entries.Count >= cap do
            let mutable victim = Unchecked.defaultof<string>

            if order.TryDequeue(&victim) then
                entries.TryRemove victim |> ignore
            else
                // Queue empty but dictionary still over cap — recover by
                // exiting the loop; counter race resolves on next Store.
                ()
                // break-equivalent: forcibly set a non-overflowing state
                if entries.Count >= cap then
                    entries.Clear()

    interface IIdempotencyStore with
        member _.TryGet(key, scope) = async {
            let now = DateTimeOffset.UtcNow
            let composite = compositeKey scope key

            match entries.TryGetValue composite with
            | true, struct (expiry, response) ->
                if now < expiry then
                    return Some response
                else
                    // Lazy eviction.
                    entries.TryRemove composite |> ignore
                    return None
            | false, _ -> return None
        }

        member _.Store(key, scope, response, ttl) = async {
            evictOldestIfFull ()
            let expiry = DateTimeOffset.UtcNow + ttl
            let composite = compositeKey scope key
            entries[composite] <- struct (expiry, response)
            order.Enqueue composite
        }

    /// Diagnostics: current entry count, for telemetry / health checks.
    member _.Count = entries.Count
    /// Diagnostics: configured cap.
    member _.MaxEntries = cap

// -----------------------------------------------------------------------------

/// Phase 69f.C — distributed `IIdempotencyStore` over `IBlobStorage`.
/// Memoised entries are JSON blobs under the `_platform` container (the
/// reserved SDK-level scope), named by a SHA-256 of `{scope}|{key}` so
/// arbitrary subject ids / method names / client keys produce a safe,
/// collision-resistant blob name. TTL is carried in the envelope and
/// enforced lazily on `TryGet` (an expired entry is read as a miss and
/// best-effort deleted).
///
/// **Concurrency.** `IBlobStorage` exposes no conditional-write / ETag
/// surface, so two requests racing the same key both miss-then-store and
/// the second overwrites the first — last-write-wins. The handler is
/// idempotent by contract, so both writes carry the same response and
/// the race is benign for replay correctness; it does NOT guarantee
/// exactly-once *handler invocation* under a concurrent race (neither
/// does the in-process default — that needs a conditional-write the
/// interface doesn't model). Distributed deployments that need stricter
/// once-only semantics wire a store with a compare-and-set primitive
/// (Redis SETNX / DynamoDB conditional put) against the same contract.
type BlobIdempotencyStore(blobStorage: ToolUp.Platform.BlobStorage.IBlobStorage, ?container: string) =
    let container = defaultArg container "_platform"

    let blobName (scope: string) (key: string) : string =
        use sha = System.Security.Cryptography.SHA256.Create()
        let bytes = System.Text.Encoding.UTF8.GetBytes(scope + "|" + key)
        let hash = System.Convert.ToHexString(sha.ComputeHash bytes).ToLowerInvariant()
        "idem/" + hash + ".json"

    // Serialise the envelope with `Utf8JsonWriter` / read it with
    // `JsonDocument` — no reflection over an F# record type, so the
    // round-trip is robust regardless of STJ's F#-record handling. The
    // body is base64 so the JSON stays text-clean for binary responses.
    let serialise (response: MemoisedResponse) (expiryTicks: int64) : byte[] =
        use ms = new System.IO.MemoryStream()

        (use writer = new System.Text.Json.Utf8JsonWriter(ms)
         writer.WriteStartObject()
         writer.WriteNumber("StatusCode", response.StatusCode)
         writer.WriteString("ContentType", response.ContentType)
         writer.WriteString("RequestBodyHash", response.RequestBodyHash)
         writer.WriteNumber("ExpiryUtcTicks", expiryTicks)
         writer.WriteString("BodyBase64", System.Convert.ToBase64String response.Body)
         writer.WriteEndObject()
         writer.Flush())

        ms.ToArray()

    interface IIdempotencyStore with
        member _.TryGet(key, scope) = async {
            let name = blobName scope key
            let! downloaded = blobStorage.Download(container, name)

            match downloaded with
            | Ok bytes ->
                try
                    use doc = System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte>(bytes))
                    let root = doc.RootElement
                    let expiry = root.GetProperty("ExpiryUtcTicks").GetInt64()

                    if DateTimeOffset.UtcNow.UtcTicks < expiry then
                        return
                            Some {
                                Body = System.Convert.FromBase64String(root.GetProperty("BodyBase64").GetString())
                                StatusCode = root.GetProperty("StatusCode").GetInt32()
                                ContentType = root.GetProperty("ContentType").GetString()
                                RequestBodyHash = root.GetProperty("RequestBodyHash").GetString()
                            }
                    else
                        // Lazy TTL expiry — best-effort delete, read as miss.
                        let! _ = blobStorage.Delete(container, name)
                        return None
                with _ ->
                    // Corrupt / unreadable envelope is a miss, never a crash.
                    return None
            | Error _ -> return None
        }

        member _.Store(key, scope, response, ttl) = async {
            let expiryTicks = (DateTimeOffset.UtcNow + ttl).UtcTicks
            let! _ = blobStorage.Upload(container, blobName scope key, serialise response expiryTicks)
            return ()
        }

// -----------------------------------------------------------------------------

module internal Idempotency =

    // Reflect over public AND non-public records, and recognise BOTH the
    // server-tier `IdempotentAttribute` and the tier-shared
    // `ToolUp.Platform.IdempotentAttribute` mirror (which Fable-compiled
    // Core API records carry) by simple type name — same family-agnostic
    // + fail-open fix as the 69d/69h/69g/69e classifiers. Without it a
    // Core API record's `[<Idempotent>]` is invisible (idempotency never
    // engages), and a non-public record silently skips classification.
    let private reflectionFlags =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    let private isIdempotentAttr (a: obj) : bool =
        match a with
        | :? IdempotentAttribute -> true
        | _ -> a.GetType().Name = "IdempotentAttribute"

    /// Cache the `[<Idempotent>]` classification per method at startup.
    let classify (apiType: Type) : Set<string> =
        if not (FSharpType.IsRecord(apiType, reflectionFlags)) then
            Set.empty
        else
            FSharpType.GetRecordFields(apiType, reflectionFlags)
            |> Array.choose (fun pi ->
                let hasIdempotent = pi.GetCustomAttributes(true) |> Array.exists isIdempotentAttr

                if hasIdempotent then Some pi.Name else None)
            |> Set.ofArray

    /// Derive the scope string for the idempotency store from the
    /// subject id + method name. Same shape as the rate-limit key root
    /// so the two seams partition the same way.
    let deriveScope (subjectKey: string) (methodName: string) : string = subjectKey + "|" + methodName

    /// 0.1.15 — SHA-256 (lowercase hex) of the request body. Stamped
    /// into the `MemoisedResponse` on first call; compared against the
    /// current call's body on replay. Mismatch surfaces as a
    /// `ErrorCategory.User` envelope + 409 Conflict so the caller
    /// notices the idempotency-key reuse instead of receiving the
    /// prior response.
    ///
    /// 0.1.16 — accepts the body as `byte[]` rather than `string`.
    /// The 0.1.15 string shape went through a UTF-8 decode that
    /// silently replaced invalid bytes with U+FFFD, producing
    /// false-match hashes for any multipart upload containing
    /// pathological byte sequences. The raw-bytes form is the
    /// only correct hash for binary bodies (multipart uploads).
    /// The `hashRequestBodyText` wrapper is kept for JSON callers
    /// who already have the text in hand.
    let hashRequestBodyBytes (bodyBytes: byte[]) : string =
        use sha = System.Security.Cryptography.SHA256.Create()
        let hash = sha.ComputeHash bodyBytes
        System.Convert.ToHexString(hash).ToLowerInvariant()

    let hashRequestBody (bodyText: string) : string =
        let bytes = System.Text.Encoding.UTF8.GetBytes(bodyText)
        hashRequestBodyBytes bytes