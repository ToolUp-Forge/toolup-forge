// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Module-contributed Home-widget registry (Phase 217) ────────────
//
// A module surfaces a custom widget on the Phase 171 Home / Overview
// landing surface by exporting an `IHomeWidgetContributor` value
// (`HomeWidget`/`IHomeWidgetContributor` live in `SDK.ClientTypes.fs`
// so `ClientConfig` can reference them). Consumers add the contributor
// to `ClientConfig.Handlers.HomeWidgetContributors`; this registry is
// populated once at boot by `SDK.Client.program`, and the built-in
// `Home` module reads `widgets ()` at render time.
//
// Same shape as `DataSourceCredentialUIRegistry` — a boot-populated
// per-tab cache whose sole writer is the SDK boot path; contributors
// never touch it directly (GP 9: the SDK never names a contributor).
//
// Default-off by absence (GP 13): no contributor ⇒ `widgets ()` is
// empty ⇒ Home renders byte-for-byte as Phase 171.

/// Registry of module-contributed Home widgets. Populated once at boot
/// from `ClientConfig.Handlers.HomeWidgetContributors`; `Home.fs` calls
/// `widgets ()` to render the contributed section below the built-in
/// cards.
module HomeWidgetRegistry =

    // Boot-populated cache: every contributor's widgets, flattened and
    // sorted by `Weight` (ascending; ties keep registration order via
    // the stable `List.sortBy`). Sole writer is the SDK boot path.
    let mutable private cached: HomeWidget list = []

    /// Called once by `SDK.Client.program` with the value from
    /// `ClientConfig.Handlers.HomeWidgetContributors`. Flattens each
    /// contributor's `Widgets ()` and sorts by `Weight` once, so the
    /// render path is a cheap field read.
    let setContributors (contributors: IHomeWidgetContributor list) : unit =
        cached <- contributors |> List.collect (fun c -> c.Widgets()) |> List.sortBy _.Weight

    /// The contributed widgets, pre-sorted by `Weight`. Empty when no
    /// contributor is wired in — `Home.fs` then renders exactly the
    /// Phase 171 surface.
    let widgets () : HomeWidget list = cached

    /// Distinct widget ids in registration/sort order — used by the
    /// `Client.run` validator to surface duplicate ids.
    let registeredIds () : string list = cached |> List.map _.Id