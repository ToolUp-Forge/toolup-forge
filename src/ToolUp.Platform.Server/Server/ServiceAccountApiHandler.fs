// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ServiceAccountApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── IServiceAccountApi handler (Phase 527) ──────────────────────────
//
// The admin surface behind `ServiceAccountUI`. Resolves
// `IServiceAccountStore`, `ITeamStore` and `AccessContext` from
// per-request DI, mirroring `WebhookApiHandler`.
//
// Three gates, applied in this order, on EVERY method:
//
//   1. **Not a machine caller.** A `ClaimBearer` subject is refused
//      outright — reads included. A validated service-account token
//      resolves to `ClaimBearer`, so without this a machine credential
//      could mint further credentials or widen its own account's
//      permissions, and a credential that can rewrite its own scope is
//      not scoped. This is enforced here rather than by an attribute
//      because no shipped auth attribute expresses "authenticated human
//      only" — `[<TenantScoped>]` classifies the method for the Phase
//      69d dispatcher and admits claim-bearers.
//
//      Share-link bearers are refused by the same rule, which is correct
//      for a different reason: a share token's authority is bounded to
//      its own resource, and account administration is not it.
//
//   2. **Scope resolved server-side.** No method takes a scope
//      parameter; the scope comes from `AccessContext.configScope`, and
//      the store refuses an account whose own `ScopeId` disagrees. A
//      caller cannot name another tenant's scope because there is
//      nowhere to name it (GP 4).
//
//   3. **Owner/Admin in team scope.** `TeamRoles.canWriteTeamConfig`,
//      the same gate `WebhookApiHandler` and `ConfigHandler` apply —
//      and applied to READS as well as writes, which is the deliberate
//      difference from those two. A token list is a map of a tenant's
//      machine integrations and their expiry dates; that is
//      administrative reconnaissance, not team-visible configuration.
//      Outside team scope (a personal deployment) the caller owns the
//      scope outright and the gate is a no-op, exactly as it is there.
//
// Errors are returned as `Result<_, string>` with operator-readable
// messages. The store's typed `ServiceAccountError` is mapped at the
// boundary rather than leaked: an admin needs "no such account", not a
// DU case name, and the token-path cases never reach this API at all
// (they belong to the middleware).

let private describe (err: ServiceAccountError) : string =
    match err with
    | ServiceAccountError.Malformed -> "The token string is not in the expected format."
    | ServiceAccountError.NotFound -> "No such token in this scope."
    | ServiceAccountError.InvalidSecret -> "The presented token secret did not match."
    | ServiceAccountError.Expired -> "That token has expired."
    | ServiceAccountError.RevokedToken -> "That token has already been revoked."
    | ServiceAccountError.AccountDisabled -> "That service account is disabled. Re-enable it before minting tokens."
    | ServiceAccountError.AccountNotFound -> "No such service account in this scope."
    | ServiceAccountError.NoPermissionsDeclared ->
        "A service account must declare at least one module permission — an empty set would grant unrestricted access."
    | ServiceAccountError.ScopeMismatch -> "No such service account in this scope."
    | ServiceAccountError.StorageFailed msg -> $"The service-account store could not complete the operation: {msg}"

let serviceAccountApi (ctx: HttpContext) : IServiceAccountApi =
    let store =
        match ctx.RequestServices.GetService(typeof<IServiceAccountStore>) with
        | :? IServiceAccountStore as s -> Some s
        | _ -> None

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback for tests that bypass ScopeResolutionMiddleware —
            // same pattern as WebhookApiHandler / ConfigHandler. An
            // anonymous floor, so the gates below refuse rather than
            // admit.
            AccessContext.unrestricted (AnonymousSession "anonymous")

    /// Gates 1 + 3, and the scope resolution of gate 2. Every method
    /// body runs through this, so there is exactly one place the policy
    /// is stated and no method can be added that quietly skips it.
    let authorised (f: IServiceAccountStore -> string -> Async<Result<'T, string>>) : Async<Result<'T, string>> = async {
        match store with
        | None -> return Error "Service accounts are not enabled in this deployment."
        | Some s ->
            match accessContext.Subject with
            | ClaimBearer _ ->
                // Gate 1 — the load-bearing one. See the preamble.
                return
                    Error
                        "Service accounts cannot be managed with a token credential. Sign in as a team owner or admin."
            | AnonymousSession _ -> return Error "Sign in to manage service accounts."
            | subject ->
                match AccessContext.configScope accessContext with
                | None -> return Error "Service accounts require a persistent scope. Sign in or join a team."
                | Some scope ->
                    match subject with
                    | TeamMember(userId, teamId) ->
                        match ctx.RequestServices.GetService(typeof<ITeamStore>) with
                        | :? ITeamStore as teams ->
                            let! role = teams.GetMemberRole(teamId, userId)

                            match role with
                            | Some r when TeamRoles.canWriteTeamConfig r -> return! f s scope.ScopeId
                            | Some r ->
                                return
                                    Error
                                        $"Only team owners and admins can manage service accounts. Your role: {TeamRoles.displayName r}."
                            | None -> return Error "You are not a member of this team."
                        | _ -> return Error "Team management is not available in this deployment."
                    | _ ->
                        // Personal / non-team scope: the caller owns the
                        // scope outright, so there is no role to check.
                        return! f s scope.ScopeId
    }

    let actor = accessContext.UserId

    let toResult (mapper: 'a -> 'b) (r: Result<'a, ServiceAccountError>) : Result<'b, string> =
        match r with
        | Ok v -> Ok(mapper v)
        | Error e -> Error(describe e)

    {
        ListAccounts =
            fun () ->
                authorised (fun store scopeId -> async {
                    let! accounts = store.List scopeId
                    return Ok accounts
                })

        CreateAccount =
            fun request ->
                authorised (fun store scopeId -> async {
                    let! result =
                        store.Create {
                            DisplayName = request.DisplayName
                            ScopeId = scopeId
                            Permissions = request.Permissions
                            CreatedBy = actor
                        }

                    return toResult id result
                })

        SetAccountStatus =
            fun (accountId, status) ->
                authorised (fun store scopeId -> async {
                    let! result = store.SetStatus(scopeId, accountId, status, actor)
                    return toResult id result
                })

        SetAccountPermissions =
            fun (accountId, permissions) ->
                authorised (fun store scopeId -> async {
                    let! result = store.SetPermissions(scopeId, accountId, permissions, actor)
                    return toResult id result
                })

        ListTokens =
            fun accountId ->
                authorised (fun store scopeId -> async {
                    // Read the account first so a caller cannot use the
                    // token listing to probe which account ids exist in
                    // ANOTHER scope: the store's `ListTokens` filters by
                    // scope and would simply return `[]`, which is
                    // indistinguishable from "an account with no tokens".
                    match! store.Get(scopeId, accountId) with
                    | Error e -> return Error(describe e)
                    | Ok _ ->
                        let! tokens = store.ListTokens(scopeId, accountId)
                        return Ok(tokens |> List.map ServiceAccountTokenView.ofRecord)
                })

        MintToken =
            fun request ->
                authorised (fun store scopeId -> async {
                    let! result =
                        store.MintToken {
                            AccountId = request.AccountId
                            ScopeId = scopeId
                            DisplayName = request.DisplayName
                            IssuedBy = actor
                            ExpiresAt = request.ExpiresAt
                        }

                    return
                        result
                        |> toResult (fun minted -> {
                            Secret = minted.Secret
                            Token = ServiceAccountTokenView.ofRecord minted.Record
                        })
                })

        RevokeToken =
            fun tokenId ->
                authorised (fun store scopeId -> async {
                    let! result = store.RevokeToken(scopeId, tokenId, actor)
                    return toResult id result
                })
    }