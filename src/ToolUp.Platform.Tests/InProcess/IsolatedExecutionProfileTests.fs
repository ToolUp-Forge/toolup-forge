module ToolUp.Platform.Tests.InProcess.IsolatedExecutionProfileTests

open System
open System.Reflection
open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 478 — the isolated execution profile ──────────────────────
//
// Two enforcement claims, and this pack is about holding each of them to
// the standard Phase 311 set rather than to "a test passed":
//
//   1. **An `Isolated` submission never reaches a backend that has not
//      declared the isolation posture.** Asserted by counting what the
//      inner dispatcher SAW, not by reading the returned error — the
//      error is equally consistent with a refusal issued after the
//      payload was handed over, which is the failure this phase exists
//      to prevent. Every such case is paired with a control running the
//      identical spec through the identical inner dispatcher UNWRAPPED,
//      which does record the submission. Delete the check in
//      `ExecutionProfileGate.enforce` and the control still passes while
//      the enforcement case turns red, which is the only arrangement
//      that makes the green meaningful.
//   2. **The ungated payload of an `Isolated` outcome is unreachable.**
//      Asserted structurally (a reflection sweep proving `hold` is the
//      only route to a `GatedComputeOutput` and that no public member
//      hands the bytes back) and behaviourally (a withheld release's
//      typed refusal, and the audit row it produced, contain no trace of
//      the payload). The behavioural half is paired with a floor-cleared
//      control on the SAME payload, so "the marker was absent" cannot be
//      passing because the pipeline broke and returns nothing.
//
// Plus the GP 11 half: a `Standard` spec's path through every one of
// these seams is the pre-478 path, asserted rather than asserted-about.

// ─── Fixtures ────────────────────────────────────────────────────────

let private jsonOptions = FableConverters.create ()

let private roundTrip<'T> (value: 'T) : 'T =
    let json = JsonSerializer.Serialize(value, jsonOptions)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

/// The distinctive string a withheld release must not leak. Deliberately
/// unlikely to occur incidentally, and deliberately free of raw control
/// bytes — a NUL in a source file makes git classify it binary and
/// silently disables EOL normalisation.
let private secretMarker = "ROW-LEVEL-SECRET-478"

let private isolatedPosture = IsolationPosture.clauses "test-sandbox"

let private handle: ExternalHandle = {
    HandleId = Guid.Parse "c0ffee00-1111-4222-8333-444455556666"
    Backend = "test-backend"
    ScopeId = "team-1"
    NativeRef = "opaque://job/1"
    SubmittedAt = DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)
}

/// A recording dispatcher: accepts everything, and remembers every spec
/// it was actually handed. The count is the assertion that matters —
/// "the refusal happened before the submission" is a claim about what
/// the backend saw, not about what the caller was told.
type private RecordingDispatcher(posture: IsolationPosture option) =
    let submitted = ResizeArray<ExternalWorkSpec>()
    let cancelled = ResizeArray<ExternalHandle>()

    member _.Submitted = List.ofSeq submitted
    member _.Cancelled = List.ofSeq cancelled

    interface IExternalComputeDispatcher with
        member _.Backend = "test-backend"

        member _.Submit(_scopeId, spec) = async {
            submitted.Add spec
            return Ok handle
        }

        member _.Poll(_handle) = async { return ExternalOutcome.Succeeded "blob://out" }

        member _.Cancel(h) = async { cancelled.Add h }

    interface IIsolatedComputeBackend with
        member _.IsolationPosture =
            match posture with
            | Some p -> p
            | None -> IsolationPosture.standardOnly

/// A dispatcher that does NOT implement `IIsolatedComputeBackend` at all
/// — the shape every pre-478 companion has. It must read as
/// `standardOnly`, never as "unspecified, therefore fine".
type private UndeclaringDispatcher() =
    let submitted = ResizeArray<ExternalWorkSpec>()
    member _.Submitted = List.ofSeq submitted

    interface IExternalComputeDispatcher with
        member _.Backend = "undeclaring-backend"

        member _.Submit(_scopeId, spec) = async {
            submitted.Add spec
            return Ok handle
        }

        member _.Poll(_handle) = async { return ExternalOutcome.Pending }

        member _.Cancel(_handle) = async { return () }

let private cell label count : PrivacyCell = {
    Label = label
    Count = count
    Value = None
}

let private template: CleanRoomTemplate = {
    TemplateId = "compute-478"
    AllowedMethods = Set.ofList [ "fit-model" ]
    Floor = {
        MinCohortSize = 10
        SuppressionThreshold = 5
        PermittedShapes = Set.ofList [ Count; Histogram ]
    }
}

/// A cohort answer carrying the marker as a cell label, so a leak of the
/// payload is detectable in anything that renders it.
let private cohortJson (count: int) =
    JsonSerializer.Serialize(
        {
            Shape = Count
            Cells = [ cell secretMarker count ]
        },
        jsonOptions
    )

let private isolatedSpec =
    ExternalWorkSpec.create "fit-model" "{}" |> ExternalWorkSpec.isolated

let private standardSpec = ExternalWorkSpec.create "fit-model" "{}"

/// Collects gate decisions so a withhold can be asserted as a recorded
/// row rather than inferred from the returned refusal.
type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

let private heldOrFail (spec: ExternalWorkSpec) (payload: string) =
    match GatedComputeOutput.hold isolatedPosture spec handle payload with
    | Ok held -> held
    | Error e -> failtestf "expected the output to be held, got %A" e

// ─── 478.A — the profile is data on the portable spec ────────────────

let profileTests =
    testList "Phase 478.A — ExecutionProfile on ExternalWorkSpec" [

        test "ExternalWorkSpec.create defaults to Standard (GP 11 — the pre-478 spec, unchanged)" {
            Expect.equal
                (ExternalWorkSpec.create "k" "{}").Profile
                ExecutionProfile.Standard
                "every pre-478 call site builds the spec it always did"
        }

        test "withProfile / isolated set the requirement and leave every other field alone" {
            let baseSpec =
                ExternalWorkSpec.create "k" """{"a":1}"""
                |> ExternalWorkSpec.withHint "gpu" "1"
                |> ExternalWorkSpec.withTimeout (TimeSpan.FromMinutes 5.0)
                |> ExternalWorkSpec.withIdempotency "idem-1"

            let isolated = ExternalWorkSpec.isolated baseSpec

            Expect.equal isolated.Profile ExecutionProfile.Isolated "the profile is set"

            Expect.equal
                {
                    isolated with
                        Profile = ExecutionProfile.Standard
                }
                baseSpec
                "nothing but the profile moved"

            Expect.equal
                (ExternalWorkSpec.withProfile ExecutionProfile.Standard isolated)
                baseSpec
                "withProfile is total in both directions"
        }

        test "the profile survives the wire — it is data, not a call-frame promise (GP 12 rule 3)" {
            // The whole reason it is a spec field: a requirement that did
            // not serialise would be kept only by the process that
            // authored it, and this substrate exists so work is handed to
            // a process that is not that one.
            let back = roundTrip isolatedSpec
            Expect.equal back isolatedSpec "the whole spec round-trips"
            Expect.equal back.Profile ExecutionProfile.Isolated "the isolation requirement survives serialisation"

            let backStandard = roundTrip standardSpec
            Expect.equal backStandard.Profile ExecutionProfile.Standard "so does the default"
        }

        test "ExecutionProfile.label is stable for logs and audit payloads" {
            Expect.equal (ExecutionProfile.label ExecutionProfile.Standard) "standard" "standard"
            Expect.equal (ExecutionProfile.label ExecutionProfile.Isolated) "isolated" "isolated"
        }
    ]

// ─── 478.B — the isolation posture contract ──────────────────────────

let postureTests =
    testList "Phase 478.B — the isolation posture contract" [

        test "an undeclared posture claims nothing, so it honours Standard and refuses Isolated" {
            // The direction matters more than the values: a default that
            // honoured Isolated would make forgetting to declare
            // indistinguishable from declaring.
            Expect.isTrue
                (IsolationPosture.honours ExecutionProfile.Standard IsolationPosture.standardOnly)
                "Standard is honoured by every backend, declared or not"

            Expect.isFalse
                (IsolationPosture.honours ExecutionProfile.Isolated IsolationPosture.standardOnly)
                "a backend that declared nothing is not an isolating backend"
        }

        test "all three clauses honour Isolated; any two of three do not" {
            Expect.isTrue
                (IsolationPosture.honours ExecutionProfile.Isolated isolatedPosture)
                "the full posture honours Isolated"

            // Two of three is not a weaker clean room, it is a leak with
            // a longer description — so each partial is refused, and the
            // shortfall names the clause that is missing.
            let partials = [
                {
                    isolatedPosture with
                        NoEgress = false
                },
                "no-egress"
                {
                    isolatedPosture with
                        InputsRestrictedToDeclaredRefs = false
                },
                "declared-refs-only"
                {
                    isolatedPosture with
                        EphemeralWorkspace = false
                },
                "ephemeral-workspace"
            ]

            for posture, clause in partials do
                Expect.isFalse
                    (IsolationPosture.honours ExecutionProfile.Isolated posture)
                    $"a posture missing {clause} does not honour Isolated"

                let shortfall = IsolationPosture.shortfall posture
                Expect.hasLength shortfall 1 $"exactly the one missing clause is named for {clause}"

                Expect.stringContains
                    (List.head shortfall)
                    clause
                    $"the shortfall names {clause}, so a refusal is actionable"

                Expect.isTrue
                    (IsolationPosture.honours ExecutionProfile.Standard posture)
                    $"a posture missing {clause} still runs Standard work"
        }

        test "the refusal is terminal and names the backend and the shortfall" {
            // Terminal on purpose: a backend does not become isolating by
            // being asked twice, and a retriable refusal would have a
            // caller re-offering gated work to a leaky worker on a timer.
            let refusal = IsolationPosture.refusal "gpu-pool" IsolationPosture.standardOnly

            Expect.isFalse refusal.Retriable "a composition change is not a transient condition"
            Expect.stringContains refusal.Message "gpu-pool" "the refusal names the backend"
            Expect.stringContains refusal.Message "no-egress" "and the first missing clause"

            Expect.stringContains
                refusal.Message
                "ExecutionProfile.Standard"
                "and the other way out, for work that is not clean-room data"
        }

        test "describe reads back the enforcement mechanism a backend declared" {
            Expect.stringContains
                (IsolationPosture.describe isolatedPosture)
                "test-sandbox"
                "the declared mechanism is echoed for audit"

            Expect.stringContains
                (IsolationPosture.describe IsolationPosture.standardOnly)
                "standard-only"
                "an undeclared posture describes itself as standard-only"
        }
    ]

// ─── 478.B — the refusal happens BEFORE the submission ───────────────

let gateTests =
    testList "Phase 478.B — ExecutionProfileGate refuses before any work is submitted" [

        test "postureOf reads a declaring backend, and reads an UNDECLARING one as standardOnly" {
            let declaring =
                RecordingDispatcher(Some isolatedPosture) :> IExternalComputeDispatcher

            let undeclaring = UndeclaringDispatcher() :> IExternalComputeDispatcher

            Expect.equal (ExecutionProfileGate.postureOf declaring) isolatedPosture "a declared posture is read back"

            Expect.equal
                (ExecutionProfileGate.postureOf undeclaring)
                IsolationPosture.standardOnly
                "a companion that never heard of this phase claims nothing"
        }

        test "an Isolated spec never reaches a non-declaring backend — the payload does not leave the process" {
            let inner = UndeclaringDispatcher()
            let guarded = ExecutionProfileGate.enforce (inner :> IExternalComputeDispatcher)

            let result = guarded.Submit("team-1", isolatedSpec) |> Async.RunSynchronously

            match result with
            | Ok _ -> failtest "an Isolated submission to a non-declaring backend must be refused"
            | Error e ->
                Expect.isFalse e.Retriable "the refusal is terminal"
                Expect.stringContains e.Message "undeclaring-backend" "and names the backend"

            // The assertion this case exists for. A refusal issued AFTER
            // the hand-off returns the same `Error`, and would be
            // indistinguishable from this one if we only read the result.
            Expect.isEmpty inner.Submitted "the backend was never handed the spec"
        }

        test "CONTROL — the same spec through the same UNWRAPPED backend does reach it" {
            // The paired half. Without it, the case above passes equally
            // against a dispatcher that had broken and stopped accepting
            // anything at all.
            let inner = UndeclaringDispatcher()

            let result =
                (inner :> IExternalComputeDispatcher).Submit("team-1", isolatedSpec)
                |> Async.RunSynchronously

            Expect.isTrue (Result.isOk result) "unwrapped, the backend accepts it"

            Expect.hasLength
                inner.Submitted
                1
                "so the enforcement case above is measuring the gate, not a broken dispatcher"
        }

        test "an Isolated spec DOES reach a declaring backend" {
            let inner = RecordingDispatcher(Some isolatedPosture)
            let guarded = ExecutionProfileGate.enforce (inner :> IExternalComputeDispatcher)

            let result = guarded.Submit("team-1", isolatedSpec) |> Async.RunSynchronously

            Expect.isTrue (Result.isOk result) "a declaring backend is handed the work"
            Expect.hasLength inner.Submitted 1 "exactly once"

            Expect.equal
                (List.head inner.Submitted).Profile
                ExecutionProfile.Isolated
                "and the requirement travels with it, so the backend can honour it"
        }

        test "a Standard spec is untouched by the gate, on a declaring or non-declaring backend (GP 11)" {
            for inner, label in
                [
                    (UndeclaringDispatcher() :> IExternalComputeDispatcher), "undeclaring"
                    (RecordingDispatcher(Some isolatedPosture) :> IExternalComputeDispatcher), "declaring"
                ] do
                let guarded = ExecutionProfileGate.enforce inner

                let direct = inner.Submit("team-1", standardSpec) |> Async.RunSynchronously
                let viaGate = guarded.Submit("team-1", standardSpec) |> Async.RunSynchronously

                Expect.equal viaGate direct $"the {label} backend answers a Standard spec identically through the gate"

            // Poll and Cancel are pass-throughs: they act on a handle the
            // backend already minted, so there is nothing left to refuse.
            let inner = RecordingDispatcher(Some isolatedPosture)
            let guarded = ExecutionProfileGate.enforce (inner :> IExternalComputeDispatcher)

            Expect.equal
                guarded.Backend
                "test-backend"
                "the decorator presents the INNER backend's label, not one of its own"

            Expect.equal
                (guarded.Poll handle |> Async.RunSynchronously)
                (ExternalOutcome.Succeeded "blob://out")
                "Poll passes through"

            guarded.Cancel handle |> Async.RunSynchronously
            Expect.equal inner.Cancelled [ handle ] "Cancel passes through"
        }

        test "check agrees with enforce, and the gate re-declares the posture so stacking cannot downgrade it" {
            let declaring =
                RecordingDispatcher(Some isolatedPosture) :> IExternalComputeDispatcher

            let undeclaring = UndeclaringDispatcher() :> IExternalComputeDispatcher

            Expect.isTrue (Result.isOk (ExecutionProfileGate.check declaring isolatedSpec)) "declaring + Isolated"
            Expect.isTrue (Result.isOk (ExecutionProfileGate.check undeclaring standardSpec)) "undeclaring + Standard"

            Expect.isTrue
                (Result.isError (ExecutionProfileGate.check undeclaring isolatedSpec))
                "undeclaring + Isolated is refused"

            // A wrapper that swallowed the declaration would make
            // composing this gate the reason Isolated stops working —
            // the shape of a control nobody leaves switched on.
            let stacked =
                declaring
                |> ExecutionProfileGate.enforce
                |> ExecutionProfileGate.enforce
                |> ExecutionProfileGate.enforce

            Expect.equal
                (ExecutionProfileGate.postureOf stacked)
                isolatedPosture
                "the posture survives arbitrary re-wrapping"

            Expect.isTrue
                (Result.isOk (stacked.Submit("team-1", isolatedSpec) |> Async.RunSynchronously))
                "so a thrice-wrapped isolating backend still accepts Isolated work"
        }
    ]

// ─── 478.C — the ungated payload is unreachable ──────────────────────

let structuralTests =
    testList "Phase 478.C — the ungated payload is structurally unreachable" [

        test "GatedComputeOutput.hold is the only public route to a GatedComputeOutput" {
            // The generalising assertion, in the shape Phase 311's uses:
            // every behavioural case below shows one path is gated; only
            // this one shows there is no second path. A future helper
            // that minted a held output without the posture check fails
            // HERE, where it is written.
            let target = typeof<GatedComputeOutput>
            let assembly = target.Assembly

            let producers =
                assembly.GetExportedTypes()
                |> Array.collect (fun t ->
                    let methods =
                        t.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance)
                        |> Array.filter (fun m ->
                            m.ReturnType = target
                            || (m.ReturnType.IsGenericType
                                && m.ReturnType.GetGenericArguments() |> Array.contains target))
                        |> Array.map (fun m -> $"{t.FullName}.{m.Name}")

                    let ctors =
                        if t = target then
                            t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance)
                            |> Array.map (fun _ -> $"{t.FullName}..ctor")
                        else
                            [||]

                    Array.append methods ctors)

            Expect.isNonEmpty producers "the sweep must find `hold` at least, else it proves nothing"

            let unexpected = producers |> Array.filter (fun name -> not (name.EndsWith ".hold"))

            Expect.isEmpty unexpected $"only GatedComputeOutput.hold may produce a held output; found %A{unexpected}"
        }

        test "no public member of GatedComputeOutput hands the payload back" {
            // The other half of unreachability: `hold` being the only
            // constructor is worth nothing if the value it builds has an
            // accessor.
            //
            // Asserted by VALUE rather than by name. A name-based sweep
            // ("no member called Payload") is satisfied by renaming, and
            // it missed the real leak here on the first draft: F#'s
            // generated `ToString()` on a union prints its case fields,
            // so a held output interpolated into a log line or an
            // exception message published the ungated payload through a
            // member nobody wrote. Invoking every public parameterless
            // member and looking for the marker catches that, catches
            // `%A`, and catches whatever the next member is without this
            // test needing to know its name.
            let held = heldOrFail isolatedSpec (cohortJson 40)
            let target = typeof<GatedComputeOutput>

            let invocable =
                target.GetMembers(BindingFlags.Public ||| BindingFlags.Instance)
                |> Array.choose (fun m ->
                    match m with
                    | :? PropertyInfo as p when p.CanRead && p.GetIndexParameters().Length = 0 ->
                        Some(p.Name, fun () -> p.GetValue held)
                    | :? MethodInfo as mi when mi.GetParameters().Length = 0 && mi.ReturnType <> typeof<Void> ->
                        Some(mi.Name, fun () -> mi.Invoke(held, [||]))
                    | _ -> None)

            // Non-vacuity: a sweep that found nothing would "pass".
            Expect.isNonEmpty invocable "the sweep must find at least Handle / Kind / ToString, else it proves nothing"

            let leaking =
                invocable
                |> Array.filter (fun (_, read) ->
                    let rendered =
                        try
                            sprintf "%A" (read ())
                        with _ ->
                            ""

                    rendered.Contains secretMarker)
                |> Array.map fst

            Expect.isEmpty leaking $"no public member may render the held payload; these did: %A{leaking}"

            // The structural renderings specifically, named so a
            // regression points straight at the cause.
            Expect.isFalse ((string held).Contains secretMarker) "ToString() does not render the payload"
            Expect.isFalse ((sprintf "%A" held).Contains secretMarker) "%A does not render the payload"
            Expect.stringContains (string held) "held ungated" "and says so explicitly"

            // Non-vacuity for the marker itself: it IS in the payload
            // that was held, so an absence above is a suppression and not
            // an empty pipeline.
            Expect.stringContains (cohortJson 40) secretMarker "the payload really does carry the marker"

            // The members that ARE meant to exist do.
            Expect.equal held.Handle handle "the handle is readable — the caller minted it"
            Expect.equal held.Kind "fit-model" "and so is the kind it chose"
        }

        test "a Standard submission's output is not routed through this pipeline (GP 11)" {
            match GatedComputeOutput.hold isolatedPosture standardSpec handle (cohortJson 40) with
            | Ok _ -> failtest "a Standard output must not be held — its result goes back to its caller as before"
            | Error(ComputeOutputNotIsolated reason) ->
                Expect.stringContains reason "Standard" "the refusal names the profile that caused it"
                Expect.stringContains reason "ExternalWorkSpec.isolated" "and the way to opt in"
            | Error other -> failtestf "wrong refusal: %A" other
        }

        test "an Isolated output from a backend that declared no posture is refused" {
            match GatedComputeOutput.hold IsolationPosture.standardOnly isolatedSpec handle (cohortJson 40) with
            | Ok _ -> failtest "an unattested backend's output has no clean-room provenance to gate"
            | Error(ComputeOutputUntrustedBackend e) ->
                Expect.isFalse e.Retriable "terminal"
                Expect.stringContains e.Message "test-backend" "and names the backend"
            | Error other -> failtestf "wrong refusal: %A" other
        }
    ]

// ─── 478.C — the gated release ───────────────────────────────────────

let releaseTests =
    testList "Phase 478.C — release runs the clean-room gate" [

        test "a floor-clearing output is released, and the decision is audited" {
            let sink = DecisionSink()

            let deps =
                GatedComputeDeps.create (CleanRoomBroker.create ()) template
                |> GatedComputeDeps.withAudit sink.Sink

            let released =
                GatedComputeOutput.release deps (heldOrFail isolatedSpec (cohortJson 40))
                |> Async.RunSynchronously

            match released with
            | Ok cohort ->
                Expect.equal cohort.Shape Count "the shape survives"
                Expect.equal (cohort.Cells |> List.sumBy _.Count) 40 "and so does the cleared cohort"
            | Error e -> failtestf "a 40-strong cohort clears a floor of 10: %A" e

            Expect.hasLength sink.Rows 1 "exactly one gate decision was recorded (GP 6)"
            let row = List.head sink.Rows
            Expect.isTrue row.Released "recorded as a release"
            Expect.equal row.TemplateId "compute-478" "under the composed template"
            Expect.equal row.MethodName "fit-model" "keyed by the work kind, which is the gated method"
            Expect.equal row.CallerPeerId "test-backend" "attributed to the backend that produced it"
            Expect.equal row.RootRequestId (string handle.HandleId) "correlated to the submission by handle id"
        }

        test "a sub-floor output is withheld as a typed error that carries no data" {
            let sink = DecisionSink()

            let deps =
                GatedComputeDeps.create (CleanRoomBroker.create ()) template
                |> GatedComputeDeps.withAudit sink.Sink

            // 3 is below both the suppression threshold (5) and the
            // k-floor (10) — the cell is suppressed and the surviving
            // cohort is 0.
            let withheld =
                GatedComputeOutput.release deps (heldOrFail isolatedSpec (cohortJson 3))
                |> Async.RunSynchronously

            match withheld with
            | Ok cohort -> failtestf "a 3-strong cohort must not clear a floor of 10, got %A" cohort
            | Error(ComputeOutputWithheld templateId) ->
                Expect.equal templateId "compute-478" "the caller learns which template refused, and nothing else"

                // The refusal is a DU with no payload-bearing case, so
                // this cannot fail by construction — which is the point.
                // It is asserted anyway, because "cannot by construction"
                // is exactly the claim a future case addition breaks.
                Expect.isFalse
                    ((sprintf "%A" (ComputeOutputWithheld templateId)).Contains secretMarker)
                    "the refusal carries no trace of the payload"
            | Error other -> failtestf "wrong refusal: %A" other

            let row = List.head sink.Rows
            Expect.isFalse row.Released "the withhold is recorded (GP 6)"

            Expect.isFalse
                (row.Reason.Contains secretMarker)
                "and the receiver-side reason quotes the floor, never the protected cell"
        }

        test "CONTROL — the identical payload clears a floor of zero, so the withhold is the gate's doing" {
            // Without this pairing, "the marker was absent" would pass
            // equally against a pipeline that had broken and returns
            // nothing at all.
            let permissive = {
                template with
                    Floor = {
                        MinCohortSize = 0
                        SuppressionThreshold = 0
                        PermittedShapes = Set.ofList [ Count; Histogram ]
                    }
            }

            let deps = GatedComputeDeps.create (CleanRoomBroker.create ()) permissive

            match
                GatedComputeOutput.release deps (heldOrFail isolatedSpec (cohortJson 3))
                |> Async.RunSynchronously
            with
            | Ok cohort ->
                Expect.equal
                    (cohort.Cells |> List.map _.Label)
                    [ secretMarker ]
                    "the same three-strong cell IS released under a zero floor — so the pipeline works and the floor is what refused"
            | Error e -> failtestf "a zero floor withholds nothing: %A" e
        }

        test "an output whose work kind is off the template surface is withheld — running is not releasing" {
            let sink = DecisionSink()

            let deps =
                GatedComputeDeps.create (CleanRoomBroker.create ()) template
                |> GatedComputeDeps.withAudit sink.Sink

            let offSurface =
                ExternalWorkSpec.create "export-rows" "{}" |> ExternalWorkSpec.isolated

            match
                GatedComputeOutput.release deps (heldOrFail offSurface (cohortJson 40))
                |> Async.RunSynchronously
            with
            | Error(ComputeOutputWithheld _) ->
                Expect.stringContains
                    (List.head sink.Rows).Reason
                    "not on clean-room template"
                    "the work already ran; the template still decides whether its output may be seen"
            | other -> failtestf "an unauthorised work kind must be withheld, got %A" other
        }

        test "an uncheckable output is withheld — row-level worker output FAILS the gate, it does not bypass it" {
            let sink = DecisionSink()

            let deps =
                GatedComputeDeps.create (CleanRoomBroker.create ()) template
                |> GatedComputeDeps.withAudit sink.Sink

            let rows =
                JsonSerializer.Serialize([ $"{secretMarker}-alice"; $"{secretMarker}-bob" ], jsonOptions)

            match
                GatedComputeOutput.release deps (heldOrFail isolatedSpec rows)
                |> Async.RunSynchronously
            with
            | Error(ComputeOutputWithheld _) ->
                Expect.isFalse
                    ((List.head sink.Rows).Reason.Contains secretMarker)
                    "the reason says the shape was uncheckable and quotes none of it"
            | other -> failtestf "a row list is not a gate-checkable CohortResult, got %A" other
        }

        test "the floor binds over a SUBSTITUTED broker that releases below it" {
            // `ICleanRoomBroker` is a replaceable seam (GP 1). The
            // substrate's own release post-condition — inherited whole
            // from Phase 311, because this pipeline dispatches THROUGH
            // the gate rather than re-implementing it — is what stops a
            // substituted mechanism releasing under the composed floor.
            let leaky =
                { new ICleanRoomBroker with
                    member _.Enforce(_, _, _, result) = Released(result, [])
                }

            let sink = DecisionSink()

            let deps =
                GatedComputeDeps.create leaky template |> GatedComputeDeps.withAudit sink.Sink

            match
                GatedComputeOutput.release deps (heldOrFail isolatedSpec (cohortJson 3))
                |> Async.RunSynchronously
            with
            | Error(ComputeOutputWithheld _) ->
                Expect.stringContains
                    (List.head sink.Rows).Reason
                    "does not clear the composed floor"
                    "the substrate overrode the broker and said so"
            | other -> failtestf "a leaky broker must not be able to release below the floor, got %A" other

            // Paired control: the same leaky broker's CONFORMING releases
            // still get through, so the override is a floor and not a
            // blanket refusal.
            match
                GatedComputeOutput.release deps (heldOrFail isolatedSpec (cohortJson 40))
                |> Async.RunSynchronously
            with
            | Ok cohort -> Expect.equal (cohort.Cells |> List.sumBy _.Count) 40 "a conforming release still passes"
            | Error e -> failtestf "the override must not be a blanket refusal: %A" e
        }
    ]