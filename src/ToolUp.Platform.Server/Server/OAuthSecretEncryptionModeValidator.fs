module ToolUp.Platform.OAuthSecretEncryptionModeValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 138 — OAuth credential-at-rest encryption refusal ─────────
//
// Connector OAuth flows (Phase 10e) persist the long-lived refresh
// token — and the Phase 10h refresher caches the access token + its
// expiry — through `ISecretStore.SetSecret`. Whether those land
// encrypted at rest depends entirely on which `ISecretStore` is
// composed:
//
//   * The SDK's *default* store is a raw `FileSecretStore` (no
//     encryption wrapper at all) — plaintext at rest.
//   * `SecretStoreFromEnv.fromEnv` wraps in `EncryptedSecretStore`,
//     but that wrapper still writes plaintext when no master key is
//     configured (`masterKey = None`).
//   * A cloud-KMS-backed store (`TOOLUP_SECRET_STORE=azure-key-vault`
//     / `aws-secrets-manager` / `vault` / `gcp-secret-manager`)
//     provides its own at-rest encryption.
//
// The existing `EncryptedSecretStoreModeValidator` (Phase 6l.E) refuses
// startup in auth modes when the *env master key* is unset — but it
// re-reads the env var rather than inspecting the registered store, so
// it gives false assurance for the default path: with the raw
// `FileSecretStore` and a master key env var set, that validator passes
// while the store ignores the key and writes plaintext. This validator
// closes that gap by inspecting the *actual store instance* (via the
// `EncryptedSecretStore.ProvidesEncryptionAtRest` capability), and is
// registered only when the OAuth substrate is active (DataIngestion
// enabled), so the refusal is scoped to deployments that actually
// persist OAuth credentials.
//
// Escape hatch: `ServerConfig.AcceptPlaintextSecretsWhenAuthRequired`
// (default `false`) — the same informed-opt-out the secrets-at-rest
// validator honours (disk FDE / a backend with its own encryption).

/// Does the registered `ISecretStore` actually provide encryption at
/// rest? True for an `EncryptedSecretStore` with a master key, or a
/// cloud-KMS-backed store (detected by the `TOOLUP_SECRET_STORE` env
/// switch, matching `EncryptedSecretStoreModeValidator`'s carve-out).
/// A raw `FileSecretStore`, or an `EncryptedSecretStore` in plaintext-
/// passthrough mode, reports `false`.
let secretStoreProvidesEncryptionAtRest (store: Secrets.ISecretStore) =
    let cloudKmsBacked =
        match Environment.GetEnvironmentVariable ConfigKeys.Names.secretStore with
        | null
        | "" -> false
        | s ->
            match s.ToLowerInvariant() with
            | "azure-key-vault"
            | "aws-secrets-manager"
            | "vault"
            | "gcp-secret-manager" -> true
            | _ -> false

    match store with
    | :? EncryptedSecretStore.EncryptedSecretStore as e -> e.ProvidesEncryptionAtRest
    | _ -> cloudKmsBacked

/// Phase 138 — refuse an authenticated, OAuth-connector deployment whose
/// secret store does not encrypt at rest. Store-type-aware (inspects the
/// registered instance), unlike the env-var-only secrets-at-rest
/// validator. Registered inside the DataIngestion-gated OAuth block, so
/// it fires only when connector OAuth credentials are actually persisted.
type OAuthSecretEncryptionModeValidator(config: ServerConfig, secretStore: Secrets.ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // OAuth connector tokens persisted to a non-encrypting store is a
    // secret-exposure hole — security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "oauth-secret-encryption-mode"
        member _.Timeout = timeout

        member _.Validate() =
            ConfigValidator.gatedAuthValidation
                config
                (fun () ->
                    not (secretStoreProvidesEncryptionAtRest secretStore)
                    && not config.AcceptPlaintextSecretsWhenAuthRequired)
                (fun () ->
                    Error(
                        sprintf
                            "ServerConfig.Surfaces = %s and connector OAuth flows are active, but the registered ISecretStore does not encrypt at rest — connector refresh tokens (and cached access tokens) will sit in plaintext blob storage. Resolutions: (1) compose the EncryptedSecretStore decorator with TOOLUP_SECRETS_MASTER_KEY set to a base64-encoded 32-byte key (EncryptedSecretStore.generateMasterKey () once during setup); (2) switch to a cloud-KMS-backed store via TOOLUP_SECRET_STORE=azure-key-vault (or aws-secrets-manager / vault / gcp-secret-manager); (3) set ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true if your storage backend provides at-rest encryption (disk FDE) and you've made an informed decision to rely on that. Note: the default SDK secret store is a raw FileSecretStore with no encryption — a master key env var alone does not encrypt it. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            (DeploymentConfig.surfacesLabel config)
                    ))