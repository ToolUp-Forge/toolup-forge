// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.NarrativeRenderer

open Fable.Core.JsInterop
open Browser
open Feliz
open ToolUp.Platform
open ToolUp.Platform.Narrative
open Toolup.UIToolkit

module NarrativeCommit = Toolup.NarrativeCommit

let rec private renderSpan (span: InlineSpan) : ReactElement =
    match span with
    | Text s -> Html.span [ prop.text s ]
    | Emphasis s -> Html.em [ prop.className "italic text-gray-700"; prop.text s ]
    | Strong s -> Html.strong [ prop.className "font-semibold"; prop.text s ]
    | Code s ->
        Html.code [
            prop.className "font-mono text-[0.92em] px-1 py-[1px] bg-gray-100 rounded border border-gray-200"
            prop.text s
        ]
    | Metric(label, value) ->
        Html.span [
            prop.className "inline-flex items-baseline gap-1 font-mono text-[0.92em]"
            prop.children [
                Html.span [ prop.className "text-gray-500"; prop.text label ]
                Html.span [ prop.className "text-gray-800"; prop.text "=" ]
                Html.span [ prop.className "font-semibold text-gray-900"; prop.text value ]
            ]
        ]
    | Link(href, spans) ->
        Html.a [
            prop.href href
            prop.className "text-brand underline underline-offset-2 hover:text-brand/80"
            prop.children (renderSpans spans)
        ]
    | Image(src, alt, title) ->
        Html.img [
            prop.src src
            prop.alt alt
            prop.className "max-w-full h-auto rounded"
            match title with
            | Some t -> prop.title t
            | None -> ()
        ]
    | Br -> Html.br []

and private renderSpans (spans: InlineSpan list) : ReactElement list = spans |> List.map renderSpan

let private clampHeadingLevel (level: int) : int =
    if level < 3 then 3
    elif level > 6 then 6
    else level

let private calloutClasses (severity: Severity) : string =
    match severity with
    | Info -> "bg-blue-50 border-blue-200 text-blue-900"
    | Notice -> "bg-gray-50 border-gray-200 text-gray-800"
    | Warning -> "bg-amber-50 border-amber-300 text-amber-900"
    | Critical -> "bg-red-50 border-red-300 text-red-900"

let private renderElement (el: NarrativeElement) : ReactElement =
    match el with
    | Paragraph spans ->
        Html.p [
            prop.className "text-base text-gray-800 leading-relaxed"
            prop.children (renderSpans spans)
        ]
    | Heading(level, spans) ->
        // Tailwind sizes mirror the standard heading-cascade ratios; the
        // section heading uses `Typography.subSubheading` above (~H2
        // shape), so H3 stays one notch below that. Each branch
        // constructs the element inline because Feliz's `Html.hN` static
        // members are overload-resolved per call site — pulling the
        // member reference into a `let`-bound value makes overload
        // resolution ambiguous (FS0041).
        let props: IReactProperty list = [
            prop.className (
                match clampHeadingLevel level with
                | 3 -> "text-lg font-semibold text-gray-900"
                | 4 -> "text-base font-semibold text-gray-900"
                | 5 -> "text-sm font-semibold text-gray-800"
                | _ -> "text-sm font-semibold text-gray-700"
            )
            prop.children (renderSpans spans)
        ]

        match clampHeadingLevel level with
        | 3 -> Html.h3 props
        | 4 -> Html.h4 props
        | 5 -> Html.h5 props
        | _ -> Html.h6 props
    | BulletList items ->
        Html.ul [
            prop.className "list-disc list-outside pl-5 space-y-1 text-base text-gray-800"
            prop.children (items |> List.map (fun spans -> Html.li [ prop.children (renderSpans spans) ]))
        ]
    | OrderedList items ->
        Html.ol [
            prop.className "list-decimal list-outside pl-5 space-y-1 text-base text-gray-800"
            prop.children (items |> List.map (fun spans -> Html.li [ prop.children (renderSpans spans) ]))
        ]
    | KeyValueGrid pairs ->
        Html.div [
            prop.className "grid grid-cols-[max-content_1fr] gap-x-6 gap-y-1 text-sm"
            prop.children (
                pairs
                |> List.collect (fun (label, spans) -> [
                    Html.span [ prop.className "text-gray-600"; prop.text label ]
                    Html.span [
                        prop.className "font-medium text-gray-900"
                        prop.children (renderSpans spans)
                    ]
                ])
            )
        ]
    | Table(columns, rows) ->
        let alignClass (a: TableAlignment) =
            match a with
            | Left -> "text-left"
            | Right -> "text-right"
            | Center -> "text-center"

        Html.div [
            prop.className "overflow-x-auto"
            prop.children [
                Html.table [
                    prop.className "w-full text-sm border border-gray-200 border-collapse"
                    prop.children [
                        Html.thead [
                            prop.className "bg-gray-50"
                            prop.children [
                                Html.tr [
                                    prop.children (
                                        columns
                                        |> List.map (fun (header, align) ->
                                            Html.th [
                                                prop.className (
                                                    "px-3 py-2 font-semibold text-gray-700 border-b border-gray-200 "
                                                    + alignClass align
                                                )
                                                prop.text header
                                            ])
                                    )
                                ]
                            ]
                        ]
                        Html.tbody [
                            prop.children (
                                rows
                                |> List.mapi (fun rowIdx row ->
                                    Html.tr [
                                        prop.className (if rowIdx % 2 = 0 then "bg-white" else "bg-gray-50/50")
                                        prop.children (
                                            List.zip columns row
                                            |> List.map (fun ((_, align), cellSpans) ->
                                                Html.td [
                                                    prop.className (
                                                        "px-3 py-1.5 border-b border-gray-100 " + alignClass align
                                                    )
                                                    prop.children (renderSpans cellSpans)
                                                ])
                                        )
                                    ])
                            )
                        ]
                    ]
                ]
            ]
        ]
    | Callout(severity, spans) ->
        Html.div [
            prop.className (
                "border rounded-md px-3 py-2 text-base leading-relaxed "
                + calloutClasses severity
            )
            prop.children (renderSpans spans)
        ]
    | CodeBlock(language, content) ->
        let codeClass =
            match language with
            | Some lang -> "font-mono text-[0.85em] language-" + lang
            | None -> "font-mono text-[0.85em]"

        Html.pre [
            prop.className "bg-gray-50 border border-gray-200 rounded p-3 overflow-x-auto"
            prop.children [ Html.code [ prop.className codeClass; prop.text content ] ]
        ]
    | Blockquote(citation, spans) ->
        Html.blockquote [
            prop.className "border-l-4 border-gray-300 pl-4 italic text-gray-700"
            prop.children [
                Html.p [ prop.children (renderSpans spans) ]
                match citation with
                | Some c -> Html.cite [ prop.className "block text-sm text-gray-500 not-italic mt-1"; prop.text c ]
                | None -> ()
            ]
        ]
    | Divider -> Misc.divider

let private renderSection (section: NarrativeSection) : ReactElement =
    Html.section [
        prop.className "flex flex-col gap-2"
        prop.key section.Id
        prop.children [
            Typography.subSubheading section.Heading
            match section.Subheading with
            | Some sub -> Html.p [ prop.className "text-sm text-gray-500 -mt-2"; prop.text sub ]
            | None -> ()
            yield! section.Elements |> List.map renderElement
        ]
    ]

// ─── Save to Knowledge Base button ──────────────────────────────
//
// Shown only when (a) the document carries a `Provenance` (so the KB
// has a dedup key and a label), and (b) a `NarrativeCommit` handler
// has been mounted by the app shell (apps without a Knowledge Base
// skip the mount and the button hides itself).
//
// States: Idle → Submitting → (Succeeded | Failed reason | prompting
// for overwrite via the inline dialog). On Duplicate the dialog asks
// the user to confirm, and on confirmation we re-submit with
// overwrite=true.

type private SaveStatus =
    | Idle
    | Submitting
    | Succeeded
    | Failed of string

[<ReactComponent>]
let private SaveToKBButton (doc: NarrativeDocument) =
    let handler = NarrativeCommit.current ()

    match handler, doc.Provenance with
    | None, _
    | _, None -> Html.none
    | Some handler, Some _ ->
        let status, setStatus = React.useState Idle

        let duplicate, setDuplicate =
            React.useState (None: (string * System.DateTimeOffset) option)

        let submit (overwrite: bool) =
            setStatus Submitting

            async {
                let! result = handler.Submit doc overwrite

                match result with
                | NarrativeCommitResult.Committed _ ->
                    setDuplicate None
                    setStatus Succeeded
                | NarrativeCommitResult.Duplicate(fileName, generatedAt) ->
                    setDuplicate (Some(fileName, generatedAt))
                    setStatus Idle
                | NarrativeCommitResult.MissingProvenance ->
                    setStatus (Failed "This narrative has no provenance and cannot be saved.")
                | NarrativeCommitResult.Failed reason -> setStatus (Failed reason)
            }
            |> Async.StartImmediate

        // Reset Succeeded / Failed back to Idle after a short delay.
        React.useEffect (
            (fun () ->
                match status with
                | Succeeded ->
                    let timer = Dom.window.setTimeout ((fun () -> setStatus Idle), 2000)

                    { new System.IDisposable with
                        member _.Dispose() = Dom.window.clearTimeout timer
                    }
                | Failed _ ->
                    let timer = Dom.window.setTimeout ((fun () -> setStatus Idle), 4000)

                    { new System.IDisposable with
                        member _.Dispose() = Dom.window.clearTimeout timer
                    }
                | _ ->
                    { new System.IDisposable with
                        member _.Dispose() = ()
                    }),
            [| box status |]
        )

        let iconColourClass, titleText, isSubmitting =
            match status with
            | Succeeded -> "text-green-500", "Saved to Knowledge Base", false
            | Failed reason -> "text-red-500", reason, false
            | Submitting -> "text-gray-300", "Saving…", true
            | Idle -> "text-gray-400 hover:text-brand", "Save to Knowledge Base", false

        Html.div [
            prop.className "relative"
            prop.children [
                Html.button [
                    prop.className ("mt-1 transition-colors flex-shrink-0 " + iconColourClass)
                    prop.title titleText
                    prop.disabled isSubmitting
                    prop.onClick (fun _ -> submit false)
                    prop.children [
                        match status with
                        | Succeeded ->
                            Svg.svg [
                                svg.className "w-5 h-5"
                                svg.fill "none"
                                svg.stroke "currentColor"
                                svg.viewBox (0, 0, 24, 24)
                                svg.children [
                                    Svg.path [
                                        svg.custom ("strokeLinecap", "round")
                                        svg.custom ("strokeLinejoin", "round")
                                        svg.strokeWidth 2
                                        svg.d "M5 13l4 4L19 7"
                                    ]
                                ]
                            ]
                        | _ ->
                            // Archive-box / "save to knowledge base" icon (Heroicons outline).
                            Svg.svg [
                                svg.className "w-5 h-5"
                                svg.fill "none"
                                svg.stroke "currentColor"
                                svg.viewBox (0, 0, 24, 24)
                                svg.children [
                                    Svg.path [
                                        svg.custom ("strokeLinecap", "round")
                                        svg.custom ("strokeLinejoin", "round")
                                        svg.strokeWidth 2
                                        svg.d
                                            "M20 7l-.625 10.632A2.25 2.25 0 0117.128 19.75H6.872A2.25 2.25 0 014.625 17.632L4 7M10 11.25h4M3.375 7.5h17.25c.621 0 1.125-.504 1.125-1.125v-1.5c0-.621-.504-1.125-1.125-1.125H3.375c-.621 0-1.125.504-1.125 1.125v1.5c0 .621.504 1.125 1.125 1.125z"
                                    ]
                                ]
                            ]
                    ]
                ]

                match duplicate with
                | Some(_, generatedAt) ->
                    let when' = generatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")

                    Html.div [
                        prop.className "fixed inset-0 bg-black/50 z-40 flex items-center justify-center"
                        prop.onClick (fun _ -> setDuplicate None)
                        prop.children [
                            Html.div [
                                prop.className "bg-white rounded-lg shadow-xl w-full max-w-md mx-4"
                                prop.onClick _.stopPropagation()
                                prop.children [
                                    Html.div [
                                        prop.className "border-b px-6 py-4"
                                        prop.children [
                                            Html.h3 [
                                                prop.className "text-lg font-semibold text-gray-900"
                                                prop.text "Narrative already saved"
                                            ]
                                        ]
                                    ]
                                    Html.div [
                                        prop.className "px-6 py-4 text-sm text-gray-700 space-y-2"
                                        prop.children [
                                            Html.p [
                                                prop.text (
                                                    sprintf
                                                        "A previous version was saved to the Knowledge Base on %s."
                                                        when'
                                                )
                                            ]
                                            Html.p [
                                                prop.className "font-medium"
                                                prop.text "Overwrite it with the current version?"
                                            ]
                                        ]
                                    ]
                                    Html.div [
                                        prop.className "px-6 py-3 flex justify-end gap-2 border-t bg-gray-50"
                                        prop.children [
                                            Html.button [
                                                prop.className
                                                    "px-4 py-2 rounded bg-white border border-gray-300 hover:bg-gray-100 text-gray-800 text-sm font-medium"
                                                prop.text "Cancel"
                                                prop.onClick (fun _ -> setDuplicate None)
                                            ]
                                            Html.button [
                                                prop.className
                                                    "px-4 py-2 rounded bg-brand hover:bg-brand/80 text-white text-sm font-semibold"
                                                prop.text "Overwrite"
                                                prop.onClick (fun _ ->
                                                    setDuplicate None
                                                    submit true)
                                            ]
                                        ]
                                    ]
                                ]
                            ]
                        ]
                    ]
                | None -> ()
            ]
        ]

[<ReactComponent>]
let private CopyMarkdownButton (markdown: string) =
    let copied, setCopied = React.useState false

    React.useEffect (
        (fun () ->
            if copied then
                let timer = Dom.window.setTimeout ((fun () -> setCopied false), 2000)

                { new System.IDisposable with
                    member _.Dispose() = Dom.window.clearTimeout timer
                }
            else
                { new System.IDisposable with
                    member _.Dispose() = ()
                }),
        [| box copied |]
    )

    Html.button [
        prop.className (
            "mt-1 transition-colors flex-shrink-0 "
            + if copied then
                  "text-green-500"
              else
                  "text-gray-400 hover:text-brand"
        )
        prop.title (if copied then "Copied!" else "Copy as Markdown")
        prop.onClick (fun _ ->
            Dom.window?navigator?clipboard?writeText (markdown) |> ignore
            setCopied true)
        prop.children [
            if copied then
                Svg.svg [
                    svg.className "w-5 h-5"
                    svg.fill "none"
                    svg.stroke "currentColor"
                    svg.viewBox (0, 0, 24, 24)
                    svg.children [
                        Svg.path [
                            svg.custom ("strokeLinecap", "round")
                            svg.custom ("strokeLinejoin", "round")
                            svg.strokeWidth 2
                            svg.d "M5 13l4 4L19 7"
                        ]
                    ]
                ]
            else
                Svg.svg [
                    svg.className "w-5 h-5"
                    svg.fill "none"
                    svg.stroke "currentColor"
                    svg.viewBox (0, 0, 24, 24)
                    svg.children [
                        Svg.path [
                            svg.custom ("strokeLinecap", "round")
                            svg.custom ("strokeLinejoin", "round")
                            svg.strokeWidth 2
                            svg.d
                                "M8 5H6a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2v-1M8 5a2 2 0 002 2h2a2 2 0 002-2M8 5a2 2 0 012-2h2a2 2 0 012 2m0 0h2a2 2 0 012 2v3m2 4H10m0 0l3-3m-3 3l3 3"
                        ]
                    ]
                ]
        ]
    ]

/// Render a NarrativeDocument as a Feliz element tree.
let render (doc: NarrativeDocument) : ReactElement =
    let markdown = NarrativeMarkdown.render doc

    Html.div [
        prop.className "flex flex-col gap-5"
        prop.children [
            Html.div [
                prop.className "flex items-start justify-between gap-3"
                prop.children [
                    Html.div [
                        prop.className "flex flex-col gap-1"
                        prop.children [
                            Typography.heading doc.Title
                            match doc.Subtitle with
                            | Some sub -> Html.p [ prop.className "text-sm text-gray-500 -mt-3"; prop.text sub ]
                            | None -> ()
                        ]
                    ]
                    Html.div [
                        prop.className "flex items-start gap-2 flex-shrink-0"
                        prop.children [ SaveToKBButton doc; CopyMarkdownButton markdown ]
                    ]
                ]
            ]
            yield! doc.Sections |> List.map renderSection
        ]
    ]