// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

// ─── Module-contributed administration-tile plumbing (Phase 573) ────
//
// Three small pieces, all deliberately dumb — every decision this
// surface makes lives in the pure `AdminTiles` fold, and every tile it
// renders comes from a contributor:
//
//   * `AdminTileRegistry` — the boot-populated contribution cache, the
//     `HomeWidgetRegistry` twin (same shape, same sole writer, same
//     GP 13 default-off-by-absence).
//   * `AdminTileContext` — how the ALREADY role-filtered tiles reach
//     the landing module's view. The landing page is an ordinary
//     registered module, so its view sees a `Model` and a `dispatch`
//     and nothing else; the filtering needs the shell's navigation
//     inputs. Rather than hand the module a second copy of the
//     shell's state (which could drift), the shell filters once, in
//     the one place that owns the decision, and publishes the result
//     — exactly the `ProcessedDataContext` pattern, and the same
//     reason: the shell distributes shared state.
//   * `AdminTileBody` — the compact body a tile gets for free: an
//     optional headline pulled from the shared `HomeWidgetContext`
//     data bag, over a fixed description of the surface. A contributor
//     wanting something richer just writes its own `Body`.
//
// This file must compile BEFORE the SDK admin built-ins, since each of
// them declares its own tile beside its `create` (573.B).

/// Registry of module-contributed administration tiles. Populated once
/// at boot from the SDK's tile-contributing built-ins (gated on their
/// own `ClientConfig` modes) plus
/// `ClientConfig.Handlers.AdminTileContributors`. The landing module
/// reads `tiles ()` for the contributed total; the shell reads it for
/// the set it filters and publishes.
module AdminTileRegistry =

    // Boot-populated cache: every contributor's tiles, flattened and
    // ordered by `Widget.Weight` once. Sole writer is the SDK boot
    // path — contributors never touch it (GP 9).
    let mutable private cached: AdminTile list = []

    /// The `AdminTile` → decision-facts projection the pure
    /// `AdminTiles` fold is stated over.
    let facts (tile: AdminTile) : AdminTiles.AdminTileFacts = {
        Id = tile.Widget.Id
        OwnerModuleId = tile.OwnerModuleId
        Weight = tile.Widget.Weight
    }

    /// Called once by `SDK.Client.boot` with the SDK's own built-in
    /// contributors followed by
    /// `ClientConfig.Handlers.AdminTileContributors`. Flattens and
    /// orders once, so the render path is a field read.
    let setContributors (contributors: IAdminTileContributor list) : unit =
        cached <- contributors |> List.collect (fun c -> c.Tiles()) |> AdminTiles.ordered facts

    /// Every contributed tile, ordered by weight — BEFORE the caller's
    /// visibility filter. Empty when nothing was contributed, which is
    /// what distinguishes the landing's two empty states.
    let tiles () : AdminTile list = cached

    /// Distinct tile ids in contribution order — the shape a duplicate-
    /// id diagnostic reports on, mirroring `HomeWidgetRegistry`.
    let registeredIds () : string list = cached |> List.map _.Widget.Id

/// React context carrying the administration tiles this caller may see,
/// already filtered and ordered by the shell (`AdminTiles.forOwners`
/// over the rail's own visible-module list). The landing module's view
/// reads it; every other view ignores it. Default `[]` — a tree
/// rendered without the shell's provider (a test harness, a companion
/// composer that builds its own view) shows the empty state rather than
/// an unfiltered one.
module AdminTileContext =
    let Context = React.createContext<AdminTile list> (defaultValue = [])

/// The default compact tile body. A tile names a key in the shared
/// `HomeWidgetContext.Data` bag (the same bag Home widgets read,
/// populated server-side by `IHomeWidgetDataProvider`); when a
/// deployment supplies a value for it the tile leads with that
/// headline, and otherwise it leads with nothing and still says what
/// the surface is for. So an SDK built-in tile is useful with no server
/// wiring at all, and lights up with real numbers through the seam the
/// deployment already has — no new server surface for the SDK to mount.
module AdminTileBody =

    /// `headlineKey`'s value from the bag (when present) above
    /// `description`.
    let summary (headlineKey: string) (description: string) (ctx: HomeWidgetContext) : ReactElement =
        Html.div [
            prop.className "space-y-1"
            prop.children [
                match ctx.Data |> Map.tryFind headlineKey with
                | Some headline when headline <> "" ->
                    Html.div [
                        prop.className "text-lg font-semibold text-gray-800 font-mono"
                        prop.text headline
                    ]
                | _ -> Html.none
                Html.p [ prop.className "text-xs text-gray-500"; prop.text description ]
            ]
        ]