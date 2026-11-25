// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.TeamScreenEffectsTests

// ─── The team and data screens state their effects ───────────────────
//
// Each screen's `step` returns its remote calls as `RemoteEffect` values
// in a `Fuaran.MVU` step list, and its `update` runs that list on today's
// loop. A case here drives one transition and states the effect values it
// returns: the method each call names, its argument and, where a
// transition asks for several, their order. A call's proxy and reply take
// no part in its equality, so the expected values are built with neither.
// Nothing is performed: the commands `update` would build are never run.

open Fuaran.MVU
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

let private noCall () : Async<'T> = async { return failwith "not called in this test" }

/// The expected value of a call: its proxy and reply take no part in
/// equality, so any will do.
let private call (methodName: string) (arg: 'a) : RemoteEffect<'msg> =
    RemoteEffect.call methodName (fun (_: 'a) -> noCall ()) arg (fun (_: Result<obj, exn>) -> Unchecked.defaultof<'msg>)

/// The expected value of a legacy body: it compares by its site label.
let private legacy (site: string) : RemoteEffect<'msg> = RemoteEffect.legacy site ignore

let private effects (steps: Step<'msg, RemoteEffect<'msg>> list) : RemoteEffect<'msg> list = Step.effects steps

let private followUps (steps: Step<'msg, RemoteEffect<'msg>> list) : 'msg list =
    steps
    |> List.choose (function
        | Then msg -> Some msg
        | Do _ -> None)

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

// ─── Screens ─────────────────────────────────────────────────────────

let private userDirectoryTests =
    testList "UserDirectory" [
        testCase "a lookup names the ids not held yet, once each"
        <| fun () ->
            let steps = UserDirectory.resolveSteps [ "u1"; ""; "u1"; "u2" ] UserDirectory.empty

            same
                (effects steps)
                [ call "IUserDirectoryApi.ResolveUsers" [ "u1"; "u2" ] ]
                "one call, blanks and repeats dropped"

        testCase "nothing to resolve asks for nothing"
        <| fun () -> same (effects (UserDirectory.resolveSteps [ "" ] UserDirectory.empty)) [] "no call"
    ]

let private noActiveTeamTests =
    testList "NoActiveTeamLandingUI" [
        testCase "checking for invitations calls CheckMyInvites"
        <| fun () ->
            let model: NoActiveTeamLandingUI.Model = {
                State = NoActiveTeamLandingUI.CheckState.Idle
                OnTeamSwitched = None
            }

            let checking, steps =
                NoActiveTeamLandingUI.step NoActiveTeamLandingUI.CheckInvites model

            same checking.State NoActiveTeamLandingUI.CheckState.Checking "the page says it is checking"
            same (effects steps) [ call "ITeamInviteApi.CheckMyInvites" () ] "one call"
    ]

let private notificationPreferencesTests =
    testList "NotificationPreferencesUI" [
        testCase "a reload reads the caller's preferences; dismissing an error asks for nothing"
        <| fun () ->
            let model = fst (NotificationPreferencesUI.init ())
            let _, load = NotificationPreferencesUI.step NotificationPreferencesUI.Load model
            same (effects load) [ call "INotificationPreferenceApi.GetMyPreferences" () ] "one read"

            let _, dismissed =
                NotificationPreferencesUI.step NotificationPreferencesUI.DismissError model

            same (effects dismissed) [] "no call"
    ]

let private sessionSecurityTests =
    testList "SessionSecurityUI" [
        testCase "a confirmed revoke names its session, and its success reloads the list"
        <| fun () ->
            let model = fst (SessionSecurityUI.init ClientModuleContext.empty)
            let _, revoke = SessionSecurityUI.step (SessionSecurityUI.ConfirmRevoke "s-1") model
            same (effects revoke) [ call "ISessionApi.RevokeSession" "s-1" ] "the revoke"

            let _, reload =
                SessionSecurityUI.step (SessionSecurityUI.RevokeCompleted(Ok())) model

            same (effects reload) [ call "ISessionApi.ListMySessions" () ] "the reload"
    ]

let private homeTests =
    testList "Home" [
        testCase "a refresh with recents on reads the overview, then the pinning"
        <| fun () ->
            let model = fst (Home.init true ())
            let _, steps = Home.step Home.Refresh model

            same
                (effects steps)
                [
                    call "IHomeOverviewApi.GetOverview" ()
                    call "IHomeOverviewApi.GetPinning" ()
                ]
                "two reads, in order"

        testCase "a refresh with recents off reads the overview alone; a pin names its module"
        <| fun () ->
            let model = fst (Home.init false ())
            let _, steps = Home.step Home.Refresh model
            same (effects steps) [ call "IHomeOverviewApi.GetOverview" () ] "one read"

            let _, pin = Home.step (Home.TogglePin("files", true)) model

            let request: PinRequest = { ModuleId = "files"; Pinned = true }
            same (effects pin) [ call "IHomeOverviewApi.SetPinned" request ] "the pin"
    ]

let private dataIngestionTests =
    testList "DataIngestionUI" [
        testCase "a connect names the source and flow; a disconnect's success re-reads both statuses"
        <| fun () ->
            let model = fst (DataIngestionUI.init ())

            let _, connect =
                DataIngestionUI.step (DataIngestionUI.ConnectClicked("src-1", "oauth")) model

            same (effects connect) [ call "IDataIngestionApi.BeginOAuth" ("src-1", "oauth") ] "the OAuth start"

            let _, refreshed =
                DataIngestionUI.step (DataIngestionUI.DisconnectComplete("src-1", Ok())) model

            same
                (effects refreshed)
                [
                    call "IDataIngestionApi.GetCredentialStatus" "src-1"
                    call "IDataIngestionApi.GetTokenStatus" "src-1"
                ]
                "the credential status, then the token status"

        testCase "the authorize-URL navigation is no effect value: update keeps it"
        <| fun () ->
            let model = fst (DataIngestionUI.init ())

            let _, steps =
                DataIngestionUI.step (DataIngestionUI.ConnectUrlReady("src-1", Ok "https://idp.test/authorize")) model

            same (effects steps) [] "the step list holds no effect"

            let _, cmd =
                DataIngestionUI.update (DataIngestionUI.ConnectUrlReady("src-1", Ok "https://idp.test/authorize")) model

            Expect.equal (List.length cmd) 1 "update builds the one navigation command (not run here)"
    ]

let private externalContactTests =
    testList "ExternalContactManagerUI" [
        testCase "a delete names its contact, and its success reloads the list"
        <| fun () ->
            let model = fst (ExternalContactManagerUI.init ())

            let _, delete =
                ExternalContactManagerUI.step (ExternalContactManagerUI.DeleteContact "c-1") model

            same (effects delete) [ call "IExternalContactApi.DeleteContact" "c-1" ] "the delete"

            let _, reload =
                ExternalContactManagerUI.step (ExternalContactManagerUI.ContactDeleted(Ok())) model

            same (effects reload) [ call "IExternalContactApi.ListContacts" () ] "the reload"
    ]

let private fileManagerTests =
    testList "FileManagerUI" [
        testCase "a delete names its file; its success asks for the list as a follow-up"
        <| fun () ->
            let model = fst (FileManagerUI.init ClientModuleContext.empty)

            let _, delete =
                FileManagerUI.step (FileManagerUI.DeleteFile(Start "sales.csv")) model

            same (effects delete) [ call "FileManagementApi.DeleteFile" "sales.csv" ] "the delete"

            let _, deleted = FileManagerUI.step (FileManagerUI.DeleteFile(Finished(Ok()))) model

            same (effects deleted) [] "no call of its own"

            match followUps deleted with
            | [ FileManagerUI.LoadFiles(Start()) ] -> ()
            | other -> failwithf "expected one LoadFiles follow-up, got %A" other

        testCase "choosing a file reads it through the one legacy body"
        <| fun () ->
            let model = fst (FileManagerUI.init ClientModuleContext.empty)

            let _, steps =
                FileManagerUI.step (FileManagerUI.SelectFile(Start(Unchecked.defaultof<Browser.Types.File>))) model

            same (effects steps) [ legacy "FileManagerUI.readFile" ] "the read"
    ]

let private teamConfigTests =
    testList "TeamConfigUI" [
        testCase "a clear names its module; a flag's success reloads the flags boot"
        <| fun () ->
            let model = fst (TeamConfigUI.init ())
            let _, clear = TeamConfigUI.step (TeamConfigUI.SubmitClear "_platform") model
            same (effects clear) [ call "IConfigApi.ClearModuleConfig" "_platform" ] "the clear"

            let _, reload =
                TeamConfigUI.step (TeamConfigUI.SetFlagCompleted("beta", Ok())) model

            same (effects reload) [ call "TeamConfigUI.loadFlagsBoot" () ] "the flags boot"
    ]

let private providerProfileTests =
    let config = ProviderProfileUI.ProviderProfileConfig.create []

    testList "ProviderProfileUI" [
        testCase "a remove names its entry; a fallback names its order"
        <| fun () ->
            let model = fst (ProviderProfileUI.init config)

            let _, remove =
                ProviderProfileUI.step config (ProviderProfileUI.Remove "work") model

            same (effects remove) [ call "IProviderProfileApi.RemoveEntry" "work" ] "the remove"

            let _, fallback =
                ProviderProfileUI.step config (ProviderProfileUI.SetFallback [ "work"; "home" ]) model

            same (effects fallback) [ call "IProviderProfileApi.SetFallback" [ "work"; "home" ] ] "the fallback"

        testCase "a verify runs the consumer's delegate as the one legacy body"
        <| fun () ->
            let verifying =
                config
                |> ProviderProfileUI.ProviderProfileConfig.withVerify (fun _ -> async { return Ok [] })

            let model = fst (ProviderProfileUI.init verifying)

            let candidate: ProviderProfileUI.ProviderCandidate = {
                Label = "work"
                ProviderId = "provider"
                Model = None
                Tags = []
                ApiKey = Some "key"
            }

            let _, steps =
                ProviderProfileUI.step verifying (ProviderProfileUI.Verify candidate) model

            same (effects steps) [ legacy "ProviderProfileUI.verify" ] "the delegate"
    ]

let private teamManagerTests =
    testList "TeamManagerUI" [
        testCase "opening a team asks for its members as a follow-up; loading them reads them"
        <| fun () ->
            let model = fst (TeamManagerUI.init ClientModuleContext.empty)
            let _, select = TeamManagerUI.step (TeamManagerUI.SelectTeam "t1") model

            match followUps select with
            | [ TeamManagerUI.LoadMembers "t1" ] -> ()
            | other -> failwithf "expected one LoadMembers follow-up, got %A" other

            let _, load = TeamManagerUI.step (TeamManagerUI.LoadMembers "t1") model
            same (effects load) [ call "TeamApi.GetTeamMembers" "t1" ] "the members read"

        testCase "the pending-invites view reloads both invite lists, pending first"
        <| fun () ->
            let model = fst (TeamManagerUI.init ClientModuleContext.empty)
            let _, steps = TeamManagerUI.step (TeamManagerUI.NavigatePendingInvites "t1") model

            match followUps steps with
            | [ TeamManagerUI.LoadPendingByEmail "t1"; TeamManagerUI.LoadExpiredInvites "t1" ] -> ()
            | other -> failwithf "expected the two invite reloads in order, got %A" other

        testCase "a transfer submitted in the modal reaches the parent's step list as the child's call"
        <| fun () ->
            let model = fst (TeamManagerUI.init ClientModuleContext.empty)
            let opened, _ = TeamManagerUI.step (TeamManagerUI.OpenTransferOwnership "t1") model

            let picked, _ =
                TeamManagerUI.step
                    (TeamManagerUI.TransferMsg(TeamManagerUI.TransferOwnership.SelectCandidate "u2"))
                    opened

            let _, steps =
                TeamManagerUI.step (TeamManagerUI.TransferMsg TeamManagerUI.TransferOwnership.Submit) picked

            same (effects steps) [ call "TeamApi.TransferOwnership" ("t1", "u2") ] "the transfer, lifted"
    ]

let private mappingDataManagerTests =
    testList "MappingDataManagerUI" [
        testCase "a load reads the files, the conversion records and the catalog, in that order"
        <| fun () ->
            let model = fst (MappingDataManagerUI.init ClientModuleContext.empty)
            let _, steps = MappingDataManagerUI.step [] MappingDataManagerUI.LoadFiles model

            same
                (effects steps)
                [
                    call "FileManagementApi.ListFiles" ()
                    call "IConversionApi.ListConversionRecords" ()
                    call "DataCatalogApi.GetDataCatalog" ()
                ]
                "three reads, in order"

        testCase "a delete cascades through the one legacy body"
        <| fun () ->
            let model = fst (MappingDataManagerUI.init ClientModuleContext.empty)

            let _, steps =
                MappingDataManagerUI.step [] (MappingDataManagerUI.DeleteFile "sales.csv") model

            same (effects steps) [ legacy "MappingDataManagerUI.deleteFiles" ] "the cascade"
    ]

/// Every team and data screen's stated effects.
let tests =
    testList "The team and data screens state their effects" [
        userDirectoryTests
        noActiveTeamTests
        notificationPreferencesTests
        sessionSecurityTests
        homeTests
        dataIngestionTests
        externalContactTests
        fileManagerTests
        teamConfigTests
        providerProfileTests
        teamManagerTests
        mappingDataManagerTests
    ]