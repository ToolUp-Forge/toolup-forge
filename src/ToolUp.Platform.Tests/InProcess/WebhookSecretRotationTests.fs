module ToolUp.Platform.Tests.InProcess.WebhookSecretRotationTests

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.WebhookDispatcher
open ToolUp.Platform.Tests.Contracts
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 235 + 6d.A — webhook signing-secret rotation & at-rest ────
//
// Covers rotation end to end at the unit level, updated for Phase 6d.A
// (the secret VALUE lives in `ISecretStore`; the subscription blob
// carries only `SecretRef` / `PreviousSecretRef`):
//   * the pure `WebhookSubscription` ref transition (id stable, current
//     ref canonicalised, previous ref recorded, inline secrets cleared,
//     grace-window expiry) + `acceptedSecretRefs` boundary + `maskSecret`
//     masking any residual inline value;
//   * the blob-backed registry's `RotateSecret` (ref bookkeeping persisted);
//   * the `IWebhookApi` handler's `CreateSubscription` (secret written to
//     ISecretStore, blob holds only the ref, value revealed once) and
//     `RotateSecret` (new secret in the store at the current ref, prior
//     secret moved to the grace-window previous ref, revealed once,
//     audited without the secret value, masked on a subsequent get);
//   * the dispatcher's grace-window dual-signing (`WebhookSignature`):
//     both resolved secrets verify while the window is open, only the
//     current after it drops.

// ─── Helpers ─────────────────────────────────────────────────────────

/// Minimal in-memory `ISecretStore` for the handler + store-persistence
/// assertions. Writable, scope+key keyed, idempotent delete.
type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// A migrated / freshly-created subscription: secret lives in
/// `ISecretStore` at `SecretRef`, the blob carries no inline value.
let private sampleSubscription (scopeId: string) : WebhookSubscription =
    let id = Guid.NewGuid()

    {
        SubscriptionId = id
        ScopeId = scopeId
        TargetUrl = "https://hooks.example.com/endpoint"
        SecretRef = WebhookSecretRef.current id
        Secret = None
        EventTypes = [ "FlagChanged" ]
        Status = WebhookStatus.Active
        CreatedBy = "user-1"
        CreatedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        ConsecutiveFailures = 0
        PreviousSecretRef = None
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

/// Stub dispatcher — the CRUD paths never call it, but the handler
/// resolves `IWebhookDispatcher` from DI eagerly at construction.
type private StubDispatcher() =
    interface IWebhookDispatcher with
        member _.Dispatch(_) = ()
        member _.TestFire(_, _) = async { return Error "not exercised by the rotate path" }

let private buildHandler
    (scopeId: string)
    (registry: IWebhookRegistry)
    (eventStore: IEventStore)
    (secretStore: ISecretStore)
    : IWebhookApi =
    let services = ServiceCollection()
    services.AddSingleton<IWebhookRegistry>(registry) |> ignore
    services.AddSingleton<IEventStore>(eventStore) |> ignore
    services.AddSingleton<ISecretStore>(secretStore) |> ignore

    // Allowlist the sample host so `CreateSubscription`'s SSRF guard
    // passes without a DNS round-trip (the allowlist branch skips
    // IP-range resolution entirely).
    services.AddSingleton<ServerConfig>(
        {
            ServerConfig.defaults with
                WebhookUrlAllowedHosts = [ "hooks.example.com" ]
        }
    )
    |> ignore

    // The handler resolves these eagerly even though the CRUD paths use
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
    testList "Phase 235 + 6d.A — webhook secret rotation & at-rest" [

        // ── Backward-compat read path (GP 11) ──
        test "a pre-6d.A blob (inline Secret, no SecretRef) deserialises without loss" {
            // The exact wire shape a pre-6d.A deployment persisted: a
            // plaintext `Secret`, no `SecretRef` / `PreviousSecretRef`
            // keys. The registry's deserialise wraps failures as `None`
            // (which would silently vanish the subscription), so the new
            // record MUST read this old shape cleanly — missing non-option
            // `SecretRef` back as null, present `Secret` string back as
            // `Some`. The migration then moves the value into ISecretStore.
            let legacyJson =
                """{"SubscriptionId":"11111111-2222-3333-4444-555555555555","ScopeId":"team-acme","TargetUrl":"https://example.com/hook","Secret":"legacy-plaintext-secret","EventTypes":["FlagChanged"],"Status":"Active","CreatedBy":"user-001","CreatedAt":"2026-01-15T12:30:00.0000000Z","ConsecutiveFailures":0,"PreviousSecret":null,"PreviousSecretExpiresAt":null}"""

            let sub =
                JsonSerializer.Deserialize<WebhookSubscription>(legacyJson, FableConverters.shared)

            Expect.equal sub.ScopeId "team-acme" "scope survives the read"
            Expect.equal sub.Secret (Some "legacy-plaintext-secret") "inline secret reads back as Some"

            Expect.isTrue
                (String.IsNullOrEmpty sub.SecretRef)
                "missing SecretRef reads back as null/empty (unmigrated marker)"

            Expect.equal sub.PreviousSecretRef None "missing PreviousSecretRef reads back as None"
        }

        // ── Pure ref transition + helpers ──
        test "withRotatedSecret keeps the id, canonicalises the current ref, records the previous ref, clears inline" {
            let sub = sampleSubscription "user-1"
            let currentRef = WebhookSecretRef.current sub.SubscriptionId
            let previousRef = WebhookSecretRef.previous sub.SubscriptionId
            let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)

            let rotated =
                WebhookSubscription.withRotatedSecret currentRef previousRef expiry sub

            Expect.equal rotated.SubscriptionId sub.SubscriptionId "subscription id is unchanged"
            Expect.equal rotated.SecretRef currentRef "current ref is canonicalised"
            Expect.equal rotated.Secret None "inline current secret is cleared"
            Expect.equal rotated.PreviousSecretRef (Some previousRef) "the previous ref is recorded"
            Expect.equal rotated.PreviousSecret None "inline previous secret is cleared"
            Expect.equal rotated.PreviousSecretExpiresAt (Some expiry) "the grace-window expiry is recorded"
        }

        test "acceptedSecretRefs returns both within the grace window and only the current after it" {
            let expiry = DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc)

            let sub = {
                sampleSubscription "user-1" with
                    SecretRef = "cur-ref"
                    PreviousSecretRef = Some "prev-ref"
                    PreviousSecretExpiresAt = Some expiry
            }

            let within = expiry.AddHours -1.0
            let after = expiry.AddHours 1.0

            Expect.equal
                (WebhookSubscription.acceptedSecretRefs within sub)
                [ "cur-ref"; "prev-ref" ]
                "within the window both refs are accepted, current first"

            Expect.equal
                (WebhookSubscription.acceptedSecretRefs after sub)
                [ "cur-ref" ]
                "after the window only the current ref is accepted"
        }

        test "acceptedSecretRefs on a never-rotated subscription returns only the current ref" {
            let sub = sampleSubscription "user-1"

            Expect.equal
                (WebhookSubscription.acceptedSecretRefs DateTime.UtcNow sub)
                [ sub.SecretRef ]
                "no previous ref → just the current one"
        }

        test "maskSecret masks residual inline secrets and leaves migrated (None) records untouched" {
            // Legacy / not-yet-migrated blob carries inline values.
            let legacy = {
                sampleSubscription "user-1" with
                    Secret = Some "abcd"
                    PreviousSecret = Some "wxyz"
            }

            let masked = WebhookSubscription.maskSecret legacy
            Expect.equal masked.Secret (Some "****") "inline current secret masked, length preserved"
            Expect.equal masked.PreviousSecret (Some "****") "inline previous secret masked, length preserved"

            // Migrated blob has no inline material — masking is a no-op.
            let migrated = sampleSubscription "user-1"
            let maskedMigrated = WebhookSubscription.maskSecret migrated
            Expect.equal maskedMigrated.Secret None "no inline current secret to mask"
            Expect.equal maskedMigrated.PreviousSecret None "no inline previous secret to mask"
        }

        // ── Registry-level rotation (ref bookkeeping) ──
        testCaseAsync "registry RotateSecret persists the ref bookkeeping and keeps the id"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let sub = sampleSubscription "team-acme"
            let! created = registry.CreateSubscription sub
            Expect.isOk created "subscription created"

            let currentRef = WebhookSecretRef.current sub.SubscriptionId
            let previousRef = WebhookSecretRef.previous sub.SubscriptionId
            let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)

            match! registry.RotateSecret(sub.ScopeId, sub.SubscriptionId, currentRef, previousRef, expiry) with
            | Error e -> failtestf "rotation failed: %s" e
            | Ok rotated ->
                Expect.equal rotated.SubscriptionId sub.SubscriptionId "id stable across rotation"
                Expect.equal rotated.SecretRef currentRef "current ref recorded"
                Expect.equal rotated.PreviousSecretRef (Some previousRef) "previous ref recorded"

                // Re-read confirms the rotation was persisted, not just returned.
                match! registry.GetSubscription(sub.ScopeId, sub.SubscriptionId) with
                | Some persisted ->
                    Expect.equal persisted.SecretRef currentRef "persisted current ref is the canonical value"
                    Expect.equal persisted.PreviousSecretRef (Some previousRef) "persisted previous ref"
                    Expect.equal persisted.PreviousSecretExpiresAt (Some expiry) "persisted grace expiry"
                | None -> failtest "subscription vanished after rotation"
        }

        testCaseAsync "registry RotateSecret errors when the subscription does not exist"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let missingId = Guid.NewGuid()

            match!
                registry.RotateSecret(
                    "team-acme",
                    missingId,
                    WebhookSecretRef.current missingId,
                    WebhookSecretRef.previous missingId,
                    DateTime.UtcNow
                )
            with
            | Error _ -> ()
            | Ok _ -> failtest "expected Error for a missing subscription"
        }

        // ── Handler create: secret lands in ISecretStore, blob holds only the ref ──
        testCaseAsync
            "handler CreateSubscription persists the secret to ISecretStore and stores only the ref on the blob"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let store = InMemorySecretStore() :> ISecretStore
            let api = buildHandler "user-1" registry events store
            let secret = "my-webhook-signing-secret-0123456789abcdef"

            match!
                api.CreateSubscription {
                    TargetUrl = "https://hooks.example.com/endpoint"
                    Secret = secret
                    EventTypes = []
                }
            with
            | Error e -> failtestf "create failed: %s" e
            | Ok created ->
                // Revealed transiently once so the admin can copy it out.
                Expect.equal created.Secret (Some secret) "secret revealed transiently on create"
                Expect.isFalse (String.IsNullOrEmpty created.SecretRef) "a SecretRef was assigned"

                // Persisted blob carries the ref, never the value.
                match! registry.GetSubscription("user-1", created.SubscriptionId) with
                | None -> failtest "subscription vanished after create"
                | Some persisted ->
                    Expect.equal persisted.Secret None "no inline secret persisted to the blob"

                    Expect.equal
                        persisted.SecretRef
                        (WebhookSecretRef.current created.SubscriptionId)
                        "persisted ref is the canonical current ref"

                // The value lives in ISecretStore, encrypted at rest by
                // whichever store is composed.
                let! stored = store.GetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf created.SecretRef)

                Expect.equal stored (Some secret) "secret persisted to ISecretStore under the ref"
        }

        // ── Handler rotation: values moved in the store, revealed once, audited ──
        testCaseAsync "handler RotateSecret moves the values in ISecretStore, reveals the new one once, id unchanged"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let store = InMemorySecretStore() :> ISecretStore
            let sub = sampleSubscription "user-1"
            let originalSecret = "original-secret-value-0123456789abcdef"

            // Seed the store + create the subscription (blob holds the ref).
            do!
                store.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf sub.SecretRef, originalSecret)
                |> Async.Ignore

            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events store

            match! api.RotateSecret sub.SubscriptionId with
            | Error e -> failtestf "handler rotation failed: %s" e
            | Ok rotated ->
                Expect.equal rotated.SubscriptionId sub.SubscriptionId "subscription id unchanged"

                let newSecret =
                    match rotated.Secret with
                    | Some s -> s
                    | None -> failtest "rotation did not reveal the new secret"

                Expect.notEqual newSecret originalSecret "a new secret was issued"
                Expect.isGreaterThanOrEqual newSecret.Length 32 "the issued secret is high-entropy (≥ 32 chars)"

                Expect.equal
                    rotated.PreviousSecretRef
                    (Some(WebhookSecretRef.previous sub.SubscriptionId))
                    "the grace-window previous ref was recorded"

                Expect.isSome rotated.PreviousSecretExpiresAt "a grace-window expiry was set"

                // Store: new secret at the current ref, old secret moved to
                // the grace-window previous ref.
                let! curStored =
                    store.GetSecret(
                        WebhookSecretRef.Scope,
                        WebhookSecretRef.keyOf (WebhookSecretRef.current sub.SubscriptionId)
                    )

                Expect.equal curStored (Some newSecret) "new secret persisted at the current ref"

                let! prevStored =
                    store.GetSecret(
                        WebhookSecretRef.Scope,
                        WebhookSecretRef.keyOf (WebhookSecretRef.previous sub.SubscriptionId)
                    )

                Expect.equal prevStored (Some originalSecret) "old secret moved to the previous ref"

                // Persisted blob still carries no inline secret.
                match! registry.GetSubscription("user-1", sub.SubscriptionId) with
                | Some persisted -> Expect.equal persisted.Secret None "no inline secret persisted after rotation"
                | None -> failtest "subscription vanished after rotation"
        }

        testCaseAsync "handler RotateSecret audits the rotation without leaking the secret value"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let store = InMemorySecretStore() :> ISecretStore
            let sub = sampleSubscription "user-1"
            let originalSecret = "original-secret-value-0123456789abcdef"

            do!
                store.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf sub.SecretRef, originalSecret)
                |> Async.Ignore

            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events store

            let! result = api.RotateSecret sub.SubscriptionId

            let newSecret =
                match result with
                | Ok r -> r.Secret |> Option.defaultValue ""
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
                Expect.isFalse (evt.Payload.Contains originalSecret) "audit payload never contains the old secret value"
        }

        testCaseAsync "handler GetSubscription exposes the ref but no secret value after a rotation"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (InMemoryBlobStorage.InMemoryBlobStorage())

            let events = CapturingEventStore()
            let store = InMemorySecretStore() :> ISecretStore
            let sub = sampleSubscription "user-1"

            do!
                store.SetSecret(
                    WebhookSecretRef.Scope,
                    WebhookSecretRef.keyOf sub.SecretRef,
                    "seed-secret-value-0123456789abcd"
                )
                |> Async.Ignore

            let! _ = registry.CreateSubscription sub
            let api = buildHandler "user-1" registry events store

            let! _ = api.RotateSecret sub.SubscriptionId

            match! api.GetSubscription sub.SubscriptionId with
            | Error e -> failtestf "get failed: %s" e
            | Ok fetched ->
                Expect.equal fetched.Secret None "no inline secret value crosses the wire"
                Expect.isFalse (String.IsNullOrEmpty fetched.SecretRef) "the (non-secret) ref is present"
                Expect.isSome fetched.PreviousSecretRef "the grace-window previous ref is present"
        }

        // ── Dispatcher grace-window signing (resolved-value level) ──
        test "WebhookSignature dual-signs the resolved set; the dropped secret stops verifying" {
            let body = Encoding.UTF8.GetBytes """{"event":"FlagChanged"}"""

            // In-window: dispatcher resolves both current + previous and
            // signs with both.
            let dualHeader =
                WebhookSignature.headerFor [ "new-signing-secret"; "old-signing-secret" ] body

            Expect.isTrue
                (WebhookSignature.verifies "new-signing-secret" body dualHeader)
                "new secret verifies in-window"

            Expect.isTrue
                (WebhookSignature.verifies "old-signing-secret" body dualHeader)
                "old secret verifies in-window"

            // After the window: dispatcher resolves only the current secret.
            let soloHeader = WebhookSignature.headerFor [ "new-signing-secret" ] body

            Expect.isTrue
                (WebhookSignature.verifies "new-signing-secret" body soloHeader)
                "new secret still verifies after the window"

            Expect.isFalse
                (WebhookSignature.verifies "old-signing-secret" body soloHeader)
                "old secret stops verifying once dropped from the accepted set"

            // An unrelated secret never verifies.
            Expect.isFalse
                (WebhookSignature.verifies "some-other-secret" body dualHeader)
                "a foreign secret never verifies"
        }
    ]