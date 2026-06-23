module ToolUp.Platform.Tests.InProcess.WebhookSecretRotationTests

open System
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.WebhookDispatcher
open ToolUp.Platform.Tests.Contracts

// ─── Phase 235 — webhook signing-secret rotation ─────────────────────
//
// Covers the rotation feature end to end at the unit level:
//   * the pure `WebhookSubscription` transition (id stable, previous
//     secret retained, grace-window expiry) + `acceptedSecrets`
//     boundary + `maskSecret` masking both secrets;
//   * the blob-backed registry's `RotateSecret` (persisted new secret,
//     previous retained, subscription id unchanged);
//   * the `IWebhookApi` handler's `RotateSecret` (server-generated
//     high-entropy secret revealed once, audited without the secret
//     value, masked on a subsequent get);
//   * the dispatcher's grace-window dual-signing (`WebhookSignature`):
//     during the window both old and new secrets verify; after it, only
//     the new secret verifies and the old one stops.

// ─── Helpers ─────────────────────────────────────────────────────────

let private sampleSubscription (scopeId: string) : WebhookSubscription = {
    SubscriptionId = Guid.NewGuid()
    ScopeId = scopeId
    TargetUrl = "https://hooks.example.com/endpoint"
    Secret = "original-secret-value-0123456789abcdef"
    EventTypes = [ "FlagChanged" ]
    Status = WebhookStatus.Active
    CreatedBy = "user-1"
    CreatedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    ConsecutiveFailures = 0
    PreviousSecret = None
    PreviousSecretExpiresAt = None
}

/// Capturing `IEventStore` — records every `Write` so the audit
/// assertions can inspect the emitted rotation event. The read /
/// erase members are never exercised by the rotate path.
type private CapturingEventStore() =
    let written = ResizeArray<ModuleEvent>()
    member _.Written = written |> List.ofSeq

    interface IEventStore with
        member _.Write(evt) = async { written.Add evt }
        member _.ReadAll(_) = async { return [] }
        member _.ReadByType(_, _) = async { return [] }
        member _.ReadBySource(_, _) = async { return [] }
        member _.ListScopes() = async { return [] }
        member _.Erase(_, _, _, _) = async { return failwith "Erase not exercised by the rotate path" }

/// Stub dispatcher — the rotate path never calls it, but the handler
/// resolves `IWebhookDispatcher` from DI eagerly at construction.
type private StubDispatcher() =
    interface IWebhookDispatcher with
        member _.Dispatch(_) = ()
        member _.TestFire(_, _) = async { return Error "not exercised by the rotate path" }

let private buildHandler (scopeId: string) (registry: IWebhookRegistry) (eventStore: IEventStore) : IWebhookApi =
    let services = ServiceCollection()
    services.AddSingleton<IWebhookRegistry>(registry) |> ignore
    services.AddSingleton<IEventStore>(eventStore) |> ignore

    // The handler resolves these eagerly even though RotateSecret uses
    // neither — register concrete stubs so the up-front DI casts succeed.
    services.AddSingleton<IWebhookDeliveryLog>(
        WebhookRegistry.createDeliveryLog (InMemoryBlobStorage.InMemoryBlobStorage())
    )
    |> ignore

    services.AddSingleton<IWebhookDispatcher>(StubDispatcher()) |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (Subject.AuthenticatedUser scopeId))
    |> ignore

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- services.BuildServiceProvider() :> IServiceProvider
    WebhookApiHandler.webhookApi ctx

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 235 — webhook secret rotation" [

        // ── Pure transition + helpers ──
        test "withRotatedSecret keeps the id, promotes the new secret, retains the old as previous" {
            let sub = sampleSubscription "user-1"
            let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
            let rotated = WebhookSubscription.withRotatedSecret "brand-new-secret" expiry sub

            Expect.equal rotated.SubscriptionId sub.SubscriptionId "subscription id is unchanged"
            Expect.equal rotated.Secret "brand-new-secret" "the new secret becomes current"
            Expect.equal rotated.PreviousSecret (Some sub.Secret) "the prior secret is retained as previous"
            Expect.equal rotated.PreviousSecretExpiresAt (Some expiry) "the grace-window expiry is recorded"
        }

        test "acceptedSecrets returns both within the grace window and only the current after it" {
            let expiry = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)

            let sub = {
                sampleSubscription "user-1" with
                    Secret = "new"
                    PreviousSecret = Some "old"
                    PreviousSecretExpiresAt = Some expiry
            }

            let within = expiry.AddHours -1.0
            let after = expiry.AddHours 1.0

            Expect.equal
                (WebhookSubscription.acceptedSecrets within sub)
                [ "new"; "old" ]
                "within the window both secrets are accepted, current first"

            Expect.equal
                (WebhookSubscription.acceptedSecrets after sub)
                [ "new" ]
                "after the window only the current secret is accepted"
        }

        test "acceptedSecrets on a never-rotated subscription returns only the current secret" {
            let sub = sampleSubscription "user-1"

            Expect.equal
                (WebhookSubscription.acceptedSecrets DateTime.UtcNow sub)
                [ sub.Secret ]
                "no previous secret → just the current one"
        }

        test "maskSecret masks both the current and the previous secret" {
            let sub = {
                sampleSubscription "user-1" with
                    Secret = "abcd"
                    PreviousSecret = Some "wxyz"
                    PreviousSecretExpiresAt = Some(DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
            }

            let masked = WebhookSubscription.maskSecret sub
            Expect.equal masked.Secret "****" "current secret masked, length preserved"
            Expect.equal masked.PreviousSecret (Some "****") "previous secret masked, length preserved"
        }

        // ── Registry-level rotation ──
        testCaseAsync "registry RotateSecret persists the new secret, retains the old, keeps the id"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let sub = sampleSubscription "team-acme"
            let! created = registry.CreateSubscription sub
            Expect.isOk created "subscription created"

            let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)

            match! registry.RotateSecret(sub.ScopeId, sub.SubscriptionId, "rotated-secret", expiry) with
            | Error e -> failtestf "rotation failed: %s" e
            | Ok rotated ->
                Expect.equal rotated.SubscriptionId sub.SubscriptionId "id stable across rotation"
                Expect.equal rotated.Secret "rotated-secret" "new secret returned"
                Expect.equal rotated.PreviousSecret (Some sub.Secret) "old secret retained as previous"

                // Re-read confirms the rotation was persisted, not just returned.
                match! registry.GetSubscription(sub.ScopeId, sub.SubscriptionId) with
                | Some persisted ->
                    Expect.equal persisted.Secret "rotated-secret" "persisted current secret is the new value"
                    Expect.equal persisted.PreviousSecret (Some sub.Secret) "persisted previous secret is the old value"
                    Expect.equal persisted.PreviousSecretExpiresAt (Some expiry) "persisted grace expiry"
                | None -> failtest "subscription vanished after rotation"
        }

        testCaseAsync "registry RotateSecret errors when the subscription does not exist"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            match! registry.RotateSecret("team-acme", Guid.NewGuid(), "x", DateTime.UtcNow) with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for a missing subscription"
        }

        // ── Handler-level rotation ──
        testCaseAsync "handler RotateSecret issues a fresh high-entropy secret, revealed once, id unchanged"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let sub = sampleSubscription "user-1"
            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events

            match! api.RotateSecret sub.SubscriptionId with
            | Error e -> failtestf "handler rotation failed: %s" e
            | Ok rotated ->
                Expect.equal rotated.SubscriptionId sub.SubscriptionId "subscription id unchanged"
                Expect.notEqual rotated.Secret sub.Secret "a new secret was issued"

                Expect.isGreaterThanOrEqual rotated.Secret.Length 32 "the issued secret is high-entropy (≥ 32 chars)"

                Expect.equal
                    rotated.PreviousSecret
                    (Some sub.Secret)
                    "the prior secret became the grace-window previous"

                Expect.isSome rotated.PreviousSecretExpiresAt "a grace-window expiry was set"
        }

        testCaseAsync "handler RotateSecret audits the rotation without leaking the secret value"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let sub = sampleSubscription "user-1"
            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events

            let! result = api.RotateSecret sub.SubscriptionId

            let newSecret =
                match result with
                | Ok r -> r.Secret
                | Error e -> failtestf "rotation failed: %s" e

            let rotationEvent =
                events.Written
                |> List.tryFind (fun e -> e.EventType = WebhookEventTypes.SubscriptionSecretRotated)

            match rotationEvent with
            | None -> failtest "no SubscriptionSecretRotated audit event emitted"
            | Some evt ->
                Expect.stringContains evt.Payload "RotatedBy" "audit names the actor"
                Expect.stringContains evt.Payload (string sub.SubscriptionId) "audit names the subscription id"
                Expect.isFalse (evt.Payload.Contains newSecret) "audit payload never contains the new secret value"
                Expect.isFalse (evt.Payload.Contains sub.Secret) "audit payload never contains the old secret value"
        }

        testCaseAsync "handler GetSubscription masks the secrets after a rotation"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let sub = sampleSubscription "user-1"
            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events

            let! _ = api.RotateSecret sub.SubscriptionId

            match! api.GetSubscription sub.SubscriptionId with
            | Error e -> failtestf "get failed: %s" e
            | Ok masked ->
                Expect.isTrue (masked.Secret |> Seq.forall (fun c -> c = '*')) "current secret masked on get"

                match masked.PreviousSecret with
                | Some prev -> Expect.isTrue (prev |> Seq.forall (fun c -> c = '*')) "previous secret masked on get"
                | None -> failtest "expected a masked previous secret after rotation"
        }

        // ── Dispatcher grace-window signing ──
        test "WebhookSignature dual-signs during the grace window; the old secret stops verifying after it" {
            let body = Encoding.UTF8.GetBytes """{"event":"FlagChanged"}"""
            let expiry = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)

            let sub = {
                sampleSubscription "user-1" with
                    Secret = "new-signing-secret"
                    PreviousSecret = Some "old-signing-secret"
                    PreviousSecretExpiresAt = Some expiry
            }

            // Within the window: both secrets verify against the emitted header.
            let withinHeader = WebhookSignature.header (expiry.AddHours -1.0) sub body

            Expect.isTrue
                (WebhookSignature.verifies "new-signing-secret" body withinHeader)
                "new secret verifies in-window"

            Expect.isTrue
                (WebhookSignature.verifies "old-signing-secret" body withinHeader)
                "old secret verifies in-window"

            // After the window: only the new secret verifies.
            let afterHeader = WebhookSignature.header (expiry.AddHours 1.0) sub body

            Expect.isTrue
                (WebhookSignature.verifies "new-signing-secret" body afterHeader)
                "new secret still verifies after the window"

            Expect.isFalse
                (WebhookSignature.verifies "old-signing-secret" body afterHeader)
                "old secret stops verifying after the grace window"

            // An unrelated secret never verifies.
            Expect.isFalse
                (WebhookSignature.verifies "some-other-secret" body withinHeader)
                "a foreign secret never verifies"
        }
    ]