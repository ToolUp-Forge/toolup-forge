// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ShareTokenSigningKeyProvenanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Wave 19 — share-token signing-key provenance preflight ──────────
//
// `BlobShareTokenStore` resolves its HMAC signing key from
// `ISecretStore`: if `share_token_signing_key` is absent it generates a
// 32-byte CSPRNG key and persists it on first use (`resolveSigningKey`).
// That is fine for a single-instance dev deployment, but in a
// production / multi-instance shape it has two governance gaps the
// 2026-06-12 Auth-core audit flagged (CSRF/SSE/share-token Finding 6):
//
//   1. **Backup / rotation invisibility.** A key the operator never
//      provisioned is a security-critical secret that nobody knows to
//      back up or rotate. If the secret store is wiped / re-provisioned,
//      every previously-issued share token silently fails verification;
//      and there is no managed rotation procedure because the key was
//      never managed.
//   2. **Multi-instance first-write race.** With N replicas booting
//      against an empty secret store, each calls `resolveSigningKey`;
//      the first `SetSecret` wins and the others read it back (or, on a
//      backend with weak read-after-write, briefly diverge). The key
//      that ends up authoritative is whichever replica raced first —
//      again, not an operator-chosen, operator-backed value.
//
// Security-class (provenance): an auto-generated, operator-unknown HMAC
// signing key is a security-critical secret with no rotation / backup
// governance — the same class of hole as an unmanaged auth secret. The
// finding must survive the `SkipPreflight` emergency-boot lever rather
// than being silently bypassable, so an operator can't lose the
// provenance signal by flipping one boolean.
//
// ─── Phase 460 — the key becomes an operator-managed secret ──────────
//
// Wave 19 only *warned*, and a warning is exactly what an unmanaged
// secret does not need: the deployment booted, the key was minted, and
// the finding scrolled past in a startup log nobody re-reads. This phase
// makes the posture a refusal in the shapes where it matters, and makes
// it VISIBLE in the shapes where it does not.
//
// The ladder, all of it gated behind a live share-token surface:
//
//   * **Key operator-provisioned ⇒ `Ok`.** Unchanged, no message, no new
//     work. A correctly-configured deployment sees nothing (GP 11).
//   * **Non-production-shaped ⇒ `Ok`.** A single-instance, non-public
//     deployment treats auto-generation as the convenience it is. Also
//     unchanged.
//   * **Production-shaped, key absent, no acknowledgement ⇒ `Error`.**
//     The phase's headline: a public or multi-replica deployment no
//     longer boots onto a key nobody provisioned. This DOES stop a
//     deployment that previously started with a warning — deliberately;
//     the non-breaking route for such a deployment is one env var
//     (below), and the correct route is provisioning the key.
//   * **Production-shaped, key absent, acknowledged ⇒ `Warning`.**
//     `ServerConfig.AcceptEphemeralShareTokenKey` /
//     `TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1` downgrades the refusal
//     and says so. The 2026-08 lesson from the OAuth-secret validator
//     applies verbatim: an escape hatch that suppresses SILENTLY makes
//     the flag and a correct configuration indistinguishable in every
//     artefact preflight produces. An opt-out nobody remembers making
//     stops being invisible.
//   * **Production-shaped, key present but AUTO-GENERATED ⇒ `Warning`.**
//     The gap Wave 19 structurally could not see: once the key had been
//     minted, `GetSecret` returned `Some` and the validator went quiet
//     forever — the deployment was running on an unmanaged secret and
//     preflight reported clean. `BlobShareTokenStore` now writes a
//     provenance marker beside the key when it mints one, so the two
//     cases are distinguishable and the posture is reported for as long
//     as it holds, not just on the one boot that created it.
//
// A key minted BEFORE this phase carries no marker and reads as
// operator-provisioned. That is the honest classification — nothing in
// the store records its origin — and it means no existing green
// deployment newly reddens on the auto-generated arm.

/// The remediation menu, identical across every severity this validator
/// emits — a deployment reading the Warning gets the same fixes the
/// refusal prints, so severity changes what happens at startup and never
/// what the operator is told to do about it.
let private resolutions =
    "Resolutions: (1) pre-provision 'share_token_signing_key' in the ISecretStore under the reserved '_platform' scope, as a base64url-encoded 32+ byte operator-managed secret, before first boot — and record a rotation procedure alongside it (rotating invalidates all outstanding share links, which is expected and is why the value must be backed up); (2) if the deployment is not in fact production-shaped, clear ServerConfig.PublicBaseUrl / set ReplicaCount = 1 so the share-token surface is not treated as internet-facing; (3) set ServerConfig.AcceptEphemeralShareTokenKey = true (TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1) to acknowledge a throwaway key for dev / CI / a single instance — the finding then reports as a Warning rather than refusing. See DEPLOYMENT.md, 'Share-token signing key'. After fixing, verify in the HealthMonitorUI Preflight tab (production-safe) or /dev/inspect Validators panel (debug builds only)."

/// Phase 460 — refuses a production / multi-instance share-token
/// deployment whose HMAC signing key is unprovisioned, and reports the
/// key's provenance (operator-provisioned vs auto-generated) on the
/// preflight snapshot so the posture is visible without log archaeology.
/// Reads the same container + secret names as
/// `BlobShareTokenStore.resolveSigningKey` (single source of truth).
type ShareTokenSigningKeyProvenanceValidator
    (config: ServerConfig, secretStore: Secrets.ISecretStore, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // An auto-generated, unmanaged share-token HMAC key is a
    // security-critical secret with no rotation / backup governance —
    // security-class, so its Warning must survive SkipPreflight (see file header).
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "share-token-signing-key-provenance"
        member _.Timeout = timeout

        member _.Validate() = async {
            // The share-token surface is live when the substrate is
            // explicitly enabled or auto-promoted by a claim-bearer
            // surface (Phase 66) — only then is a signing key resolved.
            let shareTokenSurfaceLive =
                config.ShareTokenStore = EnabledShareTokenStore
                || DeploymentConfig.hasClaimBearer config

            // Production / multi-instance shape: an explicit replica
            // declaration, or an inferred public surface (commonly
            // load-balanced). A single-instance, non-public deployment
            // treats auto-generation as a fine convenience.
            let declaredScaleOut = config.ReplicaCount > 1

            let inferredPublic =
                match config.PublicBaseUrl with
                | Some url -> not (String.IsNullOrWhiteSpace url)
                | None -> false

            let productionShaped = declaredScaleOut || inferredPublic

            if not shareTokenSurfaceLive || not productionShaped then
                return Ok
            else
                let label = DeploymentConfig.surfacesLabel config
                let! provenance = ShareTokenStore.probeSigningKeyProvenance secretStore

                match provenance with
                | ShareTokenStore.OperatorProvisioned -> return Ok

                | ShareTokenStore.AutoGenerated ->
                    return
                        Warning(
                            sprintf
                                "ServerConfig.Surfaces = %s with a live share-token surface: the HMAC signing key '%s' IS present, but it was AUTO-GENERATED by this SDK rather than provisioned by an operator (recorded by the '%s' marker beside it). The deployment works, and that is the problem — a security-critical secret nobody was told to back up or rotate is one wiped secret store away from invalidating every outstanding public share link, silently and irrecoverably. Adopt it: read the current value, record it in your secret-management system as an operator-managed secret, and delete the '%s' marker to acknowledge the handover. Or rotate to a value you chose (a base64url-encoded 32+ byte secret) — rotating invalidates all outstanding share links, which is expected. After fixing, verify in the HealthMonitorUI Preflight tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                                label
                                ShareTokenStore.signingKeySecretName
                                ShareTokenStore.signingKeyOriginSecretName
                                ShareTokenStore.signingKeyOriginSecretName
                        )

                | ShareTokenStore.AbsentWillAutoGenerate ->
                    if config.AcceptEphemeralShareTokenKey then
                        return
                            Warning(
                                sprintf
                                    "ServerConfig.Surfaces = %s with a live share-token surface, and the HMAC signing key '%s' is absent from the ISecretStore — BlobShareTokenStore will auto-generate and persist a 32-byte key on first use. This is NOT being refused only because ServerConfig.AcceptEphemeralShareTokenKey = true (TOOLUP_ACCEPT_EPHEMERAL_SHARE_TOKEN_KEY=1). That informed opt-out is suppressing a startup refusal: the key stays invisible to backup and rotation governance, and if the secret store is wiped or re-provisioned every issued share-token silently fails verification. Confirm this deployment really is one where throwaway share links are acceptable (dev / CI / a single instance behind no public URL); if it is not, clear the flag and pre-provision '%s'. %s"
                                    label
                                    ShareTokenStore.signingKeySecretName
                                    ShareTokenStore.signingKeySecretName
                                    resolutions
                            )
                    else
                        return
                            Error(
                                sprintf
                                    "ServerConfig.Surfaces = %s with a live share-token surface, but the HMAC signing key '%s' is absent from the ISecretStore. In a production / multi-instance shape this deployment must not boot onto a key it mints for itself: the key would be invisible to backup and rotation governance (wipe or re-provision the secret store and every issued share-token silently fails verification), and with multiple replicas the authoritative key is whichever replica wins the first-write race. %s"
                                    label
                                    ShareTokenStore.signingKeySecretName
                                    resolutions
                            )
        }