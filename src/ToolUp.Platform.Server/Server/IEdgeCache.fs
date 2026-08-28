// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

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

// ─── Detached purge (the only shape in-tree callers use) ─────────────

module EdgeCache =

    /// Is this a no-op edge cache? Used to skip scheduling entirely, so
    /// a deployment that composed the declared no-op pays exactly what a
    /// deployment that composed nothing pays.
    let isNoop (edge: IEdgeCache) : bool =
        match box edge with
        | :? NoopEdgeCache -> true
        | _ -> false

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
            Async.Start(
                async {
                    let! outcome =
                        purgeWithRetry retry (fun () ->
                            try
                                purge e
                            with ex ->
                                async.Return(Error(PurgeTransportFailure ex.Message)))

                    match outcome, logger with
                    | Ok(), _ -> ()
                    | Error err, Some log ->
                        log.Warn(
                            sprintf
                                "[EdgeCache:%s] purge of %s failed — the edge may serve stale bytes until its own TTL expires (%s)"
                                e.Name
                                what
                                (describe err)
                        )
                    | Error _, None -> ()
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