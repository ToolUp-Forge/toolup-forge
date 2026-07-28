// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Threading
open ToolUp.Platform.Metrics

// ─── Phase 437 — resource-envelope seam adapters ──────────────────────
//
// The Server-tier half of Phase 437: the adapters that make a declared
// `ResourceEnvelope` (Core, `ResourceEnvelope.fs`) actually bind, at the
// three seams the SDK already owns a consultation point in.
//
//   * **Job concurrency** — the Phase 9b scheduler dispatches by handler
//     NAME (`InProcessJobScheduler.handlers`), so the natural gate is the
//     handler itself, decorated at REGISTRATION time by the composition
//     root that knows which component owns the declaration. A component at
//     its ceiling returns `TransientFailure`, which routes the attempt
//     into the scheduler's EXISTING retry/backoff machinery — the job is
//     deferred, never dropped (GP 6).
//   * **Request rate** — the Phase 56 middleware enforces
//     `ServerConfig.RateLimits` through `IRateLimitStore`. An envelope
//     with `MaxRequestsPerMinute` therefore PROJECTS into that list as a
//     `RouteLimit` over the owning component's route prefixes, keyed
//     `ByComposite "component:<id>"` so the count is component-wide rather
//     than per-caller. Nothing new runs: the shipped middleware, the
//     shipped store, and the shipped `RateLimitDecisionEvent` audit path
//     do the work.
//   * **Queue depth** — a bounded queue consults `admitQueueItem` before
//     enqueueing and gets a typed `EnvelopeAdmission` back. The caller
//     surfaces the back-pressure; nothing here drops anything.
//
// **No new runtime machinery.** There is no hosted service, no timer, no
// registry and no dependency added by this file. The concurrency gate is
// one `SemaphoreSlim` per BUDGETED handler — allocated only when a
// component declares `MaxJobConcurrency`, so an undeclared component's
// handler is passed through by REFERENCE, unwrapped, and its dispatch
// path is the pre-437 one instruction for instruction (GP 11 / GP 13).
// That is also what keeps fairness intact: an unconstrained component
// never touches a shared gate, so it cannot queue behind a budgeted one.
//
// **Every refusal is observable (GP 6).** `observeRefusal` is the single
// emission point — one `IMetricsSink` counter and one `Warn` log, worded
// through `ResourceEnvelope.describeRefusal` so two seams never describe
// the same refusal differently. A seam that discards an over-budget unit
// without calling it is using this module wrong.

/// Seam adapters that enforce a declared `ResourceEnvelope` at the job
/// scheduler, the rate-limit middleware, and any queue that consults
/// them. Pure resolution + one decorator; nothing here is started,
/// scheduled or hosted.
[<RequireQualifiedAccess>]
module ResourceEnvelopeEnforcement =

    /// The counter incremented once per refusal, tagged `component` +
    /// `dimension`. One name across all three seams, so a dashboard reads
    /// budget pressure without knowing which seam produced it.
    [<Literal>]
    let RefusalMetric = "toolup.resource_envelope.refused"

    /// The `RouteLimit.Key` a projected component budget uses: a fixed
    /// composite key per component, so every caller's requests count
    /// against the SAME budget. `ByIp` would give each caller its own
    /// allowance, which is a per-caller policy, not a component ceiling.
    let componentRateKey (componentId: ComponentId) : RateLimitKeyKind =
        ByComposite("component:" + ComponentId.value componentId)

    // ── observability (GP 6) ──────────────────────────────────────────

    /// Emit one refusal to the metric sink and the log. The SINGLE
    /// emission point for every seam — a refusal that does not pass
    /// through here is a silent drop.
    ///
    /// Both sinks are optional because two of the three seams run in
    /// contexts that may compose neither (a unit-tested handler, a
    /// queue in a companion). A refusal with no sinks composed is still
    /// RETURNED to the caller as a typed `EnvelopeRefusal` — the
    /// observability is the sink's job, the honesty is the type's.
    let observeRefusal (metrics: IMetricsSink option) (logger: ILogger option) (refusal: EnvelopeRefusal) : unit =
        metrics
        |> Option.iter (fun sink ->
            sink.Increment(
                RefusalMetric,
                Map.ofList [
                    "component", ComponentId.value refusal.RefusedComponent
                    "dimension", EnvelopeDimension.toWireString refusal.RefusedDimension
                ]
            ))

        logger
        |> Option.iter (fun log -> log.Warn("[Phase 437] " + ResourceEnvelope.describeRefusal refusal))

    /// Run the sinks over an admission and hand it straight back — the
    /// shape a call site uses when it wants to observe and then branch on
    /// the same value.
    let observe
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (admission: EnvelopeAdmission)
        : EnvelopeAdmission =
        match admission with
        | EnvelopeAdmitted -> ()
        | EnvelopeRefused refusal -> observeRefusal metrics logger refusal

        admission

    // ── 437.C-1 — job concurrency at the Phase 9b handler seam ────────

    /// An `IJobHandler` that admits at most `limit` concurrent
    /// executions for one component, deferring the rest.
    ///
    /// **Deferred, not dropped.** An over-budget attempt returns
    /// `TransientFailure`, which the scheduler already treats as
    /// retryable — so the job goes back through its declared
    /// `JobRetryPolicy` backoff rather than being lost or blocking a
    /// dispatch thread. Blocking would have been the other option and is
    /// deliberately not taken: `InProcessJobScheduler` fires each
    /// dispatch with `Async.Start`, so a blocking gate would accumulate
    /// parked work with no ceiling of its own.
    ///
    /// The gate is per-INSTANCE, matching the scheduler it decorates: the
    /// in-process scheduler is single-instance by construction (its
    /// multi-instance use is already refused by
    /// `JobSchedulerInstanceValidator`), so a process-local ceiling is
    /// the honest scope. A distributed scheduler companion enforces its
    /// own ceiling from the same declared number.
    type private BudgetedJobHandler
        (componentId: ComponentId, limit: int, inner: IJobHandler, metrics: IMetricsSink option, logger: ILogger option)
        =

        // `SemaphoreSlim(limit, limit)` with a zero-timeout Wait is a
        // non-blocking try-acquire: no thread ever parks here. A ceiling
        // of ZERO admits nothing, and `SemaphoreSlim` refuses a
        // `maxCount` of 0 outright — so that case is carried as `None`
        // rather than as a degenerate semaphore.
        let gate =
            if limit > 0 then
                Some(new SemaphoreSlim(limit, limit))
            else
                None

        let tryAcquire () =
            match gate with
            | Some semaphore -> semaphore.Wait 0
            | None -> false

        let release () =
            gate |> Option.iter (fun semaphore -> semaphore.Release() |> ignore)

        interface IJobHandler with
            member _.Execute(ctx: JobContext) : Async<JobResult> = async {
                if tryAcquire () then
                    try
                        return! inner.Execute ctx
                    finally
                        release ()
                else
                    let refusal = {
                        RefusedComponent = componentId
                        RefusedDimension = JobConcurrencyDimension
                        RefusedLimit = limit
                        RefusedObserved = limit
                    }

                    observeRefusal metrics logger refusal

                    return
                        TransientFailure(
                            ResourceEnvelope.describeRefusal refusal
                            + " — deferred to the next retry attempt, not dropped"
                        )
            }

    /// Decorate a job handler with its component's concurrency budget.
    ///
    /// Returns the handler UNCHANGED — the same reference, not a
    /// pass-through wrapper — when the component declares no
    /// `MaxJobConcurrency`. That is the whole zero-cost story: an
    /// unbudgeted handler is dispatched by the pre-437 code path with no
    /// extra allocation, no semaphore, and no branch (GP 11 / GP 13).
    let gateHandler
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (handler: IJobHandler)
        : IJobHandler =
        if Map.isEmpty envelopes then
            handler
        else
            match (ResourceEnvelope.resolve envelopes componentId).MaxJobConcurrency with
            | None -> handler
            | Some limit -> BudgetedJobHandler(componentId, limit, handler, metrics, logger) :> IJobHandler

    /// Apply `gateHandler` to a compose-time job declaration, keeping
    /// every other field verbatim. Returns the declaration UNCHANGED when
    /// the component declares no concurrency budget.
    let gateDeclaration
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (declaration: ScheduledJobDeclaration)
        : ScheduledJobDeclaration =
        let gated = gateHandler metrics logger envelopes componentId declaration.Handler

        if System.Object.ReferenceEquals(gated, declaration.Handler) then
            declaration
        else
            { declaration with Handler = gated }

    /// Apply `gateDeclaration` across one component's declarations. The
    /// input list is returned unchanged (same reference) when nothing was
    /// budgeted.
    let gateDeclarations
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (declarations: ScheduledJobDeclaration list)
        : ScheduledJobDeclaration list =
        if Map.isEmpty envelopes || List.isEmpty declarations then
            declarations
        else
            declarations |> List.map (gateDeclaration metrics logger envelopes componentId)

    // ── 437.C-2 — request rate at the Phase 56 middleware seam ────────

    /// Project one component's `MaxRequestsPerMinute` onto its route
    /// prefixes as `RouteLimit`s the SHIPPED `RateLimitMiddleware`
    /// enforces through the SHIPPED `IRateLimitStore` — the envelope
    /// becomes part of what the middleware already reads, rather than a
    /// second limiter beside it.
    ///
    /// **An existing policy wins.** `RateLimitMiddleware.matchPolicy`
    /// takes the FIRST prefix match, so a prefix already covered by an
    /// operator-declared `RouteLimit` is SKIPPED here rather than
    /// shadowed by an appended duplicate that could never fire. The
    /// operator's explicit policy stays authoritative and the deployment
    /// keeps the limit it configured; the envelope adds enforcement only
    /// where there was none. `shadowedPrefixes` reports which prefixes
    /// that applied to, so the omission is inspectable rather than
    /// assumed.
    ///
    /// A component with no `MaxRequestsPerMinute`, or with no route
    /// prefixes, projects to the empty list — so an envelope-free
    /// composition appends nothing and `ServerConfig.RateLimits` is
    /// byte-for-byte what it was (GP 11).
    let routeLimitsFor
        (existing: RouteLimit list)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (routePrefixes: string list)
        : RouteLimit list =
        if Map.isEmpty envelopes || List.isEmpty routePrefixes then
            []
        else
            match (ResourceEnvelope.resolve envelopes componentId).MaxRequestsPerMinute with
            | None -> []
            | Some threshold ->
                let alreadyCovered (prefix: string) =
                    existing
                    |> List.exists (fun limit ->
                        prefix.StartsWith(limit.Route, StringComparison.OrdinalIgnoreCase)
                        || limit.Route.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))

                routePrefixes
                |> List.filter (fun prefix -> not (String.IsNullOrWhiteSpace prefix))
                |> List.distinct
                |> List.filter (alreadyCovered >> not)
                |> List.map (fun prefix -> {
                    Route = prefix
                    Key = componentRateKey componentId
                    Window = PerMinute
                    Threshold = threshold
                    OnExceeded = Return429
                })

    /// The route prefixes `routeLimitsFor` declined to project because an
    /// existing policy already covers them — the inspectable half of the
    /// "an existing policy wins" rule.
    let shadowedPrefixes
        (existing: RouteLimit list)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (routePrefixes: string list)
        : string list =
        if Map.isEmpty envelopes || List.isEmpty routePrefixes then
            []
        else
            match (ResourceEnvelope.resolve envelopes componentId).MaxRequestsPerMinute with
            | None -> []
            | Some _ ->
                let projected =
                    routeLimitsFor existing envelopes componentId routePrefixes |> List.map _.Route

                routePrefixes
                |> List.filter (fun prefix -> not (String.IsNullOrWhiteSpace prefix))
                |> List.distinct
                |> List.filter (fun prefix -> not (List.contains prefix projected))

    // ── 437.C-3 — queue depth at a bounded-queue seam ─────────────────

    /// Ask whether one more item fits inside a component's declared
    /// `MaxQueueDepth`, given the depth observed BEFORE enqueueing.
    ///
    /// The answer is a typed `EnvelopeAdmission`, never a dropped item: a
    /// refusal carries the component, the ceiling and the observed depth
    /// so the caller can apply back-pressure, return 429/503, or block —
    /// whichever its overflow policy already says. A caller that
    /// discards the refusal and drops the item silently is using this
    /// wrong.
    ///
    /// A component with no `MaxQueueDepth` (or an empty signature)
    /// short-circuits to `EnvelopeAdmitted` without reading `depth`, so
    /// the queue's own capacity behaviour is untouched (GP 13).
    let admitQueueItem
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (depth: int)
        : EnvelopeAdmission =
        ResourceEnvelope.admitIn envelopes QueueDepthDimension depth componentId
        |> observe metrics logger

    /// Ask whether one more background job may start for a component,
    /// given the count already in flight — the same question
    /// `gateHandler` answers internally, exposed for a scheduler
    /// companion that owns its own in-flight accounting and wants to
    /// consult the budget directly rather than wrap handlers.
    let admitJob
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (inFlight: int)
        : EnvelopeAdmission =
        ResourceEnvelope.admitIn envelopes JobConcurrencyDimension inFlight componentId
        |> observe metrics logger

    /// Ask whether one more request may be admitted for a component,
    /// given the count already served in the current minute — the direct
    /// form for a caller that holds the count itself (an
    /// `IRateLimitStore` reader, a peer host) rather than going through
    /// the projected `RouteLimit`.
    let admitRequest
        (metrics: IMetricsSink option)
        (logger: ILogger option)
        (envelopes: EnvelopeSignature)
        (componentId: ComponentId)
        (servedThisMinute: int)
        : EnvelopeAdmission =
        ResourceEnvelope.admitIn envelopes RequestRateDimension servedThisMinute componentId
        |> observe metrics logger

    // ── composition-time reporting ────────────────────────────────────

    /// Whether this composition enforces anything at all — the gate a
    /// composition root reads before doing any envelope work (GP 13).
    let isActive (envelopes: EnvelopeSignature) : bool =
        ResourceEnvelope.anyConstrained envelopes

    /// A deterministic, one-line-per-component summary of what the
    /// composition budgets — the startup-log line for a deployment that
    /// opted in, and empty for one that did not.
    let describe (envelopes: EnvelopeSignature) : string list =
        ResourceEnvelope.all envelopes
        |> List.choose (fun (componentId, envelope) ->
            match ResourceEnvelope.declaredDimensions envelope with
            | [] -> None
            | dimensions ->
                let rendered =
                    dimensions
                    |> List.map (fun dimension ->
                        EnvelopeDimension.toWireString dimension
                        + " "
                        + string (ResourceEnvelope.limitFor dimension envelope |> Option.defaultValue 0))

                Some(ComponentId.value componentId + ": " + String.concat ", " rendered))