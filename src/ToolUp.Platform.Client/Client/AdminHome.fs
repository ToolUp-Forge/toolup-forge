// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AdminHome

open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 573 — the Administration-area landing surface ─────────────
//
// Phase 567 gave the shell two navigation areas and a switcher between
// them. Flipping into Administration re-pointed the RAIL and left the
// content pane on whatever was already open, so "go and administer
// something" landed the operator on a product module, or — once the
// flip navigated at all — on whichever admin module happened to sort
// first. This module is where that flip lands instead.
//
// It is an ordinary registered module (`_sdk.admin-home`), injected only
// under `AdminSurface = SeparateArea` and lifted by the sidebar into the
// same always-visible leading section the product Home occupies — so
// "first in the admin rail" is structural, not a sort order somebody has
// to maintain.
//
// **It names no module, and it holds no tile.** Every tile comes from a
// contributor through the Phase 217 seam (`AdminTile` = a `HomeWidget`
// plus the id of the module it fronts); the SDK's own admin built-ins
// contribute theirs beside their `create`, gated on their own
// `ClientConfig` modes. The shell filters them through the SAME
// navigation decision the rail and the route guard run
// (`AdminTiles.visible`) and publishes the survivors on
// `AdminTileContext`. So this file reads a list and renders it — which
// is exactly why a deployment with `NoHealthMonitor` has no health tile
// without this file knowing what a health monitor is (GP 9).
//
// The data behind a tile rides the same seam too: one call to
// `IHomeOverviewApi.GetOverview` for the scope-correct
// `HomeWidgetContext.Data` bag that Home widgets already read, populated
// by the optional `IHomeWidgetDataProvider`. No new server surface, and
// a failed call degrades to an empty bag — tiles are navigational first,
// so they still render and still click through.

/// Stable reserved id, shared with the sidebar (which lifts it into the
/// leading section) and the shell (which lands the area flip on it).
let moduleId = Toolup.Sidebar.AdminHomeId

type Model = {
    /// The shared widget-data bag. Starts empty and stays empty when the
    /// deployment wires no provider (or the call fails) — every tile
    /// body is written to render without it.
    Data: Map<string, string>
}

type Msg =
    | Refresh
    | WidgetDataLoaded of Map<string, string>
    | OpenModule of string

// ─── API proxy ───────────────────────────────────────────────────

// Header freshness is the CsrfClient request-guard's job — see
// UserSession.fs + SDK.Client.fs installRequestGuard.
let private homeApi: IHomeOverviewApi =
    Api.makeProxy<IHomeOverviewApi> (
        routeBuilder = HomeOverviewApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// Fetch the shared widget-data bag. A failure is not surfaced: the bag
/// is an enrichment, and an error banner over a navigational surface
/// would be louder than the thing it reports.
let private loadWidgetDataCmd =
    Cmd.OfRemoting.call homeApi.GetOverview () (fun ov -> WidgetDataLoaded ov.WidgetData) (fun _ ->
        WidgetDataLoaded Map.empty)

let init () = { Data = Map.empty }, loadWidgetDataCmd

let update (msg: Msg) (model: Model) =
    match msg with
    | Refresh -> model, loadWidgetDataCmd
    | WidgetDataLoaded data -> { model with Data = data }, Cmd.none
    | OpenModule ownerModuleId ->
        // Navigate the way a sidebar click does — the shell's
        // `NavigationRequest` bus dispatches the same `ModuleSelected`,
        // so the route guard and the area derivation both apply.
        NavigationRequest.request ownerModuleId
        model, Cmd.none

// ─── View ────────────────────────────────────────────────────────

/// One tile. The header row is the click target (a single button, so a
/// contributor body containing its own controls never nests inside one);
/// the contributed body renders below it with the shared render context.
let private renderTile (msgs: AdminHomeMessages) (ctx: HomeWidgetContext) (onOpen: string -> unit) (tile: AdminTile) =
    Html.div [
        prop.key tile.Widget.Id
        prop.className "p-4 bg-white border border-gray-200 rounded-lg space-y-2"
        prop.children [
            Html.button [
                prop.className "w-full flex items-center gap-2 text-left group hover:text-blue-600 transition-colors"
                prop.title (msgs.OpenTile tile.Widget.Title)
                prop.onClick (fun _ -> onOpen tile.OwnerModuleId)
                prop.children [
                    Html.span [ prop.className "text-gray-500"; prop.children [ tile.Widget.Icon ] ]
                    Html.span [
                        prop.className "text-sm font-semibold text-gray-800 group-hover:text-blue-600"
                        prop.text tile.Widget.Title
                    ]
                    Html.span [
                        prop.className "ml-auto text-xs text-gray-400 group-hover:text-blue-600"
                        prop.text "→"
                    ]
                ]
            ]
            tile.Widget.Body ctx
        ]
    ]

/// The designed empty state for "this deployment contributes no tiles".
/// Operator-facing, because that is whose problem it is: it names the
/// mechanism (contribution), the two ways a tile appears, and the fact
/// that the rail is still the complete list in the meantime.
let private renderNoTilesContributed (msgs: AdminHomeMessages) =
    Html.div [
        prop.className "p-8 border border-dashed border-gray-300 rounded-lg space-y-3 bg-gray-50"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold text-gray-700"
                prop.text msgs.NoTilesContributedHeading
            ]
            Html.p [
                prop.className "text-sm text-gray-600"
                prop.text msgs.NoTilesContributedBody
            ]
            Html.p [
                prop.className "text-xs text-gray-500"
                prop.text msgs.NoTilesContributedFooter
            ]
        ]
    ]

/// The designed empty state for "tiles exist, none of them is yours".
/// Deliberately says nothing about how many exist or what they front —
/// the reader cannot act on either, and enumerating administration
/// surfaces to somebody who may not open them is a reconnaissance gift.
let private renderNoTilesForSubject (msgs: AdminHomeMessages) =
    Html.div [
        prop.className "p-8 border border-dashed border-gray-300 rounded-lg space-y-3 bg-gray-50"
        prop.children [
            Html.h3 [
                prop.className "text-sm font-semibold text-gray-700"
                prop.text msgs.NoTilesForSubjectHeading
            ]
            Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.NoTilesForSubjectBody ]
            Html.p [
                prop.className "text-xs text-gray-500"
                prop.text msgs.NoTilesForSubjectFooter
            ]
        ]
    ]

/// The tile grid. A React component so it reads the shell-published,
/// already role-filtered tile list from context; the contributed total
/// comes from the registry, and the two together choose between the
/// grid and the two empty states (`AdminTiles.landingState`).
[<ReactComponent>]
let private TileGrid (data: Map<string, string>) (onOpen: string -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).AdminHome
    let visibleTiles = React.useContext AdminTileContext.Context
    let contributed = AdminTileRegistry.tiles () |> List.length
    let ctx: HomeWidgetContext = { Data = data }

    match AdminTiles.landingState contributed visibleTiles with
    | AdminTiles.LandingState.NoTilesContributed -> renderNoTilesContributed msgs
    | AdminTiles.LandingState.NoTilesForSubject -> renderNoTilesForSubject msgs
    | AdminTiles.LandingState.Tiles tiles ->
        Html.div [
            prop.className "grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4"
            prop.children (tiles |> List.map (renderTile msgs ctx onOpen))
        ]

/// Phase 751 — the page header as a React COMPONENT, for the same reason
/// `HealthMonitorUI.HealthMonitorBody` is one: a module's `view` is invoked
/// inline by the shell's own render, so a hook there would join the shell's
/// hook order and break the moment the active module changed.
[<ReactComponent>]
let private AdminHomeHeader (onRefresh: unit -> unit) =
    let msgs = (MessageCatalogProvider.useMessages ()).AdminHome

    Html.div [
        prop.className "flex items-center justify-between mb-4"
        prop.children [
            Html.div [
                prop.children [
                    Html.h2 [ prop.className "text-lg font-semibold text-gray-800"; prop.text msgs.Heading ]
                    Html.p [ prop.className "text-sm text-gray-600"; prop.text msgs.Subheading ]
                ]
            ]
            Html.button [
                prop.className "px-3 py-1 text-sm bg-gray-100 hover:bg-gray-200 border border-gray-300 rounded"
                prop.onClick (fun _ -> onRefresh ())
                prop.text msgs.Refresh
            ]
        ]
    ]

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.className "p-4"
        prop.children [
            AdminHomeHeader(fun () -> dispatch Refresh)
            TileGrid model.Data (OpenModule >> dispatch)
        ]
    ]

// ─── Module creation ─────────────────────────────────────────────

/// Create the administration-landing module as an `ErasedModule`. The
/// shell's `prepareModules` injects it only under
/// `AdminSurface = SeparateArea` — under the default `InlineGroups`
/// there are no areas to land between, so the composition is
/// byte-for-byte pre-573 (GP 11).
///
/// Ungrouped, and declared into the `Administration` area explicitly
/// (rather than inheriting it from an admin group name), so the area
/// derivation does not depend on a presentational label — the same
/// separation Phase 568 made for the role gate. `NavRole.TeamOwnerAdmin`
/// is the weakest gate any administration surface carries, so every
/// caller who has an administration area to enter can see its landing,
/// and a plain Member — who has no admin modules and therefore no
/// switcher — never does.
let create () : ErasedModule =
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = "Administration"
        Icon = ToolUp.Platform.Icons.settings
    }
    |> ToolUp.Platform.ClientModule.withId moduleId
    // Phase 611 — declared placement, replacing the `isLandingId` id check
    // `buildSections` used to carry: this landing shares the leading
    // always-visible section with the product one, and says so itself.
    |> ToolUp.Platform.ClientModule.withPlacement Toolup.Sidebar.LeadingSlot
    |> ToolUp.Platform.ClientModule.withFullWidthView view
    |> ToolUp.Platform.ClientModule.withArea ModuleArea.Administration
    |> ToolUp.Platform.ClientModule.withNavRole ToolUp.Platform.NavRole.TeamOwnerAdmin
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register