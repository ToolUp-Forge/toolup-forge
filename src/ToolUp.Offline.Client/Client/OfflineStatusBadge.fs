// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Client.OfflineStatusBadge

open Feliz
open ToolUp.Offline

// ─── Phase 24 — connectivity / sync status indicator ─────────────────
//
// A fixed-position pill reporting online / offline / syncing /
// conflicts-pending. Deliberately small and deliberately always in the
// same place: a field user needs to answer "did my edit save?" without
// hunting, and a badge that moves with the layout does not answer it.
//
// **It renders nothing under `Online`.** A permanent green "Online"
// pill is chrome that teaches users to ignore the very element they
// must notice when it turns amber. The badge appears exactly when
// there is something to say.
//
// The status itself is derived by `SyncStatus.derive` in the Core
// tier — this component owns presentation only, so a host that wants
// its own badge reuses the same derivation and cannot disagree with
// this one about what "syncing" means.

/// Where the badge sits. Four corners; the default is bottom-left,
/// which is the corner least likely to collide with a shell's own
/// toast stack (conventionally bottom-right) or account menu (top-right).
type BadgePosition =
    | BottomLeft
    | BottomRight
    | TopLeft
    | TopRight

type StatusBadgeProps = {
    Status: SyncStatus
    Position: BadgePosition
    /// Invoked when the user clicks the badge — wire it to
    /// `Coordinator.SyncNow` so a user who can see the queue can also
    /// prod it. `None` renders a non-interactive pill.
    OnClick: (unit -> unit) option
}

module StatusBadgeProps =
    let create (status: SyncStatus) : StatusBadgeProps = {
        Status = status
        Position = BottomLeft
        OnClick = None
    }

/// Background / foreground for a status. Kept exhaustive: FS0025 is an
/// error tree-wide, so a new `SyncStatus` case cannot be added without
/// giving it a colour here.
let private palette (status: SyncStatus) : string * string =
    match status with
    | Online -> "#d1fae5", "#065f46"
    | Offline _ -> "#e5e7eb", "#374151"
    | Syncing _ -> "#dbeafe", "#1e40af"
    | ConflictsPending _ -> "#fef3c7", "#92400e"

let private positionStyles (position: BadgePosition) =
    match position with
    | BottomLeft -> [ style.bottom (length.px 16); style.left (length.px 16) ]
    | BottomRight -> [ style.bottom (length.px 16); style.right (length.px 16) ]
    | TopLeft -> [ style.top (length.px 16); style.left (length.px 16) ]
    | TopRight -> [ style.top (length.px 16); style.right (length.px 16) ]

/// Longer hover text. Says what will HAPPEN, not just what is true —
/// "queued, will send when you reconnect" is the sentence that stops a
/// user re-entering the same record.
let private tooltip (status: SyncStatus) : string =
    match status with
    | Online -> "All changes saved."
    | Offline 0 -> "No connection. Changes you make now are saved on this device and sent when you reconnect."
    | Offline n -> sprintf "No connection. %d change(s) saved on this device; they will send when you reconnect." n
    | Syncing n -> sprintf "Sending %d saved change(s) to the server." n
    | ConflictsPending n -> sprintf "%d offline edit(s) clash with someone else's changes and need your decision." n

/// The badge. Renders `Html.none` under `Online` — see the note at the
/// head of this file.
[<ReactComponent>]
let OfflineStatusBadge (props: StatusBadgeProps) =
    match props.Status with
    | Online -> Html.none
    | status ->
        let background, foreground = palette status
        let interactive = Option.isSome props.OnClick

        Html.div [
            prop.custom ("data-toolup-offline", "status-badge")
            prop.custom ("data-toolup-offline-status", SyncStatus.label status)
            prop.title (tooltip status)
            prop.role (if interactive then "button" else "status")
            if interactive then
                prop.tabIndex 0
            prop.style [
                yield! positionStyles props.Position
                style.position.fixedRelativeToWindow
                style.zIndex 1000
                style.display.flex
                style.alignItems.center
                style.gap (length.px 6)
                style.padding (length.px 6, length.px 12)
                style.borderRadius (length.em 1.0)
                style.fontSize (length.em 0.8)
                style.fontWeight 500
                style.backgroundColor background
                style.color foreground
                style.boxShadow (0, 1, 3, 0, "rgba(0,0,0,0.15)")
                if interactive then
                    style.cursor.pointer
            ]
            match props.OnClick with
            | Some onClick ->
                prop.onClick (fun _ -> onClick ())
                // Keyboard parity — a clickable div that a keyboard
                // cannot reach is an accessibility defect, and this one
                // is the only affordance for forcing a retry.
                prop.onKeyDown (fun e ->
                    if e.key = "Enter" || e.key = " " then
                        e.preventDefault ()
                        onClick ())
            | None -> ()
            prop.children [
                Html.span [
                    prop.style [
                        style.width (length.px 8)
                        style.height (length.px 8)
                        style.borderRadius (length.percent 50)
                        style.backgroundColor foreground
                        style.flexShrink 0
                    ]
                ]
                Html.span [ prop.text (SyncStatus.label status) ]
            ]
        ]