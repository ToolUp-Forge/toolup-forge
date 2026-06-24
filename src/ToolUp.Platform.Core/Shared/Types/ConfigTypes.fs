// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Kind of a single configurable field. Drives admin-UI form
/// rendering and `ConfigStore.Set` validation. Deliberately limited
/// to primitive shapes a generic form can render without bespoke
/// components — modules that need richer editors should compose
/// smaller fields (e.g. two floats for a range) rather than extend
/// this DU with domain-specific kinds, which would make the SDK
/// sector-specific.
[<RequireQualifiedAccess>]
type ConfigFieldKind =
    /// Boolean — rendered as a checkbox.
    | Bool
    /// Integer — rendered as a number input, clamped to the optional
    /// inclusive bounds when present.
    | Int of min: int option * max: int option
    /// Floating-point — rendered as a number input, clamped to the
    /// optional inclusive bounds when present.
    | Float of min: float option * max: float option
    /// Free-form string — rendered as a text input, truncated to
    /// `maxLength` characters when set.
    | String of maxLength: int option
    /// Closed set of strings — rendered as a dropdown. The first
    /// entry is the UI's suggested default when the schema default
    /// is absent; callers should still set `DefaultJson` explicitly.
    | Choice of options: string list

/// Description of one configurable field. Supplied by a module (or
/// by the platform for `_platform` keys). `DefaultJson` is serialised
/// JSON of the default value so it round-trips through Fable without
/// needing a DU-aware transport for the default itself — the persisted
/// layer is `Map<string, string>` for exactly the same reason.
type ConfigFieldSchema = {
    /// Field key. Stable identifier used in the persisted document and
    /// in module code. Must be unique within a schema.
    Key: string
    /// Label shown in the admin UI form.
    DisplayName: string
    /// Optional help text rendered beneath the field in the admin UI.
    Description: string option
    /// Shape / validation constraints. Drives the form input and the
    /// `ConfigStore.Set` validator.
    Kind: ConfigFieldKind
    /// When `true`, the admin UI rejects an empty submission and
    /// `ConfigStore.Set` rejects a missing key. When `false`, a
    /// missing value falls through to `DefaultJson` in `GetEffective`.
    Required: bool
    /// JSON encoding of the field's default value. Used by
    /// `GetEffective` when the persisted document lacks the key, and
    /// pre-populated into the admin-UI form for unset fields. Must be
    /// a valid JSON literal for the declared `Kind`.
    DefaultJson: string
}

/// A module's typed configuration surface — the set of fields a team
/// admin can edit for that module. Modules declare this in their
/// `ClientModule.Config`; the platform declares one under the
/// reserved `_platform` module key for cross-module settings
/// (locale, timezone, date format).
///
/// Empty `Fields` is valid and means "no team-editable config" —
/// equivalent to `Config = None` but retained for schemas assembled
/// dynamically.
type ModuleConfigSchema = { Fields: ConfigFieldSchema list }

module ModuleConfigSchema =
    /// A schema with no editable fields. Useful as a placeholder for
    /// modules that expose `Config = Some _` only to receive the
    /// platform-level context on `Init` without adding their own keys.
    let empty: ModuleConfigSchema = { Fields = [] }

    /// Look up a field by key, honouring exact-match semantics (keys
    /// are case-sensitive — they're identifiers, not display strings).
    let tryField (key: string) (schema: ModuleConfigSchema) : ConfigFieldSchema option =
        schema.Fields |> List.tryFind (fun f -> f.Key = key)

/// Context handed to a module's `Init`. Carries the config values
/// the shell has fetched for this module plus the platform-level
/// lane, along with the resolved identity fields modules typically
/// need.
///
/// The raw per-field map shape mirrors the persistence format —
/// modules that declared a schema can read typed values by matching
/// the field key, parsing `JsonValue` themselves, or round-tripping
/// the whole map through their own deserialiser. A future helper may
/// project this into a strongly-typed record, but shipping the raw
/// map first keeps the surface minimal and avoids committing to a
/// particular serialiser at the client boundary.
type ClientModuleContext = {
    /// The persisted config for this module. Empty map when the
    /// module declared no schema, the shell hasn't loaded config yet
    /// (first `Init` before the async fetch completes), or the team
    /// admin hasn't saved any values (in which case modules fall
    /// back to their declared `DefaultJson`).
    Config: Map<string, string>
    /// The persisted platform-level config (`_platform` module key).
    /// Non-empty only when the deployment registered a platform
    /// schema via `ServerConfig.ModuleConfigs`.
    PlatformConfig: Map<string, string>
    /// Resolved feature-flag snapshot at `Init` time — one entry per
    /// declared flag, already coerced to match the declared shape by
    /// `FlagEvaluator.Resolve`. Empty map before the shell's
    /// `FlagsLoaded` prefetch completes, and for deployments that
    /// declare no flags. Modules typically read this via the
    /// `FeatureFlags` Feliz context rather than directly — the
    /// snapshot exists so `Init` can branch on a flag without
    /// triggering a React re-render.
    Flags: Map<string, FlagValue>
    /// Authenticated user's identifier. `"anonymous"` for an
    /// `AnonymousSession` subject.
    UserId: string
    /// Active team identifier when the active subject is a
    /// `TeamMember`; `None` otherwise.
    TeamId: string option
    /// Cross-module query bus for in-browser / HTTP-fallback dispatch.
    /// Modules use this to ask other modules for data
    /// without importing them — see `ModuleQueryClient.ask` for the
    /// typed caller-side helper. `ClientModuleContext.empty` wires a
    /// no-op bus that always returns `None`, so tests and the shell's
    /// pre-fetch seed don't need a real implementation.
    QueryBus: IModuleQueryBus
    /// Hook for modules that change the user's active team to notify
    /// the shell so it can run the `TeamSwitched` reset path
    /// (clear `ModuleStates` / `ModuleConfigs` / `ResolvedFlags` /
    /// `AccessibleModules`, refetch them all against the new team,
    /// and re-init the active module). Set by the shell to
    /// `Some (fun teamId -> dispatch (TeamSwitched teamId))` in
    /// `Team` surfaces; `None` otherwise. The built-in `TeamManagerUI`
    /// invokes this from its `ActiveTeamSwitched` and `TeamCreated`
    /// handlers; custom team-management UIs should do the same.
    OnTeamSwitched: (string -> unit) option
    /// Phase 245 — hook for a module that changes a team's module
    /// **exposure** (the per-team sidebar-visibility set) to ask the
    /// shell to re-fetch its `AccessibleModules` so the sidebar (and the
    /// admin "show hidden modules" toggle) update live, without a manual
    /// reload. Lighter than `OnTeamSwitched`: it refreshes only the
    /// accessible-modules list, leaving module states / config / flags
    /// intact. Set by the shell to `Some (fun () -> dispatch
    /// RefreshAccessibleModules)` on `Team`-shaped surfaces; `None`
    /// otherwise. The built-in `PermissionsAdminUI` invokes it after a
    /// successful `SetModuleExposure`.
    OnAccessibleModulesChanged: (unit -> unit) option
}

/// No-op `IModuleQueryBus` used by `ClientModuleContext.empty` and
/// anywhere else that needs a context stub without a real bus. Every
/// query returns `None` — the same shape as "target module not
/// deployed", so callers that branch on the three-valued return behave
/// identically to a deployment where no handlers were registered.
type private NoOpModuleQueryBus() =
    interface IModuleQueryBus with
        member _.Ask(_, _) = async { return None }

module ClientModuleContext =
    /// The empty context — no config, anonymous user. Used as the
    /// pre-fetch seed in the shell's `init`, and as a safe default
    /// for tests that exercise a module's `Init` without wiring the
    /// whole shell.
    let empty: ClientModuleContext = {
        Config = Map.empty
        PlatformConfig = Map.empty
        Flags = Map.empty
        UserId = "anonymous"
        TeamId = None
        QueryBus = NoOpModuleQueryBus() :> IModuleQueryBus
        OnTeamSwitched = None
        OnAccessibleModulesChanged = None
    }

/// Reserved identifiers for the config subsystem.
module ConfigKeys =
    /// Reserved module key for cross-module platform configuration
    /// (locale, timezone, date format). Indexed the same way as any
    /// other module's config — lives under
    /// `_platform/config/{container}/_platform.json` when persisted.
    ///
    /// Exposed as a literal so deployments, the admin UI, and the
    /// shell can refer to the same key without reintroducing the
    /// string at every call site.
    [<Literal>]
    let PlatformModuleKey = "_platform"

    /// Field keys for the SDK-shipped per-team branding fields
    /// (Phase 5e). These live on the reserved `_platform` schema — the
    /// existing Platform Defaults admin tab — and are merged in by
    /// `compose` only when the deployment is team-scoped. The client
    /// shell resolves them via `Branding.resolve` against the prefetched
    /// `_platform` config, each field falling back to the composition
    /// root's `ClientConfig` default when the active team set no
    /// override. Centralised so the server schema and the client
    /// resolver name the same keys.
    module BrandingKeys =
        [<Literal>]
        let AppName = "appName"

        [<Literal>]
        let PrimaryColor = "primaryColor"

        [<Literal>]
        let LogoUrl = "logoUrl"

        [<Literal>]
        let FaviconUrl = "faviconUrl"

        // Phase 223 — full allow-listed palette (colours only; never fonts or
        // component shape). Each drives the matching client-toolkit/shell theming
        // token at :root, so a team's override re-skins the whole client surface.
        [<Literal>]
        let BrandDarkColor = "brandDarkColor"

        [<Literal>]
        let SidebarColor = "sidebarColor"

        [<Literal>]
        let PosColor = "posColor"

        [<Literal>]
        let NegColor = "negColor"

    /// Reserved module key for the per-team notification-prefs surface.
    /// Auto-injected by `compose` so deployments enabling
    /// transactional sinks (email / SMS / push) get a dedicated admin
    /// tab without each app re-declaring the schema. A `false` default
    /// on every `*.enabled` flag is the team-wide kill switch — admins
    /// must explicitly opt in to outbound transactional delivery.
    /// `IConfigStore` reads / writes the document at
    /// `{container}/_platform/notification_prefs.json`.
    [<Literal>]
    let NotificationPrefsModuleKey = "_platform.notification_prefs"

    /// Field keys inside the `_platform.notification_prefs` schema.
    /// Centralised so sinks read the same key the schema declares —
    /// the dispatcher resolves these via `IConfigStore.GetEffective`
    /// before invoking any sink.
    module NotificationPrefsKeys =
        [<Literal>]
        let EmailEnabled = "email.enabled"

        [<Literal>]
        let EmailFromAddress = "email.fromAddress"

        [<Literal>]
        let SmsEnabled = "sms.enabled"

        [<Literal>]
        let PushEnabled = "push.enabled"

        /// Phase 30d — per-scope upper bound on
        /// `IDataCatalog.GetSyntheticSample`'s row `count`.
        /// `ModulePermission.SchemaOnly` partner-sandbox callers cannot
        /// exceed this value (the catalog gate clamps before generation).
        /// Unset = SDK default
        /// (`SyntheticSampleGenerator.DefaultMaxSampleRows`). The key
        /// lives on `_platform.notification_prefs` because that schema is
        /// already the per-scope partner-policy lane; folding it in
        /// avoids minting a new platform module for one field. Despite
        /// the lane name, this is unrelated to notification delivery.
        [<Literal>]
        let SchemaOnlyMaxSampleRows = "schemaOnly.maxSampleRows"