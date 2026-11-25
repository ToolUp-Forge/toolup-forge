// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.AdminScreenEffectsTests

// ─── The platform administration screens return effect values ────────
//
// For each administration screen, one or more transitions of its `step`
// function, stating the effect values it returns: the remote call each
// names (by method and argument), the follow-up message, or the site of
// the code that has no value form yet. A remote call compares by method,
// argument and retry numbers; a `RemoteEffect.legacy` body by its site
// label. Nothing is performed: a transition only builds values.
//
// A few cases also check that the screen's `update` still hands today's
// loop one command element per step, in the same order.

open System
open Fuaran.MVU
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Platform.Usage
open ToolUp.AI.Client.Tests.NodeTest

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

let private noCall () : Async<'T> = async { return failwith "not called in this test" }

/// The expected value of a call: its proxy and reply take no part in
/// equality, so any will do.
let private expectCall (methodName: string) (arg: 'a) : Step<'msg, RemoteEffect<'msg>> =
    Do(
        RemoteEffect.call methodName (fun (_: 'a) -> noCall ()) arg (fun (_: Result<obj, exn>) ->
            Unchecked.defaultof<'msg>)
    )

/// The expected value of code with no value form yet: compared by its site.
let private expectLegacy (site: string) : Step<'msg, RemoteEffect<'msg>> = Do(RemoteEffect.legacy site ignore)

let private ctx = ClientModuleContext.empty

let tests =
    testList "Admin screens as effect values" [

        testCase "AdminHome: a refresh reads the overview once" (fun () ->
            let m0, _ = AdminHome.init ()
            let _, steps = AdminHome.step AdminHome.Refresh m0
            same steps [ expectCall "IHomeOverviewApi.GetOverview" () ] "one call")

        testCase "AuditLogUI: applying filters queries the first page; an export downloads" (fun () ->
            let m0, _ = AuditLogUI.init ()
            let m1, _ = AuditLogUI.step (AuditLogUI.SetActor "sam") m0
            let _, steps = AuditLogUI.step AuditLogUI.ApplyFilters m1

            same
                steps
                [
                    expectCall "IAuditViewApi.Query" {
                        AuditViewApi.defaultQuery with
                            Actor = Some "sam"
                    }
                ]
                "the query carries the applied actor"

            let _, exported = AuditLogUI.step (AuditLogUI.ExportComplete(Ok [| 1uy |])) m0
            same exported [ expectLegacy "AuditLogUI.downloadCsv" ] "the download, by its site")

        testCase "CompositionInspectorUI: a refresh reads the five panels in order" (fun () ->
            let m0, _ = CompositionInspectorUI.init ()
            let _, steps = CompositionInspectorUI.step CompositionInspectorUI.Refresh m0

            same
                steps
                [
                    expectCall "ICompositionInspectorApi.GetComposition" ()
                    expectCall "ICompositionInspectorApi.GetSurfaces" ()
                    expectCall "ICompositionInspectorApi.GetRules" ()
                    expectCall "ICompositionInspectorApi.GetDisclosure" ()
                    expectCall "ICompositionInspectorApi.GetProvenance" ()
                ]
                "five calls, in the order Cmd.batch ran them"

            let _, exported =
                CompositionInspectorUI.step (CompositionInspectorUI.ExportRequested RulesPanel) m0

            same exported [ expectCall "ICompositionInspectorApi.ExportPanel" RulesPanel ] "one export call")

        testCase "DataSubjectRequestAdminUI: a queued export polls; a poll reads the ticket" (fun () ->
            let m0, _ = DataSubjectRequestAdminUI.init ctx

            let m1, started =
                DataSubjectRequestAdminUI.step (DataSubjectRequestAdminUI.AsyncExportStarted(Ok "ticket-1")) m0

            same started [ expectLegacy "DataSubjectRequestAdminUI.pollDelay" ] "the poll timer, by its site"

            let _, polled =
                DataSubjectRequestAdminUI.step DataSubjectRequestAdminUI.PollTicket m1

            same polled [ expectCall "IDataSubjectRequestApi.GetExportStatus" "ticket-1" ] "one status read")

        testCase "DatadogReadbackUI: a monitors refresh is one read of its URL" (fun () ->
            let m0, _ = DatadogReadbackUI.init DatadogReadbackConfig.defaults ()
            let _, steps = DatadogReadbackUI.step DatadogReadbackUI.RefreshMonitors m0

            same
                steps
                [ expectLegacy "DatadogReadbackUI.fetch /api/observability/datadog/monitors" ]
                "a plain HTTP read, off the remoting chain")

        testCase "HealthMonitorUI: a live refresh reads four panels in order, as four commands" (fun () ->
            let m0, _ = HealthMonitorUI.init ()
            let _, steps = HealthMonitorUI.step HealthMonitorUI.RefreshLive m0

            same
                steps
                [
                    expectCall "IHealthMonitorApi.GetCurrentHealth" ()
                    expectCall "IHealthMonitorApi.GetJobSchedulerTelemetry" ()
                    expectCall "IHealthMonitorApi.GetDegradedCapabilities" ()
                    expectCall "IHealthMonitorApi.GetAIDenialRollup" ()
                ]
                "four calls, in the order Cmd.batch ran them"

            let _, cmd = HealthMonitorUI.update HealthMonitorUI.RefreshLive m0
            same (List.length cmd) 4 "update still returns four command elements")

        testCase "JobsAdminUI: expanding reads attempts; an accepted re-run reads and starts the poll" (fun () ->
            let id = Guid.NewGuid()
            let m0, _ = JobsAdminUI.init ()
            let _, expanded = JobsAdminUI.step (JobsAdminUI.ToggleAttempts id) m0

            same
                expanded
                [ expectCall "JobApi.GetRecentRuns" (id, JobsAdminUI.recentAttemptCount) ]
                "one attempts read"

            let _, accepted = JobsAdminUI.step (JobsAdminUI.RerunAccepted(id, Ok())) m0

            same
                accepted
                [
                    expectCall "JobApi.GetRecentRuns" (id, JobsAdminUI.recentAttemptCount)
                    expectLegacy ("JobsAdminUI.poll " + string id)
                ]
                "the attempts read, then the poll timer"

            let awaiting = {
                m0 with
                    Reruns = Map.ofList [ id, JobsAdminUI.RerunProgress.Awaiting(None, 0) ]
            }

            let _, polled = JobsAdminUI.step (JobsAdminUI.PollJob id) awaiting

            same
                polled
                [
                    expectCall "JobApi.GetJob" id
                    expectCall "JobApi.GetRecentRuns" (id, JobsAdminUI.recentAttemptCount)
                ]
                "a poll reads the job, then its attempts")

        testCase "MigrationStatusUI: a refresh reads types then statuses; a trigger names its data type" (fun () ->
            let m0, _ = MigrationStatusUI.init ()
            let _, steps = MigrationStatusUI.step MigrationStatusUI.Refresh m0

            same
                steps
                [
                    expectCall "IDataMigrationApi.ListDataTypes" ()
                    expectCall "IDataMigrationApi.ListStatuses" ()
                ]
                "two calls, in order"

            let _, triggered =
                MigrationStatusUI.step (MigrationStatusUI.TriggerClicked "media") m0

            same triggered [ expectCall "IDataMigrationApi.TriggerMigration" "media" ] "one trigger")

        testCase "ModuleVisibilityAdminUI: a load reads three things; a save reloads by a follow-up" (fun () ->
            let m0, _ = ModuleVisibilityAdminUI.init ctx
            let _, loaded = ModuleVisibilityAdminUI.step ModuleVisibilityAdminUI.Load m0

            same
                loaded
                [
                    expectCall "IModuleVisibilityApi.ListRegisteredModules" ()
                    expectCall "IModuleVisibilityApi.GetProfile" ()
                    expectCall "IModuleVisibilityApi.GetResolvedVisibility" ()
                ]
                "three calls, in order"

            let _, saved =
                ModuleVisibilityAdminUI.step (ModuleVisibilityAdminUI.SaveCompleted(Ok())) m0

            same saved [ Then ModuleVisibilityAdminUI.Load ] "the reload, as a follow-up message")

        testCase "ObservabilityUI: with only alerts on, the sources answer reads the alerts" (fun () ->
            let m0, _ = ObservabilityUI.init ()

            let _, steps =
                ObservabilityUI.step
                    (ObservabilityUI.SourcesLoaded(
                        Ok {
                            Logs = false
                            Metrics = false
                            Alerts = true
                        }
                    ))
                    m0

            same steps [ expectLegacy "ObservabilityUI.fetch /api/observability/alerts" ] "one read of its URL")

        testCase "PermissionsAdminUI: a refresh reads the snapshot" (fun () ->
            let m0, _ = PermissionsAdminUI.init ctx
            let _, steps = PermissionsAdminUI.step PermissionsAdminUI.RefreshSnapshot m0
            same steps [ expectCall "PermissionsAdminUI.loadSnapshot" () ] "one snapshot read")

        testCase "PlatformAdminUI: a refresh, a directory lookup and a lifted confirmation" (fun () ->
            let m0, _ = PlatformAdminUI.init ctx
            let _, refreshed = PlatformAdminUI.step PlatformAdminUI.RefreshAdmins m0
            same refreshed [ expectCall "PlatformAdminApi.ListPlatformAdmins" () ] "one list call"

            let _, owner =
                PlatformAdminUI.step (PlatformAdminUI.SetNewTeamOwnerUserId " u9 ") m0

            same owner [ expectLegacy "UserDirectory.resolve u9 #0" ] "the directory's one lookup"

            let confirming = {
                m0 with
                    AssignConfirm = Some(PlatformAdminUI.AssignConfirm.init "u1")
            }

            let _, granted =
                PlatformAdminUI.step
                    (PlatformAdminUI.AssignConfirmMsg PlatformAdminUI.AssignConfirm.Confirm)
                    confirming

            same
                granted
                [ expectCall "PlatformAdminApi.AssignPlatformAdmin" "u1" ]
                "the sub-model's call, lifted with its identity"

            let _, deleted =
                PlatformAdminUI.ConfirmDeleteTeam.step
                    PlatformAdminUI.ConfirmDeleteTeam.Confirm
                    (PlatformAdminUI.ConfirmDeleteTeam.init "t1" "Team One")

            same deleted [ expectCall "TeamApi.DeleteTeamHard" "t1" ] "the delete confirmation's call")

        testCase "PlatformUsersUI: a load reads the principals" (fun () ->
            let m0, _ = PlatformUsersUI.init ctx
            let _, steps = PlatformUsersUI.step PlatformUsersUI.LoadPrincipals m0
            same steps [ expectCall "IPlatformTenantApi.ListPrincipals" () ] "one call")

        testCase "ServiceAccountUI: selecting an account reads its tokens" (fun () ->
            let m0, _ = ServiceAccountUI.init ()
            let _, steps = ServiceAccountUI.step (ServiceAccountUI.SelectAccount "sa-1") m0
            same steps [ expectCall "IServiceAccountApi.ListTokens" "sa-1" ] "one call")

        testCase "ServiceStatusBoardUI: a section refresh names its method; an unknown one answers at once" (fun () ->
            let m0, _ = ServiceStatusBoardUI.init ctx

            let _, health =
                ServiceStatusBoardUI.step (ServiceStatusBoardUI.RefreshSection ServiceStatusSnapshot.HealthSection) m0

            same health [ expectCall "IServiceStatusBoardApi.RefreshHealth" () ] "the health section's own call"

            let _, unknown =
                ServiceStatusBoardUI.step (ServiceStatusBoardUI.RefreshSection "bogus") m0

            same
                unknown
                [
                    Then(ServiceStatusBoardUI.SectionLoaded("bogus", Error(m0.Messages.UnknownSectionMessage "bogus")))
                ]
                "a follow-up message, no call")

        testCase "TenantLifecycleAdminUI: a submitted scope is read trimmed; a blank one is not read" (fun () ->
            let m0, _ = TenantLifecycleAdminUI.init ()

            let _, steps =
                TenantLifecycleAdminUI.step (TenantLifecycleAdminUI.SubmitScope "  team-1  ") m0

            same steps [ expectCall "IPlatformTenantApi.GetLifecycleSummary" "team-1" ] "one call"

            let _, blank =
                TenantLifecycleAdminUI.step (TenantLifecycleAdminUI.SubmitScope "   ") m0

            same blank [] "nothing")

        testCase "UsageDashboard: an export names its window; a finished export downloads" (fun () ->
            let m0, _ = UsageDashboard.init ()
            let _, steps = UsageDashboard.step UsageDashboard.ExportCsv m0

            same
                steps
                [
                    expectCall "IUsageQueryApi.ExportCsv" (Some({ From = m0.From; To = m0.To }: UsageDateRange))
                ]
                "one export call"

            let _, exported =
                UsageDashboard.step (UsageDashboard.ExportComplete(Ok [| 1uy |])) m0

            same exported [ expectLegacy "UsageDashboard.downloadCsv" ] "the download, by its site")

        testCase "WebhookAdminUI: a test fire names its subscription" (fun () ->
            let id = Guid.NewGuid()
            let m0, _ = WebhookAdminUI.init ctx
            let _, steps = WebhookAdminUI.step (WebhookAdminUI.TestFireSubmit id) m0
            same steps [ expectCall "IWebhookApi.TestFire" id ] "one call")
    ]