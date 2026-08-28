// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.Metrics

// ─── Phase 472 — IEdgeCache: the CDN / edge invalidation seam ─────────
//
// A deployment serving bytes at scale puts a CDN in front of the origin.
// Once it does, the origin's own caches stop being the whole story: a
// republished page or a replaced media rendition is still being served
// from an edge node until that node's TTL runs out, and nothing in the
// SDK could tell the edge otherwise. This seam is that missing call.
//
// **It is deliberately NOT a read-through cache.** The CDN caches; the
// SDK only invalidates. There is no `Get` / `Set` here and there should
// not be one: adding a read path would make the SDK a second, weaker
// implementation of the thing the CDN already does well, and would put
// a network hop on the serve path — which is precisely what an edge
// exists to remove. The whole interface is three purge verbs.
//
// **Composition (GP 11 / GP 13).** Nothing composes an `IEdgeCache` by
// default. A deployment that composes none has no edge fan-out at all:
// the call sites hold an `IEdgeCache option` and skip the work when it
// is `None`, so an unconfigured deployment does not allocate, does not
// schedule, and behaves byte-for-byte as it did before this phase.
// `NoopEdgeCache` exists for the deployment that would rather compose a
// declared no-op than an absence (and for tests) — it too is free: every
// call returns one shared, pre-built `Async` value.
//
// **Never blocking (GP 7).** Purging is a network call to a third party.
// It must never sit on the request path: a CDN outage would then turn a
// successful publish into a failed one. `EdgeCache.purgeDetached` is the
// call shape every in-tree caller uses — it starts the purge on the
// thread pool, retries per an `EdgePurgeRetry` record, and reports a
// terminal failure to the audit logger. A purge that fails is a stale
// edge object, which is a correctness problem for the *reader*, not for
// the writer whose publish already succeeded.
//
// ─── The six portability rules (GP 12) ────────────────────────────────
//
// 1. *Identity by value* — a purge names `string` paths / prefixes /
//    tags. No live handle, no vendor invalidation object.
// 2. *Async at every boundary* — all three verbs return
//    `Async<Result<unit, EdgePurgeError>>`. Failure is data, not an
//    exception, because every implementation of this seam is a network
//    call and a caller must be able to decide what a failure means.
// 3. *Retry + supervision as data* — there is no `OnFailure` callback
//    anywhere in the interface. Retry / backoff live in the
//    `EdgePurgeRetry` record the detached-purge helper takes, so the
//    policy is inspectable and portable rather than closed over.
// 4. *Stateless between invocations* — each call carries everything it
//    needs. An implementation may hold a client and credentials, but
//    never per-call continuity, so a purge worker can be recycled
//    between calls.
// 5. *No cross-shard ordering promises* — purges are unordered with
//    respect to one another. A CDN applies them across many POPs
//    independently; correctness must never depend on purge A landing
//    before purge B. Where order matters, purge the wider prefix.
// 6. *Precision at the lower bound* — an implementation DECLARES how
//    fast its purge propagates via `Propagation`. Nothing in this seam
//    promises an object is gone from every edge when the `Async`
//    completes, because no CDN offers that, and a seam that implied it
//    would make every honest implementation a liar.

/// Why a purge did not happen. Failure-as-data (rule 2): a purge is a
/// call to someone else's network, so a caller — or an audit trail —
/// must be able to tell a rejected request from an unreachable one.
type EdgePurgeError =
    /// The purge could not be delivered (DNS, TLS, timeout, 5xx).
    /// Retryable in principle.
    | PurgeTransportFailure of detail: string
    /// The edge accepted the request and refused it (bad credentials,
    /// unknown distribution, a path outside the configured origin, a
    /// rate limit). Retrying the identical request will fail identically.
    | PurgeRejected of detail: string
    /// This implementation does not offer the verb that was called —
    /// e.g. an edge with no tag-based invalidation asked to `PurgeTags`.
    /// Distinct from a rejection: the request was well-formed and the
    /// capability is simply absent, so the caller should widen to a verb
    /// the implementation does support rather than retry.
    | PurgeNotSupported of verb: string

/// How quickly an implementation's purge reaches every edge. Rule 6 —
/// the precision contract is declared, never assumed. A caller that
/// needs a stronger guarantee than the composed implementation declares
/// has to change the implementation, not hope.
type EdgePurgePropagation =
    /// The purge is visible everywhere by the time the `Async`
    /// completes. Only an in-process or single-node edge can honestly
    /// claim this.
    | PurgeImmediate
    /// Eventually consistent with a vendor-documented upper bound.
    | PurgeEventualWithin of TimeSpan
    /// Eventually consistent with no bound the implementation is willing
    /// to promise. The honest default for most CDNs.
    | PurgeEventualUnbounded

/// Retry policy for a detached purge. Rule 3 — expressed as data so it
/// is inspectable and portable, never as a caller-supplied callback.
type EdgePurgeRetry = {
    /// Total attempts including the first. `1` means no retry. A
    /// non-positive value is read as `1` rather than as "never try".
    Attempts: int
    /// Delay before the second attempt. Each subsequent delay doubles.
    InitialBackoff: TimeSpan
}

module EdgePurgeRetry =
    /// Two attempts, half a second apart. Deliberately small: a detached
    /// purge that retries for a long time holds a thread-pool
    /// continuation and a set of paths alive for no reader's benefit —
    /// the edge object expires on its own TTL regardless, so a purge is
    /// a latency optimisation, not a durability guarantee.
    let defaults: EdgePurgeRetry = {
        Attempts = 2
        InitialBackoff = TimeSpan.FromMilliseconds 500.0
    }

    /// The effective attempt count — the configured value when positive,
    /// `1` otherwise. A hand-built record that omits it cannot mean
    /// "never attempt the purge at all".
    let effectiveAttempts (retry: EdgePurgeRetry) = max 1 retry.Attempts

/// Invalidate objects held by a CDN / edge cache in front of this
/// origin. Purge-only by design — see the header note: the CDN caches,
/// the SDK only invalidates.
type IEdgeCache =
    /// Stable name for diagnostics and audit lines (e.g. `"noop"`,
    /// `"http-purge"`). Not an identity the SDK dispatches on.
    abstract Name: string

    /// This implementation's propagation contract (rule 6).
    abstract Propagation: EdgePurgePropagation

    /// Purge specific origin-relative paths (`"/blog/hello"`,
    /// `"/api/media/hls/abc/index.m3u8"`). The most precise verb and the
    /// one every implementation must support.
    abstract PurgePaths: paths: string list -> Async<Result<unit, EdgePurgeError>>

    /// Purge every object under an origin-relative prefix
    /// (`"/api/media/hls/abc/"`). The verb to reach for when the exact
    /// object set is not knowable — an HLS rendition is an arbitrary
    /// number of segment files, and enumerating them to purge them would
    /// mean listing blob storage on a path that must not block.
    abstract PurgePrefix: prefix: string -> Async<Result<unit, EdgePurgeError>>

    /// Purge by cache tag / surrogate key, for edges that support them.
    /// An implementation whose edge does not returns
    /// `Error (PurgeNotSupported "PurgeTags")` — never a silent success,
    /// which would read as "the tagged objects are gone" when they are
    /// not.
    abstract PurgeTags: tags: string list -> Async<Result<unit, EdgePurgeError>>

/// The declared no-op (GP 13). Accepts every purge and does nothing,
/// which is the truthful answer for a deployment with no CDN in front of
/// it: there is no edge copy, so there is nothing to invalidate.
///
/// Every verb returns the SAME pre-built `Async` value, so a call
/// allocates nothing at all — the claim the contract pack pins by
/// reference equality rather than by assertion.
type NoopEdgeCache() =

    static let ok: Async<Result<unit, EdgePurgeError>> = async.Return(Ok())

    /// The shared success value every verb returns. Exposed so the
    /// contract pack can pin the allocation-free claim structurally.
    static member internal Ok = ok

    interface IEdgeCache with
        member _.Name = "noop"
        member _.Propagation = PurgeImmediate
        member _.PurgePaths(_) = ok
        member _.PurgePrefix(_) = ok
        member _.PurgeTags(_) = ok

module NoopEdgeCache =
    /// One shared instance — the no-op holds no state, so a deployment
    /// (or a test) that wants a declared no-op never needs a second.
    let instance: IEdgeCache = NoopEdgeCache() :> IEdgeCache

    /// Construct the declared no-op edge cache. Returns the shared
    /// instance; the function exists to match the `create ()` shape every
    /// other default impl in the SDK offers.
    let create () : IEdgeCache = instance

// ─── Cache-Control declaration (the surface-level knob) ───────────────

/// What a surface tells a CDN about one class of response. Rendered into
/// a `Cache-Control` header by `EdgeCacheHeader.render`.
///
/// `EdgeCacheUnset` is the default everywhere and is NOT the same as
/// `EdgePrivate`: it emits **no header at all**, which is exactly what
/// every media route did before this phase, so an upgrading deployment
/// is byte-for-byte unchanged until it declares something (GP 11).
type EdgeCacheability =
    /// Emit no `Cache-Control` header. The pre-472 behaviour.
    | EdgeCacheUnset
    /// `no-store, no-cache, must-revalidate` — never held anywhere, by
    /// an edge or by a browser. The posture a credential-bearing
    /// response must have.
    | EdgeNoStore
    /// `private, max-age=N` — a browser may hold it, a shared cache must
    /// not. The posture for a per-viewer response.
    | EdgePrivate of maxAgeSeconds: int
    /// `public, max-age=N, s-maxage=M` — a shared cache (the CDN) may
    /// hold it for `s-maxage`, a browser for `max-age`.
    | EdgePublic of maxAgeSeconds: int * sharedMaxAgeSeconds: int

module EdgeCacheHeader =
    /// Render a declared cacheability as a `Cache-Control` value.
    /// `EdgeCacheUnset` yields `None` — the caller emits no header,
    /// rather than emitting an empty one.
    ///
    /// Negative ages are clamped to zero rather than refused: a
    /// hand-built options record with a nonsense age should produce a
    /// conservative header, not a startup failure on a serving path.
    let render (cacheability: EdgeCacheability) : string option =
        let age (n: int) = max 0 n

        match cacheability with
        | EdgeCacheUnset -> None
        | EdgeNoStore -> Some "no-store, no-cache, must-revalidate"
        | EdgePrivate maxAge -> Some(sprintf "private, max-age=%d" (age maxAge))
        | EdgePublic(maxAge, sharedMaxAge) ->
            Some(sprintf "public, max-age=%d, s-maxage=%d" (age maxAge) (age sharedMaxAge))

// ─── Phase 740 — purge outcome telemetry ──────────────────────────────
//
// A purge is fire-and-forget by design (GP 7), and 472 left its terminal
// failure as one `Warn` line naming the edge. That is enough to diagnose
// a purge you already suspect and nothing at all to NOTICE one you do
// not: a mis-credentialed or rate-limited adapter fails identically on
// every publish, forever, while pages and media serve stale from the
// edge and no series moves. These three counters are that missing
// signal — attempted / succeeded / failed, at the one choke point every
// in-tree purge passes through, tagged by the edge's own name and (on a
// failure) by a class an operator can act on.
//
// **Off is free, and off is the default.** Nothing here allocates or
// emits unless the composed edge cache carries a live `IMetricsSink`
// (see `IEdgePurgeMetered` below). The two short-circuits 472 put ahead
// of the scheduling — no edge composed, or the declared no-op — are
// untouched and still run FIRST, so a no-op deployment reaches no
// telemetry code at all and the allocation-free claim it pins is
// unaffected.

/// Why a purge failed, in the vocabulary an operator acts on. The
/// classes are deliberately about the REMEDY rather than about the
/// wire: `auth` means rotate or re-scope the credential, `rate-limit`
/// means purge less or ask for more headroom, `transport` means the
/// endpoint could not be reached and may well succeed next time, and
/// `unsupported` means this edge does not offer the verb at all and no
/// amount of retrying or re-crediting will change that.
module EdgePurgeMetrics =

    /// Counter — one increment per detached purge that got as far as
    /// being scheduled. The denominator for the other two.
    [<Literal>]
    let Attempted = "toolup.edge.purge.attempted"

    /// Counter — purges whose terminal outcome was `Ok`.
    [<Literal>]
    let Succeeded = "toolup.edge.purge.succeeded"

    /// Counter — purges whose terminal outcome was an error, after the
    /// retry policy was exhausted. The series this phase exists for.
    [<Literal>]
    let Failed = "toolup.edge.purge.failed"

    /// Tag key carrying `IEdgeCache.Name` — the same token the `Warn`
    /// line names, so a dashboard and a log line agree on which edge.
    /// Bounded by construction: a deployment composes one edge, or a
    /// small handful.
    [<Literal>]
    let EdgeTagKey = "edge"

    /// Tag key carrying the failure class. Present on `Failed` only —
    /// there is no class for a success, and inventing one (`"none"`)
    /// would put a constant in an allowlist for no reader's benefit.
    [<Literal>]
    let ClassTagKey = "class"

    /// The purge could not be delivered — DNS, TLS, timeout, 5xx.
    [<Literal>]
    let ClassTransport = "transport"

    /// The edge refused the request on credentials (401 / 403 / 407).
    [<Literal>]
    let ClassAuth = "auth"

    /// The edge refused the request on quota (429).
    [<Literal>]
    let ClassRateLimit = "rate-limit"

    /// The edge does not offer the verb that was called. Not a failure
    /// of the network or the credential; a fact about the adapter's
    /// declared capability, which is why it is not folded into `other`.
    [<Literal>]
    let ClassUnsupported = "unsupported"

    /// A rejection this SDK could not refine any further. The honest
    /// answer for a third-party adapter whose rejection detail says
    /// nothing a machine can read.
    [<Literal>]
    let ClassOther = "other"

    /// The token an adapter puts between its endpoint and the HTTP
    /// status it received, when it formats a rejection detail as
    /// `"<endpoint> returned <status> <reason>"`.
    ///
    /// This is a stated CONVENTION, not a dependency: `Platform.Server`
    /// takes no reference on any edge sub-companion (GP 1), and the
    /// classifier below degrades to `other` — never to a wrong class —
    /// for a detail it cannot read. The in-tree HTTP adapter formats
    /// its detail this way, which is what makes `auth` and `rate-limit`
    /// distinguishable at all: both arrive as `PurgeRejected`, because
    /// both are 4xx, so the typed case alone cannot separate them.
    [<Literal>]
    let StatusMarker = " returned "

    /// Read an HTTP status out of a rejection detail written to the
    /// convention above. Strict on purpose: exactly three digits
    /// immediately after the marker, not followed by a fourth, so a
    /// detail carrying some other number cannot be misread as a status.
    let private httpStatusIn (detail: string) : int option =
        if String.IsNullOrEmpty detail then
            None
        else
            let marker = detail.IndexOf(StatusMarker, StringComparison.Ordinal)

            if marker < 0 then
                None
            else
                let start = marker + StatusMarker.Length

                if start + 3 > detail.Length then
                    None
                elif start + 3 < detail.Length && Char.IsDigit detail[start + 3] then
                    None
                else
                    let candidate = detail.Substring(start, 3)

                    if candidate |> Seq.forall Char.IsDigit then
                        Some(int candidate)
                    else
                        None

    /// The failure class for one terminal purge error.
    let classify (error: EdgePurgeError) : string =
        match error with
        | PurgeTransportFailure _ -> ClassTransport
        | PurgeNotSupported _ -> ClassUnsupported
        | PurgeRejected detail ->
            match httpStatusIn detail with
            | Some 401
            | Some 403
            | Some 407 -> ClassAuth
            | Some 429 -> ClassRateLimit
            | _ -> ClassOther

    /// Every class the classifier can produce. Exposed so the metric's
    /// tag allowlist and the classifier cannot drift apart unnoticed —
    /// the test pack asserts the two against each other rather than
    /// trusting this comment.
    let classes: string list = [ ClassTransport; ClassAuth; ClassRateLimit; ClassUnsupported; ClassOther ]

    /// Declarations, spliced into `StandardMetrics.registrations` so a
    /// deployment with a metrics endpoint has all three series declared
    /// without composing anything — an unregistered series is dropped
    /// by the sink, so declaring them centrally is what makes the
    /// emissions below reachable at all.
    let registrations: MetricRegistration list = [
        {
            Module = None
            Definition = {
                Name = Attempted
                Kind = Counter
                Description = "Edge-cache purges scheduled (tags: edge)"
                Unit = "1"
                Tags = [ EdgeTagKey ]
            }
        }
        {
            Module = None
            Definition = {
                Name = Succeeded
                Kind = Counter
                Description = "Edge-cache purges that succeeded (tags: edge)"
                Unit = "1"
                Tags = [ EdgeTagKey ]
            }
        }
        {
            Module = None
            Definition = {
                Name = Failed
                Kind = Counter
                Description =
                    "Edge-cache purges that failed after retries "
                    + "(tags: edge + class=transport|auth|rate-limit|unsupported|other). "
                    + "A non-zero rate means the edge is serving bytes this origin has replaced."
                Unit = "1"
                Tags = [ EdgeTagKey; ClassTagKey ]
            }
        }
    ]

/// An edge cache that carries the sink its purge outcomes are counted
/// through. Implemented by `MeteredEdgeCache` (the wrap a compose root
/// applies), and available to any out-of-tree adapter that would rather
/// carry its own sink than be wrapped.
///
/// **Why the sink rides the EDGE rather than the call.** The choke point
/// already holds the edge and nothing else: `purgeDetachedWith` is
/// reached from a publish, a delete and a slug purge, none of which has
/// a request, a response or a container in hand. Threading a sink down
/// to each of them would have widened two public constructors and three
/// composition records to carry a value only this one line reads. The
/// edge is the thing being metered, so it is the thing that carries the
/// meter.
type IEdgePurgeMetered =
    /// The live sink. Never a no-op — `EdgeCache.withMetrics` refuses to
    /// wrap with one, so an instance implementing this interface is a
    /// promise that emission is worth doing.
    abstract PurgeMetrics: IMetricsSink

/// What a purge about to be scheduled will emit. `EdgePurgeUnmetered`
/// is a nullary case — an F#-cached singleton — and every emit function
/// below matches it and returns, so the off path allocates nothing at
/// all (GP 13).
///
/// Resolved per purge from the composed edge, NOT cached in a
/// process-wide `mutable`: a cached static would make "counters appear
/// only when a sink is composed" structurally untestable in a shared
/// test process, where two tests with different compositions would see
/// each other's cache. The resolution is one type test on an object the
/// caller already holds.
type EdgePurgeTelemetry =
    | EdgePurgeUnmetered
    | EdgePurgeMetered of sink: IMetricsSink

module EdgePurgeTelemetry =

    /// The telemetry a sink implies. `NoOpMetricsSink` — which is what
    /// the SDK registers when a deployment composed no metrics endpoint
    /// — reads as OFF rather than as a live sink that happens to
    /// discard, so the gate is discriminating rather than always-on.
    let forSink (sink: IMetricsSink) : EdgePurgeTelemetry =
        match box sink with
        | null -> EdgePurgeUnmetered
        | :? NoOpMetricsSink -> EdgePurgeUnmetered
        | _ -> EdgePurgeMetered sink

    /// The telemetry a composed edge carries, or `EdgePurgeUnmetered`
    /// for an edge that carries none.
    let forEdge (edge: IEdgeCache) : EdgePurgeTelemetry =
        match box edge with
        | :? IEdgePurgeMetered as metered -> forSink metered.PurgeMetrics
        | _ -> EdgePurgeUnmetered

    /// Increment one counter, best-effort. A sink that throws must not
    /// take the detached purge — or the thread-pool work item running
    /// it — down with it; the purge's own outcome is the thing that
    /// matters and it has already been decided by the time we are here.
    let private increment (sink: IMetricsSink) (name: string) (tags: Map<string, string>) =
        try
            sink.Increment(name, tags)
        with _ ->
            ()

    /// One purge was scheduled.
    let attempted (telemetry: EdgePurgeTelemetry) (edge: IEdgeCache) : unit =
        match telemetry with
        | EdgePurgeUnmetered -> ()
        | EdgePurgeMetered sink ->
            increment sink EdgePurgeMetrics.Attempted (Map.ofList [ EdgePurgeMetrics.EdgeTagKey, edge.Name ])

    /// That purge reached a terminal `Ok`.
    let succeeded (telemetry: EdgePurgeTelemetry) (edge: IEdgeCache) : unit =
        match telemetry with
        | EdgePurgeUnmetered -> ()
        | EdgePurgeMetered sink ->
            increment sink EdgePurgeMetrics.Succeeded (Map.ofList [ EdgePurgeMetrics.EdgeTagKey, edge.Name ])

    /// That purge reached a terminal error of the given class.
    let failed (telemetry: EdgePurgeTelemetry) (edge: IEdgeCache) (failureClass: string) : unit =
        match telemetry with
        | EdgePurgeUnmetered -> ()
        | EdgePurgeMetered sink ->
            increment
                sink
                EdgePurgeMetrics.Failed
                (Map.ofList [
                    EdgePurgeMetrics.EdgeTagKey, edge.Name
                    EdgePurgeMetrics.ClassTagKey, failureClass
                ])

/// An `IEdgeCache` that forwards every verb to `inner` unchanged and
/// carries the sink its purge outcomes are counted through. Applied by
/// the compose roots via `EdgeCache.withMetrics`; a deployment may also
/// apply it itself.
///
/// It is a pure carrier: it observes nothing and decides nothing. The
/// counting happens at `EdgeCache.purgeDetachedWith`, where the TERMINAL
/// outcome is known — a decorator counting each verb call would count
/// every retry as its own attempt and could never see the difference
/// between a purge that recovered on its second try and one that did
/// not.
type MeteredEdgeCache(inner: IEdgeCache, metrics: IMetricsSink) =

    /// The wrapped edge, so a caller holding the wrapper can still
    /// reach the implementation it decorates.
    member _.Inner = inner

    interface IEdgePurgeMetered with
        member _.PurgeMetrics = metrics

    interface IEdgeCache with
        member _.Name = inner.Name
        member _.Propagation = inner.Propagation
        member _.PurgePaths(paths) = inner.PurgePaths paths
        member _.PurgePrefix(prefix) = inner.PurgePrefix prefix
        member _.PurgeTags(tags) = inner.PurgeTags tags

// ─── Detached purge (the only shape in-tree callers use) ─────────────

module EdgeCache =

    /// Is this a no-op edge cache? Used to skip scheduling entirely, so
    /// a deployment that composed the declared no-op pays exactly what a
    /// deployment that composed nothing pays.
    let isNoop (edge: IEdgeCache) : bool =
        match box edge with
        | :? NoopEdgeCache -> true
        | _ -> false

    /// Phase 740 — carry a metrics sink on an edge cache, so its purge
    /// outcomes are counted at the choke point.
    ///
    /// Three cases return `edge` UNCHANGED rather than a wrapper, and
    /// each is load-bearing rather than tidy:
    ///
    ///   * a no-op sink — the SDK registers one when a deployment
    ///     composed no metrics endpoint, and wrapping for it would put
    ///     an object and two type tests on the purge path of every
    ///     deployment that measures nothing;
    ///   * the declared no-op edge — 472 pins that it is
    ///     indistinguishable from an absent edge, and a wrapper would
    ///     defeat `isNoop` and start scheduling work for a deployment
    ///     that declared it wanted none;
    ///   * an edge that already carries a sink — wrapping twice would
    ///     be harmless but would leave `Inner` lying about what it
    ///     decorates.
    let withMetrics (metrics: IMetricsSink) (edge: IEdgeCache) : IEdgeCache =
        match EdgePurgeTelemetry.forSink metrics with
        | EdgePurgeUnmetered -> edge
        | EdgePurgeMetered sink ->
            if isNoop edge then
                edge
            else
                match box edge with
                | :? IEdgePurgeMetered -> edge
                | _ -> MeteredEdgeCache(edge, sink) :> IEdgeCache

    /// `withMetrics` over the sink registered in a container. The shape
    /// a compose root uses: the container does not exist when the edge
    /// is declared, so the wrap happens where the provider does.
    /// Returns `edge` unchanged when nothing live is registered.
    let withMetricsFrom (services: IServiceProvider) (edge: IEdgeCache) : IEdgeCache =
        match box services with
        | null -> edge
        | _ ->
            match services.GetService typeof<IMetricsSink> with
            | :? IMetricsSink as sink -> withMetrics sink edge
            | _ -> edge

    let private describe (error: EdgePurgeError) =
        match error with
        | PurgeTransportFailure d -> sprintf "transport failure: %s" d
        | PurgeRejected d -> sprintf "rejected: %s" d
        | PurgeNotSupported verb -> sprintf "%s is not supported by this edge" verb

    /// Run one purge with the retry policy, returning the terminal
    /// outcome. Exposed for callers that genuinely want to await a purge
    /// (a CLI, an ops endpoint); the serve / publish paths use
    /// `purgeDetached` below instead.
    ///
    /// `PurgeNotSupported` is terminal on the first attempt — the
    /// capability will not appear on a retry, and burning the backoff
    /// window on it would delay nothing but the audit line.
    let purgeWithRetry
        (retry: EdgePurgeRetry)
        (purge: unit -> Async<Result<unit, EdgePurgeError>>)
        : Async<Result<unit, EdgePurgeError>> =
        async {
            let attempts = EdgePurgeRetry.effectiveAttempts retry
            let mutable remaining = attempts
            let mutable delay = retry.InitialBackoff
            let mutable outcome = Error(PurgeTransportFailure "no attempt was made")
            let mutable go = true

            while go do
                let! result = async {
                    try
                        return! purge ()
                    with ex ->
                        return Error(PurgeTransportFailure ex.Message)
                }

                outcome <- result
                remaining <- remaining - 1

                match result with
                | Ok() -> go <- false
                | Error(PurgeNotSupported _) -> go <- false
                | Error _ ->
                    if remaining <= 0 then
                        go <- false
                    else
                        if delay > TimeSpan.Zero then
                            do! Async.Sleep(int delay.TotalMilliseconds)

                        delay <- delay + delay

            return outcome
        }

    /// Fire-and-forget a purge, auditing a terminal failure (GP 7).
    ///
    /// Returns immediately. The purge runs on the thread pool, so a slow
    /// or unreachable CDN cannot extend the request that triggered it —
    /// which is the whole point: a publish that succeeded must not be
    /// reported as failed because someone else's API was down.
    ///
    /// A `None` edge cache, a no-op edge cache, and an empty purge set
    /// all short-circuit before anything is scheduled, so an
    /// unconfigured deployment pays nothing (GP 13).
    let purgeDetachedWith
        (retry: EdgePurgeRetry)
        (logger: ILogger option)
        (edge: IEdgeCache option)
        (what: string)
        (purge: IEdgeCache -> Async<Result<unit, EdgePurgeError>>)
        : unit =
        match edge with
        | None -> ()
        | Some e when isNoop e -> ()
        | Some e ->
            // Phase 740 — resolved once per purge, from the edge the
            // caller already holds. `EdgePurgeUnmetered` is a cached
            // singleton, so an unmetered deployment pays one type test
            // and no allocation; the resolution sits INSIDE the detached
            // body so even that test is off the calling thread.
            Async.Start(
                async {
                    let telemetry = EdgePurgeTelemetry.forEdge e
                    EdgePurgeTelemetry.attempted telemetry e

                    let! outcome =
                        purgeWithRetry retry (fun () ->
                            try
                                purge e
                            with ex ->
                                async.Return(Error(PurgeTransportFailure ex.Message)))

                    match outcome with
                    | Ok() -> EdgePurgeTelemetry.succeeded telemetry e
                    | Error err ->
                        // The class the metric is tagged with is the
                        // class the log line names — one derivation, so
                        // a dashboard and the line an operator greps
                        // for cannot disagree about the same failure.
                        let failureClass = EdgePurgeMetrics.classify err
                        EdgePurgeTelemetry.failed telemetry e failureClass

                        match logger with
                        | Some log ->
                            log.Warn(
                                sprintf
                                    "[EdgeCache:%s] purge of %s failed [class=%s] — the edge may serve stale bytes until its own TTL expires (%s)"
                                    e.Name
                                    what
                                    failureClass
                                    (describe err)
                            )
                        | None -> ()
                }
            )

    /// `purgeDetachedWith` under `EdgePurgeRetry.defaults`.
    let purgeDetached
        (logger: ILogger option)
        (edge: IEdgeCache option)
        (what: string)
        (purge: IEdgeCache -> Async<Result<unit, EdgePurgeError>>)
        : unit =
        purgeDetachedWith EdgePurgeRetry.defaults logger edge what purge

    /// Detach a path purge. No-ops on an empty path list — an empty
    /// purge is not an error, and scheduling one would only produce an
    /// audit line about nothing.
    let purgePathsDetached (logger: ILogger option) (edge: IEdgeCache option) (paths: string list) : unit =
        match paths with
        | [] -> ()
        | _ -> purgeDetached logger edge (sprintf "%d path(s)" (List.length paths)) (fun e -> e.PurgePaths paths)

    /// Detach a prefix purge.
    let purgePrefixDetached (logger: ILogger option) (edge: IEdgeCache option) (prefix: string) : unit =
        if not (String.IsNullOrWhiteSpace prefix) then
            purgeDetached logger edge (sprintf "prefix %s" prefix) (fun e -> e.PurgePrefix prefix)