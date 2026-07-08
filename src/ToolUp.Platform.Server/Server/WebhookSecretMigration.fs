module ToolUp.Platform.WebhookSecretMigration

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets

// ─── Phase 6d.A — webhook signing-secret at-rest migration ───────────
//
// One-shot, idempotent migration for deployments that persisted webhook
// subscriptions before Phase 6d.A, when the signing secret was written to
// the subscription blob in cleartext. It walks every persisted
// subscription, and for each one still carrying an inline plaintext
// `Secret` (and/or grace-window `PreviousSecret`), moves the value(s)
// into `ISecretStore` (encrypted at rest by whichever store is composed)
// and rewrites the blob with only the reference — no plaintext left in
// blob storage.
//
// Idempotent by construction: a subscription with no inline secret is
// skipped, so re-running (or leaving the compose-time opt-in permanently
// on) only re-scans. `ISecretStore.SetSecret` is a replace, so a re-run
// after a partial failure (secret written, blob rewrite failed) simply
// re-writes the same value and completes the blob rewrite.
//
// Portability (GP 12): identity by value (Guid subscription ids, string
// refs), async at every boundary, stateless between calls — the same
// shape a distributed one-shot job handler would take. Exposed as a
// plain async function so a composition root can run it at startup (the
// `ServerConfig.MigrateWebhookSecretsAtRest` opt-in) or an operator can
// invoke it from a one-shot admin entry point.

/// Outcome counts for one migration pass. `Scanned` is every persisted
/// subscription; `AlreadyMigrated` had no inline secret; `Migrated` had
/// its value(s) moved into `ISecretStore`; `Failed` hit an error and was
/// left untouched (safe to re-run).
type MigrationSummary = {
    Scanned: int
    Migrated: int
    AlreadyMigrated: int
    Failed: int
}

/// Run the one-shot migration over every persisted webhook subscription.
/// Safe to re-run. Never throws for an individual subscription — a
/// per-subscription failure is logged, counted, and the blob is left
/// as-is (still inline, so a later re-run retries it and the
/// `WebhookSecretAtRestValidator` keeps failing loud until it succeeds).
let migrate (storage: IBlobStorage) (secretStore: ISecretStore) (logger: ILogger) : Async<MigrationSummary> = async {
    let! subs = WebhookRegistry.listAllSubscriptions storage

    let hasInline (v: string option) =
        v |> Option.exists (fun s -> not (String.IsNullOrEmpty s))

    let mutable migrated = 0
    let mutable already = 0
    let mutable failed = 0

    for sub in subs do
        if not (hasInline sub.Secret) && not (hasInline sub.PreviousSecret) then
            already <- already + 1
        else
            try
                // Canonical current ref (a legacy blob has none yet).
                let currentRef =
                    if String.IsNullOrEmpty sub.SecretRef then
                        WebhookSecretRef.current sub.SubscriptionId
                    else
                        sub.SecretRef

                // 1. Move the current inline secret into ISecretStore.
                match sub.Secret with
                | Some v when not (String.IsNullOrEmpty v) ->
                    match! secretStore.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf currentRef, v) with
                    | Ok() -> ()
                    | Error e -> failwithf "current secret write failed: %s" e
                | _ -> ()

                // 2. Move any grace-window previous inline secret.
                let previousRefOpt =
                    match sub.PreviousSecret with
                    | Some v when not (String.IsNullOrEmpty v) ->
                        let pref =
                            sub.PreviousSecretRef
                            |> Option.defaultValue (WebhookSecretRef.previous sub.SubscriptionId)

                        Some(pref, v)
                    | _ -> None

                match previousRefOpt with
                | Some(pref, v) ->
                    match! secretStore.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf pref, v) with
                    | Ok() -> ()
                    | Error e -> failwithf "previous secret write failed: %s" e
                | None -> ()

                // 3. Rewrite the blob with only the references — no
                //    plaintext. Grace-window expiry (if any) is retained.
                let migratedSub = {
                    sub with
                        SecretRef = currentRef
                        Secret = None
                        PreviousSecretRef = previousRefOpt |> Option.map fst |> Option.orElse sub.PreviousSecretRef
                        PreviousSecret = None
                }

                match! WebhookRegistry.saveSubscription storage migratedSub with
                | Ok() ->
                    migrated <- migrated + 1

                    logger.Info(
                        sprintf "[WebhookSecretMigration] migrated sub=%O scope=%s" sub.SubscriptionId sub.ScopeId
                    )
                | Error e -> failwithf "blob rewrite failed: %s" e
            with ex ->
                failed <- failed + 1

                logger.Error(
                    sprintf "[WebhookSecretMigration] failed sub=%O scope=%s" sub.SubscriptionId sub.ScopeId,
                    Some ex
                )

    let summary = {
        Scanned = List.length subs
        Migrated = migrated
        AlreadyMigrated = already
        Failed = failed
    }

    logger.Info(
        sprintf
            "[WebhookSecretMigration] complete: scanned=%d migrated=%d alreadyMigrated=%d failed=%d"
            summary.Scanned
            summary.Migrated
            summary.AlreadyMigrated
            summary.Failed
    )

    return summary
}