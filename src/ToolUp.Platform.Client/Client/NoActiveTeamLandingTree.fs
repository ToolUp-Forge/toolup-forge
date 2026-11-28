// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The no-active-team landing as a Fuaran-UI tree, built and drawn in a file of
/// its own, which `NoActiveTeamLandingUI`'s view mounts through
/// `TreeSurface.deferred` (the shell reads that module at boot; see
/// `TeamManagerTree` for the trade). Nothing imports this file statically.
///
/// The page holds no list: the deployment's heading and body, the outcome of
/// the last invitation check, and the button that checks again.
module internal ToolUp.Platform.NoActiveTeamLandingTree

open Fuaran.UI
open Fuaran.UI.Types
open Toolup.UIToolkit
open ToolUp.Platform.NoActiveTeamLandingUI

/// The outcome of the last check, as a line under the body. A failure is the
/// server's own sentence, echoed.
let private statusLine (msgs: NoActiveTeamLandingMessages) (state: CheckState) : Node<Msg> option =
    let line text = Some(Nodes.text "no-team-status" text)

    match state with
    | CheckState.Idle -> None
    | CheckState.Checking -> line msgs.Checking
    | CheckState.NothingPending -> line msgs.NothingPending
    | CheckState.Joined teamName -> line (msgs.Joined teamName)
    | CheckState.Failed message ->
        Some(
            Fuaran.callout "no-team-status" {
                Defaults.callout with
                    Body = Nodes.literal message
                    Tone = ToneVariant.Critical
            }
        )

/// The page: the deployment's heading and body (its words about its own
/// product, not the SDK's), the check's outcome, then the check button, inert
/// while a check is in flight.
let tree (msgs: NoActiveTeamLandingMessages) (title: string) (body: string) (model: Model) : Node<Msg> =
    let checking = model.State = CheckState.Checking

    Fuaran.box "no-team" {
        Defaults.box with
            Role = BoxRole.Card
            Layout = BoxLayout.Flex(Orientation.Vertical, false, Some 16)
            Children = [
                Nodes.heading "no-team-title" 2 title
                Nodes.text "no-team-body" body
                match statusLine msgs model.State with
                | Some line -> line
                | None -> ()
                Nodes.Layout.row "no-team-actions" [
                    Nodes.Actions.button
                        "no-team-check"
                        ButtonVariant.Primary
                        (if checking then msgs.Checking else msgs.CheckForInvitations)
                        checking
                        CheckInvites
                ]
            ]
    }

/// The deferred component `NoActiveTeamLandingUI`'s view mounts.
let Surface (props: NoActiveTeamLandingUI.SurfaceProps) : Fable.React.ReactElement =
    let host = {
        TreeLowering.TreeHost.ofSources HostBindingSources.empty with
            Locale = props.Locale
    }

    TreeLoweringRuntime.Tree(
        TreeLowering.treeProps host props.Dispatch (tree props.Messages props.Title props.Body props.Model)
    )