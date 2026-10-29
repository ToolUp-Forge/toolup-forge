module ToolUp.Platform.Tests.InProcess.CompositionManifestLiveInterfaceTests

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests
open ToolUp.RAG

// ─── Phase 860 — the live-interface posture on the composition manifest ──
//
// `CompositionManifest.LiveInterfaceTools` names the composed tools that are
// live-interface tools (Phase 538), read from the definition's flag and
// location, never its name. Four claims, each probed:
//
//   * The field comes from the FLAG: a server tool whose name looks like a
//     UI tool is excluded; a flagged and a client-resident tool are included.
//   * The manifest and the tool-aware RAG framing share ONE predicate: they
//     agree on every member of a fixture set covering each arm.
//   * The field is NOT sealed: the canonical form is byte-identical to what
//     the binary before this phase produced, with the field empty or
//     populated, at schema 2 or 3; `compare` ignores it; a binding sealed at
//     schema 2 still verifies with no drift.
//   * Readiness without RAG reports the posture from the manifest, and a RAG
//     composition's own summary wins whenever it is registered.

// ─── Fixtures ────────────────────────────────────────────────────────

let private tool (name: string) (location: ToolLocation) (isLiveInterface: bool) : AIToolDefinition = {
    Name = name
    Description = ""
    Parameters = []
    SourceModule = "live-interface-test"
    EmitsActions = None
    Location = location
    Surface = Both
    IsLiveInterface = isLiveInterface
    ResultBudget = DefaultResultBudget
    Effects = UndeclaredEffects
}

/// A plain server-resident analytical tool.
let private serverTool = tool "orders.run" ServerResident false

/// A server-resident tool that DECLARES the live-interface capability.
let private flaggedTool = tool "host.read_view" ServerResident true

/// A client-resident tool: live-interface by construction.
let private clientTool = tool "client.pick_colour" ClientResident false

/// A server-resident tool whose NAME follows a UI-tool convention but which
/// declares nothing. A name match would include it; the flag excludes it.
let private lookalikeTool = tool "_platform.ui.highlight" ServerResident false

let private fixtureSet = [ serverTool; flaggedTool; clientTool; lookalikeTool ]

let private registration (def: AIToolDefinition) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    def, (fun _ _ -> async { return "" })

let private appWith (defs: AIToolDefinition list) : ServerApp =
    ServerApp.empty
    |> ServerApp.addModules [
        ServerModule.create "Workbench"
        |> ServerModule.withAITools (defs |> List.map registration)
    ]

/// The fixture composition the seal goldens below were captured over: one
/// module, one companion slot, one datatype, the named tools and one knob,
/// recorded at the canonical-method schema.
let private sealFixture (toolNames: string list) : CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
        [ CompositionManifest.companionSlotEntry "IBlobStorage" ]
        [ CompositionManifest.dataTypeEntry "sales" ]
        (toolNames |> List.map CompositionManifest.toolEntry)
        [ CompositionManifest.knob "ProcessProfile" "AllInOne" ]
    |> CompositionManifest.withCanonicalMethods []

let private noLiveToolNames = [ "reports.summarise" ]

let private liveToolNames = [ "reports.summarise"; "host.read_view"; "client.pick_colour" ]

/// The canonical form of `sealFixture noLiveToolNames`, captured from the
/// binary BEFORE this phase (schema 2, no `LiveInterfaceTools` field). The
/// `1:2` after the canonical-method framing version is the schema frame.
[<Literal>]
let private PreNoLiveCanonicalForm =
    "1:49:companion22:companion:IBlobStorage12:IBlobStorage1:00:8:datatype14:datatype:sales5:sales1:00:6:module14:module:reports7:Reports1:00:4:tool22:tool:reports.summarise17:reports.summarise1:00:1:114:ProcessProfile8:AllInOne46:toolup.compositionmanifest.canonicalmethods.v11:21:0"

/// The canonical form of `sealFixture liveToolNames`, captured the same way.
[<Literal>]
let private PreWithLiveCanonicalForm =
    "1:69:companion22:companion:IBlobStorage12:IBlobStorage1:00:8:datatype14:datatype:sales5:sales1:00:6:module14:module:reports7:Reports1:00:4:tool23:tool:client.pick_colour18:client.pick_colour1:00:4:tool19:tool:host.read_view14:host.read_view1:00:4:tool22:tool:reports.summarise17:reports.summarise1:00:1:114:ProcessProfile8:AllInOne46:toolup.compositionmanifest.canonicalmethods.v11:21:0"

/// A manifest as the binary before this phase projected it: stamped at
/// schema 2, and with no `LiveInterfaceTools` field, so it deserialises
/// as `null`.
let private asSchema2 (manifest: CompositionManifest) : CompositionManifest = {
    manifest with
        SchemaVersion = CompositionManifest.CanonicalMethodSchemaVersion
        LiveInterfaceTools = Unchecked.defaultof<string list>
}

let private baseRecord =
    DeployRecord.create "deploy-860" "tenant-1" "build-1" DeployManifest.empty DeployProvenance.none

let private sealer = StubSealer "boot-secret" :> IDeployRecordSealer

let private sealPair (record: DeployRecord) (recordedComposition: CompositionManifest) =
    async {
        let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

        let seal =
            match seal with
            | Ok s -> s
            | Error e -> failwithf "fixture could not seal the record: %s" e

        let! sealedBinding =
            BootVerificationPreflight.sealBinding
                sealer
                (BootVerificationPreflight.bindingFor record recordedComposition)

        let sealedBinding =
            match sealedBinding with
            | Ok b -> b
            | Error e -> failwithf "fixture could not seal the binding: %s" e

        return { Record = record; Seal = seal }, sealedBinding
    }
    |> Async.RunSynchronously

let private optionsFor (record: SealedDeployRecord) (binding: SealedCompositionBinding) : BootVerificationOptions = {
    Profile = CompositionProfile.Standard
    Policy = BootVerificationPolicy.RefuseOnDrift
    Sealer = sealer
    Locate = (fun _ -> None)
    Record = Some record
    Binding = Some binding
    Transcript = None
    AuditLog = None
    ScopeId = BootVerificationPreflight.PlatformScopeId
    MinimumLevel = None
    Attestor = RuntimeAttestor.unattested
}

let private platformAdminContext (configure: IServiceCollection -> unit) : HttpContext =
    let services = ServiceCollection()
    configure services

    services.AddSingleton<AccessContext>(
        {
            AccessContext.unrestricted (Subject.AnonymousSession "admin-user") with
                PlatformRole = Some PlatformRole.PlatformAdmin
        }
    )
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx

let private reportedPosture (ctx: HttpContext) : DeploymentReadiness.LiveInterfaceSummary =
    match
        (DeploymentReadinessReport.deploymentReadinessApi ServerConfig.defaults ctx).GetReadinessReport()
        |> Async.RunSynchronously
    with
    | Ok report -> report.LiveInterface
    | Error e -> failtestf "expected a readiness report, got Error %s" e

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "CompositionManifestLiveInterface" [

        // ─── 860.A — the field ───────────────────────────────────────

        testList "the field" [
            test "the field is populated from the flag and the location, never from the name" {
                let manifest = ServerApp.compositionManifest (appWith fixtureSet)

                Expect.equal
                    (CompositionManifest.liveInterfaceTools manifest)
                    [ "client.pick_colour"; "host.read_view" ]
                    "the flagged and the client-resident tool, sorted; the UI-named server tool and the plain one are not"

                Expect.equal
                    (manifest.Tools |> List.map _.Label |> List.sort)
                    (fixtureSet |> List.map _.Name |> List.sort)
                    "the field is a subset of the tool census, not a second census"
            }

            test "a manifest this binary projects records the field at schema 3, empty when no tool is one" {
                let manifest = ServerApp.compositionManifest (appWith [ serverTool; lookalikeTool ])

                Expect.equal manifest.SchemaVersion CompositionManifest.LiveInterfaceSchemaVersion "stamped at schema 3"
                Expect.isTrue (CompositionManifest.recordsLiveInterfaceTools manifest) "it speaks about the posture"
                Expect.isEmpty (CompositionManifest.liveInterfaceTools manifest) "and says no tool is one"
            }

            test "the recorded list is sorted and distinct, and a schema-2 manifest is silent rather than empty" {
                let recorded =
                    CompositionManifest.empty
                    |> CompositionManifest.withLiveInterfaceTools [ "b.tool"; "a.tool"; "b.tool" ]

                Expect.equal recorded.LiveInterfaceTools [ "a.tool"; "b.tool" ] "normalised on record"

                let legacy = asSchema2 recorded

                Expect.isFalse
                    (CompositionManifest.recordsLiveInterfaceTools legacy)
                    "a schema-2 manifest never carried the field"

                Expect.isEmpty (CompositionManifest.liveInterfaceTools legacy) "and its null list reads as empty"
            }

            test "the manifest and the tool-aware framing share one predicate over every fixture" {
                for def in fixtureSet do
                    Expect.equal
                        (CompositionManifest.isLiveInterfaceTool def)
                        (RAGPromptBuilder.ToolFraming.fromTools [ def ]).HasLiveUiTools
                        $"the manifest and the framing disagree about {def.Name}"

                    Expect.equal
                        (not (CompositionManifest.liveInterfaceToolsOf [ def ]).IsEmpty)
                        (CompositionManifest.isLiveInterfaceTool def)
                        $"the derivation and the predicate disagree about {def.Name}"

                // Not vacuous: the fixture set exercises both answers.
                Expect.equal
                    (fixtureSet |> List.filter CompositionManifest.isLiveInterfaceTool |> List.length)
                    2
                    "two fixtures are live-interface tools and two are not"
            }
        ]

        // ─── 860.B — not in the seal ─────────────────────────────────

        testList "the seal" [
            test "a composition with no live-interface tools canonicalises to its pre-860 bytes" {
                let current =
                    sealFixture noLiveToolNames |> CompositionManifest.withLiveInterfaceTools []

                Expect.equal current.SchemaVersion 3 "projected at the new schema"

                Expect.equal
                    (BootVerificationPreflight.compositionCanonicalForm current)
                    PreNoLiveCanonicalForm
                    "the schema step and the empty field leave the sealed bytes where they were"

                Expect.equal
                    (BootVerificationPreflight.compositionCanonicalForm (asSchema2 current))
                    PreNoLiveCanonicalForm
                    "and the schema-2 projection of the same composition still canonicalises there"
            }

            test "a composition with live-interface tools canonicalises to its pre-860 bytes" {
                let current =
                    sealFixture liveToolNames
                    |> CompositionManifest.withLiveInterfaceTools [ "host.read_view"; "client.pick_colour" ]

                Expect.isNonEmpty current.LiveInterfaceTools "the field is populated"

                Expect.equal
                    (BootVerificationPreflight.compositionCanonicalForm current)
                    PreWithLiveCanonicalForm
                    "a populated field leaves the sealed bytes where they were"

                Expect.equal
                    (BootVerificationPreflight.bindingCanonicalBytes (
                        BootVerificationPreflight.bindingFor baseRecord current
                    ))
                    (BootVerificationPreflight.bindingCanonicalBytes (
                        BootVerificationPreflight.bindingFor baseRecord (asSchema2 current)
                    ))
                    "the binding bytes a seal is minted over are identical at schema 2 and 3"
            }

            test "compare ignores the field" {
                let empty =
                    sealFixture liveToolNames |> CompositionManifest.withLiveInterfaceTools []

                let populated =
                    sealFixture liveToolNames
                    |> CompositionManifest.withLiveInterfaceTools [ "host.read_view" ]

                Expect.isEmpty (BootVerificationPreflight.compare empty populated) "gaining the posture is not drift"
                Expect.isEmpty (BootVerificationPreflight.compare populated empty) "nor is losing it"

                Expect.isEmpty
                    (BootVerificationPreflight.compare (asSchema2 populated) populated)
                    "a schema-2 recording against a schema-3 observation is not drift"

                Expect.isEmpty
                    (BootVerificationPreflight.unrecorded (asSchema2 populated) populated)
                    "and nothing is reported unrecorded for it"
            }

            test "a binding sealed at schema 2 still verifies against this binary's projection, with no drift" {
                let observed = ServerApp.compositionManifest (appWith fixtureSet)
                let recordedAtSchema2 = asSchema2 observed
                let sealedRecord, sealedBinding = sealPair baseRecord recordedAtSchema2

                match
                    BootVerificationPreflight.verify (optionsFor sealedRecord sealedBinding) observed
                    |> Async.RunSynchronously
                with
                | BootVerificationVerdict.Verified -> ()
                | other -> failtestf "a schema-2 binding stopped verifying after the schema step: %A" other
            }
        ]

        // ─── 860.C — readiness without RAG ───────────────────────────

        testList "readiness" [
            test "the manifest-derived posture reports the tools, with framing a RAG fact" {
                let posture =
                    DeploymentReadinessReport.liveInterfaceFromManifest [ "host.read_view" ]

                Expect.isTrue posture.Composed "a posture was reported"
                Expect.isTrue posture.HasLiveInterfaceTools "the tool is reported"
                Expect.equal posture.LiveInterfaceTools [ "host.read_view" ] "by name"
                Expect.equal posture.GroundingMode "" "grounding mode is a RAG fact"
                Expect.isFalse posture.ToolAwareFramingActive "and so is the framing"

                Expect.equal
                    (DeploymentReadiness.LiveInterfaceSummary.render posture)
                    ("UI-awareness: on (1 live-interface tool(s): host.read_view); tool-aware framing inactive — "
                     + DeploymentReadinessReport.ManifestPostureNote)
                    "the rendering copes with an empty grounding mode"

                Expect.equal
                    (DeploymentReadiness.LiveInterfaceSummary.render (
                        DeploymentReadinessReport.liveInterfaceFromManifest []
                    ))
                    ("UI-awareness: off (no live-interface tools); tool-aware framing inactive — "
                     + DeploymentReadinessReport.ManifestPostureNote)
                    "and with no tool at all"
            }

            test "a deployment with live-interface tools and no RAG reports them in readiness" {
                let manifest = ServerApp.compositionManifest (appWith fixtureSet)

                let reported =
                    reportedPosture (
                        platformAdminContext (fun services ->
                            DeploymentReadinessReport.registerManifestLiveInterface
                                (CompositionManifest.liveInterfaceTools manifest)
                                services
                            |> ignore)
                    )

                Expect.isTrue reported.Composed "no longer notComposed without RAG"
                Expect.isTrue reported.HasLiveInterfaceTools "HasLiveInterfaceTools = true"
                Expect.equal reported.LiveInterfaceTools [ "client.pick_colour"; "host.read_view" ] "named"
            }

            test "a RAG composition's own summary wins, whichever is registered first" {
                let rag =
                    DeploymentReadiness.LiveInterfaceSummary.derive [ "host.read_view" ] "Preferred" true

                let ragFirst =
                    reportedPosture (
                        platformAdminContext (fun services ->
                            services.AddSingleton<DeploymentReadiness.LiveInterfaceSummary>(rag) |> ignore

                            DeploymentReadinessReport.registerManifestLiveInterface [ "client.pick_colour" ] services
                            |> ignore)
                    )

                Expect.equal ragFirst rag "the manifest registration does not displace RAG's"

                let manifestFirst =
                    reportedPosture (
                        platformAdminContext (fun services ->
                            DeploymentReadinessReport.registerManifestLiveInterface [ "client.pick_colour" ] services
                            |> ignore

                            services.AddSingleton<DeploymentReadiness.LiveInterfaceSummary>(rag) |> ignore)
                    )

                Expect.equal manifestFirst rag "RAG's registration resolves when it lands after"
            }
        ]
    ]