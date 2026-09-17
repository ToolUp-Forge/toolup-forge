module ToolUp.Platform.Tests.InProcess.NotificationPreferenceTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 441 — send-path filter + digest job ────────────────────────
//
// The filter's decision table per recipient (mute / digest / quiet
// hours / non-suppressible bypass / uncategorised pass-through / fail
// open), the ambient category scope, the quiet-hours clock, and the
// digest job's release + bucket semantics including idempotency on
// re-run. Everything runs over the real blob-backed store on the
// in-memory `IBlobStorage` double, so the filter → store → job chain
// is exercised end to end.

// ─── Doubles ─────────────────────────────────────────────────────────

type private CapturingChannel() =
    let published = ConcurrentQueue<string * Notification>()

    member _.Published = published |> Seq.toList

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue((scopeId, notification)) }
        member _.Subscribe(_, _) = async { return (Guid.NewGuid(): NotificationSubscriptionId) }
        member _.Unsubscribe _ = async { () }

type private CapturingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()

    member _.Skips =
        events
        |> Seq.choose (fun (_, e) ->
            match e with
            | NotificationSilentlySkipped p -> Some p
            | _ -> None)
        |> Seq.toList

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private CapturingLogger() =
    let lines = ConcurrentQueue<string>()
    member _.Lines = lines |> Seq.toList

    interface ILogger with
        member _.Debug m = lines.Enqueue("DEBUG " + m)
        member _.Info m = lines.Enqueue("INFO " + m)
        member _.Warn m = lines.Enqueue("WARN " + m)
        member _.Error(m, _) = lines.Enqueue("ERROR " + m)

/// A store whose `Enqueue` always fails — the fail-open probe.
type private EnqueueFailingStore(inner: INotificationPreferenceStore) =
    interface INotificationPreferenceStore with
        member _.GetPreferences(s, u) = inner.GetPreferences(s, u)
        member _.SavePreferences(s, u, p) = inner.SavePreferences(s, u, p)
        member _.Enqueue(_, _) = async { return Error "disk full" }
        member _.ListPending(s, u) = inner.ListPending(s, u)
        member _.Remove(s, u, ids) = inner.Remove(s, u, ids)
        member _.ListPendingUsers s = inner.ListPendingUsers s
        member _.ListScopesWithPending() = inner.ListScopesWithPending()
        member _.GetDigestWatermarks(s, u) = inner.GetDigestWatermarks(s, u)
        member _.SetDigestWatermark(s, u, f, t) = inner.SetDigestWatermark(s, u, f, t)

let private scope = "team-441"
let private t0 = DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc)

let private utcOnly (id: string) =
    if id = "UTC" then Some TimeZoneInfo.Utc else None

let private categories = [
    NotificationCategory.create "reports.summary" "Report summaries"
    NotificationCategory.nonSuppressible "security.password-reset" "Password resets"
]

let private email (userIds: string list) : Notification =
    TransactionalEmail {
        RecipientUserIds = userIds
        Content = InlineEmail("Subject", "Body", None)
        CorrelationId = Some "corr-1"
    }

let private recipientsOf (n: Notification) =
    fst (NotificationPreferenceFilter.recipientsOf n)

type private Harness = {
    Store: INotificationPreferenceStore
    Inner: CapturingChannel
    Audit: CapturingAuditLog
    Logger: CapturingLogger
    Filter: INotificationChannel
}

let private harnessAt (now: DateTime) (store: INotificationPreferenceStore option) : Harness =
    let blobs = InMemoryBlobStorage() :> IBlobStorage
    let logger = CapturingLogger()

    let store =
        store
        |> Option.defaultWith (fun () -> BlobNotificationPreferenceStore.create blobs (Some(logger :> ILogger)))

    let inner = CapturingChannel()
    let audit = CapturingAuditLog()

    let filter =
        NotificationPreferenceFilter(
            inner,
            store,
            categories,
            Some(audit :> IAuditLog),
            logger,
            (fun () -> now),
            utcOnly
        )

    {
        Store = store
        Inner = inner
        Audit = audit
        Logger = logger
        Filter = filter
    }

let private harness () = harnessAt t0 None

let private savePrefs (h: Harness) (userId: string) (prefs: UserNotificationPreferences) =
    h.Store.SavePreferences(scope, userId, prefs)
    |> Async.RunSynchronously
    |> ignore

let private cell category channel delivery =
    UserNotificationPreferences.empty
    |> UserNotificationPreferences.setDelivery category channel delivery

let private quiet (startMinute: int) (endMinute: int) (zone: string) : UserNotificationPreferences = {
    UserNotificationPreferences.empty with
        QuietHours =
            Some {
                StartMinute = startMinute
                EndMinute = endMinute
                TimeZoneId = zone
            }
}

let private publishAs (h: Harness) (category: string) (n: Notification) =
    NotificationCategoryScope.publish h.Filter category scope n

let private jobContext (now: DateTime) : JobContext = {
    JobId = Guid.NewGuid()
    ScopeId = "_platform"
    AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
    Attempt = 1
    Trigger = Manual
    TriggerSource = TriggerSource.ScheduledManually "_system"
    ScheduledAt = now
    RunningAt = now
    Payload = ""
    DeadLetterDestination = None
}

let private runDigest (h: Harness) (now: DateTime) =
    let handler =
        NotificationDigest.create h.Store h.Inner NotificationPreferenceSettings.defaults h.Logger

    handler.Execute(jobContext now) |> Async.RunSynchronously

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 441 — notification preferences" [

        testList "NotificationCategoryScope" [
            test "current is None outside any push and restores the prior value LIFO" {
                Expect.isNone (NotificationCategoryScope.current ()) "none outside"

                use _outer = NotificationCategoryScope.push "outer"
                Expect.equal (NotificationCategoryScope.current ()) (Some "outer") "outer"

                let nested () =
                    use _inner = NotificationCategoryScope.push "inner"
                    Expect.equal (NotificationCategoryScope.current ()) (Some "inner") "inner"

                nested ()

                Expect.equal (NotificationCategoryScope.current ()) (Some "outer") "restored"
            }

            test "publish tags the call and clears the scope afterwards" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email Muted)
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "muted, so the tagged publish reached nobody"
                Expect.isNone (NotificationCategoryScope.current ()) "scope cleared"
            }
        ]

        testList "NotificationPreferenceFilter" [
            test "an uncategorised transactional publish passes through unchanged" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email Muted)
                h.Filter.Publish(scope, email [ "alice" ]) |> Async.RunSynchronously
                Expect.equal h.Inner.Published [ scope, email [ "alice" ] ] "delivered as published"
                Expect.isEmpty h.Audit.Skips "nothing audited"
            }

            test "an in-app notification passes through even inside a category scope" {
                let h = harness ()
                let n = SystemMessage(SystemMessageLevel.Info, "hi")
                publishAs h "reports.summary" n |> Async.RunSynchronously
                Expect.equal h.Inner.Published [ scope, n ] "SSE kinds are not governed"
            }

            test "Muted drops the recipient and audits user_muted with a hashed recipient" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email Muted)
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "nothing delivered"
                let skip = Expect.wantSome (List.tryHead h.Audit.Skips) "one skip audit"
                Expect.equal skip.Reason NotificationPreferenceFilter.MutedReason "reason"
                Expect.equal skip.NotificationKind "Email" "channel family"
                Expect.equal skip.ScopeId scope "scope"
                Expect.equal skip.CorrelationId (Some "corr-1") "correlation forwarded"

                Expect.equal
                    skip.RecipientHashes
                    [ NotificationPreferenceFilter.hashRecipient "alice" ]
                    "hashed, not raw"

                Expect.isFalse (skip.RecipientHashes |> List.contains "alice") "no raw id"
            }

            test "Muted on one channel leaves another channel delivering" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email Muted)

                let sms =
                    TransactionalSms {
                        RecipientUserIds = [ "alice" ]
                        Body = "b"
                        CorrelationId = None
                    }

                publishAs h "reports.summary" sms |> Async.RunSynchronously
                Expect.equal h.Inner.Published [ scope, sms ] "SMS unaffected by an Email mute"
            }

            test "Digest holds the recipient's copy, audits digest_queued, and delivers nothing now" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email (Digest Daily))
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "held"
                let held = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                let item = Expect.wantSome (List.tryHead held) "one pending item"
                Expect.equal item.Disposition (PendingDisposition.ForDigest Daily) "digest bucket"
                Expect.equal item.Category "reports.summary" "category"
                Expect.equal item.Channel PreferenceChannel.Email "channel"
                Expect.equal (recipientsOf item.Notification) [ "alice" ] "narrowed to the recipient"
                Expect.equal item.QueuedAt t0 "queued at the filter's clock"

                Expect.equal
                    (h.Audit.Skips |> List.map _.Reason)
                    [ NotificationPreferenceFilter.DigestQueuedReason ]
                    "audited"
            }

            test "mixed recipients: kept ones are delivered on a narrowed copy, the rest held or dropped" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email Muted)
                savePrefs h "bob" (cell "reports.summary" PreferenceChannel.Email (Digest Hourly))

                publishAs h "reports.summary" (email [ "alice"; "bob"; "carol" ])
                |> Async.RunSynchronously

                match h.Inner.Published with
                | [ s, n ] ->
                    Expect.equal s scope "scope"
                    Expect.equal (recipientsOf n) [ "carol" ] "only carol"
                | other -> failtestf "expected one narrowed publish, got %A" other

                let bobHeld = h.Store.ListPending(scope, "bob") |> Async.RunSynchronously
                Expect.equal (List.length bobHeld) 1 "bob held"
                let aliceHeld = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                Expect.isEmpty aliceHeld "alice dropped, not held"

                Expect.equal
                    (h.Audit.Skips |> List.map _.Reason |> List.sort)
                    (List.sort [
                        NotificationPreferenceFilter.MutedReason
                        NotificationPreferenceFilter.DigestQueuedReason
                    ])
                    "one audit per outcome class"
            }

            test "quiet hours defer an Immediate send to the window's end and audit quiet_hours_deferred" {
                // 22:00–07:00 UTC; the clock says 23:30 UTC.
                let h = harnessAt (DateTime(2026, 9, 16, 23, 30, 0, DateTimeKind.Utc)) None
                savePrefs h "alice" (quiet 1320 420 "UTC")
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "held"
                let held = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                let item = Expect.wantSome (List.tryHead held) "one pending item"

                Expect.equal
                    item.Disposition
                    (PendingDisposition.DeferredUntil(DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc)))
                    "released at 07:00 UTC next day"

                Expect.equal
                    (h.Audit.Skips |> List.map _.Reason)
                    [ NotificationPreferenceFilter.QuietHoursDeferredReason ]
                    "audited"
            }

            test "outside quiet hours an Immediate send is delivered" {
                let h = harnessAt (DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)) None
                savePrefs h "alice" (quiet 1320 420 "UTC")
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.equal (List.length h.Inner.Published) 1 "delivered"
                Expect.isEmpty h.Audit.Skips "nothing audited"
            }

            test "an unresolvable time zone disables quiet hours rather than holding mail" {
                let h = harnessAt (DateTime(2026, 9, 16, 23, 30, 0, DateTimeKind.Utc)) None
                savePrefs h "alice" (quiet 1320 420 "Not/AZone")
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.equal (List.length h.Inner.Published) 1 "delivered"
            }

            test "a non-suppressible category ignores mute, digest and quiet hours" {
                let h = harnessAt (DateTime(2026, 9, 16, 23, 30, 0, DateTimeKind.Utc)) None

                savePrefs h "alice" {
                    cell "security.password-reset" PreferenceChannel.Email Muted with
                        QuietHours =
                            Some {
                                StartMinute = 1320
                                EndMinute = 420
                                TimeZoneId = "UTC"
                            }
                }

                publishAs h "security.password-reset" (email [ "alice" ])
                |> Async.RunSynchronously

                Expect.equal h.Inner.Published [ scope, email [ "alice" ] ] "delivered untouched"
                Expect.isEmpty h.Audit.Skips "nothing audited"
            }

            test "a category no module declared is still governed (treated as suppressible)" {
                let h = harness ()
                savePrefs h "alice" (cell "undeclared.thing" PreferenceChannel.Email Muted)
                publishAs h "undeclared.thing" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "muted"
                Expect.isTrue (h.Logger.Lines |> List.exists (fun l -> l.Contains "not declared")) "logged"
            }

            test "a hold that cannot be written delivers now (fail open) with a warning" {
                let blobs = InMemoryBlobStorage() :> IBlobStorage
                let real = BlobNotificationPreferenceStore.create blobs None

                let h =
                    harnessAt t0 (Some(EnqueueFailingStore real :> INotificationPreferenceStore))

                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email (Digest Daily))
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.equal (List.length h.Inner.Published) 1 "delivered rather than lost"
                Expect.isEmpty h.Audit.Skips "not audited as held"
                Expect.isTrue (h.Logger.Lines |> List.exists (fun l -> l.StartsWith "WARN")) "warned"
            }

            test "Subscribe and Unsubscribe delegate to the inner channel" {
                let h = harness ()
                let id = h.Filter.Subscribe(scope, ignore) |> Async.RunSynchronously
                h.Filter.Unsubscribe id |> Async.RunSynchronously
            }
        ]

        testList "QuietHoursClock" [
            test "inside a window that wraps midnight the release is the next window end" {
                let q = {
                    StartMinute = 1320
                    EndMinute = 420
                    TimeZoneId = "UTC"
                }

                let at h m =
                    DateTime(2026, 9, 16, h, m, 30, DateTimeKind.Utc)

                Expect.equal
                    (QuietHoursClock.deferralFor utcOnly (at 23 15) q)
                    (Some(DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc)))
                    "late evening"

                Expect.equal
                    (QuietHoursClock.deferralFor utcOnly (at 3 0) q)
                    (Some(DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc)))
                    "early morning"

                Expect.isNone (QuietHoursClock.deferralFor utcOnly (at 12 0) q) "midday"
                Expect.isNone (QuietHoursClock.deferralFor utcOnly (at 7 0) q) "the end minute is outside"
            }

            test "a same-day window and an unresolvable zone" {
                let q = {
                    StartMinute = 540
                    EndMinute = 600
                    TimeZoneId = "UTC"
                }

                let at h m =
                    DateTime(2026, 9, 16, h, m, 0, DateTimeKind.Utc)

                Expect.equal
                    (QuietHoursClock.deferralFor utcOnly (at 9 30) q)
                    (Some(DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc)))
                    "09:30 → 10:00"

                Expect.isNone (QuietHoursClock.deferralFor (fun _ -> None) (at 9 30) q) "unknown zone disables"
            }

            test "QuietHours.contains and minutesUntilEnd agree on the wrap and the always-quiet case" {
                let wrap = {
                    StartMinute = 1320
                    EndMinute = 420
                    TimeZoneId = "UTC"
                }

                Expect.isTrue (QuietHours.contains wrap 1400) "23:20"
                Expect.isTrue (QuietHours.contains wrap 0) "midnight"
                Expect.isFalse (QuietHours.contains wrap 420) "07:00 is out"
                Expect.equal (QuietHours.minutesUntilEnd wrap 1400) 460 "23:20 → 07:00"
                Expect.equal (QuietHours.minutesUntilEnd wrap 720) 0 "outside"
                let allDay = { wrap with EndMinute = 1320 }
                Expect.isTrue (QuietHours.contains allDay 600) "equal bounds are always quiet"
            }
        ]

        testList "DigestJobHandler" [
            test "sends one digest per channel with the digest marker, removes the items, and is idempotent on re-run" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email (Digest Daily))
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously

                publishAs
                    h
                    "reports.summary"
                    (TransactionalEmail {
                        RecipientUserIds = [ "alice" ]
                        Content = InlineEmail("Second", "More", None)
                        CorrelationId = None
                    })
                |> Async.RunSynchronously

                let held = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                let expectedMarker = DigestRenderer.marker Daily held

                let first = runDigest h (t0.AddMinutes 15.0)
                Expect.equal first JobResult.Success "first run"

                match h.Inner.Published with
                | [ s, TransactionalEmail e ] ->
                    Expect.equal s scope "published in the user's scope"
                    Expect.equal e.RecipientUserIds [ "alice" ] "to alice"
                    Expect.equal e.CorrelationId (Some expectedMarker) "digest marker"
                    Expect.isTrue (expectedMarker.StartsWith "digest:daily:") "marker shape"

                    match e.Content with
                    | InlineEmail(subject, body, _) ->
                        Expect.equal subject "Your notification digest (2 items)" "count substituted"
                        Expect.isTrue (body.Contains "Subject" && body.Contains "Second") "both items folded"
                    | other -> failtestf "expected inline email, got %A" other
                | other -> failtestf "expected exactly one digest email, got %A" other

                let after = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                Expect.isEmpty after "items removed"
                let marks = h.Store.GetDigestWatermarks(scope, "alice") |> Async.RunSynchronously
                Expect.equal (Map.tryFind "daily" marks) (Some(t0.AddMinutes 15.0)) "watermark"

                let second = runDigest h (t0.AddMinutes 30.0)
                Expect.equal second JobResult.Success "second run"
                Expect.equal (List.length h.Inner.Published) 1 "nothing re-sent"
            }

            test "a bucket is not due until its period has elapsed since the last digest" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email (Digest Daily))

                h.Store.SetDigestWatermark(scope, "alice", Daily, t0)
                |> Async.RunSynchronously
                |> ignore

                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously

                runDigest h (t0.AddHours 23.0) |> ignore
                Expect.isEmpty h.Inner.Published "23 h: not yet"

                runDigest h (t0.AddHours 24.0) |> ignore
                Expect.equal (List.length h.Inner.Published) 1 "24 h: sent"
            }

            test "buckets of different frequencies are independent" {
                let h = harness ()
                savePrefs h "alice" (cell "reports.summary" PreferenceChannel.Email (Digest Weekly))
                savePrefs h "bob" (cell "reports.summary" PreferenceChannel.Email (Digest Hourly))

                publishAs h "reports.summary" (email [ "alice"; "bob" ])
                |> Async.RunSynchronously

                runDigest h (t0.AddMinutes 15.0) |> ignore

                let markers =
                    h.Inner.Published
                    |> List.choose (fun (_, n) ->
                        match n with
                        | TransactionalEmail e -> e.CorrelationId
                        | _ -> None)
                    |> List.sort

                Expect.equal (List.length markers) 2 "one digest each"
                Expect.isTrue (markers |> List.exists (fun m -> m.StartsWith "digest:weekly:")) "weekly"
                Expect.isTrue (markers |> List.exists (fun m -> m.StartsWith "digest:hourly:")) "hourly"
            }

            test "an expired quiet-hours hold is released on its own and an unexpired one waits" {
                let h = harnessAt (DateTime(2026, 9, 16, 23, 30, 0, DateTimeKind.Utc)) None
                savePrefs h "alice" (quiet 1320 420 "UTC")
                publishAs h "reports.summary" (email [ "alice" ]) |> Async.RunSynchronously
                Expect.isEmpty h.Inner.Published "held"

                runDigest h (DateTime(2026, 9, 17, 6, 0, 0, DateTimeKind.Utc)) |> ignore
                Expect.isEmpty h.Inner.Published "06:00: still held"

                runDigest h (DateTime(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc)) |> ignore
                Expect.equal h.Inner.Published [ scope, email [ "alice" ] ] "07:00: released as itself, not as a digest"
                let after = h.Store.ListPending(scope, "alice") |> Async.RunSynchronously
                Expect.isEmpty after "removed"
            }

            test "the digest marker is a deterministic function of the item ids" {
                let items = [
                    {
                        Id = Guid("11111111-1111-1111-1111-111111111111")
                        UserId = "u"
                        Category = "c"
                        Channel = PreferenceChannel.Email
                        Notification = email [ "u" ]
                        QueuedAt = t0
                        Disposition = PendingDisposition.ForDigest Daily
                    }
                    {
                        Id = Guid("22222222-2222-2222-2222-222222222222")
                        UserId = "u"
                        Category = "c"
                        Channel = PreferenceChannel.Email
                        Notification = email [ "u" ]
                        QueuedAt = t0
                        Disposition = PendingDisposition.ForDigest Daily
                    }
                ]

                let a = DigestRenderer.marker Daily items
                let b = DigestRenderer.marker Daily (List.rev items)
                Expect.equal a b "order-independent"
                Expect.notEqual a (DigestRenderer.marker Hourly items) "frequency-specific"
                Expect.equal a.Length ("digest:daily:".Length + 16) "16 hex chars"
            }

            test "the declaration is the reserved handler on the configured cron" {
                let h = harness ()

                let decl =
                    NotificationDigest.declaration h.Store h.Inner NotificationPreferenceSettings.defaults h.Logger

                Expect.equal decl.HandlerName "_platform.notifications.digest" "name"
                Expect.equal decl.Trigger (Trigger.CronTrigger "*/15 * * * *") "cron"
            }
        ]

        testList "composition defaults (GP 11)" [
            test "ServerConfig.defaults opts out and declares no category" {
                Expect.equal ServerConfig.defaults.NotificationPreferences NoNotificationPreferences "server off"
                Expect.isEmpty ServerConfig.defaults.NotificationCategories "no SDK category"
            }

            test "ServerModule.withNotificationCategories appends and refuses an invalid id" {
                let m =
                    ServerModule.create "Reports"
                    |> ServerModule.withNotificationCategories [
                        NotificationCategory.create "reports.summary" "Summaries"
                    ]
                    |> ServerModule.withNotificationCategories [ NotificationCategory.create "reports.alerts" "Alerts" ]

                Expect.equal
                    (m.NotificationCategories |> List.map _.Id)
                    [ "reports.summary"; "reports.alerts" ]
                    "appended"

                Expect.throws
                    (fun () ->
                        ServerModule.create "Bad"
                        |> ServerModule.withNotificationCategories [ NotificationCategory.create "Not Valid" "x" ]
                        |> ignore)
                    "invalid id refused"
            }

            test "addModule fans module categories into ServerConfig.NotificationCategories" {
                let m =
                    ServerModule.create "Reports"
                    |> ServerModule.withNotificationCategories [
                        NotificationCategory.create "reports.summary" "Summaries"
                    ]

                let app =
                    ServerApp.empty
                    |> ServerApp.withNotificationCategory (
                        NotificationCategory.nonSuppressible "security.reset" "Resets"
                    )
                    |> ServerApp.addModule m

                Expect.equal
                    (app.Config.NotificationCategories |> List.map _.Id)
                    [ "security.reset"; "reports.summary" ]
                    "app-level first, then the module's"
            }
        ]
    ]