// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ReflectionToolsTests

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Providers
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 782 — the reflection door ─────────────────────────────────
//
// Pinned, each against the surface it must agree with rather than against
// a literal:
//
//   1. **Same bytes.** Each tool's output equals what the API it fronts
//      returns on the same composition — the inspector's `ExportPanel` per
//      panel, the report API's record (serialised), the seam-authority
//      evidence's projection.
//   2. **The gates are the APIs'.** Anonymous and a team Member are refused
//      by every tool; an authenticated non-admin passes the inspector gate
//      and is still refused the report and the reach, which were already
//      Platform-Admin-only.
//   3. **Every call is an audit row**, served and refused alike.
//   4. **No AI, no tools.** A composition that never opts in — with or
//      without AI — carries none of the three; the opt-in registers them
//      through the ordinary registry path, and a read-only policy admits
//      them.
//   5. **The offline door agrees with the tools** on the same composition.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private capturedAt = DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)

let private manifest: CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
        [ CompositionManifest.companionSlotEntry "IAuthProvider" ]
        [ CompositionManifest.dataTypeEntry "sales-ledger" ] [ CompositionManifest.toolEntry "reports.summarise" ] [
            CompositionManifest.knob "ProcessProfile" "StandardProfile"
        ]

let private snapshot =
    CompositionInspectorHandler.CompositionInspectorSnapshot.ofComposition
        manifest
        CompositionReferences.empty
        capturedAt

let private seamAuthority (verification: SeamAuthorityVerification) : SeamAuthorityIntegrity = {
    Profile = "verified"
    DeclarationMandatory = true
    Components = [
        {
            AuthorityComponent = ComponentId.ofModule "reports"
            DeclaredGrant = SeamGrant.ofInterfaces [ "IEntityStore" ]
            DerivedReach = [ SeamId.ofInterface "IEntityStore" ]
            ComposedHere = true
            ObservedReach = None
        }
    ]
    Verification = verification
}

type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()

    member _.Reflection =
        recorded
        |> Seq.choose (function
            | _, RemotingMethodAudited p when p.Kind = ReflectionTools.AuditKind -> Some p
            | _ -> None)
        |> List.ofSeq

    member _.Verified =
        recorded
        |> Seq.filter (function
            | _, DeploymentVerified _ -> true
            | _ -> false)
        |> Seq.length

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock recorded (fun () -> recorded.Add((scopeId, audit))) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private platformAdmin: AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "ops") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// A team store holding one team with one member in `role`.
let private teamStoreWith (teamId: string) (userId: string) (role: TeamRole) : ITeamStore =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    let store = TeamStore(storage, notifications)
    store.CreateTeam(teamId, "Test Team") |> Async.RunSynchronously |> ignore
    let teams = store :> ITeamStore
    teams.AddMember(teamId, userId, role) |> Async.RunSynchronously |> ignore
    teams

type private Fixture = {
    Services: IServiceProvider
    Audit: RecordingAuditLog
}

let private fixtureWith
    (accessContext: AccessContext)
    (teamStore: ITeamStore option)
    (evidence: IDeploymentVerificationEvidence option)
    : Fixture =
    let audit = RecordingAuditLog()
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(accessContext) |> ignore

    services.AddSingleton<CompositionInspectorHandler.CompositionInspectorSnapshot>(snapshot)
    |> ignore

    services.AddSingleton<IAuditLog>(audit) |> ignore

    teamStore
    |> Option.iter (fun ts -> services.AddSingleton<ITeamStore>(ts) |> ignore)

    evidence
    |> Option.iter (fun e -> services.AddSingleton<IDeploymentVerificationEvidence>(e) |> ignore)

    {
        Services = services.BuildServiceProvider()
        Audit = audit
    }

let private contextOf (fixture: Fixture) (userId: string) : HttpContext =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- fixture.Services
    ctx.Items["ToolUp.UserId"] <- userId
    ctx

let private run (f: HttpContext -> string -> Async<string>) (ctx: HttpContext) (args: string) =
    f ctx args |> Async.RunSynchronously

let private seamEvidence verification =
    DeploymentVerificationEvidence.none
    |> DeploymentVerificationEvidence.withSeamAuthority (Some(seamAuthority verification))

let private errorOf (json: string) : string option =
    use doc = JsonDocument.Parse json

    match doc.RootElement.ValueKind, doc.RootElement.TryGetProperty "error" with
    | JsonValueKind.Object, (true, e) -> Some(e.GetString())
    | _ -> None

let private readOptions = FableConverters.create ()

let private reportOf (json: string) : DeploymentVerification.DeploymentVerificationReport =
    JsonSerializer.Deserialize<DeploymentVerification.DeploymentVerificationReport>(json, readOptions)

type private StubProviderFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error(ProviderResolutionError.NoProviderConfigured) }
        member _.TryResolveByLabel(_, _) = async { return Error(ProviderResolutionError.NoProviderConfigured) }
        member _.BuildPlatform(_providerId, _apiKey, _model) = None

type private StubProviderProfile() =
    interface IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }

let private reflectionNames = [
    ReflectionTools.DescribeCompositionTool
    ReflectionTools.VerificationReportTool
    ReflectionTools.ModuleReachTool
]

let private toolNames (tools: (AIToolDefinition * 'exec) list) =
    tools |> List.map (fst >> _.Name) |> Set.ofList

// ─── 1. Same bytes ────────────────────────────────────────────────────

let private sameBytesTests =
    testList "Phase 782 — reflection tools return the APIs' bytes" [
        test "describe_composition byte-equals the inspector's ExportPanel, for every panel" {
            let fixture = fixtureWith platformAdmin None None
            let ctx = contextOf fixture "ops"
            let api = CompositionInspectorHandler.compositionInspectorApi ctx

            for panel in InspectorPanel.all do
                let expected =
                    match api.ExportPanel panel |> Async.RunSynchronously with
                    | Ok json -> json
                    | Error e -> failtestf "the inspector refused its own fixture: %s" e

                let actual =
                    run
                        ReflectionTools.executeDescribeComposition
                        ctx
                        (sprintf "{\"panel\":\"%s\"}" (InspectorPanel.slug panel))

                Expect.equal actual expected (sprintf "panel %s" (InspectorPanel.slug panel))
        }

        test "describe_composition with no arguments is the composition panel" {
            let fixture = fixtureWith platformAdmin None None
            let ctx = contextOf fixture "ops"

            let expected =
                (CompositionInspectorHandler.compositionInspectorApi ctx).ExportPanel CompositionPanel
                |> Async.RunSynchronously

            Expect.equal (Ok(run ReflectionTools.executeDescribeComposition ctx "")) expected "empty args"
            Expect.equal (Ok(run ReflectionTools.executeDescribeComposition ctx "{}")) expected "empty object"
        }

        test "verification_report is the report API's record, serialised, on the same composition" {
            let fixture =
                fixtureWith platformAdmin None (Some(seamEvidence (SeamAuthorityAdmitted(1, 1))))

            let ctx = contextOf fixture "ops"
            let tool = run ReflectionTools.executeVerificationReport ctx "{}"

            let api =
                match
                    (DeploymentVerificationReport.deploymentVerificationApi ctx).GetVerificationReport()
                    |> Async.RunSynchronously
                with
                | Ok report -> report
                | Error e -> failtestf "the report API refused a platform admin: %s" e

            let fromTool = reportOf tool

            // The one field two runs cannot share is the clock.
            Expect.equal
                tool
                (CompositionInspectorHandler.canonicalJson {
                    api with
                        GeneratedAt = fromTool.GeneratedAt
                })
                "byte-equal up to GeneratedAt"

            Expect.equal fromTool.VerdictDigest api.VerdictDigest "same verdict set"
        }

        test "verification_report narrows to the requested sections and keeps the whole-report fields" {
            let fixture = fixtureWith platformAdmin None None
            let ctx = contextOf fixture "ops"
            let whole = reportOf (run ReflectionTools.executeVerificationReport ctx "")
            let wanted = whole.Sections |> List.take 2 |> List.map _.Id

            let narrowed =
                reportOf (
                    run ReflectionTools.executeVerificationReport ctx (JsonSerializer.Serialize {| sections = wanted |})
                )

            Expect.equal (narrowed.Sections |> List.map _.Id) wanted "only the requested sections"
            Expect.equal narrowed.VerdictDigest whole.VerdictDigest "digest of the whole report"
            Expect.equal narrowed.NotProved whole.NotProved "not-proved statements kept whole"

            let unknown =
                run ReflectionTools.executeVerificationReport ctx "{\"sections\":[\"no-such-section\"]}"

            Expect.equal (errorOf unknown) (Some "InvalidArgument") "an unknown section is named, not dropped"
        }

        test "module_reach is the seam-authority evidence's projection, absent observed reach marked" {
            let fixture =
                fixtureWith platformAdmin None (Some(seamEvidence (SeamAuthorityAdmitted(1, 1))))

            let ctx = contextOf fixture "ops"
            let tool = run ReflectionTools.executeModuleReach ctx "{}"

            let expected =
                DeploymentVerificationReport.resolveEvidence fixture.Services
                |> ReflectionTools.ModuleReachView.ofEvidence
                |> CompositionInspectorHandler.canonicalJson

            Expect.equal tool expected "the projection, serialised once"
            Expect.stringContains tool ReflectionTools.ObservedReachAbsentNote "absent is said, never zero"
            Expect.stringContains tool "IEntityStore" "the derived reach is present"
        }

        test "module_reach on a composition with no seam-authority evidence says so" {
            let fixture = fixtureWith platformAdmin None None
            let tool = run ReflectionTools.executeModuleReach (contextOf fixture "ops") ""

            Expect.isNone (errorOf tool) "an honest absence is a document, not an error"
            Expect.stringContains tool ReflectionTools.SeamAuthorityNotComposedNote "the note"
        }
    ]

// ─── 2 + 3. Gates and audit rows ──────────────────────────────────────

let private gateTests =
    testList "Phase 782 — reflection tools apply the APIs' own gates and audit every call" [
        test "anonymous is refused by every tool, and each refusal is an audit row" {
            let fixture =
                fixtureWith (AccessContext.unrestricted (AnonymousSession "s-1")) None None

            let ctx = contextOf fixture "s-1"

            for (_, exec) in ReflectionTools.tools do
                Expect.equal (errorOf (run exec ctx "{}")) (Some "Refused") "refused"

            let rows = fixture.Audit.Reflection
            Expect.equal (rows |> List.map _.MethodName) reflectionNames "one row per call, per tool"

            Expect.isTrue
                (rows |> List.forall (fun r -> r.Payload["outcome"] = "refused"))
                "each row records the refusal"

            Expect.equal fixture.Audit.Verified 0 "a refused report call ran no report"
        }

        test "a team Member is refused by every tool — the inspector's role gate, verbatim" {
            let store = teamStoreWith "team-1" "mia" Member

            let fixture =
                fixtureWith (AccessContext.unrestricted (TeamMember("mia", "team-1"))) (Some store) None

            let ctx = contextOf fixture "mia"

            let inspectorSays =
                CompositionInspectorHandler.readGate ctx |> Async.RunSynchronously

            for (_, exec) in ReflectionTools.tools do
                let result = run exec ctx "{}"
                Expect.equal (errorOf result) (Some "Refused") "refused"

                match inspectorSays with
                | Error reason ->
                    Expect.stringContains result "Your role" (sprintf "the inspector's own reason: %s" reason)
                | Ok() -> failtest "the fixture's Member passed the inspector gate"
        }

        test "an authenticated non-admin may describe the composition but not read the report or the reach" {
            let fixture =
                fixtureWith (AccessContext.unrestricted (AuthenticatedUser "dev")) None None

            let ctx = contextOf fixture "dev"

            Expect.isNone (errorOf (run ReflectionTools.executeDescribeComposition ctx "{}")) "inspector gate passes"

            Expect.equal
                (errorOf (run ReflectionTools.executeVerificationReport ctx "{}"))
                (Some "Refused")
                "the report keeps its Platform-Admin audience"

            Expect.equal
                (errorOf (run ReflectionTools.executeModuleReach ctx "{}"))
                (Some "Refused")
                "the reach is report evidence and keeps that audience"

            Expect.equal
                (fixture.Audit.Reflection |> List.map (fun r -> r.Payload["outcome"]))
                [ "served"; "refused"; "refused" ]
                "served and refused alike"
        }

        test "a served call is an audit row naming the tool and its argument" {
            let fixture = fixtureWith platformAdmin None None
            let ctx = contextOf fixture "ops"

            run ReflectionTools.executeDescribeComposition ctx "{\"panel\":\"rules\"}"
            |> ignore

            run ReflectionTools.executeVerificationReport ctx "{}" |> ignore

            match fixture.Audit.Reflection with
            | [ describe; report ] ->
                Expect.equal describe.MethodName ReflectionTools.DescribeCompositionTool "tool"
                Expect.equal describe.Payload["panel"] "rules" "argument"
                Expect.equal describe.SubjectId "user:ops" "subject"
                Expect.equal report.Payload["outcome"] "served" "served"
            | rows -> failtestf "expected two reflection rows, got %d" rows.Length

            Expect.equal fixture.Audit.Verified 1 "the report tool's run wrote the report's own row"
        }

        test "an invalid panel is refused with the valid set named, and audited" {
            let fixture = fixtureWith platformAdmin None None

            let result =
                run ReflectionTools.executeDescribeComposition (contextOf fixture "ops") "{\"panel\":\"x\"}"

            Expect.equal (errorOf result) (Some "InvalidArgument") "invalid"
            Expect.stringContains result "disclosure" "names the panels"

            Expect.equal
                (fixture.Audit.Reflection |> List.map (fun r -> r.Payload["outcome"]))
                [ "invalidargument" ]
                "row"
        }
    ]

// ─── 4. Registration ──────────────────────────────────────────────────

let private baseApp () : AIServerApp =
    AIServerApp.create (StubProviderFactory() :> IAIProviderFactory) (StubProviderProfile() :> IProviderProfile)

let private registrationTests =
    testList "Phase 782 — reflection tools are an AI composition's opt-in" [
        test "a composition with no AI provider carries no reflection tool" {
            Expect.isEmpty
                (Set.intersect (toolNames ServerApp.empty.AITools) (Set.ofList reflectionNames))
                "plain ServerApp"
        }

        test "an AI composition that does not opt in carries none either (GP 11)" {
            Expect.isEmpty
                (Set.intersect (toolNames (baseApp ()).Base.AITools) (Set.ofList reflectionNames))
                "AIServerApp without withReflectionTools"
        }

        test "withReflectionTools registers the three through the ordinary registry path" {
            let app = baseApp () |> AIServerApp.withReflectionTools
            Expect.equal (toolNames app.Base.AITools) (Set.ofList reflectionNames) "the three, once each"

            let registry = AIToolRegistry.AIToolRegistry()

            registry.RegisterAll(
                app.Base.AITools
                |> List.map (fun (def, exec) -> AIToolRegistry.createTool def exec)
            )

            for name in reflectionNames do
                match registry.FindByName name with
                | Some tool -> Expect.equal tool.Definition.SourceModule ReflectionTools.SourceModule "platform source"
                | None -> failtestf "%s did not reach the registry" name
        }

        test "the tools are read-only, server-resident, and admitted by a read-only policy" {
            for (def, _) in ReflectionTools.tools do
                Expect.equal def.Location ServerResident "server-resident"
                Expect.equal def.Effects ToolEffectDeclaration.readContent "declared read-content"

                Expect.isTrue
                    (AIToolRegistry.ToolGate.decide platformAdmin (fun _ -> true) AIToolRegistry.ToolPolicy.readOnly def
                     |> AIToolRegistry.ToolGate.admits)
                    (sprintf "%s under readOnly" def.Name)
        }
    ]

// ─── 5. The offline door ──────────────────────────────────────────────

let private describe (fixture: Fixture) sections panel =
    match
        ReflectionTools.describe fixture.Services sections panel
        |> Async.RunSynchronously
    with
    | Ok result -> result
    | Error e -> failtestf "describe refused: %s" e

let private offlineTests =
    testList "Phase 782 — the --describe door agrees with the tools" [
        test "the composition document is the tool's bytes, for every panel" {
            let fixture = fixtureWith platformAdmin None None
            let ctx = contextOf fixture "ops"

            for panel in InspectorPanel.all do
                let slug = InspectorPanel.slug panel

                let json, code =
                    describe fixture [ ReflectionTools.CompositionDocument ] (Some slug)

                Expect.equal
                    json
                    (run ReflectionTools.executeDescribeComposition ctx (sprintf "{\"panel\":\"%s\"}" slug))
                    slug

                Expect.equal code 0 "exit 0"
        }

        test "the reach document is the tool's bytes" {
            let fixture =
                fixtureWith platformAdmin None (Some(seamEvidence (SeamAuthorityAdmitted(1, 1))))

            let json, _ = describe fixture [ ReflectionTools.ModuleReachDocument ] None
            Expect.equal json (run ReflectionTools.executeModuleReach (contextOf fixture "ops") "") "same bytes"
        }

        test "the report document is the tool's record up to the clock and the actor" {
            let fixture =
                fixtureWith platformAdmin None (Some(seamEvidence (SeamAuthorityAdmitted(1, 1))))

            let json, code =
                describe fixture [ ReflectionTools.VerificationReportDocument ] None

            let offline = reportOf json

            let tool =
                reportOf (run ReflectionTools.executeVerificationReport (contextOf fixture "ops") "")

            Expect.equal offline.Actor StartupModes.CliDescribeActor "the CLI read names its surface"

            Expect.equal
                {
                    offline with
                        GeneratedAt = tool.GeneratedAt
                        Actor = tool.Actor
                }
                tool
                "the same report"

            Expect.equal code 0 "a verified composition exits 0"
        }

        test "a composed section that failed exits non-zero, as --verify-deployment does" {
            let fixture =
                fixtureWith platformAdmin None (Some(seamEvidence (SeamAuthorityRefused("refused", [ "reports" ]))))

            let _, code = describe fixture [] None
            Expect.equal code 1 "the report's exit code"
        }

        test "all three documents are one object keyed by document, each embedded verbatim" {
            let fixture = fixtureWith platformAdmin None None
            let json, _ = describe fixture [] None
            use doc = JsonDocument.Parse json

            let keys = doc.RootElement.EnumerateObject() |> Seq.map _.Name |> List.ofSeq

            Expect.equal keys ReflectionTools.documents "document order"

            let composition, _ = describe fixture [ ReflectionTools.CompositionDocument ] None

            Expect.equal
                (doc.RootElement.GetProperty(ReflectionTools.CompositionDocument).GetRawText())
                composition
                "verbatim"
        }

        test "an unknown section or panel is a usage error, and the read is audited under the CLI actor" {
            let fixture = fixtureWith platformAdmin None None

            Expect.isError
                (ReflectionTools.describe fixture.Services [ "everything" ] None
                 |> Async.RunSynchronously)
                "section"

            Expect.isError
                (ReflectionTools.describe fixture.Services [] (Some "nope")
                 |> Async.RunSynchronously)
                "panel"

            describe fixture [ ReflectionTools.CompositionDocument ] None |> ignore

            Expect.equal
                (fixture.Audit.Reflection |> List.map _.SubjectId)
                [ StartupModes.CliDescribeActor ]
                "one row, for the served read"
        }

        test "--describe is a startup mode, after --verify-deployment, with its modifiers parsed" {
            Expect.equal (StartupModes.detect [ "app"; "--describe" ]) StartupModes.Describe "detected"

            Expect.equal
                (StartupModes.detect [ "app"; "--describe"; "--verify-deployment" ])
                StartupModes.VerifyDeployment
                "precedence"

            let argv = [
                "app"
                "--describe"
                "--describe-section"
                "composition"
                "--describe-section"
                "module-reach"
                "--describe-panel"
                "rules"
                "--describe-out"
                "x.json"
            ]

            Expect.equal (StartupModes.describeSections argv) [ "composition"; "module-reach" ] "sections"
            Expect.equal (StartupModes.describePanel argv) (Some "rules") "panel"
            Expect.equal (StartupModes.describeOut argv) (Some "x.json") "out"
            Expect.equal (StartupModes.detect [ "app" ]) StartupModes.NormalBoot "absent"
        }
    ]

let tests =
    testList "Phase 782 — reflection tools" [ sameBytesTests; gateTests; registrationTests; offlineTests ]