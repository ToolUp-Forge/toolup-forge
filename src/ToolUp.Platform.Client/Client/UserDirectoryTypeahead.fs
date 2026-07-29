// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.UserDirectoryTypeahead

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open Browser
open ToolUp.Platform
open Toolup.UIToolkit

// ─── 0.5.7 — Directory typeahead React component ─────────────────────
//
// Drop-in replacement for the plain `Forms.Input.text` used in the
// team-invite form, plus a suggestion dropdown driven by
// `IUserDirectoryApi.SearchUsers`. When no directory companion is
// wired server-side, the API returns `Ok []` and the dropdown stays
// empty — the input falls back to plain text behaviour.
//
// **Design:**
//   * Debounced (250ms) — every keystroke schedules a search; new
//     keystrokes cancel the pending search via setTimeout's handle.
//   * Minimum prefix of 2 characters (matches the server-side
//     EntraDirectory companion's minimum). Below threshold, the
//     dropdown stays closed and no API call is made.
//   * Click-outside closes the dropdown without committing.
//   * Click-on-suggestion writes the email into the input + dispatches
//     onChange + closes the dropdown. The parent reads the email out
//     of its own state on submit; no separate UserSummary path is
//     threaded through the form.
//
// The component is shaped to swap-in cleanly for `Forms.Input.text`
// in `TeamManagerUI.addMemberForm` and elsewhere. Future consumers
// (PermissionsAdminUI's per-member overrides, share-token issuance
// UIs) can adopt the same component.

// Module-level proxy. Single instance shared by every typeahead in the
// process; Fable.Remoting proxies are stateless so reuse is safe.
let private directoryApi: IUserDirectoryApi =
    Api.makeProxy<IUserDirectoryApi> (customOptions = UserSession.withRequestHeaders)

/// Debounced typeahead. `value` / `onChange` mirror `Forms.Input.text`;
/// when the user selects a directory suggestion, the suggestion's email
/// is written into the input through `onChange` and the typed prefix is
/// discarded. `placeholder` is rendered when the input is empty.
/// Pick the best human-readable label for a directory entry. Falls
/// back along display-name → email → user-id so the dropdown always
/// renders something meaningful even for partial entries.
let private summaryLabel (s: UserSummary) =
    match s.DisplayName with
    | Some n when not (System.String.IsNullOrWhiteSpace n) -> n
    | _ ->
        match s.Email with
        | Some e when not (System.String.IsNullOrWhiteSpace e) -> e
        | _ -> s.UserId

/// Email-preferred picker for invite-by-email flows. Returns the
/// directory entry's email when present, otherwise the user id —
/// either is a valid input to the team-invite handler (which detects
/// email format and routes through `IssuePendingInviteByEmail`).
let pickEmailPreferred (s: UserSummary) =
    match s.Email with
    | Some e when not (System.String.IsNullOrWhiteSpace e) -> e
    | _ -> s.UserId

/// User-id picker for flows that need the IdP's stable identifier
/// directly — e.g. `PlatformAdminApi.AssignPlatformAdmin`, which
/// stores the role keyed by user id. Picking the email here would
/// silently fail because the assign endpoint cannot resolve an email
/// back to its `oid`.
let pickUserId (s: UserSummary) = s.UserId

[<ReactComponent>]
let userTypeahead (value: string) (onChange: string -> unit) (placeholder: string) (pickValue: UserSummary -> string) =
    let displayValue, setDisplayValue = React.useState value
    let suggestions, setSuggestions = React.useState ([]: UserSummary list)
    let isOpen, setIsOpen = React.useState false
    let activeIndex, setActiveIndex = React.useState -1
    let containerRef = React.useRef<Browser.Types.HTMLDivElement option> None
    let timerRef = React.useRef<float option> None

    // Sync displayValue when the parent's value changes (e.g. after a
    // successful submit clears the model field to ""). Don't overwrite
    // while the user is mid-type — focus check mirrors the Forms.Input.text
    // useEffect pattern.
    React.useEffect (
        (fun () ->
            let active = Browser.Dom.document.activeElement

            match containerRef.current with
            | Some root when not (isNull active) && root.contains (active :?> Browser.Types.Node) -> ()
            | _ -> setDisplayValue value),
        [| box value |]
    )

    // Click-outside listener — closes the dropdown without committing.
    React.useEffect (
        (fun () ->
            let onClick (e: Browser.Types.Event) =
                match containerRef.current with
                | Some root when not (root.contains (e.target :?> Browser.Types.Node)) -> setIsOpen false
                | _ -> ()

            // `removeEventListener` matches by REFERENCE — bind the JS
            // function once and hand the identical binding to both calls
            // (an `unbox`ed lambda makes Fable emit a fresh wrapper per
            // call site, so the remove matches nothing and the listener
            // leaks one copy per mount). Same fix as CommandPalette.
            let listener: Browser.Types.Event -> unit = onClick

            Browser.Dom.document.addEventListener ("mousedown", listener)

            (fun () -> Browser.Dom.document.removeEventListener ("mousedown", listener))),
        [||]
    )

    // Cleanup the pending timer on unmount so an in-flight debounce
    // doesn't dispatch into a stale React tree.
    React.useEffectOnce (fun () ->
        (fun () ->
            match timerRef.current with
            | Some t -> Browser.Dom.window.clearTimeout t
            | None -> ()))

    let scheduleSearch (query: string) =
        // Cancel any pending timer.
        match timerRef.current with
        | Some t -> Browser.Dom.window.clearTimeout t
        | None -> ()

        timerRef.current <- None

        if query.Trim().Length < 2 then
            setSuggestions []
            setIsOpen false
            setActiveIndex -1
        else
            let handle =
                Browser.Dom.window.setTimeout (
                    (fun () ->
                        async {
                            let! result = directoryApi.SearchUsers(query.Trim(), 10)

                            match result with
                            | Ok matches ->
                                setSuggestions matches
                                setIsOpen (not matches.IsEmpty)
                                setActiveIndex -1
                            | Error _ ->
                                // Silent — render no suggestions, keep the input usable.
                                // Surfacing a Graph error inline would feel surprising;
                                // operators just type the email manually.
                                setSuggestions []
                                setIsOpen false
                                setActiveIndex -1
                        }
                        |> Async.StartImmediate),
                    250
                )

            timerRef.current <- Some handle

    let handleChange (s: string) =
        setDisplayValue s
        setActiveIndex -1
        scheduleSearch s

    let handleSelect (summary: UserSummary) =
        let resolved = pickValue summary
        setDisplayValue resolved
        onChange resolved
        setIsOpen false
        setSuggestions []
        setActiveIndex -1

    let handleKeyDown (e: Browser.Types.KeyboardEvent) =
        match e.key with
        | "ArrowDown" when isOpen && not suggestions.IsEmpty ->
            e.preventDefault ()
            let next = min (suggestions.Length - 1) (activeIndex + 1)
            setActiveIndex next
        | "ArrowUp" when isOpen && not suggestions.IsEmpty ->
            e.preventDefault ()
            // -1 keeps "no suggestion highlighted" reachable via ArrowUp from row 0.
            let next = max -1 (activeIndex - 1)
            setActiveIndex next
        | "Enter" when isOpen && activeIndex >= 0 && activeIndex < suggestions.Length ->
            e.preventDefault ()
            handleSelect suggestions[activeIndex]
        | "Enter" ->
            e.preventDefault ()
            setIsOpen false
            onChange displayValue
        | "Escape" ->
            setIsOpen false
            setActiveIndex -1
        | _ -> ()

    let handleBlur () =
        // Commit the typed value on blur — mirrors `Forms.Input.text`'s
        // commit-on-blur behaviour so the parent's submit button sees
        // the latest typed prefix even if the user blurs by clicking
        // straight onto the submit button.
        onChange displayValue

    let suggestionItem (i: int) (summary: UserSummary) =
        let isActive = i = activeIndex
        let label = summaryLabel summary

        // Secondary line — render the email when the primary label is
        // already the display name (so the row carries both). When the
        // label IS the email (no display name available), suppress the
        // secondary line to avoid showing the same string twice.
        let secondaryLine =
            match summary.DisplayName with
            | Some _ ->
                match summary.Email with
                | Some e when not (System.String.IsNullOrWhiteSpace e) ->
                    Html.div [ prop.className "text-xs text-muted"; prop.text e ]
                | _ -> Html.none
            | None -> Html.none

        Html.li [
            prop.key summary.UserId
            prop.className [
                "px-3 py-2"
                "cursor-pointer"
                "border-b border-gray-100 last:border-b-0"
                if isActive then "bg-brand/10" else "hover:bg-gray-100"
            ]
            // mouseDown fires before the input's blur — using mouseDown
            // instead of click ensures the selection lands before the
            // blur-driven commit-on-blur path runs.
            prop.onMouseDown (fun e ->
                e.preventDefault ()
                handleSelect summary)
            // mouseEnter syncs the keyboard cursor to the mouse so
            // ArrowDown / Enter behave intuitively after the operator
            // hovers a row.
            prop.onMouseEnter (fun _ -> setActiveIndex i)
            prop.children [
                Html.div [ prop.className "text-sm font-medium text-text"; prop.text label ]
                secondaryLine
            ]
        ]

    Html.div [
        prop.ref containerRef
        prop.className "relative w-full"
        prop.children [
            Html.input [
                prop.type' "text"
                prop.value displayValue
                prop.placeholder placeholder
                prop.onChange handleChange
                prop.onKeyDown handleKeyDown
                prop.onBlur (fun _ -> handleBlur ())
                prop.className [
                    "border border-border"
                    "rounded-lg"
                    "px-4 py-2"
                    "focus:outline-none focus:border-brand"
                    "transition-colors"
                    "w-full"
                ]
            ]

            if isOpen && not suggestions.IsEmpty then
                Html.ul [
                    prop.className [
                        "absolute z-10 mt-1 w-full"
                        "bg-white"
                        "border border-border rounded-lg shadow-md"
                        "max-h-64 overflow-auto"
                    ]
                    prop.children [ for i, s in List.indexed suggestions -> suggestionItem i s ]
                ]
            else
                Html.none
        ]
    ]