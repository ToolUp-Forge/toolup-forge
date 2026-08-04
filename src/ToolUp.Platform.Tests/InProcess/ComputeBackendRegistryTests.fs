module ToolUp.Platform.Tests.InProcess.ComputeBackendRegistryTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Metrics

// ─── Phase 484 — compute-backend registry + routing ──────────────────
//
// Four claims, and this pack holds each to the Phase 478 standard rather
// than to "a test passed":
//
//   1. **Routing precedence is profile, then resource fit, then the
//      declared default.** Every precedence case is asserted by counting
//      what each backend WAS HANDED, not by reading which registration
//      `select` returned — a returned registration is equally consistent
//      with a router that selected correctly and then submitted somewhere
//      else. Each case is paired with a control that differs in exactly
//      the one input the step under test reads, and which lands on a
//      DIFFERENT backend. Delete the profile filter and the profile
//      control still passes while the profile case turns red; delete the
//      resource step and the same holds there. A precedence test whose
//      control lands on the same backend as the case proves nothing,
//      which is why the fixtures put the default SECOND in composition
//      order — otherwise "the default won" is indistinguishable from
//      "the first registration won".
//
//   2. **An infeasible spec is refused, and the payload never leaves.**
//      Asserted on the backends' submission counts, not on the returned
//      error: an error is exactly what a router that handed the work over
//      and then complained would also return, and that is the failure
//      being excluded. Paired with a floor-cleared control on the same
//      fleet, so "nothing was submitted" cannot be passing because the
//      fixture never submits anything.
//
//   3. **A handle returns to the backend that minted it.** Including the
//      case that makes the restamp load-bearing — a backend whose own
//      `Backend` label differs from its registered `Kind`. The mutation
//      check is explicit: the same handle carrying the raw label instead
//      of the kind IS refused, so the round-trip's green is attributable
//      to the restamp rather than to the two strings happening to agree.
//
//   4. **GP 13 — a single-dispatcher deployment is unchanged.** Asserted
//      structurally on the service collection (the registry, telemetry,
//      probes and panel are ABSENT, by count, not by inspection) and
//      behaviourally (a one-backend fleet answers identically to the
//      backend called directly).

// ─── Fixtures ────────────────────────────────────────────────────────

let private isolatedPosture = IsolationPosture.clauses "test-sandbox"

/// A backend that accepts everything and remembers exactly what it was
/// handed. The counts are the assertions that matter throughout this pack.
///
/// `label` is the dispatcher's own `Backend` string, deliberately separate
/// from the `Kind` it gets registered under so the restamp can be tested.
type private RecordingBackend(label: string, posture: IsolationPosture option, ?outcome: ExternalOutcome) =
    let submitted = ResizeArray<string * ExternalWorkSpec>()
    let polled = ResizeArray<ExternalHandle>()
    let cancelled = ResizeArray<ExternalHandle>()
    let mutable minted = 0

    member _.Submitted = List.ofSeq submitted
    member _.SubmitCount = submitted.Count
    member _.Polled = List.ofSeq polled
    member _.PollCount = polled.Count
    member _.Cancelled = List.ofSeq cancelled
    member _.CancelCount = cancelled.Count

    interface IExternalComputeDispatcher with
        member _.Backend = label

        member _.Submit(scopeId, spec) = async {
            submitted.Add((scopeId, spec))
            minted <- minted + 1

            return
                Ok {
                    HandleId = Guid.NewGuid()
                    // The backend stamps its OWN label, exactly as Phase
                    // 318 has it do. Whether that survives to the caller
                    // is what the restamp tests assert.
                    Backend = label
                    ScopeId = scopeId
                    NativeRef = sprintf "opaque://%s/%d" label minted
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(handle) = async {
            polled.Add handle
            return outcome |> Option.defaultValue (ExternalOutcome.Succeeded "blob://out")
        }

        member _.Cancel(handle) = async { cancelled.Add handle }

    interface IIsolatedComputeBackend with
        member _.IsolationPosture =
            posture |> Option.defaultValue IsolationPosture.standardOnly

/// A backend that refuses every submission with the supplied error — the
/// fixture behind the health-probe cases.
type private RefusingBackend(label: string, error: ExternalComputeError) =
    interface IExternalComputeDispatcher with
        member _.Backend = label
        member _.Submit(_scopeId, _spec) = async { return Error error }
        member _.Poll(_handle) = async { return ExternalOutcome.Failed error }
        member _.Cancel(_handle) = async { return () }

/// A dispatcher that does NOT implement `IIsolatedComputeBackend` at all —
/// the shape every pre-478 companion has. It must read as `standardOnly`.
type private UndeclaringBackend(label: string) =
    let submitted = ResizeArray<ExternalWorkSpec>()
    member _.SubmitCount = submitted.Count

    interface IExternalComputeDispatcher with
        member _.Backend = label

        member _.Submit(scopeId, spec) = async {
            submitted.Add spec

            return
                Ok {
                    HandleId = Guid.NewGuid()
                    Backend = label
                    ScopeId = scopeId
                    NativeRef = "opaque://undeclaring/1"
                    SubmittedAt = DateTime.UtcNow
                }
        }

        member _.Poll(_handle) = async { return ExternalOutcome.Pending }
        member _.Cancel(_handle) = async { return () }

/// Captures every emission so a metric can be asserted with its exact tag
/// map — a missing `backend` tag is the failure that makes a per-backend
/// metric useless, and it is invisible to an assertion on the name alone.
type private CapturingMetricsSink() =
    let increments = ResizeArray<string * Map<string, string>>()
    let gauges = ResizeArray<string * float * Map<string, string>>()

    member _.Increments = List.ofSeq increments
    member _.Gauges = List.ofSeq gauges

    member this.IncrementsOf(name: string) =
        this.Increments |> List.filter (fun (n, _) -> n = name) |> List.map snd

    member this.GaugesOf(name: string) =
        this.Gauges
        |> List.filter (fun (n, _, _) -> n = name)
        |> List.map (fun (_, v, t) -> v, t)

    interface IMetricsSink with
        member _.Record(name, value, tags) = gauges.Add((name, value, tags))
        member _.Increment(name, tags) = increments.Add((name, tags))
        member _.SetGauge(name, value, tags) = gauges.Add((name, value, tags))

let private router (registrations: ComputeBackendRegistration list) =
    let registry = ComputeBackendRegistry registrations
    let telemetry = ComputeFleetTelemetry()
    let metrics = CapturingMetricsSink()
    let dispatcher = RoutingComputeDispatcher(registry, telemetry, metrics)
    registry, telemetry, metrics, (dispatcher :> IExternalComputeDispatcher)

let private submit (dispatcher: IExternalComputeDispatcher) (spec: ExternalWorkSpec) =
    dispatcher.Submit("team-1", spec) |> Async.RunSynchronously

let private handleOrFail (result: Result<ExternalHandle, ExternalComputeError>) =
    match result with
    | Ok handle -> handle
    | Error e -> failtestf "expected the submission to be accepted, got %A" e

let private errorOrFail (result: Result<ExternalHandle, ExternalComputeError>) =
    match result with
    | Ok handle -> failtestf "expected a refusal, but the work was accepted as %A" handle
    | Error e -> e

let private isolatedSpec =
    ExternalWorkSpec.create "fit" "{}" |> ExternalWorkSpec.isolated

let private standardSpec = ExternalWorkSpec.create "fit" "{}"

let private withClasses (classes: string) (spec: ExternalWorkSpec) =
    spec
    |> ExternalWorkSpec.withHint ComputeBackendRouting.ResourceClassHint classes

// ─── 484.A — the registry + derived capability declaration ───────────

let registryTests =
    testList "Phase 484.A — ComputeBackendRegistry" [

        test "supported profiles are DERIVED from the Phase 478 posture, not separately declared" {
            let isolating =
                ComputeBackendRegistration.create "iso" (RecordingBackend("iso", Some isolatedPosture))

            let plain =
                ComputeBackendRegistration.create "plain" (RecordingBackend("plain", None))

            Expect.equal
                (ComputeBackendRegistration.supportedProfiles isolating)
                [ ExecutionProfile.Standard; ExecutionProfile.Isolated ]
                "a backend declaring all three clauses supports both profiles"

            Expect.equal
                (ComputeBackendRegistration.supportedProfiles plain)
                [ ExecutionProfile.Standard ]
                "a backend declaring standardOnly supports Standard alone — it cannot overclaim, because the registration has no field to overclaim WITH"
        }

        test "a dispatcher that never implements IIsolatedComputeBackend reads as standardOnly" {
            // The pre-478 companion shape. Claiming nothing must never be
            // read as claiming everything.
            let registration =
                ComputeBackendRegistration.create "legacy" (UndeclaringBackend "legacy")

            Expect.equal
                (ComputeBackendRegistration.supportedProfiles registration)
                [ ExecutionProfile.Standard ]
                "forgetting to declare is not the same as declaring"

            Expect.isFalse
                (ComputeBackendRegistration.honours ExecutionProfile.Isolated registration)
                "and it cannot honour Isolated"
        }

        test "a partial posture does not support Isolated — two of three clauses is not a weaker clean room" {
            let partial: IsolationPosture = {
                IsolationPosture.clauses "half-sandbox" with
                    NoEgress = false
            }

            let registration =
                ComputeBackendRegistration.create "partial" (RecordingBackend("partial", Some partial))

            Expect.equal
                (ComputeBackendRegistration.supportedProfiles registration)
                [ ExecutionProfile.Standard ]
                "a missing clause drops Isolated support entirely"

            // Control on the SAME fixture shape: all three clauses DO
            // support it, so the assertion above is about the missing
            // clause and not about the fixture being incapable.
            let full =
                ComputeBackendRegistration.create "full" (RecordingBackend("full", Some isolatedPosture))

            Expect.isTrue
                (ComputeBackendRegistration.honours ExecutionProfile.Isolated full)
                "control — the same fixture with all three clauses does support Isolated"
        }

        test "duplicate kind is rejected at compose, naming both registrants and the contested kind" {
            let attempt () =
                ComputeBackendRegistry [
                    ComputeBackendRegistration.create "shared" (RecordingBackend("first-pool", None))
                    ComputeBackendRegistration.create "shared" (RecordingBackend("second-pool", None))
                ]
                |> ignore

            let message =
                try
                    attempt ()
                    failtest "a duplicate kind must fail at compose, not at the first submission"
                with e ->
                    e.Message

            Expect.stringContains message "'shared'" "the contested kind is named"
            Expect.stringContains message "'first-pool'" "the first registrant is named"
            Expect.stringContains message "'second-pool'" "the second registrant is named"

            // Control: two DISTINCT kinds over the same dispatcher shape
            // construct fine, so the rejection is about the collision and
            // not about the fixture being unconstructable.
            ComputeBackendRegistry [
                ComputeBackendRegistration.create "a" (RecordingBackend("first-pool", None))
                ComputeBackendRegistration.create "b" (RecordingBackend("second-pool", None))
            ]
            |> ignore
        }

        test "a second declared default is rejected at compose, naming both" {
            let attempt () =
                ComputeBackendRegistry [
                    ComputeBackendRegistration.create "a" (RecordingBackend("a", None))
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "b" (RecordingBackend("b", None))
                    |> ComputeBackendRegistration.asDefault
                ]
                |> ignore

            let message =
                try
                    attempt ()
                    failtest "two defaults is an unresolved choice, not a fallback chain"
                with e ->
                    e.Message

            Expect.stringContains message "'a'" "the first default is named"
            Expect.stringContains message "'b'" "the second default is named"
        }

        test "an empty fleet is rejected at compose" {
            let message =
                try
                    ComputeBackendRegistry [] |> ignore
                    failtest "a router over an empty fleet can only refuse"
                with e ->
                    e.Message

            Expect.stringContains
                message
                "NoExternalCompute"
                "the diagnostic points at the honest alternative rather than just complaining"
        }

        test "the registry exposes composition order, lookup by kind, and the declared default" {
            let a = ComputeBackendRegistration.create "a" (RecordingBackend("a", None))

            let b =
                ComputeBackendRegistration.create "b" (RecordingBackend("b", None))
                |> ComputeBackendRegistration.asDefault

            let registry = ComputeBackendRegistry [ a; b ]

            Expect.equal registry.Kinds [ "a"; "b" ] "composition order is preserved — it is the routing tie-break"
            Expect.equal (registry.TryFind "b" |> Option.map _.Kind) (Some "b") "lookup by kind resolves"
            Expect.isNone (registry.TryFind "absent") "an unregistered kind is None, not an exception"
            Expect.equal (registry.Default |> Option.map _.Kind) (Some "b") "the declared default is found"
        }

        test "describe names profiles, resource classes and envelopes for the refusal text" {
            let registration =
                ComputeBackendRegistration.create "gpu-queue" (RecordingBackend("gpu-queue", Some isolatedPosture))
                |> ComputeBackendRegistration.withResourceClasses [ "gpu"; "high-memory" ]
                |> ComputeBackendRegistration.withEnvelopeVersions [ "v2" ]

            let described = ComputeBackendRegistration.describe registration

            Expect.stringContains described "gpu-queue" "the kind"
            Expect.stringContains described "isolated" "the derived profile"
            Expect.stringContains described "high-memory" "the declared classes"
            Expect.stringContains described "v2" "the declared envelope versions"
        }
    ]

// ─── 484.B — routing precedence ──────────────────────────────────────

let routingTests =
    testList "Phase 484.B — routing precedence (profile > hints > default)" [

        test "an Isolated spec never reaches a non-isolating backend, even when it is the default and fits the hints" {
            // The fleet is stacked AGAINST the correct answer: the
            // non-isolating backend is the declared default AND declares
            // the requested class, so every step after the profile filter
            // would pick it.
            let leaky = RecordingBackend("leaky", None)
            let sealed' = RecordingBackend("sealed", Some isolatedPosture)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "leaky" leaky
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "sealed" sealed'
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                ]

            let handle = isolatedSpec |> withClasses "gpu" |> submit dispatcher |> handleOrFail

            // The claim is about what the backend SAW, not about what the
            // caller was told.
            Expect.equal leaky.SubmitCount 0 "the non-isolating backend was never handed the work"
            Expect.equal sealed'.SubmitCount 1 "the isolating backend got it"
            Expect.equal handle.Backend "sealed" "and the handle is stamped with the backend that ran it"
        }

        test "control — the identical fleet and hints DO route a Standard spec to the non-isolating default" {
            // The paired control. Only `Profile` differs from the case
            // above, and it lands on the OTHER backend — so deleting the
            // profile filter turns that case red while this stays green.
            let leaky = RecordingBackend("leaky", None)
            let sealed' = RecordingBackend("sealed", Some isolatedPosture)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "leaky" leaky
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "sealed" sealed'
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                ]

            standardSpec |> withClasses "gpu" |> submit dispatcher |> handleOrFail |> ignore

            Expect.equal leaky.SubmitCount 1 "a Standard spec reaches the default, which is where the fleet points it"
            Expect.equal sealed'.SubmitCount 0 "and not the isolating backend"
        }

        test "a hard resource class beats the declared default" {
            let generalist = RecordingBackend("generalist", None)
            let specialist = RecordingBackend("specialist", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "generalist" generalist
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "specialist" specialist
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu"; "gpu" ]
                ]

            standardSpec |> withClasses "gpu" |> submit dispatcher |> handleOrFail |> ignore

            Expect.equal specialist.SubmitCount 1 "the class requirement selected the only backend that declares it"
            Expect.equal generalist.SubmitCount 0 "the declared default did not win — hints outrank it"
        }

        test "control — with no class requirement, the SAME fleet routes to the declared default" {
            // The default sits SECOND in composition order deliberately:
            // if it were first, "the default won" would be
            // indistinguishable from "the first registration won".
            let specialist = RecordingBackend("specialist", None)
            let generalist = RecordingBackend("generalist", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "specialist" specialist
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu"; "gpu" ]
                    ComputeBackendRegistration.create "generalist" generalist
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                ]

            submit dispatcher standardSpec |> handleOrFail |> ignore

            Expect.equal generalist.SubmitCount 1 "with nothing to discriminate on, the declared default takes the work"

            Expect.equal
                specialist.SubmitCount
                0
                "even though it is first in composition order — so this is the default winning, not the order"
        }

        test "an advisory hint prefers a backend without being able to refuse one" {
            // No reserved `resource-class` key at all — just an ordinary
            // hint whose key happens to name a declared class.
            let cpuPool = RecordingBackend("cpu-pool", None)
            let gpuPool = RecordingBackend("gpu-pool", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "cpu-pool" cpuPool
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "gpu-pool" gpuPool
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                ]

            standardSpec
            |> ExternalWorkSpec.withHint "gpu" "1"
            |> submit dispatcher
            |> handleOrFail
            |> ignore

            Expect.equal gpuPool.SubmitCount 1 "the advisory hint preferred the backend declaring that class"
            Expect.equal cpuPool.SubmitCount 0 "over the declared default"
        }

        test
            "an advisory hint no backend declares is NOT a refusal — Phase 318 has a backend ignore what it does not understand" {
            let cpuPool = RecordingBackend("cpu-pool", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "cpu-pool" cpuPool
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                ]

            standardSpec
            |> ExternalWorkSpec.withHint "priority" "high"
            |> ExternalWorkSpec.withHint "queue" "batch"
            |> submit dispatcher
            |> handleOrFail
            |> ignore

            Expect.equal
                cpuPool.SubmitCount
                1
                "a non-resource hint must not refuse the work — that is what makes the reserved key necessary"
        }

        test "routing is deterministic — the same registry and spec select the same backend every time" {
            let first = RecordingBackend("first", None)
            let second = RecordingBackend("second", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "first" first
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    ComputeBackendRegistration.create "second" second
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                ]

            // Two equally-eligible backends, no declared default: the
            // tie-break is composition order, and it must not drift.
            for _ in 1..5 do
                submit dispatcher standardSpec |> handleOrFail |> ignore

            Expect.equal first.SubmitCount 5 "composition order is the stable tie-break"
            Expect.equal second.SubmitCount 0 "no round-robin, no ordering by hash"
        }

        test "select is pure — it chooses without touching any backend" {
            let backend = RecordingBackend("only", None)

            let registry =
                ComputeBackendRegistry [ ComputeBackendRegistration.create "only" backend ]

            match ComputeBackendRouting.select registry standardSpec with
            | Ok chosen -> Expect.equal chosen.Kind "only" "the decision resolves"
            | Error e -> failtestf "expected a selection, got %A" e

            Expect.equal backend.SubmitCount 0 "deciding where work goes must not submit it"
            Expect.equal backend.PollCount 0 "nor poll"
        }

        test "the reserved class hint tolerates blanks and whitespace" {
            let parsed =
                standardSpec
                |> withClasses "gpu, ,high-memory "
                |> ComputeBackendRouting.requiredResourceClasses

            Expect.equal
                parsed
                (Set.ofList [ "gpu"; "high-memory" ])
                "a stray comma must not produce an unsatisfiable empty class that refuses everything"
        }

        test "a spec with no reserved hint declares no hard requirement" {
            Expect.isEmpty
                (ComputeBackendRouting.requiredResourceClasses standardSpec)
                "a spec that says nothing about resources can never be refused for them"
        }
    ]

// ─── 484.B — the typed refusal, and the payload that never left ──────

let refusalTests =
    testList "Phase 484.B — no eligible backend is a typed refusal naming the gap" [

        test "an Isolated spec over a fleet with no isolating backend is refused, and no backend sees the payload" {
            let a = RecordingBackend("cpu-a", None)
            let b = RecordingBackend("cpu-b", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "cpu-a" a
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "cpu-b" b
                ]

            let error = isolatedSpec |> submit dispatcher |> errorOrFail

            // The load-bearing assertion: the refusal happened BEFORE the
            // hand-off. A check after the fact is a check on something
            // that has already left.
            Expect.equal a.SubmitCount 0 "the payload never reached the default backend"
            Expect.equal b.SubmitCount 0 "nor the other one"

            Expect.isFalse error.Retriable "terminal — a fleet does not gain an isolating backend by being asked twice"

            Expect.stringContains error.Message "isolated" "the required profile is named"
            Expect.stringContains error.Message "'cpu-a'" "each available backend is named"
            Expect.stringContains error.Message "'cpu-b'" "including the second"
            Expect.stringContains error.Message "standard" "with the profile it can actually honour"
        }

        test "control — the SAME fleet accepts the SAME work at Standard, so the zero counts above are real" {
            let a = RecordingBackend("cpu-a", None)
            let b = RecordingBackend("cpu-b", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "cpu-a" a
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "cpu-b" b
                ]

            submit dispatcher standardSpec |> handleOrFail |> ignore

            Expect.equal a.SubmitCount 1 "the fixture does record submissions when one is made"
        }

        test "a required class no backend declares is refused, naming required versus available" {
            let cpuPool = RecordingBackend("cpu-pool", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "cpu-pool" cpuPool
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                ]

            let error =
                standardSpec |> withClasses "quantum" |> submit dispatcher |> errorOrFail

            Expect.equal cpuPool.SubmitCount 0 "an infeasible spec is not quietly sent to the default"
            Expect.stringContains error.Message "quantum" "the required class is named"
            Expect.stringContains error.Message "cpu" "and what the fleet actually offers"
            Expect.isFalse error.Retriable "terminal — no retry declares a resource class"
        }

        test "a class set split across two backends is refused — no SINGLE backend can run the work" {
            // Each class is served by some backend, but neither serves
            // both. This is the case a naive "any backend declaring any
            // required class" filter would wrongly accept.
            let gpuOnly = RecordingBackend("gpu-only", None)
            let memOnly = RecordingBackend("mem-only", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "gpu-only" gpuOnly
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                    ComputeBackendRegistration.create "mem-only" memOnly
                    |> ComputeBackendRegistration.withResourceClasses [ "high-memory" ]
                ]

            let error =
                standardSpec
                |> withClasses "gpu,high-memory"
                |> submit dispatcher
                |> errorOrFail

            Expect.equal gpuOnly.SubmitCount 0 "a partial match is not a match"
            Expect.equal memOnly.SubmitCount 0 "in either direction"

            Expect.stringContains
                error.Message
                "every required resource class"
                "the refusal says WHY — the set is unservable by one backend, not unknown"

            // Control: one backend declaring BOTH classes accepts the
            // identical spec, so the refusal is about the split and not
            // about the class syntax.
            let both = RecordingBackend("both", None)

            let _, _, _, wide =
                router [
                    ComputeBackendRegistration.create "both" both
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu"; "high-memory" ]
                ]

            standardSpec
            |> withClasses "gpu,high-memory"
            |> submit wide
            |> handleOrFail
            |> ignore

            Expect.equal both.SubmitCount 1 "control — one backend covering the whole set takes it"
        }

        test "a routing refusal is counted, tagged by the profile it could not place" {
            let _, _, metrics, dispatcher =
                router [ ComputeBackendRegistration.create "cpu" (RecordingBackend("cpu", None)) ]

            isolatedSpec |> submit dispatcher |> errorOrFail |> ignore

            Expect.equal
                (metrics.IncrementsOf ComputeFleetMetrics.RoutingRefusalsTotal)
                [ Map [ "profile", "isolated" ] ]
                "one refusal, tagged with the profile — there is no backend to attribute it to, which is the finding"
        }

        test "a backend's own refusal passes through unchanged — the router does not re-word it" {
            let backendError =
                ExternalComputeError.retriable "the accelerator queue is saturated"

            let registry =
                ComputeBackendRegistry [
                    ComputeBackendRegistration.create "busy" (RefusingBackend("busy", backendError))
                ]

            let telemetry = ComputeFleetTelemetry()
            let metrics = CapturingMetricsSink()

            let dispatcher =
                RoutingComputeDispatcher(registry, telemetry, metrics) :> IExternalComputeDispatcher

            let error = submit dispatcher standardSpec |> errorOrFail

            Expect.equal error backendError "the backend's diagnostic reaches the caller verbatim"

            Expect.isTrue
                error.Retriable
                "including its retriability — the router must not flatten a transient refusal into a terminal one"

            Expect.equal
                (metrics.IncrementsOf ComputeFleetMetrics.SubmissionsTotal)
                [ Map [ "backend", "busy"; "outcome", "refused" ] ]
                "and it is counted against the backend that refused"
        }
    ]

// ─── 484.B — handle round-trip ───────────────────────────────────────

let handleRoundTripTests =
    testList "Phase 484.B — a handle returns to the backend that minted it" [

        test "Poll and Cancel route to the submitting backend and to no other" {
            let a = RecordingBackend("a", None)
            let b = RecordingBackend("b", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "a" a
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    ComputeBackendRegistration.create "b" b
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                    |> ComputeBackendRegistration.asDefault
                ]

            let handle = standardSpec |> withClasses "cpu" |> submit dispatcher |> handleOrFail

            Expect.equal handle.Backend "a" "the handle carries the routing key"

            let outcome = dispatcher.Poll handle |> Async.RunSynchronously
            dispatcher.Cancel handle |> Async.RunSynchronously

            Expect.equal outcome (ExternalOutcome.Succeeded "blob://out") "the poll reached a real backend"
            Expect.equal a.PollCount 1 "polled the backend that minted it"
            Expect.equal a.CancelCount 1 "cancelled the same one"

            Expect.equal
                b.PollCount
                0
                "and never the default — polling a NativeRef against the wrong backend would report a terminal failure for work that is running fine"

            Expect.equal b.CancelCount 0 "nor cancelled it"
        }

        test "the handle is restamped with the registered Kind when the backend's own label differs" {
            // One companion composed under a deployment-chosen kind. The
            // backend stamps `shared-companion`; the registry keys on
            // `cluster-east`.
            let backend = RecordingBackend("shared-companion", None)

            let _, _, _, dispatcher =
                router [ ComputeBackendRegistration.create "cluster-east" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail

            Expect.equal
                handle.Backend
                "cluster-east"
                "the routing key wins — the registry keys on Kind, so the handle must carry Kind"

            // And it round-trips.
            dispatcher.Poll handle |> Async.RunSynchronously |> ignore
            Expect.equal backend.PollCount 1 "the restamped handle routes back"
        }

        test "mutation check — the SAME handle carrying the backend's raw label is refused" {
            // This is what the restamp prevents. Without it, every handle
            // from the case above would look like this one.
            let backend = RecordingBackend("shared-companion", None)

            let _, _, _, dispatcher =
                router [ ComputeBackendRegistration.create "cluster-east" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail

            let unstamped = {
                handle with
                    Backend = "shared-companion"
            }

            match dispatcher.Poll unstamped |> Async.RunSynchronously with
            | ExternalOutcome.Failed error ->
                Expect.stringContains error.Message "shared-companion" "the unroutable kind is named"
                Expect.isFalse error.Retriable "terminal — retrying does not register a kind"
            | other -> failtestf "expected an unroutable handle to report Failed, got %A" other

            Expect.equal
                backend.PollCount
                0
                "so the green above is attributable to the restamp, not to the two strings coinciding"
        }

        test "a handle whose kind is not registered is refused, never redirected to the default" {
            let only = RecordingBackend("only", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "only" only
                    |> ComputeBackendRegistration.asDefault
                ]

            let orphan: ExternalHandle = {
                HandleId = Guid.Parse "c0ffee00-1111-4222-8333-444455556666"
                Backend = "decommissioned-pool"
                ScopeId = "team-1"
                NativeRef = "opaque://old/1"
                SubmittedAt = DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)
            }

            match dispatcher.Poll orphan |> Async.RunSynchronously with
            | ExternalOutcome.Failed error ->
                Expect.stringContains error.Message "decommissioned-pool" "the unregistered kind is named"
                Expect.stringContains error.Message "'only'" "and what IS registered, so the fix is legible"
            | other -> failtestf "Phase 318 requires an unrecognised handle report Failed, got %A" other

            Expect.equal
                only.PollCount
                0
                "the default was not consulted — a redirect would manufacture a confident 'this failed' for work it has never heard of"
        }

        test "cancelling an unroutable handle is a no-op, not a throw — Phase 318 requires idempotent Cancel" {
            let only = RecordingBackend("only", None)

            let _, _, _, dispatcher = router [ ComputeBackendRegistration.create "only" only ]

            let orphan: ExternalHandle = {
                HandleId = Guid.NewGuid()
                Backend = "gone"
                ScopeId = "team-1"
                NativeRef = "opaque://old/1"
                SubmittedAt = DateTime.UtcNow
            }

            dispatcher.Cancel orphan |> Async.RunSynchronously
            Expect.equal only.CancelCount 0 "and it did not fan out to a backend that never minted it"
        }

        test "the router's own Backend label never reaches a handle" {
            let backend = RecordingBackend("worker", None)

            let _, _, _, dispatcher =
                router [ ComputeBackendRegistration.create "worker" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail

            Expect.equal dispatcher.Backend RoutingComputeDispatcher.BackendName "the router reports its own label"

            Expect.notEqual
                handle.Backend
                RoutingComputeDispatcher.BackendName
                "but a handle stamped 'routing' would poll against a name no backend answers to"

            Expect.equal handle.Backend "worker" "it carries the routing key instead"
        }
    ]

// ─── 484.C — fleet observability ─────────────────────────────────────

let observabilityTests =
    testList "Phase 484.C — fleet observability" [

        test "per-backend counters follow a submission through to a terminal outcome" {
            let backend = RecordingBackend("pool", None)

            let _, telemetry, _, dispatcher =
                router [ ComputeBackendRegistration.create "pool" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail

            let afterSubmit = telemetry.StatsFor "pool"
            Expect.equal afterSubmit.Submitted 1L "the acceptance is counted"
            Expect.equal afterSubmit.InFlight 1L "and is in flight"
            Expect.equal afterSubmit.Succeeded 0L "nothing terminal yet"

            dispatcher.Poll handle |> Async.RunSynchronously |> ignore

            let afterPoll = telemetry.StatsFor "pool"
            Expect.equal afterPoll.InFlight 0L "the terminal outcome retires it"
            Expect.equal afterPoll.Succeeded 1L "and lands in the right bucket"
            Expect.equal afterPoll.Failed 0L "not another"
        }

        test "a non-terminal poll is not an event — counting it would measure how often the caller polls" {
            let backend = RecordingBackend("pool", None, ExternalOutcome.Running(Some 0.5))

            let _, telemetry, metrics, dispatcher =
                router [ ComputeBackendRegistration.create "pool" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail

            for _ in 1..4 do
                dispatcher.Poll handle |> Async.RunSynchronously |> ignore

            let stats = telemetry.StatsFor "pool"
            Expect.equal stats.InFlight 1L "four Running polls leave it in flight"
            Expect.equal (stats.Succeeded + stats.Failed + stats.Cancelled) 0L "and record no terminal outcome"

            Expect.isEmpty
                (metrics.IncrementsOf ComputeFleetMetrics.TerminalOutcomesTotal)
                "nor emit a terminal counter"
        }

        test "in-flight is floored at zero, so a pre-router handle cannot drive the gauge negative" {
            let backend = RecordingBackend("pool", None)

            let _, telemetry, _, dispatcher =
                router [ ComputeBackendRegistration.create "pool" backend ]

            // A handle the router never accepted — the Phase 318
            // single-dispatcher deployment's outstanding work.
            let preRouter: ExternalHandle = {
                HandleId = Guid.NewGuid()
                Backend = "pool"
                ScopeId = "team-1"
                NativeRef = "opaque://pre/1"
                SubmittedAt = DateTime.UtcNow
            }

            for _ in 1..3 do
                dispatcher.Poll preRouter |> Async.RunSynchronously |> ignore

            Expect.equal (telemetry.StatsFor "pool").InFlight 0L "three unmatched terminal outcomes floor at zero"

            // And the counter is self-correcting: a genuine acceptance
            // after the over-decrements still reads 1, which a
            // clamp-only-on-read implementation would report as 0.
            submit dispatcher standardSpec |> handleOrFail |> ignore

            Expect.equal
                (telemetry.StatsFor "pool").InFlight
                1L
                "a later acceptance is not swallowed by accumulated negative drift"
        }

        test "metrics carry the backend tag on every per-backend emission" {
            let backend = RecordingBackend("pool", None)

            let _, _, metrics, dispatcher =
                router [ ComputeBackendRegistration.create "pool" backend ]

            let handle = submit dispatcher standardSpec |> handleOrFail
            dispatcher.Poll handle |> Async.RunSynchronously |> ignore

            Expect.equal
                (metrics.IncrementsOf ComputeFleetMetrics.SubmissionsTotal)
                [ Map [ "backend", "pool"; "outcome", "accepted" ] ]
                "the submission counter is tagged by backend AND outcome — a name-only assertion would pass with the backend tag missing, which is the tag that makes it per-backend"

            Expect.equal
                (metrics.IncrementsOf ComputeFleetMetrics.TerminalOutcomesTotal)
                [ Map [ "backend", "pool"; "outcome", "succeeded" ] ]
                "so is the terminal counter"

            let gauges = metrics.GaugesOf ComputeFleetMetrics.InFlight

            Expect.equal
                gauges
                [ 1.0, Map [ "backend", "pool" ]; 0.0, Map [ "backend", "pool" ] ]
                "the in-flight gauge rises on accept and falls on terminal"
        }

        test "a fresh backend is Healthy — no traffic is not a fault" {
            let telemetry = ComputeFleetTelemetry()
            let probe = ComputeBackendHealthCheck("pool", telemetry) :> IHealthCheck

            Expect.equal (probe.Check() |> Async.RunSynchronously) Healthy "an idle backend is not unhealthy"
            Expect.equal probe.Name "external_compute:pool" "the name suffixes the instance id per IHealthCheck"
            Expect.equal probe.Kind Readiness "a compute backend gates readiness, not liveness"
        }

        test "a retriable refusal with no prior acceptance is Unhealthy — the mis-composed shape" {
            let telemetry = ComputeFleetTelemetry()
            telemetry.RecordRefused("pool", ExternalComputeError.retriable "endpoint unreachable")

            match
                ComputeBackendHealthCheck("pool", telemetry) :> IHealthCheck
                |> fun p -> p.Check() |> Async.RunSynchronously
            with
            | Unhealthy message ->
                Expect.stringContains message "never accepted" "the diagnostic distinguishes mis-composed from busy"
                Expect.stringContains message "endpoint unreachable" "and quotes the backend's own text"
            | other -> failtestf "expected Unhealthy, got %A" other
        }

        test "a retriable refusal AFTER an acceptance is Degraded — reachable but misbehaving" {
            let telemetry = ComputeFleetTelemetry()
            telemetry.RecordAccepted "pool"
            telemetry.RecordRefused("pool", ExternalComputeError.retriable "queue saturated")

            match
                ComputeBackendHealthCheck("pool", telemetry) :> IHealthCheck
                |> fun p -> p.Check() |> Async.RunSynchronously
            with
            | Degraded message -> Expect.stringContains message "queue saturated" "the current condition is named"
            | other -> failtestf "expected Degraded — the backend has proven it works, so it is not Unhealthy: %A" other
        }

        test "a TERMINAL refusal leaves the backend Healthy — a caller's bad request cannot mark it down" {
            let telemetry = ComputeFleetTelemetry()

            for _ in 1..10 do
                telemetry.RecordRefused("pool", ExternalComputeError.terminal "unknown work kind 'typo'")

            let probe = ComputeBackendHealthCheck("pool", telemetry) :> IHealthCheck

            Expect.equal
                (probe.Check() |> Async.RunSynchronously)
                Healthy
                "ten malformed submissions say nothing about the backend's health — Retriable is the axis that does"

            // Control on the SAME fixture: swap terminal for retriable and
            // the probe DOES move, so the green above is the retriability
            // split working rather than the probe being unable to fail.
            telemetry.RecordRefused("pool", ExternalComputeError.retriable "endpoint unreachable")

            Expect.notEqual
                (probe.Check() |> Async.RunSynchronously)
                Healthy
                "control — a retriable refusal on the same fixture does move the probe"

            // And the terminal refusals were still recorded for the panel.
            Expect.equal (telemetry.StatsFor "pool").Refused 11L "every refusal is visible on the fleet row"
        }

        test "an acceptance clears the failure run, so a recovered backend reports Healthy again" {
            let telemetry = ComputeFleetTelemetry()
            telemetry.RecordAccepted "pool"
            telemetry.RecordRefused("pool", ExternalComputeError.retriable "queue saturated")
            let probe = ComputeBackendHealthCheck("pool", telemetry) :> IHealthCheck

            Expect.notEqual (probe.Check() |> Async.RunSynchronously) Healthy "degraded while refusing"

            telemetry.RecordAccepted "pool"

            Expect.equal
                (probe.Check() |> Async.RunSynchronously)
                Healthy
                "one acceptance is the evidence the condition passed — health must not latch"
        }

        test "the fleet row lists every REGISTERED backend, including one with no traffic" {
            let busy = RecordingBackend("busy", None)
            let idle = RecordingBackend("idle", Some isolatedPosture)

            let registry =
                ComputeBackendRegistry [
                    ComputeBackendRegistration.create "busy" busy
                    |> ComputeBackendRegistration.withResourceClasses [ "cpu" ]
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "idle" idle
                    |> ComputeBackendRegistration.withEnvelopeVersions [ "v2" ]
                ]

            let telemetry = ComputeFleetTelemetry()
            let metrics = CapturingMetricsSink()

            let dispatcher =
                RoutingComputeDispatcher(registry, telemetry, metrics) :> IExternalComputeDispatcher

            submit dispatcher standardSpec |> handleOrFail |> ignore

            let panel, payload =
                ComputeFleetDiagnosticsContributor(registry, telemetry) :> IDevDiagnosticsContributor
                |> fun c -> c.Contribute() |> Async.RunSynchronously

            Expect.equal panel "Compute fleet" "a distinctive panel name — contributors overwrite on collision"

            let rendered = sprintf "%A" payload

            Expect.stringContains rendered "busy" "the backend taking work appears"

            Expect.stringContains
                rendered
                "idle"
                "and so does the one with none — an absent row would make an idle backend look unregistered"

            Expect.stringContains rendered "isolated" "with its derived profile support"
            Expect.stringContains rendered "v2" "and its declared envelope versions"

            // The mechanism the row cannot rely on: Snapshot() only knows
            // backends the router has touched, which is why the
            // contributor iterates registrations instead.
            Expect.isFalse
                (telemetry.Snapshot() |> Map.containsKey "idle")
                "control — Snapshot alone omits the idle backend, so iterating registrations is load-bearing"
        }
    ]

// ─── 484.D — GP 13: a single-dispatcher deployment is unchanged ──────

let gp13Tests =
    testList "Phase 484.D — GP 13: the registry adds nothing to a single-dispatcher deployment" [

        test "NoExternalCompute composes exactly what it did before 484 — no registry, no telemetry, no probe, no panel" {
            let services = ServiceCollection()

            ComposeStores.registerExternalCompute services {
                ServerConfig.defaults with
                    ExternalCompute = NoExternalCompute
            }

            use provider = services.BuildServiceProvider()

            Expect.isTrue
                (provider.GetService<IExternalComputeDispatcher>() :? NoExternalComputeDispatcher)
                "the default is still the no-op dispatcher"

            // The structural GP 13 assertion: every type this phase adds
            // is absent from the graph, by resolution rather than by
            // reading the compose source.
            Expect.isNull (box (provider.GetService<ComputeBackendRegistry>())) "no registry is composed"
            Expect.isNull (box (provider.GetService<ComputeFleetTelemetry>())) "no telemetry is composed"

            Expect.isEmpty
                (provider.GetServices<IHealthCheck>()
                 |> Seq.filter (fun probe -> probe.Name.StartsWith "external_compute:"))
                "no compute-backend readiness probe is registered"

            Expect.isEmpty
                (provider.GetServices<IDevDiagnosticsContributor>()
                 |> Seq.filter (fun c -> c :? ComputeFleetDiagnosticsContributor))
                "no fleet panel is registered"

            Expect.equal
                (provider.GetServices<IExternalComputeDispatcher>() |> Seq.length)
                1
                "and exactly one dispatcher — the router does not shadow the no-op"
        }

        test "the no-op dispatcher's refusal is byte-for-byte the pre-484 message" {
            let dispatcher = NoExternalComputeDispatcher() :> IExternalComputeDispatcher

            Expect.equal
                (submit dispatcher standardSpec)
                (Error ExternalComputeError.notConfigured)
                "this phase adds no behaviour to NoExternalCompute — same error value, not merely the same shape"

            Expect.equal
                (ExecutionProfileGate.postureOf dispatcher)
                IsolationPosture.standardOnly
                "and it still declares nothing"
        }

        test "a one-backend fleet answers identically to the backend called directly" {
            let direct = RecordingBackend("solo", None)
            let viaRouter = RecordingBackend("solo", None)

            let _, _, _, dispatcher =
                router [ ComputeBackendRegistration.create "solo" viaRouter ]

            let directHandle =
                (direct :> IExternalComputeDispatcher).Submit("team-1", standardSpec)
                |> Async.RunSynchronously
                |> handleOrFail

            let routedHandle = submit dispatcher standardSpec |> handleOrFail

            // Every field except the platform-minted id and timestamp,
            // which are per-submission by construction.
            Expect.equal routedHandle.Backend directHandle.Backend "same backend stamp"
            Expect.equal routedHandle.ScopeId directHandle.ScopeId "same scope"
            Expect.equal routedHandle.NativeRef directHandle.NativeRef "same opaque native ref, untouched"

            Expect.equal
                (dispatcher.Poll routedHandle |> Async.RunSynchronously)
                ((direct :> IExternalComputeDispatcher).Poll directHandle
                 |> Async.RunSynchronously)
                "and the same outcome — a pass-through registration is a pass-through"
        }

        test "the scope and spec reach the backend unaltered — the router brokers, it does not rewrite" {
            let backend = RecordingBackend("pool", None)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "pool" backend
                    |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                ]

            let spec =
                ExternalWorkSpec.create "render" "{\"frames\":120}"
                |> withClasses "gpu"
                |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 30.0)
                |> ExternalWorkSpec.withIdempotency "job-42"

            submit dispatcher spec |> handleOrFail |> ignore

            Expect.equal
                backend.Submitted
                [ "team-1", spec ]
                "the backend receives the caller's scope and the identical spec — including the routing hint, which the backend may also read"
        }

        test "ComputeFleetCompose registers the fleet: router as an INSTANCE, one probe per backend, one panel" {
            let services = ServiceCollection()

            let router' =
                ComputeFleetCompose.register
                    services
                    [
                        ComputeBackendRegistration.create "cpu" (RecordingBackend("cpu", None))
                        |> ComputeBackendRegistration.asDefault
                        ComputeBackendRegistration.create "gpu" (RecordingBackend("gpu", Some isolatedPosture))
                        |> ComputeBackendRegistration.withResourceClasses [ "gpu" ]
                    ]
                    (NoOpMetricsSink())

            Expect.equal router'.Registry.Kinds [ "cpu"; "gpu" ] "the returned router carries the fleet"

            // Phase 319's reconciliation introspects the service
            // collection for an INSTANCE-registered dispatcher and
            // silently disables the hand-off path if it finds only a
            // factory. Asserted on the descriptor, because a resolved
            // service looks identical either way.
            let descriptor =
                services
                |> Seq.find (fun d -> d.ServiceType = typeof<IExternalComputeDispatcher>)

            Expect.isNotNull
                (box descriptor.ImplementationInstance)
                "the router MUST be an instance registration or Phase 319's external hand-off reconciliation silently turns itself off"

            use provider = services.BuildServiceProvider()

            Expect.isTrue
                (provider.GetService<IExternalComputeDispatcher>() :? RoutingComputeDispatcher)
                "the router is the deployment's dispatcher"

            let probeNames =
                provider.GetServices<IHealthCheck>() |> Seq.map _.Name |> Seq.sort |> List.ofSeq

            Expect.equal
                probeNames
                [ "external_compute:cpu"; "external_compute:gpu" ]
                "one readiness probe per backend, uniquely named"

            Expect.equal
                (provider.GetServices<IDevDiagnosticsContributor>()
                 |> Seq.filter (fun c -> c :? ComputeFleetDiagnosticsContributor)
                 |> Seq.length)
                1
                "exactly one fleet panel"

            Expect.isNotNull (box (provider.GetService<ComputeBackendRegistry>())) "the registry is resolvable"
            Expect.isNotNull (box (provider.GetService<ComputeFleetTelemetry>())) "so is the telemetry"
        }

        test "ComputeFleetCompose fails at compose on a duplicate kind, before anything is registered" {
            let services = ServiceCollection()

            Expect.throws
                (fun () ->
                    ComputeFleetCompose.register
                        services
                        [
                            ComputeBackendRegistration.create "same" (RecordingBackend("a", None))
                            ComputeBackendRegistration.create "same" (RecordingBackend("b", None))
                        ]
                        (NoOpMetricsSink())
                    |> ignore)
                "the registry is constructed eagerly so the clash surfaces at compose"

            Expect.isEmpty
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<IExternalComputeDispatcher>))
                "and nothing was half-registered — a partially-composed fleet would start and route wrongly"
        }
    ]

// ─── Structural: the router must not present as an isolating backend ─

let structuralTests =
    testList "Phase 484 — the router's isolation posture is deliberately undeclared" [

        test "the router does not implement IIsolatedComputeBackend, so it claims nothing" {
            let registry =
                ComputeBackendRegistry [
                    ComputeBackendRegistration.create "iso" (RecordingBackend("iso", Some isolatedPosture))
                ]

            let dispatcher =
                RoutingComputeDispatcher(registry, ComputeFleetTelemetry(), NoOpMetricsSink())

            Expect.isFalse
                (box dispatcher :? IIsolatedComputeBackend)
                "a single posture for a fan-out router would be a false claim in one direction or the other — declaring the clauses would have the Phase 478 gate wave through an Isolated spec the router might place on a non-isolating backend"

            Expect.equal
                (ExecutionProfileGate.postureOf (dispatcher :> IExternalComputeDispatcher))
                IsolationPosture.standardOnly
                "so it reads as standardOnly — which is why the router must not be wrapped in ExecutionProfileGate.enforce"
        }

        test "the router enforces the profile ITSELF, more precisely than the gate would" {
            // The gate would refuse an Isolated spec against this router
            // (postureOf reads standardOnly). Routing accepts it and
            // places it on the isolating backend — and refuses with a
            // FLEET-level diagnostic when there is none.
            let iso = RecordingBackend("iso", Some isolatedPosture)

            let _, _, _, dispatcher =
                router [
                    ComputeBackendRegistration.create "plain" (RecordingBackend("plain", None))
                    |> ComputeBackendRegistration.asDefault
                    ComputeBackendRegistration.create "iso" iso
                ]

            let handle = isolatedSpec |> submit dispatcher |> handleOrFail

            Expect.equal handle.Backend "iso" "the router placed isolated work on the isolating backend"
            Expect.equal iso.SubmitCount 1 "which the Phase 478 gate over the router would have refused outright"

            Expect.isError
                (ExecutionProfileGate.check dispatcher isolatedSpec)
                "control — the gate over the router DOES refuse it, which is exactly why the two must not be stacked"
        }
    ]