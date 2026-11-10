// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Feliz

/// The tab bar every admin page draws: one button per `(value, label)` pair,
/// the selected one underlined in the brand colour. The labels are the
/// caller's strings and the selection is the caller's state.
module Tabs =

    let private tab (label: string) (isSelected: bool) (onClick: unit -> unit) =
        Html.button [
            prop.type' "button"
            prop.role "tab"
            prop.ariaSelected isSelected
            prop.onClick (fun _ -> onClick ())
            prop.className [
                "px-4 py-2 text-sm font-medium border-b-2 transition-colors"
                if isSelected then
                    "border-brand text-brand"
                else
                    "border-transparent hover:text-[var(--text-strong)]"
                if not isSelected then
                    Tokens.Text.muted
            ]
            prop.text label
        ]

    /// `bar tabs selected onSelect` — `tabs` in display order.
    let bar (tabs: ('T * string) list) (selected: 'T) (onSelect: 'T -> unit) : ReactElement =
        Html.div [
            prop.role "tablist"
            prop.className "flex gap-1 border-b border-border"
            prop.children [
                for value, label in tabs do
                    tab label (selected = value) (fun () -> onSelect value)
            ]
        ]