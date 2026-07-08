# Migration — Phase 6d.A: webhook signing secrets encrypted at rest

**Status.** Backward-compatible on read (GP 11); one operational step for a deployment that
has served webhooks before this version. A pre-6d.A subscription blob persisted the signing
secret in **cleartext**; from this version the secret lives in `ISecretStore` (encrypted at
rest by whichever store is composed) and the blob carries only a reference.

## What changed

`WebhookSubscription` (in `ToolUp.Platform.Core`) grew reference fields and turned its inline
secret fields into options:

| Field | Before | After |
|---|---|---|
| `SecretRef` | — (new) | `string` — `ISecretStore` key for the current secret (`_platform/webhooks/{id:N}.secret`). |
| `Secret` | `string` | `string option` — legacy inline value; `Some` only on an un-migrated blob or a transient create/rotate reveal, else `None`. |
| `PreviousSecretRef` | — (new) | `string option` — `ISecretStore` key for the grace-window previous secret. |
| `PreviousSecret` | `string option` | `string option` — legacy inline previous value; `None` post-migration. |

The dispatcher now resolves the secret from `ISecretStore` immediately before HMAC signing
(never cached beyond the request) and audits each resolve as `WebhookSecretAccessed`. The
admin API writes the secret to `ISecretStore` on create/rotate and removes it on delete; the
create/rotate responses still reveal the value once (transiently) for copy-out.

**Read compatibility.** A pre-6d.A blob (inline `Secret`, no `SecretRef`) deserialises cleanly:
missing `SecretRef` reads back as null/empty and the dispatcher falls back to the inline
`Secret`, so webhooks keep delivering *before* the migration runs. Do not hard-remove `Secret`
in this release — the option field is the migration's read path.

## Consumer-facing surface changes (breaking, SemVer-on-0.x minor)

These signatures changed — a consumer that calls them directly must update (most consumers use
only the admin API / compose and are unaffected):

- `WebhookSubscription.Secret : string` → `string option`; new `SecretRef` / `PreviousSecretRef` fields (record construction).
- `WebhookSubscription.acceptedSecrets` removed → `acceptedSecretRefs` (returns store keys).
- `WebhookSubscription.withRotatedSecret` now takes `(currentSecretRef, previousSecretRef, graceExpiresAt)`.
- `IWebhookRegistry.RotateSecret` now takes `(scopeId, subscriptionId, currentSecretRef, previousSecretRef, graceExpiresAt)`.
- `WebhookDispatcher.create` / `WebhookDispatcherService..ctor` gained an `ISecretStore` parameter.
- `ComposeJobs.buildWebhookSubsystem` / `registerWebhookSubsystem` gained `secretStore` / `resolvedBlobStorage` parameters.
- New `ServerConfig.MigrateWebhookSecretsAtRest : bool` (default `false`) — additive.

## The one operational step — run the migration once

A deployment upgrading from a pre-6d.A version has plaintext secrets in existing subscription
blobs. The new `WebhookSecretAtRestValidator` (preflight, security-class — runs even under
`SkipPreflight`) **refuses startup** while any blob still carries an inline secret. To migrate:

1. Set the opt-in for the first boot:

   ```fsharp
   { ServerConfig.defaults with
       Webhooks = EnabledWebhooks
       MigrateWebhookSecretsAtRest = true }   // or env: TOOLUP_MIGRATE_WEBHOOK_SECRETS=1
   ```

2. Boot once. Compose runs the one-shot `WebhookSecretMigration.migrate` **before** preflight:
   each subscription's inline secret (and any grace-window previous secret) is written to
   `ISecretStore` and the blob is rewritten with only the reference. The validator then passes.

3. Leave the flag on (idempotent — a fully-migrated deployment re-scans and migrates nothing,
   costing one blob scan per boot) or remove it after the first successful boot.

The migration is **idempotent** and safe to re-run: a subscription with no inline secret is
skipped, and `ISecretStore.SetSecret` is a replace, so a re-run after a partial failure
(secret written, blob rewrite failed) simply completes.

## Encryption-at-rest note

Secrets are only as protected as the composed `ISecretStore`. The SDK default `FileSecretStore`
does **not** encrypt at rest by itself — compose `EncryptedSecretStore` with
`TOOLUP_SECRETS_MASTER_KEY`, or a cloud-KMS store (`TOOLUP_SECRET_STORE=azure-key-vault` /
`aws-secrets-manager` / `vault` / `gcp-secret-manager`) for production. This mirrors the
Phase 138 OAuth-credential-at-rest posture.

## Verification

- `dotnet build ToolUp.Forge.sln`
- Boot with `MigrateWebhookSecretsAtRest = true`; confirm the log shows
  `[WebhookSecretMigration] complete: scanned=N migrated=M …` and startup proceeds.
- Inspect a persisted subscription blob: it shows `"SecretRef":"_platform/webhooks/…"` and
  `"Secret":null` — no plaintext.
- `create` / `rotate` in the admin UI still reveal the secret once for copy-out.

## Rollback

Downgrading the binary after migration is **not** clean: post-migration blobs have
`"Secret":null` and the pre-6d.A code reads `Secret` as a non-option `string`. Migrate on a
version you intend to keep. The migration itself is forward-only (it does not retain the
plaintext in the blob).
