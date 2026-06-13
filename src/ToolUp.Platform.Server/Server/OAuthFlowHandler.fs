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
let private RedirectBaseEnvVar = "TOOLUP_OAUTH_REDIRECT_BASE"

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
    let envValue =
        match Environment.GetEnvironmentVariable RedirectBaseEnvVar with
        | null
        | "" -> None
        | v -> Some v

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
/// in `DataIngestionApiHandler.fs:46-62`.
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
/// substrate-handler comment block in `IOAuthCredentialFlow.fs:127`.
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

// ─── /authorize handler ─────────────────────────────────────────────

let private authorize (flowName: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        // Step 1 — required query parameter.
        let dataSourceIdOpt =
            match ctx.Request.Query.TryGetValue "dataSourceId" with
            | true, values when values.Count > 0 ->
                let v = string values[0]
                if String.IsNullOrEmpty v then None else Some v
            | _ -> None

        match dataSourceIdOpt with
        | None -> return! respondText ctx 400 "dataSourceId query parameter required"
        | Some dataSourceId ->

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

                        // Step 5 — data-source config must exist.
                        match tryGetService<IDataSourceConfigStore> ctx with
                        | None -> return! respondText ctx 500 "Data ingestion is not enabled in this deployment."
                        | Some configStore ->
                            let! configOpt = configStore.Get(scope.ScopeId, dataSourceId)

                            match configOpt with
                            | None -> return! respondText ctx 404 $"Data source '{dataSourceId}' not found"
                            | Some config ->

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
                                            Config = Some config
                                        }

                                        let pkceChallenge =
                                            if flow.SupportsPkce then
                                                Some {
                                                    Challenge = OAuthCrypto.codeChallengeFromVerifier verifier
                                                    Method = "S256"
                                                }
                                            else
                                                None

                                        let! urlResult =
                                            flow.BuildAuthorizeUrl(flowCtx, state, redirectUri, pkceChallenge)

                                        match urlResult with
                                        | Error err ->
                                            return!
                                                respondText ctx (httpStatusForOAuthError err) (OAuthError.toMessage err)
                                        | Ok url -> return! respondRedirect ctx url
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
                                    let! exchangeResult = async {
                                        if flow.SupportsPkce && Option.isNone entry.CodeVerifier then
                                            return
                                                Error(
                                                    OAuthError.OAuthFlowFailed
                                                        "PKCE verifier missing from the state entry; this flow requires PKCE and the code cannot be exchanged without it."
                                                )
                                        else
                                            let codeVerifier = if flow.SupportsPkce then entry.CodeVerifier else None

                                            return! flow.ExchangeCode(flowCtx, code, entry.RedirectUri, codeVerifier)
                                    }

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
                                                saveCredentialMetadata storage entry.ScopeId entry.DataSourceId updated

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