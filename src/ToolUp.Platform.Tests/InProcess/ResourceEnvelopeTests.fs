module ToolUp.Platform.Tests.InProcess.ResourceEnvelopeTests

open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 437 — per-component resource envelopes ─────────────────────
//
// Covers the acceptance shape:
//   * a component with `MaxJobConcurrency = 2` never runs three handlers
//     at once — asserted against an ACTUAL concurrency counter driven by
//     three parallel dispatches, not by inspecting the budget;
//   * an unconstrained composition is byte-for-byte what it was: the
//     handler comes back as the SAME REFERENCE (never a pass-through
//     wrapper), the projected `RouteLimit` list is empty, and every
//     admission short-circuits (GP 11 / GP 13);
//   * an over-budget refusal is OBSERVABLE — the typed `EnvelopeRefusal`
//     reaches the metric sink and carries the component, dimension,
//     limit and observed level (GP 6);
//   * the Phase 290 rollup's pressure dimension is ABSENT for an
//     undeclared component and present only for declared dimensions.

// ── stubs ─────────────────────────────────────────────────────────────

/// A metric sink that records `Increment` calls, so a refusal's
/// observability is asserted rather than assumed.
type private RecordingMetricsSink() =
    let increments = ResizeArray<string * Map<string, string>>()

    member _.Increments = List.ofSeq increments

    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()

        member _.Increment(name, tags) =
            lock increments (fun () -> increments.Add(name, tags))

        member _.SetGauge(_name, _value, _tags) = ()

/// A job handler that records the PEAK number of simultaneous executions
/// it ever saw, holding each execution until `release` is set. The gate's
/// whole claim is about that peak, so the hold is an explicit event
/// rather than a sleep — the test never races a timer.
type private ConcurrencyProbeHandler(release: ManualResetEventSlim) =
    let mutable current = 0
    let mutable peak = 0
    let mutable started = 0

    member _.Peak = peak
    member _.Started = started

    interface IJobHandler with
        member _.Execute(_ctx: JobContext) : Async<JobResult> = async {
            let now = Interlocked.Increment &current
            Interlocked.Increment &started |> ignore

            // Non-atomic max is fine: the assertion is `peak <= limit`,
            // and any miss can only UNDER-report, never manufacture a
            // violation that did not happen.
            if now > peak then
                peak <- now

            release.Wait 10_000 |> ignore
            Interlocked.Decrement &current |> ignore
            return Success
        }

let private jobCtx () : JobContext = {
    JobId = System.Guid.NewGuid()
    ScopeId = "_platform"
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "admin")
    Attempt = 1
    Trigger = Manual
    TriggerSource = ScheduledManually "admin"
    ScheduledAt = System.DateTime.UtcNow
    RunningAt = System.DateTime.UtcNow
    Payload = "{}"
    DeadLetterDestination = None
}

let private budgeted = ComponentId.create "module:reports"
let private unbudgeted = ComponentId.create "module:quiet"

let private concurrencySignature (limit: int) : EnvelopeSignature =
    ResourceEnvelope.emptySignature
    |> ResourceEnvelope.declare
        budgeted
        (ResourceEnvelope.unconstrained |> ResourceEnvelope.withMaxJobConcurrency limit)

let tests =
    testList "ResourceEnvelope" [

        // ── 437.A — the identity is unconstrained ────────────────────
        testCase "an undeclared component resolves to the unconstrained envelope"
        <| fun _ ->
            let resolved = ResourceEnvelope.resolve (concurrencySignature 2) unbudgeted

            Expect.equal resolved ResourceEnvelope.unconstrained "an absent id resolves to the identity"
            Expect.isTrue (ResourceEnvelope.isUnconstrained resolved) "the identity constrains nothing"

            Expect.isEmpty
                (ResourceEnvelope.declaredDimensions resolved)
                "an unconstrained envelope declares no dimension"

        testCase "an unconstrained dimension admits without reading the observed level"
        <| fun _ ->
            let admission =
                ResourceEnvelope.admitIn ResourceEnvelope.emptySignature JobConcurrencyDimension 9_999 budgeted

            Expect.equal admission EnvelopeAdmitted "an empty signature admits everything"

        testCase "a declared ceiling admits up to it and refuses at it"
        <| fun _ ->
            let signature = concurrencySignature 2

            Expect.equal
                (ResourceEnvelope.admitIn signature JobConcurrencyDimension 1 budgeted)
                EnvelopeAdmitted
                "one in flight leaves room under a ceiling of two"

            match ResourceEnvelope.admitIn signature JobConcurrencyDimension 2 budgeted with
            | EnvelopeAdmitted -> failtest "two in flight should exhaust a ceiling of two"
            | EnvelopeRefused refusal ->
                Expect.equal refusal.RefusedComponent budgeted "the refusal names the component"
                Expect.equal refusal.RefusedDimension JobConcurrencyDimension "the refusal names the dimension"
                Expect.equal refusal.RefusedLimit 2 "the refusal carries the declared ceiling"
                Expect.equal refusal.RefusedObserved 2 "the refusal carries the observed level"

        testCase "another component's dimension is unaffected by a declared one"
        <| fun _ ->
            let signature = concurrencySignature 2

            Expect.equal
                (ResourceEnvelope.admitIn signature RequestRateDimension 10_000 budgeted)
                EnvelopeAdmitted
                "a dimension the component does not declare stays unconstrained"

            Expect.equal
                (ResourceEnvelope.admitIn signature JobConcurrencyDimension 10_000 unbudgeted)
                EnvelopeAdmitted
                "an undeclared component is never constrained by another's budget"

        // ── 437.C-1 — the concurrency gate actually gates ─────────────
        testCase "a component with MaxJobConcurrency = 2 never runs 3 concurrent handlers"
        <| fun _ ->
            use release = new ManualResetEventSlim(false)
            let probe = ConcurrencyProbeHandler release
            let sink = RecordingMetricsSink()

            let gated =
                ResourceEnvelopeEnforcement.gateHandler
                    (Some(sink :> IMetricsSink))
                    None
                    (concurrencySignature 2)
                    budgeted
                    (probe :> IJobHandler)

            let inFlight =
                [ 1..3 ]
                |> List.map (fun _ -> gated.Execute(jobCtx ()))
                |> Async.Parallel
                |> Async.StartAsTask

            // Wait until all three dispatches have been ACCOUNTED FOR —
            // either inside the handler body or already refused — then
            // let the holders finish. No timer race: the assertion runs
            // against a settled state, not a hoped-for one.
            let deadline = System.DateTime.UtcNow.AddSeconds 10.0

            while probe.Started + sink.Increments.Length < 3 && System.DateTime.UtcNow < deadline do
                Thread.Sleep 5

            release.Set()
            let results = inFlight.Result |> List.ofArray

            Expect.isLessThanOrEqual probe.Peak 2 "never more than two handler bodies ran at once"
            Expect.equal probe.Started 2 "the third dispatch never entered the handler body"

            let refusals =
                results
                |> List.filter (function
                    | TransientFailure _ -> true
                    | _ -> false)

            Expect.equal refusals.Length 1 "exactly one dispatch was deferred"

            // GP 6 — the refusal is observable, not silent.
            Expect.equal sink.Increments.Length 1 "the refusal emitted exactly one metric"

            let name, tags = sink.Increments.Head

            Expect.equal name ResourceEnvelopeEnforcement.RefusalMetric "the refusal counter is the shared name"
            Expect.equal tags.["component"] (ComponentId.value budgeted) "the metric is tagged with the component"
            Expect.equal tags.["dimension"] "job-concurrency" "the metric is tagged with the dimension"

        testCase "an over-budget job is deferred, never dropped"
        <| fun _ ->
            use release = new ManualResetEventSlim(true)
            let probe = ConcurrencyProbeHandler release

            let gated =
                ResourceEnvelopeEnforcement.gateHandler
                    None
                    None
                    (concurrencySignature 0)
                    budgeted
                    (probe :> IJobHandler)

            match gated.Execute(jobCtx ()) |> Async.RunSynchronously with
            | TransientFailure detail ->
                Expect.stringContains detail "job-concurrency" "the failure names the dimension it hit"
                Expect.stringContains detail "not dropped" "the failure says the work is retried, not lost"
            | other -> failtestf "a zero-concurrency budget should defer, got %A" other

            Expect.equal probe.Started 0 "the handler body never ran"

        // ── GP 11 / GP 13 — the unconstrained path is untouched ──────
        testCase "an unbudgeted handler is returned by reference, not wrapped"
        <| fun _ ->
            use release = new ManualResetEventSlim(true)
            let probe = ConcurrencyProbeHandler release :> IJobHandler

            let viaEmpty =
                ResourceEnvelopeEnforcement.gateHandler None None ResourceEnvelope.emptySignature budgeted probe

            let viaOtherComponent =
                ResourceEnvelopeEnforcement.gateHandler None None (concurrencySignature 2) unbudgeted probe

            Expect.isTrue
                (System.Object.ReferenceEquals(viaEmpty, probe))
                "an empty signature returns the very same handler — no wrapper allocated"

            Expect.isTrue
                (System.Object.ReferenceEquals(viaOtherComponent, probe))
                "an undeclared component returns the very same handler"

        testCase "an unbudgeted composition adds no rate-limit policy"
        <| fun _ ->
            Expect.isEmpty
                (ResourceEnvelopeEnforcement.routeLimitsFor [] ResourceEnvelope.emptySignature budgeted [
                    "/api/reports"
                ])
                "an empty signature projects no RouteLimit"

            Expect.isEmpty
                (ResourceEnvelopeEnforcement.routeLimitsFor [] (concurrencySignature 2) budgeted [ "/api/reports" ])
                "a component declaring only job concurrency projects no RouteLimit"

            Expect.isFalse
                (ResourceEnvelopeEnforcement.isActive ResourceEnvelope.emptySignature)
                "an empty signature enforces nothing"

        // ── 437.C-2 — the rate projection lands in the shipped seam ───
        testCase "a declared request rate projects onto the component's route prefixes"
        <| fun _ ->
            let signature =
                ResourceEnvelope.emptySignature
                |> ResourceEnvelope.declare
                    budgeted
                    (ResourceEnvelope.unconstrained |> ResourceEnvelope.withMaxRequestsPerMinute 120)

            let limits =
                ResourceEnvelopeEnforcement.routeLimitsFor [] signature budgeted [ "/api/reports"; "/api/reports" ]

            Expect.equal limits.Length 1 "duplicate prefixes project once"
            let limit = limits.Head
            Expect.equal limit.Route "/api/reports" "the policy covers the component's prefix"
            Expect.equal limit.Threshold 120 "the threshold is the declared ceiling"
            Expect.equal limit.Window PerMinute "the window is the declared per-minute one"
            Expect.equal limit.OnExceeded Return429 "an over-budget request gets the typed 429, never a silent drop"

            Expect.equal
                limit.Key
                (ByComposite("component:" + ComponentId.value budgeted))
                "the key is component-wide, not per-caller"

        testCase "an existing route policy is not shadowed by the projection"
        <| fun _ ->
            let signature =
                ResourceEnvelope.emptySignature
                |> ResourceEnvelope.declare
                    budgeted
                    (ResourceEnvelope.unconstrained |> ResourceEnvelope.withMaxRequestsPerMinute 120)

            let existing = [ RouteLimit.perIpPerMinute "/api/reports" 10 ]

            Expect.isEmpty
                (ResourceEnvelopeEnforcement.routeLimitsFor existing signature budgeted [ "/api/reports" ])
                "a prefix the operator already governs is left alone"

            Expect.equal
                (ResourceEnvelopeEnforcement.shadowedPrefixes existing signature budgeted [ "/api/reports" ])
                [ "/api/reports" ]
                "the skipped prefix is reported, not silently omitted"

        // ── 437.C-3 — queue depth is typed back-pressure ──────────────
        testCase "queue depth refuses with a typed outcome, never a drop"
        <| fun _ ->
            let signature =
                ResourceEnvelope.emptySignature
                |> ResourceEnvelope.declare
                    budgeted
                    (ResourceEnvelope.unconstrained |> ResourceEnvelope.withMaxQueueDepth 4)

            let sink = RecordingMetricsSink()
            let observe = Some(sink :> IMetricsSink)

            Expect.equal
                (ResourceEnvelopeEnforcement.admitQueueItem observe None signature budgeted 3)
                EnvelopeAdmitted
                "a depth below the ceiling admits"

            match ResourceEnvelopeEnforcement.admitQueueItem observe None signature budgeted 4 with
            | EnvelopeAdmitted -> failtest "a depth at the ceiling should refuse"
            | EnvelopeRefused refusal ->
                Expect.equal refusal.RefusedDimension QueueDepthDimension "the refusal names the queue dimension"
                Expect.equal refusal.RefusedLimit 4 "the refusal carries the declared depth ceiling"

            Expect.equal sink.Increments.Length 1 "only the refusal emitted a metric"

            Expect.equal
                (ResourceEnvelopeEnforcement.admitQueueItem observe None ResourceEnvelope.emptySignature budgeted 9_999)
                EnvelopeAdmitted
                "an unbudgeted queue keeps its own capacity behaviour"

        // ── 437.D — the Phase 290 pressure dimension ─────────────────
        testCase "an undeclared component contributes no pressure dimension"
        <| fun _ ->
            let rollup =
                ComponentHealthRollup.empty
                |> ComponentHealthRollup.withPressure ResourceEnvelope.emptySignature (fun _ _ -> 5)

            Expect.equal rollup.PressureHealth ComponentHealthRollup.empty "the health rollup passes through untouched"
            Expect.isEmpty rollup.PressureByComponent "no envelope means no pressure entry at all"

        testCase "pressure is reported only for declared dimensions"
        <| fun _ ->
            let signature = concurrencySignature 4

            let rollup =
                ComponentHealthRollup.empty
                |> ComponentHealthRollup.withPressure signature (fun _ _ -> 3)

            let readings = rollup.PressureByComponent.[budgeted]

            Expect.equal readings.Length 1 "only the declared dimension is reported"

            Expect.equal
                readings.Head.PressureDimension
                JobConcurrencyDimension
                "the reported dimension is the declared one"

            Expect.equal readings.Head.PressureLimit 4 "the reading carries the declared ceiling"

            Expect.equal
                (ResourceEnvelope.utilisationPercent readings.Head)
                75
                "utilisation rounds down to the whole percent"

            Expect.equal
                (ComponentHealthRollup.underPressure 90 rollup)
                []
                "a component below the threshold is not reported as under pressure"

            Expect.equal
                (ComponentHealthRollup.underPressure 75 rollup).Length
                1
                "a component at the threshold is reported"

        // ── wire vocabulary is stable + closed ───────────────────────
        testCase "dimension tokens round-trip and reject unknown input"
        <| fun _ ->
            for dimension in EnvelopeDimension.all do
                Expect.equal
                    (EnvelopeDimension.ofWireString (EnvelopeDimension.toWireString dimension))
                    dimension
                    "a dimension token round-trips"

            Expect.throws
                (fun () -> EnvelopeDimension.ofWireString "cpu-shares" |> ignore)
                "an unknown dimension token raises rather than fabricating an enforcement point"

        testCase "a negative limit is rejected at declaration"
        <| fun _ ->
            Expect.throws
                (fun () ->
                    ResourceEnvelope.unconstrained
                    |> ResourceEnvelope.withMaxJobConcurrency -1
                    |> ignore)
                "a negative ceiling is a typo, not a budget"
    ]