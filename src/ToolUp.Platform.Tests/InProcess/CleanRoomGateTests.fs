module ToolUp.Platform.Tests.InProcess.CleanRoomGateTests

open System
open System.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform.PeerAuditEvents
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.InProcess.PeerTestComposition
open ToolUp.InterPlatform.PeerCompose
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

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
    Release = ReleaseTerms.none
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
        MultiInstance = {
            ServerConfig.defaults.MultiInstance with
                PeerSubstrate = FeatureMode.Enabled
        }
}

let private appHosting () =
    PeerComposition.create ()
    |> PeerComposition.app (ServerApp.withConfig enabledConfig)
    |> PeerComposition.options (
        PeerOptions.withContract (fun fusion ->
            JsonRpcPeerHost.contract<ReachContract>
                contractId
                [ v1 ]
                fusion
                (implWith (HandlerProbe()) (countOf 50) (countOf 50)))
    )

let compositionTests =
    testList "Phase 311 — composition posture" [

        test "a template naming a contract this deployment does not host is reported" {
            let app =
                appHosting ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate "example.typo" template)

            Expect.equal
                (PeerCompose.auditCleanRoomTemplates app)
                [ "example.typo" ]
                "an unbound template is a gate that looks composed and never runs — exactly Phase 330's inert-VerifyDelegation shape"
        }

        test "CONTROL — a template bound to a hosted contract reports nothing" {
            let app =
                appHosting ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate contractId template)

            Expect.isEmpty (PeerCompose.auditCleanRoomTemplates app) "a correctly-bound template is not a defect"
        }

        test "enforceCleanRoomTemplates refuses an unbound template and is silent on a bound one" {
            let bad =
                appHosting ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate "example.typo" template)

            Expect.throws
                (fun () -> PeerCompose.enforceCleanRoomTemplates bad)
                "a composition whose privacy gate would never run must not start"

            let good =
                appHosting ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate contractId template)

            PeerCompose.enforceCleanRoomTemplates good
        }

        test "the audit-transparency contract counts as hosted" {
            let app =
                PeerComposition.create ()
                |> PeerComposition.app (ServerApp.withConfig enabledConfig)
                |> PeerComposition.options (PeerOptions.withPeerAuditTransparency)
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate PeerAudit.contractId template)

            Expect.isEmpty
                (PeerCompose.auditCleanRoomTemplates app)
                "the reserved audit contract is registered, so a template on it binds"
        }

        test "the last template composed for a contract id wins" {
            let stricter = {
                template with
                    TemplateId = "reach-v2"
            }

            let app =
                appHosting ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate contractId template)
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate contractId stricter)

            let resolved = PeerCompose.cleanRoomTemplateMap (snd app)

            Expect.equal
                (Map.find contractId resolved).TemplateId
                "reach-v2"
                "last-wins, matching withContract's re-registration rule"
        }

        test "GP 13 — a composition that gates nothing declares nothing and runs no probe" {
            let mutable builderRuns = 0

            let app =
                PeerComposition.create ()
                |> PeerComposition.app (ServerApp.withConfig enabledConfig)
                |> PeerComposition.options (
                    PeerOptions.withContract (fun fusion ->
                        builderRuns <- builderRuns + 1

                        JsonRpcPeerHost.contract<ReachContract>
                            contractId
                            [ v1 ]
                            fusion
                            (implWith (HandlerProbe()) (countOf 50) (countOf 50)))
                )

            let fresh = PeerComposition.create ()
            Expect.isEmpty (snd fresh).CleanRoomTemplates "a fresh compose record gates nothing"
            Expect.isEmpty (PeerCompose.auditCleanRoomTemplates app) "…and reports nothing"

            Expect.equal
                builderRuns
                0
                "materialising the contract table costs a builder pass, so a deployment that gates nothing must not pay for it"
        }

        test "GP 13 — the strip-imports path reports nothing even with a template composed" {
            let app =
                PeerComposition.create ()
                |> PeerComposition.options (PeerOptions.withCleanRoomTemplate "example.typo" template)

            Expect.isEmpty
                (PeerCompose.auditCleanRoomTemplates app)
                "FeatureMode.Disabled registers no contracts at all, so the short-circuit stays byte-for-byte a bare ServerApp.run"
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

// ─── 5. The differencing checks: the complement and the near duplicate ──
//
// Two answers that each clear the floor can still give a member away: by
// a complement (a cell whose split holds three people who are not in it),
// or by subtraction (two filters differing by one respondent). Each case
// below is paired with the control that shows the check, and nothing else,
// did the withholding.

/// A survey holder's contract: one table per named filter, answered with
/// the splits and filter cohort the differencing checks read.
type SurveyContract = {
    Table: string -> Async<DeclaredCohort>
}

let private surveyId = "example.survey"

let private respondents (first: int) (last: int) = [ for i in first..last -> $"respondent-{i}" ]

let private gridCell (row: string) (column: string) (count: int) =
    match CellLabel.grid row column with
    | Ok label -> cell label count
    | Error reason -> failwith reason

/// Yes/no by men/women. Among the 60 men, 52 said yes: the 8 who said no
/// are the complement of the men's yes cell.
let private table = {
    Shape = Histogram
    Cells = [
        gridCell "yes" "men" 52
        gridCell "no" "men" 8
        gridCell "yes" "women" 30
        gridCell "no" "women" 30
    ]
}

let private columns = [
    {
        Key = "column"
        Base = 60
        Cells = [ "yes␟men"; "no␟men" ]
    }
    {
        Key = "column"
        Base = 60
        Cells = [ "yes␟women"; "no␟women" ]
    }
]

let private surveyTemplate: CleanRoomTemplate = {
    TemplateId = "survey"
    AllowedMethods = Set.ofList [ "Table" ]
    Floor = floor
    Release = ReleaseTerms.none
}

let private complementTemplate = {
    surveyTemplate with
        Release.Complement = Some(ComplementRule.Within "column")
}

/// Answer the filter a call names with `answers`.
let private surveyRegistration (answers: Map<string, DeclaredCohort>) =
    let impl: SurveyContract = {
        Table = fun filter -> async { return answers[filter] }
    }

    (JsonRpcPeerHost.contract<SurveyContract> surveyId [ v1 ] None impl).Registration

let private contextOf (peerId: string) = {
    callContext with
        Peer = {
            PeerId = peerId
            DisplayName = peerId
        }
}

let private ask (registration: PeerContractRegistration) (peerId: string) (filter: string) =
    registration.Dispatch (contextOf peerId) "Table" (JsonRpc.serialize [ filter ])
    |> Async.RunSynchronously

let private at = DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero)

let private windowMeter () =
    ReleaseWindowMeter.create (InMemoryReleaseWindow()) ReleaseWindowPolicy.defaults
    |> ReleaseWindowMeter.withClock (fun () -> at)

/// A budget whose withhold policy returns a withheld answer's ε, so a
/// charged refusal shows as one the policy alone would not have charged.
let private freeWithholds () =
    let ledger = InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger

    let policy =
        PrivacyBudgetPolicy.create 100m 1m
        |> PrivacyBudgetPolicy.withWithholdCharge WithholdFree

    ledger,
    policy,
    PrivacyBudgetMeter.create ledger policy
    |> PrivacyBudgetMeter.withClock (fun () -> at)

let private differenced
    (template: CleanRoomTemplate)
    (budget: PrivacyBudgetMeter option)
    (window: ReleaseWindowMeter option)
    (sink: DecisionSink)
    (answers: Map<string, DeclaredCohort>)
    =
    (CleanRoomGate.wrapDifferenced defaultBroker template None budget None window sink.Sink (surveyRegistration answers))
        .Registration

/// A table over the respondents `first`..`last`.
let private tableOver (first: int) (last: int) =
    DeclaredCohort.ofResult table
    |> DeclaredCohort.withFilter (FilterCohort.ofMembers (respondents first last))

let private releasedLabels (json: string) =
    (JsonRpc.deserialize<CohortResult> json).Cells |> List.map _.Label

let differencingTests =
    testList "Phase 1278 — the complement rule and the near-duplicate filter" [

        test "a cell whose complement is below the floor is withheld while the cell itself clears it" {
            let sink = DecisionSink()

            let answers =
                Map.ofList [
                    "all",
                    {
                        DeclaredCohort.ofResult table with
                            Splits = columns
                    }
                ]

            match ask (differenced complementTemplate None None sink answers) "buyer" "all" with
            | Ok json ->
                let labels = releasedLabels json
                Expect.isFalse (List.contains "yes␟men" labels) "52 clears the floor, but its complement is 8"
                Expect.contains labels "no␟men" "8 clears the suppression threshold and its complement is 52"
                Expect.contains labels "yes␟women" "the women's cells stand"
                Expect.isFalse (json.Contains "column") "the splits never reach the wire"
            | Error e -> failtestf "the answer clears the floor once the cell is withheld; got %A" e

            let row = List.exactlyOne sink.Rows
            Expect.isTrue row.Released "the answer was released"
            Expect.contains row.SuppressedCells "yes␟men" "the withheld cell is on the audit row"
            Expect.stringContains row.Reason "ComplementBelowFloor" "the verdict is recorded"
            Expect.isFalse (row.Reason.Contains "52") "the verdict never names a count"
        }

        test "CONTROL — the same answer under a template declaring no complement check releases the cell" {
            let sink = DecisionSink()

            let answers =
                Map.ofList [
                    "all",
                    {
                        DeclaredCohort.ofResult table with
                            Splits = columns
                    }
                ]

            match ask (differenced surveyTemplate None None sink answers) "buyer" "all" with
            | Ok json -> Expect.contains (releasedLabels json) "yes␟men" "the floor alone releases 52"
            | Error e -> failtestf "the control must release; got %A" e
        }

        test "an answer declaring no split under the template's key is withheld" {
            let sink = DecisionSink()
            let answers = Map.ofList [ "all", DeclaredCohort.ofResult table ]

            match ask (differenced complementTemplate None None sink answers) "buyer" "all" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "a complement check with nothing to read must withhold; got %A" other

            Expect.stringContains (List.exactlyOne sink.Rows).Reason "declares no split" "the reason is recorded"
        }

        test "a filter differing by one respondent from a released one is refused, logged and charged" {
            let sink = DecisionSink()
            let ledger, policy, meter = freeWithholds ()

            let answers =
                Map.ofList [ "everyone", tableOver 1 120; "all-but-one", tableOver 2 120 ]

            let registration =
                differenced surveyTemplate (Some meter) (Some(windowMeter ())) sink answers

            match ask registration "buyer" "everyone" with
            | Ok _ -> ()
            | Error e -> failtestf "the first table must be released; got %A" e

            match ask registration "buyer" "all-but-one" with
            | Error(PeerCleanRoomWithheld id) -> Expect.equal id "survey" "the caller learns only that it was withheld"
            | other -> failtestf "one respondent's difference must be refused; got %A" other

            let refusal = sink.Rows |> List.last
            Expect.isFalse refusal.Released "the refusal is a recorded decision"
            Expect.stringContains refusal.Reason "NearDuplicateFilter" "…naming the check"

            Expect.stringContains
                refusal.Reason
                (FilterCohort.ofMembers (respondents 1 120)).MembershipHash
                "…and the earlier cohort, by its hash"

            Expect.isFalse (refusal.Reason.Contains "respondent-") "no member is named"

            let budget =
                ledger.RemainingBudget(PrivacyBudgetPolicy.scopeFor "survey" "buyer" at policy, 100m)
                |> Async.RunSynchronously

            Expect.equal budget.QueryCount 2 "the release and the refusal are both charged"

            Expect.equal
                budget.EpsilonCommitted
                2m
                "the refusal is charged though this policy returns a withheld answer's epsilon"
        }

        test "CONTROL — under the same policy an ordinary withhold is not charged" {
            let sink = DecisionSink()
            let ledger, policy, meter = freeWithholds ()

            let small = {
                DeclaredCohort.ofResult (countOf 7) with
                    Filter = Some(FilterCohort.ofMembers (respondents 1 120))
            }

            let registration =
                differenced surveyTemplate (Some meter) (Some(windowMeter ())) sink (Map.ofList [ "small", small ])

            match ask registration "buyer" "small" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "a sub-k cohort is withheld by the floor; got %A" other

            let budget =
                ledger.RemainingBudget(PrivacyBudgetPolicy.scopeFor "survey" "buyer" at policy, 100m)
                |> Async.RunSynchronously

            Expect.equal budget.QueryCount 0 "the floor's withhold is returned under WithholdFree"
        }

        test "a filter differing by the floor or more is answered" {
            let sink = DecisionSink()

            let answers = Map.ofList [ "everyone", tableOver 1 120; "fewer", tableOver 11 120 ]

            let registration =
                differenced surveyTemplate None (Some(windowMeter ())) sink answers

            for filter in [ "everyone"; "fewer" ] do
                match ask registration "buyer" filter with
                | Ok _ -> ()
                | Error e -> failtestf "'%s' differs by ten, the floor, and must be answered; got %A" filter e
        }

        test "a filter cohort smaller than the floor is refused" {
            let sink = DecisionSink()

            let registration =
                differenced surveyTemplate None (Some(windowMeter ())) sink (Map.ofList [ "few", tableOver 1 9 ])

            match ask registration "buyer" "few" with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "nine respondents are below the floor; got %A" other

            Expect.stringContains (List.exactlyOne sink.Rows).Reason "FilterBelowFloor" "the verdict is recorded"
        }

        test
            "with a window composed, an answer declaring no filter cohort is withheld; a template that opts out is not compared" {
            let answers = Map.ofList [ "bare", DeclaredCohort.ofResult table ]

            match
                ask (differenced surveyTemplate None (Some(windowMeter ())) (DecisionSink()) answers) "buyer" "bare"
            with
            | Error(PeerCleanRoomWithheld _) -> ()
            | other -> failtestf "an answer the check cannot read must be withheld; got %A" other

            let optedOut = {
                surveyTemplate with
                    Release.NearDuplicate = NearDuplicateRule.NotCompared "a published table, one filter"
            }

            let both =
                Map.ofList [ "everyone", tableOver 1 120; "all-but-one", tableOver 2 120 ]

            let registration =
                differenced optedOut None (Some(windowMeter ())) (DecisionSink()) both

            for filter in [ "everyone"; "all-but-one" ] do
                match ask registration "buyer" filter with
                | Ok _ -> ()
                | Error e -> failtestf "an opted-out template is not compared; '%s' got %A" filter e
        }

        test "per counterparty, another counterparty's release is not compared" {
            let answers =
                Map.ofList [ "everyone", tableOver 1 120; "all-but-one", tableOver 2 120 ]

            let registration =
                differenced surveyTemplate None (Some(windowMeter ())) (DecisionSink()) answers

            match ask registration "buyer" "everyone", ask registration "rival" "all-but-one" with
            | Ok _, Ok _ -> ()
            | first, second -> failtestf "the default scope is per counterparty; got %A and %A" first second

            let perVintage = {
                surveyTemplate with
                    Release.NearDuplicate = NearDuplicateRule.Compared DifferencingScope.PerVintage
            }

            let onVintage first last =
                tableOver first last
                |> fun d -> {
                    d with
                        Filter = d.Filter |> Option.map (FilterCohort.withVintage "wave-3")
                }

            let vintaged =
                Map.ofList [ "everyone", onVintage 1 120; "all-but-one", onVintage 2 120 ]

            let shared =
                differenced perVintage None (Some(windowMeter ())) (DecisionSink()) vintaged

            match ask shared "buyer" "everyone", ask shared "rival" "all-but-one" with
            | Ok _, Error(PeerCleanRoomWithheld _) -> ()
            | first, second ->
                failtestf "per vintage, the rival's near duplicate is refused; got %A and %A" first second
        }

        test "a composition without the option releases the bytes it released before" {
            let sink = DecisionSink()
            let answer = histogramOf [ cell "a" 20; cell "b" 3; cell "c" 30 ]

            let registration =
                (CleanRoomGate.wrap
                    defaultBroker
                    template
                    sink.Sink
                    (rawRegistration (implWith (HandlerProbe()) answer answer)))
                    .Registration

            match dispatch registration "Histogram" "[\"any\"]" with
            | Ok json ->
                Expect.equal
                    json
                    (JsonRpc.serialize {
                        answer with
                            Cells = [ cell "a" 20; cell "c" 30 ]
                    })
                    "the release is the surviving cells serialised exactly as before"
            | Error e -> failtestf "the control must release; got %A" e

            let row = List.exactlyOne sink.Rows
            Expect.equal row.Reason "" "the decision row's reason is unchanged"
            Expect.equal row.SuppressedCells [ "b" ] "…and so is its suppression trail"

            let canonical (t: CleanRoomTemplate) =
                Text.Encoding.UTF8.GetString(ToolUp.InterPlatform.TemplateCanonical.templateBytes t)

            let naming = {
                template with
                    Release.NamesWithheld = true
            }

            Expect.isTrue
                ((canonical naming).EndsWith("14:withheld:named\n", StringComparison.Ordinal))
                "a template declaring no differencing term encodes nothing after its last pre-existing field"
        }

        test "a template with a differencing option changes its canonical hash, so approval is re-sought" {
            let version = ToolUp.InterPlatform.TemplateCanonical.version

            let variants = [
                surveyTemplate
                complementTemplate
                {
                    surveyTemplate with
                        Release.Complement = Some(ComplementRule.Within "row")
                }
                {
                    surveyTemplate with
                        Release.NearDuplicate = NearDuplicateRule.Compared DifferencingScope.PerVintage
                }
                {
                    surveyTemplate with
                        Release.NearDuplicate = NearDuplicateRule.NotCompared "one reason"
                }
                {
                    surveyTemplate with
                        Release.NearDuplicate = NearDuplicateRule.NotCompared "another reason"
                }
            ]

            Expect.equal
                (variants |> List.map version |> List.distinct |> List.length)
                (List.length variants)
                "every differencing term, and an opt-out's reason, is covered by the hash an approval binds to"
        }
    ]