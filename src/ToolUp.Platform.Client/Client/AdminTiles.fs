// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdminTiles

// ─── Phase 573 — the administration-landing tile fold ─────────────────
//
// The Administration area (Phase 567) is entered by a switcher click, and
// until this phase that click flipped the rail while the content pane
// kept rendering whichever module happened to be active. The area now has
// a landing surface, and this module is the part of it that is a
// *decision* rather than a render: which contributed tiles this caller
// sees, in what order, and which of the two empty states applies.
//
// **Why the tiles are filtered by their OWNING MODULE.** A tile fronts a
// surface — it exists to say "here is the health of the deployment, open
// the health monitor". If the tile were visible where the surface is not,
// the landing page would advertise a destination the rail hides and the
// route guard refuses (Phase 569), which is the exact drift Phases
// 568–571 spent four passes collapsing. So a tile carries its owner's
// module id and is admitted iff `SidebarVisibility.canNavigateTo` admits
// that module — `visible` below is literally `forOwners` applied to
// `SidebarVisibility.visibleIds`, one call to the canonical decision, no
// second predicate that could fall out of step.
//
// That also gives 573.B's "mode-off ⇒ no tile" for free, twice over: a
// deployment with `NoHealthMonitor` contributes no health tile AND has no
// health module for a stray tile to own, so the tile is filtered even if
// something contributed it anyway.
//
// **Why this file and not `AdminHome.fs`.** Same reason
// `SidebarVisibility.fs` exists: the fold has to be callable from the
// .NET test harness, and nothing that builds a `ReactElement` is. So the
// decision is stated over a three-field projection of a tile and is
// generic over the tile representation — the shell passes the real
// `AdminTile` projection, the contract pack passes `id` over hand-built
// facts.

/// The three per-tile facts the landing fold reads. Everything else on a
/// contributed tile (its title, its icon, its rendered body) is
/// presentation the decision never consults — which is what keeps this
/// module declared ahead of `SDK.ClientTypes.fs` and .NET-testable.
type AdminTileFacts = {
    /// The contributed tile's stable id (the React key). Unique across
    /// contributors; duplicates are a contributor defect, surfaced at
    /// boot the same way duplicate Home-widget ids are.
    Id: string
    /// `ModuleDefinition.Id` of the module this tile fronts — BOTH the
    /// click-through target and the visibility key. A tile whose owner is
    /// not a registered, reachable module is not rendered.
    OwnerModuleId: string
    /// Sort key — ascending, ties keeping contribution order.
    Weight: int
}

/// Tiles in render order: ascending `Weight`, ties preserving
/// contribution order (`List.sortBy` is stable).
let ordered (facts: 't -> AdminTileFacts) (tiles: 't list) : 't list =
    tiles |> List.sortBy (fun t -> (facts t).Weight)

/// Tiles whose owning module is one this caller may reach, in render
/// order. `reachable` is the caller's navigable module-id set — see
/// `visible` for the canonical way to obtain it.
///
/// A tile whose owner is absent from `reachable` is dropped whether that
/// is because the caller's role does not admit the module, because the
/// deployment never enabled it, or because the owner id names nothing at
/// all: from here they are the same fact, and all three are the right
/// answer.
let forOwners (facts: 't -> AdminTileFacts) (reachable: string list) (tiles: 't list) : 't list =
    let owners = Set.ofList reachable

    tiles
    |> List.filter (fun t -> owners.Contains (facts t).OwnerModuleId)
    |> ordered facts

/// **The canonical tile-visibility decision (573.C)** — `forOwners`
/// sourced from `SidebarVisibility.visibleIds`, i.e. from the same fold
/// the rail, the route guard (Phase 569) and the command palette (Phase
/// 571) run. Stated here as one composition so the equation
/// `visible ≡ forOwners (visibleIds …)` is a definition rather than a
/// promise; the shell calls `forOwners` directly on the module list it
/// has already filtered for the rail, which is the same values without a
/// second fold.
let visible
    (tileFacts: 't -> AdminTileFacts)
    (moduleFacts: 'm -> SidebarVisibility.SidebarModuleFacts)
    (inputs: SidebarVisibility.SidebarVisibilityInputs)
    (modules: 'm list)
    (tiles: 't list)
    : 't list =
    forOwners tileFacts (SidebarVisibility.visibleIds moduleFacts inputs modules) tiles

/// What the landing surface has to render. The two empty cases are
/// deliberately DISTINCT: "this deployment contributes no tiles" is an
/// operator-facing fact with an operator-facing remedy (enable a module,
/// or register a contributor), while "none of them is yours" is a
/// role-facing fact with no action for the reader at all. Collapsing them
/// into one "nothing here" panel would tell the operator nothing and tell
/// the user something untrue.
[<RequireQualifiedAccess>]
type LandingState<'t> =
    /// No contributor supplied a tile — the deployment enables no
    /// tile-contributing administration module and wired no contributor
    /// of its own.
    | NoTilesContributed
    /// Tiles exist, but none of them fronts a surface this caller may
    /// reach.
    | NoTilesForSubject
    /// The tiles to render, in order.
    | Tiles of 't list

/// Classify the landing surface from the contributed-tile count and the
/// caller's visible tiles. Takes the count rather than the list because
/// the two are different types at the shell's call site (the registry
/// holds dressed tiles; the visible list is whatever the caller passed)
/// and the classification only ever asks whether the first is empty.
let landingState (contributedCount: int) (visibleTiles: 't list) : LandingState<'t> =
    if contributedCount = 0 then
        LandingState.NoTilesContributed
    elif List.isEmpty visibleTiles then
        LandingState.NoTilesForSubject
    else
        LandingState.Tiles visibleTiles