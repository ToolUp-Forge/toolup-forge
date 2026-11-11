// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.Platform.DataProp

module Layout =
    /// The header's identity action (an account / user button), REGISTERED
    /// by the auth companion that owns it rather than imported here: the
    /// shell bundle references no vendor UI package (GP 2 / GP 13). The Clerk
    /// companion registers Clerk's `UserButton` when its provider wraps the
    /// app (`ClerkAuthUI.wrapApp`); the shell reads it with
    /// `headerIdentityAction` into `AppShellProps.IdentityAction`.
    ///
    /// A documented client-side mutable, like the shell's other companion
    /// registries: it is set once, when the auth UI wraps the shell, before
    /// the header first renders.
    let mutable private identityAction: (unit -> ReactElement) option = None

    /// Register (or replace) the header's identity action. Called by an
    /// auth companion when its provider mounts.
    let registerHeaderIdentityAction (render: unit -> ReactElement) : unit = identityAction <- Some render

    /// The registered identity action, rendered, or `None` when no companion
    /// registered one (Anonymous / dev modes, non-Clerk providers).
    let headerIdentityAction () : ReactElement option =
        identityAction |> Option.map (fun render -> render ())

    /// Page header: the page's title and icon, then the trailing slots — the
    /// caller's `headerAction` (e.g. the AI chat toggle; the shell knows which
    /// feature owns it, the toolkit stays feature-agnostic) and the
    /// `identityAction` an auth companion renders through.
    let pageHeader (title: string, icon) (headerAction: ReactElement option) (identityAction: ReactElement option) =
        Html.div [
            prop.className "w-full h-20 bg-[var(--surface)] border-b border-separator px-6 flex items-center"
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
                        match identityAction with
                        | Some el -> el
                        | None -> ()
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

    /// Phase 767 — the `role="status"` region every loading placeholder
    /// below renders into, with its accessible name read from the
    /// catalog. A component rather than a plain function because the
    /// name comes from `useMessages ()`, and the loaders themselves are
    /// plain functions called from the shell's `view` and from module
    /// views — a hook cannot run there, but it can run inside the element
    /// they return. Outside a provider the hook serves English, so a
    /// harness rendering a loader alone sees `aria-label="Loading"` as
    /// before.
    [<ReactComponent>]
    let private LoadingRegion (className: string) (children: ReactElement list) : ReactElement =
        let messages = (MessageCatalogProvider.useMessages ()).Shell

        Html.div [
            prop.role "status"
            prop.ariaLabel messages.Loading
            prop.className className
            prop.children children
        ]

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
        LoadingRegion "flex gap-6 p-6 min-h-full" [
            Html.div [
                prop.className "w-96 shrink-0 space-y-3"
                prop.children [ pulseBlock "h-8 w-3/4"; pulseBlock "h-32 w-full"; pulseBlock "h-10 w-1/2" ]
            ]
            Html.div [
                prop.className "flex-1 min-w-0 space-y-3"
                prop.children [ pulseBlock "h-8 w-1/4"; pulseBlock "h-96 w-full" ]
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
        LoadingRegion wrapperClasses [ Html.div [ prop.className svgSizeClasses; prop.children [ icon ] ] ]

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
            LoadingRegion "space-y-3 py-8" [ pulseBlock "h-5 w-1/3"; pulseBlock "h-24 w-full"; pulseBlock "h-5 w-1/4" ]
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

    /// The shell frame `AppShell` renders: the empty-shell placeholder, or
    /// the sidebar beside the header, the active page and the optional side
    /// panel. `sidebar` is a thunk so the empty branch builds no sidebar
    /// element at all.
    let private appShellFrame
        (messages: ShellMessages)
        (appName: string)
        (sections: Toolup.Sidebar.SidebarSection list)
        (selectedModule: string)
        (sidebar: unit -> ReactElement)
        (content: PageContent)
        (sidePanel: ReactElement option)
        (headerAction: ReactElement option)
        (identityAction: ReactElement option)
        (inputsWidth: InputsPaneWidth)
        : ReactElement =
        let flatModules = Toolup.Sidebar.flatten sections

        // Empty-module shell: the accessible-modules filter (RBAC) can legitimately
        // resolve to an empty list for a user with no module permissions.
        // Render a placeholder rather than indexing into an empty list.
        if List.isEmpty flatModules then
            Html.section [
                prop.className "h-screen w-screen flex items-center justify-center bg-[var(--surface)]"
                prop.children [
                    Html.div [
                        prop.className "text-center text-[var(--muted)]"
                        prop.children [
                            Html.h1 [ prop.className "text-xl font-semibold mb-2"; prop.text appName ]
                            Html.p [ prop.text messages.NoModulesAvailable ]
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
                    Html.div [ prop.className "absolute inset-0 bg-[var(--surface)]" ]

                    Html.div [
                        prop.className "relative z-10 h-full w-full"
                        prop.children [
                            // Sidebar - absolutely positioned
                            sidebar ()

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
                                    pageHeader (selected.Name, selected.Icon) headerAction identityAction

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

    // ─── The sidebar is its own memo boundary ────────────────────────────
    //
    // A sidebar built inline renders every time the shell does: a toast, a
    // theme flip or a header badge would redraw the whole navigation tree.
    // `AppShell` takes the sidebar's inputs
    // as ONE value and renders them behind a `React.memo` whose comparer
    // asks whether anything the sidebar renders from has changed — so a
    // shell render that leaves those inputs alone renders no sidebar.
    //
    // The comparer is a VALUE walk over the whole props record, not a list
    // of fields: strings and flags compare by value, the section/row
    // records and lists by content, and the two things the sidebar cannot
    // look inside compare by REFERENCE — callbacks, and React elements (the
    // row icons). Walking the record rather than naming its fields means a
    // field added to `SidebarProps` or to a sidebar row participates
    // without an edit here, so the failure mode of forgetting is an extra
    // render, never a stale sidebar. The sidebar's own state (hover, focus,
    // the roving stop) and the message catalog it reads through a context
    // are untouched by the boundary: React re-renders a memoised component
    // for its own state and for a context change regardless of its props.
    //
    // What makes the boundary hold is the caller's half of the contract:
    // the six callbacks must be the SAME functions from render to render
    // (the SDK shell builds them once per Elmish loop, in `Client.Dispatchers`
    // through `Program.withStableDispatch`), and the icons must be the elements the
    // modules declared rather than fresh ones. A caller that passes fresh
    // closures gets a correct sidebar that renders every time.

    /// Everything the sidebar renders from, as one value — the unit the
    /// sidebar's memo boundary compares (`AppShell`).
    /// `Sections` is the structured sidebar layout (pinned / groups /
    /// other); `SelectedModule` is the sidebar id of the active entry
    /// (bare module id, or the composite page id of a multi-page module).
    /// `OnHideToggled` receives the sidebar id the user hid (or restored
    /// from the "Hidden items" section) — a per-user preference, never an
    /// access change. Pass the SAME callback functions on every render:
    /// a callback compares by reference, so a fresh closure is a change.
    type SidebarProps = {
        /// The application name the rail shows beside its logo.
        AppName: string
        /// The logo image URL at the head of the rail.
        AppLogo: string
        /// The structured rail: pinned, placed, grouped and hidden sections.
        Sections: Toolup.Sidebar.SidebarSection list
        /// The sidebar id of the active entry.
        SelectedModule: string
        /// Receives the sidebar id of the entry the user chose.
        OnModuleSelected: string -> unit
        /// Receives the key of the section whose collapse state the user toggled.
        OnGroupToggled: string -> unit
        /// Receives the sidebar id the user pinned or unpinned.
        OnPinToggled: string -> unit
        /// Receives the id of the multi-page module the user expanded or collapsed.
        OnModuleToggled: string -> unit
        /// Receives the sidebar id the user hid or restored.
        OnHideToggled: string -> unit
        /// Receives a section key and its module ids in the user's new order.
        OnReorder: string -> string list -> unit
    }

    /// Value equality as the sidebar sees it: primitives by value (NaN
    /// equal to NaN), arrays and iterables (F# lists, maps, sets) element
    /// by element, records and unions field by field (same prototype, same
    /// own keys), dates by time — and functions and React elements by
    /// REFERENCE, because nothing about a different closure or a different
    /// element can be shown to render the same. Past a depth of 32 the
    /// answer is "changed": an extra render, never a stale sidebar.
    let private sameRenderedFn: obj =
        emitJsExpr
            ()
            "(function same(a, b, depth) {
                if (a === b) return true;
                if (a !== a && b !== b) return true;
                if (a == null || b == null || typeof a !== 'object' || typeof b !== 'object') return false;
                if (a.$$typeof !== undefined || b.$$typeof !== undefined) return false;
                if (depth > 32) return false;
                if (Object.getPrototypeOf(a) !== Object.getPrototypeOf(b)) return false;
                if (a instanceof Date) return a.getTime() === b.getTime();
                if (Array.isArray(a)) {
                    if (a.length !== b.length) return false;
                    for (var i = 0; i < a.length; i++) if (!same(a[i], b[i], depth + 1)) return false;
                    return true;
                }
                if (typeof a[Symbol.iterator] === 'function') {
                    var ia = a[Symbol.iterator](), ib = b[Symbol.iterator]();
                    for (;;) {
                        var x = ia.next(), y = ib.next();
                        if (x.done || y.done) return x.done === y.done;
                        if (!same(x.value, y.value, depth + 1)) return false;
                    }
                }
                var ka = Object.keys(a);
                if (ka.length !== Object.keys(b).length) return false;
                for (var j = 0; j < ka.length; j++) {
                    var k = ka[j];
                    if (!Object.prototype.hasOwnProperty.call(b, k) || !same(a[k], b[k], depth + 1)) return false;
                }
                return true;
            })"

    /// The comparison the sidebar's memo boundary runs (see
    /// `sameRenderedFn`). Internal so the Fable pack can pin it.
    let internal sameRendered (a: obj) (b: obj) : bool =
        emitJsExpr (sameRenderedFn, a, b) "$0($1, $2, 0)"

    [<Fable.Core.Import("memo", "react")>]
    let private reactMemo (render: obj, areEqual: obj) : obj = Fable.Core.Util.jsNative

    let private renderSidebar (props: obj) : ReactElement =
        let p = unbox<SidebarProps> props

        Toolup.Sidebar.Sidebar
            p.AppName
            p.AppLogo
            p.Sections
            p.SelectedModule
            p.OnModuleSelected
            p.OnGroupToggled
            p.OnPinToggled
            p.OnModuleToggled
            p.OnHideToggled
            p.OnReorder

    let private memoisedSidebar: obj =
        // The comparer goes to React as a two-argument JS function: a boxed
        // curried F# function may reach it curried, and React would read the
        // returned closure as "equal".
        reactMemo (box renderSidebar, box (System.Func<obj, obj, bool>(fun a b -> sameRendered a b)))

    /// Everything the application shell renders from, as one value.
    type AppShellProps = {
        /// The sidebar's inputs — the unit its memo boundary compares.
        Sidebar: SidebarProps
        /// The active page; its `PageContent` case decides the inner layout.
        Content: PageContent
        /// A fixed panel on the right of the screen (the AI assistant).
        SidePanel: ReactElement option
        /// The trailing action of the page header (the AI chat toggle).
        HeaderAction: ReactElement option
        /// The header's identity slot, which an auth companion renders
        /// through (Clerk's `UserButton`); read it with `headerIdentityAction`.
        IdentityAction: ReactElement option
        /// How wide a split panel's inputs pane is.
        InputsWidth: InputsPaneWidth
    }

    /// Application shell: the sidebar behind its own memo boundary, the page
    /// header with its two trailing slots, the active page's content and the
    /// optional side panel. The sidebar re-renders only when a value in
    /// `props.Sidebar` changes, not every time the shell does; the header
    /// reads the active entry's name and icon from `props.Sidebar.Sections`.
    [<ReactComponent>]
    let AppShell (props: AppShellProps) =
        let messages = (MessageCatalogProvider.useMessages ()).Shell

        appShellFrame
            messages
            props.Sidebar.AppName
            props.Sidebar.Sections
            props.Sidebar.SelectedModule
            (fun () -> ReactLegacy.createElement (unbox<ReactElement> memoisedSidebar, box props.Sidebar))
            props.Content
            props.SidePanel
            props.HeaderAction
            props.IdentityAction
            props.InputsWidth

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