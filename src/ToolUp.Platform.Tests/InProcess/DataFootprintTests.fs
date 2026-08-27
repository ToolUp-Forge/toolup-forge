module ToolUp.Platform.Tests.InProcess.DataFootprintTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.FileProcessor
open ToolUp.Platform.IDataExporter
open ToolUp.Platform.DataFootprintDerivation
open ToolUp.Platform.DataFootprintPreflight

// ─── Phase 433 — component data-footprint manifest ────────────────────
//
// Covers the acceptance shape: a test composition yields the expected
// JOINED footprint, with registered `DataType`s appearing at zero
// per-module effort (the derivation reads the registrations — no code in
// `DataFootprint.fs` names any of these type ids); the PII-coverage rule
// fires on a synthetic gap and names the class + the components that
// persist it; and an undeclared consumer is byte-for-byte unchanged (no
// validator registered, empty surface, empty diff).
//
// The "derived, not hand-declared" property is asserted directly: the
// same derivation over a module that grew one more `DataType` yields one
// more persisted class.

// ── fixtures ──────────────────────────────────────────────────────────

/// A minimal `DataType` whose `Detect` / `Process` are never invoked —
/// the derivation reads its `Id` only.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by the footprint derivation" }
}

let private ordersId = ComponentId.ofModule "orders-service"
let private customersId = ComponentId.ofModule "customers-service"
let private splunkId = ComponentId.forCompanionImpl "IAuditSink" "SplunkHec"
let private uncomposedId = ComponentId.ofModule "not-composed"

/// Two composed modules: one registering a plain business `DataType`, the
/// other registering a type that (as the deployment later declares) holds
/// personal data.
let private ordersModule () =
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withDataTypes [ stubDataType "SalesData" ]

let private customersModule () =
    ServerModule.create "Customers"
    |> ServerModule.withComponentId "customers-service"
    |> ServerModule.withDataTypes [ stubDataType "CustomerProfile" ]

let private referenceModules () = [ ordersModule (); customersModule () ]

let private referenceApp () : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules (referenceModules ())
    |> ServerApp.withAuditSink (InMemoryAuditSink "SplunkHec")

/// The class the deployment DECLARES as personal data — same identity as
/// the derived `CustomerProfile` entity class, with the marker raised.
let private customerProfilePii =
    DataClass.pii "CustomerProfile" EntityClass DataObjectStoreSeam

/// The derived signature with the PII judgement applied — the shape a
/// real composition root builds.
let private classifiedSignature () =
    ofApp (referenceApp ()) |> DataFootprint.reclassify [ customerProfilePii ]

/// A stub exporter / erasure handler pair — the derivation reads their
/// `Name` only.
type private StubExporter(name: string) =
    interface IDataExporter with
        member _.Name = name
        member _.Export(_, _) = async { return [] }

type private StubEraser(name: string) =
    let summary: ErasureSummary = {
        HandlerName = name
        RecordsAffected = 0
        Note = None
    }

    interface IErasureHandler with
        member _.Name = name
        member _.Erase(_, _, _) = async { return Result.Ok summary }
        member _.Preview(_, _, _) = async { return summary }

let private runValidator (v: IConfigValidator) = v.Validate() |> Async.RunSynchronously

let private registeredValidatorNames (services: IServiceCollection) =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
    |> Seq.map (fun d -> (d.ImplementationInstance :?> IConfigValidator).Name)
    |> List.ofSeq

let tests =
    testList "DataFootprint" [

        // ── 433.B — derivation at zero per-module effort ──────────────
        testCase "a registered DataType derives its footprint entry with no per-module declaration"
        <| fun _ ->
            let signature = ofModules (referenceModules ())

            let ordersFootprint = DataFootprint.resolve signature ordersId

            Expect.equal
                (DataFootprint.persistedPii ordersFootprint)
                []
                "a derived class carries no PII claim — that judgement is declared, never inferred"

            Expect.equal
                (ordersFootprint.Persists |> Set.toList |> List.map _.ClassName)
                [ "SalesData" ]
                "the producing module persists the type it registered"

            Expect.equal (ordersFootprint.Writes |> Set.toList |> List.map _.ClassName) [ "SalesData" ] "and writes it"

            Expect.isTrue
                (Set.isEmpty ordersFootprint.Reads)
                "reads are not derived — the registration does not say whether the producer reads back"

            Expect.equal
                (DataFootprint.resolve signature customersId
                 |> _.Persists
                 |> Set.toList
                 |> List.map _.ClassName)
                [ "CustomerProfile" ]
                "each module's own registrations are attributed to its own id"

        testCase "the derivation is not a hand-listed table: one more DataType is one more class"
        <| fun _ ->
            let before = ofModules (referenceModules ())

            let grown =
                ServerModule.create "Orders"
                |> ServerModule.withComponentId "orders-service"
                |> ServerModule.withDataTypes [ stubDataType "SalesData"; stubDataType "RefundData" ]

            let after = ofModules [ grown; customersModule () ]

            Expect.equal
                (DataFootprint.resolve before ordersId |> _.Persists |> Set.count)
                1
                "one registered type before"

            Expect.equal
                (DataFootprint.resolve after ordersId |> _.Persists |> Set.count)
                2
                "two after — with no change to any footprint declaration"

        testCase "a composed audit sink derives an audit-record class under its own multi-impl id"
        <| fun _ ->
            let signature = ofApp (referenceApp ())
            let sink = DataFootprint.resolve signature splunkId

            Expect.equal
                (sink.Persists
                 |> Set.toList
                 |> List.map (fun c -> c.ClassName, c.ClassKind, c.Seam))
                [ "SplunkHec", AuditRecordClass, AuditSinkSeam ]
                "the sink persists audit records behind the audit seam, keyed by its Name (never its position)"

        testCase "a declared re-classification supersedes the derived one rather than duplicating it"
        <| fun _ ->
            let signature = classifiedSignature ()
            let customers = DataFootprint.resolve signature customersId

            Expect.equal (Set.count customers.Persists) 1 "the class is reported once, not twice"

            Expect.equal
                (DataFootprint.persistedPii customers |> List.map _.ClassName)
                [ "CustomerProfile" ]
                "and it now carries the declared PII marker"

        testCase "a declared companion footprint folds on top of the derived half"
        <| fun _ ->
            let blobs = ComponentId.forCompanionSlot "IBlobStorage"

            let declared =
                DataFootprint.none blobs
                |> DataFootprint.withPersist (DataClass.pii "uploads" BlobClass BlobStorageSeam)
                |> List.singleton
                |> DataFootprint.signatureOf

            let signature = DataFootprint.mergeSignature (ofApp (referenceApp ())) declared

            Expect.equal
                (DataFootprint.resolve signature blobs
                 |> DataFootprint.persistedPii
                 |> List.map _.ClassName)
                [ "uploads" ]
                "a companion declares what no registration can see"

            Expect.equal
                (DataFootprint.resolve signature ordersId |> _.Persists |> Set.count)
                1
                "and the derived half is untouched"

        // ── 433.C — the composition join ──────────────────────────────
        testCase "the join answers 'what is stored, where' without reading source"
        <| fun _ ->
            let app = referenceApp ()

            let composed =
                overManifest (ServerApp.compositionManifest app) (classifiedSignature ())

            Expect.equal
                (DataFootprint.persistedClasses composed |> List.map _.ClassName)
                [ "CustomerProfile"; "SalesData"; "SplunkHec" ]
                "every persisted class in one deterministic list"

            Expect.equal
                (DataFootprint.seams composed)
                [ AuditSinkSeam; DataObjectStoreSeam ]
                "and every store seam the composition touches"

            Expect.equal
                (DataFootprint.classesBySeam DataObjectStoreSeam composed |> List.map _.ClassName)
                [ "CustomerProfile"; "SalesData" ]
                "queryable by store"

            Expect.equal
                (DataFootprint.classesOfKind AuditRecordClass composed |> List.map _.ClassName)
                [ "SplunkHec" ]
                "queryable by data class kind"

            Expect.equal
                (DataFootprint.persistedPiiClasses composed |> List.map _.ClassName)
                [ "CustomerProfile" ]
                "queryable by PII flag"

            Expect.equal
                (DataFootprint.componentsPersisting customerProfilePii (classifiedSignature ()))
                [ customersId ]
                "and back through the signature, queryable by component"

        testCase "the join restricts to what the manifest actually composed"
        <| fun _ ->
            let stale =
                DataFootprint.none uncomposedId
                |> DataFootprint.withPersist (DataClass.pii "GhostData" EntityClass DataObjectStoreSeam)
                |> List.singleton
                |> DataFootprint.signatureOf

            let signature = DataFootprint.mergeSignature (classifiedSignature ()) stale
            let app = referenceApp ()
            let composed = overManifest (ServerApp.compositionManifest app) signature

            Expect.isFalse
                (DataFootprint.persistedClasses composed
                 |> List.exists (fun c -> c.ClassName = "GhostData"))
                "a declaration for an uncomposed component cannot inflate the composition's surface"

        testCase "the join is associative, commutative and idempotent (the Phase 296 shape)"
        <| fun _ ->
            let cls name =
                DataClass.create name EntityClass DataObjectStoreSeam

            let a = DataFootprint.none ordersId |> DataFootprint.withPersist (cls "A")
            let b = DataFootprint.none ordersId |> DataFootprint.withPersist (cls "B")
            let c = DataFootprint.none ordersId |> DataFootprint.withWrite (cls "C")

            Expect.equal
                (DataFootprint.join (DataFootprint.join a b) c)
                (DataFootprint.join a (DataFootprint.join b c))
                "associative"

            Expect.equal (DataFootprint.join a b) (DataFootprint.join b a) "commutative"
            Expect.equal (DataFootprint.join a a) a "idempotent"

            Expect.equal
                (DataFootprint.join a (DataFootprint.none ordersId))
                a
                "the empty footprint is the join identity — an undeclared component never changes the join"

        // ── 433.C — the Phase 286 diff projection ─────────────────────
        testCase "a component that starts persisting PII surfaces in the diff"
        <| fun _ ->
            let before = ofApp (referenceApp ())
            let after = classifiedSignature ()

            let delta = DataFootprint.diff before after

            Expect.isFalse (DataFootprint.isEmptyDelta delta) "a PII re-classification is a reviewable change"

            Expect.isTrue
                (delta.AccessAdded |> List.exists (fun (_, _, cls) -> cls.ContainsPii))
                "the added access carries the PII marker"

            Expect.stringContains
                (DataFootprint.renderDelta delta)
                "PII"
                "and the readable failure says so in the message itself"

        testCase "the diff is order-independent and the wire projection round-trips"
        <| fun _ ->
            let signature = classifiedSignature ()

            Expect.isTrue
                (DataFootprint.isEmptyDelta (DataFootprint.diff signature signature))
                "a signature is identical to itself"

            let back = DataFootprint.toWire signature |> DataFootprint.ofWire

            Expect.isTrue
                (DataFootprint.isEmptyDelta (DataFootprint.diff signature back))
                "toWire -> ofWire preserves the footprint structurally"

            Expect.equal
                (DataFootprint.renderDelta DataFootprint.emptyDelta)
                "(no data-footprint differences)"
                "an empty delta renders as such"

        // ── 433.D — the DSR/offboarding completeness check ────────────
        testCase "the PII-coverage rule fires on a synthetic gap and names the class + its persisters"
        <| fun _ ->
            let signature = classifiedSignature ()

            let found = DsrCoverage.gaps Set.empty [] signature

            Expect.hasLength found 1 "exactly the one persisted PII class is a gap"
            Expect.equal found.Head.GapClass.ClassName "CustomerProfile" "the gap names the class"
            Expect.equal found.Head.PersistedBy [ customersId ] "and the component that persists it"

            let reported = defects defaultUncoveredSeverity Set.empty [] signature

            Expect.hasLength reported 1 "one defect"
            Expect.equal reported.Head.RuleCode PiiUncoveredRule "under the uncovered-PII rule code"

            Expect.stringContains reported.Head.Message "CustomerProfile" "the message names the class"

            Expect.stringContains
                reported.Head.Message
                (ComponentId.value customersId)
                "and the component whose store it sits in"

        testCase "a class covered by a composed DSR path is not a gap"
        <| fun _ ->
            let app =
                referenceApp ()
                |> ServerApp.withDataExporter (StubExporter "customer-profile-export")
                |> ServerApp.withErasureHandler (StubEraser "customer-profile-erase")

            let available = dsrPathNames app

            let claims = [
                DsrCoverage.create customerProfilePii [ "customer-profile-export" ] [ "customer-profile-erase" ]
            ]

            Expect.equal
                (DsrCoverage.gaps available claims (classifiedSignature ()))
                []
                "a claim naming paths the deployment composes closes the gap"

            Expect.equal
                (defects defaultUncoveredSeverity available claims (classifiedSignature ()))
                []
                "and the rule reports nothing"

        testCase "a claim matches by class identity, so a later PII marking does not orphan it"
        <| fun _ ->
            let app =
                referenceApp ()
                |> ServerApp.withErasureHandler (StubEraser "customer-profile-erase")

            // The claim was written against the class BEFORE it was marked PII.
            let claims = [
                DsrCoverage.create (DataClass.create "CustomerProfile" EntityClass DataObjectStoreSeam) [] [
                    "customer-profile-erase"
                ]
            ]

            Expect.equal
                (DsrCoverage.gaps (dsrPathNames app) claims (classifiedSignature ()))
                []
                "identity is name + seam, so re-classifying the class keeps the claim attached"

        testCase "a declared exemption closes the gap and says why"
        <| fun _ ->
            let claims = [
                DsrCoverage.exempt customerProfilePii "pseudonymised at rest; the subject key is held only in the IDP"
            ]

            Expect.equal
                (DsrCoverage.gaps Set.empty claims (classifiedSignature ()))
                []
                "an exemption is a complete claim"

            Expect.equal
                (defects defaultUncoveredSeverity Set.empty claims (classifiedSignature ()))
                []
                "so nothing is reported"

        testCase "a claim naming an uncomposed path is stale, and a class covered only by it is still a gap"
        <| fun _ ->
            let claims = [ DsrCoverage.create customerProfilePii [] [ "renamed-away-erase" ] ]

            let available = dsrPathNames (referenceApp ())

            Expect.equal
                (DsrCoverage.staleClaims available claims |> List.map snd)
                [ [ "renamed-away-erase" ] ]
                "the stale path is named"

            let reported =
                defects defaultUncoveredSeverity available claims (classifiedSignature ())

            Expect.hasLength reported 2 "both rules fire — the class is uncovered AND the claim is stale"

            Expect.isTrue
                (reported |> List.exists (fun d -> d.RuleCode = StaleClaimRule))
                "the stale-claim rule reports the unresolved path"

            Expect.isTrue
                (reported |> List.exists (fun d -> d.RuleCode = PiiUncoveredRule))
                "and the class is still uncovered — a stale claim asserts coverage that is not there"

        testCase "a class with no PII marker is never a coverage gap"
        <| fun _ ->
            Expect.equal
                (DsrCoverage.gaps Set.empty [] (ofApp (referenceApp ())))
                []
                "the derived, unclassified composition reports nothing until a class is declared PII"

        // ── the preflight validator ───────────────────────────────────
        testCase "the coverage validator fails at the configured severity and is structural-class"
        <| fun _ ->
            let signature = classifiedSignature ()

            let warning =
                DataFootprintCoverageValidator(defaultUncoveredSeverity, Set.empty, [], signature) :> IConfigValidator

            Expect.equal
                (ValidationResult.status (runValidator warning))
                "Warning"
                "the default reports and continues — a staged DSR pipeline is a legitimate shape"

            let fatal =
                DataFootprintCoverageValidator(DefectError, Set.empty, [], signature) :> IConfigValidator

            Expect.equal
                (ValidationResult.status (runValidator fatal))
                "Error"
                "a deployment that wants DSR completeness to be a boot gate says so at registration"

            Expect.equal
                (ConfigValidatorAggregator.classify warning)
                ConfigValidatorAggregator.StructuralClass
                "an in-memory coverage sweep is not what SkipPreflight exists to bypass"

            Expect.isTrue
                (ConfigValidatorAggregator.alwaysRuns (ConfigValidatorAggregator.classify warning))
                "so it runs regardless of SkipPreflight"

        testCase "the rules are exported in the Phase 294 / Phase 585 vocabularies"
        <| fun _ ->
            Expect.equal
                (ruleManifest |> List.map _.Code)
                [ PiiUncoveredRule; StaleClaimRule ]
                "both rule codes are introspectable"

            Expect.equal
                (classifiedRuleManifest |> List.map _.Class)
                [ StructuralRule; StructuralRule ]
                "both are structural — no external dependency to be down"

            Expect.equal
                (classifiedRuleManifest |> List.map _.Code)
                (ruleManifest |> List.map _.Code)
                "the two projections read the same declared rules, so they cannot diverge"

        // ── GP 11 / GP 13 — an undeclared consumer is unchanged ───────
        testCase "an undeclared consumer registers no validator and composes an empty surface"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let before = services.Count

            let after =
                serviceRegistration defaultUncoveredSeverity Set.empty [] DataFootprint.emptySignature services

            Expect.equal
                after.Count
                before
                "the ServerApp.empty base case composes a byte-for-byte identical service collection"

            Expect.equal
                (DataFootprint.compose DataFootprint.emptySignature)
                DataFootprint.emptyComposition
                "and the empty signature composes to the empty surface"

            Expect.isTrue
                (DataFootprint.isEmptyDelta (
                    DataFootprint.diff DataFootprint.emptySignature DataFootprint.emptySignature
                ))
                "which diffs to nothing"

        testCase "a derived-but-unclassified composition still registers nothing"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let app = referenceApp ()

            serviceRegistrationForApp app [] (ofApp app) services |> ignore

            Expect.equal
                (registeredValidatorNames services)
                []
                "nothing persisted is flagged PII and nothing is claimed — there is nothing to check (GP 13)"

        testCase "a composition with a persisted PII class registers exactly one validator"
        <| fun _ ->
            let services = ServiceCollection() :> IServiceCollection
            let app = referenceApp ()

            serviceRegistrationForApp app [] (classifiedSignature ()) services |> ignore

            Expect.equal (registeredValidatorNames services) [ ValidatorName ] "the coverage validator, and only it"
    ]