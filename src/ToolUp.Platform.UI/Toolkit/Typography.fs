// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Toolup
open Feliz
open Fable.Core.JsInterop
open Browser
open ToolUp.Platform

module Typography =

    let heading (text: string) =
        Html.h2 [ prop.className "text-xl font-semibold mb-4"; prop.text text ]

    let subheading (text: string) =
        Html.h3 [
            prop.className "text-lg font-medium mb-3 flex items-center"
            prop.children [ Html.span [ prop.text text ] ]
        ]

    let subSubheading (text: string) =
        Html.h4 [
            prop.className "text-lg font-medium mb-3 flex items-center max-w-prose"
            prop.children [ Html.span [ prop.text text ] ]
        ]

    let text (text: string) =
        Html.label [ prop.className $"text-base {Tokens.Text.primary}"; prop.text text ]

    [<ReactComponent>]
    let CopyableText (text: string) =
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

        Html.div [
            prop.className "flex items-start gap-2 max-w-3xl mb-3"
            prop.children [
                Html.h4 [
                    prop.className "text-lg font-medium flex items-center"
                    prop.children [ Html.span [ prop.text text ] ]
                ]
                Html.button [
                    prop.className (
                        "mt-1 transition-colors flex-shrink-0 "
                        + if copied then
                              "text-[var(--pos)]"
                          else
                              "text-[var(--muted)] hover:text-brand"
                    )
                    prop.title (if copied then "Copied!" else "Copy to clipboard")
                    prop.onClick (fun _ ->
                        Dom.window?navigator?clipboard?writeText (text) |> ignore
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
            ]
        ]

    [<ReactComponent>]
    let CopyableTextWithContext (visibleText: string) (hiddenContext: string) =
        let copied, setCopied = React.useState false
        let clipboardText = $"{hiddenContext}\n\n\"{visibleText}\""

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

        Html.div [
            prop.className "flex items-start gap-2 max-w-3xl mb-3"
            prop.children [
                Html.h4 [
                    prop.className "text-lg font-medium flex items-center"
                    prop.children [ Html.span [ prop.className "whitespace-pre-line"; prop.text visibleText ] ]
                ]
                Html.button [
                    prop.className (
                        "mt-1 transition-colors flex-shrink-0 "
                        + if copied then
                              "text-[var(--pos)]"
                          else
                              "text-[var(--muted)] hover:text-brand"
                    )
                    prop.title (
                        if copied then
                            "Copied!"
                        else
                            "Copy to clipboard with AI context"
                    )
                    prop.onClick (fun _ ->
                        Dom.window?navigator?clipboard?writeText (clipboardText) |> ignore
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
            ]
        ]