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
// This validator *warns* (never refuses — the auto-generate path is a
// legitimate convenience and the store still functions) when the share-
// token surface is live AND the deployment is production/multi-instance
// shaped AND `share_token_signing_key` is absent at startup, steering the
// operator to pre-provision the key as a managed secret with a documented
// rotation procedure. A single-instance / non-public deployment, or one
// that already has the key provisioned, is silent.
//
// Security-class (provenance): an auto-generated, operator-unknown HMAC
// signing key is a security-critical secret with no rotation / backup
// governance — the same class of hole as an unmanaged auth secret. It is
// a `Warning` (never a refusal — the auto-generate path still functions),
// but a security-class one: the warning must survive the `SkipPreflight`
// emergency-boot lever rather than being silently bypassable, so an
// operator can't lose the provenance signal by flipping one boolean.

/// Wave 19 — warns when the share-token HMAC signing key would be
/// auto-generated (absent from `ISecretStore` at startup) in a
/// production / multi-instance deployment, so the security-critical key
/// is not invisible to backup / rotation governance. Reads the same
/// container + secret name as `BlobShareTokenStore.resolveSigningKey`
/// (single source of truth).
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
                let! existing =
                    secretStore.GetSecret(ShareTokenStore.platformContainer, ShareTokenStore.signingKeySecretName)

                match existing with
                | Some _ -> return Ok
                | None ->
                    return
                        Warning(
                            sprintf
                                "ServerConfig.Surfaces = %s with a live share-token surface, but the HMAC signing key '%s' is absent from the ISecretStore — BlobShareTokenStore will auto-generate and persist a 32-byte key on first use. In a production / multi-instance deployment that leaves a security-critical secret unmanaged: it is invisible to backup and rotation governance (if the secret store is wiped, every issued share-token silently fails verification), and with multiple replicas the authoritative key is whichever replica wins the first-write race. Pre-provision '%s' as an operator-managed secret (a base64url-encoded 32+ byte value) before first boot, and document a rotation procedure (rotating it invalidates all outstanding tokens — expected). After fixing, verify in the HealthMonitorUI Preflight tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                                (DeploymentConfig.surfacesLabel config)
                                ShareTokenStore.signingKeySecretName
                                ShareTokenStore.signingKeySecretName
                        )
        }