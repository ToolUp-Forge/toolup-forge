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

let private teamManagerTests = testList "TeamManagerUI" []

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