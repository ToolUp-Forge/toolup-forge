// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.Modal

open Feliz

type ModalProps = {
    IsOpen: bool
    Title: string option
    OnClose: unit -> unit
    Children: ReactElement list
}

let view (props: ModalProps) =
    if props.IsOpen then
        Html.div [
            // Modal backdrop
            prop.className "fixed inset-0 bg-black/50 z-30 flex items-center justify-center"
            prop.onClick (fun _ -> props.OnClose())
            prop.children [
                // Modal container
                Html.div [
                    prop.className "bg-white rounded-lg shadow-xl w-full max-w-md mx-4"
                    prop.onClick _.stopPropagation()
                    prop.children [
                        // Modal header (if title provided)
                        if props.Title.IsSome then
                            Html.div [
                                prop.className "border-b px-6 py-4"
                                prop.children [
                                    Html.h3 [ prop.className "text-lg font-medium"; prop.text props.Title.Value ]
                                ]
                            ]

                        // Modal content area with light gray background
                        Html.div [
                            prop.className "p-6"
                            prop.children [
                                Html.div [ prop.className "bg-gray-100 p-6 rounded-md"; prop.children props.Children ]
                            ]
                        ]

                        // Modal footer with Exit button
                        Html.div [
                            prop.className "px-6 py-4 flex justify-end"
                            prop.children [
                                Html.button [
                                    prop.className
                                        "bg-violet-500 hover:bg-violet-600 text-white py-2 px-6 rounded font-bold"
                                    prop.text "EXIT"
                                    prop.onClick (fun _ -> props.OnClose())
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]
    else
        Html.none