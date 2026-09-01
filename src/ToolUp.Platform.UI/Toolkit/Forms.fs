// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform
open ToolUp.Platform.DataProp
open ProcessedDataTypes

module Forms =

    /// Phase 6g.D: wrap any element with `data-ai-name="$name"` so
    /// a companion's pulse-highlight (Phase 6g.B set_field /
    /// click_button decoder) can target it by name when the AI
    /// drives the matching field. Use this when the field / button
    /// is exposed to the AI via `withAIControllableField` /
    /// `withAIControllableButton` and you want the visual pulse
    /// confirmation when the AI changes it.
    ///
    /// The wrapper is a transparent `Html.div`; the
    /// `[data-ai-recently-changed]` keyframe in the deployment's
    /// `index.css` paints the box-shadow + background-tint
    /// animation. Layout-neutral (the wrapper inherits its child's
    /// natural width via Tailwind `w-full inline-block`).
    let aiNamed (name: string) (child: ReactElement) : ReactElement =
        Html.div [
            dataProp.aiName name
            prop.className "inline-block w-full"
            prop.children [ child ]
        ]

    /// Primary button component
    module Button =

        let primary (text: string) onClick =
            Html.button [
                prop.className [
                    Tokens.Colours.brand
                    Tokens.Colours.brandText
                    Tokens.Spacing.buttonPaddingX
                    Tokens.Spacing.buttonPaddingY
                    Tokens.Typography.buttonText
                    "rounded-[var(--radius)]"
                    "hover:bg-brand-dark"
                    "transition-colors"
                ]
                prop.text text
                prop.onClick (fun _ -> onClick ())
            ]

        let secondary (text: string) onClick =
            Html.button [
                prop.className [
                    Tokens.Colours.brand
                    Tokens.Colours.brandText
                    Tokens.Spacing.secondaryButtonPaddingX
                    Tokens.Spacing.secondaryButtonPaddingY
                    Tokens.Typography.buttonText
                    "rounded-[var(--radius)]"
                    "hover:bg-gray-200"
                    "transition-colors"
                ]
                prop.text text
                prop.onClick (fun _ -> onClick ())
            ]


    /// Text input component
    module Input =
        open Fable.Core.JsInterop

        [<ReactComponent>]
        let text (value: string) (onChange: string -> unit) (placeholder: string) =
            let displayValue, setDisplayValue = React.useState value

            let inputRef = React.useRef<Browser.Types.HTMLInputElement option> None

            React.useEffect (
                (fun () ->
                    match inputRef.current with
                    | Some el when Browser.Dom.document.activeElement = el -> ()
                    | _ -> setDisplayValue value),
                [| box value |]
            )

            let handleChange (s: string) = setDisplayValue s

            let handleKeyDown (e: Browser.Types.KeyboardEvent) =
                if e.key = "Enter" then
                    e.preventDefault ()
                    onChange displayValue

            // 0.5.5 — commit `displayValue` to the parent model on blur
            // instead of discarding it. The pre-0.5.5 reset (`setDisplayValue
            // value`) created a fatal interaction with submit buttons that
            // sit alongside the input: a `mouseDown` on the button moves
            // focus → fires `blur` on the input → resets the local state
            // back to the model's stale value → button's `click` fires
            // and dispatches against an empty model field. Symptom:
            // `TeamManagerUI` reported "Team name can't be empty" after
            // the user typed a name and clicked Create.
            //
            // Commit-on-blur preserves the "submit on Enter / button click"
            // convention (no per-keystroke Elmish dispatch) while also
            // capturing the typed value for any next-tick reader — the
            // useEffect above re-syncs `displayValue ← value` after a
            // successful submission resets the model field to "", so
            // the input visibly clears once the round-trip lands.
            let handleBlur () = onChange displayValue

            Html.input [
                prop.type' "text"
                prop.value displayValue
                prop.placeholder placeholder
                prop.onChange handleChange
                prop.onKeyDown handleKeyDown
                prop.onBlur (fun _ -> handleBlur ())
                prop.className [
                    "border border-border"
                    "rounded-[var(--radius)]"
                    "px-4 py-2"
                    "focus:outline-none focus:border-brand"
                    "transition-colors"
                    "w-full"
                ]
            ]


        [<ReactComponent>]
        let currency
            (value: decimal)
            (onChange: decimal -> unit)
            (placeholder: string)
            (prefix: string option)
            (suffix: string option)
            =

            let formatNumber (n: decimal) : string =
                (float n)?toLocaleString (
                    "en-GB",
                    {|
                        minimumFractionDigits = 2
                        maximumFractionDigits = 2
                    |}
                )

            let displayValue, setDisplayValue = React.useState (formatNumber value)

            // Ref to the input so we can detect focus and avoid
            // overwriting the display value while the user is typing.
            // Required because `value` (the external model prop)
            // updates on every keystroke when the parent mirrors state;
            // without the focus check, our `useEffect` would overwrite
            // user input mid-edit.
            let inputRef = React.useRef<Browser.Types.HTMLInputElement option> None

            // Sync displayValue whenever the external `value` changes,
            // but do not overwrite when the input is currently focused.
            React.useEffect (
                (fun () ->
                    match inputRef.current with
                    | Some el when Browser.Dom.document.activeElement = el -> ()
                    | _ -> setDisplayValue (formatNumber value)),
                [| box value |]
            )
            //*****************************

            // only update the visible text on change
            let handleChange (s: string) = setDisplayValue s

            // parse & commit value when Enter is pressed
            let handleKeyDown (e: Browser.Types.KeyboardEvent) =
                if e.key = "Enter" then
                    e.preventDefault ()
                    let target = e.target :?> Browser.Types.HTMLInputElement
                    let cleaned = target.value.Replace(",", "").Replace(" ", "")

                    if System.String.IsNullOrWhiteSpace(cleaned) then
                        onChange 0m
                    else
                        match System.Double.TryParse(cleaned) with
                        | true, n -> onChange (decimal n)
                        | false, _ -> ()

            let handleBlur () = setDisplayValue (formatNumber value)

            Html.div [
                prop.className [
                    "flex items-center gap-2"
                    "border border-border"
                    "rounded-[var(--radius)]"
                    "px-4 py-2"
                    "focus-within:border-brand"
                    "transition-colors"
                    "w-full"
                    "bg-[var(--surface)]"
                ]
                prop.children [
                    match prefix with
                    | Some p -> Html.span [ prop.className "text-muted"; prop.text p ]
                    | None -> Html.none

                    Html.input [
                        prop.type' "text"
                        prop.value displayValue
                        prop.placeholder placeholder
                        prop.onChange handleChange
                        prop.onKeyDown handleKeyDown
                        prop.onBlur (fun _ -> handleBlur ())
                        prop.className [ "flex-1"; "focus:outline-none"; "text-right"; "bg-[var(--surface)]" ]
                    ]

                    match suffix with
                    | Some s -> Html.span [ prop.className "text-muted"; prop.text s ]
                    | None -> Html.none
                ]
            ]


        [<ReactComponent>]
        let integerInput (value: int) (onChange: int -> unit) (placeholder: string) (includeCommas: bool) =

            let formatNumber (n: int) (includeCommas: bool) : string =
                if not includeCommas then
                    string n
                else
                    (int n)?toLocaleString ("en-GB")

            let displayValue, setDisplayValue =
                React.useState (formatNumber value includeCommas)

            // Ref to the input so we can detect focus and avoid
            // overwriting the display value while the user is typing.
            // Same pattern as the sibling `currency` component above.
            let inputRef = React.useRef<Browser.Types.HTMLInputElement option> None

            // Sync displayValue whenever the external `value` changes,
            // but do not overwrite when the input is currently focused.
            React.useEffect (
                (fun () ->
                    match inputRef.current with
                    | Some el when Browser.Dom.document.activeElement = el -> ()
                    | _ -> setDisplayValue (formatNumber value includeCommas)),
                [| box value |]
            )
            //*****************************

            // only update the visible text on change
            let handleChange (s: string) = setDisplayValue s

            // parse & commit value when Enter is pressed
            let handleKeyDown (e: Browser.Types.KeyboardEvent) =
                if e.key = "Enter" then
                    e.preventDefault ()
                    let target = e.target :?> Browser.Types.HTMLInputElement
                    let cleaned = target.value.Replace(",", "").Replace(" ", "")

                    if System.String.IsNullOrWhiteSpace(cleaned) then
                        onChange 0
                    else
                        match System.Int32.TryParse(cleaned) with
                        | true, n -> onChange (int n)
                        | false, _ -> ()

            let handleBlur () =
                setDisplayValue (formatNumber value includeCommas)

            Html.div [
                prop.className [
                    "flex items-center gap-2"
                    "border border-border"
                    "rounded-[var(--radius)]"
                    "px-4 py-2"
                    "focus-within:border-brand"
                    "transition-colors"
                    "w-full"
                    "bg-[var(--surface)]"
                ]
                prop.children [
                    Html.input [
                        prop.type' "text"
                        prop.value displayValue
                        prop.placeholder placeholder
                        prop.onChange handleChange
                        prop.onKeyDown handleKeyDown
                        prop.onBlur (fun _ -> handleBlur ())
                        prop.className [ "flex-1"; "focus:outline-none"; "text-right"; "bg-[var(--surface)]" ]
                    ]
                ]
            ]

    /// Custom dropdown component with internal state.
    [<ReactComponent>]
    let dropdown (value: string) (onChange: string -> unit) (options: (string * string) list) =
        let isOpen, setIsOpen = React.useState (false)
        let dropdownRef = React.useRef<Browser.Types.HTMLDivElement option> (None)

        // Close on outside click
        React.useEffect (
            (fun () ->
                let handler (e: Browser.Types.Event) =
                    match dropdownRef.current with
                    | Some element when not (element.contains (e.target :?> Browser.Types.Node)) -> setIsOpen false
                    | _ -> ()

                // `removeEventListener` matches by REFERENCE — bind the JS
                // function once so add and remove receive the identical
                // object (Fable can emit a fresh wrapper per call site
                // otherwise, leaking one listener per mount).
                let listener: Browser.Types.Event -> unit = handler

                Browser.Dom.document.addEventListener ("mousedown", listener)
                FsReact.createDisposable (fun () -> Browser.Dom.document.removeEventListener ("mousedown", listener))),
            [||]
        )

        let selectedLabel =
            options
            |> List.tryFind (fun (v, _) -> v = value)
            |> Option.map snd
            |> Option.defaultValue value

        Html.div [
            prop.ref dropdownRef
            prop.className "relative"
            prop.children [
                // Trigger button
                Html.button [
                    prop.className [
                        "w-full"
                        "border border-border"
                        "rounded-[var(--radius)]"
                        "px-4 py-2"
                        "text-left"
                        "bg-[var(--surface)]"
                        "flex items-center justify-between"
                        "focus:outline-none focus:border-brand"
                        "transition-colors"
                    ]
                    prop.onClick (fun _ -> setIsOpen (not isOpen))
                    prop.children [
                        Html.span selectedLabel
                        Svg.svg [
                            svg.className "ml-2 text-brand"
                            svg.width 12
                            svg.height 8
                            svg.viewBox (0, 0, 12, 8)
                            svg.fill "currentColor"
                            svg.children [
                                if isOpen then
                                    Svg.path [ svg.d "M6 0L0 6h12L6 0z" ]
                                else
                                    Svg.path [ svg.d "M6 8L12 2H0l6 6z" ]
                            ]
                        ]
                    ]
                ]

                // Options panel
                if isOpen then
                    Html.div [
                        prop.className [
                            "absolute z-10"
                            "w-full top-0"
                            "bg-[var(--surface)]"
                            "border border-border"
                            "rounded-[var(--radius)]"
                            "max-h-60 overflow-auto"
                            "text-[var(--text-strong)]"
                        ]
                        prop.children [
                            for (optValue, optLabel) in options do
                                Html.div [
                                    prop.className [
                                        "px-4 py-2"
                                        "cursor-pointer"
                                        "hover:bg-gray-100"
                                        if optValue = value then
                                            "bg-gray-50"
                                    ]
                                    prop.text optLabel
                                    prop.onClick (fun _ ->
                                        onChange optValue
                                        setIsOpen false)
                                ]
                        ]
                    ]
            ]
        ]

    /// Checkbox component
    let checkbox (isChecked: bool) (onChange: bool -> unit) =
        Html.input [
            prop.type' "checkbox"
            prop.isChecked isChecked
            prop.onChange onChange
            prop.className "w-5 h-5 accent-brand border-border rounded focus:ring-2 focus:ring-brand cursor-pointer"
        ]

    /// File upload component
    let fileUpload
        (label: string)
        (fileName: string option)
        (placeholder: string)
        (onFileSelected: Browser.Types.File -> unit)
        =

        let fileInputId = "file-input-" + label.Replace(" ", "-").ToLower()

        Html.div [
            prop.className "flex items-center gap-4 flex-nowrap"
            prop.children [
                // Label
                Html.label [
                    prop.className "w-48 text-base text-[var(--text-strong)] flex-shrink-0"
                    prop.text label
                ]

                // Hidden file input
                Html.input [
                    prop.type' "file"
                    prop.accept ".csv"
                    prop.className "hidden"
                    prop.id fileInputId
                    prop.onClick (fun e ->
                        let input = e.target :?> Browser.Types.HTMLInputElement
                        input.value <- "")
                    prop.onChange (fun (files: Browser.Types.File list) ->
                        match files with
                        | file :: _ -> onFileSelected file
                        | [] -> ())
                ]

                // Choose file button
                Html.label [
                    prop.htmlFor fileInputId
                    prop.className [
                        "cursor-pointer"
                        Tokens.Colours.brand
                        Tokens.Colours.brandText
                        "px-6 py-2.5"
                        Tokens.Typography.buttonText
                        "rounded-[var(--radius)]"
                        "hover:bg-brand-dark"
                        "transition-colors"
                        "inline-block"
                        "text-center"
                        "whitespace-nowrap"
                        "flex-shrink-0"
                    ]
                    prop.text "CHOOSE FILE"
                ]

                // File name or placeholder
                Html.div [
                    prop.className "flex-1 flex items-center gap-2"
                    prop.children [
                        match fileName with
                        | Some name ->
                            Html.span [ prop.className "text-[var(--pos)] text-base"; prop.text "✓" ]
                            Html.span [ prop.className "text-base text-[var(--text-strong)]"; prop.text name ]
                        | None -> Html.span [ prop.className "text-base text-[var(--muted)]"; prop.text placeholder ]
                    ]
                ]
            ]
        ]

    /// Data source picker - dropdown to select a processed data file
    let dataSourcePicker
        (label: string)
        (selectedFile: string option)
        (placeholder: string)
        (options: DataSourceOption list)
        (onSelected: string option -> unit)
        =

        Html.div [
            prop.className "flex items-center gap-4 flex-nowrap"
            prop.children [
                Html.label [
                    prop.className "w-48 text-base text-[var(--text-strong)] flex-shrink-0"
                    prop.text label
                ]

                match options with
                | [] ->
                    Html.span [
                        prop.className "text-base text-[var(--muted)] italic"
                        prop.text "No data files available — upload via Data Manager"
                    ]
                | _ ->
                    let dropdownOptions =
                        ("", placeholder) :: (options |> List.map (fun o -> o.FileName, o.FileName))

                    dropdown
                        (selectedFile |> Option.defaultValue "")
                        (fun v -> if v = "" then onSelected None else onSelected (Some v))
                        dropdownOptions
            ]
        ]

    /// Form field wrapper - combines label + input horizontally
    module Field =
        let field (labelText: string) (input: ReactElement) =
            Html.div [
                prop.className "flex items-center gap-4 mb-4"
                prop.children [
                    Html.div [ prop.className "w-56"; prop.children [ Typography.text labelText ] ]
                    Html.div [ prop.className "flex-1"; prop.children [ input ] ]
                ]
            ]

        let actions (buttons: ReactElement list) =
            Html.div [
                prop.className "flex items-center gap-4 mb-4"
                prop.children [
                    Html.div [ prop.className "w-48" ] // Empty space for label alignment
                    Html.div [ prop.className "flex-1 flex justify-end"; prop.children buttons ]
                ]
            ]


module FilePicker =

    open Feliz
    open Browser.Types

    type FilePickerProps = {
        AllowMultiple: bool
        Accept: string
        OnFilesSelected: File list -> unit
        Trigger: ReactElement
    }

    [<ReactComponent>]
    let FilePicker (allowMultiple: bool, accept: string, onFilesSelected: File list -> unit, trigger: ReactElement) =
        let inputId = React.useMemo (fun () -> System.Guid.NewGuid().ToString "N")

        Html.div [
            Html.input [
                prop.type' "file"
                prop.className "sr-only"
                prop.id inputId
                prop.accept accept
                prop.multiple allowMultiple
                prop.onClick (fun e ->
                    let input = e.target :?> HTMLInputElement
                    input.value <- "")
                prop.onChange (fun (files: File list) -> onFilesSelected files)
            ]

            Html.label [ prop.htmlFor inputId; prop.children [ trigger ] ]
        ]