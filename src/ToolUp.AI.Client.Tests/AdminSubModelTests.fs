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

let private platformAdminTests = testList "PlatformAdminUI" []

// ── MappingDataManagerUI ───────────────────────────────────────────────

let private heldCsv: MappingDataManagerUI.HeldFile = {
    FileName = "people.csv"
    RawContents = "Name\nAda\nBo"
    Headers = [ "Name" ]
    Samples = Map.ofList [ "Name", [ "Ada"; "Bo" ] ]
    Fingerprint = "fp-name"
}

let private mappingParent () =
    fst (MappingDataManagerUI.init ClientModuleContext.empty)

let private mappingDataManagerTests =
    testList "MappingDataManagerUI" [
        testCase "the wizard steps and reports its outcome with no parent model"
        <| fun () ->
            let msgs = (mappingParent ()).Messages
            let wizard = MappingDataManagerUI.Wizard.init heldCsv

            Expect.equal wizard.Step MappingDataManagerUI.Wizard.PickTarget "a clean file starts on the target picker"

            let toggled, _, outcome =
                MappingDataManagerUI.Wizard.update msgs [] (MappingDataManagerUI.Wizard.ToggleColumnFixes "Name") wizard

            Expect.isTrue (toggled.DisabledFixes.Contains "Name") "the column's fixes are opted out"
            Expect.equal outcome MappingDataManagerUI.Wizard.Stayed "a step needs nothing from the parent"

            let _, _, noSchema =
                MappingDataManagerUI.Wizard.update msgs [] (MappingDataManagerUI.Wizard.SelectTarget "Sales") wizard

            Expect.equal
                noSchema
                (MappingDataManagerUI.Wizard.Errored msgs.NoSchemaCannotMap)
                "an unregistered target is an error for the parent to show"

            let _, _, cancelled =
                MappingDataManagerUI.Wizard.update msgs [] MappingDataManagerUI.Wizard.Cancel wizard

            Expect.equal cancelled MappingDataManagerUI.Wizard.Cancelled "cancel is reported, not applied"

        testCase "a WizardMsg routed through the parent reaches the wizard"
        <| fun () ->
            let fresh = mappingParent ()

            let parent = {
                fresh with
                    Wizard = Some(MappingDataManagerUI.Wizard.init heldCsv)
            }

            let toggled, _ =
                MappingDataManagerUI.update
                    []
                    (MappingDataManagerUI.WizardMsg(MappingDataManagerUI.Wizard.ToggleColumnFixes "Name"))
                    parent

            match toggled.Wizard with
            | Some w -> Expect.isTrue (w.DisabledFixes.Contains "Name") "the parent's wizard state changed"
            | None -> Expect.isTrue false "the wizard stays open"

            let closed, _ =
                MappingDataManagerUI.update
                    []
                    (MappingDataManagerUI.WizardMsg MappingDataManagerUI.Wizard.Cancel)
                    toggled

            Expect.isTrue closed.Wizard.IsNone "the parent closes the wizard on its Cancelled outcome"
    ]

// ───────────────────────────────────────────────────────────────────────

let tests =
    testList "admin sub-models" [
        userDirectoryTests
        teamManagerTests
        platformAdminTests
        mappingDataManagerTests
    ]