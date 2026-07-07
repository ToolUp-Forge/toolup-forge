module ToolUp.Platform.Tests.InProcess.CompositionValidatorTests

open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
// DataType + its DataTypeInfo record live here; the explicit open pins the
// record-field labels (Info / Detect / Process) so the well-formed-app
// fixture resolves unambiguously against DataType rather than a same-named
// field on another record in scope.
open ToolUp.Platform.FileProcessor

// ─── Phase 281 — composition well-formedness preflight ────────────────
//
// Covers the acceptance shape: a composition with a duplicate ComponentId,
// an illegal companion-slot combination, or an orphaned tool reference
// each fails preflight with a readable, alternative-enumerating message; a
// well-formed composition passes silently. The pure rule core (`check` /
// `checkWith`) is exercised directly over crafted manifests; the
// `IConfigValidator` wrapper's `Validate` mapping is exercised too.

let private validatorResult (manifest: CompositionManifest) (refs: CompositionReferences) : ValidationResult =
    (CompositionValidator.CompositionWellFormednessValidator(manifest, refs) :> IConfigValidator).Validate()
    |> Async.RunSynchronously

/// A raw companion-impl entry with a caller-chosen Id — lets a test place
/// two impls in one slot sharing a sub-id but with distinct Ids, isolating
/// the slot-legality rule from the global duplicate-id rule.
let private companionImpl (rawId: string) (slot: string) (subId: string) : ComponentEntry = {
    Id = ComponentId.create rawId
    Kind = CompanionComponent
    Label = slot
    Impl = Some subId
}

let private hasRule (code: string) (defects: CompositionDefect list) : bool =
    defects |> List.exists (fun d -> d.RuleCode = code)

let tests =
    testList "CompositionValidator" [

        // ── duplicate ComponentId → Error, enumerating the colliders ──
        testCase "duplicate ComponentId fails with an alternative-enumerating message"
        <| fun _ ->
            let manifest =
                CompositionManifest.build
                    [
                        CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "shared")
                        CompositionManifest.moduleEntry ("Inventory", ComponentId.ofModule "shared")
                    ]
                    []
                    [] [] []

            let defects = CompositionValidator.check manifest

            Expect.isTrue (hasRule "duplicate-component-id" defects) "the duplicate-id rule fired"

            let message =
                defects
                |> List.find (fun d -> d.RuleCode = "duplicate-component-id")
                |> _.Message

            Expect.stringContains message (ComponentId.value (ComponentId.ofModule "shared")) "names the colliding id"
            Expect.stringContains message "Orders" "enumerates the first collider"
            Expect.stringContains message "Inventory" "enumerates the second collider"
            Expect.stringContains message "withComponentId" "points at the disambiguation surface"

            match validatorResult manifest CompositionReferences.empty with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other

        // ── companion-slot legality: duplicate sub-id in one slot ────
        testCase "two companion impls sharing a slot sub-id fail slot legality"
        <| fun _ ->
            // Distinct Ids so ONLY the slot-legality rule fires (not the
            // global duplicate-id rule), isolating the check.
            let manifest =
                CompositionManifest.build
                    []
                    [
                        companionImpl "companion:IAuditSink/archive#a" "IAuditSink" "archive"
                        companionImpl "companion:IAuditSink/archive#b" "IAuditSink" "archive"
                    ]
                    [] [] []

            let defects = CompositionValidator.check manifest

            Expect.isTrue (hasRule "companion-slot-legality" defects) "the slot-legality rule fired"
            Expect.isFalse (hasRule "duplicate-component-id" defects) "distinct Ids — duplicate-id rule stays quiet"

            let message =
                defects
                |> List.find (fun d -> d.RuleCode = "companion-slot-legality")
                |> _.Message

            Expect.stringContains message "IAuditSink" "names the offending slot"
            Expect.stringContains message "archive" "names the shared sub-id"

            match validatorResult manifest CompositionReferences.empty with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other

        // ── orphaned tool reference → Error naming valid modules ─────
        testCase "a tool whose SourceModule names no registered module is orphaned"
        <| fun _ ->
            let manifest =
                CompositionManifest.build
                    [ CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "Orders") ]
                    []
                    [] [ CompositionManifest.toolEntry "orders.run" ] []

            let refs = {
                ToolSources = [ "orders.run", "Ghost" ]
            }

            let defects = CompositionValidator.checkWith refs manifest

            Expect.isTrue (hasRule "orphaned-tool-reference" defects) "the orphaned-reference rule fired"

            let message =
                defects
                |> List.find (fun d -> d.RuleCode = "orphaned-tool-reference")
                |> _.Message

            Expect.stringContains message "orders.run" "names the tool"
            Expect.stringContains message "Ghost" "names the unresolved source"
            Expect.stringContains message "Orders" "enumerates the registered module as an alternative"

            match validatorResult manifest refs with
            | Error _ -> ()
            | other -> failtestf "expected Error, got %A" other

        // ── a tool owned by a registered module resolves cleanly ─────
        testCase "a tool whose SourceModule matches a registered module is not orphaned"
        <| fun _ ->
            let manifest =
                CompositionManifest.build
                    [ CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "Orders") ]
                    []
                    [] [ CompositionManifest.toolEntry "orders.run" ] []

            let refs = {
                ToolSources = [ "orders.run", "Orders" ]
            }

            Expect.isFalse
                (hasRule "orphaned-tool-reference" (CompositionValidator.checkWith refs manifest))
                "a tool owned by a composed module is well-formed"

        // ── reserved / platform sources are exempt from the orphan rule ─
        testCase "a reserved platform SourceModule is exempt from the orphan rule"
        <| fun _ ->
            let manifest =
                CompositionManifest.build [] [] [] [ CompositionManifest.toolEntry "summarise" ] []

            let refs = {
                ToolSources = [ "summarise", "_platform.ai"; "narrate", "ToolUp.Platform" ]
            }

            Expect.isEmpty
                (CompositionValidator.checkWith refs manifest)
                "reserved '_'-prefixed and ToolUp.Platform sources resolve to no module by design"

        // ── well-formed composition passes silently (GP 11) ──────────
        testCase "a well-formed composition yields no defects and validates Ok"
        <| fun _ ->
            let stubDataType (id: string) : DataType = {
                Info = {
                    Id = id
                    DisplayName = id
                    Schema = None
                }
                Id = id
                Detect = fun _ -> async { return false }
                Process = fun _ -> async { return failwith "stub Process never called" }
            }

            let stubTool (name: string) (source: string) : AIToolDefinition * (HttpContext -> string -> Async<string>) =
                {
                    Name = name
                    Description = ""
                    Parameters = []
                    SourceModule = source
                    EmitsActions = None
                    Location = ServerResident
                    Surface = Both
                },
                (fun _ _ -> async { return "" })

            let orders =
                ServerModule.create "Orders"
                |> ServerModule.withComponentId "orders-service"
                |> ServerModule.withDataTypes [ stubDataType "SalesData" ]
                |> ServerModule.withAITools [ stubTool "orders.run" "Orders" ]

            let app =
                ServerApp.empty
                |> ServerApp.addModules [ orders; ServerModule.create "Inventory" ]
                |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

            let manifest = ServerApp.compositionManifest app

            let refs = {
                ToolSources = app.AITools |> List.map (fun (def, _) -> def.Name, def.SourceModule)
            }

            Expect.isEmpty (CompositionValidator.checkWith refs manifest) "a well-formed composition has no defects"

            match validatorResult manifest refs with
            | Ok -> ()
            | other -> failtestf "expected Ok, got %A" other

        // ── the validator is non-security-class (SkipPreflight bypasses) ─
        testCase "the composition validator is not a security-class validator"
        <| fun _ ->
            let manifest = CompositionManifest.empty
            let refs = { ToolSources = [] }

            let validator =
                CompositionValidator.CompositionWellFormednessValidator(manifest, refs) :> IConfigValidator

            Expect.isFalse
                (match box validator with
                 | :? ISecurityClassValidator -> true
                 | _ -> false)
                "well-formedness is bypassable by the SkipPreflight emergency-boot lever (GP 11)"
    ]