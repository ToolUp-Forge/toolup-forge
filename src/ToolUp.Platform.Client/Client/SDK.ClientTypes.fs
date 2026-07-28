// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Elmish
open Feliz

// ─── Phase 57 — static-prerender substrate types ─────────────────
//
// `PrerenderMeta` / `PrerenderRoute` / `PrerenderResult` lifted to
// `ToolUp.Platform.Core/Shared/Types/PrerenderTypes.fs` so the
// `ToolUp.Platform.Build` FAKE Prerender target factory can
// consume them without a Build → Client dep. The
// `ClientConfig.PrerenderRoutes` field below references the lifted
// types directly via the shared `ToolUp.Platform` namespace.

// ─── Data type display ────────────────────────────────────────────

/// Client-side metadata for rendering data type summaries in the file manager.
/// Each module that handles file data provides one of these per data type.
type DataTypeDisplay = {
    /// Shared metadata (Id + DisplayName), declared in the module's SharedTypes.
    Info: DataManagementTypes.DataTypeInfo
    /// Render a summary table from the type-erased Info fields of ProcessedFileEntries.
    /// The function receives a list of unboxed Info objects and returns a ReactElement.
    RenderSummary: obj list -> ReactElement
}

// ─── Module availability ──────────────────────────────────────────

/// Build-config gate over module registration. Orthogonal to
/// `config.ModuleFilter` (dev-run single-module filter), RBAC
/// (`GetAccessibleModules`), and `FeatureFlags` (per-view runtime
/// toggles) — this DU expresses whether a module is part of the
/// shipping surface at all. `DebugOnly` modules still ship in the
/// Release JS bundle (the fsproj imports them unconditionally); they
/// are just omitted from the sidebar when the app is built without
/// `DEBUG` defined.
type ModuleAvailability =
    /// Registered in every build. Default.
    | Always
    /// Only registered when the app's client fsproj was compiled with
    /// `DEBUG` defined — hides work-in-progress modules from Release
    /// builds without stripping their source.
    | DebugOnly

// ─── Page content shapes ──────────────────────────────────────────

/// Rendering shape for a page's content area. Returned by a module's
/// per-page view function (`withPages`) so each page picks its own
/// layout format. The shell's `Layout.AppShell` branches on the case
/// and lays the content out accordingly.
///
/// The side panel slot (right-fixed — today used for the AI assistant)
/// and the page header are orthogonal to every case and rendered by
/// the shell around the returned content.
///
/// Legacy single-page modules still return `ReactElement * ReactElement`
/// from their `View` function; the shell adapts that tuple to
/// `SplitPanel(l, r)` automatically, so existing modules need no changes.
type PageContent =
    /// Narrow left control pane + wide right output pane, side by side.
    /// Default shape; matches the current single-tuple contract.
    | SplitPanel of left: ReactElement * right: ReactElement
    /// Vertical stack of sections rendered top-down with a consistent
    /// gutter. Suits pages that read as one logical flow (Dataset
    /// selection + quality panel + preview; admin forms).
    | Stacked of sections: ReactElement list
    /// Single full-width pane, no internal divisions. Suits single
    /// visualisations or report views.
    | FullWidth of content: ReactElement
    /// Named-area grid. The shell maps each name to a CSS grid area
    /// with a predefined template; unknown names fall through to a
    /// default row flow. Suits multi-tile dashboards.
    | Dashboard of areas: (string * ReactElement) list
    /// Escape hatch: the module supplies its own top-level element and
    /// the shell renders it verbatim inside the main content area.
    /// Use sparingly — bypasses shell-provided gutters and styling.
    | Custom of ReactElement

// ─── Module definition ────────────────────────────────────────────

/// Route configuration for a single page within a module.
///
/// `Icon` is a typed `ReactElement` (typically imported via
/// vite-plugin-svgr's `?react` query suffix and lifted with
/// `Icon.ofImport`). The render path inlines the SVG component
/// directly so `currentColor` cascades from the parent's CSS color.
type PageConfig = {
    Route: string
    Title: string
    Icon: ReactElement
}

/// Metadata describing a module for registration with the platform.
///
/// `Id` is the stable identifier — used as the Map key for module state,
/// the sidebar-filter lookup against `GetAccessibleModules`, the
/// `AIMessageRequest.ActiveModule` payload, and (for modules exposed by
/// the app server) the `makePermissionGuardedApi` / `AccessContext`
/// permission key. Convention: PascalCase with no spaces, matching the
/// string passed to `makePermissionGuardedApi` in the app server (e.g.
/// "SkuAnalysis", "KnowledgeBase"). Must be unique across the module
/// list. Never shown to users.
///
/// Phase 279 — `Id` *is* the module's stable component identity: it is
/// declared and independent of `Name`, so renaming the display `Name`
/// never changes it. It lifts losslessly to a `ComponentId` via
/// `ComponentId.ofModule Id` for the introspection / telemetry-
/// correlation surfaces that address composed units by value. The server
/// registration surface declares the same identity explicitly via
/// `ServerModule.withComponentId`; on the client the existing `Id` field
/// already carries it, so no field changes here (GP 11).
///
/// `Name` is the display name rendered in the sidebar and page header.
/// Free-form, human-readable, localisable. Duplicates are tolerated
/// (the Id is the real key) but should be avoided for UX.
///
/// `Icon` is a typed `ReactElement` — a vite-plugin-svgr-imported SVG
/// component or any other `ReactElement` value. Used by the shell
/// sidebar for single-page modules — when `Pages = []` the shell
/// auto-derives a single sidebar entry from `Name + Icon` and the
/// route `"/" + lowercased-Id`. Multi-page modules supply each page's
/// icon via `withPages` and `Definition.Icon` is informational.
type ModuleDefinition = {
    Id: string
    Name: string
    Icon: ReactElement
    Pages: PageConfig list
}

// ─── Navigation area (Phase 567) ──────────────────────────────────

/// The sidebar navigation area a module belongs to. Under
/// `ClientConfig.AdminSurface = SeparateArea` the sidebar renders one
/// area at a time (product modules vs admin modules) with a switcher;
/// under the default `InlineGroups` it is ignored (today's inline
/// groups). A module's *effective* area is `Administration` if it
/// either declares `Area = Administration` (via `ClientModule.withArea`)
/// OR sits in an admin sidebar group (`ClientConfig.isAdminSidebarGroup`
/// — how SDK admin built-ins derive it without changing registration,
/// GP 9); otherwise `Product`. See `ClientConfig.effectiveArea`.
type ModuleArea =
    | Product
    | Administration

// ─── Type erasure ─────────────────────────────────────────────────

/// Type-erased module wrapper for heterogeneous list composition.
/// All box/unbox is contained here — modules never see it.
type ErasedModule = {
    Definition: ModuleDefinition
    Init: ClientModuleContext -> obj * Cmd<obj>
    Update: obj -> obj -> obj * Cmd<obj>
    /// Single-page view function. `Some` for single-page modules,
    /// `None` for multi-page modules (which use `PageViews` instead).
    /// At least one of `View` or `PageViews` must be set; `register`
    /// throws at construction if both are `None`.
    View: (obj -> (obj -> unit) -> ReactElement * ReactElement) option
    /// Per-page views, keyed by `PageConfig.Route`. `None` → legacy
    /// single-page; the shell uses `View` for every page and wraps its
    /// returned tuple into `SplitPanel(l, r)`. `Some map` → multi-page;
    /// the shell looks up the active page's route and dispatches to the
    /// mapped view, which returns a `PageContent` case directly.
    /// `Init` / `Update` still fire once per module — all pages share
    /// the same `Model`.
    PageViews: Map<string, obj -> (obj -> unit) -> PageContent> option
    NeedsData: ((DataManagementTypes.DataTypeId -> bool) -> bool) option
    /// Data types this module can display summaries for in the file manager.
    DataTypes: DataTypeDisplay list
    /// Extract processed data from module state (aggregated by the shell into
    /// `Model.ProcessedData` and published through `ProcessedDataContext`).
    ProvidesProcessedData: (obj -> ProcessedDataTypes.ProcessedFileEntry list) option
    /// Extract the structured narrative currently displayed for a given
    /// page. Receives the module's state and the active page route; returns
    /// `Some doc` when the page renders a `NarrativeDocument`, `None`
    /// otherwise. Used by the AI side-panel to snapshot the current page's
    /// narrative into `AIMessageRequest.ActivePageNarrative` so the agent
    /// can interpret / summarise without a tool call.
    ProvidesNarrative: (obj -> string option -> Narrative.NarrativeDocument option) option
    /// Optional team-editable config schema. When `Some`, the admin UI
    /// surfaces a form under this module's key and the shell hands the
    /// persisted values to `Init` via `ClientModuleContext.Config`.
    Config: ModuleConfigSchema option
    /// Feature flags this module reads via `FeatureFlags.flag` /
    /// `variant`. The shell aggregates these across all registered
    /// modules to form the client-side declared-key set; reads on a
    /// key outside the set log a `console.warn` (typo protection).
    /// Default `[]` — modules that don't read flags declare nothing.
    ///
    /// For server-side evaluator validation (shape checks on admin
    /// writes) and admin-UI rendering, the app's composition root must
    /// also pass these into `ServerConfig.FeatureFlags` — the server
    /// and client composition roots are separate, so the union happens
    /// at the app level (same pattern as `DataTypes` / `ModuleConfigs`).
    FeatureFlags: FeatureFlag list
    /// Build-config availability gate. `DebugOnly` modules are dropped
    /// by `SDK.Client.prepareModules` in Release builds. Default `Always`.
    Availability: ModuleAvailability
    /// Optional taxonomy group for sidebar grouping. Modules with the
    /// same group render under one collapsible section; `None` groups
    /// into a default "Other" bucket (or at top-level if it's the only
    /// group). The structural taxonomy is module-declared; per-user
    /// ordering / pinning is a separate overlay (see
    /// `UserSidebarPreferences`).
    Group: string option
    /// Phase 567 — declared navigation area. Default `Product`; set to
    /// `Administration` via `ClientModule.withArea` to place a consumer
    /// module in the admin area under `AdminSurface = SeparateArea`. SDK
    /// admin built-ins leave this `Product` and are derived into the admin
    /// area from their group (`ClientConfig.effectiveArea`).
    Area: ModuleArea
    /// Client-side `ModuleQueryBus` handlers this module publishes.
    /// Collected by `SDK.Client.run` into the per-module registry of the
    /// `ClientModuleQueryBus`, which prefers local dispatch before
    /// falling back to the server over ToolUp.Remoting. Default `[]` —
    /// modules that only make queries (or that only answer from the
    /// server) declare nothing.
    ClientQueryHandlers: ModuleQueryHandler list
    /// Decoder for server-published `Notification.ModuleAction` events
    /// targeting this module. Receives `(actionKey, payloadJson)` and
    /// returns the module's `Msg` to dispatch, or `None` to reject the
    /// action (unknown key, bad payload). The shell looks the decoder up
    /// by module Id, calls it, and routes the boxed result through the
    /// existing `ModuleMsg` pathway — no module knows about any other
    /// module's decoder. Modules without a decoder silently ignore
    /// targeted actions.
    ActionDecoder: (string * string -> obj option) option
    /// Phase 66 Stream B.3 — per-module sidebar visibility predicate
    /// over the resolved `SubjectKind`. The shell's sidebar filter
    /// invokes this for every registered module before rendering;
    /// modules returning `false` for the current `SubjectKind` are
    /// hidden, structurally replacing the deployment-wide blanket-hide
    /// behaviour Phase 55 introduced via `PlatformApiHandler.fs:497`.
    ///
    /// Default `fun _ -> true` — visible to every subject kind. The
    /// pre-B.3 sidebar shape (every module visible regardless of
    /// auth state) is preserved byte-for-byte for modules that
    /// declare nothing. Modules that should hide from anonymous
    /// callers declare `Visibility.visibleToAuthenticated`; modules
    /// that gate further declare `Visibility.visibleTo [ TeamMemberKind ]`
    /// or a custom predicate. See `Visibility` module for smart
    /// constructors.
    Visibility: SubjectKind -> bool
    /// Cross-module client event subscriptions, keyed by topic. The
    /// shell subscribes to the `ModuleEvents` bus once at boot; for
    /// every publication it looks up each registered module's map by
    /// topic and, when present, applies the mapper's `(payload -> Msg)`
    /// result against that module's state (init-on-demand if the module
    /// has never been navigated to) — exactly the `ModuleAction` routing
    /// shape, but client-internal and identity-free. The mapper is
    /// erased to `string -> obj` here; `register` boxes the typed
    /// `string -> 'Msg`. Default empty — modules that don't react to
    /// sibling events declare nothing. Construct via
    /// `ClientModule.withEventSubscription`.
    EventSubscriptions: Map<string, string -> obj>
}

/// Phase 66 Stream B.3 — smart constructors for the per-module
/// `Visibility: SubjectKind -> bool` predicate. The default
/// `visibleToAll` matches pre-B.3 behaviour (every module visible);
/// `visibleToAuthenticated` hides modules from anonymous callers
/// (Phase 55's intent expressed at module granularity); `visibleTo`
/// admits a fixed set of `SubjectKind` values.
module Visibility =
    /// Visible to every `SubjectKind`. The default — preserves the
    /// pre-B.3 sidebar shape for modules that declare nothing.
    let visibleToAll: SubjectKind -> bool = fun _ -> true

    /// Visible to authenticated subjects only — hides from anonymous
    /// callers. Admits `UserKind`, `TeamMemberKind`, and
    /// `ClaimBearerKind`; rejects `AnonymousKind`. The common
    /// pattern for modules whose value depends on a signed-in user
    /// (Settings, Permissions, Team Manager).
    let visibleToAuthenticated: SubjectKind -> bool =
        function
        | AnonymousKind -> false
        | UserKind
        | TeamMemberKind
        | ClaimBearerKind -> true

    /// Visible to anonymous subjects only. Symmetric with
    /// `visibleToAuthenticated`. Use for sign-up / welcome modules
    /// that should disappear once the user has a session.
    let visibleToAnonymous: SubjectKind -> bool =
        function
        | AnonymousKind -> true
        | _ -> false

    /// Visible to the explicit list of `SubjectKind` values. Use for
    /// fine-grained gating — e.g. a team-admin module declares
    /// `visibleTo [ TeamMemberKind ]` so the sidebar entry disappears
    /// for users without an active team scope.
    let visibleTo (kinds: SubjectKind list) : SubjectKind -> bool =
        let admitSet = Set.ofList kinds
        fun kind -> admitSet.Contains kind

/// Typed module record — modules construct this, then erase via register.
type ClientModule<'Model, 'Msg> = {
    Definition: ModuleDefinition
    Init: ClientModuleContext -> 'Model * Cmd<'Msg>
    Update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>
    /// Single-page view function. `Some` for single-page modules,
    /// `None` for multi-page modules (which use `PageViews` instead).
    /// At least one of `View` or `PageViews` must be set; `register`
    /// throws at construction if both are `None`.
    View: ('Model -> ('Msg -> unit) -> ReactElement * ReactElement) option
    /// Per-page views keyed by `PageConfig.Route`. `None` → legacy
    /// single-page; the shell uses `View`. `Some map` → multi-page;
    /// `Init` / `Update` still fire once — all pages share the `Model`.
    /// Construct via the `withPages` helper after `create`.
    PageViews: Map<string, 'Model -> ('Msg -> unit) -> PageContent> option
    NeedsData: ((DataManagementTypes.DataTypeId -> bool) -> bool) option
    DataTypes: DataTypeDisplay list
    /// Extract processed data from module state (aggregated by the shell into
    /// `Model.ProcessedData` and published through `ProcessedDataContext`).
    ProvidesProcessedData: ('Model -> ProcessedDataTypes.ProcessedFileEntry list) option
    /// Extract the structured narrative currently displayed for a given
    /// page. Receives the module's `Model` and the active page route;
    /// returns `Some doc` when the page renders a `NarrativeDocument`,
    /// `None` otherwise. Used by the AI side-panel to snapshot the page's
    /// narrative into `AIMessageRequest.ActivePageNarrative`.
    ProvidesNarrative: ('Model -> string option -> Narrative.NarrativeDocument option) option
    /// Optional team-editable config schema. When `Some`, the admin UI
    /// surfaces a form under this module's key and the shell hands the
    /// persisted values to `Init` via `ClientModuleContext.Config`.
    Config: ModuleConfigSchema option
    /// Feature flags this module reads via `FeatureFlags.flag` /
    /// `variant`. The shell aggregates these across all registered
    /// modules to form the client-side declared-key set; reads on a
    /// key outside the set log a `console.warn` (typo protection).
    /// Default `[]` — modules that don't read flags declare nothing.
    ///
    /// For server-side evaluator validation (shape checks on admin
    /// writes) and admin-UI rendering, the app's composition root must
    /// also pass these into `ServerConfig.FeatureFlags` — the server
    /// and client composition roots are separate, so the union happens
    /// at the app level (same pattern as `DataTypes` / `ModuleConfigs`).
    FeatureFlags: FeatureFlag list
    /// Build-config availability gate. `DebugOnly` modules are dropped
    /// by `SDK.Client.prepareModules` in Release builds. Default `Always`.
    Availability: ModuleAvailability
    /// Optional taxonomy group for sidebar grouping. See `ErasedModule.Group`.
    Group: string option
    /// Phase 567 — declared navigation area. See `ErasedModule.Area`;
    /// set via `ClientModule.withArea`.
    Area: ModuleArea
    /// Client-side query handlers. See `ErasedModule.ClientQueryHandlers`.
    /// Construct via `withQueryHandlers`.
    ClientQueryHandlers: ModuleQueryHandler list
    /// Decoder for server-published `Notification.ModuleAction` events.
    /// See `ErasedModule.ActionDecoder`. Construct via `withActionDecoder`.
    /// The typed form is `(actionKey, payloadJson) -> 'Msg option`; the
    /// erasure wraps it into `obj option` when `register` is called.
    ActionDecoder: (string * string -> 'Msg option) option
    /// Phase 66 Stream B.3 — per-module sidebar visibility predicate.
    /// See `ErasedModule.Visibility`. Construct via `withVisibility`,
    /// drawing from the named smart constructors in the `Visibility`
    /// module (`visibleToAll`, `visibleToAuthenticated`, `visibleTo`).
    /// Default `Visibility.visibleToAll` — modules predating B.3 are
    /// visible to every subject kind, preserving the historical
    /// sidebar shape.
    Visibility: SubjectKind -> bool
    /// Cross-module client event subscriptions, keyed by topic. See
    /// `ErasedModule.EventSubscriptions`. The typed form is
    /// `topic -> (payload -> 'Msg)`; `register` boxes each mapper's
    /// result into `obj`. Construct via `withEventSubscription`.
    /// Default `Map.empty`.
    EventSubscriptions: Map<string, string -> 'Msg>
}

// ─── Client configuration ─────────────────────────────────────────

/// Configuration for the built-in file manager's name, icon and group.
type DataManagerConfig = {
    /// Display name shown in the sidebar (default: "File Upload")
    Name: string
    /// Icon shown in the sidebar — typically `Icons.upload` or any
    /// other typed `ReactElement`. Default: `ToolUp.Platform.Icons.upload`.
    Icon: ReactElement
    /// Sidebar group the module appears under. `None` keeps the SDK
    /// default ("Data Management"); `Some g` overrides it (e.g. to
    /// merge file upload into a deployment-specific group).
    Group: string option
}

/// Controls which file/data manager module is shown in the platform.
type DataManagerMode =
    /// No data manager — modules provide their own data or none is needed.
    | NoDataManager
    /// Use the SDK's built-in file upload/management UI (default).
    | DefaultDataManager
    /// Use the SDK's built-in file manager with custom name and icon.
    | ConfiguredDataManager of DataManagerConfig
    /// Use the SDK's mapping-aware Data Manager: upload an arbitrary CSV,
    /// pick a registered (schema-bearing) target type, and map the
    /// schema's fields to the CSV's columns with smart auto-suggestion +
    /// per-field override. The confirmed map is persisted per scope,
    /// keyed by the CSV's column-structure, and reused on later uploads.
    /// Requires `ServerConfig.ColumnMapping = EnabledColumnMapping` to
    /// back the mapping store.
    | MappingDataManager
    /// The mapping-aware Data Manager with custom name / icon / group.
    | ConfiguredMappingDataManager of DataManagerConfig
    /// Use a custom data manager module provided by the developer.
    | ExternalDataManager of ErasedModule

/// Branding for the team-management module. Shown in the sidebar
/// when the deployment runs in `Team` mode.
type TeamManagerConfig = { Name: string; Icon: ReactElement }

/// Controls the team-management module. Auto-injected only when
/// `ClientConfig.Surfaces` carries a single-team `Team` surface
/// (`Switching = NoSwitcher`) — multi-team (`HeaderSwitcher`) and
/// non-team deployments never show the sidebar entry regardless of
/// this setting (there are no teams to manage in the former case,
/// and `TeamSwitcherUI` already covers the latter).
type TeamManagerMode =
    /// No team manager in the sidebar — useful for custom workflows
    /// or apps that expose team management elsewhere.
    | NoTeamManager
    /// SDK built-in team manager (default).
    | DefaultTeamManager
    /// SDK built-in with custom name/icon.
    | ConfiguredTeamManager of TeamManagerConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalTeamManager of ErasedModule

/// Controls the built-in real-time toast notification renderer that
/// subscribes to `/api/notifications` and pops transient messages for
/// every `SystemMessage` envelope. Auto-injected unless set to
/// `NoToastCentre` — other notification kinds (job completion, data
/// refresh, team activity) flow through the same subscription and
/// apps can add their own renderers alongside.
type ToastCentreMode =
    /// No toast renderer — apps that want their own UI opt out here
    /// and subscribe to `NotificationClient.subscribe` directly.
    | NoToastCentre
    /// SDK built-in toast renderer (default).
    | DefaultToastCentre
    /// App-supplied custom renderer. The element is rendered alongside
    /// the shell root; it typically subscribes to
    /// `NotificationClient.subscribe` internally. Replaces the default
    /// entirely — deployments that want both must compose their own.
    | CustomToastCentre of ReactElement

// ─── Auth UI configuration ────────────────────────────────────────

/// Configuration for the OIDC Authorization Code + PKCE flow, used
/// when `ClientConfig.AuthUI = OidcAuthUI _`. The OidcClient companion
/// reads this to orchestrate sign-in against an OIDC-compliant issuer.
type OidcUIConfig = {
    /// OIDC issuer URL (base). Used for metadata discovery at
    /// `{issuer}/.well-known/openid-configuration`.
    /// Example: "https://auth.example.com".
    Issuer: string
    /// OIDC client id registered at the issuer for this application.
    ClientId: string
    /// URL the issuer redirects to after successful sign-in. Must
    /// match one of the redirect URIs registered at the issuer.
    /// Typically "<app origin>/auth/callback".
    RedirectUri: string
    /// Scopes requested in the sign-in request.
    /// Default (in `OidcUIConfig.defaults`) is ["openid"; "profile"; "email"].
    Scopes: string list
    /// Post-sign-out redirect URL, passed as `post_logout_redirect_uri`
    /// to the issuer's end-session endpoint. Must match one of the
    /// post-logout redirect URIs registered at the issuer. When
    /// `None`, the companion falls back to the app origin.
    PostLogoutRedirectUri: string option
    /// Opt in to client-side `id_token` validation at the callback
    /// boundary (Phase 3b.A — signature + issuer + audience + expiry).
    /// `None` (the default) resolves to `false` — preserves byte-for-byte
    /// today's behaviour. `Some true` runs the full validation pipeline
    /// after the nonce check; on failure local state is cleared and the
    /// callback returns a typed `AuthError`. `Some false` is explicit
    /// opt-out (equivalent to `None`). The default flips to `true` in a
    /// coordinated minor bump once consumers have adopted.
    ValidateIdToken: bool option
}

module OidcUIConfig =
    let defaults issuer clientId redirectUri = {
        Issuer = issuer
        ClientId = clientId
        RedirectUri = redirectUri
        Scopes = [ "openid"; "profile"; "email" ]
        PostLogoutRedirectUri = None
        ValidateIdToken = None
    }

/// Configuration for the Clerk sign-in flow, used when
/// `ClientConfig.AuthUI = ProviderAuthUI ("clerk", box clerkUIConfig)`
/// (or the deprecated `ClerkAuthUI _` alias). The ClerkUI companion
/// reads this to configure the Clerk React provider; its
/// `ClerkRegister.authUI` smart constructor builds the neutral case
/// from this record without the consumer boxing by hand.
type ClerkUIConfig = {
    /// Clerk publishable key. Visible to the browser by design —
    /// Clerk's security model assumes this is shipped to the client.
    PublishableKey: string
}

/// App-supplied sign-in wrapper. `Wrap` receives the rendered shell
/// and returns the wrapped element — typically a bespoke provider
/// component that gates the shell behind its own sign-in screen.
type CustomAuthUI = { Wrap: ReactElement -> ReactElement }

/// Phase 443 — configuration for the WebAuthn / passkey sign-in flow,
/// used when `ClientConfig.AuthUI = PasskeyAuthUI _`. The PasskeyClient
/// companion reads this to orchestrate the `navigator.credentials`
/// ceremonies against the server-side passkey companion's endpoints.
/// The relying-party id + origin allow-list live server-side (the
/// authoritative ceremony config); the client only needs the endpoint
/// base and UX affordances.
type PasskeyUIConfig = {
    /// Base path for the server ceremony endpoints (default
    /// `"/api/passkey"`; the companion posts to `{ApiBase}/register/*`
    /// and `{ApiBase}/assert/*`).
    ApiBase: string
    /// Whether the sign-in screen offers a "Register a passkey"
    /// affordance. Registration is still gated server-side (invite /
    /// session / bootstrap); this only controls the UI.
    AllowRegistration: bool
}

module PasskeyUIConfig =
    /// Default config — `/api/passkey` endpoints, registration affordance
    /// shown.
    let defaults: PasskeyUIConfig = {
        ApiBase = "/api/passkey"
        AllowRegistration = true
    }

/// How the shell handles sign-in UI in authenticated modes. Ignored
/// in `Anonymous` mode (there is nothing to sign in to). The actual
/// sign-in provider code lives in companion packages whose exported
/// handler values are added to `ClientConfig.Handlers.AuthUIHandlers`
/// (tag-keyed — see `AuthUIProvider`).
type AuthUIMode =
    /// No SDK-provided sign-in UI. The shell runs as-is; the app is
    /// expected to obtain tokens by some other means and hand them to
    /// `UserSession.setAuthToken` directly. Default.
    | NoAuthUI
    /// SDK-provided OIDC Authorization Code + PKCE flow via the
    /// OidcClient companion. Requires importing
    /// `src/AuthProviders/OidcClient/OidcClient.Client.props`.
    /// (OIDC is a protocol, not a vendor — this case stays.)
    | OidcAuthUI of OidcUIConfig
    /// Phase 443 — SDK-provided WebAuthn / passkey sign-in flow via the
    /// PasskeyClient companion (`navigator.credentials`, zero npm deps).
    /// WebAuthn is a protocol, not a vendor — this case stays alongside
    /// `OidcAuthUI` rather than folding into `ProviderAuthUI`. Requires
    /// `ToolUp.AuthProviders.PasskeyRegister.handler` in
    /// `ClientConfig.Handlers.AuthUIHandlers`. Additive DU case (pre-1.0
    /// window) — consumers pattern-matching `AuthUIMode` exhaustively get
    /// a compile-time prompt; see `docs/migrations/443-passkey-companion.md`.
    | PasskeyAuthUI of PasskeyUIConfig
    /// Deprecated vendor-named alias for
    /// `ProviderAuthUI ("clerk", box clerkUIConfig)` — Phase 494. Kept
    /// compiling for source compat; removal is a later major-version
    /// act. See `docs/migrations/494-vendor-neutral-auth-ui.md`.
    | [<System.Obsolete("Vendor-named case — use ProviderAuthUI (\"clerk\", box clerkUIConfig) (or ToolUp.AuthProviders.ClerkRegister.authUI from the ClerkUI companion) instead. See docs/migrations/494-vendor-neutral-auth-ui.md. ClerkAuthUI will be removed in a future major version.")>] ClerkAuthUI of
        ClerkUIConfig
    /// App-supplied wrapper. Bypasses the companion indirection —
    /// useful for deployments that have a custom sign-in flow the
    /// SDK shouldn't know about.
    | CustomAuthUI of CustomAuthUI
    /// Vendor-neutral companion-backed sign-in flow (Phase 494). `tag`
    /// names the handler entry in
    /// `ClientConfig.Handlers.AuthUIHandlers` (the same key the
    /// `AuthUIProvider` registry dispatches on — e.g. `"clerk"`,
    /// `"oidc"`, or any third-party companion's tag); `config` is the
    /// provider-specific config payload, type-erased at this sanctioned
    /// boundary exactly as `AuthUIHandler` already receives it — the
    /// registered handler knows the concrete type and unboxes.
    /// Companions export typed smart constructors (e.g.
    /// `ClerkRegister.authUI`) so consumers never box by hand.
    | ProviderAuthUI of tag: string * config: obj

/// Phase 133 — where the client keeps the bearer JWT once acquired.
///
/// `ClientCookieAndLocalStorage` (default): the legacy behaviour. The
/// client writes the JWT to `localStorage` AND mirrors it into a
/// JS-readable `document.cookie` (which structurally cannot be
/// `HttpOnly` — only a server `Set-Cookie` can be). Both stores are
/// reachable by any injected script, so an XSS can exfiltrate a usable
/// `Authorization: Bearer` token. Acceptable only for dev / the
/// EventSource-handshake path where no server callback exists, OR for a
/// SPA-without-server-session that accepts the XSS exposure and ships a
/// strict CSP (Phase 9j / Phase 129 header baseline) as the mitigation.
///
/// `ServerSetHttpOnlyCookie`: the production-shape BFF path. On token
/// acquisition the client POSTs the JWT once to the server's
/// `POST /api/auth/session` endpoint, which validates it and reflects it
/// into an `HttpOnly; Secure; SameSite=Strict` cookie. The JWT never
/// enters `localStorage` or a JS-readable cookie; it lives only in
/// transient in-memory JS state (lost on reload, re-acquired via the
/// bridge), and the durable session credential is the HttpOnly cookie
/// the browser sends automatically for SSE + same-origin XHR. Requires
/// `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance` and an
/// `IAuthProvider` configured `TokenLocation = BearerOrCookie
/// "toolup-auth-token"`. Pairs with the `IAuthBridge` refresh model
/// (Clerk / MSAL / Auth0); the localStorage-based OidcClient PKCE
/// refresh timer is not compatible with this mode (see the Phase 133
/// migration doc).
type AuthTokenStorage =
    | ClientCookieAndLocalStorage
    | ServerSetHttpOnlyCookie

/// Branding for the team-configuration admin module. Shown in the
/// sidebar when the deployment runs in any non-Anonymous mode (Anonymous
/// has no persistent scope to configure).
type TeamConfigConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in configuration admin. Auto-injected in any
/// non-Anonymous mode unless `NoTeamConfig` is set. When the
/// deployment declares no module config schemas (`ServerConfig.ModuleConfigs = []`)
/// the module renders an empty list — harmless, but apps can opt out
/// explicitly to hide the sidebar entry entirely.
type TeamConfigMode =
    /// No configuration module in the sidebar.
    | NoTeamConfig
    /// SDK built-in configuration admin (default).
    | DefaultTeamConfig
    /// SDK built-in with custom name/icon.
    | ConfiguredTeamConfig of TeamConfigConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalTeamConfig of ErasedModule

/// Branding for the permissions admin module (Tidy-Up #3 closure of
/// Phase 4 + Phase 5 residual). Auto-injected in any non-Anonymous
/// mode unless `NoPermissionsAdmin`.
type PermissionsAdminConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in permissions admin module. Closes the Phase
/// 4 / Phase 5 long-standing RBAC admin-UX gap by surfacing the full
/// `TeamPermissions` document for the caller's active team — team
/// defaults map, per-member overrides, module summary. Read paths
/// are available to any team member; write paths gate Owner/Admin
/// server-side via `PermissionApi`. Auto-injected in any non-Anonymous
/// mode unless set to `NoPermissionsAdmin` — Anonymous deployments
/// have no role concept and `PermissionApi.GetTeamPermissions` returns
/// `Error` for unscoped callers anyway, so the module is omitted there
/// regardless of this setting.
type PermissionsAdminMode =
    /// No permissions admin module in the sidebar.
    | NoPermissionsAdmin
    /// SDK built-in permissions admin (default).
    | DefaultPermissionsAdmin
    /// SDK built-in with custom name/icon.
    | ConfiguredPermissionsAdmin of PermissionsAdminConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalPermissionsAdmin of ErasedModule

/// Branding for the Platform Admin module (Phase 4b). Auto-injected
/// in any non-Anonymous mode unless `NoPlatformAdmin`. Client-side
/// sidebar filter (added in commit 4f.2) hides the module's
/// "Platform Management" group from non-admin callers regardless of
/// this setting — the branding here only controls the module's
/// display when the user has the role.
type PlatformAdminConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in Platform Admin module (Phase 4b). The module
/// surfaces `PlatformAdminApi`'s role-management endpoints (assign /
/// revoke / list admins) plus a placeholder Settings tab for future
/// runtime-config knobs. Auto-injected in any non-Anonymous mode
/// unless set to `NoPlatformAdmin` — Anonymous deployments have no
/// role concept and bootstrapping admins requires the
/// `TOOLUP_INITIAL_PLATFORM_ADMIN` env var instead.
type PlatformAdminMode =
    /// No Platform Admin module in the sidebar. Useful for deployments
    /// that ship a custom admin surface or that manage admins entirely
    /// via direct blob manipulation / scripted tooling.
    | NoPlatformAdmin
    /// SDK built-in Platform Admin (default).
    | DefaultPlatformAdmin
    /// SDK built-in with custom name/icon.
    | ConfiguredPlatformAdmin of PlatformAdminConfig
    /// Deployment-provided custom module in place of the SDK default.
    /// The custom module must declare `withGroup "Platform Admin"` (or
    /// `withGroup "Platform Management"`) for the shell's sidebar gate
    /// (4f.2, `ClientConfig.isPlatformAdminSidebarGroup`) to hide it
    /// from non-admin callers.
    | ExternalPlatformAdmin of ErasedModule

/// Branding for the health-monitor admin module (Phase 9p). Auto-
/// injected in any non-Anonymous mode unless `NoHealthMonitor`.
type HealthMonitorConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in health monitor admin (Phase 9p). The module
/// surfaces live `IHealthCheck` results (Phase 9k) and the most
/// recent `IConfigValidator` preflight outcomes (Phase 9m) through a
/// production-safe Owner/Admin UI. Auto-injected in any non-Anonymous
/// mode unless set to `NoHealthMonitor` — Anonymous deployments have
/// no role concept to gate on, so the module is omitted there
/// regardless of this setting (surfacing deployment dependency state
/// to every visitor is a reconnaissance gift).
type HealthMonitorMode =
    /// No health monitor module in the sidebar.
    | NoHealthMonitor
    /// SDK built-in health monitor (default).
    | DefaultHealthMonitor
    /// SDK built-in with custom name/icon.
    | ConfiguredHealthMonitor of HealthMonitorConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalHealthMonitor of ErasedModule

/// Branding for the platform-users admin module (Phase 544). Unlike the
/// other Platform-Management built-ins this is **opt-in** — a deployment
/// enables it explicitly (GP 11/13).
type PlatformUsersConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in platform-users admin (Phase 544). The module
/// lists every principal the substrate has evidence for
/// (`IPlatformTenantApi.ListPrincipals`, Phase 543), flags team-less
/// ones, and drives the existing Phase 54-family offboard flow
/// (preview → confirm → summary) per row against the `user-<id>` scope.
/// **Default `NoPlatformUsers`** — off unless a deployment opts in, so an
/// existing app is byte-for-byte unchanged (GP 11/13). Platform-Admin
/// gated client-side by the "Platform Management" sidebar group; the
/// underlying `IPlatformTenantApi` is admin-gated server-side regardless.
/// Zero-cost on `NoTenantLifecycle` deployments (GP 13): the offboard
/// surface 404s and the panel's per-row actions degrade to the empty
/// state.
type PlatformUsersMode =
    /// No platform-users module in the sidebar (default — opt-in).
    | NoPlatformUsers
    /// SDK built-in platform-users admin.
    | DefaultPlatformUsers

/// Branding for the service-status-board admin module (Phase 9p.A).
/// Auto-injected in any non-Anonymous mode unless `NoServiceStatusBoard`.
type ServiceStatusBoardConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in service-status-board admin (Phase 9p.A). The
/// module aggregates every operator-facing observability surface
/// (Phase 9k HealthCheck, 9m Preflight, 9q ConfigDrift, 9p
/// HealthMonitor live state, 9v RateLimiter, 9b JobScheduler, 9o
/// SmokeTest) into a single composite snapshot — one pane of glass
/// replacing today's per-concern admin tabs. Auto-injected in any
/// non-Anonymous mode unless set to `NoServiceStatusBoard`. Pair the
/// underlying substrate modes server-side; each section auto-skips
/// when its matching `ServerConfig` mode is `No*`, so the board is
/// useful even on minimal deployments.
type ServiceStatusBoardMode =
    /// No service-status-board module in the sidebar.
    | NoServiceStatusBoard
    /// SDK built-in service-status-board (default).
    | DefaultServiceStatusBoard
    /// SDK built-in with custom name/icon.
    | ConfiguredServiceStatusBoard of ServiceStatusBoardConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalServiceStatusBoard of ErasedModule

/// Branding for the usage dashboard admin module (Phase 9d). Auto-
/// injected in any non-Anonymous mode unless `NoUsageDashboard`.
type UsageDashboardConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in usage dashboard admin (Phase 9d). The module
/// surfaces per-team usage records (AI tokens, storage bytes, etc.)
/// through the `IUsageQueryApi` Owner/Admin surface. Auto-injected in
/// any non-Anonymous mode unless set to `NoUsageDashboard` —
/// Anonymous deployments have no role concept and exposing usage to
/// every visitor is a reconnaissance gift (cost telemetry leaks tenant
/// size). Pair with `ServerConfig.UsageMetering = EnabledUsageMetering`
/// — the dashboard renders an empty table when metering is disabled
/// server-side, but it is harmless to leave the sidebar entry in
/// place for future enablement.
type UsageDashboardMode =
    /// No usage dashboard module in the sidebar.
    | NoUsageDashboard
    /// SDK built-in usage dashboard (default).
    | DefaultUsageDashboard
    /// SDK built-in with custom name/icon.
    | ConfiguredUsageDashboard of UsageDashboardConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalUsageDashboard of ErasedModule

/// Branding for the built-in Home / Overview landing module (Phase
/// 171).
type HomeModuleConfig = { Name: string; Icon: ReactElement }

/// Controls the optional built-in Home / Overview landing module
/// (Phase 171). When enabled, the module is injected at the very top
/// of the sidebar and — unless `ClientConfig.ActiveModule` names a
/// specific module — becomes the default landing surface (the place
/// to start, instead of the first registered module). It summarises
/// the deployment: the data-producing tools with their per-tool
/// record counts (scoped to the caller), the active AI provider/model,
/// and light deployment context, via the `IHomeOverviewApi` surface.
///
/// **Off by default (GP 13).** Unlike the admin built-ins (Health
/// Monitor / Usage Dashboard, which default to `Default*` in any
/// non-Anonymous mode), the Home module defaults to `NoHomeModule` so
/// an existing deployment that upgrades is byte-for-byte unchanged
/// until it opts in. The `IHomeOverviewApi` route is auto-mounted
/// server-side but is never called unless the module is enabled.
type HomeModuleMode =
    /// No Home module; the landing surface stays the first registered
    /// module (the prior behaviour). The default.
    | NoHomeModule
    /// SDK built-in Home / Overview landing module.
    | EnabledHomeModule
    /// SDK built-in with custom name/icon.
    | ConfiguredHomeModule of HomeModuleConfig
    /// Deployment-provided custom module in place of the SDK default
    /// (still injected at the head of the sidebar + used as the default
    /// landing surface).
    | ExternalHomeModule of ErasedModule

// ─── Module-contributed home widgets (Phase 217) ─────────────────
//
// A module can surface a custom widget (a chart, a recents list, a
// call-to-action) on the Home / Overview landing surface (Phase 171)
// without `Platform.Client` ever naming it (GP 9). It exports an
// `IHomeWidgetContributor` value; the consumer adds it to
// `ClientConfig.Handlers.HomeWidgetContributors`. The built-in `Home`
// module collects every contributor's widgets, sorts by `Weight`, and
// renders them below the built-in tool / Active-AI / deployment cards.
// Click-through reuses `NavigationRequest.request` (Phase 6g.C) — a
// widget body calls it directly, so no new navigation primitive.
//
// Default-off by absence (GP 13): no contributor ⇒ Home renders
// byte-for-byte as Phase 171.

/// Render context handed to a contributed widget's body. Carries the
/// server-composed, scope-correct data bag a widget may need
/// (`HomeOverview.WidgetData`, populated by the optional
/// `IHomeWidgetDataProvider` DI seam — Phase 217). Empty unless a
/// provider populated it; a widget that needs no server data ignores
/// it. Contributors namespace their keys (e.g. `"my-widget.total"`)
/// since the bag is shared across every widget.
type HomeWidgetContext = { Data: Map<string, string> }

/// One module-contributed Home widget. The body is a function of the
/// render context so a data-driven widget reads its scope-correct
/// values from `ctx.Data`; a static widget ignores the argument.
type HomeWidget = {
    /// Stable widget id — used as the React key and as the namespace
    /// prefix convention for `HomeWidgetContext.Data` keys. Unique
    /// across all contributors (duplicates are surfaced at
    /// `Client.run`).
    Id: string
    /// Human-readable widget heading.
    Title: string
    /// Leading icon rendered beside the title.
    Icon: ReactElement
    /// Sort key — widgets render in ascending `Weight` order; ties
    /// fall back to registration order (stable sort).
    Weight: int
    /// Widget content. Receives the scope-correct render context.
    Body: HomeWidgetContext -> ReactElement
}

/// Client-side erased-module seam (GP 9). A module exports a value of
/// this type declaring the widgets it contributes to Home; the SDK
/// never names the contributing module. Mirrors the value-handler
/// registration pattern used for auth / data-source UIs — consumers
/// add contributors to `ClientConfig.Handlers.HomeWidgetContributors`
/// at compose time (no module-load side effects).
type IHomeWidgetContributor =
    /// The widgets this contributor surfaces on Home. Called once at
    /// boot; the result is flattened across contributors, sorted by
    /// `Weight`, and cached.
    abstract Widgets: unit -> HomeWidget list

/// Branding for the data-ingestion admin module (Phase 10b). Auto-
/// injected in any non-Anonymous mode unless `NoDataIngestionAdmin`.
type DataIngestionAdminConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in data-ingestion admin (Phase 10b). The module
/// surfaces configured `IDataSource` instances, their credential
/// status (`NotConfigured` / `NeedsAuthorization` / `Connected` /
/// `NeedsReauthorization`), and routes Connect / Disconnect actions
/// through `IDataIngestionApi.BeginOAuth` / `Disconnect`. Per-Kind
/// credential forms are contributed by connector companions via
/// `DataSourceCredentialUIRegistry.register` at module load time.
///
/// Auto-injected in any non-Anonymous mode unless `NoDataIngestionAdmin`
/// — Anonymous deployments have no role concept and exposing data-
/// source credentials to every visitor is a reconnaissance gift.
/// Pair with `ServerConfig.DataIngestion = EnabledDataIngestion` —
/// the dashboard renders an empty list when ingestion is disabled
/// server-side, but it's harmless to leave the sidebar entry in
/// place for future enablement.
type DataIngestionAdminMode =
    /// No data-ingestion admin module in the sidebar.
    | NoDataIngestionAdmin
    /// SDK built-in data-ingestion admin (default).
    | DefaultDataIngestionAdmin
    /// SDK built-in with custom name/icon.
    | ConfiguredDataIngestionAdmin of DataIngestionAdminConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalDataIngestionAdmin of ErasedModule

/// Branding for the data-subject-request admin module (Phase 9h).
type DataSubjectRequestAdminConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in data-subject-request admin (Phase 9h —
/// GDPR Article 15 export + Article 17 erasure). Defaults to
/// `NoDataSubjectRequestAdmin` because the substrate is opt-in
/// server-side (`ServerConfig.DataSubjectRequests = Disabled` by
/// default): apps without GDPR / CCPA / DPDPA exposure pay nothing.
/// Apps that need DSR set `ServerConfig.DataSubjectRequests = Enabled
/// policy` AND this to `DefaultDataSubjectRequestAdmin` (or a branded
/// variant). Anonymous deployments have no persistent scope to attach
/// the request to, so the API surface short-circuits to an error there
/// — the module is omitted regardless of this setting.
type DataSubjectRequestAdminMode =
    /// No DSR admin module in the sidebar (default).
    | NoDataSubjectRequestAdmin
    /// SDK built-in DSR admin.
    | DefaultDataSubjectRequestAdmin
    /// SDK built-in with custom name/icon.
    | ConfiguredDataSubjectRequestAdmin of DataSubjectRequestAdminConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalDataSubjectRequestAdmin of ErasedModule

/// Branding for the webhook admin module.
type WebhookAdminConfig = { Name: string; Icon: ReactElement }

/// Controls the built-in webhook admin. Default flipped in Phase 1g
/// (lightweight composition profile): the admin UI is no longer auto-
/// injected. Apps that want webhooks set `Webhooks = EnabledWebhooks`
/// server-side AND `WebhookAdmin = DefaultWebhookAdmin` (or one of the
/// branded variants) on the client. Anonymous deployments have no
/// persistent scope to attach subscriptions to, so the API surface
/// short-circuits to an error there — the module is omitted regardless
/// of this setting.
type WebhookAdminMode =
    /// No webhook admin module in the sidebar (default).
    | NoWebhookAdmin
    /// SDK built-in webhook admin.
    | DefaultWebhookAdmin
    /// SDK built-in with custom name/icon.
    | ConfiguredWebhookAdmin of WebhookAdminConfig
    /// Deployment-provided custom module in place of the SDK default.
    | ExternalWebhookAdmin of ErasedModule

/// Phase 12c — payload delivered to `ClientConfig.OnError` when a module's
/// view tree throws. `ComponentStack` carries the React component-stack
/// captured by the boundary's `componentDidCatch` (empty string when React
/// did not provide one). Telemetry consumers can forward this to a server
/// activity sink, structured logger, etc.
type ModuleErrorReport = {
    ModuleId: string
    Error: exn
    ComponentStack: string
}

// ─── Phase 13a — Client-side composition seams ────────────────────
//
// `ClientHandlerRegistry` and `ClientRequestSeam` are the explicit
// data-on-`ClientConfig` shape that replaces the legacy module-load
// `register()` pattern. Companion packages (OidcClient, ClerkUI,
// KnowledgeBase, data-source credential UIs) export handler values;
// consumers add them to the matching field on `ClientConfig.Handlers`.
// The SDK validates the registry at `Client.run` (fail-loud naming
// the missing import) and reads `RequestSeam` thunks at request-send
// time so values populated post-init (async CSRF prefetch, auth-bridge
// JWT refresh) reach the wire. See `docs/platform/client-composition.md`.
//
// Handler types live here (not in the per-module files that consume
// them) so `ClientConfig` can reference them — the consuming modules
// (`AuthUIProvider`, `DataSourceCredentialUIRegistry`,
// `Toolup.NarrativeCommit`) compile after `SDK.ClientTypes.fs` and
// would otherwise force a circular dependency.

/// Sign-in UI handler. Wraps the rendered shell with a sign-in gate
/// keyed by an `AuthUIMode` tag ("oidc", "clerk"). The first argument
/// is the matching mode's config (type-erased — the handler knows the
/// concrete type and unboxes; sanctioned-erasure boundary).
type AuthUIHandler = obj -> ReactElement -> ReactElement

/// Render context handed to a per-`DataSource.Kind` credential form.
/// Companion packages (`src/DataSources/<Provider>/`) consume this
/// context and render a Kind-specific Feliz form.
type DataSourceCredentialUIContext = {
    /// The data source the form is editing. `None` when the user has
    /// just selected a Kind from the "Add data source" dropdown — the
    /// form is responsible for persisting a fresh `DataSourceConfig`
    /// via `IDataIngestionApi.SaveDataSource` before configuring
    /// credentials.
    DataSource: DataSourceConfig option
    /// Callback the form invokes after a successful Save / Connect /
    /// Disconnect so the parent module re-fetches its data. Idempotent
    /// — calling it twice in succession is harmless.
    Refresh: unit -> unit
}

/// Per-`DataSource.Kind` credential form handler.
type DataSourceCredentialHandler = DataSourceCredentialUIContext -> ReactElement

/// Result of a `Save to Knowledge Base` attempt. `Duplicate` asks the
/// UI to prompt the user to confirm overwrite; on confirmation the
/// renderer re-submits with `overwrite = true`.
type NarrativeCommitResult =
    | Committed of docId: string * fileName: string
    | Duplicate of existingFileName: string * existingGeneratedAt: System.DateTimeOffset
    | MissingProvenance
    | Failed of reason: string

/// Broker for "save this narrative to the Knowledge Base". `Submit`
/// takes the document plus an overwrite flag (initially `false`; the
/// UI re-submits with `true` after a confirmation dialog).
type NarrativeCommitHandler = {
    Submit: ToolUp.Platform.Narrative.NarrativeDocument -> bool -> Async<NarrativeCommitResult>
}

/// Read-per-call seam for the client-side request-attachment pipeline.
/// Each field is invoked at *send time*, not snapshot at config-build
/// time, so values populated post-`Client.run` (e.g. the async CSRF
/// prefetch, the auth-bridge JWT refresh) reach the wire correctly.
/// The SDK injects its own identity provider
/// (`UserSession.identityHeaderPairs`) ahead of consumer-supplied
/// providers in the effective chain, so deployments that wire nothing
/// here still get the `X-User-Id` / `Authorization: Bearer ...`
/// defaults.
type ClientRequestSeam = {
    /// Ordered consumer-supplied header providers. Each is called
    /// fresh on every eligible `/api/*` request; emitted pairs are
    /// attached without overwriting headers the caller already set.
    /// Duplicate header names across providers fail loud at
    /// `Client.run` naming the conflicting pair. Reserved prefix for
    /// SDK-internal names: `X-ToolUp-*`. Consumer-defined names
    /// outside that prefix get a one-time `console.warn` advisory.
    HeaderProviders: (unit -> (string * string)[]) list

    /// Explicit API origin for split-origin SPA/API deployments
    /// (e.g. CDN-hosted SPA pointing at a different host for the
    /// API). Returns `None` (default) = same-origin only; the guard
    /// warns once on cross-origin `/api/` requests that don't match.
    /// Returns `Some origin` for split-origin deployments.
    ApiOrigin: unit -> string option

    /// 0.4.1 — per-request correlation-id provider. Called fresh on
    /// every eligible `/api/*` request; the returned value is attached
    /// as the `x-correlation-id` header so server-side observability
    /// can stitch client → server traces (the Giraffe dispatcher reads
    /// `x-correlation-id` on entry per Phase 69b.D and stamps it back
    /// on the response).
    ///
    /// Default `None` — the SDK generates a fresh `Guid.NewGuid().ToString("N")`
    /// per request. Apps with an existing logical-trace id (an OIDC
    /// session correlation, a per-tab span id) override by setting
    /// `Some readMyTraceId`.
    CorrelationIdProvider: (unit -> string) option
}

module ClientRequestSeam =
    /// No consumer-supplied header providers; same-origin only;
    /// SDK-default correlation id (fresh GUID per request).
    let empty: ClientRequestSeam = {
        HeaderProviders = []
        ApiOrigin = fun () -> None
        CorrelationIdProvider = None
    }

/// Snapshot-at-run-time companion handler registry. Companions export
/// value handlers (`OidcClient.handler : string * AuthUIHandler`,
/// `KnowledgeBaseView.narrativeCommitHandler : NarrativeCommitHandler`,
/// etc.) that consumers add to the matching field at compose time.
/// No module-load side effects; missing handlers for a declared
/// `ClientConfig.AuthUI` mode or `DataSource.Kind` fail loud at
/// `Client.run` naming the missing import.
type ClientHandlerRegistry = {
    /// Sign-in UI handlers keyed by `AuthUIMode` tag. The SDK looks
    /// up the handler matching `ClientConfig.AuthUI` and wraps the
    /// shell. Default: empty (compatible with `AuthUI = NoAuthUI`).
    AuthUIHandlers: (string * AuthUIHandler) list

    /// Per-`DataSource.Kind` credential UI forms. The data-ingestion
    /// admin module looks the form up by Kind when rendering each
    /// row. Default: empty (sources without a registered handler
    /// surface a "credential UI not registered" placeholder).
    DataSourceCredentialHandlers: (string * DataSourceCredentialHandler) list

    /// Phase 217 — module-contributed Home widgets. Each contributor
    /// declares the widgets it surfaces on the built-in Home / Overview
    /// landing module; the SDK never names a contributing module (GP 9).
    /// Default: empty — no contributor ⇒ Home renders byte-for-byte as
    /// Phase 171 (GP 13).
    HomeWidgetContributors: IHomeWidgetContributor list

    /// "Save to Knowledge Base" broker. `Some` when the
    /// KnowledgeBase companion is wired in. `NarrativeRenderer`
    /// reads this to decide whether to show the Save-to-KB button.
    /// Default: `None` (the button hides itself).
    NarrativeCommitHandler: NarrativeCommitHandler option

    /// 0.5.7 — Sign-out broker. `Some` when an auth-UI companion that
    /// owns the sign-in flow also owns the sign-out flow (`OidcClient`,
    /// `ClerkUI`, future provider) — the consumer wires its
    /// `signOutHandler` value here at compose time and the shell renders
    /// a "Sign out" affordance in the page header that calls it.
    /// `None` (default) leaves the header without a sign-out button —
    /// suitable for `NoAuthUI` deployments and for consumers that ship
    /// their own header chrome via `ClientConfig.HeaderAction` /
    /// `ExtraChrome`.
    ///
    /// The thunk fires unmodified — no SDK-side confirmation modal — so
    /// any "Are you sure?" UX is the consumer's call. Wraps the
    /// companion-specific async into a unit-returning function so the
    /// shell doesn't need an Async dependency in its render path.
    SignOutHandler: (unit -> unit) option
}

module ClientHandlerRegistry =
    /// No companion handlers wired in. Compatible with apps that
    /// declare `ClientConfig.AuthUI = NoAuthUI`, don't use the
    /// data-ingestion admin, and don't ship a Knowledge Base.
    let empty: ClientHandlerRegistry = {
        AuthUIHandlers = []
        DataSourceCredentialHandlers = []
        HomeWidgetContributors = []
        NarrativeCommitHandler = None
        SignOutHandler = None
    }

/// Width constraint applied to the inputs (left) child of a
/// `PageContent.SplitPanel` render. Calculator-shaped modules with
/// intrinsic-width form controls settle naturally at ~400px, but
/// presentation-only inputs (descriptive text + toggles, no `<input>` /
/// `<select>` children) have no intrinsic width and would otherwise let
/// the inputs pane expand across the row, squeezing the results pane to
/// a narrow strip. The shell enforces the "narrow sidebar + wide
/// results" design intent in `Narrow` (default), and lets consumers opt
/// out via `Wide` (balanced split) or `Auto` (today's natural-content-
/// width behaviour, preserved byte-for-byte).
type InputsPaneWidth =
    /// Inputs pane wrapped in `w-96 shrink-0` (24rem ≈ 384px). Default —
    /// enforces the design intent regardless of consumer content.
    | Narrow
    /// Inputs pane wrapped in `w-[32rem] shrink-0` (32rem ≈ 512px) for
    /// consumers that want a wider inputs column without going all the
    /// way to a balanced split.
    | Wide
    /// No width class — preserves today's natural-content-width behaviour
    /// byte-for-byte. Explicit opt-out for consumers that prefer the
    /// pre-shell-constraint shape.
    | Auto

/// Branding configuration for the client application
/// Selects the indicator the shell renders in the content area during
/// its `Prefetching` lifecycle phase (`Client.InitPhase`) — and any
/// future SDK-owned loading surface. `SkeletonLoader` (default) is the
/// gray-pulse content skeleton, byte-for-byte unchanged from 0.5.16, so
/// existing deployments are unaffected until they opt in (GP 11).
/// `BrandMarkLoader` centres the animated ToolUp mark
/// (`ToolUp.Platform.Icons.dataLoading`, the spinning, colour-cycling chevron-and-dot); `SpinnerLoader`
/// centres the neutral, `currentColor`-tinted spinner
/// (`ToolUp.Platform.Icons.spinner`) for deployments that prefer not to
/// show the brand mark. `CustomLoader` supplies a bespoke element.
type LoadingIndicatorMode =
    | SkeletonLoader
    | BrandMarkLoader
    | SpinnerLoader
    | CustomLoader of (unit -> ReactElement)

/// Phase 567 — how the sidebar presents the SDK's admin built-ins.
type AdminSurfaceMode =
    /// The default — admin modules render as inline sidebar groups
    /// ("Platform Management" / "Team Management") alongside product
    /// modules, exactly as before Phase 567. Byte-identical to pre-567.
    | InlineGroups
    /// Split the sidebar into a Product area and an Administration area,
    /// rendering one at a time with a role-gated switcher. Admin modules
    /// (derived via `ClientConfig.effectiveArea`) move to the admin area.
    | SeparateArea

type ClientConfig = {
    AppName: string
    AppLogo: string
    /// Selects the shell's loading indicator (rendered in the content
    /// area during the `Prefetching` lifecycle phase). Default
    /// `SkeletonLoader` — the gray-pulse content skeleton, unchanged from
    /// 0.5.16. Switch to `BrandMarkLoader` for the animated ToolUp mark
    /// or `SpinnerLoader` for a neutral theme-tinted spinner.
    LoadingIndicator: LoadingIndicatorMode
    /// Name of the module to show on startup. Falls back to the first module if None or not found.
    ActiveModule: string option
    /// Controls the data manager. Default: SDK built-in file manager.
    DataManager: DataManagerMode
    /// Controls the team manager. Only active when `Mode = Team`.
    /// Default: SDK built-in.
    TeamManager: TeamManagerMode
    /// Controls the configuration admin. Active in every non-Anonymous
    /// mode; `NoTeamConfig` opts out explicitly. Default: SDK built-in.
    TeamConfig: TeamConfigMode
    /// Controls the webhook admin. Default: `NoWebhookAdmin` —
    /// pair with `ServerConfig.Webhooks = EnabledWebhooks` and set
    /// this to `DefaultWebhookAdmin` (or one of the branded variants)
    /// to surface the admin UI.
    WebhookAdmin: WebhookAdminMode
    /// Controls the Platform Admin module (Phase 4b). Active in every
    /// non-Anonymous mode unless set to `NoPlatformAdmin`. The shell
    /// sidebar filter hides the module's "Platform Management" group
    /// from callers without `PlatformRole.PlatformAdmin` regardless of this setting
    /// — the mode controls module *registration*, the role gates
    /// *visibility*. Default: SDK built-in. Server-side
    /// `PlatformAdminApi` is auto-injected by `compose` independently;
    /// a deployment shipping only a custom client can still query the
    /// API directly.
    PlatformAdmin: PlatformAdminMode
    /// Controls the permissions admin module (Tidy-Up #3 closure of
    /// Phase 4 + Phase 5 residual). Active in every non-Anonymous
    /// mode; `NoPermissionsAdmin` opts out explicitly. Default: SDK
    /// built-in. Anonymous mode skips the module by construction (no
    /// role concept; server-side `PermissionApi` returns `Error` for
    /// unscoped callers). The read surface is available to any team
    /// member; write paths gate Owner/Admin server-side via
    /// `PermissionApi`.
    PermissionsAdmin: PermissionsAdminMode
    /// Controls the health monitor admin (Phase 9p). Active in every
    /// non-Anonymous mode; `NoHealthMonitor` opts out explicitly.
    /// Default: SDK built-in. Server-side surface auto-mounts whenever
    /// the SDK composes — a deployment that ships only a custom client
    /// can still query the API directly.
    HealthMonitor: HealthMonitorMode
    /// Controls the platform-users admin (Phase 544). **Opt-in** — default
    /// `NoPlatformUsers`, so an existing deployment's sidebar is unchanged
    /// until it sets `DefaultPlatformUsers` (GP 11/13). When enabled, the
    /// module lists every principal (Phase 543 `ListPrincipals`), flags
    /// team-less ones, and drives the Phase 54-family offboard flow per
    /// row. Platform-Admin gated by the "Platform Management" sidebar
    /// group; pair with `ServerConfig.TenantLifecycle = EnabledTenantLifecycle`
    /// server-side for the offboard actions (the list still renders under
    /// `NoTenantLifecycle`, the per-row offboard degrades gracefully).
    PlatformUsers: PlatformUsersMode
    /// Controls the service-status-board admin (Phase 9p.A). Active in
    /// every non-Anonymous mode; `NoServiceStatusBoard` opts out
    /// explicitly. Default: SDK built-in. Server-side surface auto-
    /// mounts whenever the SDK composes. The board aggregates every
    /// operator-facing observability surface into one composite
    /// snapshot; each section auto-skips when its matching
    /// `ServerConfig` mode is `No*` (e.g. `JobScheduler = NoJobScheduler`
    /// hides the JobQueue section), so the board is useful even on
    /// minimal deployments.
    ServiceStatusBoard: ServiceStatusBoardMode
    /// Controls the usage dashboard admin (Phase 9d). Active in every
    /// non-Anonymous mode; `NoUsageDashboard` opts out explicitly.
    /// Default: SDK built-in. Pair with `ServerConfig.UsageMetering =
    /// EnabledUsageMetering` server-side — the dashboard renders empty
    /// otherwise.
    UsageDashboard: UsageDashboardMode
    /// Controls the optional Home / Overview landing module (Phase 171).
    /// **Default: `NoHomeModule`** (off — unlike the admin built-ins) so
    /// existing deployments are unchanged until they opt in (GP 13).
    /// When `EnabledHomeModule`, the module is injected at the head of
    /// the sidebar and becomes the default landing surface unless
    /// `ActiveModule` names a specific module.
    HomeModule: HomeModuleMode
    /// Phase 567 — how the admin built-ins are presented in the sidebar.
    /// **Default `InlineGroups`** (byte-identical to pre-567: admin modules
    /// render as inline "Platform/Team Management" groups). `SeparateArea`
    /// splits the sidebar into Product and Administration areas with a
    /// role-gated switcher.
    AdminSurface: AdminSurfaceMode
    /// Phase 217 — opt in to the built-in "Pinned / Recent" widget on the
    /// Home surface (a small per-user store of recently-visited + pinned
    /// tools, persisted through the per-user config store). **Default:
    /// `false`** — off, so a Home deployment that doesn't opt in renders
    /// byte-for-byte as Phase 171 (GP 13). Only meaningful when
    /// `HomeModule` is enabled; ignored otherwise.
    HomeRecents: bool
    /// Opt-in no-active-team gate. When `Some moduleId` AND the deployment
    /// declares a `Team` surface AND the caller has no active team
    /// (`ActiveTeamId = None`, i.e. the resolved `SubjectKind` is
    /// `UserKind` in the post-sign-in / pre-team-pick window), the shell
    /// hides every sidebar module EXCEPT the named landing module — and,
    /// for `PlatformRole.PlatformAdmin` callers, the admin / management
    /// sidebar groups (`ClientConfig.isAdminSidebarGroup`) so an admin can
    /// still reach the team-assignment tools. The named module is the
    /// deployment's "you have no team yet" surface; it should declare
    /// `Visibility.visibleTo [ UserKind ]` so it disappears once an active
    /// team upgrades the subject to `TeamMemberKind`.
    ///
    /// Why a deployment-wide gate and not per-module `Visibility`:
    /// SDK-injected built-ins (the Data Manager, Team Manager, Settings)
    /// ship `Visibility.visibleToAuthenticated`, which admits `UserKind`,
    /// so a consumer cannot hide them from a no-team caller by editing its
    /// own modules. This restores a refined form of the Phase 55
    /// team-mode-no-active-team blanket-hide, opt-in and admin-aware.
    ///
    /// **Default `None`** — no gate; behaviour is byte-identical for every
    /// deployment that doesn't opt in (GP 13). Inert on non-team surfaces
    /// (the `hasTeamScope` guard) even when set.
    ///
    /// GP 12 — this is UI shape only; the server-side `[<TenantScoped>]`
    /// classifier + `SurfaceEnforcementMiddleware` remain the authoritative
    /// gate (a no-team caller's tenant-scoped API calls 401 regardless).
    NoActiveTeamLandingModuleId: string option
    /// Opt-in parameterized no-active-team landing surface — the lightweight
    /// path that pairs with the same gate as `NoActiveTeamLandingModuleId`.
    /// When `Some cfg` AND the deployment declares a `Team` surface AND no
    /// explicit `NoActiveTeamLandingModuleId` is set, the SDK registers a
    /// built-in landing module (stable id `NoActiveTeamLanding.moduleId`)
    /// from `cfg`'s copy and wires the no-team gate to it — the consumer
    /// supplies a few strings instead of hand-rolling an Elmish module.
    ///
    /// **Precedence:** `NoActiveTeamLandingModuleId` wins. If a consumer
    /// sets both, the explicit custom module is used and this built-in is
    /// NOT injected (the consumer fully owns the landing). Resolved by
    /// `ClientConfig.effectiveNoActiveTeamLandingId`.
    ///
    /// **Default `None`** — no built-in landing (GP 13). Inert on non-team
    /// surfaces even when set.
    NoActiveTeamLanding: NoActiveTeamLandingConfig option
    /// Controls the data-ingestion admin (Phase 10b). Active in every
    /// non-Anonymous mode; `NoDataIngestionAdmin` opts out explicitly.
    /// Default: SDK built-in. Pair with `ServerConfig.DataIngestion =
    /// EnabledDataIngestion` server-side — the admin renders an empty
    /// list otherwise. Per-Kind credential forms are contributed by
    /// connector companion packages.
    DataIngestionAdmin: DataIngestionAdminMode
    /// Controls the data-subject-request admin (Phase 9h — GDPR Article
    /// 15 export + Article 17 erasure). Default `NoDataSubjectRequestAdmin`
    /// — apps without GDPR / CCPA / DPDPA exposure pay nothing. Pair with
    /// `ServerConfig.DataSubjectRequests = Enabled policy` server-side
    /// and flip this to `DefaultDataSubjectRequestAdmin` (or a branded
    /// variant) to surface the admin UI. Owner / Admin gating is enforced
    /// upstream by the API handler; the sidebar entry is rendered for
    /// every authenticated caller in non-Anonymous mode and the API
    /// itself refuses non-admin writes.
    DataSubjectRequestAdmin: DataSubjectRequestAdminMode
    /// Controls the real-time toast renderer. Default: SDK built-in.
    ToastCentre: ToastCentreMode
    /// Sign-in / sign-out UI. Companion-delegated:
    /// `ProviderAuthUI (tag, config)` needs the matching companion's
    /// handler in `Handlers.AuthUIHandlers` (e.g. the ClerkUI
    /// companion's `"clerk"` handler); `OidcAuthUI` needs OidcClient.
    /// Default `NoAuthUI` — no SDK-provided sign-in flow, the app
    /// takes responsibility for obtaining tokens.
    AuthUI: AuthUIMode
    /// Phase 133 — where the client keeps the bearer JWT.
    /// `ClientCookieAndLocalStorage` (default) preserves the legacy
    /// `localStorage` + JS-readable `document.cookie` writes;
    /// `ServerSetHttpOnlyCookie` moves the JWT out of JS-readable storage
    /// into a server-set `HttpOnly; Secure; SameSite=Strict` cookie via
    /// `POST /api/auth/session`. Pairs with
    /// `ServerConfig.AuthCookieIssuance = EnabledAuthCookieIssuance`.
    AuthTokenStorage: AuthTokenStorage
    /// Declared subject shapes this deployment supports. Mirrors
    /// `ServerConfig.Surfaces` (the server-side non-empty list of
    /// `SurfaceProfile`). Single-shape deployments declare one entry
    /// (e.g. `Surfaces.individual`); mixed-mode deployments declare
    /// two or more (e.g. `Surfaces.anonymousAndIndividual`). The
    /// client derives the active `SubjectKind` per render from the
    /// list + auth state + active team scope (see
    /// `ClientConfig.resolveSubjectKind`).
    Surfaces: SurfaceProfile list
    /// AG Grid module configuration for the AgGridProvider (Variant B).
    /// When set, the SDK wraps the app in AgGridProvider with these modules.
    /// Default: Community modules (no license key required).
    GridModules: ToolUp.Platform.AgGrid.AgGridModuleConfig
    /// Optional case-insensitive substring filter over
    /// `ErasedModule.Definition.Name` — modules whose name doesn't
    /// contain the filter as a substring (whitespace ignored) are
    /// dropped before the shell builds its sidebar. Populated from the
    /// Vite-defined `__TOOLUP_MODULE__` constant in the reference app;
    /// `None` / empty keeps every module registered. Mirrors
    /// `ServerConfig.ModuleFilter` so single-module dev runs behave
    /// consistently across the stack.
    ModuleFilter: string option
    /// Phase 6g.D: thunks the SDK shell renders at the top level
    /// alongside `ToastCentre`. Companion packages expose components
    /// that need to mount globally without the deployment touching
    /// the React tree directly — e.g. a floating banner / status
    /// indicator owned by the companion. Each thunk is invoked once
    /// per shell render, so the components' hooks register correctly.
    /// Default `[]` — apps that don't use overlays pay nothing.
    ///
    /// Same shape as `ToastCentreMode` but generalised: any
    /// companion can contribute, the deployment opts in by listing
    /// the thunks here. ToastCentre stays a separate slot because
    /// its three modes (`No` / `Default` / `Custom`) are richer
    /// than a free list.
    GlobalOverlays: (unit -> ReactElement) list
    /// Phase 12c — telemetry hook fired when a module's view tree throws.
    /// The SDK's per-module `Components.ModuleBoundary` catches the exception,
    /// shows a localised "this module crashed — reload?" panel for the
    /// affected sidebar entry, and (if set) calls this with the report.
    /// Default `None` — the boundary still works and falls back to a single
    /// `console.error` for dev diagnostics. Deployments that wire telemetry
    /// (e.g. forwarding to a server-side activity sink) own logging entirely
    /// when this is `Some`; the SDK does not also `console.error` to avoid
    /// double-logging.
    OnError: (ModuleErrorReport -> unit) option

    /// Phase 6k Workstream A. Optional client-side bridge to a
    /// deployment-chosen identity SDK (Clerk, Microsoft Entra via
    /// MSAL, Auth0, WorkOS). When `Some`, the SDK calls
    /// `UserSession.installBridge` during startup; the bridge then
    /// drives JWT refresh into both the `Authorization: Bearer ...`
    /// header (POST path) and the `toolup-auth-token` cookie (SSE
    /// handshake path). Default `None` — deployments without server-
    /// validated JWTs use the existing X-User-Id / ?userId= flow.
    AuthBridge: IAuthBridge option
    /// Dev-tooling: when `true`, the SDK logs every Elmish model-update
    /// transition to the browser console via `Logger.forCategory
    /// "client.elmish.trace"`. Default `false` — production deployments
    /// leave it off; App composition roots flip it on for development.
    /// Replaces the previous compile-time `#if DEBUG`-only behaviour
    /// with explicit operator opt-in.
    ///
    /// 0.4.1 — implemented via a tiny `update` interceptor + the
    /// structured `Program.withErrorReporter`, replacing the now-deprecated
    /// `Program.withConsoleTrace` shim. Trace records carry the same
    /// `(initial state, msg, updated state, sub-ids)` shape as before.
    EnableElmishConsoleTrace: bool
    /// 0.4.1 — structured Elmish error reporter. When `Some`, every
    /// Elmish runtime exception (Init / Update / View / Subscription /
    /// Termination phases) is delivered as an `ErrorContext` record
    /// carrying the phase, optional module id, optional correlation id,
    /// human-readable message, and raw exception. Use to forward to a
    /// server-side activity sink or a structured-log forwarder.
    ///
    /// Default `None` — the SDK falls back to logging via
    /// `Logger.forCategory "client.elmish"`. Consumers wiring this MUST
    /// not also subscribe to `withErrorHandler` upstream-shape callbacks
    /// (the SDK installs `withErrorReporter` as the structured path;
    /// upstream-shape `onError` still routes through the compat shim).
    OnElmishError: (ErrorContext -> unit) option
    /// Dev-tooling: when `true`, modules registered with
    /// `ModuleAvailability = DebugOnly` are surfaced in the sidebar.
    /// Default `false` — DebugOnly modules are filtered out (the JS still
    /// ships in the bundle; the goal is hiding under-development modules,
    /// not reducing bundle size). App composition roots flip it on for
    /// development. Replaces the previous compile-time `#if DEBUG`-only
    /// behaviour with explicit operator opt-in.
    ShowDebugOnlyModules: bool
    /// Phase 4b dev convenience — when `Some userId`, the SDK uses this
    /// value as the `toolup-user-id` localStorage seed when no value is
    /// already stored (replacing the auto-generated GUID). Pairs with
    /// `ServerConfig.AutoBootstrapDevAdmin` so dev composition roots can
    /// run with a stable, predictable user-id end-to-end (`X-User-Id`
    /// header → server-side `AccessContext.UserId` → bootstrap target).
    /// Production deployments MUST leave this `None`; deterministic
    /// user-ids defeat per-user isolation in any multi-user setting.
    /// Existing localStorage values are preserved — only the first-visit
    /// generation path is overridden.
    DevDefaultUserId: string option
    /// First-chance entry-point dispatchers. Each is invoked once during
    /// `Client.run` *before* the full authenticated shell is bootstrapped;
    /// the first dispatcher whose `Tries (ClientConfig) → bool` returns
    /// `true` short-circuits the shell (the dispatcher is responsible for
    /// rendering its own Elmish program against `"elmish-app"`).
    ///
    /// Use for surfaces that need to render an anonymous, modules-less
    /// page on a specific URL pattern — e.g. `ToolUp.Forms.PublicEmbed`
    /// on `/r/{token}`. Each dispatcher reads `window.location` itself
    /// so it can shape its own URL-matching predicate. Default `[]` —
    /// the full shell bootstraps unconditionally.
    ///
    /// Dispatchers run in list order. Returning `false` lets the next
    /// dispatcher try; if every dispatcher declines, the shell
    /// bootstraps as normal.
    PublicEntryDispatchers: (ClientConfig -> bool) list

    /// Phase 13a — explicit companion handler registry. Companion
    /// packages export `Handler` values (e.g.
    /// `OidcClient.handler : string * AuthUIHandler`,
    /// `KnowledgeBaseView.narrativeCommitHandler : NarrativeCommitHandler`)
    /// which the consumer adds to the matching field here. Default
    /// `ClientHandlerRegistry.empty` is compatible with apps that
    /// declare `AuthUI = NoAuthUI`, don't use the data-ingestion
    /// admin, and don't ship a Knowledge Base. The SDK validates the
    /// registry at `Client.run` and fails loud when a declared
    /// `AuthUI` mode or `DataSource.Kind` references a handler the
    /// consumer didn't add (naming the missing companion + import).
    Handlers: ClientHandlerRegistry

    /// Phase 13a — read-per-call request-attachment seam. Consumer-
    /// supplied per-request header providers (tenant id, audit
    /// context, feature-flag override) plus optional cross-origin API
    /// root. The SDK injects its own identity headers + CSRF token
    /// alongside; this slot is for *extra* providers beyond those.
    /// Default `ClientRequestSeam.empty` — same-origin only, no extra
    /// headers.
    RequestSeam: ClientRequestSeam

    /// Phase 57 — static-prerender routes. Each entry declares a
    /// route the build-time FAKE `Prerender` target renders to an
    /// indexable `dist/{slug}.html`. The SPA hydrates on top via
    /// React's `hydrateRoot`. Default `[]` — opt-in per GP 13;
    /// stock deployments stay pure-SPA byte-for-byte. Required for
    /// ads-monetised public-utility deployments whose SEO depends on
    /// an indexable first paint.
    PrerenderRoutes: PrerenderRoute list

    /// Phase 59 — consent-provider selection. Default
    /// `NoConsentProvider` (the no-op provider — `Necessary` always
    /// granted, everything else `NotYetDecided`). Set to
    /// `FundingChoicesConsent adClientId` to wire Google Funding
    /// Choices (CMP-blessed by AdSense). `CustomConsentProvider`
    /// reserves the seam for sub-companion CMP packages
    /// (Quantcast / Cookiebot / OneTrust).
    ConsentProvider: ConsentProviderMode

    /// Phase 60 — AdSense AdPanel mode. Default `NoAdPanel`
    /// strips every `<AdSlot>` render path — slots produce empty
    /// fragments without loading AdSense JS. `EnabledAdPanel
    /// config` activates the substrate; slots load AdSense
    /// (gated through the configured `ConsentProvider`) and
    /// emit impressions when `ServerConfig.AdAnalytics` is on.
    AdPanel: AdPanelMode

    /// Phase 62 — premium-claim model. Default `AnonymousFirst`
    /// — anonymous-by-default with operator-granted premium
    /// recognised via the active `IAuthProvider`'s
    /// user-metadata. Modules consume the resulting
    /// `PremiumStatus` via the `usePremium` hook (companion-
    /// ships with the AI / RAG client packs when wired).
    PremiumModel: PremiumModel

    /// Phase 61 — PlatformAdmin profile. `StandardPlatformAdminProfile`
    /// (default) renders today's widget set (HealthMonitor /
    /// TeamAdmin / etc.). `PublicUtilityPlatformAdminProfile`
    /// adds the public-utility widgets (TrafficDashboard /
    /// RateLimitEventLog / AdUnitConfig / PremiumUserList) when
    /// their substrate dependencies are wired.
    PlatformAdminProfile: PlatformAdminProfile

    /// Width constraint applied to the inputs (left) child of a
    /// `PageContent.SplitPanel` render. Default `Narrow` enforces the
    /// "narrow sidebar + wide results" design intent shell-side so
    /// presentation-only modules (descriptive text + Terms toggle, no
    /// form fields) don't expand across the row and squeeze the
    /// results pane to a narrow strip. Consumers that genuinely want a
    /// balanced split set `Wide`; consumers wanting today's natural-
    /// content-width behaviour byte-for-byte set `Auto`.
    InputsPaneWidth: InputsPaneWidth

    /// Phase 583 — the module set this deployment expects to compose, as
    /// the consumer declares it. The client-tier mirror of
    /// `ServerConfig.ExpectedModules`: declare the SAME list on both and
    /// each root is measured against it, so the two roots are shown to
    /// compose the same modules without either tier referencing the
    /// other. Entries are the cross-tier identity token — the client's
    /// `ModuleDefinition.Id`, which is also the server's
    /// `ServerModule.Name` (the identity law on
    /// `ModuleIdentity.componentIdOf`), not the display `Name`.
    ///
    /// `None` (the default) leaves `ModuleParityValidator` dormant: boot
    /// evaluates one `match` and continues, byte-for-byte the pre-583
    /// path (GP 11 + GP 13). `Some ids` makes a mismatch a loud boot
    /// failure naming both directions, in the same shape as the server
    /// side's `client-server-module-parity` preflight defect.
    ExpectedModules: string list option
}

module ClientConfig =
    /// Phase 11.C.5 Tier 3 — preferred constructor with explicit
    /// `ClientHandlerRegistry`. Promotes `Handlers` from the silently-
    /// empty default on `ClientConfig.defaults` to a required
    /// positional parameter, so a deployment that forgets to wire
    /// (e.g.) `OidcClient.handler` fails to compile rather than
    /// failing with a runtime "no handler registered" on the first
    /// sign-in attempt. Consumers with no companion handlers pass
    /// `ClientHandlerRegistry.empty` explicitly — the no-op stays
    /// available, but the omission is now visible at the call site.
    ///
    /// All other fields are populated from the existing
    /// `ClientConfig.defaults` shape; callers customise via the
    /// standard `{ ClientConfig.create handlers with X = … }` record-
    /// update syntax.
    let create (handlers: ClientHandlerRegistry) : ClientConfig = {
        AppName = "My App"
        AppLogo = "favicon.png"
        LoadingIndicator = SkeletonLoader
        ActiveModule = None
        DataManager = DefaultDataManager
        TeamManager = DefaultTeamManager
        TeamConfig = DefaultTeamConfig
        WebhookAdmin = NoWebhookAdmin
        PlatformAdmin = DefaultPlatformAdmin
        PermissionsAdmin = DefaultPermissionsAdmin
        HealthMonitor = DefaultHealthMonitor
        // Phase 544 — opt-in (GP 11/13); existing deployments keep no
        // platform-users module until they set DefaultPlatformUsers.
        PlatformUsers = NoPlatformUsers
        ServiceStatusBoard = DefaultServiceStatusBoard
        UsageDashboard = DefaultUsageDashboard
        // Phase 171 — off by default (GP 13); existing deployments
        // keep their first-registered module as the landing surface.
        HomeModule = NoHomeModule
        // Phase 567 — inline admin groups by default (byte-identical to
        // pre-567); SeparateArea is the opt-in two-area sidebar.
        AdminSurface = InlineGroups
        // Phase 217 — recents/pinning off by default (GP 13).
        HomeRecents = false
        // Opt-in; off by default so the no-team gate never changes an
        // existing deployment's sidebar (GP 13).
        NoActiveTeamLandingModuleId = None
        // Opt-in parameterized landing; off by default (GP 13).
        NoActiveTeamLanding = None
        DataIngestionAdmin = DefaultDataIngestionAdmin
        DataSubjectRequestAdmin = NoDataSubjectRequestAdmin
        ToastCentre = DefaultToastCentre
        AuthUI = NoAuthUI
        AuthTokenStorage = ClientCookieAndLocalStorage
        Surfaces = Surfaces.anonymous
        GridModules = ToolUp.Platform.AgGrid.AgGridModuleConfig.community
        ModuleFilter = None
        GlobalOverlays = []
        OnError = None
        AuthBridge = None
        EnableElmishConsoleTrace = false
        OnElmishError = None
        ShowDebugOnlyModules = false
        DevDefaultUserId = None
        PublicEntryDispatchers = []
        Handlers = handlers
        RequestSeam = ClientRequestSeam.empty
        PrerenderRoutes = []
        ConsentProvider = NoConsentProvider
        AdPanel = NoAdPanel
        PremiumModel = AnonymousFirst
        PlatformAdminProfile = StandardPlatformAdminProfile
        InputsPaneWidth = Narrow
        // Phase 583 — parity check dormant until the consumer declares
        // an expected-module list on BOTH roots (GP 13).
        ExpectedModules = None
    }

    /// Back-compat: `ClientConfig` with every field at the SDK default
    /// AND `Handlers = ClientHandlerRegistry.empty`. Equivalent to
    /// `create ClientHandlerRegistry.empty`. Deployments that wire
    /// companion handlers (OidcClient.handler, NarrativeCommit,
    /// IngestionStatus, …) SHOULD prefer `create handlers` so the
    /// omission of a required handler is caught at compile time
    /// (Phase 11.C.5 Tier 3).
    let defaults: ClientConfig = create ClientHandlerRegistry.empty

    /// Phase 66 Stream B.8 — true iff any declared surface admits
    /// unauthenticated requests. Mirrors `DeploymentConfig.hasAnonymous`
    /// on the server side.
    let hasAnonymous (config: ClientConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> true
            | _ -> false)

    /// Phase 66 Stream B.8 — true iff any declared surface needs
    /// authenticated credentials (any non-Anonymous surface).
    let requiresAnyAuth (config: ClientConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Anonymous _ -> false
            | _ -> true)

    /// Phase 66 Stream B.8 — true iff any declared surface carries a
    /// team scope (single-team or multi-team UX).
    let hasTeamScope (config: ClientConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team _ -> true
            | _ -> false)

    /// Platform-scoped admin sidebar groups — visible ONLY to callers
    /// holding `PlatformRole.PlatformAdmin`. Mirrors the SDK built-ins'
    /// `withGroup` labels (PlatformAdminUI / HealthMonitorUI /
    /// ServiceStatusBoardUI / DataSubjectRequestAdminUI /
    /// TenantLifecycleAdminUI / PlatformUsersUI → "Platform Management")
    /// plus the consumer-convention "Platform Admin" group that the
    /// Phase 4b role-gate has recognised since commit 4f.2.
    let private platformAdminSidebarGroups: Set<string> =
        Set.ofList [ "Platform Admin"; "Platform Management" ]

    /// Team-scoped admin sidebar group(s) — the management surfaces a
    /// team Owner/Admin legitimately uses (TeamManagerUI /
    /// PermissionsAdminUI / DataIngestionUI / TeamConfigUI /
    /// WebhookAdminUI / UsageDashboard → "Team Management"). NOT gated
    /// on `PlatformRole` — hiding these from non-platform-admins would
    /// strip team Owners of their own management tools.
    let private teamAdminSidebarGroups: Set<string> = Set.ofList [ "Team Management" ]

    /// Union of the platform-scoped and team-scoped admin groups — the
    /// set kept visible for a `PlatformRole.PlatformAdmin` caller under
    /// the `NoActiveTeamLandingModuleId` no-team gate, so a team-less
    /// admin still reaches the team-assignment tools (Team Manager lives
    /// in "Team Management"). A module with no group is never in the
    /// admin set.
    let private adminSidebarGroups: Set<string> =
        Set.union platformAdminSidebarGroups teamAdminSidebarGroups

    /// True iff the (optional) sidebar group is platform-scoped — i.e.
    /// its entries should render only for `PlatformRole.PlatformAdmin`
    /// callers. This is the predicate behind the shell's sidebar
    /// role filter (Phase 4b, commit 4f.2). Team-scoped groups
    /// ("Team Management") return `false`: their visibility is a
    /// team-role concern, not a platform-role one.
    let isPlatformAdminSidebarGroup (group: string option) : bool =
        match group with
        | Some g -> platformAdminSidebarGroups.Contains g
        | None -> false

    /// True iff the (optional) sidebar group is an admin / management
    /// group (platform- OR team-scoped) kept visible to a team-less
    /// platform admin under the no-team gate. See `adminSidebarGroups`
    /// and `NoActiveTeamLandingModuleId`.
    let isAdminSidebarGroup (group: string option) : bool =
        match group with
        | Some g -> adminSidebarGroups.Contains g
        | None -> false

    /// Phase 567 — a module's *effective* navigation area. A module is in
    /// the `Administration` area if it declares it (`ClientModule.withArea
    /// Administration`) OR sits in an admin sidebar group (so SDK admin
    /// built-ins land in the admin area with no registration change, GP 9);
    /// otherwise `Product`. Consulted only under `AdminSurface = SeparateArea`.
    let effectiveArea (area: ModuleArea) (group: string option) : ModuleArea =
        match area with
        | Administration -> Administration
        | Product ->
            if isAdminSidebarGroup group then
                Administration
            else
                Product

    /// The effective module id the no-active-team gate targets, unifying
    /// the two opt-in paths: an explicit consumer-supplied
    /// `NoActiveTeamLandingModuleId` (a full custom module) takes
    /// precedence; otherwise the parameterized `NoActiveTeamLanding` config
    /// resolves to the SDK built-in landing module's stable id
    /// (`NoActiveTeamLanding.moduleId`). `None` when neither path is set —
    /// the gate is then inert. Scope (`hasTeamScope`) is checked at the call
    /// sites, not here, mirroring the prior direct-field reads.
    let effectiveNoActiveTeamLandingId (config: ClientConfig) : string option =
        NoActiveTeamLanding.resolveLandingId config.NoActiveTeamLandingModuleId config.NoActiveTeamLanding

    /// Phase 66 Stream B.8 — true iff any declared `Team` surface
    /// requests the header team-switcher UX (the retiring `MultiTeam`
    /// shape). Drives the SDK's header switcher render decision.
    let hasMultiTeamSwitcher (config: ClientConfig) : bool =
        config.Surfaces
        |> List.exists (function
            | SurfaceProfile.Team { Switching = HeaderSwitcher } -> true
            | _ -> false)

    /// Phase 66 Stream B.8 — true iff `ClaimBearer` is the only
    /// declared surface (single-shape claim-bearer deployment, e.g.
    /// a public-form-only embed).
    let isClaimBearerOnly (config: ClientConfig) : bool =
        config.Surfaces
        |> List.forall (function
            | SurfaceProfile.ClaimBearer _ -> true
            | _ -> false)
        && not (List.isEmpty config.Surfaces)

    /// Phase 66 Stream B.8 — derive the effective `SubjectKind` for
    /// shell-render decisions (storage selection, sign-in UI mount,
    /// sidebar visibility). Pure projection over the deployment-shape
    /// declared in `config.Surfaces` plus the active team scope —
    /// matches the B.3 derivation byte-for-byte for every
    /// single-surface deployment (the pre-66 norm), and falls back to
    /// the dominant most-authenticated kind for mixed-mode.
    ///
    /// `ClaimBearer` is per-request and unreachable from a persistent
    /// shell render — single-shape `ClaimBearer` deployments still
    /// produce `ClaimBearerKind` here so AuthUIProvider stays
    /// pass-through (the claim has already authenticated the request;
    /// there is nothing for the shell to sign in to).
    ///
    /// GP 12 — this is UI shape only; server-side
    /// `SurfaceEnforcementMiddleware` is the authoritative gate.
    let resolveSubjectKind (activeTeamId: string option) (config: ClientConfig) : SubjectKind =
        // Single-surface ClaimBearer (a public-form-only embed) — the
        // claim is the identity; no sign-in mount, no team scope.
        if isClaimBearerOnly config then
            ClaimBearerKind
        elif requiresAnyAuth config then
            if hasTeamScope config then
                match activeTeamId with
                | Some _ -> TeamMemberKind
                | None -> UserKind
            else
                UserKind
        else
            AnonymousKind

// ─── Module registration helpers ──────────────────────────────────

module ClientModule =
    /// Erase the types for composition into a heterogeneous list.
    /// One of the sanctioned type-erasure boundaries — see
    /// CLAUDE.md "Type erasure boundaries" for the full list.
    let register (m: ClientModule<'Model, 'Msg>) : ErasedModule =
        if m.View.IsNone && m.PageViews.IsNone then
            failwithf
                "ClientModule.register: module '%s' has neither View nor PageViews. Single-page modules must set View; multi-page modules must call withPages."
                m.Definition.Id

        {
            Definition = m.Definition
            Init =
                fun ctx ->
                    let model, cmd = m.Init ctx
                    box model, Cmd.map box cmd
            Update =
                fun msg state ->
                    let typedMsg = unbox<'Msg> msg
                    let typedModel = unbox<'Model> state
                    let newModel, cmd = m.Update typedMsg typedModel
                    box newModel, Cmd.map box cmd
            View =
                m.View
                |> Option.map (fun v ->
                    fun (state: obj) (dispatch: obj -> unit) ->
                        let typedModel = unbox<'Model> state
                        let typedDispatch msg = dispatch (box msg)
                        v typedModel typedDispatch)
            PageViews =
                m.PageViews
                |> Option.map (fun map ->
                    map
                    |> Map.map (fun _ view ->
                        fun (state: obj) (dispatch: obj -> unit) ->
                            let typedModel = unbox<'Model> state
                            let typedDispatch msg = dispatch (box msg)
                            view typedModel typedDispatch))
            NeedsData = m.NeedsData
            DataTypes = m.DataTypes
            ProvidesProcessedData =
                m.ProvidesProcessedData
                |> Option.map (fun f -> fun state -> f (unbox<'Model> state))
            ProvidesNarrative =
                m.ProvidesNarrative
                |> Option.map (fun f -> fun state page -> f (unbox<'Model> state) page)
            Config = m.Config
            FeatureFlags = m.FeatureFlags
            Availability = m.Availability
            Group = m.Group
            Area = m.Area
            ClientQueryHandlers = m.ClientQueryHandlers
            ActionDecoder =
                m.ActionDecoder
                |> Option.map (fun f -> fun (key, payload) -> f (key, payload) |> Option.map box)
            Visibility = m.Visibility
            EventSubscriptions =
                m.EventSubscriptions
                |> Map.map (fun _ mapMsg -> fun payload -> box (mapMsg payload))
        }

    /// Adapter for modules that do not need a `ClientModuleContext`.
    /// Lets an existing `unit -> 'Model * Cmd<'Msg>` init slot into the
    /// new `ClientModuleContext -> 'Model * Cmd<'Msg>` shape without
    /// forcing every module to thread the context through its init.
    /// Modules that read config or platform identity should take the
    /// context directly instead of using this adapter.
    let withUnitInit (init: unit -> 'Model * Cmd<'Msg>) : ClientModuleContext -> 'Model * Cmd<'Msg> = fun _ -> init ()

    /// Record-shaped specification for `ClientModule.create`. Four
    /// fields, all required. Every other capability (View / PageViews /
    /// data types / config / feature flags / RBAC / etc.) defaults off
    /// and is added via the `with*` helpers. The `Id` is auto-derived
    /// from `Name` with spaces stripped; chain `withId` to override
    /// (SDK built-ins do this with `_sdk.*` / `_ai.*` prefixes).
    type ClientModuleSpec<'Model, 'Msg> = {
        Init: unit -> 'Model * Cmd<'Msg>
        Update: 'Msg -> 'Model -> 'Model * Cmd<'Msg>
        Name: string
        Icon: ReactElement
    }

    /// Build a `ClientModule<'Model, 'Msg>` populated with sensible
    /// defaults: `Always` availability, no data types, no config, no
    /// feature flags, `unit`-style init (the common case). The result
    /// has neither `View` nor `PageViews` set — chain `withView` for
    /// a single-page module or `withPages` for a multi-page module
    /// before `register` (the SDK rejects modules with neither at
    /// register time). Mirrors `ServerModule.create |> withGuardedApi`.
    let create<'Model, 'Msg> (spec: ClientModuleSpec<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        Definition = {
            Id = spec.Name.Replace(" ", "")
            Name = spec.Name
            Icon = spec.Icon
            // Empty Pages → shell auto-derives a single sidebar entry
            // from `Definition.Icon + Name` and route `"/" + lowercased-Id`.
            Pages = []
        }
        Init = withUnitInit spec.Init
        Update = spec.Update
        View = None
        PageViews = None
        NeedsData = None
        DataTypes = []
        ProvidesProcessedData = None
        ProvidesNarrative = None
        Config = None
        FeatureFlags = []
        Availability = Always
        Group = None
        Area = Product
        ClientQueryHandlers = []
        ActionDecoder = None
        Visibility = Visibility.visibleToAll
        EventSubscriptions = Map.empty
    }

    /// Set the single-page view function on a `ClientModule` built by
    /// `create`. Mutually exclusive with `withPages` — modules that
    /// have multiple pages call `withPages` instead. The SDK rejects
    /// modules with neither at `register` time.
    let withView
        (view: 'Model -> ('Msg -> unit) -> ReactElement * ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        { m with View = Some view }

    /// Attach per-page views to a strongly-typed `ClientModule` before
    /// erasure. Each entry pairs a `PageConfig` (which also supplies the
    /// sidebar Title and Icon) with a view function that returns a
    /// `PageContent` case — each page picks its own layout format.
    ///
    /// The module's `Init` and `Update` still fire once per module: all
    /// pages share the `Model` and `Msg` types and state is preserved
    /// across page navigation. `Definition.Pages` is set from the keys
    /// of `pageViews`; `Definition.Pages` on the input module is ignored.
    ///
    /// Usage (call before `register`):
    /// ```
    /// {
    ///     Definition = { Id = "SalesAnalysis"; Name = "Sales Analysis"; Pages = [] }
    ///     Init = ...; Update = ...; View = fun _ _ -> Html.none, Html.none
    ///     PageViews = None
    ///     ...
    /// }
    /// |> ClientModule.withPages [
    ///     datasetPage, DatasetView.view
    ///     skuAnalysisPage, SkuAnalysisView.view
    ///     priceElasticityPage, PriceElasticityView.view ]
    /// |> ClientModule.register
    /// ```
    let withPages
        (pageViews: (PageConfig * ('Model -> ('Msg -> unit) -> PageContent)) list)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        let pages = pageViews |> List.map fst
        let viewMap = pageViews |> List.map (fun (p, v) -> p.Route, v) |> Map.ofList

        {
            m with
                Definition = { m.Definition with Pages = pages }
                PageViews = Some viewMap
        }

    /// 0.5.6 — Single-page module variant whose page renders as
    /// `PageContent.FullWidth` rather than the legacy `withView`
    /// `SplitPanel(left, right)` tuple. Sugar over `withPages` with
    /// a single anonymous-route page — saves the boilerplate of
    /// declaring a `PageConfig` for modules that don't expose
    /// multiple routes.
    ///
    /// Suits settings-shaped modules (Permissions, Team Manager,
    /// Webhook Admin, Health Monitor, Data Ingestion config,
    /// Platform Admin tabs) that don't have a separate "controls
    /// left, output right" affordance and would otherwise render
    /// squashed inside the shell's `SplitPanel` wrap (the typical
    /// `body, Html.none` tuple from a single-pane view).
    ///
    /// Caller's view returns the page's content `ReactElement`
    /// directly; the helper wraps it in `PageContent.FullWidth` and
    /// installs it under a single `PageConfig` that inherits the
    /// module's `Definition.Name` and `Definition.Icon`. Mutually
    /// exclusive with `withView` and `withPages` (only the last
    /// applied wins, per the `register` invariant — `register`
    /// fails fast when neither `View` nor `PageViews` is set).
    let withFullWidthView
        (view: 'Model -> ('Msg -> unit) -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        let page: PageConfig = {
            Route = ""
            Title = m.Definition.Name
            Icon = m.Definition.Icon
        }

        let pageView (model: 'Model) (dispatch: 'Msg -> unit) : PageContent =
            PageContent.FullWidth(view model dispatch)

        withPages [ page, pageView ] m

    /// Attach a narrative extractor to a strongly-typed `ClientModule`
    /// before erasure. The function receives the module's `Model` and the
    /// active page route; return `Some doc` if that page currently renders
    /// a `NarrativeDocument`, `None` otherwise. Picked up by the AI
    /// side-panel via `Client.currentNarrative` and attached to
    /// `AIMessageRequest.ActivePageNarrative`.
    let withNarrative
        (extract: 'Model -> string option -> Narrative.NarrativeDocument option)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        {
            m with
                ProvidesNarrative = Some extract
        }

    /// Attach client-side `ModuleQueryBus` handlers to a strongly-typed
    /// `ClientModule` before erasure. Each handler answers one
    /// `(moduleName, queryKey)` pair locally; `SDK.Client.run` collects
    /// them under this module's `Definition.Id` into the shared
    /// `ClientModuleQueryBus`. Construct entries with
    /// `ClientModuleQueryHandler.typed` to get typed request / response
    /// serialisation.
    let withQueryHandlers
        (handlers: ModuleQueryHandler list)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        {
            m with
                ClientQueryHandlers = handlers
        }

    /// Answer a cross-module query declared as a `ModuleQueryContract`
    /// (Phase 584) — the client mirror of `ServerModule.withQueryContract`
    /// and the recommended shape for client-side handlers. The contract
    /// carries the query key and both payload codecs as one value shared
    /// with every caller, so a key typo or a payload-shape drift is a
    /// compile error rather than a request-time `NoHandler`.
    ///
    /// **No new registration shape.** The contract lowers immediately
    /// onto the existing `ClientQueryHandlers` list
    /// (`ModuleQueryContract.handler`), so `SDK.Client.run`'s registry
    /// build, the Phase 579 duplicate rejection and the server-fallback
    /// path see exactly what `withQueryHandlers` produces (GP 11).
    /// Unlike `withQueryHandlers` (which sets the list) this one
    /// *appends*, so several contracts compose.
    ///
    /// The module's `Definition.Id` must equal the contract's
    /// `TargetModule` — `SDK.Client.run` files client handlers under the
    /// module's id, so a mismatch would register under a key no caller
    /// of the contract asks for. Rejected here at compose time.
    let withQueryContract
        (contract: ModuleQueryContract<'Req, 'Resp>)
        (handle: ModuleQueryContext -> 'Req -> Async<'Resp>)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ModuleQueryContract.ensureTargetMatches m.Definition.Id contract

        {
            m with
                ClientQueryHandlers = m.ClientQueryHandlers @ [ ModuleQueryContract.handler contract handle ]
        }

    /// Attach an action decoder to a strongly-typed `ClientModule` before
    /// erasure. The decoder receives `(actionKey, payloadJson)` for every
    /// `Notification.ModuleAction` targeted at this module's `Definition.Id`
    /// and returns the `Msg` to dispatch (or `None` to reject). The shell
    /// routes the result through its existing `ModuleMsg` pathway — the
    /// decoded message lands in the module's `update` exactly as if the
    /// user had triggered it from a UI event.
    let withActionDecoder
        (decoder: string * string -> 'Msg option)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        { m with ActionDecoder = Some decoder }

    /// Subscribe this module to a cross-module client event topic. When
    /// any module publishes `topic` via `ModuleEvents.publish`, the shell
    /// calls `mapMsg` with the event payload and dispatches the resulting
    /// `'Msg` against this module's state through its normal `update`
    /// loop — the module reacts exactly as if the user had triggered the
    /// message from its UI. The target module is initialised on demand if
    /// it has never been navigated to this session.
    ///
    /// Topics are plain strings namespaced by convention (`"<app>/<event>"`
    /// or bare); the publisher and subscriber never reference each other.
    /// Chain multiple calls to subscribe to several topics; a repeated
    /// topic replaces the prior mapper. A module receives its own
    /// publications only when it also subscribes to that topic.
    ///
    /// ```fsharp
    /// ClientModule.create { ... }
    /// |> ClientModule.withEventSubscription "myapp/batch-done" (fun _ -> Refresh)
    /// |> ClientModule.register
    /// ```
    let withEventSubscription
        (topic: string)
        (mapMsg: string -> 'Msg)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        {
            m with
                EventSubscriptions = m.EventSubscriptions |> Map.add topic mapMsg
        }

    /// Gate this module to DEBUG builds only (or re-affirm `Always`). The
    /// decision lives at the application composition root so debug-vs-release
    /// availability is an app-scope concern, not a module-owned property.
    let withAvailability
        (availability: ModuleAvailability)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        { m with Availability = availability }

    /// Assign this module to a sidebar group. Modules sharing a group
    /// name are rendered together under a collapsible header in the
    /// sidebar.
    let withGroup (group: string) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            Group = Some group
    }

    /// Phase 567 — place this module in the given navigation `area`. Only
    /// consulted under `ClientConfig.AdminSurface = SeparateArea`; the
    /// default `Product` (and the default `InlineGroups` surface) leaves
    /// behaviour unchanged. SDK admin built-ins do NOT call this — they are
    /// derived into `Administration` from their sidebar group
    /// (`ClientConfig.effectiveArea`), so no built-in registration changes.
    let withArea (area: ModuleArea) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            Area = area
    }

    /// Phase 66 Stream B.3 — declare a per-module sidebar visibility
    /// predicate. The shell's sidebar filter invokes the predicate
    /// for the resolved `SubjectKind` before rendering; modules
    /// returning `false` are hidden. Prefer the named smart
    /// constructors in the `Visibility` module
    /// (`visibleToAuthenticated`, `visibleToAnonymous`, `visibleTo
    /// [ TeamMemberKind ]`); the default value `Visibility.visibleToAll`
    /// matches pre-B.3 behaviour and need not be set explicitly.
    let withVisibility (predicate: SubjectKind -> bool) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            Visibility = predicate
    }

    /// Override the auto-derived `Definition.Id`. By default `create`
    /// derives the Id as `Name.Replace(" ", "")`. SDK built-in modules
    /// (and any module that wants a stable Id distinct from its display
    /// Name) use this helper to set an explicit value — the Id is the
    /// RBAC key against `makePermissionGuardedApi`, the routing key for
    /// `ModuleAction` notifications, and the sidebar-state key, so it
    /// must be stable across renames.
    let withId (id: string) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            Definition = { m.Definition with Id = id }
    }

    /// Declare data types this module renders summaries for in the file
    /// manager. Each `DataTypeDisplay` pairs a shared `DataTypeInfo`
    /// (declared in the module's SharedTypes) with a `RenderSummary`
    /// function that produces a Feliz `ReactElement` from a list of
    /// type-erased Info objects.
    let withDataTypes (dataTypes: DataTypeDisplay list) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            DataTypes = dataTypes
    }

    /// Declare which data types this module needs before it has
    /// anything to display. Receives a `(DataTypeId -> bool)` predicate
    /// the shell calls per-module to populate the sidebar's "no data"
    /// indicator. Modules that always have something to show (debug
    /// modules, file-manager-style modules) omit this helper.
    let withNeedsData
        (check: (DataManagementTypes.DataTypeId -> bool) -> bool)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        { m with NeedsData = Some check }

    /// Declare a team-editable configuration schema. The SDK admin UI
    /// surfaces a form under this module's key and the shell hands the
    /// persisted values to `Init` via `ClientModuleContext.Config`.
    let withConfig (schema: ModuleConfigSchema) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            Config = Some schema
    }

    /// Declare feature flags this module reads via `FeatureFlags.flag`
    /// or `FeatureFlags.variant`. Aggregating the declared-key set
    /// across modules lets the shell warn on typos at runtime.
    let withFeatureFlags (flags: FeatureFlag list) (m: ClientModule<'Model, 'Msg>) : ClientModule<'Model, 'Msg> = {
        m with
            FeatureFlags = flags
    }

    /// Expose processed data from the module's `Model`. The shell
    /// aggregates these into `Model.ProcessedData` and publishes via
    /// `ProcessedDataContext` so other modules' views can consume them
    /// through the `ProcessedData.forType` hook.
    let withProcessedData
        (extract: 'Model -> ProcessedDataTypes.ProcessedFileEntry list)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        {
            m with
                ProvidesProcessedData = Some extract
        }

    /// Convert a `unit`-style init function on an existing
    /// `ClientModule` to take a `ClientModuleContext` instead. Use when
    /// `create`'s default unit init isn't enough — e.g. modules that
    /// read team-config or platform identity at startup.
    let withContextInit
        (init: ClientModuleContext -> 'Model * Cmd<'Msg>)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        { m with Init = init }