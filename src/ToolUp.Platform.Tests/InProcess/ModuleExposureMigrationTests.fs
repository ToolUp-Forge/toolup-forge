module ToolUp.Platform.Tests.InProcess.ModuleExposureMigrationTests

open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.Tests.Contracts

// ─── Phase 245 — tri-state ModuleExposure persistence migration ──────
//
// The exposure axis became a tri-state (`Available | Hidden |
// Unavailable`) persisted as an `exposure` object. These pins protect
// the two persistence guarantees: a legacy `hidden: string[]` document
// (written before the tri-state) reads back as `Hidden` (cosmetic — off
// the sidebar, data still mappable), and a freshly-written document
// dual-writes the legacy `hidden` array so an un-upgraded reader still
// keeps the modules off the sidebar.

let private platformContainer = "_platform"
let private blobName teamId = $"permissions/{teamId}.json"

[<Tests>]
let tests =
    testList "Phase 245 — ModuleExposure persistence migration" [

        testCaseAsync "legacy `hidden` array migrates to the Hidden state"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let team = "team-legacy"

            // A document written before the tri-state: only `hidden`.
            let legacy = """{"defaults":{},"members":{},"hidden":["Marketing","Finance"]}"""
            let! _ = blob.Upload(platformContainer, blobName team, Encoding.UTF8.GetBytes legacy)

            let store = PermissionStore(blob) :> IPermissionStore
            let! exposure = store.GetModuleExposure team

            Expect.equal
                exposure
                (Map.ofList [ "Marketing", ModuleExposure.Hidden; "Finance", ModuleExposure.Hidden ])
                "legacy hidden entries migrate to Hidden (cosmetic — still mappable)"
        }

        testCaseAsync "new document round-trips both states + dual-writes legacy `hidden`"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let team = "team-new"
            let store = PermissionStore(blob) :> IPermissionStore

            let! _ = store.SetModuleExposure(team, "Marketing", ModuleExposure.Hidden)
            let! _ = store.SetModuleExposure(team, "Finance", ModuleExposure.Unavailable)

            let! exposure = store.GetModuleExposure team

            Expect.equal
                exposure
                (Map.ofList [ "Marketing", ModuleExposure.Hidden; "Finance", ModuleExposure.Unavailable ])
                "both states round-trip"

            // Dual-write back-compat: the legacy `hidden` array carries
            // every non-Available module (both Hidden and Unavailable).
            let! raw = blob.Download(platformContainer, blobName team)

            match raw with
            | Ok bytes ->
                let json = Encoding.UTF8.GetString bytes
                Expect.stringContains json "\"hidden\"" "dual-writes the legacy hidden array"
                Expect.stringContains json "Marketing" "legacy array carries the Hidden module"
                Expect.stringContains json "Finance" "legacy array carries the Unavailable module"
            | Error e -> failtest $"download failed: {e}"
        }

        testCaseAsync "Available clears the entry — empty document has no exposure overrides"
        <| async {
            let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let team = "team-clear"
            let store = PermissionStore(blob) :> IPermissionStore

            let! _ = store.SetModuleExposure(team, "Marketing", ModuleExposure.Unavailable)
            let! _ = store.SetModuleExposure(team, "Marketing", ModuleExposure.Available)
            let! exposure = store.GetModuleExposure team

            Expect.isEmpty exposure "setting Available removes the entry (back to default)"
        }
    ]