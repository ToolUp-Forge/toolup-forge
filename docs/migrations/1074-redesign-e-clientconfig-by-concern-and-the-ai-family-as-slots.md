# forge#1074: `ClientConfig` by concern, the chrome rows as `ChromeFill`, the AI family as slots

**What changes.** The client twin of [877](877-nested-server-config.md). `ClientConfig` was one record of
forty-five fields; it is now the identity fields beside the concern records:

```fsharp skip=fragment
type ClientConfig = {
    AppName: string
    AppLogo: string
    Surfaces: SurfaceProfile list
    Slots: ShellSlotFills                // 879
    Chrome: ShellChrome                  // the six chrome rows
    Auth: AuthConfig
    PublicUtility: PublicUtilityConfig
    Locale: LocaleConfig
    Shell: ShellConfig
    Composition: CompositionConfig
}
```

A new client toggle is a field on the concern record it belongs to; an on/off toggle is 877's
`FeatureMode`. Each concern record has its own `defaults` (`CompositionConfig.create handlers`), and
`ClientConfig.create` / `ClientConfig.defaults` are unchanged in what they produce.

**Applying it.** `dotnet run --project Build.fsproj -- Codemod <your-client-dir> --phase 1074` (or with no
`--phase`, every migration). F# nests a copy-and-update label, so a moved field keeps its update with
a qualified label:

```diff
 let config = {
     ClientConfig.create handlers with
         AppName = "Acme"
-        ActiveModule = Some "reports"
-        AuthUI = OidcAuthUI oidc
-        CommandPalette = DefaultCommandPalette
+        Shell.ActiveModule = Some "reports"
+        Auth.AuthUI = OidcAuthUI oidc
+        Shell.CommandPalette = FeatureMode.Enabled
 }
```

The codemod moves a label only inside an update whose base names a client configuration
(`ClientConfig.defaults`, `ClientConfig.create …`, `config`, `cfg`, `clientConfig`), so a
`ClientConfigOverrides` update or a message-catalog override keeps its labels. A read of a field only
`ClientConfig` carries (`config.AuthUI`) is rewritten; a read of a name a server or catalog record
also carries (`config.Handlers`, `.Observability`, `.Presence`, `.ModuleFilter`, `.Locale`, …) is
reported as `1074-shared-field-read`.

## The field map

| Was (`ClientConfig.`) | Is |
|---|---|
| `LoadingIndicator`, `ActiveModule`, `BrowserHistory`, `NotAuthorisedView`, `HomeRecents`, `ToastCentre`, `GlobalOverlays`, `InputsPaneWidth`, `GridModules`, `Presence`, `Offline` | `Shell.<same>` |
| `CommandPalette: CommandPaletteMode` | `Shell.CommandPalette: FeatureMode` (`NoCommandPalette` → `Disabled`, `DefaultCommandPalette` → `Enabled`) |
| `AuthUI`, `AuthBridge`, `DevDefaultUserId` | `Auth.<same>` |
| `AuthTokenStorage: AuthTokenStorage` | `Auth.HttpOnlySession: FeatureMode` (`ClientCookieAndLocalStorage` → `Disabled`, `ServerSetHttpOnlyCookie` → `Enabled`) — the client twin of the server's `SecurityPosture.AuthCookieIssuance` |
| `PrerenderRoutes`, `ConsentProvider`, `AdPanel`, `PremiumModel`, `PlatformAdminProfile` | `PublicUtility.<same>` |
| `Locale: LocaleMode` | `Locale.Resolution` |
| `MessageCatalogOverride` | `Locale.MessageCatalogOverride` |
| `Handlers`, `RequestSeam`, `ModuleFilter`, `ShowDebugOnlyModules`, `ExpectedModules`, `PublicEntryDispatchers`, `OnError`, `EnableElmishConsoleTrace`, `OnElmishError` | `Composition.<same>` |
| `AdminSurface: AdminSurfaceMode` | `Chrome.AdminArea: ChromeFill<unit>` (`InlineGroups` → `Off`, `SeparateArea` → `On ()`) |
| `Observability: ObservabilityModuleMode` | `Chrome.Observability: ChromeFill<unit>` |
| `PlatformUsers: PlatformUsersMode` | `Chrome.PlatformUsers: ChromeFill<unit>` |
| `DatadogReadback: DatadogReadbackMode` | `Chrome.DatadogReadback: ChromeFill<DatadogReadbackConfig>` — the same config value the server's `EnabledDatadogReadback` carries (reported, not rewritten: the server field keeps its mode) |
| `NoActiveTeamLandingModuleId: string option` + `NoActiveTeamLanding: NoActiveTeamLandingConfig option` | one `Chrome.NoActiveTeamLanding: ChromeFill<NoActiveTeamLandingFill>` — `On(Module id)` for your module, `On(BuiltIn copy)` for the SDK's landing (when both were set, the module won: keep `Module`). Reported, not rewritten. |
| (tenant lifecycle, unconditional) | `Chrome.TenantLifecycle: ChromeFill<unit>`, default `On ()` |

The retired types: `CommandPaletteMode`, `AuthTokenStorage`, `AdminSurfaceMode`, `ObservabilityModuleMode`,
`PlatformUsersMode`, `AIAssistantMode`, and the `[<Obsolete>]` `AuthUIMode.ClerkAuthUI` case.
`ClientConfig.isAdminSidebarGroup` and `ClientConfig.isPlatformAdminSidebarGroup` were forwarders;
call `SidebarVisibility.isAdminSidebarGroup` / `isPlatformAdminSidebarGroup`. The no-active-team gate's
admin exception is `SidebarVisibility.noActiveTeamAdmits`.

**Kept, with the reason.** `BrowserHistory` keeps its `BrowserHistoryMode` union and its cases and moves
to `Shell.BrowserHistory`: the public composition profile binds the union (`spec/surface-binding.json`,
`clientShell.browserHistory` — one case per profile token, one for the absent member), and the binding
now names the field by its path from `ClientConfig`. The profile's vocabulary is unchanged, so its
version does not move (§P1). `ModuleAvailability`, `ModuleArea` and
`BearerTokenKind` are two-case unions with no payload, but they classify a module or choose a token,
and neither case is "off" — 877's rule keeps such a choice its own type. `OfflineMode` carries a
payload.

## `ClerkAuthUI` is gone

Clerk is the ClerkUI companion's: `ClerkAuthUI cfg` is `ToolUp.AuthProviders.ClerkRegister.authUI cfg`
(the vendor-neutral `ProviderAuthUI ("clerk", box cfg)`, the documented path since
[494](494-vendor-neutral-auth-ui.md)), with `ClerkRegister.handler` in
`Composition.Handlers.AuthUIHandlers`. The codemod rewrites the construction; a `| ClerkAuthUI _ ->`
pattern is reported (`1074-clerk-auth-ui-pattern`) — match `ProviderAuthUI ("clerk", _)`.

## The data manager's two choices are symmetric

```diff
-SlotFill.Configured(DataManagerChoice.ColumnMapping None)
+SlotFill.Configured(DataManagerChoice.ColumnMapping DataManagerConfig.unlabelled)
-DataManagerChoice.FileUpload { Name = "Uploads"; Icon = icon; Group = Some "Data" }
+DataManagerChoice.FileUpload { Label = Some { Name = "Uploads"; Icon = icon }; Group = Some "Data" }
```

`DataManagerConfig` is an optional `ModuleLabel` (`None` keeps the manager's own) plus a `Group`, and both
cases carry it. The `None` form is rewritten; a record literal is reported (`1074-data-manager-label`).

## The AI assistant family is three slots

`AIAssistantMode` is retired. `ToolUp.AI.Client` declares three slots through the shell's companion-slot
seam (`ShellSlotFills.Extensions`, a `SlotExtension` list): `AIAssistant: SlotFill<AIAssistantClientBranding>`,
`AISettings: SlotFill<ModuleLabel>`, `PlatformAIKeys: SlotFill<ModuleLabel>` — the record `AISlotFills`.

| Was | Is |
|---|---|
| `NoAIAssistant` | `AISlotFills.empty` |
| `DefaultAIAssistant` | `AISlotFills.defaults` |
| `ConfiguredAIAssistant branding` | `AISlotFills.configured branding` |
| `AIClientConfig.appendAssistantModule mode modules` | `AIClientConfig.withAISlots fills config` (the pages fill slots, not the module list) |

`AIClientConfig.run` / `program` take the `AISlotFills` where they took the mode. Any slot can now be
`Empty` or `External myModule`. The pages sit where they did — after your modules, ahead of the
administration built-ins — and count with your modules for the module filter and the boot validators
(the grouping check, the Phase 583 parity check), as they did when they were appended.

## `PlatformAdminUI.create`

`PlatformAdminUI.create label publicUtility` takes the deployment's `PublicUtilityConfig`, not the whole
`ClientConfig`: the public-utility widgets read its profile, ad panel and premium model, and nothing
else.

**Verification.** `dotnet build` of your client; the codemod's `--check` lists nothing left to rewrite;
read the review findings. The shell's composition is unchanged for every configuration (the 32
shell-slot snapshot baselines are byte-identical).

**Rollback.** Pin the SDK below 0.42.0.
