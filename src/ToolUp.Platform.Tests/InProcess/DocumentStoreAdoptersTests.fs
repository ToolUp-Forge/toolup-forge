// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DocumentStoreAdoptersTests

// ─── The long tail of BlobDocumentStore adopters ─────────────────────
//
// Phase 1061 moved each of these stores off its own `IBlobStorage.Download`
// onto `BlobDocumentStore`. Every store here pins the same observable
// behaviour through its own interface: a document whose bytes do not decode
// is a REFUSAL of the read and of the write — raised where the member has
// no error channel, `Error` where it has one — its bytes copied aside and
// the canonical blob left exactly as it was. Before the move each read the
// document as absent or empty and, for the read-modify-write members, wrote
// that back over everything the document held.

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run work = Async.RunSynchronously work

let private garbage = Encoding.UTF8.GetBytes "{ not json"

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A fresh backend with undecodable bytes at `container`/`name`.
let private planted (container: string) (name: string) =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    storage.Upload(container, name, garbage) |> run |> ignore
    storage

/// The bytes were copied aside and the canonical blob is untouched.
let private expectQuarantined (storage: IBlobStorage) (container: string) (name: string) =
    let listed = storage.List(container, name) |> run
    Expect.isTrue (listed |> List.exists (fun n -> n.StartsWith(name + ".corrupt-"))) $"{name} was quarantined"
    Expect.equal (storage.Download(container, name) |> run) (Ok garbage) $"{name} was left exactly as it was"

/// The bytes were copied aside — and only then was the document started
/// again (`UpdateOrRestart` / `Replace`), so the canonical blob now holds
/// what the store wrote.
let private expectSetAside (storage: IBlobStorage) (container: string) (name: string) =
    let copies = storage.List(container, name + ".corrupt-") |> run

    Expect.equal copies.Length 1 $"{name}'s undecodable bytes were set aside"
    Expect.equal (storage.Download(container, copies.Head) |> run) (Ok garbage) "byte for byte"
    Expect.notEqual (storage.Download(container, name) |> run) (Ok garbage) $"{name} was started again"

let private expectRefusedRead (label: string) (read: unit -> unit) =
    Expect.throwsT<DocumentUnavailableException> read $"{label} refuses rather than reading the document as absent"

let private expectError (label: string) (result: Result<'a, 'e>) =
    Expect.isError result $"{label} refuses rather than writing over the document"

let private platform = ScopeKey.PlatformContainer

let tests =
    testList "Phase 1061 — the long tail of BlobDocumentStore adopters" [

        test "FeatureFlagStore" {
            let name = $"flags/{FlagScope.slug FlagScope.Platform}.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Config.FeatureFlagStore.create storage

            expectRefusedRead "GetFlag" (fun () -> store.GetFlag(FlagScope.Platform, "beta") |> run |> ignore)
            expectError "SetFlag" (store.SetFlag(FlagScope.Platform, "beta", FlagValue.Bool true) |> run)
            expectQuarantined storage platform name
        }

        test "ModuleVisibilityStore" {
            let name = $"module-visibility/{FlagScope.slug FlagScope.Platform}.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Scope.ModuleVisibilityStore.create storage

            expectRefusedRead "GetProfile" (fun () -> store.GetProfile FlagScope.Platform |> run |> ignore)
            expectQuarantined storage platform name
        }

        test "ConfigStore" {
            let scope: StorageScope = {
                ScopeId = "t-config"
                Container = "team-t-config"
                Persist = true
            }

            let name = $"config/{scope.Container}/billing.json"
            let storage = planted platform name
            let store = ToolUp.Platform.Config.ConfigStore.createWithLogger storage silentLogger

            expectRefusedRead "GetRaw" (fun () -> store.GetRaw(scope, "billing") |> run |> ignore)
            expectQuarantined storage platform name
        }

        test "WorkerKeyRegistry" {
            let name = "external-compute/worker-keys/w1/k1.json"
            let storage = planted platform name
            let registry = BlobWorkerKeyRegistry(storage) :> IWorkerKeyRegistry

            expectRefusedRead "Resolve" (fun () -> registry.Resolve("w1", "k1") |> run |> ignore)
            expectRefusedRead "List" (fun () -> registry.List() |> run |> ignore)
            expectError "Approve" (registry.Approve("w1", "k1", "admin") |> run)
            expectQuarantined storage platform name
        }

        test "ExternalHandleStore — an unreadable record is absent on the ingress path, and never claimed" {
            let handleId = Guid.NewGuid()
            let name = $"external-compute/handles/s1/{handleId}.json"
            let storage = planted platform name

            storage.Upload(platform, $"external-compute/handle-index/{handleId}", Encoding.UTF8.GetBytes "s1")
            |> run
            |> ignore

            let store = BlobExternalHandleStore(storage) :> IExternalHandleStore

            Expect.isNone (store.Resolve handleId |> run) "the callback is refused, not thrown out of the ingress path"
            Expect.isFalse (store.MarkTerminal handleId |> run) "nothing is claimed"
            expectQuarantined storage platform name
        }

        test "SessionRegistry — a record that cannot be decoded is never read as absent" {
            let sessionId = String('a', 64)
            let name = $"sessions/t1/{sessionId}.json"
            let storage = planted platform name

            let registry =
                ToolUp.Platform.Scope.SessionRegistry.BlobBackedSessionRegistry.create storage (Some silentLogger) 30

            match registry.Revoke("t1", sessionId, UserId "admin") |> run with
            | Error(SessionError.StoreUnavailable _) -> ()
            | other -> failtest $"expected StoreUnavailable, not NotFound, got %A{other}"

            expectQuarantined storage platform name
        }

        test "OAuthFlowHandler credential metadata" {
            let name = "data-sources/t1/credentials/src-1.json"
            let storage = planted platform name

            expectRefusedRead "loadCredentialMetadata" (fun () ->
                ToolUp.Platform.Jobs.OAuthFlowHandler.loadCredentialMetadata storage "t1" "src-1"
                |> run
                |> ignore)

            expectQuarantined storage platform name
        }

        test "WebhookRegistry" {
            let subscriptionId = Guid.NewGuid()
            let name = $"webhooks/t1/subscriptions/{subscriptionId:N}.json"
            let storage = planted platform name
            let registry = ToolUp.Platform.Jobs.WebhookRegistry.createRegistry storage

            expectRefusedRead "GetSubscription" (fun () ->
                registry.GetSubscription("t1", subscriptionId) |> run |> ignore)

            expectQuarantined storage platform name
        }

        test "WhatsAppTemplateRegistry — an undecodable template is not vouched for, and is set aside" {
            let name = "whatsapp-templates/welcome.json"
            let storage = planted platform name

            let registry =
                ToolUp.Platform.Jobs.WhatsAppTemplateRegistry.blobBacked storage (Some silentLogger)

            Expect.isNone (registry.GetTemplate "welcome" |> run) "the send is refused"
            expectQuarantined storage platform name
        }

        test "DataSourceConfigStore" {
            let name = "data-sources/t1/configs/src-1.json"
            let storage = planted platform name
            let store = DataSourceConfigStore.create storage

            expectRefusedRead "Get" (fun () -> store.Get("t1", "src-1") |> run |> ignore)
            Expect.isEmpty (store.List "t1" |> run) "the listing leaves the unreadable configuration out"
            expectQuarantined storage platform name
        }

        test "MigrationStatusStore" {
            let name = ToolUp.Platform.Config.MigrationStatusStore.statusBlob "t1" "orders"
            let storage = planted platform name

            let store =
                ToolUp.Platform.Config.MigrationStatusStore.BlobMigrationStatusStore(storage)
                :> ToolUp.Platform.Config.IMigrationStatusStore

            expectRefusedRead "Read" (fun () -> store.Read(TeamId "t1", "orders") |> run |> ignore)
            expectQuarantined storage platform name
        }

        test "ComputeBudgetStore — an undecodable budget stays unrestricted by decision, and is set aside" {
            let name = ComputeBudgetLayout.budgetBlob "t1"
            let storage = planted ComputeBudgetLayout.DefaultContainer name
            let store = BlobComputeBudgetStore(storage, silentLogger) :> IComputeBudgetStore

            Expect.equal (store.GetBudget "t1" |> run) ComputeBudget.unrestricted "the recorded fail-open direction"
            expectQuarantined storage ComputeBudgetLayout.DefaultContainer name
        }

        test "JobTriggerWatermark — an undecodable cursor is Unreadable, never Missing, and the next flush replaces it" {
            let name = "job-triggers/t1.cursor"
            let storage = planted platform name

            let watermark =
                ToolUp.Platform.Jobs.JobTriggerWatermark.JobTriggerWatermark(storage, silentLogger)

            match watermark.LoadPersisted "t1" |> run with
            | ToolUp.Platform.Jobs.JobTriggerWatermark.Unreadable _ -> ()
            | other -> failtest $"expected Unreadable, got %A{other}"

            let cursor =
                ToolUp.Platform.Jobs.JobTriggerWatermark.JobTriggerCursor.at (
                    DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc)
                )

            watermark.Seed("t1", cursor, true)
            watermark.FlushDirty() |> run

            match watermark.LoadPersisted "t1" |> run with
            | ToolUp.Platform.Jobs.JobTriggerWatermark.Loaded loaded ->
                Expect.equal loaded cursor "the flushed cursor reads back"
            | other -> failtest $"expected Loaded, got %A{other}"

            expectSetAside storage platform name
        }

        test "NotificationAddressBook — an undecodable contact is no address by decision, and is set aside" {
            let name = "contacts/t1/u1.json"
            let storage = planted platform name
            let warned = ResizeArray<string>()

            let logger =
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn message = warned.Add message
                    member _.Error(_, _) = ()
                }

            let book =
                ToolUp.Platform.Jobs.NotificationAddressBook.BlobBackedNotificationAddressBook(storage, Some logger)
                :> ToolUp.Platform.Jobs.INotificationAddressBook

            Expect.isNone (book.ResolveEmail(RecipientId.User(UserId "u1"), "t1") |> run) "the one send is skipped"
            Expect.isNonEmpty warned "and never silently, as though the user had no address"
            expectQuarantined storage platform name
        }

        test
            "NotificationPreferenceStore — an undecodable watermark map is set aside, and the next digest starts it again" {
            let name = "notification-preferences/t1/digest/u1.json"
            let storage = planted platform name

            let store =
                ToolUp.Platform.Jobs.BlobNotificationPreferenceStore.create storage (Some silentLogger)

            Expect.isEmpty (store.GetDigestWatermarks("t1", UserId "u1") |> run) "no watermark is vouched for"

            let sentAt = DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc)

            Expect.equal
                (store.SetDigestWatermark("t1", UserId "u1", DigestFrequency.Daily, sentAt)
                 |> run)
                (Ok())
                "the digest job is not wedged behind the corrupt map"

            Expect.equal
                (store.GetDigestWatermarks("t1", UserId "u1") |> run)
                (Map [ DigestFrequency.toWireString DigestFrequency.Daily, sentAt ])
                "the new watermark reads back"

            expectSetAside storage platform name
        }

        test
            "LifecycleLedger — an undecodable ledger re-runs every hook, and the next record starts it again, same bytes" {
            let name = "_tenant-lifecycle-ledger/t1/Deprovisioning.json"
            let storage = planted platform name

            let ledger = ToolUp.Platform.Compose.BlobBackedLifecycleLedger.create storage

            Expect.isEmpty
                (ledger.GetCompleted("t1", TenantLifecyclePhase.Deprovisioning) |> run)
                "nothing is vouched for: a full, idempotent re-run"

            ledger.Record(
                "t1",
                TenantLifecyclePhase.Deprovisioning,
                "erase",
                ToolUp.Platform.Compose.LedgerDisposition.Completed
            )
            |> run

            ledger.Record(
                "t1",
                TenantLifecyclePhase.Deprovisioning,
                "cancel-jobs",
                ToolUp.Platform.Compose.LedgerDisposition.Skipped
            )
            |> run

            Expect.equal
                (ledger.GetCompleted("t1", TenantLifecyclePhase.Deprovisioning) |> run)
                (set [ "cancel-jobs"; "erase" ])
                "both hooks are recorded"

            Expect.equal
                (storage.Download(platform, name) |> run)
                (Ok(Encoding.UTF8.GetBytes "[\"cancel-jobs\",\"erase\"]"))
                "the ledger's bytes at rest are the flat array it has always written"

            expectSetAside storage platform name
        }

        test "LifecycleSummaryStore — an undecodable summary reads as none by decision, and the next run's replaces it" {
            let name = "_tenant-lifecycle/t1.json"
            let storage = planted platform name

            let store = ToolUp.Platform.Compose.BlobBackedLifecycleSummaryStore.create storage

            Expect.isNone (store.GetLast "t1" |> run) "the admin read path reports no last summary"

            let summary: LifecycleSummary = {
                ScopeId = "t1"
                Phase = TenantLifecyclePhase.Deprovisioning
                Outcomes = []
                TotalElapsedMs = 12L
            }

            store.SetLast("t1", summary) |> run
            Expect.equal (store.GetLast "t1" |> run) (Some summary) "the new summary reads back"
            expectSetAside storage platform name
        }

        test "AuditReplicator cursor — an undecodable cursor re-delivers by decision, and the next save replaces it" {
            let name = "audit-replicator/s1/t1.cursor"
            let storage = planted platform name

            let cursors =
                ToolUp.Platform.Audit.AuditReplicator.BlobAuditReplicatorCursorStore(storage, silentLogger)
                :> ToolUp.Platform.Audit.AuditReplicator.IAuditReplicatorCursorStore

            Expect.equal
                (cursors.Load("s1", "t1") |> run)
                ToolUp.Platform.Audit.AuditReplicatorCursor.empty
                "at-least-once: the scope's history is re-delivered to an idempotent sink"

            let cursor: ToolUp.Platform.Audit.AuditReplicatorCursor = {
                LastDeliveredAt = DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc)
                LastDeliveredEventId = Guid.NewGuid()
            }

            cursors.Save("s1", "t1", cursor) |> run
            Expect.equal (cursors.Load("s1", "t1") |> run) cursor "the saved cursor reads back"
            expectSetAside storage platform name
        }

        test "BackgroundExportStore — the status sidecar keeps its bytes, and one that does not decode refuses" {
            let ticketFor (guid: string) =
                let scope =
                    Convert.ToBase64String(Encoding.UTF8.GetBytes "t1").TrimEnd('=').Replace('+', '-').Replace('/', '_')

                $"dsr.{scope}.{guid}"

            let good = Guid.NewGuid().ToString "N"
            let goodName = $"data-subject-requests/t1/{good}.status"

            let statusBytes (status: string) =
                Encoding.UTF8.GetBytes(
                    "{\"status\":\""
                    + status
                    + "\",\"subjectUserId\":\"u1\",\"requestId\":\"r1\",\"createdAt\":\"2099-01-01T00:00:00.0000000\\u002B00:00\",\"expiresAt\":\"2099-01-08T00:00:00.0000000\\u002B00:00\"}"
                )

            let bad = Guid.NewGuid().ToString "N"
            let badName = $"data-subject-requests/t1/{bad}.status"
            let storage = planted platform badName
            storage.Upload(platform, goodName, statusBytes "Preparing") |> run |> ignore

            let store = ToolUp.Platform.Audit.BlobBackedBackgroundExportStore.create storage

            Expect.equal (store.GetStatus(ticketFor good) |> run) ExportStatus.Preparing "today's bytes read"
            store.Cancel(ticketFor good) |> run

            Expect.equal
                (storage.Download(platform, goodName) |> run)
                (Ok(statusBytes "Cancelled"))
                "a transition writes the same shape, field order and escapes included"

            expectRefusedRead "GetStatus" (fun () -> store.GetStatus(ticketFor bad) |> run |> ignore)
            expectRefusedRead "Download" (fun () -> store.Download(ticketFor bad) |> run |> ignore)

            Expect.throws
                (fun () -> store.Cancel(ticketFor bad) |> run)
                "a transition refuses rather than writing over it"

            expectQuarantined storage platform badName
        }

        test
            "ProviderProfile — an undecodable profile reads as unconfigured by decision, health refuses, and a save replaces it" {
            let scope: StorageScope = {
                ScopeId = "t-profile"
                Container = "team-t-profile"
                Persist = true
            }

            let name = "provider-profile.json"
            let storage = planted scope.Container name
            let profiles = ToolUp.Platform.Infra.BlobProviderProfile.create storage

            Expect.isNone (profiles.Get scope |> run) "the consumer falls back per its own policy"

            expectError "SetEntryHealth" (profiles.SetEntryHealth(scope, "primary", ProviderHealth.unknown) |> run)

            expectQuarantined storage scope.Container name

            let profile = {
                ProviderProfile.empty () with
                    UpdatedAt = DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc)
            }

            Expect.equal (profiles.Set(scope, profile) |> run) (Ok()) "a save is not wedged behind the corrupt profile"
            Expect.equal (profiles.Get scope |> run) (Some profile) "the saved profile reads back"
            expectSetAside storage scope.Container name
        }

        test "BudgetLedger — an undecodable row is set aside and the period starts again, never wedging admission" {
            let key = BudgetLedgerKey.create "test-budget" "t1" "2026-11"
            let name = BudgetLedgerLayout.usageBlob key
            let storage = planted platform name
            let ledger = BlobBudgetLedger(storage, silentLogger) :> IBudgetLedger

            Expect.equal (ledger.ReadUsage key |> run).InFlight 0 "an unusable row reads as zero consumption"

            match ledger.Reserve(key, 2M, (fun _ -> Ok())) |> run with
            | Ok usage -> Expect.equal usage.InFlight 1 "the reservation is admitted over the restarted row"
            | Error denial -> failtest $"expected the reservation to be admitted, got %A{denial}"

            let after = ledger.ReadUsage key |> run
            Expect.equal (after.InFlight, after.Spent) (1, 2M) "and it is durable"
            expectSetAside storage platform name
        }

        test "UsageLog — a rollup that does not decode refuses the query, and the next flush sets it aside" {
            let day = DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc)
            let name = "usage/t1/2026-11-09.json"
            let storage = planted platform name

            let policy: ToolUp.Platform.Usage.BatchFlushPolicy = {
                FlushAtCount = 1
                FlushInterval = TimeSpan.FromMilliseconds 50.0
                ChannelCapacity = 16
                ProducerWaitTimeout = TimeSpan.FromSeconds 1.0
            }

            use flusher =
                new ToolUp.Platform.Infra.UsageLog.UsageBatchFlusher(storage, policy, silentLogger, None)

            let log =
                ToolUp.Platform.Infra.UsageLog.BlobUsageLog(storage, flusher) :> ToolUp.Platform.Infra.Usage.IUsageLog

            expectRefusedRead "Query" (fun () -> log.Query("t1", None, None) |> run |> ignore)

            let record: ToolUp.Platform.Usage.UsageRecord = {
                RecordId = Guid.NewGuid()
                ScopeId = "t1"
                ResourceKind = "test"
                Quantity = 3M
                Unit = "unit"
                Origin = None
                Metadata = Map.empty
                Timestamp = day
            }

            log.Record record |> run
            flusher.DrainAndFlush() |> run

            Expect.equal (log.Query("t1", None, None) |> run) [ record ] "the new record landed in the restarted day"
            expectSetAside storage platform name
        }

        test "ConfigDriftDetector — an unparseable snapshot is replaced, and one that cannot be read is kept" {
            let name = "_deploy/last-config.json"

            let audit =
                { new IAuditLog with
                    member _.Record(_, _) = async { return () }
                    member _.GetAuditTrail(_, _, _) = async { return [] }
                }

            let detect storage =
                ToolUp.Platform.Config.ConfigDriftDetector.run storage audit silentLogger ServerConfig.defaults []
                |> run

            let storage = planted platform name
            detect storage
            expectSetAside storage platform name

            let backing = InMemoryBlobStorage()
            let before = Encoding.UTF8.GetBytes "{ \"schema\": 1 }"
            (backing :> IBlobStorage).Upload(platform, name, before) |> run |> ignore

            let failing =
                { new IBlobStorage with
                    member _.CanComposeFrom = false
                    member _.ComposeFrom(_, _, _) = composeNotSupported "test double"
                    member _.Download(_, _) = async { return Error "simulated outage" }

                    member _.Upload(c, n, b) =
                        (backing :> IBlobStorage).Upload(c, n, b)

                    member _.Delete(c, n) = (backing :> IBlobStorage).Delete(c, n)
                    member _.List(c, p) = (backing :> IBlobStorage).List(c, p)
                    member _.Exists(c, n) = (backing :> IBlobStorage).Exists(c, n)

                    member _.GetMetadata(c, n) =
                        (backing :> IBlobStorage).GetMetadata(c, n)

                    member _.DownloadRange(c, n, o, l) =
                        (backing :> IBlobStorage).DownloadRange(c, n, o, l)

                    member _.Erase(c, p, policy, dryRun) =
                        (backing :> IBlobStorage).Erase(c, p, policy, dryRun)
                }

            detect failing

            Expect.equal
                ((backing :> IBlobStorage).Download(platform, name) |> run)
                (Ok before)
                "the previous snapshot is kept for the next startup's comparison"
        }
    ]