// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The session-security page as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `SessionSecurityUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// Every session row is kept, its ids come from the session's id and its
/// messages carry it. The page owns no hook, so nothing here is an island.
module internal SessionSecurityTree

open System
open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform
open SessionSecurityUI
open KeepScoped

/// A notice with its dismiss button.
let private notice (id: string) (msgs: SessionSecurityMessages) (notice: Notice option) (dismiss: Msg) =
    match notice with
    | Some(Notice.Problem text) -> [ Nodes.Actions.banner id ToneVariant.Critical text msgs.Dismiss dismiss ]
    | Some(Notice.Info text) -> [ Nodes.Actions.banner id ToneVariant.Info text msgs.Dismiss dismiss ]
    | None -> []

/// What every row shows besides its record: the catalog, the minute the
/// "last seen" labels are read against, whether a call is in flight, and the
/// row awaiting its confirmation.
type private RowContext = {
    Msgs: SessionSecurityMessages
    Now: DateTimeOffset
    Busy: bool
    Pending: string option
}

/// One session: its device and when it was last seen, then the action it
/// offers. A revoked session stays, marked, so a revoke is seen to have worked.
let private sessionRows =
    Rows(
        (fun (a: RowContext) b ->
            sameObject a.Msgs b.Msgs
            && a.Now = b.Now
            && a.Busy = b.Busy
            && a.Pending = b.Pending),
        fun context (record: SessionRecord) ->
            let msgs = context.Msgs
            let id = Nodes.Ids.keyed "session" record.SessionId
            let active = SessionRecord.isActive record

            Nodes.Layout.row id [
                Nodes.Layout.column (Nodes.Ids.child id "about") [
                    Nodes.text (Nodes.Ids.child id "device") record.DeviceDescriptor
                    Nodes.text
                        (Nodes.Ids.child id "seen")
                        (if active then
                             msgs.DeviceLastSeen
                                 record.AuthProvider
                                 (lastSeenLabelWith msgs context.Now record.LastSeenAt)
                         else
                             msgs.DeviceSignedOut record.AuthProvider)
                ]
                Nodes.Layout.row (Nodes.Ids.child id "actions") [
                    if not active then
                        Fuaran.badge (Nodes.Ids.child id "revoked") {
                            Label = Nodes.literal msgs.Revoked
                            Variant = BadgeVariant.Neutral
                        }
                    elif context.Pending = Some record.SessionId then
                        Nodes.Actions.button
                            (Nodes.Ids.child id "confirm")
                            ButtonVariant.Destructive
                            msgs.Confirm
                            context.Busy
                            (ConfirmRevoke record.SessionId)

                        Nodes.Actions.secondary (Nodes.Ids.child id "cancel") msgs.Cancel CancelRevoke
                    else
                        Nodes.Actions.button
                            (Nodes.Ids.child id "sign-out")
                            ButtonVariant.Secondary
                            msgs.SignOut
                            context.Busy
                            (AskRevoke record.SessionId)
                ]
            ]
    )

/// Sign out everywhere, behind a confirmation of its own: it is the one action
/// that signs the caller out of the page they are on.
let private everywhere (msgs: SessionSecurityMessages) (model: Model) : Node<Msg> list =
    if not (List.exists SessionRecord.isActive model.Sessions) then
        []
    elif model.PendingRevokeAll then
        [
            Nodes.Layout.column "sign-out-everywhere" [
                Fuaran.callout "sign-out-everywhere-warning" {
                    Defaults.callout with
                        Body = Nodes.literal msgs.SignOutEverywhereWarning
                        Tone = ToneVariant.Critical
                }
                Nodes.Layout.row "sign-out-everywhere-actions" [
                    Nodes.Actions.button
                        "sign-out-everywhere-confirm"
                        ButtonVariant.Destructive
                        msgs.SignOutEverywhere
                        model.Busy
                        ConfirmRevokeAll
                    Nodes.Actions.secondary "sign-out-everywhere-cancel" msgs.Cancel CancelRevokeAll
                ]
            ]
        ]
    else
        [
            Nodes.Actions.button
                "sign-out-everywhere"
                ButtonVariant.Secondary
                msgs.SignOutEverywhere
                model.Busy
                AskRevokeAll
        ]

/// The page, its "last seen" labels read against `now`. Every node sends its
/// message through the tree.
let tree (msgs: SessionSecurityMessages) (now: DateTimeOffset) (model: Model) : Node<Msg> =
    Nodes.Layout.column "session-security" [
        Nodes.heading "session-security-heading" 2 msgs.Heading
        Nodes.text "session-security-subheading" msgs.Subheading
        yield! notice "session-security-error" msgs model.Error DismissError
        yield! notice "session-security-status" msgs model.Status DismissStatus
        if not model.Loaded then
            Nodes.text "sessions-loading" msgs.Loading
        elif List.isEmpty model.Sessions then
            Nodes.text "sessions-none" msgs.EmptyState
        else
            Nodes.Layout.column
                "sessions"
                (sessionRows.List
                    {
                        Msgs = msgs
                        Now = now
                        Busy = model.Busy
                        Pending = model.PendingRevoke
                    }
                    model.Sessions)
        yield! everywhere msgs model
    ]

/// The minute `now` falls in: the labels are minute-grained, so the rows stay
/// kept across the dispatches of one minute.
let internal minuteOf (now: DateTimeOffset) : DateTimeOffset =
    DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset)

/// The deferred component `SessionSecurityUI`'s view mounts.
let Surface (props: SessionSecurityUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages (minuteOf DateTimeOffset.UtcNow) props.Model)
    )