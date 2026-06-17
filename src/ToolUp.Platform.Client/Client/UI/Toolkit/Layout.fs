// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform
open ToolUp.Platform.DataProp

module Layout =
    //open Fable.Core.JsInterop
    //let UserButton: obj = import "UserButton" "@clerk/react"
    // Import everything once
    let private clerk = importAll<obj> "@clerk/react"

    // Wrapped components - each accepts Feliz-style props (IReactProperty list)
    let UserButton (props: IReactProperty list) : ReactElement =
        ReactLegacy.createElement (unbox<ReactElement> clerk?UserButton, createObj !!props)

    /// Render Clerk's UserButton inside the page header when an active
    /// ClerkProvider has been mounted; render nothing otherwise. Apps in
    /// Anonymous / dev modes don't mount a provider, so `clerk?UserButton`
    /// would throw at render — `window.Clerk` (populated by the Clerk SDK
    /// when its provider mounts) is the feature-detection signal.
    /// Bypasses the F# `UserButton` wrapper above because passing
    /// `{| showName = true |}` through `ReactLegacy.createElement` hits
    /// the wrapper's `createObj !!props` with an anonymous record, which
    /// is not an `IReactProperty list` and crashes with "fields is not
    /// iterable".
    let userButton () : ReactElement =
        if isNull Browser.Dom.window?Clerk then
            Html.none
        else
            ReactLegacy.createElement (unbox<ReactElement> clerk?UserButton, {| showName = true |}, [||])

    /// Page header with optional caller-supplied trailing action element
    /// (e.g. an AI chat toggle button — the shell knows which feature owns
    /// the slot; the toolkit stays feature-agnostic).
    let pageHeader (title: string, icon) (headerAction: ReactElement option) =
        Html.div [
            prop.className "w-full h-20 bg-white border-b border-separator px-6 flex items-center"
            prop.children [
                Html.div [
                    prop.className "flex items-center gap-3"
                    prop.children [
                        Html.div [ prop.className "w-10 h-10"; prop.children [ icon ] ]
                        Html.h1 [ prop.className "text-xl font-bold text-brand"; prop.text title ]
                    ]
                ]
                Html.div [
                    prop.className "flex justify-end ml-auto items-center gap-3"
                    prop.children [
                        match headerAction with
                        | Some el -> el
                        | None -> ()
                        userButton ()
                    ]
                ]
            ]
        ]

    /// Resolve the Tailwind class string that wraps the inputs (left)
    /// child of a `SplitPanel` render. `Narrow` / `Wide` give the inputs
    /// pane an enforced width regardless of consumer content; `Auto`
    /// lets the natural content width drive sizing but still pins
    /// `shrink-0` so the input pane doesn't collapse when the sibling
    /// (right pane / `flex-1`) takes more space than the parent has —
    /// the symptom that motivated the change was combo-box content
    /// overflowing the left column on Price Elasticity / Category
    /// Analysis when the right pane's table widened.
    ///
    /// `Narrow` / `Wide` additionally carry `min-w-0 overflow-hidden` to
    /// defeat the default flex `min-width: auto` that would otherwise let
    /// intrinsically-wide inner content (long button labels, AG Grid
    /// summaries, wide form rows) push the wrapper past its declared
    /// width and bleed into the results pane. With the pair, `w-96` /
    /// `w-[32rem]` becomes a hard cap; content that genuinely exceeds it
    /// gets clipped at the boundary rather than escape across the row.
    /// `Auto` deliberately omits both — its contract is "let natural
    /// content width drive sizing", so a hard cap there would contradict
    /// the only reason a consumer would pick it.
    let private inputsPaneClass (width: InputsPaneWidth) : string =
        match width with
        | Narrow -> "w-96 shrink-0 min-w-0 overflow-hidden"
        | Wide -> "w-[32rem] shrink-0 min-w-0 overflow-hidden"
        | Auto -> "shrink-0"

    /// Render the active page's `PageContent` to a single `ReactElement`,
    /// applying the per-case content-area gutters / layout classes. Extracted
    /// from `AppShell` so callers (notably `Components.ModuleBoundary`) can
    /// wrap a module's view in an error boundary while still producing the
    /// laid-out tree the boundary's child needs.
    let renderPageContent (inputsWidth: InputsPaneWidth) (content: PageContent) : ReactElement =
        match content with
        | SplitPanel(leftPane, rightPane) ->
            let leftWrapped =
                match inputsPaneClass inputsWidth with
                | "" -> leftPane
                | cls -> Html.div [ prop.className cls; prop.children [ leftPane ] ]

            Html.div [
                prop.className "flex gap-6 p-6 min-h-full"
                prop.children [
                    leftWrapped
                    // `min-w-0` on the results pane mirrors the inputs-pane
                    // containment: without it, an intrinsically-wide right
                    // child (auto-sized AG Grid, long-form chart legend)
                    // could push the row past the viewport and bleed past
                    // the AppShell's `overflow-auto` boundary.
                    Html.div [ prop.className "flex-1 min-w-0"; prop.children [ rightPane ] ]
                ]
            ]
        | Stacked sections -> Html.div [ prop.className "flex flex-col gap-6 p-6 min-h-full"; prop.children sections ]
        | FullWidth el -> Html.div [ prop.className "p-6 min-h-full"; prop.children [ el ] ]
        | Dashboard areas ->
            Html.div [
                prop.className "grid gap-6 p-6 min-h-full"
                prop.style [
                    style.custom ("gridTemplateColumns", "repeat(auto-fit, minmax(320px, 1fr))")
                    style.custom ("gridAutoRows", "min-content")
                ]
                prop.children [
                    for (name, el) in areas -> Html.div [ dataProp.area name; prop.children [ el ] ]
                ]
            ]
        | Custom el -> el

    /// Gray-100 + `animate-pulse` placeholder block — the shared "data is
    /// arriving" idiom (also used by `HealthMonitorUI` /
    /// `ServiceStatusBoardUI`). Sized by the caller via `cls`.
    let private pulseBlock (cls: string) : ReactElement =
        Html.div [ prop.className (sprintf "bg-gray-100 rounded animate-pulse %s" cls) ]

    /// 0.5.16 — content-area skeleton placeholder rendered by the shell
    /// during its `Prefetching` lifecycle phase (`Client.InitPhase`).
    /// Wrapped by the shell into `PageContent.Custom` so
    /// `renderPageContent` emits it verbatim. Matches the visual rhythm of
    /// `SplitPanel` — narrow inputs column + wide results pane — so the
    /// layout doesn't reflow when the active module mounts on promotion
    /// to `Ready`. `role="status"` + `aria-label="Loading"` surfaces the
    /// placeholder to assistive tech without announcing every block
    /// individually.
    let loadingSkeleton () : ReactElement =
        Html.div [
            prop.role "status"
            prop.ariaLabel "Loading"
            prop.className "flex gap-6 p-6 min-h-full"
            prop.children [
                Html.div [
                    prop.className "w-96 shrink-0 space-y-3"
                    prop.children [ pulseBlock "h-8 w-3/4"; pulseBlock "h-32 w-full"; pulseBlock "h-10 w-1/2" ]
                ]
                Html.div [
                    prop.className "flex-1 min-w-0 space-y-3"
                    prop.children [ pulseBlock "h-8 w-1/4"; pulseBlock "h-96 w-full" ]
                ]
            ]
        ]

    /// Centre a loading icon. `wrapperClasses` controls the surrounding
    /// flex box + height behaviour (the shell's full-area variant uses
    /// `min-h-full`, the in-content variant a fixed vertical pad);
    /// `svgSizeClasses` carries the Tailwind arbitrary-variant sizing
    /// (`[&>svg]:w-16` etc.) the icon SVG inherits — the brand mark is
    /// self-coloured (gradient) while the spinner picks up `text-*` via
    /// `currentColor`.
    let private centredLoader (wrapperClasses: string) (svgSizeClasses: string) (icon: ReactElement) : ReactElement =
        Html.div [
            prop.role "status"
            prop.ariaLabel "Loading"
            prop.className wrapperClasses
            prop.children [ Html.div [ prop.className svgSizeClasses; prop.children [ icon ] ] ]
        ]

    /// Resolve a `LoadingIndicatorMode` to the full-content-area element
    /// the shell renders during its `Prefetching` phase. `SkeletonLoader`
    /// keeps the 0.5.16 gray-pulse skeleton; `BrandMarkLoader` /
    /// `SpinnerLoader` centre the matching icon; `CustomLoader` defers to
    /// the consumer-supplied thunk.
    let loadingIndicator (mode: LoadingIndicatorMode) : ReactElement =
        match mode with
        | SkeletonLoader -> loadingSkeleton ()
        | BrandMarkLoader ->
            centredLoader
                "flex items-center justify-center min-h-full p-6"
                "[&>svg]:w-16 [&>svg]:h-16"
                ToolUp.Platform.Icons.dataLoading
        | SpinnerLoader ->
            centredLoader
                "flex items-center justify-center min-h-full p-6"
                "[&>svg]:w-10 [&>svg]:h-10 text-brand"
                ToolUp.Platform.Icons.spinner
        | CustomLoader render -> render ()

    /// In-content variant of `loadingIndicator` for a module / panel
    /// loading state — the shell's full-area two-column skeleton is too
    /// large inside a panel. `SkeletonLoader` degrades to a compact
    /// stacked-pulse placeholder; `BrandMarkLoader` / `SpinnerLoader`
    /// centre the matching icon at a smaller size; `CustomLoader` defers
    /// to the consumer-supplied thunk.
    let loadingIndicatorInline (mode: LoadingIndicatorMode) : ReactElement =
        match mode with
        | SkeletonLoader ->
            Html.div [
                prop.role "status"
                prop.ariaLabel "Loading"
                prop.className "space-y-3 py-8"
                prop.children [ pulseBlock "h-5 w-1/3"; pulseBlock "h-24 w-full"; pulseBlock "h-5 w-1/4" ]
            ]
        | BrandMarkLoader ->
            centredLoader
                "flex items-center justify-center py-10"
                "[&>svg]:w-12 [&>svg]:h-12"
                ToolUp.Platform.Icons.dataLoading
        | SpinnerLoader ->
            centredLoader
                "flex items-center justify-center py-10"
                "[&>svg]:w-8 [&>svg]:h-8 text-brand"
                ToolUp.Platform.Icons.spinner
        | CustomLoader render -> render ()

    /// Application shell - combines sidebar, header, main content, and optional side panel.
    /// `sections` is the structured sidebar layout (pinned / groups / other).
    /// `selectedModule` is the id of the currently active module.
    /// `content` is the active page's rendered body, whose `PageContent` case
    /// determines the inner layout (split panel, stacked column, full-width,
    /// named-area dashboard, or a custom element).
    /// sidePanel: when Some, rendered as a fixed panel on the right side of the screen.
    /// headerAction: when Some, rendered in the trailing position of the page header
    /// (used by features such as the AI assistant toggle; the shell owns the markup,
    /// the toolkit stays feature-agnostic).
    [<ReactComponent>]
    let AppShell
        (appName: string)
        (appLogo: string)
        (sections: Toolup.Sidebar.SidebarSection list)
        selectedModule
        onModuleSelected
        (onGroupToggled: string -> unit)
        (onPinToggled: string -> unit)
        (onReorder: string -> string list -> unit)
        (content: PageContent)
        (sidePanel: ReactElement option)
        (headerAction: ReactElement option)
        (inputsWidth: InputsPaneWidth)
        =
        let flatModules = Toolup.Sidebar.flatten sections

        // Empty-module shell: the accessible-modules filter (RBAC) can legitimately
        // resolve to an empty list for a user with no module permissions.
        // Render a placeholder rather than indexing into an empty list.
        if List.isEmpty flatModules then
            Html.section [
                prop.className "h-screen w-screen flex items-center justify-center bg-white"
                prop.children [
                    Html.div [
                        prop.className "text-center text-gray-500"
                        prop.children [
                            Html.h1 [ prop.className "text-xl font-semibold mb-2"; prop.text appName ]
                            Html.p [ prop.text "No modules are available for this account." ]
                        ]
                    ]
                ]
            ]
        else
            let selected =
                flatModules
                |> List.tryFind (fun m -> m.Id = selectedModule)
                |> Option.defaultValue flatModules[0]

            Html.section [
                prop.className "h-screen w-screen relative overflow-hidden"
                prop.children [
                    Html.div [ prop.className "absolute inset-0 bg-white" ]

                    Html.div [
                        prop.className "relative z-10 h-full w-full"
                        prop.children [
                            // Sidebar - absolutely positioned
                            Toolup.Sidebar.Sidebar
                                appName
                                appLogo
                                sections
                                selectedModule
                                onModuleSelected
                                onGroupToggled
                                onPinToggled
                                onReorder

                            // Main content area - header + content with left padding for sidebar
                            // When side panel is open, add right padding to avoid overlap.
                            // The padding width tracks the user-resizable AI panel via a
                            // CSS custom property the panel writes on its <html> root;
                            // 380px is the fallback for the first render before the
                            // panel's useEffect has fired (and for any consumer that
                            // ships its own non-AI side panel without setting the var).
                            Html.div [
                                prop.className [
                                    "flex flex-col h-full pl-20"
                                    if sidePanel.IsSome then
                                        "pr-[var(--toolup-ai-panel-width,_380px)]"
                                ]
                                prop.children [
                                    pageHeader (selected.Name, selected.Icon) headerAction

                                    Html.div [
                                        prop.className "flex-1 overflow-auto"
                                        prop.children [ renderPageContent inputsWidth content ]
                                    ]
                                ]
                            ]

                            // Side panel (conversation panel, rendered outside main content flow)
                            match sidePanel with
                            | Some panel -> panel
                            | None -> ()
                        ]
                    ]
                ]
            ]

    /// Panel component - container with background
    module Panel =

        /// `w-full` makes the panel respect its parent's allotted width
        /// rather than expand to its intrinsic content width. Paired with
        /// the `min-w-0 overflow-hidden` cap on the SplitPanel inputs
        /// wrapper, this gives the consumer's InputPanel a hard 24rem /
        /// 32rem ceiling and lets inner form rows / dividers shrink
        /// against the panel's `w-full` parent rather than push it wider.
        let panel (heading: string) (children: ReactElement list) =
            Html.div [
                prop.className $"w-full {Tokens.Bg.panel} {Tokens.Border.panel} p-6"
                prop.children [ Typography.heading heading; yield! children ]
            ]

        let panelSection (title: string) (children: ReactElement list) =
            Html.div [
                prop.children [
                    Typography.subheading title
                    Html.div [ prop.className "space-y-3"; prop.children children ]
                ]
            ]

    /// Tab navigation component
    module Tabs =

        /// Individual tab item
        let tab (name: string) (isSelected: bool) (onClick: unit -> unit) =
            Html.button [
                prop.onClick (fun _ -> onClick ())
                prop.className [
                    "px-6 py-3 text-base font-medium transition-all relative group"
                    if isSelected then
                        "text-brand"
                    else
                        "text-muted hover:text-text"
                ]
                prop.children [
                    Html.text name
                    // Bottom border indicator - thicker line positioned at bottom
                    Html.div [
                        prop.className [
                            "absolute bottom-0 left-0 right-0 h-[2px] transition-all"
                            if isSelected then
                                "bg-brand"
                            else
                                "bg-transparent group-hover:bg-brand/30"
                        ]
                    ]
                ]
            ]

        /// Tab container with multiple tabs
        let container (tabs: ReactElement list) =
            Html.div [ prop.className "flex gap-4"; prop.children tabs ]

        /// Helper to generate tabs from a list of (value, label) pairs
        let tabGroup (tabs: ('T * string) list) (selectedTab: 'T) (onTabChange: 'T -> unit) =
            container [
                for value, label in tabs do
                    tab label (selectedTab = value) (fun () -> onTabChange value)
            ]