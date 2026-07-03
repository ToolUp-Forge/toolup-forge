module ToolUp.Platform.Tests.InProcess.CompositionDescriptorVersionTests

open Expecto
open ToolUp.Platform

// ─── Phase 292 — descriptor schema-version + migration ────────────────
//
// Covers the acceptance shape: an older-version `CompositionDescriptor`
// migrates and composes equivalently to a current-version one; a too-new
// version fails with a readable version-gap message (never silently
// mis-loads); a current-version descriptor migrates as a byte-identical
// no-op. `ServerApp.ofManifest` runs `migrate` before composing.

// A trivial module + a catalogue that registers it, so a descriptor
// selecting its id resolves and composes.
let private wares () : ServerModule = ServerModule.create "Wares"

let private catalogue () : RegistrationCatalogue =
    RegistrationCatalogue.empty |> RegistrationCatalogue.addModule (wares ())

let private selection () : ComponentSelection =
    CompositionDescriptor.select (ComponentId.ofModule "Wares")

let tests =
    testList "CompositionDescriptorVersion" [

        // ── create stamps the current schema version ──────────────────
        testCase "create stamps CurrentSchemaVersion"
        <| fun _ ->
            let d = CompositionDescriptor.create [ selection () ] ServerConfig.defaults

            Expect.equal
                d.Version
                CompositionDescriptor.CurrentSchemaVersion
                "a freshly-authored descriptor carries the current schema version"

        // ── a current-version descriptor is a no-op migrate ───────────
        testCase "migrating a current-version descriptor is a no-op"
        <| fun _ ->
            let d = CompositionDescriptor.create [ selection () ] ServerConfig.defaults

            match CompositionDescriptorVersion.migrate d with
            | Ok migrated -> Expect.equal migrated d "a current-version descriptor migrates unchanged"
            | Error e -> failtestf "expected a no-op Ok, got %A" e

        // ── an older descriptor migrates + composes equivalently ──────
        testCase "an older-version descriptor migrates and composes equivalently to a current one"
        <| fun _ ->
            let legacy =
                CompositionDescriptor.createVersioned 0 [ selection () ] ServerConfig.defaults

            let current = CompositionDescriptor.create [ selection () ] ServerConfig.defaults

            // Migration lifts the legacy descriptor to the current version.
            match CompositionDescriptorVersion.migrate legacy with
            | Error e -> failtestf "legacy descriptor failed to migrate: %A" e
            | Ok migrated ->
                Expect.equal
                    migrated.Version
                    CompositionDescriptor.CurrentSchemaVersion
                    "the legacy descriptor is upgraded to the current version"

            // Both compose (via the versioned path) to the same manifest.
            let legacyApp = ServerApp.ofManifest (catalogue (), legacy)
            let currentApp = ServerApp.ofManifest (catalogue (), current)

            Expect.equal
                (ServerApp.compositionManifest legacyApp
                 |> CompositionManifest.allComponents
                 |> List.map _.Id)
                (ServerApp.compositionManifest currentApp
                 |> CompositionManifest.allComponents
                 |> List.map _.Id)
                "the migrated legacy descriptor composes the same components as the current one"

        // ── a too-new version fails with a readable version-gap error ─
        testCase "a too-new descriptor version fails with a readable version-gap message"
        <| fun _ ->
            let future =
                CompositionDescriptor.createVersioned
                    (CompositionDescriptor.CurrentSchemaVersion + 1)
                    [ selection () ]
                    ServerConfig.defaults

            match CompositionDescriptorVersion.migrate future with
            | Ok _ -> failtest "expected a DescriptorTooNew error for a future version"
            | Error(DescriptorTooNew(found, current)) ->
                Expect.equal found (CompositionDescriptor.CurrentSchemaVersion + 1) "reports the found version"
                Expect.equal current CompositionDescriptor.CurrentSchemaVersion "reports the current version"

                let message =
                    CompositionDescriptorVersion.renderMigrationError (DescriptorTooNew(found, current))

                Expect.stringContains message "newer than this forge" "the message names the version gap"
            | Error other -> failtestf "expected DescriptorTooNew, got %A" other

        // ── an unknown (negative) version fails readably ──────────────
        testCase "a negative version is rejected as unknown"
        <| fun _ ->
            let corrupt =
                CompositionDescriptor.createVersioned -1 [ selection () ] ServerConfig.defaults

            match CompositionDescriptorVersion.migrate corrupt with
            | Error(UnknownDescriptorVersion found) -> Expect.equal found -1 "reports the offending version"
            | other -> failtestf "expected UnknownDescriptorVersion, got %A" other

        // ── ServerApp.ofManifest runs migrate before composing ────────
        testCase "ServerApp.ofManifest raises the version-gap message on a too-new descriptor"
        <| fun _ ->
            let future =
                CompositionDescriptor.createVersioned
                    (CompositionDescriptor.CurrentSchemaVersion + 1)
                    [ selection () ]
                    ServerConfig.defaults

            Expect.throwsC (fun () -> ServerApp.ofManifest (catalogue (), future) |> ignore) (fun ex ->
                Expect.stringContains ex.Message "newer than this forge" "raises the readable version-gap message")

        // ── the versioned total path surfaces a migration failure as data ─
        testCase "CompositionDescriptorVersion.ofManifest returns MigrationFailed for a too-new version"
        <| fun _ ->
            let future =
                CompositionDescriptor.createVersioned
                    (CompositionDescriptor.CurrentSchemaVersion + 1)
                    [ selection () ]
                    ServerConfig.defaults

            match CompositionDescriptorVersion.ofManifest (catalogue ()) future with
            | Error(MigrationFailed(DescriptorTooNew _)) -> ()
            | other -> failtestf "expected MigrationFailed (DescriptorTooNew), got %A" other
    ]