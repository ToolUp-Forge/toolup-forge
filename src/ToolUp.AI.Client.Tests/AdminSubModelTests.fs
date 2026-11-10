module ToolUp.AI.Client.Tests.AdminSubModelTests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform

// ─── The admin sub-models, each driven without its parent ─────────────
//
// A modal, a confirmation or a wizard of an admin module is a sub-model:
// its own `Model`, `Msg` and `update`, held by the parent as an option and
// reached through one parent message with `Cmd.map`. Each list below calls
// a sub-model's `update` with no parent model in sight, then routes one of
// its messages through the parent's `update` to show the parent delegates.
//
// Fable-side for the reason `JobsAdminUITests` gives: the modules declare
// module-level `Api.makeProxy` values, which only build under Fable. The
// commands `update` returns are not executed.

// ── UserDirectory ──────────────────────────────────────────────────────

let private summary userId name email : UserSummary = {
    UserId = userId
    DisplayName = name
    Email = email
}

let private userDirectoryTests =
    testList "UserDirectory" [
        testCase "a resolved entry labels by name, then email, then the raw id"
        <| fun () ->
            let model, _ =
                UserDirectory.update
                    (UserDirectory.Resolved(
                        Ok [
                            summary "u1" (Some "Ada") (Some "ada@x.test")
                            summary "u2" None (Some "bo@x.test")
                        ]
                    ))
                    UserDirectory.empty

            Expect.equal (UserDirectory.label model "u1") "Ada" "the display name first"
            Expect.equal (UserDirectory.label model "u2") "bo@x.test" "the email when there is no name"
            Expect.equal (UserDirectory.label model "u3") "u3" "the raw id when unresolved"
            Expect.equal (UserDirectory.email model "u1") "ada@x.test" "email-first reading"

        testCase "a failed lookup leaves the entries as they were"
        <| fun () ->
            let model, _ =
                UserDirectory.update (UserDirectory.Resolved(Ok [ summary "u1" (Some "Ada") None ])) UserDirectory.empty

            let after, _ = UserDirectory.update (UserDirectory.Resolved(Error "unwired")) model
            Expect.equal after model "an error changes nothing"

        testCase "only blank-free, unresolved, distinct ids are asked for"
        <| fun () ->
            let model, _ =
                UserDirectory.update (UserDirectory.Resolved(Ok [ summary "u1" None None ])) UserDirectory.empty

            Expect.equal
                (UserDirectory.unresolved [ "u1"; ""; "u2"; "u2"; " " ] model)
                [ "u2" ]
                "known and blank ids are skipped"
    ]

// ── TeamManagerUI ──────────────────────────────────────────────────────

let private teamManagerTests =
    testList "TeamManagerUI" [
        testCase "TransferOwnership.update picks, steps back and closes with no TeamManager model"
        <| fun () ->
            let picked, _, outcome =
                TeamManagerUI.TransferOwnership.update
                    (TeamManagerUI.TransferOwnership.SelectCandidate "u2")
                    (TeamManagerUI.TransferOwnership.init "t1")

            Expect.equal picked.SelectedUserId (Some "u2") "the candidate is held"

            Expect.equal
                picked.Step
                TeamManagerUI.TransferOwnership.ConfirmTransfer
                "picking advances to the confirmation"

            Expect.equal outcome TeamManagerUI.TransferOwnership.Stay "the modal stays open"

            let back, _, _ =
                TeamManagerUI.TransferOwnership.update TeamManagerUI.TransferOwnership.BackToPick picked

            Expect.equal back.Step TeamManagerUI.TransferOwnership.PickNewOwner "back returns to the pick step"

            let _, _, closed =
                TeamManagerUI.TransferOwnership.update (TeamManagerUI.TransferOwnership.Done(Ok())) picked

            Expect.equal
                closed
                (TeamManagerUI.TransferOwnership.Transferred "t1")
                "a done transfer tells the parent which team to reload"

        testCase "a TransferMsg routed through TeamManagerUI.update reaches the transfer sub-model"
        <| fun () ->
            let parent, _ = TeamManagerUI.init ClientModuleContext.empty

            let opened, _ =
                TeamManagerUI.update (TeamManagerUI.OpenTransferOwnership "t1") parent

            let routed, _ =
                TeamManagerUI.update
                    (TeamManagerUI.TransferMsg(TeamManagerUI.TransferOwnership.SelectCandidate "u2"))
                    opened

            Expect.equal
                (routed.Transfer |> Option.bind _.SelectedUserId)
                (Some "u2")
                "the parent's transfer sub-model took the pick"

            let closed, _ =
                TeamManagerUI.update (TeamManagerUI.TransferMsg TeamManagerUI.TransferOwnership.Close) routed

            Expect.isNone closed.Transfer "the Closed outcome closes the modal"

        testCase "IssueByEmail.update validates, clamps and reports with no TeamManager model"
        <| fun () ->
            let start = TeamManagerUI.IssueByEmail.init "t1" "email required"

            let blank, _, _ =
                TeamManagerUI.IssueByEmail.update TeamManagerUI.IssueByEmail.Submit start

            Expect.equal blank.SubmitError (Some "email required") "a blank email is refused in the modal"
            Expect.isFalse blank.Submitting "nothing is sent"

            let clamped, _, _ =
                TeamManagerUI.IssueByEmail.update (TeamManagerUI.IssueByEmail.SetExpiresInDays 0) start

            Expect.equal clamped.ExpiresInDays 1 "the lifetime is at least a day"

            let sending, _, _ =
                TeamManagerUI.IssueByEmail.update TeamManagerUI.IssueByEmail.Submit { start with Email = " a@x.test " }

            Expect.isTrue sending.Submitting "a valid email is sent"

            let failed, _, stay =
                TeamManagerUI.IssueByEmail.update (TeamManagerUI.IssueByEmail.Submitted(Error "boom")) sending

            Expect.equal failed.SubmitError (Some "boom") "the handler's error stays in the modal"
            Expect.equal stay TeamManagerUI.IssueByEmail.Stay "the modal stays open for a retry"

            let _, _, issued =
                TeamManagerUI.IssueByEmail.update (TeamManagerUI.IssueByEmail.Submitted(Ok())) sending

            Expect.equal issued (TeamManagerUI.IssueByEmail.Issued "t1") "an issued invite tells the parent"

        testCase "RevokeByEmail.update confirms once and reports with no TeamManager model"
        <| fun () ->
            let start = TeamManagerUI.RevokeByEmail.init "t1" "a@x.test"

            let busy, _, _ =
                TeamManagerUI.RevokeByEmail.update TeamManagerUI.RevokeByEmail.Confirm start

            Expect.isTrue busy.Submitting "confirming sends the revoke"

            let again, _, _ =
                TeamManagerUI.RevokeByEmail.update TeamManagerUI.RevokeByEmail.Confirm busy

            Expect.equal again busy "a second confirm while in flight changes nothing"

            let _, _, revoked =
                TeamManagerUI.RevokeByEmail.update (TeamManagerUI.RevokeByEmail.Done(Ok())) busy

            Expect.equal revoked (TeamManagerUI.RevokeByEmail.Revoked "t1") "a revoke tells the parent"

            let _, _, failed =
                TeamManagerUI.RevokeByEmail.update (TeamManagerUI.RevokeByEmail.Done(Error "boom")) busy

            Expect.equal failed (TeamManagerUI.RevokeByEmail.Failed "boom") "a failure is handed to the parent"

            let _, _, cancelled =
                TeamManagerUI.RevokeByEmail.update TeamManagerUI.RevokeByEmail.Cancel start

            Expect.equal cancelled TeamManagerUI.RevokeByEmail.Closed "cancel closes"
    ]

// ── PlatformAdminUI ────────────────────────────────────────────────────

let private platformAdminTests =
    testList "PlatformAdminUI" [
        testCase "AssignConfirm: confirm goes in flight, its result ends it as an outcome"
        <| fun () ->
            let opened = PlatformAdminUI.AssignConfirm.init "u1"
            Expect.equal opened.Outcome PlatformAdminUI.AssignConfirm.Open "a fresh confirmation is open"

            let inFlight, _ =
                PlatformAdminUI.AssignConfirm.update PlatformAdminUI.AssignConfirm.Confirm opened

            Expect.isTrue inFlight.InFlight "confirm issues the grant and goes busy"

            let ignored, _ =
                PlatformAdminUI.AssignConfirm.update PlatformAdminUI.AssignConfirm.Cancel inFlight

            Expect.equal ignored inFlight "cancel is inert while the grant is in flight"

            let failed, _ =
                PlatformAdminUI.AssignConfirm.update (PlatformAdminUI.AssignConfirm.Resolved(Error "denied")) inFlight

            Expect.equal
                failed.Outcome
                (PlatformAdminUI.AssignConfirm.Done(Error "denied"))
                "the result is reported as the outcome"

            let cancelled, _ =
                PlatformAdminUI.AssignConfirm.update PlatformAdminUI.AssignConfirm.Cancel opened

            Expect.equal cancelled.Outcome PlatformAdminUI.AssignConfirm.Cancelled "cancel at rest closes"

        testCase "ConfirmDeleteTeam: confirm goes in flight, its result ends it as an outcome"
        <| fun () ->
            let opened = PlatformAdminUI.ConfirmDeleteTeam.init "t1" "Alpha"
            Expect.equal opened.TeamName "Alpha" "the prompt echoes the team name"

            let inFlight, _ =
                PlatformAdminUI.ConfirmDeleteTeam.update PlatformAdminUI.ConfirmDeleteTeam.Confirm opened

            Expect.isTrue inFlight.InFlight "confirm issues the delete and goes busy"

            let again, _ =
                PlatformAdminUI.ConfirmDeleteTeam.update PlatformAdminUI.ConfirmDeleteTeam.Confirm inFlight

            Expect.equal again inFlight "a second confirm while in flight is inert"

            let done', _ =
                PlatformAdminUI.ConfirmDeleteTeam.update (PlatformAdminUI.ConfirmDeleteTeam.Resolved(Ok())) inFlight

            Expect.equal
                done'.Outcome
                (PlatformAdminUI.ConfirmDeleteTeam.Done(Ok()))
                "success is reported as the outcome"

            Expect.isTrue (not done'.InFlight) "no longer busy once resolved"

        testCase "the parent routes a sub-model message to the open confirmation"
        <| fun () ->
            let parent, _ = PlatformAdminUI.init ClientModuleContext.empty

            let withAssign = {
                parent with
                    AssignConfirm = Some(PlatformAdminUI.AssignConfirm.init "u1")
            }

            let routed, _ =
                PlatformAdminUI.update
                    (PlatformAdminUI.AssignConfirmMsg PlatformAdminUI.AssignConfirm.Confirm)
                    withAssign

            Expect.equal
                (routed.AssignConfirm |> Option.map _.InFlight)
                (Some true)
                "the grant confirmation went in flight through the parent"

            let closed, _ =
                PlatformAdminUI.update
                    (PlatformAdminUI.AssignConfirmMsg(PlatformAdminUI.AssignConfirm.Resolved(Ok())))
                    routed

            Expect.isTrue closed.AssignConfirm.IsNone "the parent closes the confirmation on its outcome"

            let withDelete = {
                parent with
                    ConfirmDeleteTeam = Some(PlatformAdminUI.ConfirmDeleteTeam.init "t1" "Alpha")
            }

            let cancelled, _ =
                PlatformAdminUI.update
                    (PlatformAdminUI.DeleteTeamMsg PlatformAdminUI.ConfirmDeleteTeam.Cancel)
                    withDelete

            Expect.isTrue cancelled.ConfirmDeleteTeam.IsNone "a cancelled delete confirmation closes"
    ]

// ── MappingDataManagerUI ───────────────────────────────────────────────

let private mappingDataManagerTests = testList "MappingDataManagerUI" []

// ───────────────────────────────────────────────────────────────────────

let tests =
    testList "admin sub-models" [
        userDirectoryTests
        teamManagerTests
        platformAdminTests
        mappingDataManagerTests
    ]