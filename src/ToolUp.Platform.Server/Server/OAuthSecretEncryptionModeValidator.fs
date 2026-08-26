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
//
// ─── Phase 340 — the refusal scope is no longer auth-gated ───────────
//
// Phase 138 wrapped the predicate in `ConfigValidator.gatedAuthValidation`,
// so the whole check evaluated to `Ok` unless `requiresAnyAuth` held. That
// gate is wrong for THIS validator, and only for this one: its siblings
// (`csrf-default-mode`, `header-auth-mode`, `sse-auth-mode`) reason about
// how a REQUEST is authenticated, which is genuinely meaningless without
// auth — but connector OAuth credentials are persisted by the ingestion
// substrate regardless of how (or whether) the deployment authenticates
// its callers. An `Anonymous`-surface deployment running OAuth connectors
// writes exactly the same long-lived third-party refresh tokens to exactly
// the same plaintext blob, and reported clean.
//
// Two silences are closed here, and neither closure turns a previously
// green startup red:
//
//   * A non-auth-requiring deployment persisting OAuth credentials to a
//     non-encrypting store now emits `Warning` where it emitted `Ok`. It
//     is a Warning rather than an Error deliberately: the aggregator
//     treats Warning as non-blocking, so an existing anonymous deployment
//     upgrades, sees the finding in its preflight summary, and keeps
//     booting. Refusing there would break running deployments to close a
//     gap they can fix on their own schedule.
//   * `AcceptPlaintextSecretsWhenAuthRequired` used to suppress the
//     refusal SILENTLY — the flag and a correctly-encrypting store were
//     indistinguishable in every artefact preflight produced. It now
//     emits a `Warning` naming the flag as the reason nothing was
//     refused. An informed opt-out stays honoured; an opt-out nobody
//     remembers making stops being invisible.
//
// A deployment whose store DOES encrypt at rest is byte-for-byte
// unchanged — `Ok`, no message, no new work (GP 11). That is the shape
// the acceptance criterion pins.

/// Does the registered `ISecretStore` actually provide encryption at
/// rest? True for an `EncryptedSecretStore` with a master key, or a
/// cloud-KMS-backed store (detected by the `TOOLUP_SECRET_STORE` env
/// switch, matching `EncryptedSecretStoreModeValidator`'s carve-out).
/// A raw `FileSecretStore`, or an `EncryptedSecretStore` in plaintext-
/// passthrough mode, reports `false`.
let secretStoreProvidesEncryptionAtRest (store: Secrets.ISecretStore) =
    let cloudKmsBacked =
        // Phase 698 — through the Phase-696 `ConfigResolution` seam.
        match ConfigResolution.tryValue ConfigKeys.Names.secretStore with
        | None -> false
        | Some s ->
            match s.ToLowerInvariant() with
            | "azure-key-vault"
            | "aws-secrets-manager"
            | "vault"
            | "gcp-secret-manager" -> true
            | _ -> false

    match store with
    | :? EncryptedSecretStore.EncryptedSecretStore as e -> e.ProvidesEncryptionAtRest
    | _ -> cloudKmsBacked

/// The remediation menu, identical across every severity this validator
/// emits — a deployment reading the Warning gets the same three fixes a
/// refusal prints, so severity changes what happens at startup and never
/// what the operator is told to do about it.
let private resolutions =
    "Resolutions: (1) compose the EncryptedSecretStore decorator with TOOLUP_SECRETS_MASTER_KEY set to a base64-encoded 32-byte key (EncryptedSecretStore.generateMasterKey () once during setup); (2) switch to a cloud-KMS-backed store via TOOLUP_SECRET_STORE=azure-key-vault (or aws-secrets-manager / vault / gcp-secret-manager); (3) set ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true if your storage backend provides at-rest encryption (disk FDE) and you've made an informed decision to rely on that. Note: the default SDK secret store is a raw FileSecretStore with no encryption — a master key env var alone does not encrypt it. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."

/// Phase 138 / Phase 340 — surface an OAuth-connector deployment whose
/// secret store does not encrypt at rest. Store-type-aware (inspects the
/// registered instance), unlike the env-var-only secrets-at-rest
/// validator. Registered inside the DataIngestion-gated OAuth block, so
/// it fires only when connector OAuth credentials are actually persisted.
///
/// Severity ladder (Phase 340): auth-requiring + no escape hatch is a
/// refusal, exactly as before; a non-auth-requiring deployment and a
/// deployment holding the escape hatch both get a non-blocking `Warning`
/// where they previously got a silent `Ok`.
type OAuthSecretEncryptionModeValidator(config: ServerConfig, secretStore: Secrets.ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // OAuth connector tokens persisted to a non-encrypting store is a
    // secret-exposure hole — security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "oauth-secret-encryption-mode"
        member _.Timeout = timeout

        member _.Validate() = async {
            if secretStoreProvidesEncryptionAtRest secretStore then
                // The only path that existed before Phase 340 AND still
                // returns a bare Ok. An encrypting deployment sees no
                // change whatsoever (GP 11).
                return Ok
            else
                let label = DeploymentConfig.surfacesLabel config
                let requiresAuth = DeploymentConfig.requiresAnyAuth config

                if config.AcceptPlaintextSecretsWhenAuthRequired then
                    return
                        Warning(
                            sprintf
                                "ServerConfig.Surfaces = %s and connector OAuth flows are active: the registered ISecretStore does not encrypt at rest, so connector refresh tokens (and cached access tokens) sit in plaintext blob storage. This is NOT being refused only because ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true — that informed opt-out is suppressing %s. Confirm the storage backend really does provide at-rest encryption (disk FDE / an encrypting volume); if it does not, clear the flag. %s"
                                label
                                (if requiresAuth then
                                     "a startup refusal"
                                 else
                                     "this finding's escalation")
                                resolutions
                        )
                elif requiresAuth then
                    return
                        Error(
                            sprintf
                                "ServerConfig.Surfaces = %s and connector OAuth flows are active, but the registered ISecretStore does not encrypt at rest — connector refresh tokens (and cached access tokens) will sit in plaintext blob storage. %s"
                                label
                                resolutions
                        )
                else
                    return
                        Warning(
                            sprintf
                                "ServerConfig.Surfaces = %s (no authenticated surface) and connector OAuth flows are active, but the registered ISecretStore does not encrypt at rest — connector refresh tokens (and cached access tokens) will sit in plaintext blob storage. The credentials at risk are the deployment's own long-lived third-party grants, so an anonymous surface does not make this benign; it is a Warning rather than a refusal so an existing deployment can remediate on its own schedule. %s"
                                label
                                resolutions
                        )
        }