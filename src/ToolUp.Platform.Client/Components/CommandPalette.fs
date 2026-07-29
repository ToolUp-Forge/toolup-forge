// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.CommandPalette

open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AriaProp
open ToolUp.Platform.DataProp

// ─── Phase 571 — the Ctrl+K command palette ──────────────────────────
//
// With nested multi-page modules and the Phase 567 administration area,
// page discovery stops being "scan the rail" and becomes "search it".
// This is the search surface: one overlay, one keybinding, keyboard-
// first, over exactly the pages the caller could have clicked to.
//
// **The entries are not this component's business.** They arrive as a
// prop, derived by the shell from `CommandPaletteNav.candidates`, which
// is `SidebarVisibility.visible` plus a page expansion. That is the
// whole reason a palette is safe to ship: it cannot enumerate a module
// the rail hides, because it never sees one. Read
// `Client/CommandPaletteNav.fs`'s header for why the derivation lives
// there rather than here.
//
// **Elmish-native.** Open/query/highlight live on the shell model and
// arrive as props; every interaction is a callback into `dispatch`. No
// portal library, no component-local navigation state, and `update`
// stays the single place the shell's selection changes — selecting an
// entry dispatches the same `ModuleSelected` message a sidebar click
// does (Phase 12b semantics preserved, including the same-module page
// switch that does not re-run `Init`).
//
// **Zero weight when off (GP 13).** The shell renders `Html.none` for
// `NoCommandPalette` — this component is never mounted, so the document
// keydown listener below is never registered and Ctrl+K keeps whatever
// meaning the browser gave it.
//
// Theming: every rendered part carries a stable `data-toolup-palette-*`
// attribute and draws its colours from the client-toolkit tokens
// (`--surface` / `--text-strong` / `--text` / `--muted` / `--radius`),
// so a deployment re-skins it from CSS without forking the component.

/// One navigable row. The shell builds these from
/// `CommandPaletteNav.PaletteCandidate` plus the icon the sidebar would
/// have rendered for the same destination — icons cannot live in the
/// pure candidate type, which has to stay .NET-constructible for the
/// contract pack.
type CommandPaletteEntry = {
    /// Composite sidebar id — dispatched verbatim as `ModuleSelected`.
    SidebarId: string
    ModuleName: string
    /// `Some title` for a page of a multi-page module; `None` for a leaf.
    PageTitle: string option
    /// Group (or, under `SeparateArea`, the area) label rendered as the
    /// row's context.
    Group: string option
    Icon: ReactElement
}

type CommandPaletteProps = {
    IsOpen: bool
    Query: string
    /// The shell model's unbounded highlight counter — resolved to a row
    /// index here, where the filtered count is known
    /// (`CommandPaletteNav.highlightIndex`).
    Highlight: int
    /// Every destination the caller may reach, in rail order.
    Entries: CommandPaletteEntry list
    OnOpen: unit -> unit
    OnDismiss: unit -> unit
    OnQueryChanged: string -> unit
    /// `+1` / `-1`; wrapping is the renderer's job.
    OnHighlightMoved: int -> unit
    OnSelect: string -> unit
}

/// The text a query matches against — module name for a leaf, module
/// name + page title for a page. Mirrors
/// `CommandPaletteNav.searchText` over the render-tier record.
let private entryText (e: CommandPaletteEntry) : string =
    match e.PageTitle with
    | Some title -> e.ModuleName + " " + title
    | None -> e.ModuleName

/// Is the event's target a place where the user is typing?
///
/// Ctrl+K is a text-editing chord in several editors and in every
/// readline-style input, so hijacking it while the caret is in a form
/// field would break the field to make a shortcut work. The palette's
/// own input is caught by this too, which is intended — Escape closes
/// the palette; Ctrl+K is only ever an opener.
let private isTextEntryTarget (target: obj) : bool =
    if isNull target then
        false
    else
        let tag: string = target?tagName

        if isNull (box tag) then
            false
        else
            // `tagName` is upper-case for HTML elements. `select` is in
            // the list because a type-ahead select consumes keystrokes
            // the same way.
            let editable: bool = target?isContentEditable
            tag = "INPUT" || tag = "TEXTAREA" || tag = "SELECT" || editable = true

/// The row a caller is about to activate, and the rows around it.
[<ReactComponent>]
let CommandPalette (props: CommandPaletteProps) =
    let inputRef = React.useRef<Browser.Types.HTMLInputElement option> None
    let listRef = React.useRef<Browser.Types.HTMLDivElement option> None

    // The document listener is attached ONCE (deps `[||]`) so the
    // shortcut does not churn a listener per render. It therefore must
    // not close over `props` — a mount-time capture would keep calling
    // the first render's `dispatch`. This ref is refreshed after every
    // render and read at event time instead.
    let latest = React.useRef props
    React.useEffect (fun () -> latest.current <- props)

    React.useEffect (
        (fun () ->
            let onKeyDown (e: Browser.Types.Event) =
                let ke = e :?> Browser.Types.KeyboardEvent

                // Cmd+K on macOS, Ctrl+K elsewhere. Alt is excluded so
                // Ctrl+Alt+K (an AltGr composition on some layouts)
                // stays the layout's business.
                let isPaletteChord =
                    (ke.ctrlKey || ke.metaKey) && not ke.altKey && ke.key.ToLower() = "k"

                if isPaletteChord && not (isTextEntryTarget (box e.target)) then
                    ke.preventDefault ()
                    latest.current.OnOpen()

            // `removeEventListener` matches by REFERENCE, so both calls
            // must be handed the identical function object. Passing an
            // `unbox`ed F# lambda makes Fable emit a fresh
            // `(arg) => onKeyDown(arg)` wrapper at each call site — the
            // add succeeds, the remove silently matches nothing, and the
            // listener outlives every unmount. Binding the JS function
            // once and passing that binding twice is what keeps the two
            // references equal; verified in the emitted
            // `output/src/ToolUp.Platform.Client/Components/CommandPalette.js`.
            let listener: Browser.Types.Event -> unit = onKeyDown

            Browser.Dom.document.addEventListener ("keydown", listener)

            (fun () -> Browser.Dom.document.removeEventListener ("keydown", listener))),
        [||]
    )

    // Focus the query box whenever the overlay opens — a palette that
    // opens without the caret in it is a palette the user has to click.
    React.useEffect (
        (fun () ->
            if props.IsOpen then
                inputRef.current |> Option.iter _.focus()),
        [| box props.IsOpen |]
    )

    let matches = CommandPaletteNav.rank entryText props.Query props.Entries
    let activeIndex = CommandPaletteNav.highlightIndex matches.Length props.Highlight

    // Keep the keyboard cursor visible in a long list. Same idiom as the
    // AI conversation panel's citation focus — query the marked row and
    // let the browser do the minimum scroll.
    React.useEffect (
        (fun () ->
            match listRef.current with
            | Some root when props.IsOpen ->
                let target = root?querySelector ($"[data-toolup-palette-index='{activeIndex}']")

                if not (isNull target) then
                    target?scrollIntoView ({| block = "nearest" |})
            | _ -> ()),
        [| box activeIndex; box props.IsOpen |]
    )

    let select (index: int) =
        if index >= 0 && index < matches.Length then
            props.OnSelect matches[index].SidebarId

    let onKeyDown (e: Browser.Types.KeyboardEvent) =
        match e.key with
        | "ArrowDown" ->
            e.preventDefault ()
            props.OnHighlightMoved 1
        | "ArrowUp" ->
            e.preventDefault ()
            props.OnHighlightMoved -1
        | "Enter" ->
            e.preventDefault ()
            select activeIndex
        | "Escape" ->
            e.preventDefault ()
            props.OnDismiss()
        | _ -> ()

    let row (index: int) (entry: CommandPaletteEntry) =
        let isActive = index = activeIndex

        Html.button [
            prop.key entry.SidebarId
            dataProp.custom "data-toolup-palette-index" (string index)
            ariaProp.selected isActive
            prop.className [
                "w-full flex items-center gap-3 text-left px-4 py-2.5"
                "rounded-[var(--radius)] transition-colors"
                if isActive then
                    "bg-brand/10 text-[var(--text-strong)]"
                else
                    "text-[var(--text)] hover:bg-black/5"
            ]
            // mouseEnter syncs the keyboard cursor to the pointer so
            // Enter always activates the row under the mouse.
            prop.onMouseEnter (fun _ -> props.OnHighlightMoved(index - activeIndex))
            prop.onClick (fun _ -> select index)
            prop.children [
                Html.span [ prop.className "w-4 h-4 shrink-0"; prop.children [ entry.Icon ] ]

                Html.span [
                    prop.className "flex-1 min-w-0"
                    prop.children [
                        Html.div [
                            prop.className "text-sm truncate"
                            prop.text (
                                match entry.PageTitle with
                                | Some title -> entry.ModuleName + " › " + title
                                | None -> entry.ModuleName
                            )
                        ]
                    ]
                ]

                match entry.Group with
                | Some group ->
                    Html.span [
                        dataProp.custom "data-toolup-palette-group" group
                        prop.className "text-xs text-[var(--muted)] shrink-0"
                        prop.text group
                    ]
                | None -> Html.none
            ]
        ]

    if not props.IsOpen then
        Html.none
    else
        Html.div [
            dataProp.custom "data-toolup-palette" "backdrop"
            prop.className "fixed inset-0 z-40 bg-black/40 flex items-start justify-center pt-[12vh] px-4"
            prop.onClick (fun _ -> props.OnDismiss())
            prop.children [
                Html.div [
                    dataProp.custom "data-toolup-palette" "panel"
                    ariaProp.role "dialog"
                    ariaProp.modal true
                    ariaProp.label "Command palette"
                    prop.className [
                        "w-full max-w-xl overflow-hidden"
                        "bg-[var(--surface)] border border-border rounded-[var(--radius)] shadow-xl"
                    ]
                    prop.onClick _.stopPropagation()
                    prop.children [
                        Html.div [
                            prop.className "border-b border-border"
                            prop.children [
                                Html.input [
                                    prop.ref inputRef
                                    dataProp.custom "data-toolup-palette" "input"
                                    ariaProp.label "Search pages"
                                    prop.type' "text"
                                    prop.value props.Query
                                    prop.placeholder "Jump to a page…"
                                    prop.autoComplete "off"
                                    prop.className [
                                        "w-full bg-transparent px-4 py-3"
                                        "text-[var(--text-strong)] placeholder:text-[var(--muted)]"
                                        "focus:outline-none"
                                    ]
                                    // Live filtering, so this is a
                                    // controlled input dispatching per
                                    // keystroke rather than the SDK's
                                    // usual commit-on-Enter shape: the
                                    // result list IS the feedback, and
                                    // there is nothing to commit.
                                    prop.onChange props.OnQueryChanged
                                    prop.onKeyDown onKeyDown
                                ]
                            ]
                        ]

                        Html.div [
                            prop.ref listRef
                            dataProp.custom "data-toolup-palette" "results"
                            ariaProp.role "listbox"
                            prop.className "max-h-80 overflow-y-auto p-2"
                            prop.children [
                                if List.isEmpty matches then
                                    Html.div [
                                        dataProp.custom "data-toolup-palette" "empty"
                                        prop.className "px-4 py-6 text-sm text-[var(--muted)] text-center"
                                        prop.text "No pages match that."
                                    ]
                                else
                                    for index, entry in List.indexed matches do
                                        row index entry
                            ]
                        ]

                        Html.div [
                            dataProp.custom "data-toolup-palette" "footer"
                            prop.className [
                                "flex items-center gap-4 px-4 py-2"
                                "border-t border-border text-xs text-[var(--muted)]"
                            ]
                            prop.children [
                                Html.span [ prop.text "↑↓ to move" ]
                                Html.span [ prop.text "↵ to open" ]
                                Html.span [ prop.text "esc to close" ]
                            ]
                        ]
                    ]
                ]
            ]
        ]