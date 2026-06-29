module ToolUp.Platform.Tests.InProcess.OffboardConfirmationTests

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform

// ─── Phase 54i — confirmation-gated offboard tests ───────────────────
//
// Exercises the `IPlatformTenantApi` handler's confirmation gate end to
// end (DefaultHttpContext + DI, the same shape as the production compose
// path). Covers: NoConfirmation preserves Phase 54 one-call behaviour;
// TokenConfirmation refuses the token-less paths and admits the minted-
// token path; one-time-token consumption; scope binding; and the
// TwoPersonRule requester≠redeemer invariant.

// ─── In-memory IShareTokenStore (stub: Token string == TokenId) ──────

type private InMemoryShareTokenStore() =
    let claims = ConcurrentDictionary<string, ShareTokenClaim>()
    let mutable counter = 0

    interface IShareTokenStore with
        member _.Issue(req: ShareTokenIssueRequest) = async {
            counter <- counter + 1
            let tokenId = sprintf "tok-%d" counter
            let now = DateTimeOffset.UtcNow

            let claim: ShareTokenClaim = {
                TokenId = tokenId
                ScopeId = req.ScopeId
                ResourceKind = req.ResourceKind
                ResourceId = req.ResourceId
                AttributedHandle = req.AttributedHandle
                IssuedBy = req.IssuedBy
                IssuedAt = now
                ExpiresAt =
                    match req.ExpiresAt with
                    | Some e -> e
                    | None -> now.AddDays 30.0
                UseLimit =
                    match req.UseLimit with
                    | Some u -> u
                    | None -> Some 1
                UsedCount = 0
                Revoked = false
                RateLimit = req.RateLimit
            }

            claims[tokenId] <- claim
            return Ok { Token = tokenId; Claim = claim }
        }

        member _.Validate(token: string) = async {
            match claims.TryGetValue token with
            | false, _ -> return Error ShareTokenError.NotFound
            | true, c ->
                if c.Revoked then
                    return Error ShareTokenError.RevokedToken
                elif c.ExpiresAt < DateTimeOffset.UtcNow then
                    return Error ShareTokenError.Expired
                else
                    match c.UseLimit with
                    | Some lim when c.UsedCount >= lim -> return Error ShareTokenError.UseLimitExceeded
                    | _ -> return Ok c
        }

        member _.MarkUsed(_scopeId: string, tokenId: string) = async {
            match claims.TryGetValue tokenId with
            | false, _ -> return Error ShareTokenError.NotFound
            | true, c ->
                match c.UseLimit with
                | Some lim when c.UsedCount >= lim -> return Error ShareTokenError.UseLimitExceeded
                | _ ->
                    claims[tokenId] <- { c with UsedCount = c.UsedCount + 1 }
                    return Ok()
        }

        member _.Revoke(_scopeId, tokenId, _actor) = async {
            match claims.TryGetValue tokenId with
            | true, c ->
                claims[tokenId] <- { c with Revoked = true }
                return Ok()
            | false, _ -> return Ok() // idempotent
        }

        member _.ListByResource(_, _, _) = async { return [] }
        member _.ListByIssuer(_, _) = async { return [] }

// ─── Handler builder ─────────────────────────────────────────────────

let private adminCtx (userId: string) : AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser userId) with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// Build a tenant API handler for `userId` under `mode`, sharing the
/// given `tokenStore` (so a token minted by one admin's handler is
/// redeemable by another's — the two-person flow). `tokenStore = None`
/// models a deployment that enabled a confirmation mode without composing
/// the share-token substrate.
let private handlerFor
    (userId: string)
    (mode: OffboardConfirmationMode)
    (tokenStore: IShareTokenStore option)
    : IPlatformTenantApi =
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(adminCtx userId) |> ignore

    services.AddSingleton<ServerConfig>(
        {
            ServerConfig.defaults with
                TenantLifecycle = EnabledTenantLifecycle
                TenantOffboardConfirmation = mode
        }
    )
    |> ignore

    match tokenStore with
    | Some s -> services.AddSingleton<IShareTokenStore>(s) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    PlatformTenantApiHandler.platformTenantApi ctx

let private confirmationRequired =
    PlatformTenantApiHandler.offboardConfirmationRequired

let tests =
    testList "Phase 54i — confirmation-gated offboard" [

        testCaseAsync "NoConfirmation — DeprovisionTenant proceeds (Phase 54 one-call behaviour preserved)"
        <| async {
            let api = handlerFor "admin-1" NoConfirmation (Some(InMemoryShareTokenStore()))
            let! result = api.DeprovisionTenant("team-x", "admin-1", "winding down")

            match result with
            | Ok _ -> ()
            | Error e -> failtestf "expected Ok under NoConfirmation, got Error %s" e
        }

        testCaseAsync "TokenConfirmation — token-less DeprovisionTenant is refused"
        <| async {
            let api = handlerFor "admin-1" TokenConfirmation (Some(InMemoryShareTokenStore()))
            let! result = api.DeprovisionTenant("team-x", "admin-1", "winding down")
            Expect.equal result (Error confirmationRequired) "token-less offboard refused under TokenConfirmation"
        }

        testCaseAsync "TokenConfirmation — token-less DeprovisionTenantAsync + ExportThenDeprovision also refused"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TokenConfirmation (Some store)
            let! r1 = api.DeprovisionTenantAsync("team-x", "admin-1", "x")
            let! r2 = api.ExportThenDeprovision("team-x", "admin-1", "x")
            Expect.equal r1 (Error confirmationRequired) "async path gated"
            Expect.equal r2 (Error confirmationRequired) "export-then-erase path gated"
        }

        testCaseAsync "TokenConfirmation — request a token, then redeem it → offboard proceeds"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TokenConfirmation (Some store)

            match! api.RequestDeprovisionToken("team-x", "winding down") with
            | Error e -> failtestf "RequestDeprovisionToken failed: %s" e
            | Ok confirmation ->
                Expect.equal confirmation.ScopeId "team-x" "token bound to scope"
                Expect.equal confirmation.RequestedBy "admin-1" "requester recorded"
                let! result = api.DeprovisionTenantConfirmed("team-x", "admin-1", "winding down", confirmation.Token)

                match result with
                | Ok _ -> ()
                | Error e -> failtestf "expected Ok with a valid token, got Error %s" e
        }

        testCaseAsync "TokenConfirmation — a garbage token is refused"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TokenConfirmation (Some store)
            let! result = api.DeprovisionTenantConfirmed("team-x", "admin-1", "x", "not-a-real-token")
            Expect.equal result (Error confirmationRequired) "invalid token refused"
        }

        testCaseAsync "TokenConfirmation — token is one-time; a second redemption is refused"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TokenConfirmation (Some store)

            match! api.RequestDeprovisionToken("team-x", "r") with
            | Error e -> failtestf "mint failed: %s" e
            | Ok confirmation ->
                let! first = api.DeprovisionTenantConfirmed("team-x", "admin-1", "r", confirmation.Token)
                Expect.isTrue (Result.isOk first) "first redemption succeeds"
                let! second = api.DeprovisionTenantConfirmed("team-x", "admin-1", "r", confirmation.Token)
                Expect.equal second (Error confirmationRequired) "second redemption of a one-time token refused"
        }

        testCaseAsync "TokenConfirmation — a token minted for one scope cannot offboard another"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TokenConfirmation (Some store)

            match! api.RequestDeprovisionToken("team-x", "r") with
            | Error e -> failtestf "mint failed: %s" e
            | Ok confirmation ->
                let! result = api.DeprovisionTenantConfirmed("team-y", "admin-1", "r", confirmation.Token)
                Expect.equal result (Error confirmationRequired) "cross-scope token redemption refused"
        }

        testCaseAsync "TwoPersonRule — the requester cannot self-approve"
        <| async {
            let store = InMemoryShareTokenStore()
            let api = handlerFor "admin-1" TwoPersonRule (Some store)

            match! api.RequestDeprovisionToken("team-x", "r") with
            | Error e -> failtestf "mint failed: %s" e
            | Ok confirmation ->
                let! result = api.DeprovisionTenantConfirmed("team-x", "admin-1", "r", confirmation.Token)
                Expect.equal result (Error confirmationRequired) "same-admin redemption refused under TwoPersonRule"
        }

        testCaseAsync "TwoPersonRule — a second, different admin can redeem the token"
        <| async {
            // One shared store; admin-1 requests, admin-2 redeems.
            let store = InMemoryShareTokenStore()
            let requester = handlerFor "admin-1" TwoPersonRule (Some store)
            let approver = handlerFor "admin-2" TwoPersonRule (Some store)

            match! requester.RequestDeprovisionToken("team-x", "r") with
            | Error e -> failtestf "mint failed: %s" e
            | Ok confirmation ->
                let! result = approver.DeprovisionTenantConfirmed("team-x", "admin-2", "r", confirmation.Token)

                match result with
                | Ok _ -> ()
                | Error e -> failtestf "expected a second admin to redeem successfully, got Error %s" e
        }

        testCaseAsync "TokenConfirmation without a composed IShareTokenStore — mint + redeem return a clear error"
        <| async {
            let api = handlerFor "admin-1" TokenConfirmation None
            let! mint = api.RequestDeprovisionToken("team-x", "r")
            let! redeem = api.DeprovisionTenantConfirmed("team-x", "admin-1", "r", "tok")
            Expect.equal mint (Error PlatformTenantApiHandler.offboardConfirmationNoStore) "mint needs a store"
            Expect.equal redeem (Error PlatformTenantApiHandler.offboardConfirmationNoStore) "redeem needs a store"
        }
    ]