// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Home / Overview landing API (Phase 171) ─────────────────────
//
// `IHomeOverviewApi` is the read-side aggregation surface for the
// optional SDK-built-in Home landing module (`Client/Home.fs`). One
// call returns the deployment at a glance: the data-producing tools,
// the per-tool record counts within the caller's scope, the active AI
// provider/model, and a light deployment-context strip.
//
// **Scope-correct by construction (GP 4).** Every count is read for
// the scope resolved from `AccessContext` only; the caller cannot pass
// an arbitrary one. Another tenant's objects are structurally unreachable.
//
// **Opt-in, zero-cost when unused (GP 13).** The route is mounted only
// when a deployment opts the Home module in; the `ActiveAi` field is
// `None` unless an AI companion is composed (it is populated through
// the optional `IActiveAiProbe` DI seam below, never by a hard
// dependency on `ToolUp.AI` — GP 1).

/// Per-data-type record count for one tool, within the caller's scope.
type ToolDataCount = {
    /// Catalog data-type id (`DataTypeInfo.Id`).
    TypeId: string
    /// Human-readable type name for display.
    DisplayName: string
    /// Number of stored objects of this type in the caller's scope.
    Count: int
}

/// Per-tool (per-module) data summary. A "tool" here is a data-
/// producing module known to the data catalog; modules that declare
/// no data types do not appear (a Home overview summarises *data
/// present per tool*).
type ToolSummary = {
    /// The producing module's id — matches the client module's
    /// `Definition.Id`, so the Home view can navigate to it via
    /// `NavigationRequest.request`.
    ModuleId: string
    /// Human-readable tool name.
    Name: string
    /// Per-type counts within the caller's scope.
    DataCounts: ToolDataCount list
    /// Sum of `DataCounts` — the tool's total record count in scope.
    TotalRecords: int
}

/// Active AI provider/model summary. Present on the overview only when
/// an AI provider is composed (GP 13).
type ActiveAiSummary = {
    /// Human-readable provider name (e.g. "Anthropic Claude").
    ProviderLabel: string
    /// Active model id, when resolvable.
    Model: string option
}

/// Light deployment-context strip for the Home overview. (The app
/// name / logo already live in the shell header, so they are not
/// repeated here.)
type DeploymentContext = {
    /// Coarse deployment-mode label derived from the resolved subject
    /// (e.g. "Team", "Individual", "Anonymous") — reflects what the
    /// caller actually sees, not the raw config.
    Mode: string
    /// One-line health summary — populated only for platform admins
    /// (least-privilege; `None` for everyone else).
    Health: string option
}

/// Aggregated landing-page overview. Scope-correct + RBAC-filtered:
/// only tools and counts the caller can access appear.
type HomeOverview = {
    Tools: ToolSummary list
    ActiveAi: ActiveAiSummary option
    Deployment: DeploymentContext
    /// UTC timestamp the snapshot was assembled.
    GeneratedAt: DateTime
    /// Phase 217 — scope-correct data bag for module-contributed Home
    /// widgets, populated by the optional `IHomeWidgetDataProvider` DI
    /// seam. A contributed widget reads its values from here (via
    /// `HomeWidgetContext.Data`) rather than issuing a second round
    /// trip. Empty (`Map.empty`) when no provider is composed — the
    /// default — so the overview is byte-for-byte the Phase 171 shape
    /// for a deployment with no widget contributors (GP 13). Keys are
    /// contributor-namespaced (e.g. `"my-widget.total"`).
    WidgetData: Map<string, string>
}

/// Phase 217 — per-user recents/pinning state for the optional
/// "Pinned / Recent" Home widget. `Recent` is most-recently-visited
/// first; `Pinned` is the user's pinned tool ids. Both are module ids
/// (matching `ToolSummary.ModuleId` so the widget navigates via
/// `NavigationRequest.request`). **Per-user, never cross-scope (GP 4):**
/// persisted under the caller's *user* scope even in Team mode, so a
/// team-mate never sees another member's recents.
type HomePinningState = {
    /// Tool ids the user has pinned, in pin order.
    Pinned: string list
    /// Recently-visited tool ids, most-recent-first (bounded).
    Recent: string list
}

module HomePinningState =
    /// Nothing pinned, nothing visited — the default a fresh user (and
    /// every deployment with recents/pinning off) sees.
    let empty: HomePinningState = { Pinned = []; Recent = [] }

/// A pin/unpin request for the recents/pinning surface.
type PinRequest = {
    /// The tool (module id) to pin or unpin.
    ModuleId: string
    /// `true` to pin, `false` to unpin.
    Pinned: bool
}

/// Fable.Remoting surface for the Home/Overview landing module.
/// Scoped server-side from the caller's resolved `AccessContext`; requires a
/// resolved scope (no anonymous access — an Anonymous caller has no
/// scope to summarise).
type IHomeOverviewApi = {
    /// Single aggregation call: tool inventory + per-tool scope-correct
    /// record counts + active-AI summary + deployment context.
    [<RequiresClaim "scope">]
    GetOverview: unit -> Async<HomeOverview>
    /// Phase 217 — read the caller's per-user recents/pinning state.
    /// `HomePinningState.empty` when nothing is stored yet.
    [<RequiresClaim "scope">]
    GetPinning: unit -> Async<HomePinningState>
    /// Phase 217 — record that the caller opened a tool; prepends it to
    /// `Recent` (dedup, bounded) and returns the updated state so the
    /// client refreshes without a second read.
    [<RequiresClaim "scope">]
    RecordVisit: string -> Async<HomePinningState>
    /// Phase 217 — pin or unpin a tool for the caller; returns the
    /// updated state.
    [<RequiresClaim "scope">]
    SetPinned: PinRequest -> Async<HomePinningState>
}

/// Optional DI seam (GP 1 / GP 13). The AI companion (`ToolUp.AI`)
/// implements and registers this so the platform-tier Home overview
/// can surface the active provider/model **without** `Platform.Server`
/// taking a dependency on `ToolUp.AI`. Absent from DI when no AI is
/// composed → the overview's `ActiveAi` is `None`.
///
/// Wire-safe by construction — returns only the `ActiveAiSummary`
/// value record, so the seam compiles harmlessly under Fable even
/// though only the server resolves it.
type IActiveAiProbe =
    /// Describe the AI provider/model active for `scopeId`. `None`
    /// when no provider is configured/resolvable for that scope.
    abstract Describe: scopeId: string -> Async<ActiveAiSummary option>

/// Optional DI seam for a module-contributed Home widget that needs
/// scope-correct server data (Phase 217). A module registers an
/// implementation so its widget's values ride the single
/// `GetOverview` aggregation call (`HomeOverview.WidgetData`) rather
/// than issuing a second round trip. Resolved as a *collection* —
/// every registered provider runs, and the handler merges their maps;
/// absent entirely (the default) → `WidgetData` is empty and Home is
/// the byte-for-byte Phase 171 shape (GP 13). The SDK never names a
/// providing module (GP 9 / GP 1).
///
/// **Scope-correct by construction (GP 4).** The handler passes the
/// caller's resolved `scopeId`; a provider reads only that scope's
/// data, never an arbitrary one. **Namespacing:** a provider keys its
/// values under its widget id prefix (e.g. `"my-widget.total"`) so
/// providers don't collide in the shared bag.
type IHomeWidgetDataProvider =
    /// Scope-correct data for this provider's widget(s), keyed by
    /// contributor-namespaced keys. Empty map when the provider has
    /// nothing to contribute for `scopeId`.
    abstract Describe: scopeId: string -> Async<Map<string, string>>

module HomeOverviewApi =
    /// Fable.Remoting route builder. Mirrors `UsageQueryApi`'s shape —
    /// a fixed `/api/_platform/home/*` prefix so the Home client proxy
    /// can discover the endpoint by path alone.
    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "/api/_platform/home/%s" methodName