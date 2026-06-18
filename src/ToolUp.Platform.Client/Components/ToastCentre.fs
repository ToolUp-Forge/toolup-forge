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

/// Defensive cap on a toast's body length. A `SystemMessage` is meant to
/// be a short, human-readable line. An upstream bug or a verbose server
/// error could carry an unbounded payload (a stack trace, or echoed file
/// contents from a failed upload), and rendering megabytes of text into a
/// fixed-size pop-up locks up the main thread. Clamp well below anything a
/// genuine message needs.
[<Literal>]
let private maxToastChars = 500

let private clampText (text: string) =
    if isNull text then ""
    elif text.Length <= maxToastChars then text
    else text.Substring(0, maxToastChars) + "…"

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
    // `useStateWithUpdater` (vs `useState`) so every mutation is a
    // functional update over the *latest* committed list — never a stale
    // closure capture. This is load-bearing: the subscription and the
    // auto-dismiss interval both mutate the list from callbacks that
    // outlive the render they were created in.
    let toasts, setToasts = React.useStateWithUpdater<ActiveToast list> []

    let dismiss (id: Guid) =
        setToasts (fun current -> current |> List.filter (fun t -> t.Id <> id))

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
                        Text = clampText text
                        DismissAt = dismissDeadline level DateTime.UtcNow
                    }

                    setToasts (fun current -> toast :: current)
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    // Auto-dismiss tick. Set up ONCE (`useEffectOnce`) — a single interval
    // for the component's lifetime. The functional updater reads the latest
    // list, so the interval never needs `toasts` in its closure and never
    // has to be torn down and recreated on every change.
    //
    // Critically, the updater returns the SAME reference when nothing
    // expired, so React bails out of the re-render. The previous version
    // had `[| box toasts |]` deps + `setToasts (List.filter ...)`, where
    // `List.filter` allocates a fresh list every tick even when nothing
    // changed — that fresh reference forced a re-render every second, which
    // re-ran the effect, which recreated the interval, forever. A
    // non-expiring `Error` toast (DismissAt = MaxValue) meant the list was
    // never empty, so the loop never stopped and could freeze the tab.
    React.useEffectOnce (fun () ->
        let intervalId =
            JS.setInterval
                (fun () ->
                    let now = DateTime.UtcNow

                    setToasts (fun current ->
                        let kept = current |> List.filter (fun t -> t.DismissAt > now)

                        if List.length kept = List.length current then
                            current
                        else
                            kept))
                1000

        FsReact.createDisposable (fun () -> JS.clearInterval intervalId))

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