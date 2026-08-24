// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.OAuth1aFlowHandler

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.TeamManagement

// ─── Phase 10g — OAuth 1.0a three-legged flow endpoints ─────────────────
//
// Two HTTP handlers backing the substrate's `/api/oauth1a/{flowName}/*`
// surface (the 1.0a sibling of `OAuthFlowHandler`'s OAuth 2.0 routes):
//
//   GET /api/oauth1a/{flowName}/start?resourceId={id}
//   GET /api/oauth1a/{flowName}/callback?oauth_token=...&oauth_verifier=...
//
// `/start` (leg 1) resolves the caller's scope, RBAC-gates (Owner / Admin
// in Team modes), fetches a request token from the provider via
// `IOAuth1aFlow.BuildRequestTokenUrl` (a server-to-server call the flow
// signs with the consumer credentials + empty token secret), stashes the
// request-token secret + connection identity in the `IOAuth1aStateStore`
// keyed by the request token, and 302-redirects the user-agent to the
// provider's authorisation URL.
//
// `/callback` (leg 3) consumes the stashed state single-use (keyed by the
// returned `oauth_token`), exchanges the now-authorised request token for
// the permanent access-token pair via
// `IOAuth1aFlow.ExchangeRequestTokenForAccess`, persists the token pair
// via `ISecretStore` (two keys — token + token secret), emits an
// `OAuth1aConnected` audit event, and 302-redirects back to the admin UI.
//
// Routes are mounted only when `ServerConfig.OAuth1a = EnabledOAuth1a`
// (see `BuildRouteHandlers`). Stateless across calls; the state store is
// the only stateful element (single-instance default + distributed
// companion candidate).

[<Literal>]
let private RedirectBaseEnvVar = ConfigKeys.Names.oauthRedirectBase

/// TTL for a pending request-token authorisation (the user has this long
/// to complete provider consent before the stashed state expires).
let private stateTtl = TimeSpan.FromMinutes 10.0

/// Secret key for the persisted access token. Stable per
/// `(flowName, resourceId)`.
let accessTokenKey (flowName: string) (resourceId: string) =
    sprintf "%s-access-token-%s" flowName resourceId

/// Secret key for the persisted access-token secret.
let accessSecretKey (flowName: string) (resourceId: string) =
    sprintf "%s-access-secret-%s" flowName resourceId

let private redirectBaseFromRequest (ctx: HttpContext) : string =
    sprintf "%s://%s" ctx.Request.Scheme (string ctx.Request.Host)

let private resolveRedirectBase (ctx: HttpContext) : string =
    // Phase 698 — through the Phase-696 `ConfigResolution` seam.
    match ConfigResolution.tryValue RedirectBaseEnvVar with
    | None -> (redirectBaseFromRequest ctx).TrimEnd('/')
    | Some v -> v.TrimEnd('/')

let private callbackUriFor (ctx: HttpContext) (flowName: string) : string =
    sprintf "%s/api/oauth1a/%s/callback" (resolveRedirectBase ctx) flowName

let private tryGetService<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

let private tryFindFlow (ctx: HttpContext) (flowName: string) : IOAuth1aFlow option =
    ctx.RequestServices.GetServices(typeof<IOAuth1aFlow>)
    |> Seq.cast<IOAuth1aFlow>
    |> Seq.tryFind (fun f -> f.Name = flowName)

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Owner / Admin gate — mirrors `OAuthFlowHandler.ensureOwnerAdmin`.
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

let private respondText (ctx: HttpContext) (status: int) (msg: string) = task {
    ctx.Response.StatusCode <- status
    do! ctx.WriteTextAsync msg :> Task
    return Some ctx
}

let private respondRedirect (ctx: HttpContext) (url: string) = task {
    ctx.Response.StatusCode <- 302
    ctx.Response.Headers["Location"] <- Microsoft.Extensions.Primitives.StringValues url
    return Some ctx
}

let private queryValue (ctx: HttpContext) (key: string) : string option =
    match ctx.Request.Query.TryGetValue key with
    | true, values when values.Count > 0 ->
        let v = string values[0]
        if String.IsNullOrEmpty v then None else Some v
    | _ -> None

// ─── leg 1: /start ──────────────────────────────────────────────────────

let private start (flowName: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        match queryValue ctx "resourceId" with
        | None -> return! respondText ctx 400 "Missing required query parameter: resourceId"
        | Some resourceId ->
            let accessContext = resolveAccessContext ctx

            match AccessContext.configScope accessContext with
            | None -> return! respondText ctx 400 "OAuth flow requires a persistent scope (sign in or join a team)."
            | Some scope ->
                let! rbac = ensureOwnerAdmin ctx accessContext

                match rbac with
                | Error msg -> return! respondText ctx 403 msg
                | Ok() ->
                    match tryFindFlow ctx flowName with
                    | None -> return! respondText ctx 404 $"OAuth 1.0a flow '{flowName}' is not registered"
                    | Some flow ->
                        match tryGetService<IOAuth1aStateStore> ctx with
                        | None ->
                            return!
                                respondText
                                    ctx
                                    500
                                    "OAuth 1.0a state store is not registered (compose-time wiring missing)."
                        | Some stateStore ->
                            let flowCtx: OAuth1aFlowContext = {
                                ScopeId = scope.Container
                                ResourceId = resourceId
                            }

                            let! result = flow.BuildRequestTokenUrl(flowCtx, callbackUriFor ctx flowName)

                            match result with
                            | Error err -> return! respondText ctx 502 (OAuth1aError.toMessage err)
                            | Ok rt ->
                                let state: OAuth1aRequestState = {
                                    ScopeId = scope.ScopeId
                                    Container = scope.Container
                                    ResourceId = resourceId
                                    UserId = accessContext.UserId
                                    FlowName = flowName
                                    RequestTokenSecret = rt.RequestTokenSecret
                                    CreatedAt = DateTime.UtcNow
                                }

                                do! stateStore.Save(rt.RequestToken, state)
                                return! respondRedirect ctx rt.AuthorizeUrl
    }

// ─── leg 3: /callback ───────────────────────────────────────────────────

let private callback (flowName: string) : HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        let denied = queryValue ctx "denied"

        match queryValue ctx "oauth_token", queryValue ctx "oauth_verifier" with
        | _ when denied.IsSome ->
            // The user declined consent (provider appends `denied`).
            return! respondText ctx 400 "Authorization was declined."
        | None, _
        | _, None -> return! respondText ctx 400 "Missing oauth_token / oauth_verifier on the callback."
        | Some requestToken, Some verifier ->
            match tryGetService<IOAuth1aStateStore> ctx with
            | None -> return! respondText ctx 500 "OAuth 1.0a state store is not registered."
            | Some stateStore ->
                let! stateOpt = stateStore.TakeValid(requestToken, stateTtl)

                match stateOpt with
                | None -> return! respondText ctx 400 "No matching pending authorisation (expired or already used)."
                | Some state ->
                    match tryFindFlow ctx flowName with
                    | None -> return! respondText ctx 404 $"OAuth 1.0a flow '{flowName}' is not registered"
                    | Some flow ->
                        let flowCtx: OAuth1aFlowContext = {
                            ScopeId = state.Container
                            ResourceId = state.ResourceId
                        }

                        let! exchange =
                            flow.ExchangeRequestTokenForAccess(
                                flowCtx,
                                requestToken,
                                state.RequestTokenSecret,
                                verifier
                            )

                        match exchange with
                        | Error err -> return! respondText ctx 502 (OAuth1aError.toMessage err)
                        | Ok tokenPair ->
                            match tryGetService<ISecretStore> ctx with
                            | None -> return! respondText ctx 500 "Secret store is not registered."
                            | Some secretStore ->
                                let! t =
                                    secretStore.SetSecret(
                                        state.Container,
                                        accessTokenKey flowName state.ResourceId,
                                        tokenPair.Token
                                    )

                                let! s =
                                    secretStore.SetSecret(
                                        state.Container,
                                        accessSecretKey flowName state.ResourceId,
                                        tokenPair.TokenSecret
                                    )

                                match t, s with
                                | Error msg, _
                                | _, Error msg ->
                                    return! respondText ctx 500 (sprintf "Failed to persist access token: %s" msg)
                                | Ok(), Ok() ->
                                    // Best-effort audit (GP 6).
                                    match tryGetService<IAuditLog> ctx with
                                    | Some auditLog ->
                                        auditLog.Record(
                                            state.ScopeId,
                                            OAuth1aConnected {
                                                UserId = state.UserId
                                                ScopeId = state.ScopeId
                                                FlowName = flowName
                                                ResourceId = state.ResourceId
                                                ConnectedAt = DateTime.UtcNow
                                            }
                                        )
                                        |> Async.Start
                                    | None -> ()

                                    return! respondRedirect ctx "/?oauth1a=connected"
    }

// ─── Route table ────────────────────────────────────────────────────────

/// The OAuth 1.0a flow routes. Mounted by `BuildRouteHandlers` only when
/// `ServerConfig.OAuth1a = EnabledOAuth1a`.
let routes: HttpHandler list = [
    GET >=> routef "/api/oauth1a/%s/start" start
    GET >=> routef "/api/oauth1a/%s/callback" callback
]