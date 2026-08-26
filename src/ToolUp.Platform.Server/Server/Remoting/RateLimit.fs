namespace ToolUp.Remoting.Server

open System
open System.Collections.Concurrent

// =============================================================================
// Phase 69g — per-method rate-limit attribution
// =============================================================================
//
// API record fields can carry one or more `[<RateLimit>]` attributes; the
// dispatcher evaluates each budget per call against an `IRateLimitStore`,
// denies with a categorised `ErrorCategory.RateLimit` envelope + 429 +
// `Retry-After` header when any budget is exhausted.
//
// Multi-attribute semantics are AND: a method with both
// `[<RateLimit(10, RateLimitWindow.perSecond)>; RateLimit(1000, RateLimitWindow.perHour)>]`
// must pass BOTH budgets — short bursts and sustained traffic are
// independently capped.

/// Phase 69g — per-method rate-limit budget. `count` requests are
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
    /// `TimeSpan` view of the window for use in `IRateLimitStore.TryAcquire`.
    member this.Window = TimeSpan.FromSeconds(float windowSeconds)

/// Phase 69g — named window-size constants for the
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

// -----------------------------------------------------------------------------

/// Phase 69g — default in-memory implementation of `IRateLimitStore`.
/// Sliding-window counter via a per-key timestamp queue. Suitable for
/// single-instance deployments; distributed deployments wire Redis or
/// equivalent against the `IRateLimitStore` contract.
///
/// Bounded by `maxBuckets` (default 100_000). Once the cap is reached,
/// the oldest bucket (by first-insertion time) is evicted on next
/// `TryAcquire`. Cardinality-bounded so a deployment hit by many
/// distinct IPs (or subject ids) can't OOM the host.
type InMemoryRateLimitStore(?maxBuckets: int) =
    let cap = defaultArg maxBuckets 100_000
    let buckets = ConcurrentDictionary<string, ConcurrentQueue<DateTimeOffset>>()
    let order = ConcurrentQueue<string>()

    /// Opportunistically drain stale heads from `order` — keys that were
    /// already removed from `buckets` (e.g. by a prior eviction whose
    /// victim got re-added later). Keeps `order.Count` close to
    /// `buckets.Count` so subsequent eviction work doesn't scan
    /// through ghost entries.
    let compactStaleHeads () =
        let mutable peeked = Unchecked.defaultof<string>
        let mutable keepCompacting = true

        while keepCompacting && order.TryPeek(&peeked) do
            if buckets.ContainsKey peeked then
                keepCompacting <- false
            else
                let mutable _ignored = Unchecked.defaultof<string>
                order.TryDequeue(&_ignored) |> ignore

    let evictOldestIfFull () =
        compactStaleHeads ()

        while buckets.Count >= cap do
            let mutable victim = Unchecked.defaultof<string>

            if order.TryDequeue(&victim) then
                buckets.TryRemove victim |> ignore
            else if buckets.Count >= cap then
                buckets.Clear()

    interface IRateLimitStore with
        member _.TryAcquire(key, count, window) = async {
            let now = DateTimeOffset.UtcNow
            let cutoff = now - window
            let mutable createdNew = false

            let queue =
                buckets.GetOrAdd(
                    key,
                    fun _ ->
                        createdNew <- true
                        ConcurrentQueue<DateTimeOffset>()
                )

            if createdNew then
                evictOldestIfFull ()
                order.Enqueue key

            // Evict expired timestamps from the front. The queue is
            // approximately ordered by enqueue time so we can stop at
            // the first non-expired entry.
            let mutable peeked = DateTimeOffset.MinValue
            let mutable evicting = true

            while evicting && queue.TryPeek(&peeked) do
                if peeked < cutoff then
                    let mutable dequeued = DateTimeOffset.MinValue
                    queue.TryDequeue(&dequeued) |> ignore
                else
                    evicting <- false

            if queue.Count < count then
                queue.Enqueue now
                return RateLimitAllowed
            else
                // Budget exhausted. RetryAfter is the time until the
                // oldest in-window entry expires (the earliest moment
                // a new acquisition becomes possible).
                let mutable oldest = DateTimeOffset.MinValue

                if queue.TryPeek(&oldest) then
                    let retryAfter = (oldest + window) - now

                    let safe =
                        if retryAfter < TimeSpan.Zero then
                            TimeSpan.FromMilliseconds 1.0
                        else
                            retryAfter

                    return RateLimitDenied safe
                else
                    // Lost a race; treat as allowed and re-enqueue.
                    queue.Enqueue now
                    return RateLimitAllowed
        }

    /// Diagnostics: bucket count for telemetry / health checks.
    member _.BucketCount = buckets.Count
    /// Diagnostics: configured cap.
    member _.MaxBuckets = cap

// -----------------------------------------------------------------------------

module internal RateLimit =

    // Phase 69d.tail parity — reflect over both public AND non-public
    // record types so internal / private API records arm the rate-limit
    // classifier exactly like public ones. A bare `FSharpType.IsRecord`
    // reports false for a non-public record and would silently skip
    // budget enforcement (the same fail-open hole 69d closed for auth).
    let private reflectionFlags =
        System.Reflection.BindingFlags.Public
        ||| System.Reflection.BindingFlags.NonPublic

    // ── Phase 727 severity assessment — the rate-limit family ─────────
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
    /// evaluator consumes. Family membership is CLR TYPE IDENTITY (Phase
    /// 727); the reflective `Count` / `WindowSeconds` read decodes the
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

    /// Phase 727 — marker-name collisions on the API record, for the
    /// dispatcher's startup refusal.
    let foreignMarkers (apiType: Type) : (string * string * string) list =
        markers.Collisions(apiType, reflectionFlags)
        |> List.map (fun (field, rendered) -> "rate limiting", field, rendered)

    /// Cache `[<RateLimit>]` attributes per API record field at startup.
    let classify (apiType: Type) : Map<string, RateLimitAttribute list> =
        if not (Microsoft.FSharp.Reflection.FSharpType.IsRecord(apiType, reflectionFlags)) then
            Map.empty
        else
            let fields =
                Microsoft.FSharp.Reflection.FSharpType.GetRecordFields(apiType, reflectionFlags)

            fields
            |> Array.map (fun pi ->
                let attrs = pi.GetCustomAttributes(true) |> Array.choose tryBudget |> Array.toList

                pi.Name, attrs)
            |> Map.ofArray

    /// Evaluate every budget for a method against the store, return the
    /// first denial (if any). All budgets must pass for the call to
    /// proceed (AND semantics).
    let evaluate
        (store: IRateLimitStore)
        (subjectKey: string)
        (methodName: string)
        (budgets: RateLimitAttribute list)
        : Async<RateLimitDecision> =
        async {
            let mutable decision = RateLimitAllowed
            let mutable budgetsRemaining = budgets

            while decision = RateLimitAllowed && not (List.isEmpty budgetsRemaining) do
                let budget = List.head budgetsRemaining
                budgetsRemaining <- List.tail budgetsRemaining
                let key = sprintf "%s|%s|%ds" subjectKey methodName budget.WindowSeconds
                let! d = store.TryAcquire(key, budget.Count, budget.Window)
                decision <- d

            return decision
        }

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