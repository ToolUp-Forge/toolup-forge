module ToolUp.Platform.OAuthFlowHandler

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement

// ─── Phase 10b — OAuth Authorization Code endpoints ─────────────────
//
// Two HTTP handlers backing the substrate's `/api/oauth/{flowName}/*`
// surface:
//
//   GET /api/oauth/{flowName}/authorize?dataSourceId={id}
//   GET /api/oauth/{flowName}/callback?code=...&state=...
//
// `/authorize` resolves the caller's `AccessContext`, RBAC-gates
// (Owner / Admin in `Team` / `MultiTeam` mode), looks up the data-
// source config, generates a CSRF state token + PKCE code verifier,
// pins the redirect URI in the state-store entry, calls
// `IOAuthCredentialFlow.BuildAuthorizeUrl`, and 302-redirects the
// user-agent to the upstream provider.
//
// `/callback` atomically consumes the state entry (single-use),
// validates the flow + actor identity, exchanges the code for
// credentials, persists the refresh token via `ISecretStore`, writes
// a small `CredentialMetadata` blob for `GetCredentialStatus` (A.4),
// emits an `OAuthConnected` audit event, and 302-redirects back to
// the admin UI.
//
// **Routes are not yet mounted.** A.8 wires `routes` into
// `SDK.Server.fs`'s router. Until then the handler module is a
// dead surface — adding it here keeps the diff small enough to
// review independently of compose-side wiring.
//
// **Phase 9c portability.** The handler is stateless across calls;
// every input arrives through `HttpContext` and DI. The state store
// is the only stateful element, already flagged single-instance +
// distributed-companion candidate. No new violations introduced.

// ─── Internal helpers ───────────────────────────────────────────────

[<Literal>]
let private RedirectBaseEnvVar = ConfigKeys.Names.oauthRedirectBase

[<Literal>]
let private PlatformContainer = "_platform"

/// Compute the base URL for the deployment from the active request.
/// Honours `TrustForwardedHeaders` because the `UseForwardedHeaders`
/// middleware (registered upstream of routing in `SDK.Server.fs`)
/// has already rewritten `Request.Scheme` to honour
/// `X-Forwarded-Proto` from a TLS-terminating proxy.
let private redirectBaseFromRequest (ctx: HttpContext) : string =
    sprintf "%s://%s" ctx.Request.Scheme (string ctx.Request.Host)

/// Resolve the redirect-base URL for /authorize and /callback.
/// `TOOLUP_OAUTH_REDIRECT_BASE` env var takes precedence; falls back
/// to the request's resolved scheme + host. Trailing `/` trimmed so
/// concatenation is safe.
let private resolveRedirectBase (ctx: HttpContext) : string =
    // Phase 698 — through the Phase-696 `ConfigResolution` seam.
    let envValue = ConfigResolution.tryValue RedirectBaseEnvVar

    let raw = envValue |> Option.defaultWith (fun () -> redirectBaseFromRequest ctx)
    raw.TrimEnd('/')

/// Compute the absolute redirect URI passed to the upstream provider.
/// Deterministic across `/authorize` and `/callback` for the same
/// deployment + same trusted forwarded-headers settings — the state-
/// store entry pins this value so /callback uses the byte-identical
/// string regardless. Google validates exact-match on the token
/// endpoint.
let private redirectUriFor (ctx: HttpContext) (flowName: string) : string =
    let baseUrl = resolveRedirectBase ctx
    sprintf "%s/api/oauth/%s/callback" baseUrl flowName

/// Resolve `AccessContext` from DI; fall back to `HttpContext.Items`
/// for tests bypassing the standard middleware. Mirrors the pattern
/// in `DataIngestionApiHandler.fs`.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        let teamId =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
            | _ -> None

        AccessContext.unrestricted (AnonymousSession userId)

/// Resolve a service from DI; `None` when the service isn't
/// registered.
let private tryGetService<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

/// Locate the `IOAuthCredentialFlow` implementation matching the URL
/// path's `{flowName}` segment. Multiple flows can be registered;
/// `Name` is the dispatch discriminator.
let private tryFindFlow (ctx: HttpContext) (flowName: string) : IOAuthCredentialFlow option =
    ctx.RequestServices.GetServices(typeof<IOAuthCredentialFlow>)
    |> Seq.cast<IOAuthCredentialFlow>
    |> Seq.tryFind (fun f -> f.Name = flowName)

/// Owner / Admin gate mirroring `DataIngestionApiHandler.ensureWriteAllowed`.
/// In `Team` / `MultiTeam` mode looks up the caller's role via
/// `ITeamStore.GetMemberRole`. Other modes pass through unconditionally
/// (Anonymous is rejected upstream by the configScope check).
let private ensureOwnerAdmin (ctx: HttpContext) (accessContext: AccessContext) : Async<Result<unit, string>> = async {
    match accessContext.Subject with
    | TeamMember(userId, teamId) ->
        match tryGetService<ITeamStore> ctx with
        | Some ts ->
            let! role = ts.GetMemberRole(teamId, userId)

            match role with
            | Some r when TeamRoles.canWriteTeamConfig r -> return Ok()
            | Some r ->
                return
                    Error $"Only team owners and admins can initiate OAuth flows. Your role: {TeamRoles.displayName r}."
            | None -> return Error "You are not a member of this team."
        | None -> return Error "Team management is not available in this deployment."
    | _ -> return Ok()
}

/// Map a typed `OAuthError` to an HTTP status code. Matches the
/// substrate-handler comment block in `IOAuthCredentialFlow.fs`.
let private httpStatusForOAuthError (err: OAuthError) : int =
    match err with
    | StateMismatch _ -> 400
    | ProviderRejected _ -> 502
    | NetworkError _ -> 503
    | ClientCredentialMissing _
    | RevocationUnsupported
    | OAuthFlowFailed _ -> 500

/// Write a status code + plain-text body and return the next-handler
/// continuation. Wraps the common Giraffe shape so each error path
/// stays one line.
let private respondText (ctx: HttpContext) (status: int) (msg: string) = task {
    ctx.Response.StatusCode <- status
    do! ctx.WriteTextAsync msg :> Task
    return Some ctx
}

/// 302-redirect to an absolute or relative URL. Sets `Location`
/// + status code; no body. Used by both `/authorize` (provider URL)
/// and `/callback` (admin-UI return path).
let private respondRedirect (ctx: HttpContext) (url: string) = task {
    ctx.Response.StatusCode <- 302
    ctx.Response.Headers["Location"] <- Microsoft.Extensions.Primitives.StringValues url
    return Some ctx
}

// ─── Credential metadata blob ───────────────────────────────────────
//
// Tiny per-data-source record persisted on /callback success. Read
// by `IDataIngestionApi.GetCredentialStatus` (A.4) to project a
// `CredentialStatus` for admin-UI rendering. Path layout mirrors
// `DataSourceConfigStore`'s `_platform/data-sources/{scopeId}/...`
// convention so operators see one consistent shape under `_platform/`.

/// Per-data-source credential metadata. Tracks which OAuth flow
/// minted the current credentials and when the last successful
/// connection completed. `LastError` carries the most recent
/// `ExchangeCode` failure diagnostic so the admin UI can show a
/// "Reconnect required" banner without a dedicated audit-event read.
type CredentialMetadata = {
    FlowName: string
    DataSourceId: string
    ConnectedAt: DateTime
    LastError: string option
}

module private MetadataJson =
    let private options = FableConverters.create ()

    let serialize (value: CredentialMetadata) : byte[] =
        JsonSerializer.Serialize(value, options) |> Encoding.UTF8.GetBytes

    let tryDeserialize (bytes: byte[]) : CredentialMetadata option =
        try
            let json = Encoding.UTF8.GetString bytes
            Some(JsonSerializer.Deserialize<CredentialMetadata>(json, options))
        with _ ->
            None

let private credentialBlobName (scopeId: string) (dataSourceId: DataSourceId) =
    $"data-sources/{scopeId}/credentials/{dataSourceId}.json"

/// Persist a `CredentialMetadata` record under
/// `_platform/data-sources/{scopeId}/credentials/{dataSourceId}.json`.
/// Best-effort — failure logs but does not block the OAuth flow's
/// happy path (the secret store carries the load-bearing state; the
/// metadata blob is a UI projection).
let saveCredentialMetadata
    (storage: IBlobStorage)
    (scopeId: string)
    (dataSourceId: DataSourceId)
    (metadata: CredentialMetadata)
    : Async<Result<unit, string>> =
    async {
        let bytes = MetadataJson.serialize metadata
        let! result = storage.Upload(PlatformContainer, credentialBlobName scopeId dataSourceId, bytes)

        match result with
        | Ok _ -> return Ok()
        | Error e -> return Error e
    }

/// Read the `CredentialMetadata` for a data source, or `None` when
/// no metadata blob exists yet (e.g. credentials never minted, or
/// `Disconnect` cleared the blob).
let loadCredentialMetadata
    (storage: IBlobStorage)
    (scopeId: string)
    (dataSourceId: DataSourceId)
    : Async<CredentialMetadata option> =
    async {
        let! result = storage.Download(PlatformContainer, credentialBlobName scopeId dataSourceId)

        match result with
        | Ok bytes -> return MetadataJson.tryDeserialize bytes
        | Error _ -> return None
    }

/// Remove the credential metadata blob for a data source. Idempotent
/// — `IBlobStorage.Delete` returns `Ok` on a missing blob in every
/// shipped implementation. Used by `Disconnect` to drop a data
/// source back to `NeedsAuthorization` status.
let deleteCredentialMetadata
    (storage: IBlobStorage)
    (scopeId: string)
    (dataSourceId: DataSourceId)
    : Async<Result<unit, string>> =
    async {
        let! result = storage.Delete(PlatformContainer, credentialBlobName scopeId dataSourceId)
        return result
    }

/// Find an `IOAuthCredentialFlow` registered under the given name.
/// Exposed for `IDataIngestionApi.Disconnect` and other server-side
/// callers that need flow lookup by string name.
let findFlow (services: System.IServiceProvider) (flowName: string) : IOAuthCredentialFlow option =
    services.GetServices(typeof<IOAuthCredentialFlow>)
    |> Seq.cast<IOAuthCredentialFlow>
    |> Seq.tryFind (fun f -> f.Name = flowName)

// ─── Phase 43.B — correlation resolution ────────────────────────────
//
// `/authorize` used to take one query parameter, `dataSourceId`, and
// that parameter WAS the correlation. Since 43.B the subject of a
// round-trip is an `OAuthCorrelationKey`, and the endpoint accepts
// either shape:
//
//   ?dataSourceId={id}      → the Phase 10e data-source path,
//                             byte-for-byte unchanged.
//   ?providerEntry={label}  → the 43.B provider-profile path, which
//                             mints an `OAuthConnected` `ProviderEntry`
//                             on callback instead of a data-source
//                             credential blob.
//
// `dataSourceId` is checked FIRST so a request carrying both behaves
// exactly as it did before this phase.

let private queryValueOf (ctx: HttpContext) (key: string) : string option =
    match ctx.Request.Query.TryGetValue key with
    | true, values when values.Count > 0 ->
        let v = string values[0]
        if String.IsNullOrEmpty v then None else Some v
    | _ -> None

let private resolveCorrelation (ctx: HttpContext) : OAuthCorrelationKey option =
    match queryValueOf ctx "dataSourceId" with
    | Some id -> Some(OAuthCorrelationKey.dataSource id)
    | None -> queryValueOf ctx "providerEntry" |> Option.map OAuthCorrelationKey.providerEntry

// ─── /authorize handler ─────────────────────────────────────────────

let private authorize (flowName: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        // Step 1 — required query parameter (either correlation shape).
        match resolveCorrelation ctx with
        | None ->
            return!
                respondText
                    ctx
                    400
                    "dataSourceId (data-source connect) or providerEntry (provider-profile connect) query parameter required"
        | Some correlation when not (OAuthCorrelationKey.isWellFormed correlation) ->
            return! respondText ctx 400 "correlation identifier must be non-empty and must not contain ':'"
        | Some correlation ->
            let dataSourceId = correlation.Id

            // Step 2 — resolve scope. Anonymous mode (or any path
            // that produced `configScope = None`) is rejected with
            // a clear message rather than a silent no-op.
            let accessContext = resolveAccessContext ctx

            match AccessContext.configScope accessContext with
            | None -> return! respondText ctx 400 "OAuth flow requires a persistent scope (sign in or join a team)."
            | Some scope ->

                // Step 3 — RBAC gate (Owner / Admin in Team modes).
                let! rbac = ensureOwnerAdmin ctx accessContext

                match rbac with
                | Error msg -> return! respondText ctx 403 msg
                | Ok() ->

                    // Step 4 — flow lookup.
                    match tryFindFlow ctx flowName with
                    | None -> return! respondText ctx 404 $"OAuth flow '{flowName}' is not registered"
                    | Some flow ->

                        // Step 5 — per-family preflight. A data-source
                        // connect must resolve a persisted
                        // `DataSourceConfig` (unchanged from Phase
                        // 10e); a provider-profile connect instead
                        // requires the flow to declare itself an
                        // `IProviderOAuthFlow` and the deployment to
                        // have an `IProviderProfile` — no
                        // provider-profile substrate means the route
                        // is simply not available (GP 13).
                        let! preflight = async {
                            if OAuthCorrelationKey.isProviderEntry correlation then
                                match box flow with
                                | :? IProviderOAuthFlow ->
                                    match tryGetService<Providers.IProviderProfile> ctx with
                                    | None ->
                                        return
                                            Error(
                                                404,
                                                "Provider-profile connect is not enabled in this deployment (no IProviderProfile is registered)."
                                            )
                                    | Some _ -> return Ok None
                                | _ ->
                                    return
                                        Error(
                                            400,
                                            $"OAuth flow '{flowName}' does not support provider-profile connect (it is not an IProviderOAuthFlow)."
                                        )
                            else
                                match tryGetService<IDataSourceConfigStore> ctx with
                                | None -> return Error(500, "Data ingestion is not enabled in this deployment.")
                                | Some configStore ->
                                    let! configOpt = configStore.Get(scope.ScopeId, dataSourceId)

                                    match configOpt with
                                    | None -> return Error(404, $"Data source '{dataSourceId}' not found")
                                    | Some config -> return Ok(Some config)
                        }

                        match preflight with
                        | Error(status, msg) -> return! respondText ctx status msg
                        | Ok config ->

                            // Step 6 — generate state + PKCE
                            // verifier. Verifier is stamped
                            // into the state-store entry; the
                            // SHA-256 challenge derived from it
                            // rides the authorize URL (Step 8)
                            // and the verifier itself is replayed
                            // into ExchangeCode on /callback when
                            // the flow declares `SupportsPkce`.
                            let state = OAuthCrypto.generateState ()
                            let verifier = OAuthCrypto.generateCodeVerifier ()
                            let redirectUri = redirectUriFor ctx flowName

                            // Step 7 — persist state entry.
                            let entry: OAuthFlowState = {
                                Token = state
                                FlowName = flowName
                                DataSourceId = dataSourceId
                                ScopeId = scope.ScopeId
                                Container = scope.Container
                                UserId = accessContext.UserId
                                CreatedAt = DateTime.UtcNow
                                RedirectUri = redirectUri
                                CodeVerifier = Some verifier
                                Correlation = Some correlation
                            }

                            match tryGetService<IOAuthStateStore> ctx with
                            | None ->
                                return!
                                    respondText
                                        ctx
                                        500
                                        "OAuth state store is not registered (compose-time wiring missing)."
                            | Some stateStore ->
                                let! saveResult = stateStore.Save entry

                                match saveResult with
                                | Error msg ->
                                    return! respondText ctx 500 (sprintf "Failed to persist OAuth state: %s" msg)
                                | Ok() ->

                                    // Step 8 — provider URL. When the
                                    // flow supports PKCE, derive the
                                    // S256 challenge from the stashed
                                    // verifier and hand it over; a
                                    // non-PKCE flow gets `None` and is
                                    // byte-for-byte unchanged (GP 11).
                                    let flowCtx: OAuthFlowContext = {
                                        ScopeId = scope.Container
                                        DataSourceId = dataSourceId
                                        Correlation = correlation
                                        Config = config
                                    }

                                    let pkceChallenge =
                                        if flow.SupportsPkce then
                                            Some {
                                                Challenge = OAuthCrypto.codeChallengeFromVerifier verifier
                                                Method = "S256"
                                            }
                                        else
                                            None

                                    let! urlResult = flow.BuildAuthorizeUrl(flowCtx, state, redirectUri, pkceChallenge)

                                    match urlResult with
                                    | Error err ->
                                        return! respondText ctx (httpStatusForOAuthError err) (OAuthError.toMessage err)
                                    | Ok url -> return! respondRedirect ctx url
    }

/// PKCE-aware code exchange, shared by both correlation families so
/// the fail-closed rule (a `SupportsPkce` flow with no stashed
/// verifier must NEVER exchange) cannot be implemented twice and drift.
let private exchangeCode
    (flow: IOAuthCredentialFlow)
    (flowCtx: OAuthFlowContext)
    (entry: OAuthFlowState)
    (code: string)
    : Async<Result<OAuthCredentials, OAuthError>> =
    async {
        match flow.SupportsPkce, entry.CodeVerifier with
        | true, None ->
            return
                Error(
                    OAuthError.OAuthFlowFailed
                        "PKCE verifier missing from the state entry; this flow requires PKCE and the code cannot be exchanged without it."
                )
        | true, (Some _ as verifier) -> return! flow.ExchangeCode(flowCtx, code, entry.RedirectUri, verifier)
        | false, _ -> return! flow.ExchangeCode(flowCtx, code, entry.RedirectUri, None)
    }

// ─── Phase 43.B — provider-profile callback tail ────────────────────
//
// The `provider-entry` half of `/callback`. Everything up to and
// including the single-use state consume, the flow-name check and the
// actor-identity check is shared with the data-source path — those are
// the security-bearing steps and duplicating them would be how one of
// them quietly loses a check.
//
// What differs after that is only the DESTINATION of the credentials:
// a `ProviderEntry` with `Origin = OAuthConnected` and an
// `OAuthBinding`, instead of a data-source credential-metadata blob.

let private providerCallbackTail
    (ctx: HttpContext)
    (flowName: string)
    (flow: IOAuthCredentialFlow)
    (entry: OAuthFlowState)
    (correlation: OAuthCorrelationKey)
    (code: string)
    =
    task {
        let logger =
            tryGetService<ILogger> ctx
            |> Option.defaultWith (fun () ->
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                })

        let label = correlation.Id

        let failTo (reason: string) =
            respondRedirect ctx $"/?providerError={Uri.EscapeDataString label}&reason={Uri.EscapeDataString reason}"

        match box flow with
        | :? IProviderOAuthFlow as providerFlow ->
            match tryGetService<Providers.IProviderProfile> ctx, tryGetService<ISecretStore> ctx with
            | None, _ ->
                return!
                    respondText
                        ctx
                        404
                        "Provider-profile connect is not enabled in this deployment (no IProviderProfile is registered)."
            | _, None -> return! respondText ctx 500 "Secret store is not registered (deployment misconfiguration)."
            | Some providerProfile, Some secretStore ->
                let flowCtx = OAuthFlowContext.forCorrelation entry.Container correlation
                let! exchanged = exchangeCode flow flowCtx entry code

                match exchanged with
                | Error err ->
                    let message = OAuthError.toMessage err

                    logger.Warn
                        $"OAuth callback: provider ExchangeCode failed flow={flowName} entry={label} scope={entry.ScopeId}: {message}"

                    return! failTo message
                | Ok credentials ->
                    // `Persist = true` is correct by construction here:
                    // `/authorize` refuses the whole flow when
                    // `AccessContext.configScope` yields None, which is
                    // exactly the non-persistent case.
                    let scope: StorageScope = {
                        ScopeId = entry.ScopeId
                        Container = entry.Container
                        Persist = true
                    }

                    let connectedAt = DateTime.UtcNow

                    let! bound =
                        ProviderOAuthConnect.completeConnect
                            providerProfile
                            secretStore
                            (tryGetService<IAuditLog> ctx)
                            scope
                            entry.UserId
                            providerFlow
                            correlation
                            credentials
                            connectedAt

                    match bound with
                    | Error e ->
                        logger.Warn
                            $"OAuth callback: provider entry bind failed flow={flowName} entry={label} scope={entry.ScopeId}: {e}"

                        return! failTo e
                    | Ok() ->
                        // Schedule the proactive refresh (and the
                        // live-status probe) for this entry. Both are
                        // best-effort: a deployment with no
                        // `IJobScheduler` still gets a working
                        // connection, it just refreshes lazily on the
                        // next consumer call rather than ahead of
                        // expiry. Failing the connect over a scheduler
                        // that is not wired would be the wrong trade.
                        match tryGetService<IJobScheduler> ctx with
                        | None ->
                            logger.Debug
                                "OAuth callback: no IJobScheduler registered; provider token auto-refresh will not run"
                        | Some scheduler ->
                            let! refreshScheduled =
                                scheduler.Schedule(
                                    ProviderOAuthRefreshJobHandler.registrationFor scope flowName label entry.UserId
                                )

                            match refreshScheduled with
                            | Ok _ -> ()
                            | Error e ->
                                logger.Warn
                                    $"OAuth callback: could not schedule provider token refresh for '{label}': %A{e}"

                            if (tryGetService<IProviderEntryProbe> ctx).IsSome then
                                let! probeScheduled =
                                    scheduler.Schedule(
                                        ProviderStatusProbeJobHandler.registrationFor
                                            scope
                                            ProviderStatusProbeJobHandler.DefaultCronExpression
                                            entry.UserId
                                    )

                                match probeScheduled with
                                | Ok _ -> ()
                                | Error e ->
                                    logger.Warn
                                        $"OAuth callback: could not schedule provider status probe for scope {scope.Container}: %A{e}"

                        logger.Info
                            $"OAuth callback: provider entry connected flow={flowName} entry={label} scope={entry.ScopeId}"

                        return!
                            respondRedirect
                                ctx
                                $"/?providerConnected={Uri.EscapeDataString label}&flow={Uri.EscapeDataString flowName}"
        | _ ->
            return!
                respondText
                    ctx
                    400
                    $"OAuth flow '{flowName}' does not support provider-profile connect (it is not an IProviderOAuthFlow)."
    }

// ─── /callback handler ──────────────────────────────────────────────

let private callback (flowName: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        let queryValue (key: string) =
            match ctx.Request.Query.TryGetValue key with
            | true, values when values.Count > 0 ->
                let v = string values[0]
                if String.IsNullOrEmpty v then None else Some v
            | _ -> None

        let stateOpt = queryValue "state"
        let codeOpt = queryValue "code"
        let errorOpt = queryValue "error"

        // Step 1 — provider-side error: user cancelled or the
        // upstream rejected before consent. Clean up any state
        // entry that was issued, then redirect with a reason.
        match errorOpt with
        | Some providerError ->
            match stateOpt, tryGetService<IOAuthStateStore> ctx with
            | Some token, Some stateStore ->
                let! _ = stateStore.TryConsume token
                ()
            | _ -> ()

            let dataSourceIdHint =
                // Best-effort identification — when state is
                // consumed above we lose the dataSourceId, so the
                // admin UI lands on `/?dataSourceError=&reason=...`
                // and infers from the most recent dirty source.
                ""

            let reason = Uri.EscapeDataString providerError
            let target = $"/?dataSourceError={dataSourceIdHint}&reason={reason}"
            return! respondRedirect ctx target
        | None ->

            // Step 2 — required query parameters.
            match stateOpt, codeOpt with
            | None, _ -> return! respondText ctx 400 "state query parameter required"
            | _, None -> return! respondText ctx 400 "code query parameter required"
            | Some token, Some code ->

                // Step 3 — atomic state consume. TryConsume
                // checks TTL; expired or unknown returns None.
                match tryGetService<IOAuthStateStore> ctx with
                | None ->
                    return! respondText ctx 500 "OAuth state store is not registered (compose-time wiring missing)."
                | Some stateStore ->
                    let! entryOpt = stateStore.TryConsume token

                    match entryOpt with
                    | None ->
                        return! respondText ctx 400 "OAuth state mismatch (token unknown, expired, or already consumed)"
                    | Some entry ->

                        // Step 4 — defensive: state token must
                        // belong to this flow.
                        if entry.FlowName <> flowName then
                            return! respondText ctx 400 "OAuth state token does not match the requested flow"
                        else

                            // Step 5 — actor identity must match
                            // the /authorize initiator.
                            let accessContext = resolveAccessContext ctx

                            if accessContext.UserId <> entry.UserId then
                                return! respondText ctx 403 "OAuth flow was initiated by a different user"
                            else

                                // Step 6 — flow + config still
                                // present (hot-reload dev path
                                // could have unregistered them).
                                match tryFindFlow ctx flowName with
                                | None -> return! respondText ctx 404 $"OAuth flow '{flowName}' is not registered"
                                | Some flow ->

                                    // Phase 43.B — dispatch on the
                                    // correlation family. A pre-43.B state
                                    // entry has no `Correlation`, and
                                    // `correlationOf` maps its
                                    // `DataSourceId` onto the neutral key,
                                    // so an in-flight round-trip that
                                    // started before this deploy lands on
                                    // the data-source path exactly as it
                                    // would have.
                                    let correlation = OAuthFlowState.correlationOf entry

                                    if OAuthCorrelationKey.isProviderEntry correlation then
                                        return! providerCallbackTail ctx flowName flow entry correlation code
                                    else

                                        let configFromStore =
                                            tryGetService<IDataSourceConfigStore> ctx
                                            |> Option.map (fun s -> s.Get(entry.ScopeId, entry.DataSourceId))

                                        let! configOpt =
                                            match configFromStore with
                                            | Some a -> a
                                            | None -> async { return None }

                                        let flowCtx: OAuthFlowContext = {
                                            ScopeId = entry.Container
                                            DataSourceId = entry.DataSourceId
                                            Correlation = correlation
                                            Config = configOpt
                                        }

                                        // Step 7 — exchange code. A PKCE-
                                        // declaring flow is handed the stashed
                                        // verifier; an intercepted code is
                                        // useless without it. Fail closed if
                                        // the flow requires PKCE but the state
                                        // entry carries no verifier — never
                                        // silently exchange without it. A non-
                                        // PKCE flow gets `None` and is unchanged.
                                        // (Phase 43.B lifted this into
                                        // `exchangeCode` so the provider path
                                        // fails closed identically.)
                                        let! exchangeResult = exchangeCode flow flowCtx entry code

                                        let logger =
                                            tryGetService<ILogger> ctx
                                            |> Option.defaultWith (fun () ->
                                                { new ILogger with
                                                    member _.Debug _ = ()
                                                    member _.Info _ = ()
                                                    member _.Warn _ = ()
                                                    member _.Error(_, _) = ()
                                                })

                                        let blobStorage = tryGetService<IBlobStorage> ctx

                                        match exchangeResult with
                                        | Error err ->
                                            let message = OAuthError.toMessage err

                                            logger.Warn
                                                $"OAuth callback: ExchangeCode failed flow={flowName} dataSource={entry.DataSourceId} scope={entry.ScopeId}: {message}"

                                            // Persist last-error so
                                            // admin UI can surface
                                            // "Reconnect required".
                                            // Don't overwrite a prior
                                            // good ConnectedAt — read
                                            // existing metadata first.
                                            match blobStorage with
                                            | Some storage ->
                                                let! existing =
                                                    loadCredentialMetadata storage entry.ScopeId entry.DataSourceId

                                                let updated = {
                                                    FlowName = flowName
                                                    DataSourceId = entry.DataSourceId
                                                    ConnectedAt =
                                                        existing
                                                        |> Option.map _.ConnectedAt
                                                        |> Option.defaultValue entry.CreatedAt
                                                    LastError = Some message
                                                }

                                                let! _ =
                                                    saveCredentialMetadata
                                                        storage
                                                        entry.ScopeId
                                                        entry.DataSourceId
                                                        updated

                                                ()
                                            | None -> ()

                                            let reason = Uri.EscapeDataString message
                                            let target = $"/?dataSourceError={entry.DataSourceId}&reason={reason}"

                                            return! respondRedirect ctx target

                                        | Ok credentials ->

                                            // Step 8 — persist refresh
                                            // token in ISecretStore
                                            // under {flow}-refresh-{id}.
                                            // Container is pre-resolved
                                            // (entry.Container) so no
                                            // re-resolution risk.
                                            match tryGetService<ISecretStore> ctx with
                                            | None ->
                                                return!
                                                    respondText
                                                        ctx
                                                        500
                                                        "Secret store is not registered (deployment misconfiguration)."
                                            | Some secretStore ->
                                                let secretKey = $"{flowName}-refresh-{entry.DataSourceId}"

                                                let! secretResult =
                                                    secretStore.SetSecret(
                                                        entry.Container,
                                                        secretKey,
                                                        credentials.RefreshToken
                                                    )

                                                match secretResult with
                                                | Error e ->
                                                    logger.Error(
                                                        $"OAuth callback: refresh-token persist failed flow={flowName} dataSource={entry.DataSourceId} scope={entry.Container}: {e}",
                                                        None
                                                    )

                                                    return!
                                                        respondText
                                                            ctx
                                                            500
                                                            (sprintf "Failed to persist refresh token: %s" e)
                                                | Ok() ->

                                                    let connectedAt = DateTime.UtcNow

                                                    // Step 9 — write
                                                    // metadata blob.
                                                    match blobStorage with
                                                    | Some storage ->
                                                        let metadata = {
                                                            FlowName = flowName
                                                            DataSourceId = entry.DataSourceId
                                                            ConnectedAt = connectedAt
                                                            LastError = None
                                                        }

                                                        let! _ =
                                                            saveCredentialMetadata
                                                                storage
                                                                entry.ScopeId
                                                                entry.DataSourceId
                                                                metadata

                                                        ()
                                                    | None -> ()

                                                    // Step 10 — audit.
                                                    // Fire-and-forget;
                                                    // Record is best-
                                                    // effort and
                                                    // swallows its own
                                                    // failures.
                                                    match tryGetService<IAuditLog> ctx with
                                                    | Some auditLog ->
                                                        auditLog.Record(
                                                            entry.ScopeId,
                                                            OAuthConnected {
                                                                UserId = entry.UserId
                                                                ScopeId = entry.ScopeId
                                                                FlowName = flowName
                                                                DataSourceId = entry.DataSourceId
                                                                ConnectedAt = connectedAt
                                                            }
                                                        )
                                                        |> Async.Start
                                                    | None -> ()

                                                    logger.Info
                                                        $"OAuth callback: connected flow={flowName} dataSource={entry.DataSourceId} scope={entry.ScopeId}"

                                                    // Step 11 — return
                                                    // user to admin UI.
                                                    let target =
                                                        $"/?dataSourceConnected={entry.DataSourceId}&flow={flowName}"

                                                    return! respondRedirect ctx target
    }

// ─── Routes table ───────────────────────────────────────────────────

/// Routes mounted by `SDK.Server.compose` only when
/// `ServerConfig.DataIngestion = EnabledDataIngestion` AND at least
/// one `IOAuthCredentialFlow` is registered (gating lives in A.8 —
/// this list is the unconditional shape).
let routes: HttpHandler list = [
    GET >=> routef "/api/oauth/%s/authorize" authorize
    GET >=> routef "/api/oauth/%s/callback" callback
]