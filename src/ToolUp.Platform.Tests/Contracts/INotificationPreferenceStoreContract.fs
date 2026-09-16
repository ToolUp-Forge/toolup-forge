module ToolUp.Platform.Tests.Contracts.INotificationPreferenceStoreContract

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 441 — INotificationPreferenceStore contract pack ───────────
//
// The conformance bar for `INotificationPreferenceStore`: preference
// round-trip with an empty default, scope isolation on every member,
// the pending queue's ordering / id-idempotence / removal semantics,
// user and scope discovery, and the digest watermarks. Bound here to
// the blob-backed default over an in-memory `IBlobStorage`; a second
// implementation binds `contractTests` with its own factory.

let private noopLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private email (userIds: string list) : Notification =
    TransactionalEmail {
        RecipientUserIds = userIds
        Content = InlineEmail("Subject", "Body", None)
        CorrelationId = None
    }

let private pending (userId: string) (queuedAt: DateTime) (disposition: PendingDisposition) : PendingNotification = {
    Id = Guid.NewGuid()
    UserId = userId
    Category = "reports.summary"
    Channel = PreferenceChannel.Email
    Notification = email [ userId ]
    QueuedAt = queuedAt
    Disposition = disposition
}

let private t0 = DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc)

/// The reusable pack: `factory` yields a fresh, empty store per test.
let contractTests (name: string) (factory: unit -> INotificationPreferenceStore) =
    let fresh () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        factory (), $"team-{suffix}", $"user-{suffix}"

    testList $"{name} — INotificationPreferenceStore contract" [

        testCaseAsync "GetPreferences is empty (not an error) before any save"
        <| async {
            let store, scope, user = fresh ()
            let! result = store.GetPreferences(scope, user)
            Expect.equal result (Ok UserNotificationPreferences.empty) "empty default"
        }

        testCaseAsync "SavePreferences round-trips cells and quiet hours"
        <| async {
            let store, scope, user = fresh ()

            let prefs =
                UserNotificationPreferences.empty
                |> UserNotificationPreferences.setDelivery "reports.summary" PreferenceChannel.Email (Digest Daily)
                |> UserNotificationPreferences.setDelivery "reports.summary" PreferenceChannel.Sms Muted

            let prefs = {
                prefs with
                    QuietHours =
                        Some {
                            StartMinute = 1320
                            EndMinute = 420
                            TimeZoneId = "UTC"
                        }
            }

            let! saved = store.SavePreferences(scope, user, prefs)
            Expect.equal saved (Ok()) "saved"
            let! loaded = store.GetPreferences(scope, user)
            Expect.equal loaded (Ok prefs) "round-trip"
        }

        testCaseAsync "SavePreferences replaces the prior record wholesale"
        <| async {
            let store, scope, user = fresh ()

            let first =
                UserNotificationPreferences.empty
                |> UserNotificationPreferences.setDelivery "a" PreferenceChannel.Email Muted

            let! _ = store.SavePreferences(scope, user, first)
            let! _ = store.SavePreferences(scope, user, UserNotificationPreferences.empty)
            let! loaded = store.GetPreferences(scope, user)
            Expect.equal loaded (Ok UserNotificationPreferences.empty) "replaced, not merged"
        }

        testCaseAsync "preferences are scope-isolated: the same user in another scope reads empty"
        <| async {
            let store, scope, user = fresh ()

            let prefs =
                UserNotificationPreferences.empty
                |> UserNotificationPreferences.setDelivery "a" PreferenceChannel.Email Muted

            let! _ = store.SavePreferences(scope, user, prefs)
            let! other = store.GetPreferences(scope + "-other", user)
            Expect.equal other (Ok UserNotificationPreferences.empty) "other scope sees nothing"
        }

        testCaseAsync "Enqueue + ListPending returns items oldest first"
        <| async {
            let store, scope, user = fresh ()
            let later = pending user (t0.AddMinutes 5.0) (PendingDisposition.ForDigest Daily)
            let earlier = pending user t0 (PendingDisposition.ForDigest Daily)
            let! _ = store.Enqueue(scope, later)
            let! _ = store.Enqueue(scope, earlier)
            let! items = store.ListPending(scope, user)
            Expect.equal (items |> List.map _.Id) [ earlier.Id; later.Id ] "oldest first"
            Expect.equal items[0] earlier "item round-trips, notification included"
        }

        testCaseAsync "Enqueue is idempotent on the item id"
        <| async {
            let store, scope, user = fresh ()
            let item = pending user t0 (PendingDisposition.ForDigest Hourly)
            let! _ = store.Enqueue(scope, item)
            let! _ = store.Enqueue(scope, item)
            let! items = store.ListPending(scope, user)
            Expect.equal (List.length items) 1 "one item"
        }

        testCaseAsync "Remove drops exactly the named ids and is idempotent"
        <| async {
            let store, scope, user = fresh ()
            let a = pending user t0 (PendingDisposition.ForDigest Daily)
            let b = pending user (t0.AddMinutes 1.0) (PendingDisposition.ForDigest Daily)
            let! _ = store.Enqueue(scope, a)
            let! _ = store.Enqueue(scope, b)
            let! removed = store.Remove(scope, user, [ a.Id ])
            Expect.equal removed (Ok()) "removed"
            let! items = store.ListPending(scope, user)
            Expect.equal (items |> List.map _.Id) [ b.Id ] "b remains"
            let! again = store.Remove(scope, user, [ a.Id; Guid.NewGuid() ])
            Expect.equal again (Ok()) "missing ids are not an error"
            let! empty = store.Remove(scope, user, [])
            Expect.equal empty (Ok()) "empty list is a no-op"
        }

        testCaseAsync "pending queues are scope-isolated"
        <| async {
            let store, scope, user = fresh ()
            let! _ = store.Enqueue(scope, pending user t0 (PendingDisposition.ForDigest Daily))
            let! other = store.ListPending(scope + "-other", user)
            Expect.isEmpty other "other scope sees nothing"
        }

        testCaseAsync "ListPendingUsers and ListScopesWithPending discover held work"
        <| async {
            let store, scope, user = fresh ()
            let! none = store.ListPendingUsers scope
            Expect.isEmpty none "nothing yet"
            let! _ = store.Enqueue(scope, pending user t0 (PendingDisposition.ForDigest Daily))
            let! _ = store.Enqueue(scope, pending (user + "-2") t0 (PendingDisposition.ForDigest Daily))

            let! _ =
                store.Enqueue(scope, pending (user + "-2") (t0.AddMinutes 1.0) (PendingDisposition.ForDigest Weekly))

            let! users = store.ListPendingUsers scope
            Expect.equal (List.sort users) (List.sort [ user; user + "-2" ]) "both users, each once"
            let! scopes = store.ListScopesWithPending()
            Expect.contains scopes scope "scope discovered"
        }

        testCaseAsync "digest watermarks are empty until set and then round-trip per frequency"
        <| async {
            let store, scope, user = fresh ()
            let! before = store.GetDigestWatermarks(scope, user)
            Expect.isTrue (Map.isEmpty before) "empty"
            let! _ = store.SetDigestWatermark(scope, user, Daily, t0)
            let! _ = store.SetDigestWatermark(scope, user, Weekly, t0.AddDays 1.0)
            let! _ = store.SetDigestWatermark(scope, user, Daily, t0.AddHours 1.0)
            let! after = store.GetDigestWatermarks(scope, user)
            Expect.equal (Map.tryFind "daily" after) (Some(t0.AddHours 1.0)) "daily replaced"
            Expect.equal (Map.tryFind "weekly" after) (Some(t0.AddDays 1.0)) "weekly kept"
        }
    ]

/// The blob-backed default over the in-memory `IBlobStorage` double.
let private freshBlobStore () : INotificationPreferenceStore =
    BlobNotificationPreferenceStore.create (InMemoryBlobStorage() :> IBlobStorage) (Some noopLogger)

let tests =
    testList "INotificationPreferenceStore contract" [
        contractTests "BlobNotificationPreferenceStore" freshBlobStore

        testCaseAsync "BlobNotificationPreferenceStore reads a corrupt preference blob as empty"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobNotificationPreferenceStore.create blobs (Some noopLogger)

            let! _ =
                blobs.Upload(
                    "_platform",
                    "notification-preferences/team-x/prefs/user-x.json",
                    Encoding.UTF8.GetBytes "{not json"
                )

            let! loaded = store.GetPreferences("team-x", "user-x")
            Expect.equal loaded (Ok UserNotificationPreferences.empty) "corrupt reads as empty, never as an error"
        }

        testCaseAsync "BlobNotificationPreferenceStore keys every blob under the scope in the _platform container"
        <| async {
            let blobs = InMemoryBlobStorage() :> IBlobStorage
            let store = BlobNotificationPreferenceStore.create blobs (Some noopLogger)
            let! _ = store.SavePreferences("team-x", "user/with/slashes", UserNotificationPreferences.empty)
            let! _ = store.Enqueue("team-x", pending "user/with/slashes" t0 (PendingDisposition.ForDigest Daily))
            let! names = blobs.List("_platform", "notification-preferences/team-x/")
            Expect.equal (List.length names) 2 "one prefs blob + one pending blob"

            for name in names do
                Expect.isTrue (name.StartsWith "notification-preferences/team-x/") $"scoped: {name}"

            let! users = store.ListPendingUsers "team-x"
            Expect.equal users [ "user/with/slashes" ] "an id with slashes survives the segment encoding"
        }
    ]