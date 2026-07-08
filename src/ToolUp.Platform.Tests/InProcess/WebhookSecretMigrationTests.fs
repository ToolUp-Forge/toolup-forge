module ToolUp.Platform.Tests.InProcess.WebhookSecretMigrationTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Tests.Contracts

// ─── Phase 6d.A — secret-at-rest migration + preflight validator ─────
//
// End-to-end at the storage level: seed a pre-6d.A subscription blob
// (inline plaintext `Secret`, no `SecretRef`), run the one-shot
// migration, and assert the value moved into `ISecretStore` and the blob
// was rewritten with only a reference. Then assert the preflight
// validator Errors before migration and passes after — the
// acceptance-criteria adversarial case (a blob tampered to carry a
// literal secret is rejected at startup).

/// Minimal in-memory `ISecretStore`.
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
            // `Result.Ok` qualified — `open ConfigValidation` brings a
            // `ValidationResult.Ok` case into scope that would shadow it.
            return Result.Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Result.Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private nullLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A pre-6d.A blob: inline plaintext `Secret`, no `SecretRef`.
let private legacySubscription (scopeId: string) (secret: string) : WebhookSubscription =
    let id = Guid.NewGuid()

    {
        SubscriptionId = id
        ScopeId = scopeId
        TargetUrl = "https://hooks.example.com/endpoint"
        SecretRef = ""
        Secret = Some secret
        EventTypes = [ "FlagChanged" ]
        Status = WebhookStatus.Active
        CreatedBy = "user-1"
        CreatedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        ConsecutiveFailures = 0
        PreviousSecretRef = None
        PreviousSecret = None
        PreviousSecretExpiresAt = None
    }

[<Tests>]
let tests =
    testList "Phase 6d.A — webhook secret migration & validator" [

        testCaseAsync "migrate moves an inline secret into ISecretStore and rewrites the blob with only a ref"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage()
            let secretStore = InMemorySecretStore() :> ISecretStore
            let sub = legacySubscription "team-acme" "legacy-plaintext-secret"
            do! WebhookRegistry.saveSubscription storage sub |> Async.Ignore

            let! summary = WebhookSecretMigration.migrate storage secretStore nullLogger

            Expect.equal summary.Scanned 1 "one subscription scanned"
            Expect.equal summary.Migrated 1 "one subscription migrated"
            Expect.equal summary.Failed 0 "no failures"

            // Blob rewritten: no inline secret, canonical ref set.
            match! WebhookRegistry.listAllSubscriptions storage with
            | [ persisted ] ->
                Expect.equal persisted.Secret None "inline secret cleared from the blob"

                Expect.equal
                    persisted.SecretRef
                    (WebhookSecretRef.current sub.SubscriptionId)
                    "canonical current ref stamped on the blob"
            | other -> failtestf "expected exactly one subscription, got %d" (List.length other)

            // Value now lives in the secret store.
            let! stored =
                secretStore.GetSecret(
                    WebhookSecretRef.Scope,
                    WebhookSecretRef.keyOf (WebhookSecretRef.current sub.SubscriptionId)
                )

            Expect.equal stored (Some "legacy-plaintext-secret") "secret moved into ISecretStore"
        }

        testCaseAsync "migrate also moves a grace-window previous secret and is idempotent on re-run"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage()
            let secretStore = InMemorySecretStore() :> ISecretStore

            let sub = {
                legacySubscription "user-9" "current-plain" with
                    PreviousSecret = Some "previous-plain"
                    PreviousSecretExpiresAt = Some(DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
            }

            do! WebhookRegistry.saveSubscription storage sub |> Async.Ignore

            let! first = WebhookSecretMigration.migrate storage secretStore nullLogger
            Expect.equal first.Migrated 1 "migrated on first run"

            let! curStored =
                secretStore.GetSecret(
                    WebhookSecretRef.Scope,
                    WebhookSecretRef.keyOf (WebhookSecretRef.current sub.SubscriptionId)
                )

            let! prevStored =
                secretStore.GetSecret(
                    WebhookSecretRef.Scope,
                    WebhookSecretRef.keyOf (WebhookSecretRef.previous sub.SubscriptionId)
                )

            Expect.equal curStored (Some "current-plain") "current secret moved"
            Expect.equal prevStored (Some "previous-plain") "grace-window previous secret moved"

            // Idempotent: a second pass sees a fully-migrated blob.
            let! second = WebhookSecretMigration.migrate storage secretStore nullLogger
            Expect.equal second.Migrated 0 "nothing to migrate on re-run"
            Expect.equal second.AlreadyMigrated 1 "the blob is recognised as already migrated"
        }

        testCaseAsync "WebhookSecretAtRestValidator Errors on an inline secret and passes after migration"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage()
            let secretStore = InMemorySecretStore() :> ISecretStore
            let sub = legacySubscription "team-acme" "tampered-inline-secret"
            do! WebhookRegistry.saveSubscription storage sub |> Async.Ignore

            let validator =
                WebhookSecretValidator.WebhookSecretAtRestValidator(storage) :> IConfigValidator

            // Security-class marker present (runs even under SkipPreflight).
            Expect.isTrue (box validator :? ISecurityClassValidator) "validator is security-class"

            match! validator.Validate() with
            | Error msg -> Expect.stringContains msg (string sub.SubscriptionId) "names the offending subscription"
            | Ok -> failtest "expected Error while a plaintext secret is persisted"
            | Warning m -> failtestf "expected Error, got Warning: %s" m

            // After migration the validator passes.
            let! _ = WebhookSecretMigration.migrate storage secretStore nullLogger

            match! validator.Validate() with
            | Ok -> ()
            | other -> failtestf "expected Ok after migration, got %A" other
        }

        testCaseAsync "WebhookSecretAtRestValidator passes on an empty deployment"
        <| async {
            let storage = InMemoryBlobStorage.InMemoryBlobStorage()

            let validator =
                WebhookSecretValidator.WebhookSecretAtRestValidator(storage) :> IConfigValidator

            match! validator.Validate() with
            | Ok -> ()
            | other -> failtestf "expected Ok with no subscriptions, got %A" other
        }
    ]