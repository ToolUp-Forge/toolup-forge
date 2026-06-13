module ToolUp.Platform.EncryptedSecretStoreModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 6l.E — plaintext secrets in authenticated modes ──────────
//
// `EncryptedSecretStore` falls back to plaintext writes when no master
// key is configured (`TOOLUP_SECRETS_MASTER_KEY` unset → `masterKey =
// None`). The wrapper logs a one-off Warn at startup but the warning
// is easily missed in dev / container restarts / CI replays. The
// permanent consequence is unencrypted API keys, OAuth tokens, and
// per-tenant credentials sitting in blob storage forever — a
// secrets-at-rest breach if the storage backend is ever exposed
// (misconfigured S3 bucket, leaked backup, dev environment without
// proper access control).
//
// In authenticated modes (any production mode), running without a
// master key is almost certainly wrong. The validator refuses startup
// — operators set the env var (or explicitly opt out via the escape
// hatch below) before the deployment proceeds. Dev / Anonymous mode
// is unaffected because requiresAuth = false.
//
// Escape hatch: `ServerConfig.AcceptPlaintextSecretsWhenAuthRequired`
// (default `false`). For deployments where the underlying storage
// already provides at-rest encryption (cloud KMS-managed bucket,
// disk-level FDE) and operators have made an informed decision to
// rely on that rather than envelope encryption.
//
// Phase 2a interaction. When `TOOLUP_SECRET_STORE` resolves to a
// managed cloud KMS (`azure-key-vault` / `aws-secrets-manager` /
// `vault` / `gcp-secret-manager`), the active `ISecretStore` is a
// cloud companion that provides its own at-rest encryption (HSM-
// backed in Azure Key Vault, AWS-managed KMS keys for Secrets
// Manager, Vault transit engine, Google-managed AES-256 for GCP
// Secret Manager). Wrapping such companions in `EncryptedSecretStore`
// is intentionally skipped in the composition root, so the
// `TOOLUP_SECRETS_MASTER_KEY` requirement does not apply. The
// validator reads `TOOLUP_SECRET_STORE` directly here rather than
// type-checking the registered store — matches the existing env-var-
// inspection style and avoids coupling the SDK validator to
// companion types that live outside `ToolUp.Platform`.

/// Phase 6l.E — config validator that refuses an authenticated mode
/// running `EncryptedSecretStore` with `masterKey = None`. The
/// validator inspects the wrapper to detect the no-key state without
/// requiring a write attempt — pure configuration check, no I/O.
type EncryptedSecretStoreModeValidator(config: ServerConfig, secretStore: Secrets.ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // Phase 2a — recognise the new TOOLUP_SECRET_STORE env switch.
    // When set to a managed cloud KMS, the deployment opted out of
    // EncryptedSecretStore wrapping; `TOOLUP_SECRETS_MASTER_KEY`
    // becomes irrelevant and the master-key gate is skipped.
    let isCloudKmsBacked () =
        match Environment.GetEnvironmentVariable "TOOLUP_SECRET_STORE" with
        | null
        | "" -> false
        | s ->
            match s.ToLowerInvariant() with
            | "azure-key-vault"
            | "aws-secrets-manager"
            | "vault"
            | "gcp-secret-manager" -> true
            | _ -> false

    interface IConfigValidator with
        member _.Name = "encrypted-secret-store-mode"
        member _.Timeout = timeout

        member _.Validate() =
            // Detect the no-master-key state. We re-read the env var here
            // (rather than reaching into the opaque EncryptedSecretStore
            // wrapper) via the detailed resolver, so the validator
            // distinguishes "unset" from "set-but-malformed" and reports
            // the parse failure verbatim — without this, an operator who
            // set TOOLUP_SECRETS_MASTER_KEY=<typo> sees "is unset" and
            // burns 20 minutes diagnosing. Cost: one env read per startup.
            // Bound once here so both the predicate (keyAvailable) and the
            // message (stateDescription) read the same resolution.
            let resolution = EncryptedSecretStore.masterKeyFromEnvironmentDetailed ()

            let keyAvailable =
                match resolution with
                | EncryptedSecretStore.Valid _ -> true
                | _ -> false

            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    not keyAvailable
                    && not config.AcceptPlaintextSecretsWhenAuthRequired
                    && not (isCloudKmsBacked ()))
                (fun () ->
                    let stateDescription =
                        match resolution with
                        | EncryptedSecretStore.Unset -> "TOOLUP_SECRETS_MASTER_KEY is unset"
                        | EncryptedSecretStore.Malformed reason ->
                            sprintf "TOOLUP_SECRETS_MASTER_KEY is set but %s" reason
                        | EncryptedSecretStore.Valid _ -> "TOOLUP_SECRETS_MASTER_KEY parsed cleanly" // unreachable

                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s but %s. EncryptedSecretStore falls back to plaintext writes — every API key, OAuth token, and per-tenant credential will sit unencrypted in blob storage. Resolutions: (1) set TOOLUP_SECRETS_MASTER_KEY to a base64-encoded 32-byte key (call EncryptedSecretStore.generateMasterKey () once during deployment setup); (2) switch to a cloud-KMS-backed store via TOOLUP_SECRET_STORE=azure-key-vault (or aws-secrets-manager / vault / gcp-secret-manager) — Phase 2a/2b companions provide their own at-rest encryption; (3) set ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true if your storage backend provides at-rest encryption (disk FDE) and you've made an informed decision to rely on that. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                            stateDescription
                    ))