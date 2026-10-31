// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1168 — the code that needs AG Charts itself, in a file of its own
/// so a production build can load it on demand. `MemoizedChart` reaches this
/// module only through a dynamic import, and nothing else in the package may
/// reference it: one static reference puts all of AG Charts back in the first
/// load.
module Feliz.AgChartRuntime

open Fable.Core
open Fable.Core.JsInterop
open Feliz

let private agChartsCommunityModule: obj =
    import "AgChartsCommunityModule" "ag-charts-community"

let private agCharts: obj = import "AgCharts" "ag-charts-react"

// Sanctioned module-level mutable: the same per-tab one-shot registration
// guard as `AgCharts.ensureChartsModulesRegistered`, on this side of the chunk.
let mutable private registered = false

/// The chart behind `MemoizedChart`. Community modules register on the first
/// render, unless the Enterprise companion registered its own first.
let Chart
    (props:
        {|
            externallyRegistered: bool
            chartProps: obj
        |})
    : ReactElement =
    if not props.externallyRegistered && not registered then
        agChartsCommunityModule?setup ()
        registered <- true

    ReactLegacy.createElement (unbox<ReactElement> agCharts, props.chartProps)