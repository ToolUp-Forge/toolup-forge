module ToolUp.Platform.AdminTokenValidator

open System
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.ConfigValidation

// ─── Gap #8 — Admin-token discoverability for crypto-shred ──────────
//
// When `IBlobEncryptionKeyResolver = PerScopeKeyResolver`, `compose`
// auto-mounts `POST /api/_platform/encryption/destroy-scope-key/{scopeId}`
// (the Phase 22 crypto-shred admin endpoint). The handler fail-closes
// at runtime: requests return 401 if `TOOLUP_ADMIN_TOKEN` is unset.
// Security-wise this is correct — no unauthenticated access. But the
// feature is silently dormant: an operator who relies on crypto-shred
// for tenant offboarding (GDPR right-to-erasure, contract termination)
// only discovers the missing env var when they need the feature for
// real, often weeks or months after deploy.
//
// This validator surfaces the gap at preflight as a `Warning` so the
// HealthMonitorUI / `/dev/inspect` Validators panel show it permanently
// until the operator either (a) sets the env var, or (b) decides they
// don't need crypto-shred and switches to `SingleKeyResolver`.
//
// Severity is `Warning` (not `Error`) because:
//   1. The endpoint already fail-closes at runtime; missing token is
//      not a security vulnerability — only a usability gap.
//   2. Some deployments may legitimately register `PerScopeKeyResolver`
//      while not yet exposing crypto-shred to operators (planned for a
//      later admin UI rollout).

[<Literal>]
let private AdminTokenEnvVar = "TOOLUP_ADMIN_TOKEN"

let private isAdminTokenSet () =
    match Environment.GetEnvironmentVariable AdminTokenEnvVar with
    | null
    | "" -> false
    | _ -> true

/// Gap #8 — config validator that warns when `PerScopeKeyResolver` is
/// registered (auto-mounting the crypto-shred admin endpoint) but
/// `TOOLUP_ADMIN_TOKEN` is unset. Endpoint already fail-closes at
/// runtime; this validator surfaces the dormant feature at preflight.
type AdminTokenValidator(encryptionKeyResolver: IBlobEncryptionKeyResolver option, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "admin-token"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isPerScope =
                match encryptionKeyResolver with
                | Some r -> r.GetType() = typeof<PerScopeKeyResolver.PerScopeKeyResolver>
                | None -> false

            if isPerScope && not (isAdminTokenSet ()) then
                return
                    Warning(
                        "PerScopeKeyResolver is registered (the crypto-shred admin endpoint POST /api/_platform/encryption/destroy-scope-key/{scopeId} is auto-mounted) but TOOLUP_ADMIN_TOKEN is unset — every call to the endpoint will return 401 'TOOLUP_ADMIN_TOKEN not configured'. The endpoint fail-closes correctly (no unauthenticated access) but the feature is silently dormant: operators who need crypto-shred for tenant offboarding (GDPR right-to-erasure, contract termination) will only discover the missing env var when they need it. Generate a random bearer token and set TOOLUP_ADMIN_TOKEN to enable the endpoint, then call it with the X-Admin-Token request header. Pair with X-Actor-UserId to record the requester in the EncryptionKeyDestroyed audit event."
                    )
            else
                return Ok
        }