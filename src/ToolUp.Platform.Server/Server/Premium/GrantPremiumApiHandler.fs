// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.GrantPremiumApiHandler

open System
open Giraffe
open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Fable.Remoting.Json
open ToolUp.Platform

// ─── Phase 62 — operator grant / revoke endpoint ──────────────────
//
// POST /api/_platform/users/{userId}/premium    — grant
// DELETE /api/_platform/users/{userId}/premium  — revoke
//
// Gated to Platform-Admin role only. Body: `{ Reason: string option }`
// for both verbs. Writes through to the configured `IUserClaims`
// implementation; the default `NoOpUserClaims` succeeds without
// touching any provider (the audit trail still captures the operator
// intent).

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private PlatformScope = "_platform"

let private resolveUserClaims (ctx: HttpContext) : IUserClaims =
    match ctx.RequestServices.GetService(typeof<IUserClaims>) with
    | :? IUserClaims as svc -> svc
    | _ -> NoOpUserClaims() :> _

let private resolveAuditLog (ctx: HttpContext) : IAuditLog option =
    match ctx.RequestServices.GetService(typeof<IAuditLog>) with
    | :? IAuditLog as svc -> Some svc
    | _ -> None

let private resolvePlatformAdminStore (ctx: HttpContext) : IPlatformAdminStore option =
    match ctx.RequestServices.GetService(typeof<IPlatformAdminStore>) with
    | :? IPlatformAdminStore as s -> Some s
    | _ -> None

let private resolveGrantorId (ctx: HttpContext) : string =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac.UserId
    | _ -> "anonymous"

let private ensurePlatformAdmin (ctx: HttpContext) : Async<Result<unit, string>> = async {
    let grantorId = resolveGrantorId ctx

    if grantorId = "anonymous" then
        return Error "Authenticated Platform Admin required"
    else
        match resolvePlatformAdminStore ctx with
        | None -> return Error "Platform Admin store not registered"
        | Some store ->
            let! isAdmin = store.IsPlatformAdmin grantorId

            if isAdmin then
                return Ok()
            else
                return Error "Platform Admin role required"
}

type private PremiumBody = { Reason: string option }

let private readReason (ctx: HttpContext) : System.Threading.Tasks.Task<string option> = task {
    use reader = new System.IO.StreamReader(ctx.Request.Body)
    let! body = reader.ReadToEndAsync()

    if String.IsNullOrWhiteSpace body then
        return None
    else
        try
            let parsed = JsonConvert.DeserializeObject<PremiumBody>(body, jsonSettings)
            return parsed.Reason
        with _ ->
            return None
}

let private grantHandler (userId: string) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        match! ensurePlatformAdmin ctx |> Async.StartAsTask with
        | Error msg ->
            ctx.Response.StatusCode <- 403
            return! ctx.WriteTextAsync msg
        | Ok() ->
            let! reason = readReason ctx
            let grantor = resolveGrantorId ctx
            let claims = resolveUserClaims ctx
            let! result = claims.GrantPremium(userId, grantor, reason) |> Async.StartAsTask

            match result with
            | Error msg ->
                ctx.Response.StatusCode <- 502
                return! ctx.WriteTextAsync msg
            | Ok status ->
                let occurredAt =
                    match status with
                    | Premium(grantedAt, _, _) -> grantedAt
                    | NotPremium -> DateTimeOffset.UtcNow

                resolveAuditLog ctx
                |> Option.iter (fun auditLog ->
                    auditLog.Record(PlatformScope, AuditEvent.PremiumGranted(userId, grantor, reason, occurredAt))
                    |> Async.Start)

                ctx.Response.StatusCode <- 204
                return! next ctx
    }

let private revokeHandler (userId: string) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        match! ensurePlatformAdmin ctx |> Async.StartAsTask with
        | Error msg ->
            ctx.Response.StatusCode <- 403
            return! ctx.WriteTextAsync msg
        | Ok() ->
            let! reason = readReason ctx
            let grantor = resolveGrantorId ctx
            let claims = resolveUserClaims ctx
            let! result = claims.RevokePremium(userId, grantor, reason) |> Async.StartAsTask

            match result with
            | Error msg ->
                ctx.Response.StatusCode <- 502
                return! ctx.WriteTextAsync msg
            | Ok() ->
                resolveAuditLog ctx
                |> Option.iter (fun auditLog ->
                    auditLog.Record(
                        PlatformScope,
                        AuditEvent.PremiumRevoked(userId, grantor, reason, DateTimeOffset.UtcNow)
                    )
                    |> Async.Start)

                ctx.Response.StatusCode <- 204
                return! next ctx
    }

/// GET handler — returns the current user's `PremiumStatus`. The
/// `usePremium` Feliz hook calls this on first mount. Anonymous
/// callers receive `NotPremium`.
let private statusHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let userId = resolveGrantorId ctx
        let claims = resolveUserClaims ctx
        let! status = claims.GetPremiumStatus userId |> Async.StartAsTask

        let json = JsonConvert.SerializeObject(status, jsonSettings)
        ctx.Response.StatusCode <- 200
        ctx.Response.ContentType <- "application/json; charset=utf-8"
        return! ctx.WriteTextAsync json
    }

/// Routes table. Always mounted — the write handlers gate themselves
/// on Platform-Admin role + the configured `IUserClaims` impl
/// decides whether writes go anywhere meaningful. The read endpoint
/// is open to any caller (anonymous → NotPremium).
let routes: HttpHandler list = [
    GET >=> route "/api/_platform/users/me/premium-status" >=> statusHandler
    POST >=> routef "/api/_platform/users/%s/premium" grantHandler
    DELETE >=> routef "/api/_platform/users/%s/premium" revokeHandler
]