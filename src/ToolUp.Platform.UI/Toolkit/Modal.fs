// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace Toolup.UIToolkit

open Feliz
open ToolUp.Platform.AriaProp

/// The words a confirmation dialog shows — every one the caller's, so the
/// toolkit names no wording and follows the caller's catalog.
type ConfirmMessages = {
    Title: string
    Confirm: string
    /// The confirm button's label while the action is in flight.
    Busy: string
    Cancel: string
}

/// The admin overlay: a dimmed backdrop and a centred card with a title, a
/// body and a row of actions. The caller owns whether it is open (it renders
/// the overlay or does not) and every string in it.
module Modal =

    /// `overlay title body actions` — `actions` render right-aligned under the body.
    let overlay (title: string) (body: ReactElement list) (actions: ReactElement list) : ReactElement =
        Html.div [
            prop.className "fixed inset-0 bg-black/40 flex items-center justify-center z-50"
            prop.children [
                Html.div [
                    prop.role "dialog"
                    ariaProp.modal true
                    prop.ariaLabel title
                    prop.className [
                        Tokens.Bg.card
                        "rounded-[var(--radius)] shadow-lg p-6 w-full max-w-md space-y-4"
                    ]
                    prop.children [
                        Html.h3 [
                            prop.className [ "text-lg font-semibold"; Tokens.Text.primary ]
                            prop.text title
                        ]
                        yield! body
                        if not actions.IsEmpty then
                            Html.div [ prop.className "flex justify-end gap-3 pt-2"; prop.children actions ]
                    ]
                ]
            ]
        ]

    /// A yes/no confirmation. While `busy` the confirm button shows
    /// `messages.Busy` and both buttons are inert, so a second click cannot
    /// re-send the action.
    let confirm
        (messages: ConfirmMessages)
        (prompt: ReactElement list)
        (onConfirm: unit -> unit)
        (onCancel: unit -> unit)
        (busy: bool)
        : ReactElement =
        let cancelState = if busy then Forms.Button.Disabled else Forms.Button.Enabled

        let confirmState =
            if busy then
                Forms.Button.Busy messages.Busy
            else
                Forms.Button.Enabled

        overlay messages.Title prompt [
            Forms.Button.secondaryWith cancelState messages.Cancel onCancel
            Forms.Button.primaryWith confirmState messages.Confirm onConfirm
        ]