// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.AdminScreenEffectsTests

// ─── The platform administration screens return effect values ────────
//
// For each administration screen, one or more transitions of its `update`
// function, stating the effect values it returns: the remote call each
// names (by method and argument), the follow-up message, or the site of
// the code that has no value form yet. A remote call compares by method,
// argument and retry numbers; a `RemoteEffect.legacy` body by its site
// label. Nothing is performed: a transition only builds values.

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

/// The expected value of a one-shot operation that is not a remote call.
let private expectAwait (name: string) (arg: 'a) : Step<'msg, RemoteEffect<'msg>> =
    Do(RemoteEffect.await name (fun (_: 'a) -> noCall ()) arg (fun (_: Result<obj, exn>) -> Unchecked.defaultof<'msg>))

/// The expected value of code that answers nothing: compared by its site.
let private expectSignal (site: string) : Step<'msg, RemoteEffect<'msg>> = Do(RemoteEffect.signal site ignore)

let private ctx = ClientModuleContext.empty

let tests =
    testList "Admin screens as effect values" [

        testCase "AdminHome: a refresh reads the overview once" (fun () ->
            let m0, _ = AdminHome.init ()
            let _, steps = AdminHome.update AdminHome.Refresh m0
            same steps [ expectCall "IHomeOverviewApi.GetOverview" () ] "one call")

        testCase "AuditLogUI: applying filters queries the first page; an export downloads" (fun () ->
            let m0, _ = AuditLogUI.init ()
            let m1, _ = AuditLogUI.update (AuditLogUI.SetActor "sam") m0
            let _, steps = AuditLogUI.update AuditLogUI.ApplyFilters m1

            same
                steps
                [
                    expectCall "IAuditViewApi.Query" {
                        AuditViewApi.defaultQuery with
                            Actor = Some "sam"
                    }
                ]
                "the query carries the applied actor"

            let _, exported = AuditLogUI.update (AuditLogUI.ExportComplete(Ok [| 1uy |])) m0
            same exported [ expectSignal "AuditLogUI.downloadCsv" ] "the download, by its site")

        testCase "CompositionInspectorUI: a refresh reads the five panels in order" (fun () ->
            let m0, _ = CompositionInspectorUI.init ()
            let _, steps = CompositionInspectorUI.update CompositionInspectorUI.Refresh m0

            same
                steps
                [
                    expectCall "ICompositionInspectorApi.GetComposition" ()
                    expectCall "ICompositionInspectorApi.GetSurfaces" ()
                    expectCall "ICompositionInspectorApi.GetRules" ()
                    expectCall "ICompositionInspectorApi.GetDisclosure" ()
                    expectCall "ICompositionInspectorApi.GetProvenance" ()
                ]
                "five calls, in the order the runtime performs them"

            let _, exported =
                CompositionInspectorUI.update (CompositionInspectorUI.ExportRequested RulesPanel) m0

            same exported [ expectCall "ICompositionInspectorApi.ExportPanel" RulesPanel ] "one export call")

        testCase "DataSubjectRequestAdminUI: a queued export polls; a poll reads the ticket" (fun () ->
            let m0, _ = DataSubjectRequestAdminUI.init ctx

            let m1, started =
                DataSubjectRequestAdminUI.update (DataSubjectRequestAdminUI.AsyncExportStarted(Ok "ticket-1")) m0

            same started [ expectLegacy "DataSubjectRequestAdminUI.pollDelay" ] "the poll timer, by its site"

            let _, polled =
                DataSubjectRequestAdminUI.update DataSubjectRequestAdminUI.PollTicket m1

            same polled [ expectCall "IDataSubjectRequestApi.GetExportStatus" "ticket-1" ] "one status read")

        testCase "DatadogReadbackUI: a monitors refresh is one read of its URL" (fun () ->
            let m0, _ = DatadogReadbackUI.init DatadogReadbackConfig.defaults ()
            let _, steps = DatadogReadbackUI.update DatadogReadbackUI.RefreshMonitors m0

            same
                steps
                [
                    expectAwait "DatadogReadbackUI.fetchMonitors" "/api/observability/datadog/monitors"
                ]
                "a plain HTTP read, off the remoting chain")

        testCase "HealthMonitorUI: a live refresh reads four panels in order" (fun () ->
            let m0, _ = HealthMonitorUI.init ()
            let _, steps = HealthMonitorUI.update HealthMonitorUI.RefreshLive m0

            same
                steps
                [
                    expectCall "IHealthMonitorApi.GetCurrentHealth" ()
                    expectCall "IHealthMonitorApi.GetJobSchedulerTelemetry" ()
                    expectCall "IHealthMonitorApi.GetDegradedCapabilities" ()
                    expectCall "IHealthMonitorApi.GetAIDenialRollup" ()
                ]
                "four calls, in the order the runtime performs them")

        testCase "JobsAdminUI: expanding reads attempts; an accepted re-run reads and starts the poll" (fun () ->
            let id = Guid.NewGuid()
            let m0, _ = JobsAdminUI.init ()
            let _, expanded = JobsAdminUI.update (JobsAdminUI.ToggleAttempts id) m0

            same
                expanded
                [ expectCall "JobApi.GetRecentRuns" (id, JobsAdminUI.recentAttemptCount) ]
                "one attempts read"

            let _, accepted = JobsAdminUI.update (JobsAdminUI.RerunAccepted(id, Ok())) m0

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

            let _, polled = JobsAdminUI.update (JobsAdminUI.PollJob id) awaiting

            same
                polled
                [
                    expectCall "JobApi.GetJob" id
                    expectCall "JobApi.GetRecentRuns" (id, JobsAdminUI.recentAttemptCount)
                ]
                "a poll reads the job, then its attempts")

        testCase "MigrationStatusUI: a refresh reads types then statuses; a trigger names its data type" (fun () ->
            let m0, _ = MigrationStatusUI.init ()
            let _, steps = MigrationStatusUI.update MigrationStatusUI.Refresh m0

            same
                steps
                [
                    expectCall "IDataMigrationApi.ListDataTypes" ()
                    expectCall "IDataMigrationApi.ListStatuses" ()
                ]
                "two calls, in order"

            let _, triggered =
                MigrationStatusUI.update (MigrationStatusUI.TriggerClicked "media") m0

            same triggered [ expectCall "IDataMigrationApi.TriggerMigration" "media" ] "one trigger")

        testCase "ModuleVisibilityAdminUI: a load reads three things; a save reloads by a follow-up" (fun () ->
            let m0, _ = ModuleVisibilityAdminUI.init ctx
            let _, loaded = ModuleVisibilityAdminUI.update ModuleVisibilityAdminUI.Load m0

            same
                loaded
                [
                    expectCall "IModuleVisibilityApi.ListRegisteredModules" ()
                    expectCall "IModuleVisibilityApi.GetProfile" ()
                    expectCall "IModuleVisibilityApi.GetResolvedVisibility" ()
                ]
                "three calls, in order"

            let _, saved =
                ModuleVisibilityAdminUI.update (ModuleVisibilityAdminUI.SaveCompleted(Ok())) m0

            same saved [ Then ModuleVisibilityAdminUI.Load ] "the reload, as a follow-up message")

        testCase "ObservabilityUI: with only alerts on, the sources answer reads the alerts" (fun () ->
            let m0, _ = ObservabilityUI.init ()

            let _, steps =
                ObservabilityUI.update
                    (ObservabilityUI.SourcesLoaded(
                        Ok {
                            Logs = false
                            Metrics = false
                            Alerts = true
                        }
                    ))
                    m0

            same steps [ expectAwait "ObservabilityUI.fetchAlerts" "/api/observability/alerts" ] "one read of its URL")

        testCase "PermissionsAdminUI: a refresh reads the snapshot" (fun () ->
            let m0, _ = PermissionsAdminUI.init ctx
            let _, steps = PermissionsAdminUI.update PermissionsAdminUI.RefreshSnapshot m0
            same steps [ expectCall "PermissionsAdminUI.loadSnapshot" () ] "one snapshot read")

        testCase "PlatformAdminUI: a refresh, a directory lookup and a lifted confirmation" (fun () ->
            let m0, _ = PlatformAdminUI.init ctx
            let _, refreshed = PlatformAdminUI.update PlatformAdminUI.RefreshAdmins m0
            same refreshed [ expectCall "PlatformAdminApi.ListPlatformAdmins" () ] "one list call"

            let _, owner =
                PlatformAdminUI.update (PlatformAdminUI.SetNewTeamOwnerUserId " u9 ") m0

            same owner [ expectCall "IUserDirectoryApi.ResolveUsers" [ "u9" ] ] "the directory's one lookup"

            let confirming = {
                m0 with
                    AssignConfirm = Some(PlatformAdminUI.AssignConfirm.init "u1")
            }

            let _, granted =
                PlatformAdminUI.update
                    (PlatformAdminUI.AssignConfirmMsg PlatformAdminUI.AssignConfirm.Confirm)
                    confirming

            same
                granted
                [ expectCall "PlatformAdminApi.AssignPlatformAdmin" "u1" ]
                "the sub-model's call, lifted with its identity"

            let _, deleted =
                PlatformAdminUI.ConfirmDeleteTeam.update
                    PlatformAdminUI.ConfirmDeleteTeam.Confirm
                    (PlatformAdminUI.ConfirmDeleteTeam.init "t1" "Team One")

            same deleted [ expectCall "TeamApi.DeleteTeamHard" "t1" ] "the delete confirmation's call")

        testCase "PlatformUsersUI: a load reads the principals" (fun () ->
            let m0, _ = PlatformUsersUI.init ctx
            let _, steps = PlatformUsersUI.update PlatformUsersUI.LoadPrincipals m0
            same steps [ expectCall "IPlatformTenantApi.ListPrincipals" () ] "one call")

        testCase "ServiceAccountUI: selecting an account reads its tokens" (fun () ->
            let m0, _ = ServiceAccountUI.init ()
            let _, steps = ServiceAccountUI.update (ServiceAccountUI.SelectAccount "sa-1") m0
            same steps [ expectCall "IServiceAccountApi.ListTokens" "sa-1" ] "one call")

        testCase "ServiceStatusBoardUI: a section refresh names its method; an unknown one answers at once" (fun () ->
            let m0, _ = ServiceStatusBoardUI.init ctx

            let _, health =
                ServiceStatusBoardUI.update
                    (ServiceStatusBoardUI.RefreshSection ServiceStatusSnapshot.HealthSection)
                    m0

            same health [ expectCall "IServiceStatusBoardApi.RefreshHealth" () ] "the health section's own call"

            let _, unknown =
                ServiceStatusBoardUI.update (ServiceStatusBoardUI.RefreshSection "bogus") m0

            same
                unknown
                [
                    Then(ServiceStatusBoardUI.SectionLoaded("bogus", Error(m0.Messages.UnknownSectionMessage "bogus")))
                ]
                "a follow-up message, no call")

        testCase "TenantLifecycleAdminUI: a submitted scope is read trimmed; a blank one is not read" (fun () ->
            let m0, _ = TenantLifecycleAdminUI.init ()

            let _, steps =
                TenantLifecycleAdminUI.update (TenantLifecycleAdminUI.SubmitScope "  team-1  ") m0

            same steps [ expectCall "IPlatformTenantApi.GetLifecycleSummary" "team-1" ] "one call"

            let _, blank =
                TenantLifecycleAdminUI.update (TenantLifecycleAdminUI.SubmitScope "   ") m0

            same blank [] "nothing")

        testCase "UsageDashboard: an export names its window; a finished export downloads" (fun () ->
            let m0, _ = UsageDashboard.init ()
            let _, steps = UsageDashboard.update UsageDashboard.ExportCsv m0

            same
                steps
                [
                    expectCall "IUsageQueryApi.ExportCsv" (Some({ From = m0.From; To = m0.To }: UsageDateRange))
                ]
                "one export call"

            let _, exported =
                UsageDashboard.update (UsageDashboard.ExportComplete(Ok [| 1uy |])) m0

            same exported [ expectSignal "UsageDashboard.downloadCsv" ] "the download, by its site")

        testCase "WebhookAdminUI: a test fire names its subscription" (fun () ->
            let id = Guid.NewGuid()
            let m0, _ = WebhookAdminUI.init ctx
            let _, steps = WebhookAdminUI.update (WebhookAdminUI.TestFireSubmit id) m0
            same steps [ expectCall "IWebhookApi.TestFire" id ] "one call")
    ]