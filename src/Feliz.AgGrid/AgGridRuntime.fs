// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1168 — the code that needs AG Grid itself, in a file of its own so
/// a production build can load it on demand. `MemoizedGrid` reaches this
/// module only through a dynamic import, and nothing else in the package may
/// reference it: one static reference puts all of AG Grid back in the first
/// load, which the perf-budget gate reports as a chunk that went missing.
module Feliz.AgGridRuntime

open Fable.Core
open Fable.Core.JsInterop
open Feliz

let private allCommunityModule: obj =
    import "AllCommunityModule" "ag-grid-community"

let private agGridReact: obj = import "AgGridReact" "ag-grid-react"
let private agGridProvider: obj = import "AgGridProvider" "ag-grid-react"
let private moduleRegistry: obj = import "ModuleRegistry" "ag-grid-community"

let private communityModules = [| allCommunityModule |]

/// The base themes `AgGrid.baseTheme` names. A name resolves here, in the
/// chunk, so a view can pick a theme without importing AG Grid.
let private baseThemes =
    createObj [
        "themeQuartz" ==> (import "themeQuartz" "ag-grid-community": obj)
        "themeBalham" ==> (import "themeBalham" "ag-grid-community": obj)
    ]

// Sanctioned module-level mutable: the same per-tab one-shot registration
// guard as `AgGrid.ensureGridModulesRegistered`, on this side of the chunk.
let mutable private registered = false

/// `gridProps` with a base-theme name replaced by the theme it names.
/// Anything else, a built `Theme` included, passes through untouched.
let private withResolvedTheme (gridProps: obj) : obj =
    let theme: obj = baseThemes?(unbox<string> gridProps?theme)

    if isNullOrUndefined theme then
        gridProps
    else
        JS.Constructors.Object.assign (createObj [], gridProps, createObj [ "theme" ==> theme ])

/// The grid behind `MemoizedGrid`. `modules` is `None` for a program that
/// never named a module configuration, which registers the Community modules
/// globally; `Some` is what the SDK shell configured, supplied through
/// `AgGridProvider`. An empty array there means Community.
let Grid
    (props:
        {|
            modules: obj array option
            licenseKey: string option
            externallyRegistered: bool
            gridProps: obj
        |})
    : ReactElement =
    if not props.externallyRegistered && not registered then
        moduleRegistry?registerModules (communityModules)
        registered <- true

    let gridProps =
        React.useMemo ((fun () -> withResolvedTheme props.gridProps), [| props.gridProps |])

    let grid = ReactLegacy.createElement (unbox<ReactElement> agGridReact, gridProps)

    match props.modules with
    | None -> grid
    | Some modules ->
        let providerProps =
            createObj [
                "modules" ==> (if Array.isEmpty modules then communityModules else modules)
                match props.licenseKey with
                | Some key -> "licenseKey" ==> key
                | None -> ()
                "children" ==> grid
            ]

        ReactLegacy.createElement (unbox<ReactElement> agGridProvider, providerProps)