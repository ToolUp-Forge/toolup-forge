module ToolUp.Platform.EncryptionAdminHandler

open System
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22 — encryption admin endpoint ───────────────────────────
//
// Single deployment-admin route:
//   POST /api/_platform/encryption/destroy-scope-key/{scopeId}
//
// Crypto-shreds the encryption key for `scopeId`. After this call,
// every blob previously encrypted under that scope's key is
// permanently undecryptable. Used as the canonical tenant-offboarding
// path for deployments wired with `PerScopeKeyResolver`.
//
// Gating (Phase 4b commit 4g — role-OR-token):
//
//   1. **Platform Admin role** (preferred). When the caller's
//      `AccessContext.canModifyPlatformConfig` is true, the request
//      proceeds with `actor = AccessContext.UserId`. This is the
//      normal interactive path; the Platform Admin clicks a button
//      in the admin UI and the request travels under their session.
//
//   2. **TOOLUP_ADMIN_TOKEN fallback**. When the caller does NOT
//      hold the role, the existing env-var token gate kicks in:
//      `X-Admin-Token` header must match `TOOLUP_ADMIN_TOKEN`. The
//      actor is read from the optional `X-Actor-UserId` header
//      (falls back to "deployment-admin" when absent). Reserved
//      for emergency / scripted access — the same pattern remains
//      available for any future deployment-wide admin endpoint that
//      a Platform Admin may not be able to reach (e.g. recovery
//      from lockout if the admin list was wiped).
//
//   * Only `PerScopeKeyResolver` supports per-scope destruction. If
//     the registered resolver is not a `PerScopeKeyResolver`, the
//     handler returns 400 with a clear "operation not supported by
//     active resolver" message.
//   * Token is read fresh from the env var on every request —
//     supports rotation without server restart.
//
// On success: audit event `EncryptionKeyDestroyed` is emitted by
// `PerScopeKeyResolver.DestroyKey` with the resolved actor user-id.

[<Literal>]
let private AdminTokenEnvVar = "TOOLUP_ADMIN_TOKEN"

[<Literal>]
let private AdminTokenHeader = "X-Admin-Token"

[<Literal>]
let private ActorHeader = "X-Actor-UserId"

let private readEnvToken () =
    match Environment.GetEnvironmentVariable AdminTokenEnvVar with
    | null
    | "" -> None
    | value -> Some value

let private readHeader (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Headers.TryGetValue name with
    | true, values when values.Count > 0 ->
        let v = values[0]
        if String.IsNullOrEmpty v then None else Some v
    | _ -> None

let private resolveActor (ctx: HttpContext) =
    readHeader ctx ActorHeader |> Option.defaultValue "deployment-admin"

/// Constant-time string comparison. Prevents timing side channels
/// against the admin token.
let private constantTimeEquals (a: string) (b: string) : bool =
    if a.Length <> b.Length then
        false
    else
        let mutable result = 0

        for i in 0 .. a.Length - 1 do
            result <- result ||| (int a[i] ^^^ int b[i])

        result = 0

/// Decide the gate outcome and resolve the audit actor. Returns
/// `Ok actor` when the caller is admitted (either via the Phase 4b
/// `PlatformRole` path or via the Phase 22 token fallback); `Error
/// (statusCode, message)` otherwise. Pure on `HttpContext` reads —
/// the actual handler writes the response after invoking this.
let private resolveGate (ctx: HttpContext) : Result<string, int * string> =
    // Phase 4b — preferred path. Caller holds Platform Admin role.
    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> Some ac
        | _ -> None

    let isAdmin =
        accessContext
        |> Option.map AccessContext.canModifyPlatformConfig
        |> Option.defaultValue false

    if isAdmin then
        Ok accessContext.Value.UserId
    else
        // Token fallback — preserves the Phase 22 emergency / scripted
        // access path. Same error contract as the original handler.
        match readEnvToken () with
        | None ->
            // Endpoint registered, but neither role nor env-token
            // available. Fail closed with a message that names both
            // recovery paths.
            Error(401, "encryption-admin: caller lacks PlatformAdmin role and TOOLUP_ADMIN_TOKEN is not configured")
        | Some envToken ->
            match readHeader ctx AdminTokenHeader with
            | None ->
                Error(
                    401,
                    sprintf "encryption-admin: missing %s header (or assign PlatformAdmin role)" AdminTokenHeader
                )
            | Some headerToken ->
                if constantTimeEquals envToken headerToken then
                    Ok(resolveActor ctx)
                else
                    Error(403, "encryption-admin: invalid admin token")

let private destroyScopeKey (scopeId: string) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        // Step 1: gate. Role-OR-token (Phase 4b commit 4g).
        match resolveGate ctx with
        | Error(statusCode, message) ->
            ctx.Response.StatusCode <- statusCode
            return! ctx.WriteTextAsync message
        | Ok actor ->
            // Step 2: resolve the active key resolver and dispatch on
            // its concrete type.
            let resolverObj = ctx.RequestServices.GetService(typeof<IBlobEncryptionKeyResolver>)

            match resolverObj with
            | null ->
                ctx.Response.StatusCode <- 400

                return!
                    ctx.WriteTextAsync
                        "encryption-admin: no IBlobEncryptionKeyResolver registered (encryption not enabled)"
            | :? PerScopeKeyResolver.PerScopeKeyResolver as perScope ->
                let! result = perScope.DestroyKey(scopeId, actor) |> Async.StartImmediateAsTask

                match result with
                | Ok() ->
                    ctx.Response.StatusCode <- 200

                    return!
                        ctx.WriteJsonAsync {|
                            ScopeId = scopeId
                            Status = "destroyed"
                        |}
                | Error err ->
                    ctx.Response.StatusCode <- 500
                    return! ctx.WriteTextAsync(KeyResolutionError.message err)
            | _ ->
                // Other resolver types (SingleKeyResolver, KMS, custom
                // impls) don't support per-scope destruction. Surface a
                // clear error.
                ctx.Response.StatusCode <- 400

                return!
                    ctx.WriteTextAsync
                        "encryption-admin: active resolver does not support per-scope destruction (only PerScopeKeyResolver does)"
    }

/// Routes table. Mounted by `compose` only when an
/// `IBlobEncryptionKeyResolver` is registered. Apps without
/// encryption never see this surface.
let routes: HttpHandler list = [
    POST >=> routef "/api/_platform/encryption/destroy-scope-key/%s" destroyScopeKey
]