module ToolUp.Platform.Tests.InProcess.MinimumViableShapeTests

open Expecto
open ToolUp.Platform

// ─── Phase 1g — minimum-viable composition profile guards ────────────
//
// Pins the SDK's lightweight defaults and the auto-detection logic for
// `NotificationMode`. A future phase that reintroduces an always-on
// dependency will fail one of these assertions and either fix the
// regression or document a deliberate change to the lightweight shape.
//
// **What this file does NOT cover (yet):**
// - Building a real `WebApplicationBuilder` and asserting `IServiceCollection`
//   contents post-`compose`. That requires importing the full server-side
//   props (Saturn / Giraffe / Fable.Remoting) into the test project, which
//   is a heavier change than fits Phase 1g. Tracked as a follow-up — the
//   end-to-end `BackgroundService` count assertion lives behind the same
//   surface that the dev-diagnostics endpoint already mounts.

// ─── ServerConfig.defaults — lightweight shape pin ───────────────────

let private defaultsAreLightweight =
    testList "ServerConfig.defaults — lightweight shape" [
        test "Webhooks default is NoWebhooks" {
            Expect.equal ServerConfig.defaults.Webhooks NoWebhooks "lightweight default skips webhook substrate"
        }

        test "AuditLog default is NoAuditLog" {
            Expect.equal ServerConfig.defaults.AuditLog NoAuditLog "lightweight default uses NoOpAuditLog"
        }

        test "Notifications default is NotificationsAuto" {
            Expect.equal
                ServerConfig.defaults.Notifications
                NotificationsAuto
                "lightweight default lets compose auto-detect"
        }

        test "EventStore default is InMemoryOnly" {
            Expect.equal ServerConfig.defaults.EventStore InMemoryOnly "lightweight default keeps events in-memory"
        }

        test "JobScheduler default is NoJobScheduler" {
            Expect.equal
                ServerConfig.defaults.JobScheduler
                NoJobScheduler
                "lightweight default registers no scheduler BackgroundService"
        }

        test "DataIngestion default is NoDataIngestion" {
            Expect.equal
                ServerConfig.defaults.DataIngestion
                NoDataIngestion
                "lightweight default skips ingestion substrate"
        }

        test "ResultStore default is NoResultStore" {
            Expect.equal ServerConfig.defaults.ResultStore NoResultStore "lightweight default skips result store"
        }

        test "Lineage default is NoLineageStore" {
            Expect.equal ServerConfig.defaults.Lineage NoLineageStore "lightweight default skips lineage store"
        }

        test "RateLimit default is None" {
            Expect.isNone ServerConfig.defaults.RateLimit "lightweight default does not register rate limiter"
        }

        test "EnableDevEndpoints default is false" {
            Expect.isFalse ServerConfig.defaults.EnableDevEndpoints "lightweight default keeps dev endpoint dark"
        }

        test "Surfaces default is anonymous (Phase 66 Stream A.2)" {
            Expect.equal
                ServerConfig.defaults.Surfaces
                Surfaces.anonymous
                "lightweight default needs no auth — single-shape anonymous surface"
        }

        test "SecurityHeaders default is empty (Phase 1f)" {
            Expect.isTrue ServerConfig.defaults.SecurityHeaders.IsEmpty "lightweight default stamps no security headers"
        }

        test "Cors default is None (Phase 1f)" {
            Expect.isNone ServerConfig.defaults.Cors "lightweight default registers no CORS middleware"
        }
    ]

// ─── CorsConfig — convenience constructors ───────────────────────────

let private corsConfigConstructors =
    testList "CorsConfig (Phase 1f)" [
        test "permissive has wildcard origins / methods / headers, no credentials" {
            Expect.equal CorsConfig.permissive.Origins [ "*" ] "permissive allows any origin"
            Expect.equal CorsConfig.permissive.Methods [ "*" ] "permissive allows any method"
            Expect.equal CorsConfig.permissive.Headers [ "*" ] "permissive allows any header"

            Expect.isFalse CorsConfig.permissive.AllowCredentials "wildcard origins cannot combine with credentials"
        }

        test "forOrigins explicit allowlist + credentials" {
            let cfg =
                CorsConfig.forOrigins [ "https://app.example.com"; "https://admin.example.com" ]

            Expect.equal
                cfg.Origins
                [ "https://app.example.com"; "https://admin.example.com" ]
                "explicit origins preserved"

            Expect.equal cfg.Methods [ "GET"; "POST"; "OPTIONS" ] "typical credentialed-API verbs"
            Expect.equal cfg.Headers [ "*" ] "any header allowed by default"
            Expect.isTrue cfg.AllowCredentials "credentials enabled for explicit origins"
        }
    ]

// ─── NotificationMode.resolve — auto-detection rules ─────────────────

let private autoDetectionRules =
    testList "NotificationMode.resolve" [
        test "NotificationsAuto + Anonymous + NoJobScheduler + no consumers → NoNotifications" {
            let result = NotificationMode.resolve NotificationsAuto NoJobScheduler false []

            Expect.equal result NoNotifications "fully lightweight shape resolves to NoNotifications"
        }

        test "NotificationsAuto + InProcessJobScheduler → InMemoryNotifications" {
            let result =
                NotificationMode.resolve NotificationsAuto InProcessJobScheduler false []

            Expect.equal result InMemoryNotifications "job scheduler publishes dead-letter notifications"
        }

        test "NotificationsAuto + MultiTeam mode → InMemoryNotifications" {
            let result = NotificationMode.resolve NotificationsAuto NoJobScheduler true []

            Expect.equal result InMemoryNotifications "MultiTeam fires membership-change events"
        }

        test "NotificationsAuto + AI consumer declared → InMemoryNotifications" {
            let result =
                NotificationMode.resolve NotificationsAuto NoJobScheduler false [ "AI" ]

            Expect.equal result InMemoryNotifications "companion-declared consumer flips auto-detection"
        }

        test "NotificationsAuto + RAG + AI consumers → InMemoryNotifications" {
            let result =
                NotificationMode.resolve NotificationsAuto NoJobScheduler false [ "AI"; "RAG" ]

            Expect.equal result InMemoryNotifications "multi-consumer composition still resolves to InMemory"
        }

        test "Explicit NoNotifications overrides auto-detect even with consumers" {
            let result =
                NotificationMode.resolve NoNotifications InProcessJobScheduler true [ "AI" ]

            Expect.equal
                result
                NoNotifications
                "explicit user override wins — caller knows their deployment uses webhooks instead"
        }

        test "Explicit InMemoryNotifications passes through unchanged" {
            let result = NotificationMode.resolve InMemoryNotifications NoJobScheduler false []

            Expect.equal result InMemoryNotifications "explicit value bypasses auto-detection"
        }

        test "Explicit RedisNotifications passes through unchanged" {
            let result =
                NotificationMode.resolve (RedisNotifications "redis://localhost:6379") NoJobScheduler false []

            Expect.equal
                result
                (RedisNotifications "redis://localhost:6379")
                "Redis connection string passes through verbatim"
        }
    ]

// ─── No-op channels — runtime contract ───────────────────────────────

let private noOpChannelTests =
    testList "NoOp channels — runtime behaviour" [
        testCaseAsync "NoOpNotificationChannel.Publish returns immediately without throwing"
        <| async {
            let channel = NotificationChannel.NoOpNotificationChannel() :> INotificationChannel
            let payload = SystemMessage(SystemMessageLevel.Info, "noop")
            do! channel.Publish("scope-x", payload)
        }

        testCaseAsync "NoOpNotificationChannel.Subscribe returns a Guid that Unsubscribe accepts"
        <| async {
            let channel = NotificationChannel.NoOpNotificationChannel() :> INotificationChannel
            let! subId = channel.Subscribe("scope-x", fun _ -> ())
            do! channel.Unsubscribe(subId)
        }

        testCaseAsync "NoOpAuditLog.Record returns immediately without throwing"
        <| async {
            let log = AuditLog.NoOpAuditLog() :> IAuditLog

            let payload = UserLoggedIn { UserId = "u1"; AuthProvider = "test" }

            do! log.Record("scope-x", payload)
        }

        testCaseAsync "NoOpAuditLog.GetAuditTrail returns empty list"
        <| async {
            let log = AuditLog.NoOpAuditLog() :> IAuditLog
            let! trail = log.GetAuditTrail("scope-x", None, None)
            Expect.isEmpty trail "no audit trail when audit log is disabled"
        }
    ]

// ─── Aggregate ───────────────────────────────────────────────────────

let tests =
    testList "MinimumViableShape (Phase 1g + 1f)" [
        defaultsAreLightweight
        autoDetectionRules
        noOpChannelTests
        corsConfigConstructors
    ]