namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent

// =============================================================================
// Per-method idempotency keys
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
//   auth → idempotency → rate-limit → handler
// Rationale: an idempotency cache hit means we're replaying a response
// that was already counted against rate-limit on its first call;
// re-counting it would double-charge the budget.

/// Opts a method into client-key-driven idempotency. Calls
/// against an `[<Idempotent>]` method MUST carry an `X-Idempotency-Key`
/// header; the dispatcher denies with `RemotingError.User` envelope
/// when the header is missing AND a store is composed. When no store is
/// composed, the attribute is dormant (the method runs normally on
/// every call).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type IdempotentAttribute() =
    inherit Attribute()

// -----------------------------------------------------------------------------

/// Default in-memory implementation of `IIdempotencyStore`. Concurrent
/// dictionary keyed on `{scope}|{key}`; TTL eviction is lazy (checked on read).
/// Bounded by `maxEntries` (default 100_000) through `BoundedFifo`: once the
/// cap is reached, the oldest entry (by insertion order) is evicted on the next
/// `Store`, so a burst of unique keys (retry storms with fresh GUIDs) cannot
/// OOM the host.
///
/// Eviction is a bounded drain, never a mass wipe: wiping would make every
/// in-flight idempotency key miss and re-execute its handler. If no eviction
/// marker is left while the store is at its cap, the store accepts a transient
/// over-cap and records the event (`OverCapRecoveryCount` + an optional
/// `logger` `Warn`), so the path is observable. The optional `logger` is used
/// only for that signal; omit it to stay silent (GP 11).
type InMemoryIdempotencyStore(?maxEntries: int, ?logger: ToolUp.Platform.ILogger) =
    let cap = defaultArg maxEntries 100_000

    let entries =
        ConcurrentDictionary<string, struct (DateTimeOffset * MemoisedResponse)>()

    // A heap-backed single-element int64 so the lock-free `Interlocked`
    // increment can take a byref to it from inside a closure: a `let mutable`
    // field cannot be address-taken through one, an array element can.
    let overCapRecoveries: int64[] = Array.zeroCreate 1

    let compositeKey scope key = scope + "|" + key

    /// Increments the cumulative counter and emits an operator-visible `Warn`.
    /// A store with markers left never reaches here, so this is self-throttling:
    /// no log flood under normal at-cap operation.
    let recordOverCapRecovery () =
        let total = System.Threading.Interlocked.Increment(&overCapRecoveries[0])

        match logger with
        | Some log ->
            log.Warn(
                sprintf
                    "InMemoryIdempotencyStore: FIFO eviction queue empty while at/over the %d-entry cap — accepting a transient over-cap instead of clearing the cache (cumulative over-cap recoveries: %d). Sustained occurrences mean the idempotency store is under-provisioned for this instance's traffic; raise maxEntries or wire a distributed IIdempotencyStore."
                    cap
                    total
            )
        | None -> ()

    let fifo =
        BoundedFifo(
            cap,
            (fun () -> entries.Count),
            entries.ContainsKey,
            (fun key -> entries.TryRemove key |> ignore),
            BoundedFifoPolicy.oldestFirst (function
                | FifoPressure.NoMarkers -> recordOverCapRecovery ()
                | _ -> ())
        )

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
                    fifo.Withdraw composite
                    return None
            | false, _ -> return None
        }

        member _.Store(key, scope, response, ttl) = async {
            let expiry = DateTimeOffset.UtcNow + ttl
            let composite = compositeKey scope key

            // The critical section is a handful of dictionary and queue
            // operations, with no I/O and no awaits, so holding the lock is cheap
            // for an in-process store; distributed deployments wire a
            // Redis/DynamoDB impl with its own atomicity.
            fifo.Admit(composite, fun () -> entries[composite] <- struct (expiry, response))
        }

    /// Diagnostics: current entry count, for telemetry / health checks.
    member _.Count = entries.Count
    /// Diagnostics: configured cap.
    member _.MaxEntries = cap

    /// Diagnostics: cumulative number of over-cap recovery events (no eviction
    /// marker was left at/over the cap, so a transient over-cap was accepted
    /// rather than discarding live keys). A non-zero and climbing value is the
    /// observable signal that this store is under cap pressure.
    member _.OverCapRecoveryCount =
        System.Threading.Interlocked.Read(&overCapRecoveries[0])

// -----------------------------------------------------------------------------

/// Shared layout for the blob-backed idempotency store, so the
/// TTL sweep job (`IdempotencySweepJob`, compiled after the job substrate)
/// enumerates + parses the same blobs `BlobIdempotencyStore` writes without
/// duplicating the container / prefix / envelope-shape knowledge.
module BlobIdempotencyLayout =
    /// Reserved SDK-level container the idempotency envelopes live under.
    [<Literal>]
    let DefaultContainer = ToolUp.Platform.ScopeKey.PlatformContainer

    /// Blob-name prefix shared by every idempotency envelope (the `List`
    /// argument the sweep enumerates).
    [<Literal>]
    let BlobPrefix = "idem/"

    /// Read the absolute expiry (UTC ticks) from a stored envelope; `None`
    /// when the blob is corrupt / not an idempotency envelope — the sweep
    /// then leaves it untouched rather than crashing or mis-deleting.
    let tryReadExpiryTicks (bytes: byte[]) : int64 option =
        try
            use doc = System.Text.Json.JsonDocument.Parse(System.ReadOnlyMemory<byte>(bytes))
            Some(doc.RootElement.GetProperty("ExpiryUtcTicks").GetInt64())
        with _ ->
            None

// -----------------------------------------------------------------------------

module internal Idempotency =

    // Recognise BOTH the server-tier `IdempotentAttribute` and the
    // tier-shared `ToolUp.Platform.IdempotentAttribute` mirror (which
    // Fable-compiled Core API records carry); without it a Core API record's
    // `[<Idempotent>]` is invisible and idempotency never engages.

    // ── Severity assessment — the idempotency family ────────
    //
    // What a forgery buys:
    //
    //   * HONOURED, a foreign `IdempotentAttribute` arms response
    //     memoisation on a method the consumer never marked: a second
    //     call carrying the same `X-Idempotency-Key` is answered from the
    //     store instead of executing the handler. That is a CORRECTNESS
    //     defect (a stale answer where a fresh execution was intended),
    //     and it is bounded in the direction that matters most: the store
    //     scope is `subjectKey|methodName` (`deriveScope`), so a memoised
    //     response can only ever be replayed to the SAME subject — a
    //     forged marker cannot leak one caller's response to another. It
    //     is also doubly opt-in: dormant unless an `IIdempotencyStore` is
    //     composed (GP 13) AND the caller sends the header.
    //   * NOT honoured — the direction a bare identity fix introduces —
    //     is the sharper one: a consumer whose own `IdempotentAttribute`
    //     was picked up by accident loses replay protection silently, and
    //     the calls that carry an idempotency key are the ones where
    //     duplicate execution is expensive (payments, provisioning).
    //
    // Same shape as rate limiting, one notch lower in blast radius
    // because of the per-subject scope and the double opt-in. VERDICT:
    // fix — CLR identity + collision refusal. Severity MEDIUM
    // (correctness; no cross-subject exposure).
    let private markers =
        MarkerFamily [ typeof<IdempotentAttribute>; typeof<ToolUp.Platform.IdempotentAttribute> ]

    let private isIdempotentAttr (a: obj) : bool = markers.IsSanctioned(a.GetType())

    /// Whether one field carries `[<Idempotent>]`.
    let isIdempotent (field: System.Reflection.PropertyInfo) : bool =
        field.GetCustomAttributes(true) |> Array.exists isIdempotentAttr

    /// The idempotency marker collisions one field contributes.
    let foreignMarkersOf (field: System.Reflection.PropertyInfo) : (string * string * string) list =
        markers.CollisionsOf field
        |> List.map (fun rendered -> "idempotency", field.Name, rendered)

    /// Marker-name collisions on the API record, for the
    /// dispatcher's startup refusal.
    let foreignMarkers (apiType: Type) : (string * string * string) list =
        ApiRecord.fields apiType |> Array.toList |> List.collect foreignMarkersOf

    /// Cache the `[<Idempotent>]` classification per method at startup.
    let classify (apiType: Type) : Set<string> =
        ApiRecord.fields apiType
        |> Array.choose (fun pi -> if isIdempotent pi then Some pi.Name else None)
        |> Set.ofArray

    /// Derive the scope string for the idempotency store from the
    /// subject id + method name. Same shape as the rate-limit key root
    /// so the two seams partition the same way.
    let deriveScope (subjectKey: string) (methodName: string) : string = subjectKey + "|" + methodName

    /// SHA-256 (lowercase hex) of the request body. Stamped
    /// into the `MemoisedResponse` on first call; compared against the
    /// current call's body on replay. Mismatch surfaces as a
    /// `ErrorCategory.User` envelope + 409 Conflict so the caller
    /// notices the idempotency-key reuse instead of receiving the
    /// prior response.
    ///
    /// Accepts the body as `byte[]` rather than `string`.
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