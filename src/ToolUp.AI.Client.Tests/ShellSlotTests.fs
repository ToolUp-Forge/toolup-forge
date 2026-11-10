module ToolUp.AI.Client.Tests.ShellSlotTests

open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── Phase 879 — the slot table's own contract ──────────────────────
//
// `ShellSlotSnapshotTests` pins what the shell composes. This pack pins
// the table's promises directly: every slot the SDK declares accepts a
// deployment's own module, a replacement gets the slot's administration
// tile (unless it supplies its own), and the id the shell reads per
// message without building a module is the id of the module it would
// build.

let private module' (id: string) (name: string) : ErasedModule =
    ClientModule.create {
        Init = fun () -> 0, Cmd.none
        Update = fun (_: int) (m: int) -> m, Cmd.none
        Name = name
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId id
    |> ClientModule.withGroup "Replacements"
    |> ClientModule.register

let private replacement (slotId: string) =
    module' ("custom." + slotId) ("Custom " + slotId)

let private team = {
    ClientConfig.defaults with
        Surfaces = Surfaces.team
}

/// Every slot filled by a deployment's own module whose id is
/// `custom.<slot id>`.
let private everyExternal = {
    team with
        Slots = {
            HomeModule = SlotFill.External(replacement "HomeModule")
            DataManager = SlotFill.External(replacement "DataManager")
            TeamManager = SlotFill.External(replacement "TeamManager")
            TeamConfig = SlotFill.External(replacement "TeamConfig")
            WebhookAdmin = SlotFill.External(replacement "WebhookAdmin")
            ServiceAccountAdmin = SlotFill.External(replacement "ServiceAccountAdmin")
            ExternalContactManager = SlotFill.External(replacement "ExternalContactManager")
            NotificationPreferences = SlotFill.External(replacement "NotificationPreferences")
            ModuleVisibilityAdmin = SlotFill.External(replacement "ModuleVisibilityAdmin")
            SessionSecurity = SlotFill.External(replacement "SessionSecurity")
            PlatformAdmin = SlotFill.External(replacement "PlatformAdmin")
            PermissionsAdmin = SlotFill.External(replacement "PermissionsAdmin")
            HealthMonitor = SlotFill.External(replacement "HealthMonitor")
            ServiceStatusBoard = SlotFill.External(replacement "ServiceStatusBoard")
            UsageDashboard = SlotFill.External(replacement "UsageDashboard")
            AuditViewer = SlotFill.External(replacement "AuditViewer")
            CompositionInspector = SlotFill.External(replacement "CompositionInspector")
            DataIngestionAdmin = SlotFill.External(replacement "DataIngestionAdmin")
            MigrationAdmin = SlotFill.External(replacement "MigrationAdmin")
            DataSubjectRequestAdmin = SlotFill.External(replacement "DataSubjectRequestAdmin")
            JobsAdmin = SlotFill.External(replacement "JobsAdmin")
            Extensions = []
        }
}

let private label (name: string) : ModuleLabel = { Name = name; Icon = Html.none }

let private everyConfigured = {
    team with
        Slots = {
            HomeModule = SlotFill.Configured(label "home")
            DataManager =
                SlotFill.Configured(
                    DataManagerChoice.FileUpload {
                        Label = Some(label "uploads")
                        Group = None
                    }
                )
            TeamManager = SlotFill.Configured(label "teams")
            TeamConfig = SlotFill.Configured(label "config")
            WebhookAdmin = SlotFill.Configured(label "webhooks")
            ServiceAccountAdmin = SlotFill.Configured(label "accounts")
            ExternalContactManager = SlotFill.Configured(label "contacts")
            NotificationPreferences = SlotFill.Configured(label "notifications")
            ModuleVisibilityAdmin = SlotFill.Configured(label "visibility")
            SessionSecurity = SlotFill.Configured(label "sessions")
            PlatformAdmin = SlotFill.Configured(label "platform")
            PermissionsAdmin = SlotFill.Configured(label "permissions")
            HealthMonitor = SlotFill.Configured(label "health")
            ServiceStatusBoard = SlotFill.Configured(label "status")
            UsageDashboard = SlotFill.Configured(label "usage")
            AuditViewer = SlotFill.Configured(label "audit")
            CompositionInspector = SlotFill.Configured(label "composition")
            DataIngestionAdmin = SlotFill.Configured(label "ingestion")
            MigrationAdmin = SlotFill.Configured(label "migrations")
            DataSubjectRequestAdmin = SlotFill.Configured(label "dsr")
            JobsAdmin = SlotFill.Configured(label "jobs")
            Extensions = []
        }
}

let private columnMapping = {
    team with
        Slots.DataManager = SlotFill.Configured(DataManagerChoice.ColumnMapping DataManagerConfig.unlabelled)
}

let private ctxOf (config: ClientConfig) : ShellSlotContext = { Config = config; DataTypes = [] }

let private tileFor (ownerId: string) (tiles: AdminTile list) =
    tiles |> List.tryFind (fun t -> t.OwnerModuleId = ownerId)

let tests =
    testList "Phase 879 — shell slots: the table's contract" [

        testCase "the table declares one slot per ShellSlotFills field, each id once"
        <| fun () ->
            let ids = ShellSlots.all |> List.map _.Id

            Expect.equal
                ids
                [
                    "HomeModule"
                    "DataManager"
                    "TeamManager"
                    "TeamConfig"
                    "WebhookAdmin"
                    "ServiceAccountAdmin"
                    "ExternalContactManager"
                    "NotificationPreferences"
                    "ModuleVisibilityAdmin"
                    "SessionSecurity"
                    "PermissionsAdmin"
                    "UsageDashboard"
                    "AuditViewer"
                    "DataIngestionAdmin"
                    "JobsAdmin"
                    "PlatformAdmin"
                    "HealthMonitor"
                    "ServiceStatusBoard"
                    "DataSubjectRequestAdmin"
                    "MigrationAdmin"
                    "CompositionInspector"
                ]
                "the twenty-one slots, in composition order"

        testCase "every one of the twenty-one slots accepts a deployment's own module"
        <| fun () ->
            for slot in ShellSlots.all do
                let filled = slot.Fill(ctxOf everyExternal) |> Option.map _.Definition.Id
                let expected = Some("custom." + slot.Id)
                Expect.equal filled expected (slot.Id + " is filled by the replacement")
                Expect.equal (slot.FilledModuleId everyExternal) expected (slot.Id + " reports the replacement's id")

        testCase "the id read without building a module is the id of the module the slot builds"
        <| fun () ->
            for config in [ team; everyConfigured; everyExternal; columnMapping ] do
                for slot in ShellSlots.all do
                    let built = slot.Fill(ctxOf config) |> Option.map _.Definition.Id
                    Expect.equal (slot.FilledModuleId config) built (slot.Id + " — FilledModuleId agrees with Fill")

        testCase
            "the data slot names the mapping-aware manager when it is chosen, and the replacement when there is one"
        <| fun () ->
            Expect.equal
                (ShellSlots.composedModuleId ShellSlots.dataManager team)
                (Some "_sdk.DataManager")
                "default: the file-upload manager"

            Expect.equal
                (ShellSlots.composedModuleId ShellSlots.dataManager columnMapping)
                (Some "_sdk.MappingDataManager")
                "ColumnMapping: the mapping-aware manager"

            Expect.equal
                (ShellSlots.composedModuleId ShellSlots.dataManager everyExternal)
                (Some "custom.DataManager")
                "External: the replacement"

            Expect.equal
                (ShellSlots.composedModuleId ShellSlots.dataManager {
                    team with
                        Slots.DataManager = SlotFill.Empty
                })
                None
                "Empty: no data source"

        testCase "a replacement in the team-manager slot takes the built-in's position, gate and tile"
        <| fun () ->
            let mine = module' "my.teams" "My teams"

            let withMine (config: ClientConfig) = {
                config with
                    Slots.TeamManager = SlotFill.External mine
            }

            let apps = [ module' "app.alpha" "Alpha" ]
            let builtInIds = Client.prepareModules team apps |> List.map _.Definition.Id

            let replacedIds =
                Client.prepareModules (withMine team) apps |> List.map _.Definition.Id

            // Position: exactly where the built-in stood.
            Expect.equal
                replacedIds
                (builtInIds
                 |> List.map (fun id -> if id = TeamManagerUI.moduleId then "my.teams" else id))
                "the replacement sits in the built-in's place"

            // Gate: a deployment with no Team surface admits neither.
            let anonymous = {
                ClientConfig.defaults with
                    Surfaces = Surfaces.anonymous
            }

            Expect.isFalse
                (Client.prepareModules (withMine anonymous) apps
                 |> List.exists (fun m -> m.Definition.Id = "my.teams"))
                "the slot's gate applies to the replacement"

            // Tile: the slot's tile, pointed at the replacement.
            let tile = ShellSlots.adminTiles (withMine team) [] |> tileFor "my.teams"
            Expect.isTrue tile.IsSome "the replacement carries the slot's tile"
            Expect.equal tile.Value.Widget.Id "_sdk.tile.teams" "it is the team slot's tile"
            Expect.equal tile.Value.Widget.Title "My teams" "titled with the replacement's name"

            Expect.isTrue
                (ShellSlots.adminTiles (withMine team) []
                 |> tileFor TeamManagerUI.moduleId
                 |> Option.isNone)
                "and no tile points at the built-in the deployment replaced"

        testCase "a replacement that supplies its own tile keeps it, and the slot's steps aside"
        <| fun () ->
            let mine = module' "my.health" "My health"

            let config = {
                team with
                    Slots.HealthMonitor = SlotFill.External mine
            }

            let own: AdminTile = {
                OwnerModuleId = "my.health"
                Widget = {
                    Id = "my.tile.health"
                    Title = "Mine"
                    Icon = Html.none
                    Weight = 5
                    Body = fun _ -> Html.none
                }
            }

            let slotTiles = ShellSlots.adminTiles config [ own ]
            Expect.isTrue (tileFor "my.health" slotTiles |> Option.isNone) "the slot contributes no second tile"

            Expect.isTrue
                (ShellSlots.adminTiles config [] |> tileFor "my.health" |> Option.isSome)
                "without its own tile, the replacement gets the slot's"

        testCase "an empty slot contributes neither a module nor a tile"
        <| fun () ->
            let config = {
                team with
                    Slots.UsageDashboard = SlotFill.Empty
            }

            Expect.isFalse
                (Client.prepareModules config []
                 |> List.exists (fun m -> m.Definition.Id = UsageDashboard.moduleId))
                "no module"

            Expect.isTrue
                (ShellSlots.adminTiles config []
                 |> tileFor UsageDashboard.moduleId
                 |> Option.isNone)
                "no tile"

        // ── Phase 1074 — the AI family on the slot table, the chrome rows ──

        testCase "an AI deployment with the assistant slot Empty composes no assistant module"
        <| fun () ->
            let fills = {
                ToolUp.AI.AISlotFills.defaults with
                    AIAssistant = SlotFill.Empty
            }

            let ids =
                Client.prepareModules (ToolUp.AI.Client.AIClientConfig.withAISlots fills team) []
                |> List.map _.Definition.Id

            let defaultIds =
                Client.prepareModules (ToolUp.AI.Client.AIClientConfig.withAISlots ToolUp.AI.AISlotFills.defaults team) []
                |> List.map _.Definition.Id

            let assistantId =
                defaultIds
                |> List.except (Client.prepareModules team [] |> List.map _.Definition.Id)
                |> List.head

            Expect.isFalse (List.contains assistantId ids) "the assistant slot composes nothing"
            Expect.equal (List.length defaultIds - List.length ids) 1 "and only the assistant is gone"

        testCase "an AI deployment with the assistant slot External composes the replacement in its place"
        <| fun () ->
            let mine = module' "my.assistant" "My assistant"
            let apps = [ module' "app.alpha" "Alpha" ]

            let fills = {
                ToolUp.AI.AISlotFills.defaults with
                    AIAssistant = SlotFill.External mine
            }

            let builtIn =
                Client.prepareModules
                    (ToolUp.AI.Client.AIClientConfig.withAISlots ToolUp.AI.AISlotFills.defaults team)
                    apps
                |> List.map _.Definition.Id

            let replaced =
                Client.prepareModules (ToolUp.AI.Client.AIClientConfig.withAISlots fills team) apps
                |> List.map _.Definition.Id

            let alpha = List.findIndex ((=) "app.alpha") replaced
            Expect.equal replaced[alpha + 1] "my.assistant" "the replacement sits right after the app's modules"
            Expect.equal (List.length replaced) (List.length builtIn) "one module for one"

        testCase "every AI slot can be emptied, and an empty family composes the plain shell"
        <| fun () ->
            Expect.equal
                (Client.prepareModules (ToolUp.AI.Client.AIClientConfig.withAISlots ToolUp.AI.AISlotFills.empty team) []
                 |> List.map _.Definition.Id)
                (Client.prepareModules team [] |> List.map _.Definition.Id)
                "no AI page at all"

        testCase "a chrome row set Off is absent from prepareModules on every surface"
        <| fun () ->
            let allOn (config: ClientConfig) = {
                config with
                    Chrome = {
                        AdminArea = ChromeFill.On()
                        NoActiveTeamLanding =
                            ChromeFill.On(
                                NoActiveTeamLandingFill.BuiltIn {
                                    Label = "Welcome"
                                    Title = "t"
                                    Body = "b"
                                    Icon = None
                                }
                            )
                        DatadogReadback = ChromeFill.On DatadogReadbackConfig.defaults
                        Observability = ChromeFill.On()
                        PlatformUsers = ChromeFill.On()
                        TenantLifecycle = ChromeFill.On()
                    }
            }

            let allOff (config: ClientConfig) = {
                config with
                    Chrome = {
                        AdminArea = ChromeFill.Off
                        NoActiveTeamLanding = ChromeFill.Off
                        DatadogReadback = ChromeFill.Off
                        Observability = ChromeFill.Off
                        PlatformUsers = ChromeFill.Off
                        TenantLifecycle = ChromeFill.Off
                    }
            }

            for surfaces in [ Surfaces.anonymous; Surfaces.individual; Surfaces.team; Surfaces.multiTeam ] do
                let base' = {
                    ClientConfig.defaults with
                        Surfaces = surfaces
                }

                let on = Client.prepareModules (allOn base') [] |> List.map _.Definition.Id
                let off = Client.prepareModules (allOff base') [] |> List.map _.Definition.Id
                let chromeIds = on |> List.except off
                Expect.isFalse (List.isEmpty chromeIds) "the chrome rows compose modules when on"

                Expect.isEmpty
                    (off |> List.filter (fun id -> List.contains id chromeIds))
                    "every chrome row set Off composes nothing"
    ]