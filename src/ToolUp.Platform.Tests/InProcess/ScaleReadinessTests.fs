module ToolUp.Platform.Tests.InProcess.ScaleReadinessTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 434 — composition scale-readiness planner ──────────────────
//
// Covers the acceptance shape: an all-in-memory composition reports
// `SingleInstanceOnly` with the right per-component attributions (the
// dev-only companion is named, the distributed-ready one is not);
// swapping the distributed companion in flips the verdict; and the
// preflight gate fires ONLY when the deployment's own topology
// declaration asks for more than one instance.
//
// The unblock suggestions (434.B) are asserted against a PINNED
// vocabulary rather than `ComposableSurface.slots ()`, so the assertions
// say something about the derivation and not about whichever slots this
// build happens to reflect. One test does go through the live vocabulary,
// to pin that the join against real slot ids resolves at all.

// ── fixtures ──────────────────────────────────────────────────────────

/// Two companion slots, standing in for the Phase 293 vocabulary: one
/// single-impl (the scheduler-shaped slot) and one multi-impl (the
/// sink-shaped slot), so both cardinality arms of the unblock suggestion
/// are exercised.
let private schedulerSlot: ComposableSlot = {
    Slot = ComponentId.forCompanionSlot "IJobScheduler"
    Interface = "IJobScheduler"
    Cardinality = SingleImpl
    SubstrateRequirements = []
}

let private sinkSlot: ComposableSlot = {
    Slot = ComponentId.forCompanionSlot "IAuditSink"
    Interface = "IAuditSink"
    Cardinality = MultiImpl
    SubstrateRequirements = [ "ISecretStore" ]
}

let private vocabulary: ComposableSlot list = [ schedulerSlot; sinkSlot ]

let private schedulerId = ComponentId.forCompanionSlot "IJobScheduler"
let private channelId = ComponentId.forCompanionSlot "INotificationChannel"
let private sinkImplId = ComponentId.forCompanionImpl "IAuditSink" "local-archive"
let private moduleId = ComponentId.ofModule "reports"

/// A composition of one module, two single-impl companion slots and one
/// multi-impl audit-sink implementation.
let private manifest: CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("reports", moduleId) ]
        [
            CompositionManifest.companionSlotEntry "IJobScheduler"
            CompositionManifest.companionSlotEntry "INotificationChannel"
            CompositionManifest.companionImplEntry "IAuditSink" "local-archive"
        ]
        [ CompositionManifest.dataTypeEntry "sales" ] [] []

/// Every companion in the composition declared dev-only — the in-memory
/// reference shape a fresh deployment composes.
let private allInMemory: ScaleDeclarations = {
    ScaleDeclarations.empty with
        Capabilities =
            Map.ofList [
                schedulerId, CompanionCapability.devOnlyEffecting
                channelId, CompanionCapability.devOnlyEffecting
                sinkImplId, CompanionCapability.devOnlyEffecting
            ]
}

/// The same composition with distributed-ready companions swapped in.
let private allDistributed: ScaleDeclarations = {
    ScaleDeclarations.empty with
        Capabilities =
            Map.ofList [
                schedulerId, CompanionCapability.distributedEffecting
                channelId, CompanionCapability.distributedEffecting
                sinkImplId, CompanionCapability.distributedEffecting
            ]
}

let private assess declarations =
    ScaleReadiness.assessWith vocabulary declarations manifest

let private scaleOfComponent (report: ScaleReport) (componentId: ComponentId) =
    report.Findings
    |> List.tryFind (fun f -> f.Component = componentId)
    |> Option.map _.Scale

let private multiInstance n = ScaleIntent.MultiInstance n

let private runValidator (v: IConfigValidator) = v.Validate() |> Async.RunSynchronously

let private registeredValidatorNames (services: IServiceCollection) =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
    |> Seq.map (fun d -> (d.ImplementationInstance :?> IConfigValidator).Name)
    |> List.ofSeq

let tests =
    testList "ScaleReadiness" [

        // ── 434.A — the readiness join ────────────────────────────────

        testCase "an all-in-memory composition is SingleInstanceOnly with per-component attributions"
        <| fun _ ->
            let report = assess allInMemory

            Expect.equal
                report.Verdict
                SingleInstanceOnly
                "one dev-only component makes the whole composition single-instance"

            Expect.equal
                (scaleOfComponent report schedulerId)
                (Some SingleInstanceOnly)
                "the dev-only scheduler slot is attributed"

            Expect.equal
                (scaleOfComponent report sinkImplId)
                (Some SingleInstanceOnly)
                "so is the dev-only multi-impl sink, by its own sub-id rather than its slot"

            Expect.equal
                (scaleOfComponent report moduleId)
                (Some MultiInstanceSafe)
                "a component that declared nothing is not blamed — it resolves to the identity (GP 11)"

            Expect.equal
                (ScaleReadiness.limitingFindings report |> List.map _.Component |> List.sort)
                ([ schedulerId; channelId; sinkImplId ] |> List.sort)
                "exactly the three declared dev-only components limit the verdict"

        testCase "swapping in the distributed companions flips the verdict"
        <| fun _ ->
            Expect.equal (assess allInMemory).Verdict SingleInstanceOnly "before the swap"

            let after = assess allDistributed

            Expect.equal after.Verdict MultiInstanceSafe "after the swap"
            Expect.isEmpty (ScaleReadiness.limitingFindings after) "and nothing is left limiting it"
            Expect.isEmpty (ScaleReadiness.unblockLines after) "so there is nothing to suggest"

        testCase "one dev-only component among distributed ones still sinks the verdict"
        <| fun _ ->
            let mostlyDistributed = {
                allDistributed with
                    Capabilities =
                        allDistributed.Capabilities
                        |> Map.add sinkImplId CompanionCapability.devOnlyEffecting
            }

            let report = assess mostlyDistributed

            Expect.equal report.Verdict SingleInstanceOnly "SingleInstanceOnly absorbs in the meet"

            Expect.equal
                (ScaleReadiness.limitingFindings report |> List.map _.Component)
                [ sinkImplId ]
                "and only the offending component is named"

        testCase "an undeclared composition is MultiInstanceSafe throughout"
        <| fun _ ->
            let report = assess ScaleDeclarations.empty

            Expect.equal
                report.Verdict
                MultiInstanceSafe
                "nothing declared means nothing asserted to the contrary (GP 11)"

            Expect.all
                report.Findings
                (fun f -> f.Scale = MultiInstanceSafe)
                "every component resolves to CompanionCapability.identity"

            Expect.equal
                (report.Findings |> List.length)
                (CompositionManifest.allComponents manifest |> List.length)
                "and every composed unit is still enumerated"

        testCase "the empty manifest assesses to MultiInstanceSafe with no findings"
        <| fun _ ->
            let report = ScaleReadiness.assess CompositionManifest.empty

            Expect.equal report.Verdict MultiInstanceSafe "a composition of nothing constrains nothing"
            Expect.isEmpty report.Findings "and has nothing to attribute"

        // ── 434.A — the MultiInstanceWith middle case ─────────────────

        testCase "an unsatisfied distributed prerequisite yields MultiInstanceWith naming it"
        <| fun _ ->
            let uncomposed = ComponentId.forCompanionSlot "IDistributedLock"

            let declarations = {
                allDistributed with
                    Prerequisites = Map.ofList [ schedulerId, [ uncomposed ] ]
            }

            let report = assess declarations

            Expect.equal
                (scaleOfComponent report schedulerId)
                (Some(MultiInstanceWith [ uncomposed ]))
                "the prerequisite is not composed, so it is reported as needed"

            Expect.equal
                report.Verdict
                (MultiInstanceWith [ uncomposed ])
                "and the meet carries the need up to the composition verdict"

        testCase "a prerequisite that is composed and distributed-ready is satisfied"
        <| fun _ ->
            let declarations = {
                allDistributed with
                    Prerequisites = Map.ofList [ schedulerId, [ channelId ] ]
            }

            Expect.equal
                (scaleOfComponent (assess declarations) schedulerId)
                (Some MultiInstanceSafe)
                "the prerequisite is composed and declared DistributedReady — nothing outstanding"

        testCase "a prerequisite that is composed but dev-only is NOT satisfied"
        <| fun _ ->
            let declarations = {
                allInMemory with
                    Capabilities =
                        allInMemory.Capabilities
                        |> Map.add schedulerId CompanionCapability.distributedEffecting
                    Prerequisites = Map.ofList [ schedulerId, [ channelId ] ]
            }

            Expect.equal
                (scaleOfComponent (assess declarations) schedulerId)
                (Some(MultiInstanceWith [ channelId ]))
                "being present is not enough — a dev-only prerequisite cannot coordinate across instances"

        testCase "a SingleInstanceOnly component does not report prerequisites"
        <| fun _ ->
            let declarations = {
                allInMemory with
                    Prerequisites = Map.ofList [ schedulerId, [ ComponentId.forCompanionSlot "IDistributedLock" ] ]
            }

            Expect.equal
                (scaleOfComponent (assess declarations) schedulerId)
                (Some SingleInstanceOnly)
                "the fix is swapping this component, not composing a companion it names"

        // ── the meet is a semilattice ─────────────────────────────────

        testCase "MultiInstanceSafe is the meet identity and SingleInstanceOnly absorbs"
        <| fun _ ->
            let needs = MultiInstanceWith [ channelId ]

            for scale in [ MultiInstanceSafe; needs; SingleInstanceOnly ] do
                Expect.equal
                    (ComponentScale.meet MultiInstanceSafe scale)
                    scale
                    "the identity leaves the other side alone"

                Expect.equal (ComponentScale.meet scale MultiInstanceSafe) scale "on both sides"
                Expect.equal (ComponentScale.meet scale scale) scale "and the meet is idempotent"

                Expect.equal
                    (ComponentScale.meet SingleInstanceOnly scale)
                    SingleInstanceOnly
                    "SingleInstanceOnly absorbs everything"

            Expect.equal (ComponentScale.meetAll []) MultiInstanceSafe "an empty meet is the identity"

        testCase "the meet is commutative and unions prerequisites"
        <| fun _ ->
            let a = MultiInstanceWith [ schedulerId ]
            let b = MultiInstanceWith [ channelId ]

            Expect.equal (ComponentScale.meet a b) (ComponentScale.meet b a) "order does not matter"

            Expect.equal
                (ComponentScale.meet a b |> ComponentScale.needs |> List.sort)
                ([ schedulerId; channelId ] |> List.sort)
                "and both sides' needs survive the union"

        testCase "an empty prerequisite list normalises to MultiInstanceSafe"
        <| fun _ ->
            Expect.equal
                (ComponentScale.ofNeeds [])
                MultiInstanceSafe
                "MultiInstanceWith [] never exists as an alias for the identity"

            Expect.equal
                (ComponentScale.ofNeeds [ channelId; schedulerId ])
                (ComponentScale.ofNeeds [ schedulerId; channelId; channelId ])
                "and construction deduplicates + orders, so equality is total"

        // ── 434.B — unblock suggestions ───────────────────────────────

        testCase "a scale-limiting companion slot names the swap that would lift it"
        <| fun _ ->
            let report = assess allInMemory

            let unblock =
                report.Findings |> List.find (fun f -> f.Component = schedulerId) |> _.Unblock

            Expect.isSome unblock "the vocabulary knows this slot, so a swap can be named"

            let text = Option.get unblock

            Expect.stringContains text "IJobScheduler" "the suggestion names the slot's interface"
            Expect.stringContains text "DistributedReady" "and the posture the replacement must declare"

            Expect.stringContains text "at most one implementation" "a single-impl slot is described as a replacement"

        testCase "a multi-impl slot's suggestion says the dev-only implementation must be removed too"
        <| fun _ ->
            let text =
                assess allInMemory
                |> _.Findings
                |> List.find (fun f -> f.Component = sinkImplId)
                |> _.Unblock
                |> Option.get

            Expect.stringContains text "IAuditSink" "the multi-impl slot is resolved from the impl's own sub-id"
            Expect.stringContains text "REMOVED" "adding a distributed sink alongside a dev-only one is not enough"
            Expect.stringContains text "ISecretStore" "and the slot's declared substrate requirement rides along"

        testCase "a component the vocabulary knows no slot for gets no fabricated suggestion"
        <| fun _ ->
            let declarations = {
                ScaleDeclarations.empty with
                    Capabilities = Map.ofList [ moduleId, CompanionCapability.devOnlyEffecting ]
            }

            let finding =
                assess declarations |> _.Findings |> List.find (fun f -> f.Component = moduleId)

            Expect.equal finding.Scale SingleInstanceOnly "the module is scale-limiting"
            Expect.isNone finding.Unblock "but no companion swap exists for a module — an honest absence"

            Expect.stringContains
                (ScaleReadiness.unblockLines (assess declarations) |> List.exactlyOne)
                "no slot for it"
                "and the report line still mentions it rather than dropping it"

        testCase "a component NOT scale-limiting carries no suggestion"
        <| fun _ ->
            Expect.all (assess allDistributed).Findings (fun f -> f.Unblock.IsNone) "there is nothing to unblock"

        testCase "the live Phase 293 vocabulary resolves a real slot id"
        <| fun _ ->
            // Goes through `ComposableSurface.slots ()` rather than the
            // pinned fixture, to pin that the id join works against ids
            // this build actually derives.
            let liveSlot = ComposableSurface.slots () |> List.head

            let liveManifest =
                CompositionManifest.build [] [ CompositionManifest.companionSlotEntry liveSlot.Interface ] [] [] []

            let declarations = {
                ScaleDeclarations.empty with
                    Capabilities = Map.ofList [ liveSlot.Slot, CompanionCapability.devOnlyEffecting ]
            }

            let finding =
                ScaleReadiness.assessDeclared declarations liveManifest
                |> _.Findings
                |> List.exactlyOne

            Expect.equal finding.Scale SingleInstanceOnly "the declared dev-only posture is read"

            Expect.isSome finding.Unblock "and the derived vocabulary resolves the slot, so a swap is nameable"

        // ── 434.C — the intent knob ───────────────────────────────────

        testCase "the intent is read from the ServerConfig fields that already carry it"
        <| fun _ ->
            Expect.equal
                (ScaleReadiness.intentOf ServerConfig.defaults)
                ScaleIntent.SingleInstance
                "the default topology is a single instance"

            Expect.equal
                (ScaleReadiness.intentOf {
                    ServerConfig.defaults with
                        ReplicaCount = 4
                })
                (multiInstance 4)
                "ReplicaCount is the declared instance count"

            Expect.equal
                (ScaleReadiness.intentOf {
                    ServerConfig.defaults with
                        ServerlessHost = ServerlessHost
                })
                ScaleIntent.Serverless
                "a serverless host profile wins over the ReplicaCount default"

            Expect.equal
                (ScaleReadiness.intentOf {
                    ServerConfig.defaults with
                        ServerlessHost = ServerlessHost
                        ReplicaCount = 3
                })
                ScaleIntent.Serverless
                "and over an explicit count — there is no stable instance at all"

        testCase "a single-instance intent is satisfied by every verdict"
        <| fun _ ->
            for verdict in [ MultiInstanceSafe; MultiInstanceWith [ channelId ]; SingleInstanceOnly ] do
                Expect.isTrue
                    (ScaleReadiness.satisfies ScaleIntent.SingleInstance verdict)
                    "an in-memory composition on one instance is correct, not defective"

            Expect.isTrue
                (ScaleReadiness.satisfies (multiInstance 1) SingleInstanceOnly)
                "and ReplicaCount = 1 is the same declaration"

        testCase "a concurrent intent requires MultiInstanceSafe, and MultiInstanceWith does not satisfy it"
        <| fun _ ->
            for intent in [ multiInstance 2; ScaleIntent.Serverless ] do
                Expect.isTrue
                    (ScaleReadiness.satisfies intent MultiInstanceSafe)
                    "a fully distributed composition passes"

                Expect.isFalse
                    (ScaleReadiness.satisfies intent SingleInstanceOnly)
                    "a dev-only composition cannot serve concurrency"

                Expect.isFalse
                    (ScaleReadiness.satisfies intent (MultiInstanceWith [ channelId ]))
                    "and the prerequisites it names are by construction NOT composed"

        // ── 434.C — the gate fires only when the knob is set ──────────

        testCase "the gate is silent on the default topology, however bad the composition"
        <| fun _ ->
            let report = assess allInMemory

            Expect.equal report.Verdict SingleInstanceOnly "the composition is single-instance-only"

            Expect.isEmpty
                (ScaleReadinessPreflight.defects ScaleIntent.SingleInstance report)
                "yet the gate has nothing to say — the deployment declared one instance (GP 11)"

        testCase "the gate fires with names when the deployment declares more than one instance"
        <| fun _ ->
            let defect =
                ScaleReadinessPreflight.defects (multiInstance 3) (assess allInMemory)
                |> List.exactlyOne

            Expect.equal defect.RuleCode ScaleReadinessPreflight.IntentUnsatisfiableRule "the stable rule code"
            Expect.equal defect.Severity DefectError "fail-fast, not a warning"
            Expect.stringContains defect.Message "ReplicaCount = 3" "the operator's own declaration is quoted back"
            Expect.stringContains defect.Message "IJobScheduler" "and every limiting component is named"
            Expect.stringContains defect.Message "IAuditSink" "including the multi-impl one"
            Expect.stringContains defect.Message "DistributedReady" "with the swap that would lift it"

        testCase "the gate fires for a serverless host profile too"
        <| fun _ ->
            let defect =
                ScaleReadinessPreflight.defects ScaleIntent.Serverless (assess allInMemory)
                |> List.exactlyOne

            Expect.stringContains defect.Message "serverless" "the intent is named"

        testCase "the gate passes once the distributed companions are composed"
        <| fun _ ->
            Expect.isEmpty
                (ScaleReadinessPreflight.defects (multiInstance 3) (assess allDistributed))
                "the swap that flips the verdict also clears the gate"

        testCase "an unsatisfiable defect maps to a preflight Error"
        <| fun _ ->
            let result =
                ScaleReadinessPreflight.defects (multiInstance 2) (assess allInMemory)
                |> ScaleReadinessPreflight.toValidationResult

            match result with
            | Error _ -> ()
            | other -> failtestf "expected a preflight Error, got %A" other

            Expect.equal (ScaleReadinessPreflight.toValidationResult []) Ok "and a clean sweep is Ok"

        // ── 434.C — registration (GP 13) ──────────────────────────────

        testCase "no validator is registered on the default topology"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let before = services.Count

            let after =
                ScaleReadinessPreflight.serviceRegistrationForConfig ServerConfig.defaults allInMemory manifest services

            Expect.equal
                after.Count
                before
                "a deployment on the defaults composes a byte-for-byte identical service collection (GP 11 / GP 13)"

            Expect.isEmpty (registeredValidatorNames services) "and registers nothing"

        testCase "exactly one validator is registered when the topology is declared"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection

            ScaleReadinessPreflight.serviceRegistrationForConfig
                {
                    ServerConfig.defaults with
                        ReplicaCount = 2
                }
                allInMemory
                manifest
                services
            |> ignore

            Expect.equal
                (registeredValidatorNames services)
                [ ScaleReadinessPreflight.ValidatorName ]
                "the scale-readiness validator, and only it"

        testCase "the registered validator refuses the composition and is structural-class"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection

            ScaleReadinessPreflight.serviceRegistrationForConfig
                {
                    ServerConfig.defaults with
                        ReplicaCount = 2
                }
                allInMemory
                manifest
                services
            |> ignore

            let validator =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
                |> Seq.map (fun d -> d.ImplementationInstance :?> IConfigValidator)
                |> Seq.exactlyOne

            match runValidator validator with
            | Error message ->
                Expect.stringContains message ScaleReadinessPreflight.IntentUnsatisfiableRule "rule-tagged"
            | other -> failtestf "expected the validator to refuse the composition, got %A" other

            Expect.isTrue
                (validator :? IStructuralClassValidator)
                "SkipPreflight must not switch off a pure in-memory topology check"

        testCase "a declared topology the composition can serve registers a validator that passes"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection

            ScaleReadinessPreflight.serviceRegistrationForConfig
                {
                    ServerConfig.defaults with
                        ReplicaCount = 2
                }
                allDistributed
                manifest
                services
            |> ignore

            let validator =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
                |> Seq.map (fun d -> d.ImplementationInstance :?> IConfigValidator)
                |> Seq.exactlyOne

            Expect.equal (runValidator validator) Ok "the gate is registered but has nothing to refuse"

        // ── Phase 294 / 585 — the exported rule manifest ──────────────

        testCase "the rule manifest exports exactly one append-only rule in the Phase 294 vocabulary"
        <| fun _ ->
            let rule = ScaleReadinessPreflight.ruleManifest |> List.exactlyOne

            Expect.equal rule.Code ScaleReadinessPreflight.IntentUnsatisfiableRule "the stable code"
            Expect.equal rule.Severity DefectError "the declared severity"
            Expect.isNotEmpty rule.Description "with a description an external checker can render"

        testCase "the classified projection cannot diverge from the rule manifest"
        <| fun _ ->
            Expect.equal
                (ScaleReadinessPreflight.classifiedRuleManifest |> List.map _.Code)
                (ScaleReadinessPreflight.ruleManifest |> List.map _.Code)
                "the two projections read the same declared rule"

            Expect.all
                ScaleReadinessPreflight.classifiedRuleManifest
                (fun r -> r.Class = StructuralRule)
                "the check reaches nothing outside the process, so it is structural"

        testCase "the rule code does not collide with another family's"
        <| fun _ ->
            let others =
                (CompositionValidator.ruleManifest
                 @ EventTopologyPreflight.ruleManifest
                 @ DataFootprintPreflight.ruleManifest)
                |> List.map _.Code

            for code in ScaleReadinessPreflight.ruleManifest |> List.map _.Code do
                Expect.isFalse (List.contains code others) (sprintf "rule code '%s' is unique across families" code)
    ]