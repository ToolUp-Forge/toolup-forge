module ToolUp.AI.Client.Tests.ShellSlotSnapshotTests

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── Phase 879 — the prepared module list, pinned across the slot refactor ──
//
// Phase 879 moves every SDK built-in that a deployment can replace behind a
// declared shell SLOT: placement, gating, the admin tile and every shell
// lookup follow the slot instead of one hand-written arm per built-in. Its
// acceptance is that a deployment which configured nothing sees the same
// sidebar — same modules, same ORDER, same gates — and that a replacement
// keeps the built-in's position and gate.
//
// The only evidence that means anything is a snapshot taken BEFORE the
// refactor, over the untouched tree, and compared AFTER it. That is what
// this file is: the reference configurations below were first written
// against the per-built-in mode fields, the baselines in
// `shell-slot-baselines/` were recorded from that tree, and the refactor
// then rewrote ONLY the configuration section of this file (the rendering
// and the comparison are unchanged). A baseline recorded after the
// refactor would prove nothing.
//
// Two baselines per reference configuration:
//
//   * `<config>.modules.txt` — `prepareModules`' output, one line per
//     module: id, display name, sidebar group, nav role, area, and the
//     visibility predicate evaluated over the four subject kinds. This is
//     the order the sidebar renders groups in (first occurrence) and every
//     gate the shell applies to a module, so it must be IDENTICAL before
//     and after.
//   * `<config>.tiles.txt` — the administration tiles the built-ins
//     contribute (`Client.boot` populates `AdminTileRegistry`). Here the
//     refactor makes ONE deliberate change, reviewed as a diff: a
//     replacement in a tile-bearing slot now carries the slot's tile,
//     pointed at the replacement, where it used to lose it. Every
//     configuration without a replacement must be identical.
//
// ── Refreshing a baseline ──
// Never written by an ordinary run. To record a deliberate change:
//
//     $env:TOOLUP_SHELL_SLOT_REFRESH = "1"; node --import ./register-loader.mjs --test output/Program.js
//
// then read `git diff shell-slot-baselines/` and commit the baselines with
// the change that caused them.

// ─── Baseline files ──────────────────────────────────────────────────

[<Import("readFileSync", from = "node:fs")>]
let private readFileSync (path: obj) (encoding: string) : string = jsNative

[<Import("writeFileSync", from = "node:fs")>]
let private writeFileSync (path: obj) (data: string) (encoding: string) : unit = jsNative

[<Import("existsSync", from = "node:fs")>]
let private existsSync (path: obj) : bool = jsNative

[<Import("mkdirSync", from = "node:fs")>]
let private mkdirSync (path: obj) (options: obj) : unit = jsNative

/// Resolved against this module (`output/…`), not the process cwd — see the
/// same helper in `SidebarRailShapeSnapshotTests.fs`.
[<Emit("new URL($0, import.meta.url)")>]
let private beside (relative: string) : obj = jsNative

/// An absent variable normalised to `""` in JS (the `=== null` trap
/// documented in `SidebarRailShapeSnapshotTests.fs`).
[<Emit("(globalThis.process.env[$0] ?? \"\")")>]
let private envVar (name: string) : string = jsNative

let private refreshRequested () =
    match envVar "TOOLUP_SHELL_SLOT_REFRESH" with
    | ""
    | "0" -> false
    | _ -> true

[<Literal>]
let private BaselineDir = "../shell-slot-baselines/"

let private baselinePath (file: string) = beside (BaselineDir + file)

let private readBaseline (file: string) =
    let path = baselinePath file

    if existsSync path then
        Some((readFileSync path "utf8").Replace("\r", ""))
    else
        None

let private writeBaseline (file: string) (content: string) =
    mkdirSync (beside BaselineDir) (createObj [ "recursive" ==> true ])
    writeFileSync (baselinePath file) content "utf8"

// ─── Rendering ───────────────────────────────────────────────────────

let private visibilityCode (m: ErasedModule) =
    [ AnonymousKind, "A"; UserKind, "U"; TeamMemberKind, "T"; ClaimBearerKind, "C" ]
    |> List.map (fun (kind, code) -> if m.Visibility kind then code else "-")
    |> String.concat ""

let private renderModule (m: ErasedModule) =
    let navRole =
        match m.NavRole with
        | Some role -> string role
        | None -> "-"

    let group = m.Group |> Option.defaultValue "-"

    let area =
        match m.Area with
        | Product -> "product"
        | Administration -> "administration"

    String.concat " | " [ m.Definition.Id; m.Definition.Name; group; navRole; area; visibilityCode m ]

let private renderModules (modules: ErasedModule list) =
    (modules |> List.map renderModule |> String.concat "\n") + "\n"

let private renderTiles (tiles: AdminTile list) =
    let lines =
        tiles
        |> List.map (fun t ->
            String.concat " | " [ t.Widget.Id; t.OwnerModuleId; t.Widget.Title; string t.Widget.Weight ])

    (if List.isEmpty lines then
         "(no tiles)"
     else
         String.concat "\n" lines)
    + "\n"

// ─── The app's own modules ───────────────────────────────────────────

let private module' (id: string) (name: string) (group: string) : ErasedModule =
    ClientModule.create {
        Init = fun () -> 0, Cmd.none
        Update = fun (_: int) (m: int) -> m, Cmd.none
        Name = name
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId id
    |> ClientModule.withGroup group
    |> ClientModule.register

let private apps = [ module' "app.alpha" "Alpha" "Work"; module' "app.beta" "Beta" "Work" ]

/// A deployment's own module standing in for a built-in.
let private replacement (slot: string) =
    module' ("custom." + slot) ("Custom " + slot) "Replacements"

// ─── The reference configurations ────────────────────────────────────
//
// This section — and only this section — is rewritten by the refactor. Each
// configuration keeps its NAME (the baseline file) and its MEANING.

let private team = {
    ClientConfig.defaults with
        Surfaces = Surfaces.team
}

let private anonymous = {
    ClientConfig.defaults with
        Surfaces = Surfaces.anonymous
}

let private label (name: string) : ModuleLabel = { Name = name; Icon = Html.none }

let private everyDefault (config: ClientConfig) = {
    config with
        Slots = {
            HomeModule = SlotFill.Default
            DataManager = SlotFill.Default
            TeamManager = SlotFill.Default
            TeamConfig = SlotFill.Default
            WebhookAdmin = SlotFill.Default
            ServiceAccountAdmin = SlotFill.Default
            ExternalContactManager = SlotFill.Default
            NotificationPreferences = SlotFill.Default
            ModuleVisibilityAdmin = SlotFill.Default
            SessionSecurity = SlotFill.Default
            PlatformAdmin = SlotFill.Default
            PermissionsAdmin = SlotFill.Default
            HealthMonitor = SlotFill.Default
            ServiceStatusBoard = SlotFill.Default
            UsageDashboard = SlotFill.Default
            AuditViewer = SlotFill.Default
            CompositionInspector = SlotFill.Default
            DataIngestionAdmin = SlotFill.Default
            MigrationAdmin = SlotFill.Default
            DataSubjectRequestAdmin = SlotFill.Default
        }
}

let private everyConfigured (config: ClientConfig) = {
    config with
        Slots = {
            HomeModule = SlotFill.Configured(label "My home")
            DataManager =
                SlotFill.Configured(
                    DataManagerChoice.FileUpload {
                        Name = "My uploads"
                        Icon = Html.none
                        Group = Some "My data"
                    }
                )
            TeamManager = SlotFill.Configured(label "My teams")
            TeamConfig = SlotFill.Configured(label "My team config")
            WebhookAdmin = SlotFill.Configured(label "My webhooks")
            ServiceAccountAdmin = SlotFill.Configured(label "My service accounts")
            ExternalContactManager = SlotFill.Configured(label "My contacts")
            NotificationPreferences = SlotFill.Configured(label "My notifications")
            ModuleVisibilityAdmin = SlotFill.Configured(label "My visibility")
            SessionSecurity = SlotFill.Configured(label "My sessions")
            PlatformAdmin = SlotFill.Configured(label "My platform")
            PermissionsAdmin = SlotFill.Configured(label "My permissions")
            HealthMonitor = SlotFill.Configured(label "My health")
            ServiceStatusBoard = SlotFill.Configured(label "My status")
            UsageDashboard = SlotFill.Configured(label "My usage")
            AuditViewer = SlotFill.Configured(label "My audit")
            CompositionInspector = SlotFill.Configured(label "My composition")
            DataIngestionAdmin = SlotFill.Configured(label "My ingestion")
            MigrationAdmin = SlotFill.Configured(label "My migrations")
            DataSubjectRequestAdmin = SlotFill.Configured(label "My DSRs")
        }
}

let private everyExternal (config: ClientConfig) = {
    config with
        Slots = {
            HomeModule = SlotFill.External(replacement "home")
            DataManager = SlotFill.External(replacement "dataManager")
            TeamManager = SlotFill.External(replacement "teamManager")
            TeamConfig = SlotFill.External(replacement "teamConfig")
            WebhookAdmin = SlotFill.External(replacement "webhookAdmin")
            ServiceAccountAdmin = SlotFill.External(replacement "serviceAccountAdmin")
            ExternalContactManager = SlotFill.External(replacement "externalContactManager")
            NotificationPreferences = SlotFill.External(replacement "notificationPreferences")
            ModuleVisibilityAdmin = SlotFill.External(replacement "moduleVisibilityAdmin")
            SessionSecurity = SlotFill.External(replacement "sessionSecurity")
            PlatformAdmin = SlotFill.External(replacement "platformAdmin")
            PermissionsAdmin = SlotFill.External(replacement "permissionsAdmin")
            HealthMonitor = SlotFill.External(replacement "healthMonitor")
            ServiceStatusBoard = SlotFill.External(replacement "serviceStatusBoard")
            UsageDashboard = SlotFill.External(replacement "usageDashboard")
            AuditViewer = SlotFill.External(replacement "auditViewer")
            CompositionInspector = SlotFill.External(replacement "compositionInspector")
            DataIngestionAdmin = SlotFill.External(replacement "dataIngestionAdmin")
            MigrationAdmin = SlotFill.External(replacement "migrationAdmin")
            DataSubjectRequestAdmin = SlotFill.External(replacement "dataSubjectRequestAdmin")
        }
}

let private everyEmpty (config: ClientConfig) = {
    config with
        Slots = {
            HomeModule = SlotFill.Empty
            DataManager = SlotFill.Empty
            TeamManager = SlotFill.Empty
            TeamConfig = SlotFill.Empty
            WebhookAdmin = SlotFill.Empty
            ServiceAccountAdmin = SlotFill.Empty
            ExternalContactManager = SlotFill.Empty
            NotificationPreferences = SlotFill.Empty
            ModuleVisibilityAdmin = SlotFill.Empty
            SessionSecurity = SlotFill.Empty
            PlatformAdmin = SlotFill.Empty
            PermissionsAdmin = SlotFill.Empty
            HealthMonitor = SlotFill.Empty
            ServiceStatusBoard = SlotFill.Empty
            UsageDashboard = SlotFill.Empty
            AuditViewer = SlotFill.Empty
            CompositionInspector = SlotFill.Empty
            DataIngestionAdmin = SlotFill.Empty
            MigrationAdmin = SlotFill.Empty
            DataSubjectRequestAdmin = SlotFill.Empty
        }
}

let private columnMapping (config: ClientConfig) = {
    config with
        Slots.DataManager = SlotFill.Configured(DataManagerChoice.ColumnMapping None)
}

let private configuredColumnMapping (config: ClientConfig) = {
    config with
        Slots.DataManager =
            SlotFill.Configured(
                DataManagerChoice.ColumnMapping(
                    Some {
                        Name = "My mapping"
                        Icon = Html.none
                        Group = Some "My data"
                    }
                )
            )
}

let private referenceConfigurations: (string * ClientConfig) list = [
    "defaults", ClientConfig.defaults
    "anonymous", anonymous
    "individual",
    {
        ClientConfig.defaults with
            Surfaces = Surfaces.individual
    }
    "team", team
    "multi-team",
    {
        ClientConfig.defaults with
            Surfaces = Surfaces.multiTeam
    }
    "anonymous-and-team",
    {
        ClientConfig.defaults with
            Surfaces = Surfaces.anonymousAndTeam
    }
    "team-every-default", everyDefault team
    "anonymous-every-default", everyDefault anonymous
    "team-every-configured", everyConfigured team
    "anonymous-every-configured", everyConfigured anonymous
    "team-every-external", everyExternal team
    "anonymous-every-external", everyExternal anonymous
    "team-every-empty", everyEmpty team
    "team-column-mapping", columnMapping team
    "team-configured-column-mapping", configuredColumnMapping team
    "team-separate-admin-area",
    {
        everyDefault team with
            AdminSurface = SeparateArea
    }
]

// ─── The gate ────────────────────────────────────────────────────────

let private check (file: string) (actual: string) =
    if refreshRequested () then
        writeBaseline file actual
    else
        match readBaseline file with
        | None ->
            failwithf
                "shell-slot baseline %s has never been recorded. Record it with $env:TOOLUP_SHELL_SLOT_REFRESH = \"1\" and commit it."
                file
        | Some expected -> Expect.equal actual expected ("shell-slot baseline " + file)

let tests =
    testList "Phase 879 — shell slots: the prepared module list and the admin tiles, pinned" [
        for name, config in referenceConfigurations do
            testCase (name + " — prepared modules")
            <| fun () -> check (name + ".modules.txt") (renderModules (Client.prepareModules config apps))

            testCase (name + " — admin tiles")
            <| fun () ->
                let prepared = Client.prepareModules config apps
                Client.boot config prepared
                check (name + ".tiles.txt") (renderTiles (AdminTileRegistry.tiles ()))
    ]