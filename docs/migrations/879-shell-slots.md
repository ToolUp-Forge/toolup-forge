# Phase 879 — built-in client modules fill declared shell slots

**What changes for you:** if you set any of the twenty per-built-in `ClientConfig` fields below
(`TeamManager = NoTeamManager`, `DataManager = MappingDataManager`, `HealthMonitor =
ExternalHealthMonitor m`, …), run the codemod. A deployment that set none of them changes nothing:
the sidebar it composes — every module, in the same order, behind the same gates — is identical,
and that is pinned by baselines recorded before the change (see [Verification](#verification)).

This is a breaking change in a 0.x minor, landing before 1.0 by operator decision.

## What it is

The shell used to know each replaceable built-in by name: one mode type per built-in
(`TeamManagerMode = NoTeamManager | DefaultTeamManager | ConfiguredTeamManager of
TeamManagerConfig | ExternalTeamManager of ErasedModule`, twenty of them), one `ClientConfig` field
per built-in, twenty byte-identical `{ Name; Icon }` records, and a hand-written arm per built-in in
`prepareModules`. A deployment could already put its own module in place of a built-in, but the
swap was partial: **the replacement lost the built-in's administration tile**, and the data-manager
lookup, the boot-time prefetch and the tile code all still asked which built-in they were talking
to.

Now the shell declares **slots** (`ShellSlots.all`). A slot has a stable id, a position (Home,
Leading, Trailing), a gate over the deployment's declared surfaces, and optionally an administration
tile. What fills it is one type for every slot:

```fsharp
[<RequireQualifiedAccess>]
type SlotFill<'cfg> =
    | Empty
    | Default
    | Configured of 'cfg
    | External of ErasedModule
```

`'cfg` is `ModuleLabel` (the one `{ Name; Icon }` record) for nineteen slots and
`DataManagerChoice` for the data manager. `ClientConfig.Slots : ShellSlotFills` carries one fill per
slot, in place of the twenty fields. Placement, the gate, the tile and every shell lookup follow the
slot, so **a replacement is a full replacement**: it sits where the built-in sat, behind the same
gate, and carries the slot's tile pointed at itself — unless it wires its own tile through
`ClientConfig.Handlers.AdminTileContributors`, in which case that one is kept and the slot's steps
aside.

## Run the codemod

```
dotnet run --project <path-to>/Build.fsproj -- Codemod <your-src-dir> --check   # preview
dotnet run --project <path-to>/Build.fsproj -- Codemod <your-src-dir>           # apply
```

```diff
 let config = {
     ClientConfig.defaults with
-        TeamManager = ExternalTeamManager myTeams
-        UsageDashboard = NoUsageDashboard
-        HealthMonitor = ConfiguredHealthMonitor { Name = "Health"; Icon = Icons.health }
-        DataManager = MappingDataManager
+        Slots.TeamManager = SlotFill.External myTeams
+        Slots.UsageDashboard = SlotFill.Empty
+        Slots.HealthMonitor = SlotFill.Configured { Name = "Health"; Icon = Icons.health }
+        Slots.DataManager = (SlotFill.Configured(DataManagerChoice.ColumnMapping None))
 }
```

It rewrites, line by line: a field set to one of its own cases moves under `Slots.`; `No…` →
`SlotFill.Empty`; `Default…` / `EnabledHomeModule` → `SlotFill.Default`; `Configured…` (labelled
slots) → `SlotFill.Configured`; `External…` → `SlotFill.External`; `MappingDataManager` →
`(SlotFill.Configured(DataManagerChoice.ColumnMapping None))`; `<BuiltIn>Config` → `ModuleLabel`.
It **reports** and does not touch: `ConfiguredDataManager cfg` (now
`SlotFill.Configured(DataManagerChoice.FileUpload cfg)`) and `ConfiguredMappingDataManager cfg` (now
`SlotFill.Configured(DataManagerChoice.ColumnMapping(Some cfg))`); a field whose value starts on the
next line; any `<BuiltIn>Mode` type annotation; and any read such as `config.UsageDashboard`
(now `config.Slots.UsageDashboard`, matched on `SlotFill` cases).

## The mode-to-slot table

Every slot keeps its field name under `Slots`, its default, and its gate. Group, nav role and
visibility are the **built-in module's own** declarations — a replacement declares its own, and
should declare these to sit in the same sidebar group and hide from the same people.

| `Slots.` field | Old cases | Default | Position / gate | Built-in id · group · nav role | Tile |
|---|---|---|---|---|---|
| `HomeModule` | `No/Enabled/Configured/ExternalHomeModule` | `Empty` | Home · always | `_sdk.home` · none · none | — |
| `DataManager` | `No/Default/Configured/ExternalDataManager`, `(Configured)MappingDataManager` | `Default` | Leading · always | `_sdk.DataManager` or `_sdk.MappingDataManager` · Data Management · none | — |
| `TeamManager` | `…TeamManager` | `Default` | Trailing · any Team surface | `_sdk.TeamManager` · Team Management · TeamOwnerAdmin | Teams |
| `TeamConfig` | `…TeamConfig` | `Default` | Trailing · any authenticated surface | `_sdk.TeamConfig` · Team Management · TeamOwnerAdmin | — |
| `WebhookAdmin` | `…WebhookAdmin` | `Empty` | Trailing · authenticated | `_sdk.WebhookAdmin` · Team Management · TeamOwnerAdmin | — |
| `ServiceAccountAdmin` | `…ServiceAccountAdmin` | `Empty` | Trailing · authenticated | `_sdk.ServiceAccountAdmin` · Team Management · TeamOwnerAdmin | — |
| `ExternalContactManager` | `…ExternalContactManager` | `Empty` | Trailing · authenticated | `_sdk.ExternalContactManager` · Team Management · TeamOwnerAdmin | — |
| `NotificationPreferences` | `…NotificationPreferencesUI` | `Empty` | Trailing · authenticated | `_sdk.NotificationPreferences` · Settings · none | — |
| `ModuleVisibilityAdmin` | `…ModuleVisibilityAdmin` | `Empty` | Trailing · authenticated | `_sdk.ModuleVisibilityAdmin` · Team Management · TeamOwnerAdmin | — |
| `SessionSecurity` | `…SessionSecurity` | `Empty` | Trailing · authenticated | `_sdk.SessionSecurity` · none · none | — |
| `PermissionsAdmin` | `…PermissionsAdmin` | `Default` | Trailing · authenticated | `_sdk.PermissionsAdmin` · Team Management · TeamOwnerAdmin | — |
| `UsageDashboard` | `…UsageDashboard` | `Default` | Trailing · authenticated | `_sdk.UsageDashboard` · Team Management · TeamOwnerAdmin | Usage |
| `AuditViewer` | `…AuditViewer` | `Default` | Trailing · authenticated | `_sdk.AuditLog` · Team Management · TeamOwnerAdmin | — |
| `DataIngestionAdmin` | `…DataIngestionAdmin` | `Default` | Trailing · authenticated | `_sdk.DataIngestion` · Team Management · TeamOwnerAdmin | — |
| `PlatformAdmin` | `…PlatformAdmin` | `Default` | Trailing · always | `_sdk.PlatformAdmin` · Platform Management · PlatformAdminOnly | — |
| `HealthMonitor` | `…HealthMonitor` | `Default` | Trailing · always | `_sdk.HealthMonitor` · Observability · PlatformAdminOnly | Health Monitor |
| `ServiceStatusBoard` | `…ServiceStatusBoard` | `Default` | Trailing · always | `_sdk.ServiceStatusBoard` · Platform Management · PlatformAdminOnly | Service Status |
| `DataSubjectRequestAdmin` | `…DataSubjectRequestAdmin` | `Empty` | Trailing · authenticated | `_sdk.DataSubjectRequests` · Platform Management · PlatformAdminOnly | — |
| `MigrationAdmin` | `…MigrationAdmin` | `Empty` | Trailing · authenticated | `_sdk.DataMigrations` · Platform Management · TeamOwnerAdmin | — |
| `CompositionInspector` | `…CompositionInspector` | `Default` | Trailing · authenticated | `_sdk.CompositionInspector` · Platform Management · TeamOwnerAdmin | — |

Every built-in declares `Visibility.visibleToAuthenticated` except Home (visible to all).

**Not slots, and unchanged:** the toast centre, auth UI, loading indicator, not-authorised view,
command palette, admin surface, offline mode, observability module and platform users. None ever
accepted a deployment's module, and this phase does not pretend a slot answers whether one should.
(`PlatformUsersConfig` is `ModuleLabel` now; `PlatformUsersMode` is unchanged.)

## The one behaviour change you can see

A deployment that filled a tile-bearing slot (team manager, usage dashboard, health monitor, service
status board) with its own module now gets that slot's administration tile — titled with the
replacement's name, clicking through to the replacement. It used to get no tile. If you already
contribute a tile owned by your replacement's id, yours is kept and the slot adds none.

## Verification

- `src/ToolUp.AI.Client.Tests/shell-slot-baselines/` holds the prepared module list and the admin
  tiles for sixteen reference configurations, **recorded on the tree before this change**. After it,
  every prepared-module baseline is byte-identical; the only tile baselines that moved are the two
  "every slot External" configurations, which gained the four slot tiles described above.
- `ShellSlotTests` pins that every one of the twenty slots accepts a deployment's module, that the
  id the shell reads without building a module is the id it would build, and the tile rules.
- For your own deployment: before upgrading, log `Client.moduleIdentityReport (Client.prepareModules
  config modules)`; after, log it again. The lists should be identical.

## Rollback

Pin the previous SDK version and revert the codemod's diff; nothing server-side changed.
