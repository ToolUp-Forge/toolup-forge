// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.CompositionInspectorHandlerTests

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 593 — composition inspector handler ───────────────────────
//
// Four properties are pinned, and each is one the handler ADDS over the
// declarations it projects:
//
//   1. **The role gate, per METHOD.** Anonymous and Member refused,
//      Owner / Admin and single-scope callers admitted. Per method
//      rather than once, for the reason `AuditViewApiHandlerTests` gives
//      about the export: each method carries its own `withGate` call,
//      and the one whose omission hurts most (`ExportPanel` — it leaves
//      a file) is the one added last.
//   2. **The panels render the reference composition.** A manifest with
//      one entry of every kind, a knob, a metric, a subject and a
//      purpose, projected and read back — so a component kind dropped
//      from the projection is caught, not merely a plumbing break.
//   3. **The honest empty states.** A grounding-free composition
//      declares no grounding, which is a complete answer; a missing
//      snapshot is a DIFFERENT state and says so rather than rendering
//      an empty composition; and the surfaces panel carries its
//      structural note rather than a bare empty list. Conflating the
//      three is the one failure the module exists to prevent.
//   4. **Export round-trips.** Each panel's JSON deserialises back into
//      the value its own getter returned. The handler serialises the
//      same value through the same options, so this is structural — the
//      test is what keeps it structural if someone later renders an
//      export separately.
//
// The provenance panel is asserted through DI registration rather than
// through a walk: the handler reports composition, and the walk is
// `IProvenanceQueryApi`'s (see the handler's header for why there is not
// a second one).

// ─── Fixtures ─────────────────────────────────────────────────────────

let private capturedAt = DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)

/// A composition with one entry of every kind the manifest enumerates,
/// so a projection that drops a kind fails rather than passing on the
/// kinds it kept.
let private referenceManifest: CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
        [
            CompositionManifest.companionSlotEntry "IAuthProvider"
            CompositionManifest.companionImplEntry "IAuditSink" "s3-archive"
        ]
        [ CompositionManifest.dataTypeEntry "sales-ledger" ] [ CompositionManifest.toolEntry "reports.summarise" ] [
            CompositionManifest.knob "ProcessProfile" "StandardProfile"
            // Phase 592's per-surface allowed purpose set rides the knobs
            // rather than the purpose entries — `ServerApp.compositionManifest`
            // emits it as a `DisclosurePurposes.<Surface>` knob, and it is
            // what `GroundingEnvelope.ofManifest` reads the
            // `disclosure-policy` facet out of. The fixture mirrors that
            // shape; a purpose entry alone would declare a purpose with no
            // policy, which is a different composition.
            CompositionManifest.knob "DisclosurePurposes.FactExport" "billing"
        ]
    |> CompositionManifest.withGrounding [ CompositionManifest.metricEntry "revenue" ] [
        CompositionManifest.subjectEntry "region"
    ]
    |> CompositionManifest.withPurposes [
        CompositionManifest.purposeEntry {
            PurposeId = "billing"
            Description = "Invoicing and collections"
            TaxonomyVersion = "v1"
            AllowedSurfaces = [ "FactExport" ]
        }
    ]

/// A composition that declares no grounding at all — the pre-526 shape,
/// and a legitimate one.
let private bareManifest: CompositionManifest =
    CompositionManifest.build [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ] [] [] [] []

let private snapshotOf (manifest: CompositionManifest) =
    CompositionInspectorHandler.CompositionInspectorSnapshot.ofComposition
        manifest
        CompositionReferences.empty
        capturedAt

let private freshTeamStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications)

/// Build the handler over an optional snapshot and an optional set of
/// composed provenance services.
let private buildApiWith
    (accessContext: AccessContext)
    (teamStore: ITeamStore option)
    (snapshot: CompositionInspectorHandler.CompositionInspectorSnapshot option)
    (provenanceGraph: IProvenanceGraph option)
    : ICompositionInspectorApi =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(accessContext) |> ignore

    match teamStore with
    | Some ts -> services.AddSingleton<ITeamStore>(ts) |> ignore
    | None -> ()

    match snapshot with
    | Some s ->
        services.AddSingleton<CompositionInspectorHandler.CompositionInspectorSnapshot>(s)
        |> ignore
    | None -> ()

    match provenanceGraph with
    | Some g -> services.AddSingleton<IProvenanceGraph>(g) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    CompositionInspectorHandler.compositionInspectorApi ctx

let private buildApi accessContext teamStore snapshot =
    buildApiWith accessContext teamStore snapshot None

let private teamContextWith (teamId: string) (userId: string) (role: TeamRole) = async {
    let store = freshTeamStore ()
    let! _ = store.CreateTeam(teamId, "Test Team")
    let! _ = (store :> ITeamStore).AddMember(teamId, userId, role)
    return AccessContext.unrestricted (TeamMember(userId, teamId)), (store :> ITeamStore)
}

let private expectOk (label: string) (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error msg -> failtestf "%s: expected Ok, got Error '%s'" label msg

let private expectError (label: string) (result: Result<'T, string>) : string =
    match result with
    | Ok _ -> failtestf "%s: expected Error, got Ok" label
    | Error msg -> msg

/// Read an exported panel back with the same converter set the handler
/// wrote it with — the reader an SDK consumer would use.
let private readBack<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, FableConverters.shared)

// ─── 1. Role gating ───────────────────────────────────────────────────

let roleGateTests =
    testList "Phase 593 — composition-inspector role gate" [

        testCaseAsync "an Anonymous caller is refused on every method"
        <| async {
            let ctx = AccessContext.unrestricted (AnonymousSession "sess-1")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))

            let! composition = api.GetComposition()
            let! surfaces = api.GetSurfaces()
            let! rules = api.GetRules()
            let! disclosure = api.GetDisclosure()
            let! provenance = api.GetProvenance()
            let! exported = api.ExportPanel CompositionPanel

            for label, msg in
                [
                    "GetComposition", expectError "GetComposition" composition
                    "GetSurfaces", expectError "GetSurfaces" surfaces
                    "GetRules", expectError "GetRules" rules
                    "GetDisclosure", expectError "GetDisclosure" disclosure
                    "GetProvenance", expectError "GetProvenance" provenance
                    "ExportPanel", expectError "ExportPanel" exported
                ] do
                Expect.stringContains msg "not available in this mode" $"{label} refuses an anonymous caller"
        }

        testCaseAsync "a Member is refused on every method, and told why"
        <| async {
            let! accessContext, teamStore = teamContextWith "team-a" "bob" Member

            let api =
                buildApi accessContext (Some teamStore) (Some(snapshotOf referenceManifest))

            let! composition = api.GetComposition()
            let! rules = api.GetRules()
            let! exported = api.ExportPanel RulesPanel

            for label, msg in
                [
                    "GetComposition", expectError "GetComposition" composition
                    "GetRules", expectError "GetRules" rules
                    "ExportPanel", expectError "ExportPanel" exported
                ] do
                Expect.stringContains msg "owners and admins" $"{label} names the role requirement"
        }

        testCaseAsync "an Owner and an Admin are both admitted"
        <| async {
            for role in [ Owner; Admin ] do
                let! accessContext, teamStore = teamContextWith "team-a" "alice" role

                let api =
                    buildApi accessContext (Some teamStore) (Some(snapshotOf referenceManifest))

                let! composition = api.GetComposition()
                let view = expectOk $"GetComposition ({role})" composition
                Expect.hasLength view.Modules 1 $"{role} reads the composition"
        }

        testCaseAsync "a single-scope authenticated caller is admitted"
        <| async {
            // No team store is registered — the single-scope modes have
            // no role concept and the caller owns the scope they read.
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! composition = api.GetComposition()
            expectOk "GetComposition" composition |> ignore
        }

        testCaseAsync "the export is gated BEFORE the snapshot is consulted"
        <| async {
            // Ordering matters: a gate applied after the projection
            // would still refuse, but a refactor that returned the JSON
            // alongside the error would leak. Asserted by refusing an
            // anonymous caller on a deployment that HAS a snapshot.
            let ctx = AccessContext.unrestricted (AnonymousSession "sess-2")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! exported = api.ExportPanel DisclosurePanel
            let msg = expectError "ExportPanel" exported
            Expect.isFalse (msg.Contains "{") "the refusal carries no JSON"
        }
    ]

// ─── 2. The panels render the reference composition ───────────────────

let panelTests =
    testList "Phase 593 — panels over the reference composition" [

        testCaseAsync "the composition panel carries every component kind the manifest enumerates"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! result = api.GetComposition()
            let view = expectOk "GetComposition" result

            Expect.equal view.SchemaVersion CompositionManifest.SchemaVersion "the manifest schema version is echoed"
            Expect.hasLength view.Modules 1 "one module"
            Expect.hasLength view.CompanionSlots 2 "one slot and one impl"
            Expect.hasLength view.DataTypes 1 "one data type"
            Expect.hasLength view.Tools 1 "one tool"
            Expect.hasLength view.Metrics 1 "one grounding metric"
            Expect.hasLength view.Subjects 1 "one grounding subject"
            Expect.hasLength view.Purposes 1 "one disclosure purpose"

            let moduleEntry = List.head view.Modules
            Expect.equal moduleEntry.Kind "module" "the kind label matches the ComponentId slot prefix"
            Expect.stringContains moduleEntry.Id "module:" "the rendered id carries its slot prefix"
            Expect.equal moduleEntry.Label "Reports" "the display label is the module name"

            // The multi-impl companion entry is the only one that carries
            // an `Impl`, and losing it would silently flatten two audit
            // sinks into one indistinguishable row.
            let impls = view.CompanionSlots |> List.choose _.Impl
            Expect.equal impls [ "s3-archive" ] "the companion impl sub-id survives the projection"

            // Phase 592's per-surface allowed set rides the knobs, so the
            // purpose regime is readable from this panel alone.
            let knobNames = view.ConfigKnobs |> List.map _.Name
            Expect.contains knobNames "ProcessProfile" "the composition knob is projected"

            Expect.contains knobNames "DisclosurePurposes.FactExport" "the per-surface allowed purpose set is projected"
        }

        testCaseAsync "the rules panel lists the rule manifest beside this composition's verdict"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! result = api.GetRules()
            let view = expectOk "GetRules" result

            Expect.equal
                (List.length view.Rules)
                (List.length CompositionValidator.classifiedRuleManifest)
                "every declared rule is listed"

            Expect.isNonEmpty view.Rules "the rule manifest is not empty"
            Expect.equal view.CapturedAtUtc capturedAt "the capture timestamp names the boot, not the read"

            // A well-formed composition passes silently (GP 11), and the
            // panel's pass state is the EMPTY defect list against a
            // NON-empty rule list — which is why both halves are asserted.
            Expect.isEmpty view.Defects "the reference composition is well-formed"

            let classes = view.Rules |> List.map _.RuleClass |> List.distinct
            Expect.contains classes "structural" "the Phase 585 rule class is projected"
        }

        testCaseAsync "the rules panel surfaces a real defect when the composition is malformed"
        <| async {
            // Two modules sharing one ComponentId — the duplicate-id rule,
            // the first structural rule the validator declares. Asserted
            // through the panel so a projection that dropped the defect
            // list would fail here rather than reading as "all passed".
            let colliding =
                CompositionManifest.build
                    [
                        CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports")
                        CompositionManifest.moduleEntry ("Reporting", ComponentId.ofModule "reports")
                    ]
                    []
                    [] [] []

            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf colliding))
            let! result = api.GetRules()
            let view = expectOk "GetRules" result

            Expect.isNonEmpty view.Defects "a duplicate component id is reported"

            let severities = view.Defects |> List.map _.Severity |> List.distinct
            Expect.contains severities "error" "the defect carries its rendered severity"
        }

        testCaseAsync "the disclosure panel carries the envelope's declarations and its digest"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! result = api.GetDisclosure()
            let view = expectOk "GetDisclosure" result

            let facets = view.Declarations |> List.map _.Facet |> List.distinct
            Expect.contains facets "metric-registration" "the metric declaration is projected"
            Expect.contains facets "subject-registration" "the subject declaration is projected"
            Expect.contains facets "purpose-declaration" "the purpose declaration is projected"
            Expect.contains facets "disclosure-policy" "the per-surface policy is projected"

            // The digest is the value a boot seal binds; a reviewer
            // matches an exported panel against a recorded seal by it, so
            // it must be the envelope's own and not a re-derivation.
            let expected =
                referenceManifest |> GroundingEnvelope.ofManifest |> GroundingEnvelope.digest

            Expect.equal view.Digest expected "the digest is the envelope's own"
        }

        testCaseAsync "the provenance panel reports what the deployment composed"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")

            let! bare = (buildApi ctx None (Some(snapshotOf referenceManifest))).GetProvenance()
            let bareView = expectOk "GetProvenance (bare)" bare
            Expect.isFalse bareView.GraphComposed "a deployment with no provenance graph says so"
            Expect.isFalse bareView.FactEvidenceComposed "no fact-evidence source"
            Expect.isFalse bareView.ArtifactProvenanceComposed "no artifact-provenance source"

            let emptyChain: ProvenanceChain = { Root = ""; Nodes = []; Edges = [] }

            let graph =
                { new IProvenanceGraph with
                    member _.GetChain(_, _, _, _) = async { return emptyChain }
                    member _.GetChainForMessage(_, _, _, _) = async { return emptyChain }
                }

            let composedApi =
                buildApiWith ctx None (Some(snapshotOf referenceManifest)) (Some graph)

            let! composed = composedApi.GetProvenance()
            let composedView = expectOk "GetProvenance (composed)" composed
            Expect.isTrue composedView.GraphComposed "a composed provenance graph is reported"
        }
    ]

// ─── 3. Honest empty states ───────────────────────────────────────────

let emptyStateTests =
    testList "Phase 593 — absent substrate says which absence it is" [

        testCaseAsync "a grounding-free composition declares no grounding, and that is a complete answer"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf bareManifest))

            let! disclosure = api.GetDisclosure()
            let view = expectOk "GetDisclosure" disclosure
            Expect.isEmpty view.Declarations "no declarations"

            // The digest is still the envelope's — an empty envelope has
            // one, and rendering it as blank would make an unsealed
            // deployment indistinguishable from a grounding-free one.
            Expect.isNotEmpty view.Digest "an empty envelope still has a digest"

            let! composition = api.GetComposition()
            let compositionView = expectOk "GetComposition" composition
            Expect.isEmpty compositionView.Metrics "no metrics"
            Expect.hasLength compositionView.Modules 1 "the module it DOES compose is still reported"
        }

        testCaseAsync "the surfaces panel carries its structural note rather than a bare empty list"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))
            let! result = api.GetSurfaces()
            let view = expectOk "GetSurfaces" result

            Expect.isEmpty view.Descriptors "no descriptor is captured at compose time"

            match view.Note with
            | None -> failtest "an empty descriptor list without a note reads as 'this deployment composes no modules'"
            | Some note ->
                Expect.stringContains note "not reporting" "the note refuses the wrong reading explicitly"
                Expect.isGreaterThan note.Length 80 "the note explains rather than labels"
        }

        testCaseAsync "a missing snapshot is a DIFFERENT state from an empty composition"
        <| async {
            // The route is mounted by `ServerApp.run`, which always
            // registers a snapshot — so this path is only reachable from
            // a hand-wired host. It still must not render as "this
            // deployment composes nothing", which is a false statement
            // about the deployment rather than an honest absence.
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None None

            let! composition = api.GetComposition()
            let msg = expectError "GetComposition" composition
            Expect.stringContains msg "did not record a composition snapshot" "the absence names itself"
            Expect.stringContains msg "ServerApp.run" "the absence names the remedy"

            let! exported = api.ExportPanel CompositionPanel
            expectError "ExportPanel" exported |> ignore

            // The provenance panel is resolved per request from DI, not
            // from the snapshot, so it still answers — an absent snapshot
            // must not make an unrelated panel lie.
            let! provenance = api.GetProvenance()
            let view = expectOk "GetProvenance" provenance
            Expect.isFalse view.GraphComposed "the provenance report is independent of the snapshot"
        }
    ]

// ─── 4. Export round-trip ─────────────────────────────────────────────

let exportTests =
    testList "Phase 593 — panel export round-trips against its own getter" [

        testCaseAsync "every snapshot-backed panel's JSON deserialises back into the value its getter returned"
        <| async {
            let ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "carol")
            let api = buildApi ctx None (Some(snapshotOf referenceManifest))

            let! composition = api.GetComposition()
            let! compositionJson = api.ExportPanel CompositionPanel

            Expect.equal
                (readBack<CompositionView> (expectOk "export composition" compositionJson))
                (expectOk "GetComposition" composition)
                "the composition export is the composition panel"

            let! surfaces = api.GetSurfaces()
            let! surfacesJson = api.ExportPanel SurfacesPanel

            Expect.equal
                (readBack<SurfacesView> (expectOk "export surfaces" surfacesJson))
                (expectOk "GetSurfaces" surfaces)
                "the surfaces export is the surfaces panel — note included"

            let! rules = api.GetRules()
            let! rulesJson = api.ExportPanel RulesPanel

            Expect.equal
                (readBack<RulesView> (expectOk "export rules" rulesJson))
                (expectOk "GetRules" rules)
                "the rules export is the rules panel"

            let! disclosure = api.GetDisclosure()
            let! disclosureJson = api.ExportPanel DisclosurePanel

            Expect.equal
                (readBack<DisclosureView> (expectOk "export disclosure" disclosureJson))
                (expectOk "GetDisclosure" disclosure)
                "the disclosure export is the disclosure panel"

            let! provenance = api.GetProvenance()
            let! provenanceJson = api.ExportPanel ProvenancePanel

            Expect.equal
                (readBack<ProvenanceView> (expectOk "export provenance" provenanceJson))
                (expectOk "GetProvenance" provenance)
                "the provenance export is the provenance panel"
        }

        testCase "every panel exports under a distinct, stable filename"
        <| fun () ->
            // The filename is part of the contract rather than a client
            // detail: a reviewer receiving five files needs them to be
            // five files, not one overwritten four times.
            let names = InspectorPanel.all |> List.map CompositionInspectorApi.exportFileName

            Expect.equal (List.distinct names |> List.length) (List.length names) "the filenames are distinct"
            Expect.all names _.EndsWith(".json") "every export is a .json"

        testCase "the exported JSON is indented, because a person reads it"
        <| fun () ->
            let api =
                buildApi
                    (AccessContext.unrestricted (Subject.AuthenticatedUser "carol"))
                    None
                    (Some(snapshotOf referenceManifest))

            let json =
                api.ExportPanel CompositionPanel |> Async.RunSynchronously |> expectOk "export"

            Expect.stringContains json "\n" "the artifact is not a single line"
    ]

// ─── 5. The read-only shape of the contract ───────────────────────────

let contractShapeTests =
    testList "Phase 593 — the inspector contract is read-only by construction" [

        testCase "no method on ICompositionInspectorApi returns unit"
        <| fun () ->
            // The same property `IProvenanceQueryApi` asserts, and for
            // the same reason: `unit` is the shape a mutation takes, so
            // its absence is what makes "read-only" structural rather
            // than a claim in a comment. A deployment cannot expose a
            // write path by composing this contract.
            let fields =
                Microsoft.FSharp.Reflection.FSharpType.GetRecordFields typeof<ICompositionInspectorApi>

            Expect.isNonEmpty fields "the contract has methods"

            for field in fields do
                let returns = field.PropertyType.ToString()

                Expect.isFalse
                    (returns.Contains "Microsoft.FSharp.Core.Unit]"
                     && returns.Contains "FSharpAsync`1")
                    $"{field.Name} does not return Async<unit>"

        testCase "every panel has a slug and the slugs are distinct"
        <| fun () ->
            let slugs = InspectorPanel.all |> List.map InspectorPanel.slug
            Expect.hasLength slugs 5 "five panels"
            Expect.equal (List.distinct slugs |> List.length) 5 "the slugs are distinct routes"
    ]