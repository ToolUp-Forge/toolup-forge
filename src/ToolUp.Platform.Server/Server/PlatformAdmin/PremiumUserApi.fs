// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.PremiumUserApi

open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 61 — Premium-user list admin endpoint ──────────────────
//
//   GET /api/_platform/admin/premium-users
//
// Read-only surface over `IUserClaims.ListPremiumUsers`. Platform-
// Admin gated (Phase 4b). Always mounted — the default
// `NoOpUserClaims` returns `Ok []` so disabled-claim deployments see
// an empty list rather than 404 / 500.
//
// **Grant / revoke writes are NOT here.** Phase 62 already ships
// `POST/DELETE /api/_platform/users/{userId}/premium` via
// `GrantPremiumApiHandler.fs`; the admin widget composes against
// the existing endpoint family rather than duplicating it here.
// This handler covers only the missing piece — the list read.

let private jsonOptions = FableConverters.create ()

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private resolveUserClaims (ctx: HttpContext) : IUserClaims =
    match ctx.RequestServices.GetService(typeof<IUserClaims>) with
    | :? IUserClaims as svc -> svc
    | _ -> NoOpUserClaims() :> _

let private writeError (ctx: HttpContext) (statusCode: int) (message: string) : HttpFuncResult = task {
    ctx.Response.StatusCode <- statusCode
    return! ctx.WriteTextAsync message
}

let private writeJson (ctx: HttpContext) (statusCode: int) (payload: 'T) : HttpFuncResult = task {
    let json = JsonSerializer.Serialize(payload, jsonOptions)
    ctx.Response.StatusCode <- statusCode
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    return! ctx.WriteTextAsync json
}

let private listPremiumUsersHandler: HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        let accessContext = resolveAccessContext ctx

        if not (AccessContext.canModifyPlatformConfig accessContext) then
            return! writeError ctx 403 "platform admin role required"
        else
            let claims = resolveUserClaims ctx
            let! result = claims.ListPremiumUsers() |> Async.StartAsTask

            match result with
            | Ok users -> return! writeJson ctx 200 users
            | Error msg -> return! writeError ctx 502 msg
    }

/// Routes table. Always mounted — the read handler gates on
/// Platform-Admin role server-side; the default `NoOpUserClaims`
/// returns `Ok []` for deployments that haven't wired a concrete
/// auth-provider claim reader.
let routes: HttpHandler list = [
    GET >=> route "/api/_platform/admin/premium-users" >=> listPremiumUsersHandler
]