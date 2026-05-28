// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.OAuthRefreshJobHandler

open System
open ToolUp.Platform.Secrets

// ─── Phase 10h — OAuthRefreshJobHandler ──────────────────────────
//
// `IJobHandler` registered under
// `InProcessOAuthTokenRefresher.HandlerName` (`_platform.oauth.refresh`).
// Each registered descriptor schedules a `CronTrigger "*/5 * * * *"`
// against this handler; the handler gates on cached-expiry against
// `descriptor.ScheduleAheadOfExpiry` and only invokes the refresher's
// `AttemptRefresh` when the window has opened. The vast majority of
// dispatches short-circuit as no-ops (one `ISecretStore.GetSecret` +
// a `DateTimeOffset` comparison).
//
// **Audit emission split.** The refresher's `AttemptRefresh`
// emits `OAuthTokenRefreshed` (success) and `OAuthRefreshTokenInvalidated`
// (terminal-invalidation) — both are state-bearing events the
// substrate already has full context for. This handler emits
// `OAuthTokenRefreshFailed` per attempt and `OAuthRefreshDeadLettered`
// on terminal failure (immediate `PermanentError` or
// retry-exhausted `TransientError`).
//
// **Stateless across invocations.** The handler resolves the
// descriptor from `JobContext.Payload` on every dispatch
// (`InProcessOAuthTokenRefresher.tryDeserializeDescriptor`) — survives
// Orleans grain deactivation / Akka.Persistence restart / in-process
// recycle. No closure over registry state.
//
// **MaxAttempts coupling.** The handler hard-codes the dead-letter
// boundary against `InProcessOAuthTokenRefresher.DefaultRetryPolicy.MaxAttempts`
// (5). The scheduler will dead-letter the job after attempt 5
// regardless; the handler emits `OAuthRefreshDeadLettered` on
// attempt 5's failed `TransientError` for the dedicated OAuth audit
// trail (the scheduler emits its own generic `JobDeadLettered`
// audit independently). Connectors wiring a custom
// `IOAuthTokenRefresher` that uses a different policy must register
// their own job handler.

/// Read the cached access-token expiry from `ISecretStore` and
/// decide whether the refresh window has opened. Returns `true`
/// when the handler should invoke `AttemptRefresh`; `false` when
/// the cached expiry is still further out than
/// `descriptor.ScheduleAheadOfExpiry` (the typical short-circuit
/// path).
///
/// First-dispatch case (no cached expiry yet) returns `true` so
/// the first scheduled tick after `RegisterDescriptor` actually
/// runs the refresh and seeds the expiry secret.
let private isRefreshWindowOpen (secretStore: ISecretStore) (descriptor: OAuthRefreshDescriptor) : Async<bool> = async {
    let! existing = secretStore.GetSecret(descriptor.ScopeId, OAuthRefreshDescriptor.accessExpiryKey descriptor)

    match existing with
    | None -> return true
    | Some iso ->
        match DateTimeOffset.TryParse iso with
        | true, expiry ->
            let untilExpiry = expiry - DateTimeOffset.UtcNow
            return untilExpiry <= descriptor.ScheduleAheadOfExpiry
        | _ ->
            // Cached expiry corrupt / unparseable — treat as
            // open-window so the next attempt re-seeds it.
            return true
}

/// Translate `OAuthRefreshResult` to `JobResult` and emit the
/// handler-owned audit family (`OAuthTokenRefreshFailed` per
/// transient attempt, `OAuthRefreshDeadLettered` on terminal
/// outcomes).
let private classify
    (auditLog: IAuditLog)
    (descriptor: OAuthRefreshDescriptor)
    (attempt: int)
    (maxAttempts: int)
    (result: OAuthRefreshResult)
    : Async<JobResult> =
    async {
        match result with
        | Refreshed _ ->
            // Refresher emitted OAuthTokenRefreshed already.
            return JobResult.Success
        | TokenInvalidatedByProvider ->
            // Refresher emitted OAuthRefreshTokenInvalidated already;
            // terminal — skip remaining retries.
            return JobResult.PermanentFailure "refresh token invalidated by provider"
        | TransientError reason ->
            do!
                auditLog.Record(
                    descriptor.ScopeId,
                    OAuthTokenRefreshFailed {
                        Provider = descriptor.Provider
                        ConfigId = descriptor.ConfigId
                        ScopeId = descriptor.ScopeId
                        Attempt = attempt
                        Reason = reason
                    }
                )

            if attempt >= maxAttempts then
                do!
                    auditLog.Record(
                        descriptor.ScopeId,
                        OAuthRefreshDeadLettered {
                            Provider = descriptor.Provider
                            ConfigId = descriptor.ConfigId
                            ScopeId = descriptor.ScopeId
                            Attempts = attempt
                            FinalReason = reason
                        }
                    )

                return JobResult.PermanentFailure(sprintf "exhausted retries after %d attempts: %s" attempt reason)
            else
                return JobResult.TransientFailure reason
        | PermanentError reason ->
            do!
                auditLog.Record(
                    descriptor.ScopeId,
                    OAuthTokenRefreshFailed {
                        Provider = descriptor.Provider
                        ConfigId = descriptor.ConfigId
                        ScopeId = descriptor.ScopeId
                        Attempt = attempt
                        Reason = reason
                    }
                )

            do!
                auditLog.Record(
                    descriptor.ScopeId,
                    OAuthRefreshDeadLettered {
                        Provider = descriptor.Provider
                        ConfigId = descriptor.ConfigId
                        ScopeId = descriptor.ScopeId
                        Attempts = attempt
                        FinalReason = reason
                    }
                )

            return JobResult.PermanentFailure reason
    }

/// `IJobHandler` for `_platform.oauth.refresh`. Reads the descriptor
/// from `JobContext.Payload`, gates on the refresh window, and
/// dispatches to `InProcessOAuthTokenRefresher.Impl.AttemptRefresh`
/// (which does the actual HTTP + persistence work).
type OAuthRefreshJobHandler
    (refresher: InProcessOAuthTokenRefresher.Impl, secretStore: ISecretStore, auditLog: IAuditLog, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx) = async {
            match InProcessOAuthTokenRefresher.tryDeserializeDescriptor ctx.Payload with
            | None ->
                let msg = sprintf "[OAuthRefreshJobHandler] malformed payload for job %A" ctx.JobId
                logger.Warn msg
                return JobResult.PermanentFailure msg
            | Some descriptor ->
                let! windowOpen = isRefreshWindowOpen secretStore descriptor

                if not windowOpen then
                    return JobResult.Success
                else
                    let! result = refresher.AttemptRefresh descriptor

                    let maxAttempts = InProcessOAuthTokenRefresher.DefaultRetryPolicy.MaxAttempts

                    return! classify auditLog descriptor ctx.Attempt maxAttempts result
        }

/// Factory. Compose-time wiring constructs the handler and registers
/// it under `InProcessOAuthTokenRefresher.HandlerName` on the
/// scheduler.
let create
    (refresher: InProcessOAuthTokenRefresher.Impl)
    (secretStore: ISecretStore)
    (auditLog: IAuditLog)
    (logger: ILogger)
    : IJobHandler =
    OAuthRefreshJobHandler(refresher, secretStore, auditLog, logger) :> IJobHandler