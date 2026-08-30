// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SecretStoreAtRestPostureValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets

// ─── Phase 457 — the unconditional at-rest posture gate ──────────────
//
// Two validators already reason about plaintext secrets, and between them
// they leave a deployment-shaped hole:
//
//   * `EncryptedSecretStoreModeValidator` (Phase 6l.E) reads the MASTER
//     KEY env var. It says nothing about which store is composed, so a
//     deployment on a raw `FileSecretStore` with the key set passes while
//     writing plaintext.
//   * `OAuthSecretEncryptionModeValidator` (Phase 138 / 340) inspects the
//     composed store — but is registered only inside the DataIngestion
//     -gated OAuth block, so it never runs for a deployment that persists
//     BYOK provider keys, webhook signing secrets and per-tenant
//     credentials without running a connector OAuth flow.
//
// So the shape this closes is: `RequireAuth`, a non-encrypting store, no
// connector OAuth. That deployment writes every secret it holds to disk in
// the clear, and its entire signal is one `Warn` line at boot from
// `SecretStore.fromEnv` — emitted before the log sink is usually being
// watched, and only on the default composition path at that.
//
// This validator asks the one question that covers all of them: does the
// store that is ACTUALLY COMPOSED encrypt what it persists? It fires
// whenever the deployment requires auth, whatever the store, whatever the
// substrate around it.
//
// ─── Why it can refuse where its siblings only warn ──────────────────
//
// It is an `Error` by default, and that is a deliberate break with GP 11's
// usual "a new check never turns a green boot red". A deployment this
// fires on is one whose secrets are readable by anything that can read the
// disk — the acceptance criterion the phase pins is precisely that it
// stops booting. The acknowledgement below is what keeps that honest: an
// operator whose medium provides the encryption (disk FDE, an encrypting
// volume, a KMS-managed bucket) says so once and boots, and the fact that
// they said so is then visible in preflight rather than assumed.
//
// ─── The acknowledgement is ONE fact with two spellings ──────────────
//
// `TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1` and the older
// `TOOLUP_ACCEPT_PLAINTEXT_SECRETS_IN_AUTH_MODE=1` both set
// `ServerConfig.AcceptPlaintextSecretsWhenAuthRequired` through
// `ServerConfig.fromEnv`, so all three plaintext-secret validators honour
// either. This validator ALSO resolves the shorter key itself, because a
// consumer that builds its `ServerConfig` by hand never goes through
// `fromEnv` — and an acknowledgement that works only on one composition
// path is an acknowledgement an operator cannot rely on.

/// Is the deployment's store a cloud KMS the SDK recognises by name?
/// The `TOOLUP_SECRET_STORE` carve-out `EncryptedSecretStoreModeValidator`
/// and `OAuthSecretEncryptionModeValidator` already apply, reused here so
/// the three cannot drift on what counts as managed encryption.
let private cloudKmsSwitch () : string option =
    match ConfigResolution.tryValue ConfigKeys.Names.secretStore with
    | None -> None
    | Some s ->
        match s.ToLowerInvariant() with
        | "azure-key-vault"
        | "aws-secrets-manager"
        | "vault"
        | "gcp-secret-manager" -> Some(s.ToLowerInvariant())
        | _ -> None

/// The at-rest posture of a composed `ISecretStore`, in the one place the
/// SDK answers that question.
///
/// The ladder, and the order is load-bearing:
///
///  1. **What the store DECLARES** (`ISecretStoreAtRestPosture`) wins.
///     A store that answers for itself is the best evidence available,
///     and it is the only rung that can speak for a companion or a
///     consumer's own implementation.
///  2. **The `TOOLUP_SECRET_STORE` carve-out**, for a store that declares
///     nothing. It is a recognition, not a declaration — the switch
///     records what the deployment ASKED for, and a companion that fell
///     back to the local default still matches it — so it is consulted
///     only when nobody has answered. This rung is what keeps an existing
///     KMS deployment on an older companion booting unchanged (GP 11).
///  3. **Unknown**, named as unknown. A fail-closed reader treats it as
///     not-encrypting, but the message says nobody declared rather than
///     asserting plaintext — a guard that overstates its evidence is one
///     operators learn to override on reflex.
let resolveAtRestPosture (store: ISecretStore) : SecretAtRestPosture =
    match box store with
    | :? ISecretStoreAtRestPosture as declared -> declared.AtRestPosture
    | _ ->
        match cloudKmsSwitch () with
        | Some name ->
            EncryptsAtRest(
                sprintf "%s=%s (the companion's own managed encryption at rest)" ConfigKeys.Names.secretStore name
            )
        | None ->
            UnknownAtRest(
                sprintf
                    "the composed %s implements no ISecretStoreAtRestPosture and %s names no managed backend, so nothing has stated what happens to these values on disk"
                    (store.GetType().Name)
                    ConfigKeys.Names.secretStore
            )

/// Does the deployment encrypt secrets at rest, by its store's own account
/// or by a recognition the SDK can make? `false` covers both the declared
/// -plaintext and the nobody-declared cases — the two differ in what an
/// operator is TOLD, never in whether the guard fires.
let encryptsAtRest (store: ISecretStore) : bool =
    match resolveAtRestPosture store with
    | EncryptsAtRest _ -> true
    | PlaintextAtRest _
    | UnknownAtRest _ -> false

/// The acknowledgement, resolved from both places it can legitimately
/// come from: the typed flag (which `fromEnv` sets from either env
/// spelling) and the shorter key read straight through the config seam
/// for a hand-built `ServerConfig`.
let internal acknowledged (config: ServerConfig) : bool =
    config.AcceptPlaintextSecretsWhenAuthRequired
    || (match ConfigResolution.tryValue ConfigKeys.Names.acceptPlaintextSecrets with
        | None -> false
        | Some v ->
            match v.Trim().ToLowerInvariant() with
            | "1"
            | "true"
            | "yes"
            | "on" -> true
            | _ -> false)

/// The remediation menu, identical whichever severity is emitted — the
/// severity decides what happens at startup, never what the operator is
/// told to do about it (the `OAuthSecretEncryptionModeValidator` rule).
let private resolutions =
    "Resolutions: (1) compose EncryptedSecretStore over the local store and set TOOLUP_SECRETS_MASTER_KEY to a base64-encoded 32-byte key (EncryptedSecretStore.generateMasterKey () once during deployment setup) — note the SDK's default store is a raw FileSecretStore with no encryption, so the key alone does nothing until the decorator is composed; (2) switch to a KMS-backed store via TOOLUP_SECRET_STORE=azure-key-vault (or aws-secrets-manager / gcp-secret-manager / vault) and wire that companion's resolver; (3) if the storage medium itself provides at-rest encryption (disk FDE, an encrypting volume, a KMS-managed bucket) and you have made an informed decision to rely on it, acknowledge that with TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1 (or ServerConfig.AcceptPlaintextSecretsWhenAuthRequired = true) — the deployment then boots and the acknowledgement is reported as a warning. A custom ISecretStore that does encrypt can declare it by implementing ISecretStoreAtRestPosture. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."

/// Phase 457 — refuse an auth-requiring deployment whose composed secret
/// store does not encrypt at rest, whichever store that is.
///
/// Security-class (`ISecurityClassValidator`), so `SkipPreflight = true`
/// does not bypass it: one boolean intended to skip slow connectivity
/// probes must not be able to turn off the check that stands between BYOK
/// keys and a readable disk.
type SecretStoreAtRestPostureValidator(config: ServerConfig, secretStore: ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "secret-store-at-rest-posture"
        member _.Timeout = timeout

        member _.Validate() = async {
            // An anonymous-surface deployment is out of scope here, and
            // deliberately: it holds no per-user credentials by
            // definition, and `OAuthSecretEncryptionModeValidator` already
            // covers the one thing such a deployment does persist (its own
            // connector grants) with a non-blocking Warning.
            if not (DeploymentConfig.requiresAnyAuth config) then
                return Ok
            else
                let label = DeploymentConfig.surfacesLabel config

                match resolveAtRestPosture secretStore with
                | EncryptsAtRest _ ->
                    // The whole of the unchanged path. A deployment that
                    // already encrypts sees nothing new (GP 11).
                    return Ok
                | PlaintextAtRest reason
                | UnknownAtRest reason ->
                    if acknowledged config then
                        return
                            Warning(
                                sprintf
                                    "ServerConfig.Surfaces = %s and the composed ISecretStore does not encrypt at rest (%s), so every BYOK provider key, OAuth token and webhook secret this deployment stores is readable by anything that can read the medium. This is NOT being refused only because the plaintext-secrets acknowledgement is set (TOOLUP_ACCEPT_PLAINTEXT_SECRETS / ServerConfig.AcceptPlaintextSecretsWhenAuthRequired). Confirm the medium really does provide at-rest encryption; if it does not, clear the acknowledgement. %s"
                                    label
                                    reason
                                    resolutions
                            )
                    else
                        return
                            Error(
                                sprintf
                                    "ServerConfig.Surfaces = %s requires authentication, but the composed ISecretStore does not encrypt at rest (%s). Every BYOK provider key, OAuth token and webhook secret this deployment stores will sit unencrypted on the storage medium, permanently — a leaked backup or a misconfigured bucket exposes all of them at once. %s"
                                    label
                                    reason
                                    resolutions
                            )
        }