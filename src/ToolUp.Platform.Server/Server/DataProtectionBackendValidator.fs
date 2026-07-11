// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DataProtectionBackendValidator

open System
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.ConfigValidation

// ─── Phase 329 — DataProtection key-ring backend preflight ───────────
//
// The DataProtection key ring persists through `BlobXmlRepository`
// under `_platform/dataprotection/` (Phase 9j). If that backend is
// misconfigured or unreachable at startup, ASP.NET DataProtection sees
// an empty ring, silently mints a fresh ephemeral key, and the app
// boots green — the fault surfaces much later as an unexplained 403
// `csrf_validation_failed` storm (other replicas / post-restart
// processes can't verify the seal) with no log line pointing at the
// blob store. This validator probes read + write access to the
// key-ring prefix at preflight so the real fault fails loudly before
// Kestrel binds, naming the container, prefix, and underlying error.
//
// Probe shape (mirrors `ConfigValidator.BlobStorageValidator`, scoped
// to the key-ring prefix): `List` over the prefix — the exact read
// `BlobXmlRepository.GetAllElements` performs — then a sentinel
// write / readback / delete under the prefix. An empty listing is NOT
// a failure: a genuinely-empty first-boot ring is legitimate, and this
// probe is precisely what makes it distinguishable from a read failure.
// The sentinel body is valid XML so a leftover from a failed
// best-effort delete still parses cleanly in `GetAllElements` (the
// ASP.NET key manager skips non-key elements).
//
// Security-class: an ephemeral-key boot is a cross-instance-auth-state
// hole — the CSRF seal (and any other DataProtection-sealed payload)
// silently stops verifying across replicas / restarts. Per Phase 327
// the marker means `SkipPreflight = true` cannot bypass this probe.

[<Literal>]
let private sentinelBlobName = "dataprotection/_preflight/sentinel.xml"

/// Phase 329 — probes read/write access to the DataProtection key-ring
/// prefix in `_platform` at startup. A misconfigured / unreachable
/// backend fails preflight with an actionable message instead of
/// booting with a silent ephemeral key. Reads the same container +
/// prefix constants as `BlobXmlRepository` (single source of truth).
type DataProtectionBackendValidator(storage: IBlobStorage, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    let fail (operation: string) (underlying: string) =
        Error(
            sprintf
                "DataProtection key-ring backend probe failed (%s) for container '%s' prefix '%s': %s. The key ring persists through the resolved IBlobStorage; a backend that cannot be read or written at startup would make DataProtection mint a fresh ephemeral key on every boot, so sealed payloads (e.g. the stateless CSRF token) fail verification on other replicas and after restarts. Fix the blob-storage configuration for the '%s' container (connectivity, credentials, container existence, permissions) and redeploy."
                operation
                BlobDpKeyRing.Container
                BlobDpKeyRing.Prefix
                underlying
                BlobDpKeyRing.Container
        )

    // An unverifiable key-ring backend is a cross-instance-auth-state
    // hole (ephemeral-key boot ⇒ CSRF/auth seals silently stop
    // verifying) — security-class, so SkipPreflight can't bypass it.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "dataprotection-keyring-backend"
        member _.Timeout = timeout

        member _.Validate() = async {
            try
                // The exact read GetAllElements performs. An empty list
                // is Ok — a first-boot ring is legitimately empty; only
                // a raised failure means the backend is unreachable.
                let! _ = storage.List(BlobDpKeyRing.Container, BlobDpKeyRing.Prefix)

                // Write access: key creation/rotation must be able to
                // persist. Valid-XML sentinel; best-effort delete.
                let payload =
                    System.Text.Encoding.UTF8.GetBytes(
                        sprintf "<dataProtectionPreflightSentinel utc=\"%s\" />" (DateTime.UtcNow.ToString "o")
                    )

                let! uploadResult = storage.Upload(BlobDpKeyRing.Container, sentinelBlobName, payload)

                match uploadResult with
                | Result.Error msg -> return fail "sentinel upload" msg
                | Result.Ok _ ->
                    let! downloadResult = storage.Download(BlobDpKeyRing.Container, sentinelBlobName)

                    match downloadResult with
                    | Result.Error msg -> return fail "sentinel download" msg
                    | Result.Ok readback ->
                        // Best-effort delete — a leftover sentinel is
                        // valid XML at a fixed path, overwritten on the
                        // next deploy and skipped by the key manager.
                        let! _ = storage.Delete(BlobDpKeyRing.Container, sentinelBlobName)

                        if readback = payload then
                            return Ok
                        else
                            return
                                fail
                                    "sentinel readback"
                                    "write succeeded but read returned unexpected bytes (silent corruption or permission gap)"
            with ex ->
                return fail "probe" ex.Message
        }