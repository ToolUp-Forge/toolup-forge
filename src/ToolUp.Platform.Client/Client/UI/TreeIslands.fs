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