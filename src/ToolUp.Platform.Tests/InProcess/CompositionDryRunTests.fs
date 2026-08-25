module ToolUp.Platform.Tests.InProcess.CompositionDryRunTests

open System
open System.Diagnostics
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Phase 436 — null-composition dry-run harness ─────────────────────
//
// Covers the acceptance shape: a valid preset descriptor dry-runs green
// with every companion slot rebound to its in-process default; a
// descriptor with a missing null default / an unresolved id / a broken
// lifecycle edge / a failing well-formedness rule each yields the NAMED
// finding with `ComponentId` attribution rather than an exception; and the
// minimal preset's wall clock stays in unit-test territory (no service
// waits, because nothing external is reachable by construction).

// A minimal `DataType` whose `Detect` / `Process` are never invoked — the
// dry run reads only its `Id`.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by a dry run" }
}

let private stubTool
    (name: string)
    (sourceModule: string)
    : AIToolDefinition * (HttpContext -> string -> Async<string>) =
    {
        Name = name
        Description = ""
        Parameters = []
        SourceModule = sourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
    },
    (fun _ _ -> async { return "" })

let private ordersModule () : ServerModule =
    ServerModule.create "Orders"
    |> ServerModule.withComponentId "orders-service"
    |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
    |> ServerModule.withAITools [ stubTool "orders.run" "Orders" ]

let private inventoryModule () : ServerModule = ServerModule.create "Inventory"

// A module whose tool declares a SourceModule that is never composed —
// the `orphaned-tool-reference` structural rule's trigger.
let private orphanModule () : ServerModule =
    ServerModule.create "Orphan"
    |> ServerModule.withAITools [ stubTool "orphan.run" "NeverComposed" ]

let private catalogue () : RegistrationCatalogue =
    RegistrationCatalogue.empty
    |> RegistrationCatalogue.addModule (ordersModule ())
    |> RegistrationCatalogue.addModule (inventoryModule ())
    |> RegistrationCatalogue.addModule (orphanModule ())

let private ordersId = ComponentId.ofModule "orders-service"
let private inventoryId = ComponentId.ofModule "Inventory"

/// The valid preset: two modules plus an externally-bound blob-storage
/// companion (an S3 impl, complete with a bucket input nothing can reach)
/// and an audit sink. Null binding is what makes it dry-runnable.
let private presetDescriptor () : CompositionDescriptor =
    CompositionDescriptor.create
        [
            CompositionDescriptor.select ordersId
            CompositionDescriptor.select inventoryId
            CompositionDescriptor.selectWith (ComponentId.forCompanionSlot "IBlobStorage") [
                "bucket", "s3://production-bucket"
                "region", "eu-west-2"
            ]
            CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "s3-archive")
            CompositionDescriptor.select (ComponentId.forCompanionImpl "IAuditSink" "splunk-hec")
        ]
        ServerConfig.defaults

let private findingCodes (report: CompositionDryRunReport) : string list =
    report.ReportFindings |> List.map _.FindingCode

let private hasKind (kind: CompositionDryRunFindingKind) (report: CompositionDryRunReport) : bool =
    report.ReportFindings |> List.exists (fun f -> f.FindingKind = kind)

let tests =
    testList "CompositionDryRun" [

        // ── 436.A — null-binding resolution ──────────────────────────

        testCase "bindNulls rebinds every companion selection to its bare slot id with inputs cleared"
        <| fun _ ->
            let bound = CompositionDryRun.bindNulls (presetDescriptor ())

            let companionSelections =
                bound.Components
                |> List.filter (fun s -> (CompositionDryRun.slotInterface s.Id).IsSome)

            Expect.equal
                (companionSelections |> List.map _.Id)
                [
                    ComponentId.forCompanionSlot "IBlobStorage"
                    ComponentId.forCompanionSlot "IAuditSink"
                ]
                "both audit-sink impls collapse to one null binding on the IAuditSink slot; IBlobStorage keeps its slot id"

            Expect.isTrue
                (companionSelections |> List.forall (fun s -> Map.isEmpty s.Inputs))
                "the S3 bucket / region inputs are cleared — a null binding takes no binding parameters"

            Expect.equal
                (bound.Components
                 |> List.filter (fun s -> (CompositionDryRun.slotInterface s.Id).IsNone)
                 |> List.map _.Id)
                [ ordersId; inventoryId ]
                "module selections pass through untouched, in declaration order"

        testCase "bindNulls is idempotent"
        <| fun _ ->
            let once = CompositionDryRun.bindNulls (presetDescriptor ())
            let twice = CompositionDryRun.bindNulls once
            Expect.equal twice once "null-binding an already-null-bound descriptor is a no-op"

        testCase "bindNulls fills every unbound descriptor hole with the empty filling"
        <| fun _ ->
            let preset =
                CompositionDescriptor.create [ CompositionDescriptor.select ordersId ] ServerConfig.defaults
                |> CompositionDescriptor.withHoles [ "storage"; "identity" ]

            let bound, _, filled, findings = CompositionDryRun.bindNullsWithFindings preset

            Expect.isEmpty (CompositionDescriptor.unfilledHoles bound) "no hole is left unbound"
            Expect.equal filled [ "storage"; "identity" ] "both filled holes are reported, in declaration order"
            Expect.isEmpty findings "an unfilled hole has a null default (the empty filling) — it is not a finding"

        testCase "the null-bindable slot universe is derived from the reflected composable surface"
        <| fun _ ->
            let bindable = CompositionDryRun.nullBindableInterfaces ()

            Expect.equal
                bindable
                (ComposableSurface.slots () |> List.map _.Interface |> Set.ofList)
                "the universe is exactly Phase 293's reflected slot set — no hand-maintained table to drift"

            Expect.isTrue (bindable.Contains "IBlobStorage") "a representative single-impl slot is null-bindable"
            Expect.isTrue (bindable.Contains "IAuditSink") "a representative multi-impl slot is null-bindable"

        testCase "slotInterface parses both slot and multi-impl companion ids, and nothing else"
        <| fun _ ->
            Expect.equal
                (CompositionDryRun.slotInterface (ComponentId.forCompanionSlot "IBlobStorage"))
                (Some "IBlobStorage")
                "a bare slot id parses"

            Expect.equal
                (CompositionDryRun.slotInterface (ComponentId.forCompanionImpl "IAuditSink" "s3-archive"))
                (Some "IAuditSink")
                "a multi-impl id parses to its interface, not its sub-id"

            Expect.isNone (CompositionDryRun.slotInterface ordersId) "a module id is not a companion slot"
            Expect.isNone (CompositionDryRun.slotInterface (ComponentId.forDataType "SalesData")) "nor is a datatype id"

        // ── 436.A — a slot with no null default is a FINDING ─────────

        testCase "a companion interface forge declares no slot for is a named finding, not a crash"
        <| fun _ ->
            // IFactStore is a composition-WRAPPING companion (Phase 526), so
            // it is not a field on ServerApp and has no in-process default to
            // rebind to. This is the case 436.A names.
            let descriptor =
                CompositionDescriptor.create
                    [
                        CompositionDescriptor.select ordersId
                        CompositionDescriptor.select (ComponentId.forCompanionSlot "IFactStore")
                    ]
                    ServerConfig.defaults

            let report = CompositionDryRun.run (catalogue ()) descriptor

            Expect.equal report.ReportVerdict DoesNotCompose "a slot with no null default fails the run"
            Expect.isTrue (hasKind MissingNullDefault report) "the finding is MissingNullDefault"

            let finding =
                report.ReportFindings |> List.find (fun f -> f.FindingKind = MissingNullDefault)

            Expect.equal finding.FindingCode CompositionDryRun.MissingNullDefaultCode "the stable code is carried"

            Expect.equal
                finding.FindingComponent
                (Some(ComponentId.forCompanionSlot "IFactStore"))
                "the finding is attributed to the offending ComponentId"

            Expect.stringContains finding.FindingDetail "IFactStore" "the message names the interface"

        // ── 436.B — a valid preset dry-runs green ────────────────────

        testCase "a valid preset descriptor dry-runs green with every slot null-bound"
        <| fun _ ->
            let report = CompositionDryRun.run (catalogue ()) (presetDescriptor ())

            Expect.equal report.ReportVerdict Composes (CompositionDryRun.renderReport report)
            Expect.isEmpty report.ReportFindings "a well-formed composition yields no findings"

            Expect.equal
                report.ReportNullBoundSlots
                [
                    ComponentId.forCompanionSlot "IBlobStorage"
                    ComponentId.forCompanionSlot "IAuditSink"
                ]
                "the report says exactly which bindings were replaced — the S3 storage and the audit sinks"

        testCase "the composed component set is driven through the lifecycle, disposed in reverse init order"
        <| fun _ ->
            let report = CompositionDryRun.run (catalogue ()) (presetDescriptor ())

            // The null-bound companions contribute nothing to the composed
            // app (leaving a slot None IS its default), so the composed set
            // is the two modules plus their transitive datatype + tool.
            Expect.equal
                (report.ReportInitOrder |> List.map ComponentId.value |> List.sort)
                [
                    "datatype:SalesData"
                    "module:Inventory"
                    "module:orders-service"
                    "tool:orders.run"
                ]
                "every composed unit — modules and their module-derived datatypes / tools — is initialised"

            Expect.equal
                report.ReportDisposeOrder
                (List.rev report.ReportInitOrder)
                "dispose is the init order reversed (Phase 291)"

            Expect.isTrue
                (report.ReportComponents
                 |> List.forall (fun c -> c.OutcomeInitialised && c.OutcomeDisposed))
                "every component both initialised and disposed"

        testCase "declared init-before edges reorder init and reverse dispose"
        <| fun _ ->
            let report =
                CompositionDryRun.options (catalogue ())
                |> CompositionDryRun.withEdges [ inventoryId, ordersId ]
                |> fun opts -> CompositionDryRun.runWith opts (presetDescriptor ())

            Expect.equal report.ReportVerdict Composes (CompositionDryRun.renderReport report)

            let inventoryPosition = report.ReportInitOrder |> List.findIndex ((=) inventoryId)
            let ordersPosition = report.ReportInitOrder |> List.findIndex ((=) ordersId)

            Expect.isLessThan
                inventoryPosition
                ordersPosition
                "the declared edge puts Inventory before Orders in the init sequence"

            Expect.isGreaterThan
                (report.ReportDisposeOrder |> List.findIndex ((=) inventoryId))
                (report.ReportDisposeOrder |> List.findIndex ((=) ordersId))
                "and therefore after it in dispose"

        // ── 436.B — a broken lifecycle edge is a named finding ───────

        testCase "a cyclic lifecycle order is a named finding and initialises nothing"
        <| fun _ ->
            let report =
                CompositionDryRun.options (catalogue ())
                |> CompositionDryRun.withEdges [ ordersId, inventoryId; inventoryId, ordersId ]
                |> fun opts -> CompositionDryRun.runWith opts (presetDescriptor ())

            Expect.equal report.ReportVerdict DoesNotCompose "a cycle cannot compose"
            Expect.isTrue (hasKind LifecycleOrderUnsatisfiable report) "the finding names the lifecycle order"

            Expect.equal
                (findingCodes report)
                [ CompositionDryRun.LifecycleOrderCode ]
                "the stable dry-run-lifecycle-order code is carried"

            Expect.isEmpty report.ReportComponents "no component was initialised under an unsatisfiable order"
            Expect.isEmpty report.ReportInitOrder "and no init order was produced"

        testCase
            "a throwing init probe is a finding attributed to its component, and everything before it still disposes"
        <| fun _ ->
            let report =
                CompositionDryRun.options (catalogue ())
                |> CompositionDryRun.withEdges [ inventoryId, ordersId ]
                |> CompositionDryRun.withInitProbe (fun id ->
                    if id = ordersId then
                        failwith "orders init blew up")
                |> fun opts -> CompositionDryRun.runWith opts (presetDescriptor ())

            Expect.equal report.ReportVerdict DoesNotCompose "a failed init does not compose"
            Expect.isTrue (hasKind ComponentInitFailed report) "the finding names the failed init"

            let finding =
                report.ReportFindings
                |> List.find (fun f -> f.FindingKind = ComponentInitFailed)

            Expect.equal finding.FindingComponent (Some ordersId) "attributed to the throwing component"
            Expect.stringContains finding.FindingDetail "orders init blew up" "the underlying message is carried"

            Expect.isTrue
                (report.ReportComponents
                 |> List.exists (fun c -> c.OutcomeComponent = inventoryId && c.OutcomeInitialised && c.OutcomeDisposed))
                "the component that initialised before the failure is still disposed"

            Expect.isFalse
                (report.ReportComponents
                 |> List.exists (fun c -> c.OutcomeComponent = ordersId && c.OutcomeInitialised))
                "the throwing component is recorded as not initialised"

        // ── 436.B — an unresolved id is a named finding ──────────────

        testCase "an unresolved component id is a finding per id, never an exception"
        <| fun _ ->
            let descriptor =
                CompositionDescriptor.create
                    [
                        CompositionDescriptor.select ordersId
                        CompositionDescriptor.select (ComponentId.ofModule "nowhere")
                        CompositionDescriptor.select (ComponentId.ofModule "also-nowhere")
                    ]
                    ServerConfig.defaults

            let report = CompositionDryRun.run (catalogue ()) descriptor

            Expect.equal report.ReportVerdict DoesNotCompose "an unresolved id does not compose"

            Expect.equal
                (report.ReportFindings |> List.choose _.FindingComponent)
                [ ComponentId.ofModule "nowhere"; ComponentId.ofModule "also-nowhere" ]
                "one finding per unresolved id — the whole build failure is reported in one pass"

            Expect.isTrue
                (report.ReportFindings
                 |> List.forall (fun f -> f.FindingKind = UnresolvedComponent))
                "each is an UnresolvedComponent finding"

        testCase "a too-new descriptor schema version is a migration finding"
        <| fun _ ->
            let descriptor =
                CompositionDescriptor.createVersioned
                    (CompositionDescriptor.CurrentSchemaVersion + 7)
                    [ CompositionDescriptor.select ordersId ]
                    ServerConfig.defaults

            let report = CompositionDryRun.run (catalogue ()) descriptor

            Expect.equal report.ReportVerdict DoesNotCompose "a version gap does not compose"
            Expect.isTrue (hasKind SchemaMigrationRejected report) "the finding names the schema migration"
            Expect.isEmpty report.ReportComponents "nothing was built, so nothing was initialised"

        // ── 436.B — a failed well-formedness rule is a named finding ─

        testCase "a failed Phase 281 / 294 rule surfaces as a finding carrying the rule's own code and message"
        <| fun _ ->
            // The Orphan module's tool declares SourceModule "NeverComposed",
            // which resolves to no registered module — the shipped
            // `orphaned-tool-reference` structural rule.
            let descriptor =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "Orphan") ]
                    ServerConfig.defaults

            let report = CompositionDryRun.run (catalogue ()) descriptor

            Expect.equal report.ReportVerdict DoesNotCompose "an error-severity rule defect does not compose"
            Expect.isTrue (hasKind WellFormednessDefect report) "the finding is a well-formedness defect"

            Expect.equal
                (findingCodes report)
                [ "orphaned-tool-reference" ]
                "the finding carries the rule's OWN stable code — the same token ruleManifest publishes"

            let finding = List.head report.ReportFindings
            Expect.equal finding.FindingSeverity DefectError "the rule's declared severity carries through"
            Expect.stringContains finding.FindingDetail "NeverComposed" "the rule message is carried verbatim"

        testCase "a duplicate ComponentId defect is attributed to the colliding component"
        <| fun _ ->
            // Two modules resolving to the same explicit id — the shipped
            // `duplicate-component-id` rule. Registered directly (rather than
            // via addModule, which keys the catalogue by the resolved id and
            // would therefore collapse them into one entry).
            let collidingCatalogue =
                RegistrationCatalogue.empty
                |> RegistrationCatalogue.add (ComponentId.ofModule "twin") (fun _ app ->
                    app
                    |> ServerApp.addModule (ServerModule.create "First" |> ServerModule.withComponentId "twin")
                    |> ServerApp.addModule (ServerModule.create "Second" |> ServerModule.withComponentId "twin"))

            let descriptor =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "twin") ]
                    ServerConfig.defaults

            let report = CompositionDryRun.run collidingCatalogue descriptor

            Expect.equal report.ReportVerdict DoesNotCompose "a duplicate identity does not compose"

            Expect.equal (findingCodes report) [ "duplicate-component-id" ] "the rule code names the invariant"

            Expect.equal
                (List.head report.ReportFindings).FindingComponent
                (Some(ComponentId.ofModule "twin"))
                "the defect is attributed to the colliding ComponentId"

        // ── 436.B — nothing external is reachable ────────────────────

        testCase "the null catalogue overlays every composable slot, so a vendor binding is never reached"
        <| fun _ ->
            let mutable vendorBindingReached = false

            let vendorCatalogue =
                catalogue ()
                |> RegistrationCatalogue.add (ComponentId.forCompanionSlot "IBlobStorage") (fun _ app ->
                    vendorBindingReached <- true
                    app)

            let report = CompositionDryRun.run vendorCatalogue (presetDescriptor ())

            Expect.equal report.ReportVerdict Composes (CompositionDryRun.renderReport report)

            Expect.isFalse
                vendorBindingReached
                "the caller's IBlobStorage registration was overlaid by the null binding and never invoked"

        // ── 436.C — the Expecto affordance ───────────────────────────

        testCase "DryRun.shouldCompose passes a valid preset and returns its report"
        <| fun _ ->
            let report = DryRun.shouldCompose (catalogue ()) (presetDescriptor ())
            Expect.equal report.ReportVerdict Composes "the returned report is the passing one"

        testCase "DryRun.shouldCompose raises the rendered report on a defect"
        <| fun _ ->
            let broken =
                CompositionDescriptor.create
                    [ CompositionDescriptor.select (ComponentId.ofModule "nowhere") ]
                    ServerConfig.defaults

            Expect.throwsC (fun () -> DryRun.shouldCompose (catalogue ()) broken |> ignore) (fun ex ->
                Expect.stringContains ex.Message "DOES NOT COMPOSE" "the raised text is the rendered verdict"

                Expect.stringContains
                    ex.Message
                    CompositionDryRun.UnresolvedComponentCode
                    "and it carries the greppable finding code")

        testCase "DryRun.shouldComposeWithin fails a composition that overruns its offline budget"
        <| fun _ ->
            // A zero budget can never be met, which is the point: the guard
            // fires on wall clock, not on the verdict.
            Expect.throws
                (fun () ->
                    DryRun.shouldComposeWithin TimeSpan.Zero (catalogue ()) (presetDescriptor ())
                    |> ignore)
                "an otherwise-clean composition still fails a budget it overran"

        // ── 436.D — offline speed ────────────────────────────────────

        testCase "the minimal preset dry-runs in unit-test time"
        <| fun _ ->
            // Warm the reflection cache + JIT the way any second call in a
            // suite would, then measure the steady-state cost.
            CompositionDryRun.run (catalogue ()) (presetDescriptor ()) |> ignore

            let watch = Stopwatch.StartNew()
            let report = CompositionDryRun.run (catalogue ()) (presetDescriptor ())
            watch.Stop()

            Expect.equal report.ReportVerdict Composes (CompositionDryRun.renderReport report)

            Expect.isLessThan
                watch.ElapsedMilliseconds
                250L
                "a null composition answers in unit-test time — nothing external is reachable, so there is nothing to wait for"

            Expect.isLessThan report.ReportElapsedMicros 250_000L "the report's own wall clock agrees"

        testCase "a consumer that never dry-runs is unaffected — the harness registers nothing"
        <| fun _ ->
            // GP 11 / GP 13: the dry run builds its own throwaway app from the
            // descriptor. The manifest of a fluently-composed app is
            // unchanged by the fact that a dry run happened.
            let fluent =
                ServerApp.empty |> ServerApp.addModules [ ordersModule (); inventoryModule () ]

            let before = ServerApp.compositionManifest fluent
            CompositionDryRun.run (catalogue ()) (presetDescriptor ()) |> ignore
            let after = ServerApp.compositionManifest fluent

            Expect.equal after before "composing is untouched by a dry run — the harness has no compose-time footprint"
    ]