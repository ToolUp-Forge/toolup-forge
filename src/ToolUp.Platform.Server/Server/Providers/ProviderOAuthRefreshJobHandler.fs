// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ProviderOAuthRefreshJobHandler

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets

// ─── Phase 43.B — provider-entry OAuth token auto-refresh ─────────
//
// An `IJobHandler` registered with `IJobScheduler` at
// `JobPrecision.Minute` (GP 12 rule 6 — the in-process scheduler's
// floor, declared rather than assumed). One cron job per connected
// entry, `*/5 * * * *`; the handler short-circuits as a no-op unless
// the cached access token is inside
// `ProviderOAuthConnect.DefaultLeadTime` of expiry, so the steady-state
// cost is one `ISecretStore.GetSecret` and a `DateTime` comparison.
//
// **Why a job per entry, not one sweep job.** `IProviderProfile` is a
// per-`StorageScope` store with no cross-scope enumeration — by
// design, because enumerating every tenant's BYOK configuration from a
// background thread is exactly the shape GP 4 exists to prevent. So
// the substrate cannot discover which entries need refreshing; it is
// TOLD, at connect time, and the knowledge lives in the scheduler's
// own durable `JobDefinition` rather than in a process-local registry
// (rule 4 — the handler reads everything it needs out of
// `JobContext.Payload` on every dispatch, so a restart loses nothing).
//
// **On refresh failure the entry's `ProviderHealth.Status` becomes
// `NeedsReauthorization` and an audit event is emitted** — both done
// inside `ProviderOAuthConnect.refreshEntry`, which is also what the
// contract pack exercises, so the interactive and scheduled paths
// cannot diverge on the failure semantics.
//
// **GP 13.** Nothing here is registered when a deployment has no
// `IProviderProfile` — `ProviderOAuthCompose.wire` is a no-op in that
// case, so the handler is never constructed, the cron job never
// scheduled, and the deployment pays nothing.

/// Handler name the scheduler dispatches under. Stable — it is
/// persisted in every `JobDefinition.Handler` this substrate writes.
[<Literal>]
let HandlerName = "_platform.providers.oauth.refresh"

/// Cron expression for the per-entry refresh job. Five-minutely, the
/// same cadence as the Phase 10h data-source refresher: frequent
/// enough that the lead-time window is never missed, sparse enough
/// that the no-op dispatches cost nothing measurable.
[<Literal>]
let RefreshCronExpression = "*/5 * * * *"

/// Durable payload identifying WHICH entry a dispatch is for. Every
/// field is a value the handler needs to reconstruct the call without
/// consulting any in-memory state (GP 12 rules 1 + 4). `StorageScope`
/// is carried field-by-field rather than as the record so the payload
/// stays readable in the admin UI's job inspector.
type ProviderOAuthRefreshPayload = {
    ScopeId: string
    Container: string
    Persist: bool
    /// `ProviderEntry.Label` — also the correlation key's `Id`.
    EntryLabel: string
    /// `IProviderOAuthFlow.Name` that minted the credentials.
    FlowName: string
}

module ProviderOAuthRefreshPayload =
    let private options = FableConverters.create ()

    let serialize (payload: ProviderOAuthRefreshPayload) : string =
        JsonSerializer.Serialize(payload, options)

    let tryDeserialize (json: string) : ProviderOAuthRefreshPayload option =
        if String.IsNullOrWhiteSpace json then
            None
        else
            try
                let parsed = JsonSerializer.Deserialize<ProviderOAuthRefreshPayload>(json, options)

                if
                    String.IsNullOrWhiteSpace parsed.EntryLabel
                    || String.IsNullOrWhiteSpace parsed.FlowName
                then
                    None
                else
                    Some parsed
            with _ ->
                None

    /// The scope the payload names.
    let scope (payload: ProviderOAuthRefreshPayload) : StorageScope = {
        ScopeId = payload.ScopeId
        Container = payload.Container
        Persist = payload.Persist
    }

/// Build the `JobRegistration` for one connected entry. Idempotent by
/// construction: the `IdempotencyKey` is derived from
/// `(handler, scope, label)`, so re-connecting an entry reuses the
/// existing job rather than accumulating a second cron for the same
/// subject.
let registrationFor
    (scope: StorageScope)
    (flowName: string)
    (entryLabel: string)
    (createdBy: string)
    : JobRegistration =
    let payload: ProviderOAuthRefreshPayload = {
        ScopeId = scope.ScopeId
        Container = scope.Container
        Persist = scope.Persist
        EntryLabel = entryLabel
        FlowName = flowName
    }

    {
        ScopeId = scope.ScopeId
        Handler = HandlerName
        Payload = ProviderOAuthRefreshPayload.serialize payload
        Trigger = CronTrigger RefreshCronExpression
        Idempotency =
            Some {
                Key = $"{HandlerName}:{scope.Container}:{entryLabel}"
                TtlSeconds = 365 * 24 * 60 * 60
            }
        RetryPolicy = JobRetryPolicy.defaults
        // Affinity by scope: two entries in the same tenant refresh on
        // the same node under a sharded scheduler, which keeps their
        // `ISecretStore` writes off each other's toes. No ordering is
        // promised ACROSS scopes (GP 12 rule 5).
        ShardKey = Some scope.Container
        Precision = Minute
        CreatedBy = createdBy
        Tags = Map [ "substrate", "provider-oauth"; "flow", flowName ]
    }

/// `IJobHandler` for `_platform.providers.oauth.refresh`.
///
/// `flows` is the registered `IProviderOAuthFlow` set resolved from DI
/// at construction; the handler looks the payload's flow up by name on
/// every dispatch rather than closing over one, so a deployment
/// wiring three provider flows needs one handler registration.
type ProviderOAuthRefreshJobHandler
    (
        flows: IProviderOAuthFlow list,
        providerProfile: IProviderProfile,
        secretStore: ISecretStore,
        auditLog: IAuditLog option,
        logger: ILogger
    ) =

    interface IJobHandler with
        member _.Execute(ctx) = async {
            match ProviderOAuthRefreshPayload.tryDeserialize ctx.Payload with
            | None ->
                let msg = $"[ProviderOAuthRefresh] malformed payload for job %A{ctx.JobId}"
                logger.Warn msg
                return PermanentFailure msg
            | Some payload ->
                match flows |> List.tryFind (fun f -> f.Name = payload.FlowName) with
                | None ->
                    // The flow was unregistered (a companion package
                    // removed at compose). Permanent: no future
                    // dispatch can succeed either.
                    let msg =
                        $"[ProviderOAuthRefresh] no IProviderOAuthFlow named '{payload.FlowName}' is registered"

                    logger.Warn msg
                    return PermanentFailure msg
                | Some flow ->
                    let scope = ProviderOAuthRefreshPayload.scope payload
                    let! profile = providerProfile.Get scope

                    let entry =
                        profile
                        |> Option.bind (fun p -> p.Entries |> List.tryFind (fun e -> e.Label = payload.EntryLabel))

                    match entry with
                    | None ->
                        // Entry deleted (the user disconnected). This
                        // is the NORMAL end of a job's life, not a
                        // failure — returning `Success` avoids a
                        // dead-letter audit for a routine disconnect.
                        // The stale cron is cancelled by the
                        // disconnect path; a missed cancellation
                        // degrades to a five-minutely profile read.
                        logger.Debug
                            $"[ProviderOAuthRefresh] entry '{payload.EntryLabel}' no longer present in scope {scope.Container}; nothing to refresh"

                        return Success
                    | Some e ->
                        let! outcome =
                            ProviderOAuthConnect.refreshEntry
                                providerProfile
                                secretStore
                                auditLog
                                scope
                                flow
                                e
                                DateTime.UtcNow
                                ProviderOAuthConnect.DefaultLeadTime

                        match outcome with
                        | ProviderOAuthConnect.NotDue
                        | ProviderOAuthConnect.NotOAuthConnected -> return Success
                        | ProviderOAuthConnect.Refreshed expiry ->
                            logger.Info
                                $"[ProviderOAuthRefresh] refreshed '{payload.EntryLabel}' flow={flow.Name} scope={scope.Container} newExpiry={expiry:o}"

                            return Success
                        | ProviderOAuthConnect.NeedsReauthorization reason ->
                            // Terminal for this entry: retrying cannot
                            // recover a revoked grant, and the health
                            // write + audit have already told the user.
                            logger.Warn
                                $"[ProviderOAuthRefresh] '{payload.EntryLabel}' needs reauthorization flow={flow.Name} scope={scope.Container}: {reason}"

                            return PermanentFailure reason
                        | ProviderOAuthConnect.TransientFailure reason ->
                            logger.Warn
                                $"[ProviderOAuthRefresh] transient failure on '{payload.EntryLabel}' flow={flow.Name} scope={scope.Container}: {reason}"

                            return TransientFailure reason
        }

/// Factory used by the compose-time wiring.
let create
    (flows: IProviderOAuthFlow list)
    (providerProfile: IProviderProfile)
    (secretStore: ISecretStore)
    (auditLog: IAuditLog option)
    (logger: ILogger)
    : IJobHandler =
    ProviderOAuthRefreshJobHandler(flows, providerProfile, secretStore, auditLog, logger) :> IJobHandler