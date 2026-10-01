// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.TeamOutputVisibilityUITests

// ─── Phase 942 — the client UI for team output visibility ────────────
//
// Pins, under Fable and over real markup from a real mount, the platform
// team configuration's output-visibility section and the member-visible
// notice the fact browse pages carry:
//
//   1. **An undeclared axis says so.** A deployment that has not composed
//      team output visibility shows a sentence saying there is no level to
//      choose, and offers no choice; outside a team scope the section says
//      the level is per team.
//   2. **The owner chooses within `Selectable`.** Every allowed level is
//      listed with its description; a level the server did not offer this
//      caller is disabled; choosing one hands it to the set action.
//   3. **A non-owner sees the level and cannot change it**, and a refusal
//      is shown as the server worded it, never swallowed.
//   4. **The notice reaches members** — and says nothing where the axis is
//      not composed or there is no team.
//
// The views are the pure halves of the section and the notice; their
// components add only the read and the set over `TeamOutputVisibilityApi`.

open Fable.Core.JsInterop
open ToolUp.Platform
open ToolUp.Platform.Testing
open ToolUp.AI.Client.Tests.NodeTest

let private msgs = MessageCatalog.english.TeamConfig

let private view (level: TeamVisibilityLevel) (selectable: TeamVisibilityLevel list) : TeamOutputVisibilityView = {
    InTeamScope = true
    Enabled = true
    Level = level
    Allowed = TeamVisibilityLevel.all
    Selectable = selectable
}

let private section read status =
    ViewMount.mount (TeamConfigUI.outputVisibilityView msgs read status ignore)

let private radioCount (markup: string) =
    markup.Split("type=\"radio\"").Length - 1

let private sectionTests =
    testList "output-visibility section" [
        testCase "a deployment that has not composed the axis says so and offers nothing"
        <| fun _ ->
            let markup =
                section
                    (Some(
                        Ok {
                            view TeamVisible [] with
                                Enabled = false
                        }
                    ))
                    TeamConfigUI.Idle

            Expect.isTrue (markup.Contains "has not enabled team output visibility") "the undeclared state is stated"
            Expect.equal (radioCount markup) 0 "no level is offered"

        testCase "outside a team scope the section says the level is per team"
        <| fun _ ->
            let markup =
                section
                    (Some(
                        Ok {
                            view TeamVisible [] with
                                InTeamScope = false
                        }
                    ))
                    TeamConfigUI.Idle

            Expect.isTrue (markup.Contains "set per team") "the no-team state is stated"
            Expect.equal (radioCount markup) 0 "no level is offered"

        testCase "while loading, and when the level cannot be read, the section says which"
        <| fun _ ->
            Expect.isTrue ((section None TeamConfigUI.Idle).Contains msgs.OutputVisibilityLoading) "loading"

            Expect.isTrue
                ((section (Some(Error "boom")) TeamConfigUI.Idle).Contains "output visibility: boom")
                "the reason is shown"

        testCase "an owner sees every allowed level, described, and only Selectable is enabled"
        <| fun _ ->
            let markup =
                section (Some(Ok(view TeamAdmins [ TeamVisible; TeamAdmins ]))) TeamConfigUI.Idle

            Expect.equal (radioCount markup) 3 "one choice per allowed level"

            for level in TeamVisibilityLevel.all do
                Expect.isTrue
                    (markup.Contains(TeamConfigUI.outputLevelDescription msgs level))
                    $"{TeamVisibilityLevel.name level} is described"

            Expect.equal (markup.Split("disabled=\"\"").Length - 1) 1 "PlatformAdmins, not selectable, is disabled"
            Expect.isTrue (markup.Contains "checked=\"\"") "the level in force is checked"

        testCase "choosing a selectable level hands it to the set action"
        <| fun _ ->
            let mutable chosen: TeamVisibilityLevel option = None

            ViewMount.mountAndInteract
                (fun host ->
                    let radio: obj = host?querySelector ("input[value='TeamAdmins']")
                    radio?click ())
                (TeamConfigUI.outputVisibilityView
                    msgs
                    (Some(Ok(view TeamVisible [ TeamVisible; TeamAdmins ])))
                    TeamConfigUI.Idle
                    (fun level -> chosen <- Some level))
            |> ignore

            Expect.equal chosen (Some TeamAdmins) "the chosen level reached the set action"

        testCase "a non-owner sees the level in force and is told who can change it"
        <| fun _ ->
            let markup = section (Some(Ok(view TeamAdmins []))) TeamConfigUI.Idle

            Expect.isTrue (markup.Contains msgs.OutputLevelTeamAdminsDescription) "the level in force is shown"
            Expect.isTrue (markup.Contains msgs.OutputVisibilityOwnerOnly) "who can change it is said"
            Expect.equal (radioCount markup) 0 "no level is offered"

        testCase "the server's refusal is shown as it was worded"
        <| fun _ ->
            let refusal =
                "PlatformAdmins is not an allowed output visibility in this deployment. Allowed: TeamVisible, TeamAdmins."

            let markup =
                section (Some(Ok(view TeamVisible [ TeamVisible; TeamAdmins ]))) (TeamConfigUI.Failed refusal)

            Expect.isTrue (markup.Contains refusal) "the refusal text reaches the page"
            Expect.isTrue (markup.Contains "role=\"alert\"") "and is announced"
    ]

let private noticeTests =
    testList "member-visible notice" [
        testCase "a member reads who sees the team's restricted output"
        <| fun _ ->
            let markup =
                ViewMount.mount (TeamConfigUI.outputVisibilityNoticeView msgs (Some(view TeamAdmins [])))

            Expect.isTrue (markup.Contains msgs.OutputLevelTeamAdminsDescription) "the level is described"
            Expect.isTrue (markup.Contains msgs.OutputVisibilityNoticeHint) "and where it is set"

        testCase "the notice says nothing where the axis is not composed, there is no team, or nothing was read"
        <| fun _ ->
            let empty v =
                ViewMount.mount (TeamConfigUI.outputVisibilityNoticeView msgs v)

            Expect.equal
                (empty (
                    Some {
                        view TeamVisible [] with
                            Enabled = false
                    }
                ))
                ""
                "not composed"

            Expect.equal
                (empty (
                    Some {
                        view TeamVisible [] with
                            InTeamScope = false
                    }
                ))
                ""
                "no team"

            Expect.equal (empty None) "" "not read"
    ]

/// Every Phase 942 client case.
let tests =
    testList "Phase 942 — team output visibility UI" [ sectionTests; noticeTests ]