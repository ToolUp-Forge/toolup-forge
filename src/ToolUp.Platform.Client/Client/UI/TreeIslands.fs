// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The React leaves a tree view still draws as islands, in one place: the
/// controls the node vocabulary has no equivalent for yet. A text field holds
/// what is typed in a hook until the field is left or Enter is pressed, so
/// free typing does not round-trip through `update` (the renderer's local
/// buffer would replace it, and is not yet sound under a submit); a checkbox
/// outside a form is not a filter, so it is not a filter chip. Each is named
/// by the label that wraps it.
module internal TreeIslands

open Fuaran.UI.Types
open Feliz
open Toolup.UIToolkit

/// A text field under its visible label, sending `onChange` with what was
/// typed when the field is left or on Enter.
let textField
    (id: string)
    (label: string)
    (placeholder: string)
    (value: string)
    (onChange: string -> unit)
    : Node<'Msg> =
    TreeLowering.island id (fun () ->
        Html.label [
            prop.className "flex flex-col gap-1"
            prop.children [
                Html.span [ prop.className "text-xs font-medium"; prop.text label ]
                Forms.Input.text value onChange placeholder
            ]
        ])

/// `textField` whose label only assistive technology reads: for a field whose
/// placeholder is the only text a reader sees.
let hiddenLabelTextField
    (id: string)
    (label: string)
    (placeholder: string)
    (value: string)
    (onChange: string -> unit)
    : Node<'Msg> =
    TreeLowering.island id (fun () ->
        Html.label [
            prop.className "block"
            prop.children [
                Html.span [ prop.className "sr-only"; prop.text label ]
                Forms.Input.text value onChange placeholder
            ]
        ])

/// A checkbox with its label beside it, calling `onToggle` when it is toggled
/// and inert while `disabled`.
let checkbox (id: string) (label: string) (value: bool) (disabled: bool) (onToggle: unit -> unit) : Node<'Msg> =
    TreeLowering.island id (fun () ->
        Html.label [
            prop.className "inline-flex items-center gap-1 text-xs select-none"
            prop.children [
                Html.input [
                    prop.type' "checkbox"
                    prop.isChecked value
                    prop.disabled disabled
                    prop.onChange (fun (_: bool) -> onToggle ())
                ]
                Html.span [ prop.text label ]
            ]
        ])

/// A drop-down of `options` (value, text) under its visible label, styled as
/// `textField`'s label is, sending `onChange` with the chosen value: for a
/// choice inside a form whose other fields are `textField`s, where the node
/// vocabulary's select draws its label in a style of its own.
let select
    (id: string)
    (label: string)
    (options: (string * string) list)
    (value: string)
    (onChange: string -> unit)
    : Node<'Msg> =
    TreeLowering.island id (fun () ->
        Html.label [
            prop.className "flex flex-col gap-1"
            prop.children [
                Html.span [ prop.className "text-xs font-medium"; prop.text label ]
                Html.select [
                    prop.className "self-start border border-border rounded-[var(--radius)] px-4 py-2 text-sm"
                    prop.value value
                    prop.onChange onChange
                    prop.children [ for v, text in options -> Html.option [ prop.value v; prop.text text ] ]
                ]
            ]
        ])

/// One choice of `radios`: its label, what it means, whether it is the one
/// chosen, and what choosing it does.
type internal Radio = {
    Label: string
    Description: string
    Chosen: bool
    OnChoose: unit -> unit
}

/// A group of radio buttons named `name`, each a card naming its choice with
/// what it means under it: outside a form the vocabulary has no radio, and a
/// button per choice cannot say which one is chosen.
let radios (id: string) (name: string) (choices: Radio list) : Node<'Msg> =
    TreeLowering.island id (fun () ->
        Html.div [
            prop.className "flex flex-col gap-2"
            prop.children [
                for choice in choices ->
                    Html.label [
                        prop.className [
                            "flex items-start gap-2 p-3 border rounded cursor-pointer"
                            if choice.Chosen then
                                "border-brand bg-brand/5"
                            else
                                "border-border"
                        ]
                        prop.children [
                            Html.input [
                                prop.type' "radio"
                                prop.name name
                                prop.className "mt-1"
                                prop.isChecked choice.Chosen
                                prop.onChange (fun (_: bool) -> choice.OnChoose())
                            ]
                            Html.div [
                                prop.children [
                                    Html.div [ prop.className "text-sm font-medium"; prop.text choice.Label ]
                                    Html.div [ prop.className "text-xs text-muted"; prop.text choice.Description ]
                                ]
                            ]
                        ]
                    ]
            ]
        ])