module ToolUp.Platform.DataIngestionApiHandler

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement

// ─── Phase 10h — OAuth refresh audit payload decoding ──────────────
//
// `IAuditLog` writes every `AuditEvent` (incl. the four
// `_platform.oauth.refresh` cases) through `IEventStore` under
// `SourceModule = AuditSourceModule.value`, and exposes only an
// `AuditEvent list` to readers — which loses each row's `OccurredAt`.
// `GetTokenStatus` needs both the decoded payload (for `Provider` /
// `ConfigId` filtering + the outcome shape) AND the wall-clock
// timestamp (for the column's "last seen at" rendering), so we read
// the underlying `ModuleEvent`s directly via `IEventStore.ReadBySource`
// and decode the four known OAuth-refresh event types here.
// `FableJsonConverter` matches `AuditLog.fs`'s emission settings, so
// payloads round-trip without re-deriving the wire shape.

let private oauthRefreshJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private decodeOAuthRefreshPayload<'T> (json: string) : 'T option =
    try
        Some(JsonConvert.DeserializeObject<'T>(json, oauthRefreshJsonSettings))
    with _ ->
        None

// ─── IDataIngestionApi handler factory ───────────────────────────
//
// Builds the `IDataIngestionApi` Fable.Remoting handler. Resolves
// `IDataSourceConfigStore`, `IDataIngestor`, optional `IJobScheduler`
// (for triggered refreshes), `AccessContext`, and (in Team /
// MultiTeam mode) `ITeamStore` lazily from DI per request. Same
// pattern as `JobApiHandler.jobApi`, `ConfigHandler.configApi`,
// `AISettingsHandler`.
//
// **Scope discipline.** Every method validates that the caller's
// resolved `AccessContext` produces a `configScope`. `Anonymous`
// callers get a clear error rather than a no-op — data ingestion
// requires a persistent scope to be useful.
//
// **Write gating.** `SaveDataSource` / `DeleteDataSource` /
// `TriggerRefresh` require Owner / Admin in `Team` / `MultiTeam`
// mode (via `TeamRoles.canWriteTeamConfig`). Read paths
// (`ListDataSources`, `GetDataSource`, `ListRecentRuns`) are
// ungated within the caller's scope — any team member can inspect
// the team's data sources.

let dataIngestionApi (ctx: HttpContext) : IDataIngestionApi =

    let configStore =
        match ctx.RequestServices.GetService(typeof<IDataSourceConfigStore>) with
        | :? IDataSourceConfigStore as s -> Some s
        | _ -> None

    let ingestor =
        match ctx.RequestServices.GetService(typeof<IDataIngestor>) with
        | :? IDataIngestor as i -> Some i
        | _ -> None

    let scheduler =
        match ctx.RequestServices.GetService(typeof<IJobScheduler>) with
        | :? IJobScheduler as s -> Some s
        | _ -> None

    // Phase 10h — token-status read path. Both are optional: deployments
    // without `OAuthRefresher = EnabledOAuthRefresher` will not register
    // an `IOAuthTokenRefresher`, and deployments running `AuditLog =
    // NoAuditLog` will register `NoOpAuditLog` (no event store reads to
    // do). Either missing collapses `GetTokenStatus` to `None`.
    let tokenRefresher =
        match ctx.RequestServices.GetService(typeof<IOAuthTokenRefresher>) with
        | :? IOAuthTokenRefresher as r -> Some r
        | _ -> None

    let eventStore =
        match ctx.RequestServices.GetService(typeof<IEventStore>) with
        | :? IEventStore as s -> Some s
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests bypassing the middleware. Mirrors
            // ConfigHandler.configApi's fallback.
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let scopeOpt = AccessContext.configScope accessContext

    // Owner/Admin write gate — same shape as ConfigHandler.ensureWriteAllowed
    // and JobApiHandler.ensureWriteAllowed.
    let ensureWriteAllowed () : Async<Result<unit, string>> = async {
        match accessContext.Subject with
        | TeamMember(userId, teamId) ->
            match ctx.RequestServices.GetService(typeof<ITeamStore>) with
            | :? ITeamStore as ts ->
                let! role = ts.GetMemberRole(teamId, userId)

                match role with
                | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
                | Some r ->
                    return
                        Error
                            $"Only team owners and admins can manage data sources. Your role: {TeamRoles.displayName r}."
                | None -> return Error "You are not a member of this team."
            | _ -> return Error "Team management is not available in this deployment."
        | _ -> return Ok()
    }

    let withWriteScope (f: IDataSourceConfigStore -> string -> Async<Result<'T, string>>) = async {
        match configStore, scopeOpt with
        | None, _ -> return Error "Data ingestion is not enabled in this deployment."
        | _, None -> return Error "Data ingestion requires a persistent scope (sign in or join a team)."
        | Some s, Some scope ->
            let! rbac = ensureWriteAllowed ()

            match rbac with
            | Error msg -> return Error msg
            | Ok() -> return! f s scope.ScopeId
    }

    {
        ListDataSources =
            fun () -> async {
                match configStore, scopeOpt with
                | Some s, Some scope -> return! s.List scope.ScopeId
                | _ -> return []
            }

        GetDataSource =
            fun sourceId -> async {
                match configStore, scopeOpt with
                | Some s, Some scope -> return! s.Get(scope.ScopeId, sourceId)
                | _ -> return None
            }

        SaveDataSource =
            fun config ->
                withWriteScope (fun s scopeId -> async {
                    // Reject mismatched ids — admin UI sends the
                    // source's intended id; the handler enforces
                    // it matches what the scope sees.
                    do! s.Save(scopeId, config)
                    return Ok()
                })

        DeleteDataSource =
            fun sourceId ->
                withWriteScope (fun s scopeId -> async {
                    do! s.Delete(scopeId, sourceId)
                    return Ok()
                })

        TriggerRefresh =
            fun (sourceId, table) -> async {
                match scheduler, scopeOpt with
                | None, _ ->
                    return
                        Error
                            "TriggerRefresh requires the background-job scheduler to be enabled (ServerConfig.JobScheduler = InProcessJobScheduler)."
                | _, None -> return Error "Data ingestion requires a persistent scope (sign in or join a team)."
                | Some s, Some scope ->
                    let! rbac = ensureWriteAllowed ()

                    match rbac with
                    | Error msg -> return Error msg
                    | Ok() ->
                        let registration: JobRegistration = {
                            ScopeId = scope.ScopeId
                            Handler = DataIngestionJobHandler.JobHandlerName
                            Payload = DataIngestionJobHandler.payloadFor sourceId table
                            Trigger = Manual
                            Idempotency = None
                            RetryPolicy = JobRetryPolicy.defaults
                            ShardKey = Some sourceId
                            Precision = Minute
                            CreatedBy = accessContext.UserId
                            Tags = Map.ofList [ "source-id", sourceId; "table", table ]
                        }

                        match! s.Schedule registration with
                        | Ok jobId ->
                            // The scheduler treats Manual triggers as
                            // dispatch-on-TriggerOnce; immediately fire it
                            // so admin-UI "Refresh now" actually runs the
                            // ingestion within seconds rather than at the
                            // next minute tick.
                            match! s.TriggerOnce(scope.ScopeId, jobId, accessContext.UserId) with
                            | Ok() -> return Ok jobId
                            | Error msg -> return Error msg
                        | Error err -> return Error(sprintf "%A" err)
            }

        ListRecentRuns =
            fun (sourceId, count) -> async {
                match ingestor, scopeOpt with
                | Some i, Some scope -> return! i.GetRecentRuns(scope.ScopeId, sourceId, count)
                | _ -> return []
            }

        // ─── Phase 10b — OAuth credential lifecycle ─────────────────

        BeginOAuth =
            fun (sourceId, flowName) ->
                withWriteScope (fun s scopeId -> async {
                    // Look-before-you-leap validation. /authorize
                    // re-checks RBAC + flow + config, but doing it
                    // here lets the admin UI surface a synchronous
                    // error before redirecting through the upstream
                    // provider — much better UX than a 403 returned
                    // from a 302 chain.
                    let! configOpt = s.Get(scopeId, sourceId)

                    match configOpt with
                    | None -> return Error $"Data source '{sourceId}' not found"
                    | Some _ ->
                        match OAuthFlowHandler.findFlow ctx.RequestServices flowName with
                        | None -> return Error $"OAuth flow '{flowName}' is not registered"
                        | Some _ ->
                            // Construct the absolute URL the client
                            // will navigate to. Relative path is fine
                            // — the browser resolves against the
                            // current origin (which equals the
                            // /authorize endpoint's origin).
                            let url =
                                sprintf
                                    "/api/oauth/%s/authorize?dataSourceId=%s"
                                    flowName
                                    (Uri.EscapeDataString sourceId)

                            return Ok url
                })

        GetCredentialStatus =
            fun sourceId -> async {
                match configStore, scopeOpt with
                | None, _
                | _, None -> return NotConfigured
                | Some store, Some scope ->
                    let! configOpt = store.Get(scope.ScopeId, sourceId)

                    match configOpt with
                    | None -> return NotConfigured
                    | Some _ ->
                        match ctx.RequestServices.GetService(typeof<IBlobStorage>) with
                        | :? IBlobStorage as blob ->
                            let! metaOpt = OAuthFlowHandler.loadCredentialMetadata blob scope.ScopeId sourceId

                            match metaOpt with
                            | None -> return NeedsAuthorization
                            | Some meta ->
                                match meta.LastError with
                                | Some reason -> return NeedsReauthorization reason
                                | None -> return Connected meta.ConnectedAt
                        | _ ->
                            // Blob storage not registered —
                            // deployment misconfiguration.
                            // Conservatively report needs-auth.
                            return NeedsAuthorization
            }

        Disconnect =
            fun sourceId ->
                withWriteScope (fun store scopeId -> async {
                    let blob =
                        match ctx.RequestServices.GetService(typeof<IBlobStorage>) with
                        | :? IBlobStorage as b -> Some b
                        | _ -> None

                    let secretStore =
                        match ctx.RequestServices.GetService(typeof<ISecretStore>) with
                        | :? ISecretStore as s -> Some s
                        | _ -> None

                    match blob, secretStore with
                    | None, _ -> return Error "Blob storage is not registered."
                    | _, None -> return Error "Secret store is not registered."
                    | Some storage, Some secrets ->
                        // Resolve the data source's scope container
                        // for the secret-store key. We need it for
                        // both the secret read (revocation) and
                        // delete (cleanup).
                        let containerForScope =
                            scopeOpt |> Option.map _.Container |> Option.defaultValue scopeId

                        let! metaOpt = OAuthFlowHandler.loadCredentialMetadata storage scopeId sourceId

                        match metaOpt with
                        | None ->
                            // Idempotent — Disconnect on a never-
                            // connected source is a no-op. The
                            // admin UI surfaces "already
                            // disconnected" rather than an error.
                            return Ok()
                        | Some meta ->
                            // Best-effort upstream revocation.
                            // Errors logged but not propagated —
                            // the local secret deletion is what
                            // makes the credential unusable.
                            let secretKey = sprintf "%s-refresh-%s" meta.FlowName sourceId
                            let! refreshOpt = secrets.GetSecret(containerForScope, secretKey)

                            let! upstreamRevoked =
                                match refreshOpt, OAuthFlowHandler.findFlow ctx.RequestServices meta.FlowName with
                                | Some refresh, Some flow -> async {
                                    let flowCtx: OAuthFlowContext = {
                                        ScopeId = containerForScope
                                        DataSourceId = sourceId
                                        Config = None
                                    }

                                    let! result = flow.Revoke(flowCtx, refresh)

                                    return
                                        match result with
                                        | Ok() -> true
                                        | Error _ -> false
                                  }
                                | _ -> async { return false }

                            // Local cleanup is unconditional. The
                            // user clicked Disconnect; their intent
                            // is unambiguous regardless of whether
                            // the upstream revoke succeeded.
                            let! _ = secrets.DeleteSecret(containerForScope, secretKey)
                            let! _ = OAuthFlowHandler.deleteCredentialMetadata storage scopeId sourceId

                            // Emit OAuthDisconnected audit (fire-
                            // and-forget). UpstreamRevoked records
                            // best-effort revocation outcome.
                            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                            | :? IAuditLog as auditLog ->
                                auditLog.Record(
                                    scopeId,
                                    OAuthDisconnected {
                                        UserId = accessContext.UserId
                                        ScopeId = scopeId
                                        FlowName = meta.FlowName
                                        DataSourceId = sourceId
                                        UpstreamRevoked = upstreamRevoked
                                    }
                                )
                                |> Async.Start
                            | _ -> ()

                            // Suppress unused-binding warning when
                            // the store argument isn't otherwise
                            // touched in this branch.
                            store |> ignore
                            return Ok()
                })

        // ─── Phase 10h — token-status read path ─────────────────────

        GetTokenStatus =
            fun sourceId -> async {
                match scopeOpt, tokenRefresher with
                | None, _
                | _, None ->
                    // No scope (Anonymous) or no `IOAuthTokenRefresher`
                    // registered — the column renders "—" downstream.
                    return None
                | Some scope, Some refresher ->
                    // Identity-by-value (GP 12 rule 1): descriptor
                    // lookup keys on `(Provider, ConfigId)` strings.
                    // `ListDescriptors` enumerates across every scope;
                    // we filter to the caller's scope + the requested
                    // `sourceId` to honour `ConfigId = DataSourceId`,
                    // the registration convention documented on
                    // `OAuthRefreshDescriptor.ConfigId`.
                    let! descriptors = refresher.ListDescriptors()

                    let descriptor =
                        descriptors
                        |> List.tryFind (fun d -> d.ScopeId = scope.ScopeId && d.ConfigId = sourceId)

                    match descriptor with
                    | None -> return None
                    | Some d ->
                        // Read the audit envelopes directly so the
                        // `OccurredAt` timestamps are preserved (the
                        // `IAuditLog.GetAuditTrail` surface returns
                        // only the decoded `AuditEvent`s and drops the
                        // envelope). Returns an empty list when no
                        // `IEventStore` is wired (`NoAuditLog` mode).
                        let! envelopes =
                            match eventStore with
                            | Some es -> es.ReadBySource(scope.ScopeId, AuditSourceModule.value)
                            | None -> async { return [] }

                        // For each terminal `_platform.oauth.refresh`
                        // case matching this descriptor, project to
                        // `(OccurredAt, OAuthRefreshOutcome,
                        //  newExpiry option)`. The `newExpiry` branch
                        // is preserved only for `RefreshedOutcome` so
                        // we can compute `NextScheduledRefresh` below
                        // from the latest successful refresh.
                        let matchesDescriptor (provider: string) (configId: string) =
                            provider = d.Provider && configId = sourceId

                        let projected =
                            envelopes
                            |> List.choose (fun evt ->
                                match evt.EventType with
                                | "OAuthTokenRefreshed" ->
                                    decodeOAuthRefreshPayload<OAuthTokenRefreshedPayload> evt.Payload
                                    |> Option.bind (fun p ->
                                        if matchesDescriptor p.Provider p.ConfigId then
                                            Some(evt.OccurredAt, RefreshedOutcome p.NewExpiry, Some p.NewExpiry)
                                        else
                                            None)
                                | "OAuthTokenRefreshFailed" ->
                                    decodeOAuthRefreshPayload<OAuthTokenRefreshFailedPayload> evt.Payload
                                    |> Option.bind (fun p ->
                                        if matchesDescriptor p.Provider p.ConfigId then
                                            Some(evt.OccurredAt, TransientErrorOutcome p.Reason, None)
                                        else
                                            None)
                                | "OAuthRefreshTokenInvalidated" ->
                                    decodeOAuthRefreshPayload<OAuthRefreshTokenInvalidatedPayload> evt.Payload
                                    |> Option.bind (fun p ->
                                        if matchesDescriptor p.Provider p.ConfigId then
                                            Some(evt.OccurredAt, RequiresReauthOutcome p.Reason, None)
                                        else
                                            None)
                                | "OAuthRefreshDeadLettered" ->
                                    decodeOAuthRefreshPayload<OAuthRefreshDeadLetteredPayload> evt.Payload
                                    |> Option.bind (fun p ->
                                        if matchesDescriptor p.Provider p.ConfigId then
                                            Some(evt.OccurredAt, DeadLetteredOutcome p.FinalReason, None)
                                        else
                                            None)
                                | _ -> None)

                        // Most recent terminal outcome — sort by
                        // `OccurredAt` because `IEventStore.ReadBySource`
                        // does not guarantee order (its own docstring,
                        // `SDK.Shared.fs:50–51`).
                        let latest =
                            projected
                            |> List.sortByDescending (fun (occurredAt, _, _) -> occurredAt)
                            |> List.tryHead

                        // Most recent SUCCESSFUL refresh — drives
                        // `NextScheduledRefresh = NewExpiry -
                        // ScheduleAheadOfExpiry`. Distinct from
                        // `latest` because a transient failure can
                        // follow a successful refresh; the next-
                        // scheduled time is still anchored to the
                        // last good expiry.
                        let latestSuccess =
                            projected
                            |> List.choose (fun (occurredAt, _, newExpiryOpt) ->
                                newExpiryOpt |> Option.map (fun expiry -> occurredAt, expiry))
                            |> List.sortByDescending fst
                            |> List.tryHead

                        let lastOutcome = latest |> Option.map (fun (_, outcome, _) -> outcome)
                        let lastOutcomeAt = latest |> Option.map (fun (occurredAt, _, _) -> occurredAt)

                        let nextScheduledRefresh =
                            latestSuccess
                            |> Option.map (fun (_, expiry) -> expiry - d.ScheduleAheadOfExpiry)

                        return
                            Some {
                                Provider = d.Provider
                                LastOutcome = lastOutcome
                                LastOutcomeAt = lastOutcomeAt
                                NextScheduledRefresh = nextScheduledRefresh
                            }
            }
    }