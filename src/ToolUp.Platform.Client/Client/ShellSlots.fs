// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 879 — the shell's declared slots ───────────────────────────
//
// The shell used to know every built-in it could inject by name: one
// hand-written arm per built-in in `prepareModules` placing it and gating
// it, a second match per tile-bearing built-in deciding its
// administration tile, and a third deciding which module id is the data
// manager. A deployment could already swap a built-in for its own module
// (the `External…` mode cases), but the swap was partial — the
// replacement lost the built-in's administration tile, and every lookup
// outside module preparation still asked which built-in it was talking
// to.
//
// Now the shell declares SLOTS. A slot has a stable id, a position in the
// composed module list, a gate over the deployment's configuration, and
// optionally an administration tile. What fills it — nothing, the SDK's
// module, a configured variant of it, or a deployment's own module — is
// `ClientConfig.Slots` (`ShellSlotFills`). Placement, gating, the tile
// and the data-manager lookup follow the slot, so a replacement is a full
// replacement, and `prepareModules` walks this table without naming any
// built-in.
//
// The table also carries the few built-ins that are shell chrome rather
// than replaceable modules (the administration landing, the Datadog and
// observability panels, tenant lifecycle, platform users, the
// no-active-team landing). They are NOT slots — none accepts a
// deployment's own module, and nothing here pretends otherwise — but
// they interleave with the slots in the sidebar's order, and one table
// is the only way to state that order once.
//
// ORDER IS LOAD-BEARING. The sidebar renders groups in first-occurrence
// order across the composed list, so the order of the rows below is the
// order the admin groups appear in. It is pinned, over sixteen reference
// configurations, by the Phase 879 snapshot baselines
// (`src/ToolUp.AI.Client.Tests/shell-slot-baselines/`), which were
// recorded before this table existed.

/// Where a slot's module sits in the composed module list.
[<RequireQualifiedAccess>]
type ShellSlotPosition =
    /// At the very head. The shell lands on the first module when
    /// `ClientConfig.ActiveModule` names none, so a module here is the
    /// default start surface.
    | Home
    /// Ahead of the app's own modules.
    | Leading
    /// After the app's own modules (and before any `DebugOnly` ones) —
    /// the administration built-ins.
    | Trailing

/// Which deployments a slot is admitted in, decided from the surfaces
/// the deployment declares (`ClientConfig.Surfaces`). A slot that is not
/// admitted contributes no module however it is filled.
[<RequireQualifiedAccess>]
type ShellSlotGate =
    /// Every deployment. The session-time gate (a nav role, a visibility
    /// predicate) is then the filling module's own.
    | Always
    /// Deployments declaring any `Team` surface, single-team or
    /// multi-team (`ClientConfig.hasTeamScope`).
    | AnyTeamSurface
    /// Deployments declaring any surface other than Anonymous
    /// (`ClientConfig.requiresAnyAuth`) — an Anonymous-only deployment has
    /// no persistent scope and no role concept, so the module's API would
    /// refuse every call.
    | AnyAuthenticatedSurface

/// Evaluating a `ShellSlotGate`.
module ShellSlotGate =
    /// Whether the gate admits the slot under this configuration.
    let admits (gate: ShellSlotGate) (config: ClientConfig) : bool =
        match gate with
        | ShellSlotGate.Always -> true
        | ShellSlotGate.AnyTeamSurface -> ClientConfig.hasTeamScope config
        | ShellSlotGate.AnyAuthenticatedSurface -> ClientConfig.requiresAnyAuth config

/// What a slot's SDK module is built from.
type ShellSlotContext = {
    /// The deployment's client configuration.
    Config: ClientConfig
    /// Every data type the app's own modules declare — the data
    /// manager's input. Empty where no module is being composed (building
    /// an administration tile).
    DataTypes: DataTypeDisplay list
}

/// One declared shell slot. The shell places, gates and tiles the SLOT;
/// whatever fills it (`ClientConfig.Slots`) inherits all three.
type ShellSlot = {
    /// Stable id — the `ShellSlotFills` field the slot is filled through
    /// (`"TeamManager"`, `"DataManager"`, …).
    Id: string
    /// Where the filling module sits in the composed list.
    Position: ShellSlotPosition
    /// Which deployments admit the slot.
    Gate: ShellSlotGate
    /// The module filling the slot under `ctx.Config` — the SDK's module
    /// for `Default` / `Configured`, the deployment's for `External`,
    /// `None` for `Empty`. The gate is NOT applied here.
    Fill: ShellSlotContext -> ErasedModule option
    /// The id of the module that fills the slot, read WITHOUT building it
    /// (the shell asks per message which module is the data source).
    /// `None` when the slot is `Empty`. The gate is not applied here.
    FilledModuleId: ClientConfig -> string option
    /// The slot's administration tile, built for whatever module fills
    /// the slot — so a replacement's tile carries the replacement's id,
    /// name and icon. `None` when the slot carries no tile.
    AdminTile: (ModuleDefinition -> AdminTile) option
}

/// The shell's slot table.
module ShellSlots =

    // ── Building a slot ───────────────────────────────────────────────

    let private slot<'cfg>
        (id: string)
        (position: ShellSlotPosition)
        (gate: ShellSlotGate)
        (fillOf: ShellSlotFills -> SlotFill<'cfg>)
        (builtInId: 'cfg option -> string)
        (builtIn: ShellSlotContext -> 'cfg option -> ErasedModule)
        (tile: (ModuleDefinition -> AdminTile) option)
        : ShellSlot =
        // `Default` is the SDK's module with no configuration, `Configured`
        // the same module configured; `External` is the deployment's.
        let resolve (config: ClientConfig) : Choice<'cfg option, ErasedModule> option =
            match fillOf config.Slots with
            | SlotFill.Empty -> None
            | SlotFill.Default -> Some(Choice1Of2 None)
            | SlotFill.Configured cfg -> Some(Choice1Of2(Some cfg))
            | SlotFill.External replacement -> Some(Choice2Of2 replacement)

        {
            Id = id
            Position = position
            Gate = gate
            Fill =
                fun ctx ->
                    resolve ctx.Config
                    |> Option.map (function
                        | Choice1Of2 cfg -> builtIn ctx cfg
                        | Choice2Of2 replacement -> replacement)
            FilledModuleId =
                fun config ->
                    resolve config
                    |> Option.map (function
                        | Choice1Of2 cfg -> builtInId cfg
                        | Choice2Of2 replacement -> replacement.Definition.Id)
            AdminTile = tile
        }

    /// A slot whose SDK module is relabelled by a `ModuleLabel`.
    let private labelled
        (id: string)
        (position: ShellSlotPosition)
        (gate: ShellSlotGate)
        (fillOf: ShellSlotFills -> SlotFill<ModuleLabel>)
        (moduleId: string)
        (create: ShellSlotContext -> ModuleLabel option -> ErasedModule)
        (tile: (ModuleDefinition -> AdminTile) option)
        : ShellSlot =
        slot id position gate fillOf (fun _ -> moduleId) create tile

    // ── The slots ─────────────────────────────────────────────────────

    let private homeModule =
        labelled
            "HomeModule"
            ShellSlotPosition.Home
            ShellSlotGate.Always
            _.HomeModule
            Home.moduleId
            (fun ctx label -> Home.create ctx.Config.HomeRecents label)
            None

    /// The data-manager slot — the one the shell reads for the boot-time
    /// file snapshot and for which module's live state supersedes it.
    let dataManager: ShellSlot =
        slot
            "DataManager"
            ShellSlotPosition.Leading
            ShellSlotGate.Always
            _.DataManager
            (function
            | Some(DataManagerChoice.ColumnMapping _) -> MappingDataManagerUI.moduleId
            | Some(DataManagerChoice.FileUpload _)
            | None -> FileManagerUI.moduleId)
            (fun ctx choice ->
                match choice with
                | None -> FileManagerUI.create ctx.DataTypes None
                | Some(DataManagerChoice.FileUpload label) -> FileManagerUI.create ctx.DataTypes (Some label)
                | Some(DataManagerChoice.ColumnMapping label) -> MappingDataManagerUI.create ctx.DataTypes label)
            None

    let private teamManager =
        labelled
            "TeamManager"
            ShellSlotPosition.Trailing
            ShellSlotGate.AnyTeamSurface
            _.TeamManager
            TeamManagerUI.moduleId
            (fun _ label -> TeamManagerUI.create label)
            (Some TeamManagerUI.adminTile)

    let private authenticatedAdmin fillOf moduleId create tile id =
        labelled
            id
            ShellSlotPosition.Trailing
            ShellSlotGate.AnyAuthenticatedSurface
            fillOf
            moduleId
            (fun _ label -> create label)
            tile

    let private everywhereAdmin fillOf moduleId create tile id =
        labelled id ShellSlotPosition.Trailing ShellSlotGate.Always fillOf moduleId create tile

    let private teamConfig =
        authenticatedAdmin _.TeamConfig TeamConfigUI.moduleId TeamConfigUI.create None "TeamConfig"

    let private webhookAdmin =
        authenticatedAdmin _.WebhookAdmin WebhookAdminUI.moduleId WebhookAdminUI.create None "WebhookAdmin"

    let private serviceAccountAdmin =
        authenticatedAdmin
            _.ServiceAccountAdmin
            ServiceAccountUI.moduleId
            ServiceAccountUI.create
            None
            "ServiceAccountAdmin"

    let private externalContactManager =
        authenticatedAdmin
            _.ExternalContactManager
            ExternalContactManagerUI.moduleId
            ExternalContactManagerUI.create
            None
            "ExternalContactManager"

    let private notificationPreferences =
        authenticatedAdmin
            _.NotificationPreferences
            NotificationPreferencesUI.moduleId
            NotificationPreferencesUI.create
            None
            "NotificationPreferences"

    let private moduleVisibilityAdmin =
        authenticatedAdmin
            _.ModuleVisibilityAdmin
            ModuleVisibilityAdminUI.moduleId
            ModuleVisibilityAdminUI.create
            None
            "ModuleVisibilityAdmin"

    let private sessionSecurity =
        authenticatedAdmin _.SessionSecurity SessionSecurityUI.moduleId SessionSecurityUI.create None "SessionSecurity"

    let private permissionsAdmin =
        authenticatedAdmin
            _.PermissionsAdmin
            PermissionsAdminUI.moduleId
            PermissionsAdminUI.create
            None
            "PermissionsAdmin"

    let private usageDashboard =
        authenticatedAdmin
            _.UsageDashboard
            UsageDashboard.moduleId
            UsageDashboard.create
            (Some UsageDashboard.adminTile)
            "UsageDashboard"

    let private auditViewer =
        authenticatedAdmin _.AuditViewer AuditLogUI.moduleId AuditLogUI.create None "AuditViewer"

    let private dataIngestionAdmin =
        authenticatedAdmin
            _.DataIngestionAdmin
            DataIngestionUI.moduleId
            DataIngestionUI.create
            None
            "DataIngestionAdmin"

    let private platformAdmin =
        everywhereAdmin
            _.PlatformAdmin
            PlatformAdminUI.moduleId
            (fun ctx label -> PlatformAdminUI.create label ctx.Config)
            None
            "PlatformAdmin"

    let private healthMonitor =
        everywhereAdmin
            _.HealthMonitor
            HealthMonitorUI.moduleId
            (fun _ label -> HealthMonitorUI.create label)
            (Some HealthMonitorUI.adminTile)
            "HealthMonitor"

    let private serviceStatusBoard =
        everywhereAdmin
            _.ServiceStatusBoard
            ServiceStatusBoardUI.moduleId
            (fun _ label -> ServiceStatusBoardUI.create label)
            (Some ServiceStatusBoardUI.adminTile)
            "ServiceStatusBoard"

    let private dataSubjectRequestAdmin =
        authenticatedAdmin
            _.DataSubjectRequestAdmin
            DataSubjectRequestAdminUI.moduleId
            DataSubjectRequestAdminUI.create
            None
            "DataSubjectRequestAdmin"

    let private migrationAdmin =
        authenticatedAdmin _.MigrationAdmin MigrationStatusUI.moduleId MigrationStatusUI.create None "MigrationAdmin"

    let private compositionInspector =
        authenticatedAdmin
            _.CompositionInspector
            CompositionInspectorUI.moduleId
            CompositionInspectorUI.create
            None
            "CompositionInspector"

    // ── The composition table ─────────────────────────────────────────

    /// One row of the composition table: a slot, or a built-in that is
    /// shell chrome (registered by its own `ClientConfig` setting, never
    /// by a deployment's module).
    [<RequireQualifiedAccess>]
    type private Row =
        | Slot of ShellSlot
        | Chrome of ShellSlotPosition * (ShellSlotContext -> ErasedModule list)

    /// The parameterised no-active-team landing: only on a `Team`
    /// surface, only when the deployment set its copy
    /// (`ClientConfig.NoActiveTeamLanding`), and only when it did NOT
    /// name a module of its own (`NoActiveTeamLandingModuleId` wins).
    let private noActiveTeamLanding (ctx: ShellSlotContext) =
        match
            ClientConfig.hasTeamScope ctx.Config, ctx.Config.NoActiveTeamLandingModuleId, ctx.Config.NoActiveTeamLanding
        with
        | true, None, Some landing -> [ NoActiveTeamLandingUI.create landing ]
        | _ -> []

    /// Phase 573 — the administration landing leads the admin partition,
    /// and exists only under `AdminSurface = SeparateArea` (under the
    /// default `InlineGroups` there is no area to land on).
    let private adminHome (ctx: ShellSlotContext) =
        match ctx.Config.AdminSurface with
        | InlineGroups -> []
        | SeparateArea -> [ AdminHome.create () ]

    let private datadogReadback (ctx: ShellSlotContext) =
        match ctx.Config.DatadogReadback with
        | NoDatadogReadback -> []
        | EnabledDatadogReadback readback -> [ DatadogReadbackUI.create readback ]

    let private observability (ctx: ShellSlotContext) =
        match ctx.Config.Observability with
        | NoObservabilityModule -> []
        | DefaultObservabilityModule -> [ ObservabilityUI.create () ]

    /// Phase 54e — registered unconditionally; the "Platform Management"
    /// group's role gate hides it from non-admins, and on
    /// `NoTenantLifecycle` its API 404s and the panel shows its empty
    /// state (GP 13).
    let private tenantLifecycleAdmin (_: ShellSlotContext) = [ TenantLifecycleAdminUI.create () ]

    let private platformUsers (ctx: ShellSlotContext) =
        match ctx.Config.PlatformUsers with
        | NoPlatformUsers -> []
        | DefaultPlatformUsers -> [ PlatformUsersUI.create None ]

    /// The composed order. Within `Trailing`, every "Team Management"
    /// module is listed before every "Platform Management" one, so the
    /// Team Management group renders above Platform Management; the
    /// composition inspector (a Platform Management module) sits LAST
    /// rather than beside the audit viewer, because a Platform
    /// Management module listed in the Team Management run would pull
    /// that whole group above Team Management for every deployment.
    let private rows: Row list = [
        Row.Slot homeModule
        Row.Chrome(ShellSlotPosition.Leading, noActiveTeamLanding)
        Row.Slot dataManager
        Row.Chrome(ShellSlotPosition.Trailing, adminHome)
        // Team Management
        Row.Slot teamManager
        Row.Slot teamConfig
        Row.Slot webhookAdmin
        Row.Slot serviceAccountAdmin
        Row.Slot externalContactManager
        Row.Slot notificationPreferences
        Row.Slot moduleVisibilityAdmin
        Row.Slot sessionSecurity
        Row.Slot permissionsAdmin
        Row.Slot usageDashboard
        Row.Slot auditViewer
        Row.Slot dataIngestionAdmin
        // Platform Management
        Row.Slot platformAdmin
        Row.Slot healthMonitor
        Row.Chrome(ShellSlotPosition.Trailing, datadogReadback)
        Row.Chrome(ShellSlotPosition.Trailing, observability)
        Row.Slot serviceStatusBoard
        Row.Slot dataSubjectRequestAdmin
        Row.Slot migrationAdmin
        Row.Chrome(ShellSlotPosition.Trailing, tenantLifecycleAdmin)
        Row.Chrome(ShellSlotPosition.Trailing, platformUsers)
        Row.Slot compositionInspector
    ]

    /// Every slot the shell declares, in composition order — one per
    /// `ShellSlotFills` field.
    let all: ShellSlot list =
        rows
        |> List.choose (function
            | Row.Slot s -> Some s
            | Row.Chrome _ -> None)

    /// The modules admitted at `position`, in table order: each admitted,
    /// filled slot's module and each chrome built-in's. `prepareModules`
    /// composes the sidebar from this — Home, then Leading, then the
    /// app's own modules, then Trailing.
    let modulesAt (position: ShellSlotPosition) (ctx: ShellSlotContext) : ErasedModule list =
        rows
        |> List.collect (function
            | Row.Slot s when s.Position = position ->
                if ShellSlotGate.admits s.Gate ctx.Config then
                    s.Fill ctx |> Option.toList
                else
                    []
            | Row.Chrome(p, build) when p = position -> build ctx
            | _ -> [])

    /// The id of the module the shell composes into `slot` under
    /// `config` — `None` when the slot is empty or its gate does not
    /// admit it. Read without building the module.
    let composedModuleId (slot: ShellSlot) (config: ClientConfig) : string option =
        if ShellSlotGate.admits slot.Gate config then
            slot.FilledModuleId config
        else
            None

    /// The administration tiles the slots contribute. A tile-bearing slot
    /// contributes its tile whenever it is FILLED — by the SDK's module
    /// or by a replacement, the tile then carrying the replacement's id,
    /// name and icon — unless `supplied` already holds a tile for the
    /// module filling it, in which case that tile is the module's own
    /// and the slot's steps aside (one tile per module). `supplied` is
    /// every tile the deployment wires through
    /// `ClientConfig.Handlers.AdminTileContributors`.
    ///
    /// The slot's gate is deliberately NOT consulted, exactly as before
    /// the slots existed: the landing distinguishes "nothing was
    /// contributed" from "nothing you may see" by the contributed total,
    /// and the shell's visibility filter (`AdminTiles.visible`) already
    /// hides a tile whose module was not composed.
    let adminTiles (config: ClientConfig) (supplied: AdminTile list) : AdminTile list =
        let ownTiles = supplied |> List.map _.OwnerModuleId |> Set.ofList
        let ctx = { Config = config; DataTypes = [] }

        all
        |> List.choose (fun s ->
            match s.AdminTile with
            | None -> None
            | Some tileFor ->
                s.Fill ctx
                |> Option.map (fun filling -> tileFor filling.Definition)
                |> Option.filter (fun tile -> not (ownTiles.Contains tile.OwnerModuleId)))