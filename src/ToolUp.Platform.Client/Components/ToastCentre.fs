// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Components.ToastCentre

open System
open Feliz
open Fable.Core
open ToolUp.Platform

/// One active toast. `DismissAt` is absolute UTC; the component's
/// timer-driven re-render removes any toast whose deadline has passed.
/// `Error`-level toasts use `DateTime.MaxValue` so they stay until
/// the user clicks them (auto-dismiss would be hostile for errors).
type private ActiveToast = {
    Id: Guid
    Level: SystemMessageLevel
    Text: string
    DismissAt: DateTime
}

/// Compute the absolute dismissal deadline for a toast. `Error` level
/// uses `DateTime.MaxValue` as a sentinel for "never" — the user must
/// click to dismiss. Auto-dismissing errors would be hostile.
/// `TimeSpan.MaxValue` is not Fable-compatible, so we build the
/// deadline directly rather than via `now + span`.
let private dismissDeadline (level: SystemMessageLevel) (now: DateTime) =
    match level with
    | SystemMessageLevel.Info -> now.AddSeconds 4.0
    | SystemMessageLevel.Warning -> now.AddSeconds 7.0
    | SystemMessageLevel.Error -> DateTime.MaxValue

let private toastClasses (level: SystemMessageLevel) =
    match level with
    | SystemMessageLevel.Info -> "bg-sky-50 border-sky-300 text-sky-900"
    | SystemMessageLevel.Warning -> "bg-amber-50 border-amber-400 text-amber-900"
    | SystemMessageLevel.Error -> "bg-red-50 border-red-400 text-red-900"

let private levelLabel (level: SystemMessageLevel) =
    match level with
    | SystemMessageLevel.Info -> "Info"
    | SystemMessageLevel.Warning -> "Warning"
    | SystemMessageLevel.Error -> "Error"

/// Fixed-position toast container. Subscribes to the generic
/// `NotificationClient` on mount and renders `SystemMessage` envelopes
/// as transient pop-ups. Other notification kinds pass through the
/// subscription unused — apps that want richer UI (job-completion
/// banners, refresh prompts) subscribe separately to the same stream.
///
/// Self-contained React state — not wired into the Elmish model. Toast
/// display is pure UI: the persisted source of truth already lives on
/// the server, so keeping the list in-component avoids polluting the
/// shell model and keeps dismissal local.
[<ReactComponent>]
let ToastCentre () =
    let toasts, setToasts = React.useState<ActiveToast list> []

    let dismiss (id: Guid) =
        setToasts (toasts |> List.filter (fun t -> t.Id <> id))

    // Subscribe on mount, unsubscribe on unmount. `useEffectOnce` runs
    // exactly once per component lifetime — the NotificationClient
    // opens a single EventSource that stays alive until the page is
    // closed or the user navigates away.
    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.SystemMessage(level, text) ->
                    let toast = {
                        Id = envelope.Id
                        Level = level
                        Text = text
                        DismissAt = dismissDeadline level DateTime.UtcNow
                    }

                    setToasts (toast :: toasts)
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    // Auto-dismiss tick. Fires every second; removes any toasts whose
    // deadline has passed. Cheap even with no toasts — the filter is
    // O(n) on a list that's almost always empty or single-digit.
    React.useEffect (
        (fun () ->
            let intervalId =
                JS.setInterval
                    (fun () ->
                        let now = DateTime.UtcNow
                        setToasts (toasts |> List.filter (fun t -> t.DismissAt > now)))
                    1000

            FsReact.createDisposable (fun () -> JS.clearInterval intervalId)),
        [| box toasts |]
    )

    if toasts.IsEmpty then
        Html.none
    else
        Html.div [
            prop.className "fixed top-4 right-4 z-50 flex flex-col gap-2 max-w-sm"
            prop.children (
                toasts
                |> List.map (fun toast ->
                    Html.div [
                        prop.key (string toast.Id)
                        prop.className $"border rounded shadow-lg px-4 py-3 cursor-pointer {toastClasses toast.Level}"
                        prop.onClick (fun _ -> dismiss toast.Id)
                        prop.children [
                            Html.div [
                                prop.className "text-xs font-semibold uppercase tracking-wide mb-1"
                                prop.text (levelLabel toast.Level)
                            ]
                            Html.div [ prop.className "text-sm"; prop.text toast.Text ]
                        ]
                    ])
            )
        ]