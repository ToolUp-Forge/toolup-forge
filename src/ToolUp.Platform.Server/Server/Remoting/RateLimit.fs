namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent

// =============================================================================
// Per-method rate-limit attribution
// =============================================================================
//
// API record fields can carry one or more `[<RateLimit>]` attributes; the
// dispatcher evaluates each budget per call against an `IRemotingRateLimitStore`,
// denies with a categorised `ErrorCategory.RateLimit` envelope + 429 +
// `Retry-After` header when any budget is exhausted.
//
// Multi-attribute semantics are AND: a method with both
// `[<RateLimit(10, RateLimitWindow.perSecond)>; RateLimit(1000, RateLimitWindow.perHour)>]`
// must pass BOTH budgets — short bursts and sustained traffic are
// independently capped.

/// Per-method rate-limit budget. `count` requests are
/// allowed per `windowSeconds` window per subject (per the IAuthContext's
/// `SubjectId`); when no auth context resolver is composed, the per-IP
/// remote address is used as the subject key fallback.
///
/// Attribute constructors are constrained to constant values, so window
/// is expressed as an integer seconds count; `RateLimitWindow.perSecond`
/// etc. provide named convenience constants.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RateLimitAttribute(count: int, windowSeconds: int) =
    inherit Attribute()
    member _.Count = count
    member _.WindowSeconds = windowSeconds
    /// `TimeSpan` view of the window for use in `IRemotingRateLimitStore.TryAcquire`.
    member this.Window = TimeSpan.FromSeconds(float windowSeconds)

/// Named window-size constants for the
/// `[<RateLimit(count, window)>]` attribute. Use as
/// `[<RateLimit(30, RateLimitWindow.perMinute)>]`.
module RateLimitWindow =
    [<Literal>]
    let perSecond = 1

    [<Literal>]
    let perMinute = 60

    [<Literal>]
    let perHour = 3600

    [<Literal>]
    let perDay = 86400

/// One `[<RateLimit>]` budget as an API record's contract carries it: `Count`
/// calls per `WindowSeconds` window per subject.
[<RequireQualifiedAccess>]
type RateBudget = { Count: int; WindowSeconds: int }

// -----------------------------------------------------------------------------

/// The one in-memory rate-limit store (Phase 1068): the default
/// `IRemotingRateLimitStore`, and the store behind the Phase 56
/// `ToolUp.Platform.Infra.InMemoryRateLimitStore` and the in-memory
/// share-token limiter, which adapt it. Sliding window over a per-key
/// timestamp queue. Suitable for single-instance deployments; distributed
/// deployments wire Redis or equivalent against the interfaces.
///
/// Admission is atomic per key: N callers released together against one
/// remaining slot admit exactly one. A non-positive budget admits every
/// call and records nothing — a misconfigured budget never becomes a
/// refusal of every caller.
///
/// Bounded by `maxBuckets` (default 100_000) through `BoundedFifo`: a new key
/// at the cap evicts the oldest bucket (by first insertion), so a deployment
/// hit by many distinct IPs or subject ids cannot OOM the host.
///
/// `clock` (default `DateTimeOffset.UtcNow`) exists so a window's edge is
/// assertable without racing the wall clock.
type InMemoryRateLimitStore(?maxBuckets: int, ?clock: unit -> DateTimeOffset) =
    let cap = defaultArg maxBuckets 100_000
    let now = defaultArg clock (fun () -> DateTimeOffset.UtcNow)
    let buckets = ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>>()

    let fifo =
        BoundedFifo(
            cap,
            (fun () -> buckets.Count),
            buckets.ContainsKey,
            (fun key -> buckets.TryRemove key |> ignore),
            BoundedFifoPolicy.oldestFirst ignore
        )

    /// Drop the timestamps that have left the window. The queue is ordered
    /// by enqueue time (admissions are serialised under its lock), so the
    /// walk stops at the first one still inside. Call under `lock queue`.
    let evict (queue: ConcurrentQueue<DateTimeOffset>) (cutoff: DateTimeOffset) =
        let mutable peeked = DateTimeOffset.MinValue
        let mutable evicting = true

        while evicting && queue.TryPeek(&peeked) do
            if peeked < cutoff then
                let mutable dequeued = DateTimeOffset.MinValue
                queue.TryDequeue(&dequeued) |> ignore
            else
                evicting <- false

    /// Admit one call against `count` per `window` for `key`.
    member _.Acquire(key: string, count: int, window: TimeSpan) : ToolUp.Platform.Admission =
        if count <= 0 then
            ToolUp.Platform.Admission.Admit 0
        else
            let at = now ()

            let queue =
                match buckets.TryGetValue key with
                | true, existing -> existing
                | false, _ ->
                    fifo.Admit(key, fun () -> buckets.GetOrAdd(key, fun _ -> ConcurrentQueue<DateTimeOffset>()))

            // Evict, check and admit under ONE per-key lock.
            // `ConcurrentQueue` makes each operation safe, not their
            // composition: `Count < count` then `Enqueue` is
            // check-then-act, so N callers arriving together at
            // `count - 1` all read "one slot left" before any of them
            // records its admission, and all pass. The lock is the queue
            // itself — one per key, so distinct subjects never contend —
            // and nothing inside it awaits.
            lock queue (fun () ->
                evict queue (at - window)

                if queue.Count < count then
                    queue.Enqueue at
                    ToolUp.Platform.Admission.Admit(count - queue.Count)
                else
                    // Budget exhausted (the queue is non-empty: count > 0).
                    // The next slot frees when the oldest in-window
                    // admission leaves the window.
                    let mutable oldest = DateTimeOffset.MinValue
                    queue.TryPeek(&oldest) |> ignore
                    let retryAfter = (oldest + window) - at
                    ToolUp.Platform.Admission.Deny(max retryAfter (TimeSpan.FromMilliseconds 1.0)))

    /// The admissions `key` holds inside the trailing `window`, without
    /// admitting one. A key the store has never seen holds none.
    member _.Admitted(key: string, window: TimeSpan) : int =
        match buckets.TryGetValue key with
        | true, queue ->
            lock queue (fun () ->
                evict queue (now () - window)
                queue.Count)
        | false, _ -> 0

    interface IRemotingRateLimitStore with
        member this.TryAcquire(key, count, window) = async { return this.Acquire(key, count, window) }

    /// Diagnostics: bucket count for telemetry / health checks.
    member _.BucketCount = buckets.Count
    /// Diagnostics: configured cap.
    member _.MaxBuckets = cap

// -----------------------------------------------------------------------------

module internal RateLimit =

    // ── Severity assessment — the rate-limit family ─────────
    //
    // What a forgery buys, in each direction:
    //
    //   * A foreign `RateLimitAttribute` HONOURED (the pre-727 behaviour)
    //     imposes a budget the consumer never declared, with semantics
    //     read off a foreign type's `Count` / `WindowSeconds` properties —
    //     which need not mean what this evaluator reads them as. It cannot
    //     RELAX anything: budgets compose with AND semantics (`evaluate`
    //     denies on the first exhausted budget), so an extra loose budget
    //     never widens a tight one. The damage is availability — 429s on a
    //     method the consumer believes ungated — not access.
    //   * A foreign attribute silently NOT honoured is the sharper
    //     direction, and it is what a bare identity fix would have
    //     introduced: a consumer whose own `RateLimitAttribute` was being
    //     picked up by accident loses the limiter with nothing anywhere
    //     saying so, and an abuse budget that quietly stops applying is
    //     exactly the failure a rate limiter exists to prevent.
    //
    // So the identity fix alone is NOT sufficient here — the startup
    // collision refusal is the load-bearing half, and the assessment is
    // what says so. VERDICT: fix — CLR identity + collision refusal.
    // Severity MEDIUM (availability / abuse-budget, both directions), one
    // rung below the audit family's PII exposure and above validation.
    let private markers =
        MarkerFamily [ typeof<RateLimitAttribute>; typeof<ToolUp.Platform.RateLimitAttribute> ]

    /// Normalise either sanctioned attribute family — the server-tier
    /// `ToolUp.Remoting.Server.RateLimitAttribute` or the tier-shared
    /// `ToolUp.Platform.RateLimitAttribute` mirror (which Fable-compiled
    /// Core API records carry) — into the server-tier budget shape the
    /// evaluator consumes. Family membership is CLR TYPE IDENTITY;
    /// the reflective `Count` / `WindowSeconds` read decodes the
    /// mirror only after identity has established that it IS the mirror.
    let private tryBudget (a: obj) : RateLimitAttribute option =
        let t = a.GetType()

        if not (markers.IsSanctioned t) then
            None
        else
            match a with
            | :? RateLimitAttribute as rl -> Some rl
            | _ ->
                match t.GetProperty "Count", t.GetProperty "WindowSeconds" with
                | null, _
                | _, null -> None
                | countProp, windowProp ->
                    match countProp.GetValue a, windowProp.GetValue a with
                    | (:? int as c), (:? int as w) -> Some(RateLimitAttribute(c, w))
                    | _ -> None

    /// One field's budgets, in declaration order.
    let budgetsOf (field: System.Reflection.PropertyInfo) : RateBudget list =
        field.GetCustomAttributes(true)
        |> Array.choose tryBudget
        |> Array.map (fun rl -> {
            RateBudget.Count = rl.Count
            RateBudget.WindowSeconds = rl.WindowSeconds
        })
        |> Array.toList

    /// The evaluator's view of a contract budget.
    let ofBudget (budget: RateBudget) : RateLimitAttribute =
        RateLimitAttribute(budget.Count, budget.WindowSeconds)

    /// The rate-limit marker collisions one field contributes.
    let foreignMarkersOf (field: System.Reflection.PropertyInfo) : (string * string * string) list =
        markers.CollisionsOf field
        |> List.map (fun rendered -> "rate limiting", field.Name, rendered)

    /// Marker-name collisions on the API record, for the
    /// dispatcher's startup refusal.
    let foreignMarkers (apiType: Type) : (string * string * string) list =
        ApiRecord.fields apiType |> Array.toList |> List.collect foreignMarkersOf

    /// Cache `[<RateLimit>]` attributes per API record field at startup.
    let classify (apiType: Type) : Map<string, RateLimitAttribute list> =
        ApiRecord.fields apiType
        |> Array.map (fun pi -> pi.Name, budgetsOf pi |> List.map ofBudget)
        |> Map.ofArray

    /// Evaluate every budget for a method against the store, return the
    /// first denial (if any). All budgets must pass for the call to
    /// proceed (AND semantics).
    let evaluate
        (store: IRemotingRateLimitStore)
        (subjectKey: string)
        (methodName: string)
        (budgets: RateLimitAttribute list)
        : Async<ToolUp.Platform.Admission> =
        let rec charge (left: int) (remaining: RateLimitAttribute list) : Async<ToolUp.Platform.Admission> = async {
            match remaining with
            | [] -> return ToolUp.Platform.Admission.Admit left
            | budget :: rest ->
                let key = sprintf "%s|%s|%ds" subjectKey methodName budget.WindowSeconds

                match! store.TryAcquire(key, budget.Count, budget.Window) with
                | ToolUp.Platform.Admission.Admit n -> return! charge (min left n) rest
                | denied -> return denied
        }

        charge Int32.MaxValue budgets

    /// Derive a stable subject key for the rate-limit bucket. Uses the
    /// resolved auth context's `SubjectId` when available; otherwise
    /// falls back to a per-remote-IP key (`ip:<addr>`) so anonymous
    /// callers can't collapse into a single global bucket — a hostile
    /// IP would otherwise be able to exhaust the budget for every
    /// legitimate anonymous caller. `unknown` is used only when no IP
    /// is resolvable (rare; e.g. unit tests that bypass the connection
    /// info).
    let deriveSubjectKey (authContext: IAuthContext option) (remoteIpAddress: string option) : string =
        match authContext with
        | Some ctx -> "subject:" + ctx.SubjectId
        | None ->
            match remoteIpAddress with
            | Some ip when not (System.String.IsNullOrWhiteSpace ip) -> "ip:" + ip
            | _ -> "anonymous-unknown"