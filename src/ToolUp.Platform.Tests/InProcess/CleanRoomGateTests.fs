module ToolUp.Platform.Tests.InProcess.CleanRoomGateTests

open System
open System.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 311 — the structurally-enforced clean-room gate ───────────
//
// Phase 18b's broker was a mechanism nothing called. This pack is about
// the claim that replaced it: that a contract composed with
// `withCleanRoomTemplate` has the floor applied *by the substrate*, so a
// handler that never touches the broker cannot release row-level data.
//
// Four kinds of case, in the order they carry weight:
//
//   1. **The bypass does not exist.** A reflection sweep over the public
//      surface asserting `CleanRoomGate.wrap` is the ONLY route to a
//      `GatedContractRegistration`, and that it demands a broker and a
//      template. This is the one assertion that generalises: every case
//      below shows that one particular path is gated, and only this one
//      shows there is no other path.
//   2. **The gate does the work, not the handler.** Every withhold case
//      runs a handler that makes no broker call at all, and is paired
//      with a NEGATIVE CONTROL registering the identical handler
//      ungated — which returns the unprotected answer verbatim. Without
//      that half, "the sub-k answer was withheld" would pass equally
//      against a dispatch that had broken and started refusing
//      everything.
//   3. **The floor survives a substituted broker.** `ICleanRoomBroker`
//      is a replaceable seam, so a deliberately leaky implementation is
//      run through the gate to show the substrate's own post-condition
//      overrides it — paired with a control proving the same leaky
//      broker's *conforming* releases still get through, so the override
//      is a floor and not a blanket refusal.
//   4. **Nothing else moved (GP 11 / GP 13).** An ungated contract is
//      the same registration value it was before the phase, and a
//      composition that gates nothing runs no probe.

// ─── Fixtures ────────────────────────────────────────────────────────

/// NOT `private`: `JsonRpcPeerHost.contract` reflects via
/// `FSharpType.IsRecord` without the private-representation flag, so a
/// `private` record reads back as a non-record and is rejected.
type ReachContract = {
    /// Answers in the gate-checkable shape and makes NO broker call —
    /// the handler this phase is about.
    EstimateReach: string -> Async<CohortResult>
    /// Same, for the histogram shape.
    Histogram: string -> Async<CohortResult>
    /// Answers with row-level data in a shape the gate cannot evaluate.
    ExportRows: string -> Async<string list>
}

let private cell label count : PrivacyCell = {
    Label = label
    Count = count
    Value = None
}

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Histogram ]
}

let private template: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach"; "Histogram" ]
    Floor = floor
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

[<Literal>]
let private contractId = "example.reach"

/// The rows a leaking handler would hand back — used as the negative
/// control's payload so a leak is unmistakable in a failure message.
let private rows = [ "alice@example.com"; "bob@example.com" ]

/// Tracks whether the handler ran at all, so surface enforcement can be
/// shown to refuse *before* the sensitive computation, not after it.
type private HandlerProbe() =
    let mutable invoked = 0
    member _.Invocations = invoked
    member _.Ran() = invoked <- invoked + 1

let private implWith (probe: HandlerProbe) (reach: CohortResult) (histogram: CohortResult) : ReachContract = {
    EstimateReach =
        fun _ -> async {
            probe.Ran()
            return reach
        }
    Histogram =
        fun _ -> async {
            probe.Ran()
            return histogram
        }
    ExportRows =
        fun _ -> async {
            probe.Ran()
            return rows
        }
}

let private countOf n = {
    Shape = Count
    Cells = [ cell "all" n ]
}

let private histogramOf cells = { Shape = Histogram; Cells = cells }

/// The raw (ungated) registration for an implementation — what
/// `withContract` alone produces.
let private rawRegistration (impl: ReachContract) =
    (JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None impl).Registration

let private callContext: PeerCallContext = {
    Peer = {
        PeerId = "buyer"
        DisplayName = "Buyer"
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ "buyer" ]
    RootRequestId = "root-311"
    ParentRequestId = None
    HopsRemaining = 4
}

/// Collects the gate's audit payloads so a decision can be asserted as a
/// recorded row rather than inferred from the wire answer.
type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

/// Dispatch one call through a registration, exactly as
/// `DefaultPlatformPeer` would.
let private dispatch (registration: PeerContractRegistration) (methodName: string) (argsJson: string) =
    registration.Dispatch callContext methodName argsJson |> Async.RunSynchronously

let private gated (broker: ICleanRoomBroker) (sink: DecisionSink) (impl: ReachContract) =
    (CleanRoomGate.wrap broker template sink.Sink (rawRegistration impl)).Registration

let private defaultBroker = CleanRoomBroker.create ()

/// A deliberately leaky substitute: it releases whatever it is given,
/// suppressing nothing. Stands in for any bespoke `ICleanRoomBroker` a
/// deployment might compose — the seam exists precisely so one can be.
type private LeakyBroker() =
    interface ICleanRoomBroker with
        member _.Enforce(_, _, _, result) = Released(result, [])

// ─── 1. There is no ungated route ────────────────────────────────────

let structuralTests =
    testList "Phase 311 — the bypass does not exist" [

        test
            "CleanRoomGate.wrap is the only public route to a GatedContractRegistration, and it demands a broker + a template" {
            // The generalising assertion. Every other case here shows one
            // path is gated; this one shows there is no second path — a
            // future helper that minted a "gated" registration without
            // installing the gate fails HERE, at the point it is written,
            // rather than silently in whatever deployment composes it.
            let assembly = typeof<GatedContractRegistration>.Assembly
            let target = typeof<GatedContractRegistration>

            let producers =
                assembly.GetExportedTypes()
                |> Array.collect (fun t ->
                    let methods =
                        t.GetMethods(BindingFlags.Public ||| BindingFlags.Static ||| BindingFlags.Instance)
                        |> Array.filter (fun m -> m.ReturnType = target)
                        |> Array.map (fun m ->
                            $"{t.FullName}.{m.Name}", m.GetParameters() |> Array.map _.ParameterType)

                    let ctors =
                        if t = target then
                            t.GetConstructors(BindingFlags.Public ||| BindingFlags.Instance)
                            |> Array.map (fun c ->
                                $"{t.FullName}..ctor", c.GetParameters() |> Array.map _.ParameterType)
                        else
                            [||]

                    Array.append methods ctors)

            // Non-vacuity: a sweep that found nothing would "pass".
            Expect.isNonEmpty producers "the sweep must find at least the wrap function, else it proves nothing"

            let unguarded =
                producers
                |> Array.filter (fun (_, parameterTypes) ->
                    not (
                        Array.contains typeof<ICleanRoomBroker> parameterTypes
                        && Array.contains typeof<CleanRoomTemplate> parameterTypes
                    ))

            Expect.isEmpty
                (unguarded |> Array.map fst)
                "every public route to a GatedContractRegistration must take an ICleanRoomBroker AND a CleanRoomTemplate — a route without both could mint the witness without installing the gate"

            Expect.contains
                (producers |> Array.map fst)
                "ToolUp.InterPlatform.CleanRoomGate.wrap"
                "wrap is the sanctioned route and must be among the producers found"
        }

        test "the witness cannot be constructed from a bare registration outside the gate" {
            // The union case's factory must not be publicly reachable: if
            // `NewGated` were public, any caller could label an ungated
            // registration as gated and the type would prove nothing.
            let publicCaseFactories =
                typeof<GatedContractRegistration>.GetMethods(BindingFlags.Public ||| BindingFlags.Static)
                |> Array.filter (fun m -> m.Name.StartsWith("New", StringComparison.Ordinal))
                |> Array.map _.Name

            Expect.isEmpty
                publicCaseFactories
                "GatedContractRegistration's representation must stay private — a public case factory is an ungated route to the witness"
        }

        test "CONTROL — the wrapped registration is readable, so the witness is usable" {
            let sink = DecisionSink()

            let witness =
                CleanRoomGate.wrap
                    defaultBroker
                    template
                    sink.Sink
                    (rawRegistration (implWith (HandlerProbe()) (countOf 50) (countOf 50)))

            Expect.equal witness.Registration.ContractId contractId "the gated registration keeps its contract id"
            Expect.equal witness.Registration.Versions [ v1 ] "…and its versions"
        }
    ]

// ─── 2. The gate does the work, not the handler ──────────────────────

let enforcementTests =
    testList "Phase 311 — the substrate gates an unco-operative handler" [

        test "a sub-k answer from a handler that never calls the broker is withheld" {
            let probe = HandlerProbe()
            let sink = DecisionSink()
            // 7 < k = 10, and the handler does nothing about it.
            let registration = gated defaultBroker sink (implWith probe (countOf 7) (countOf 7))

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld id) -> Expect.equal id template.TemplateId "the refusal names the template"
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "A sub-k cohort reached the wire: %s" payload

            Expect.equal
                probe.Invocations
                1
                "the handler did run — the gate acted on its answer, it did not merely block the call"
        }

        test "NEGATIVE CONTROL — the identical handler, ungated, returns the sub-k answer verbatim" {
            // The gate removed. This is what makes the case above mean
            // something: the ONLY thing that withheld the answer was the
            // wrapper.
            let registration =
                rawRegistration (implWith (HandlerProbe()) (countOf 7) (countOf 7))

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Ok payload ->
                Expect.stringContains
                    payload
                    "7"
                    "an ungated contract answers with the unprotected cohort — the exposure Phase 311 closes"
            | Error e -> failtestf "The ungated control must succeed, else the gated case proves nothing; got %A" e
        }

        test "an answer whose shape the gate does not permit is withheld" {
            let probe = HandlerProbe()
            let sink = DecisionSink()

            // Aggregate is not in the template floor's permitted set.
            let offShape = {
                Shape = Aggregate
                Cells = [ cell "all" 500 ]
            }

            let registration = gated defaultBroker sink (implWith probe offShape offShape)

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "An off-shape answer reached the wire: %s" payload
        }

        test "a method off the template surface is withheld, and the handler never runs" {
            let probe = HandlerProbe()
            let sink = DecisionSink()

            let registration =
                gated defaultBroker sink (implWith probe (countOf 500) (countOf 500))

            match dispatch registration "ExportRows" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "An off-surface method answered: %s" payload

            Expect.equal
                probe.Invocations
                0
                "surface enforcement must refuse before the handler computes anything — an off-template query is not work this contract does over sensitive data"
        }

        test "an answer that is not gate-checkable at all is withheld, not passed through" {
            // `ExportRows` is off-surface, so reach the checkability arm
            // through an ON-surface method whose answer is row-level. The
            // template allows `Histogram`; the implementation answers it
            // with a row list.
            let leakyImpl: ReachContract = {
                EstimateReach = fun _ -> async { return countOf 500 }
                Histogram = fun _ -> async { return countOf 500 }
                ExportRows = fun _ -> async { return rows }
            }

            let raw = rawRegistration leakyImpl

            // Substitute a dispatch that answers `Histogram` with rows —
            // the shape a handler produces when it was never written to
            // the gate's contract in the first place.
            let rowAnswering: PeerContractRegistration = {
                raw with
                    Dispatch = fun _ _ _ -> async { return Ok(JsonRpc.serialize rows) }
            }

            let sink = DecisionSink()

            let registration =
                (CleanRoomGate.wrap defaultBroker template sink.Sink rowAnswering).Registration

            match dispatch registration "Histogram" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "Row-level data reached the wire through a gated method: %s" payload

            match sink.Rows with
            | [ row ] ->
                Expect.isFalse row.Released "the decision row records a withhold"
                Expect.stringContains row.Reason "gate-checkable" "…naming why the floor could not be evaluated"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }

        test "a within-floor answer is released with sub-threshold cells dropped" {
            let probe = HandlerProbe()
            let sink = DecisionSink()

            let answer =
                histogramOf [ cell "big1" 40; cell "small1" 3; cell "big2" 30; cell "small2" 1 ]

            let registration = gated defaultBroker sink (implWith probe (countOf 50) answer)

            match dispatch registration "Histogram" "[\"any\"]" with
            | Ok payload ->
                let released = JsonRpc.deserialize<CohortResult> payload

                Expect.equal
                    (released.Cells |> List.map _.Label)
                    [ "big1"; "big2" ]
                    "only cells at or above the suppression threshold ride back"

                Expect.equal released.Shape Histogram "the released shape is unchanged"
            | Error e -> failtestf "A within-floor answer must release, got %A" e

            match sink.Rows with
            | [ row ] ->
                Expect.isTrue row.Released "the decision row records a release"

                Expect.equal
                    (List.sort row.SuppressedCells)
                    [ "small1"; "small2" ]
                    "the suppressed labels are recorded for audit"

                Expect.equal row.RootRequestId callContext.RootRequestId "…under the derived correlation id"
                Expect.equal row.CallerPeerId callContext.Peer.PeerId "…attributed to the validated caller"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }

        test "a withhold records the broker's reason receiver-side and sends none of it on the wire" {
            let sink = DecisionSink()

            let registration =
                gated defaultBroker sink (implWith (HandlerProbe()) (countOf 7) (countOf 7))

            let result = dispatch registration "EstimateReach" "[\"any\"]"

            match sink.Rows with
            | [ row ] ->
                Expect.stringContains row.Reason "k-anonymity floor" "the audit row carries the broker's own diagnosis"
                Expect.stringContains row.Reason "7" "…including the quantitative detail an operator needs"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)

            match result with
            | Error e ->
                let wire = JsonRpc.serialize (JsonRpc.failure "id" e)

                Expect.isFalse
                    (wire.Contains "7")
                    "the wire refusal must not disclose the sub-k cohort — a caller that can vary its query and read the reason back has a counting oracle over the protected data"

                Expect.stringContains wire template.TemplateId "…while still naming the template that refused"
            | Ok payload -> failtestf "Expected a refusal, got %s" payload
        }

        test "a dispatch that already failed passes through unchanged and records nothing" {
            let sink = DecisionSink()
            let raw = rawRegistration (implWith (HandlerProbe()) (countOf 50) (countOf 50))

            let failing: PeerContractRegistration = {
                raw with
                    Dispatch = fun _ _ _ -> async { return Error(PeerHandler "boom") }
            }

            let registration =
                (CleanRoomGate.wrap defaultBroker template sink.Sink failing).Registration

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Error(PeerHandler message) ->
                Expect.equal message "boom" "the handler's own error is not rewritten as a gate refusal"
            | Error e -> failtestf "Expected the original PeerHandler, got %A" e
            | Ok payload -> failtestf "Expected the original failure, got %s" payload

            Expect.isEmpty sink.Rows "nothing was produced, so there is no gate decision to record"
        }

        test "an audit sink that throws does not turn a released answer into a failure" {
            let throwing: PeerCleanRoomDecisionPayload -> Async<unit> =
                fun _ -> async { return failwith "audit sink is down" }

            let registration =
                (CleanRoomGate.wrap
                    defaultBroker
                    template
                    throwing
                    (rawRegistration (implWith (HandlerProbe()) (countOf 50) (countOf 50))))
                    .Registration

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Ok _ -> ()
            | Error e -> failtestf "Audit is best-effort; a broken sink must not fail the call, got %A" e
        }
    ]

// ─── 3. The floor survives a substituted broker ──────────────────────

let substitutionTests =
    testList "Phase 311 — the composed floor binds on a substituted broker" [

        test "a broker that releases below the floor is overridden by the substrate" {
            let sink = DecisionSink()
            let leaky = LeakyBroker() :> ICleanRoomBroker
            // 7 < k = 10, and this broker releases it happily.
            let registration =
                gated leaky sink (implWith (HandlerProbe()) (countOf 7) (countOf 7))

            match dispatch registration "EstimateReach" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload ->
                failtestf
                    "A substituted broker released below the composed floor and the substrate let it through: %s"
                    payload

            match sink.Rows with
            | [ row ] ->
                Expect.isFalse row.Released "the override is recorded as a withhold"

                Expect.stringContains
                    row.Reason
                    "does not clear the composed floor"
                    "…naming the substrate override, not the broker's own reason"
            | other -> failtestf "Expected exactly one decision row, got %i" (List.length other)
        }

        test "a broker that leaves sub-threshold cells in place is overridden" {
            let sink = DecisionSink()
            let leaky = LeakyBroker() :> ICleanRoomBroker
            // Total 74 clears k = 10, but the 3-cell is below the
            // suppression threshold of 5 and this broker dropped nothing.
            let answer = histogramOf [ cell "big" 71; cell "small" 3 ]
            let registration = gated leaky sink (implWith (HandlerProbe()) (countOf 50) answer)

            match dispatch registration "Histogram" "[\"any\"]" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | Error e -> failtestf "Expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "A sub-threshold cell rode back through a substituted broker: %s" payload
        }

        test "CONTROL — the same substituted broker's conforming releases still get through" {
            // Without this, the two cases above would pass against a
            // post-condition that refused everything, and the gate would
            // be a wall rather than a floor.
            let sink = DecisionSink()
            let leaky = LeakyBroker() :> ICleanRoomBroker
            let answer = histogramOf [ cell "big1" 40; cell "big2" 30 ]
            let registration = gated leaky sink (implWith (HandlerProbe()) (countOf 50) answer)

            match dispatch registration "Histogram" "[\"any\"]" with
            | Ok payload ->
                let released = JsonRpc.deserialize<CohortResult> payload

                Expect.equal
                    (released.Cells |> List.map _.Label)
                    [ "big1"; "big2" ]
                    "a conforming release is untouched by the post-condition"
            | Error e -> failtestf "A conforming release must get through, got %A" e
        }

        test "PrivacyGate.observed reports the strictest gate a result satisfies" {
            let result = histogramOf [ cell "a" 40; cell "b" 12 ]
            let seen = PrivacyGate.observed result

            Expect.equal seen.MinCohortSize 52 "the released cohort is the sum of the cells"
            Expect.equal seen.SuppressionThreshold 12 "the observed threshold is the smallest cell"
            Expect.equal seen.PermittedShapes (Set.singleton Histogram) "…and the only shape it has"
            Expect.isTrue (PrivacyGate.isStricterOrEqual floor seen) "this result clears the floor on every axis"

            let below = histogramOf [ cell "a" 40; cell "b" 2 ]

            Expect.isFalse
                (PrivacyGate.isStricterOrEqual floor (PrivacyGate.observed below))
                "a sub-threshold cell fails the floor even when the total cohort clears k"
        }
    ]

// ─── 4. Composition: an inert gate cannot ship ───────────────────────

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
}

let private appHosting () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<ReachContract>
            contractId
            [ v1 ]
            fusion
            (implWith (HandlerProbe()) (countOf 50) (countOf 50)))

let compositionTests =
    testList "Phase 311 — composition posture" [

        test "a template naming a contract this deployment does not host is reported" {
            let app =
                appHosting () |> PeerServerApp.withCleanRoomTemplate "example.typo" template

            Expect.equal
                (PeerServerApp.auditCleanRoomTemplates app)
                [ "example.typo" ]
                "an unbound template is a gate that looks composed and never runs — exactly Phase 330's inert-VerifyDelegation shape"
        }

        test "CONTROL — a template bound to a hosted contract reports nothing" {
            let app = appHosting () |> PeerServerApp.withCleanRoomTemplate contractId template
            Expect.isEmpty (PeerServerApp.auditCleanRoomTemplates app) "a correctly-bound template is not a defect"
        }

        test "enforceCleanRoomTemplates refuses an unbound template and is silent on a bound one" {
            let bad =
                appHosting () |> PeerServerApp.withCleanRoomTemplate "example.typo" template

            Expect.throws
                (fun () -> PeerServerApp.enforceCleanRoomTemplates bad)
                "a composition whose privacy gate would never run must not start"

            let good = appHosting () |> PeerServerApp.withCleanRoomTemplate contractId template
            PeerServerApp.enforceCleanRoomTemplates good
        }

        test "the audit-transparency contract counts as hosted" {
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withPeerAuditTransparency
                |> PeerServerApp.withCleanRoomTemplate PeerAudit.contractId template

            Expect.isEmpty
                (PeerServerApp.auditCleanRoomTemplates app)
                "the reserved audit contract is registered, so a template on it binds"
        }

        test "the last template composed for a contract id wins" {
            let stricter = {
                template with
                    TemplateId = "reach-v2"
            }

            let app =
                appHosting ()
                |> PeerServerApp.withCleanRoomTemplate contractId template
                |> PeerServerApp.withCleanRoomTemplate contractId stricter

            let resolved = PeerServerApp.cleanRoomTemplateMap app

            Expect.equal
                (Map.find contractId resolved).TemplateId
                "reach-v2"
                "last-wins, matching withContract's re-registration rule"
        }

        test "GP 13 — a composition that gates nothing declares nothing and runs no probe" {
            let mutable builderRuns = 0

            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withConfig enabledConfig
                |> PeerServerApp.withContract (fun fusion ->
                    builderRuns <- builderRuns + 1

                    JsonRpcPeerHost.contract<ReachContract>
                        contractId
                        [ v1 ]
                        fusion
                        (implWith (HandlerProbe()) (countOf 50) (countOf 50)))

            let fresh = PeerServerApp.create ()
            Expect.isEmpty fresh.CleanRoomTemplates "a fresh compose record gates nothing"
            Expect.isEmpty (PeerServerApp.auditCleanRoomTemplates app) "…and reports nothing"

            Expect.equal
                builderRuns
                0
                "materialising the contract table costs a builder pass, so a deployment that gates nothing must not pay for it"
        }

        test "GP 13 — the strip-imports path reports nothing even with a template composed" {
            let app =
                PeerServerApp.create ()
                |> PeerServerApp.withCleanRoomTemplate "example.typo" template

            Expect.isEmpty
                (PeerServerApp.auditCleanRoomTemplates app)
                "NoPeerSubstrate registers no contracts at all, so the short-circuit stays byte-for-byte a bare ServerApp.run"
        }
    ]

// ─── Wire mapping ────────────────────────────────────────────────────

let wireTests =
    testList "Phase 311 — PeerCleanRoomWithheld on the wire" [

        test "the refusal maps to its own JSON-RPC code and case name" {
            let err = PeerCleanRoomWithheld "reach"

            Expect.equal
                (JsonRpc.errorCode err)
                JsonRpc.cleanRoomWithheld
                "a distinct code, not a reuse of handlerError"

            Expect.notEqual
                JsonRpc.cleanRoomWithheld
                JsonRpc.handlerError
                "a privacy refusal and a handler fault have different remedies and must be distinguishable"

            Expect.equal (JsonRpc.errorCaseName err) "PeerCleanRoomWithheld" "the audit outcome label is the case name"
        }

        test "the human-readable message names the template and nothing quantitative" {
            let message = JsonRpc.errorMessage (PeerCleanRoomWithheld "reach")
            Expect.stringContains message "reach" "the caller learns which gate refused"
            Expect.stringContains message "withheld" "…and that it was a withhold"
        }
    ]