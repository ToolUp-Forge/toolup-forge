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
//      audit actor is the verified identity `"token:deployment-admin"`
//      — the token authenticates the deployment, not a person. The
//      optional `X-Actor-UserId` header is self-asserted, so it is
//      recorded only as a `(claimed: …)` annotation, never as the
//      actor itself (Gap audit 2026-06-12 Auth G5 — pre-hardening the
//      header value WAS the actor, giving spoofable attribution on
//      the most destructive op in the SDK). Reserved for emergency /
//      scripted access — the same pattern remains available for any
//      future deployment-wide admin endpoint that a Platform Admin
//      may not be able to reach (e.g. recovery from lockout if the
//      admin list was wiped).
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
let private AdminTokenEnvVar = ConfigKeys.Names.adminToken

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

/// Resolve the audit actor for the token-gated path. The verified
/// identity is the deployment token, so the actor is always
/// `"token:deployment-admin"`; the self-asserted `X-Actor-UserId`
/// header rides along as a `(claimed: …)` annotation inside the
/// existing audit field — sanitised so a hostile header can't inject
/// control characters or path fragments into the audit trail.
let private resolveActor (ctx: HttpContext) =
    match readHeader ctx ActorHeader with
    | None -> "token:deployment-admin"
    | Some claimed ->
        match Auth.IdentitySanitiser.sanitiseScopeId claimed with
        | Ok v -> $"token:deployment-admin (claimed: {v})"
        | Error _ -> "token:deployment-admin (claimed: <rejected by sanitiser>)"

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

// Phase 232 — why the gate failed. The handler maps EVERY failure to a
// uniform 403 (so the response never discloses whether a token is
// configured or which part failed — pre-232 the missing-header case
// returned 401, leaking that a token IS set), but distinguishes the
// reason internally for logging + throttling.
type private GateFailure =
    /// No Platform-Admin role AND no `TOOLUP_ADMIN_TOKEN` configured.
    | NoCredentials
    /// A token is configured but the `X-Admin-Token` header is absent.
    | TokenMissing
    /// A token is configured and the header is present but wrong.
    | TokenInvalid

/// Phase 232 — per-source-IP throttle on token attempts. In-process
/// (single-instance) brute-force friction layered over the high-entropy
/// token; not the primary control. `recordTokenFailure` counts a failed
/// token attempt within a sliding window; `isThrottled` blocks once the
/// cap is reached. Successful auth does not reset the window (the cap is
/// on failures only).
[<Literal>]
let private maxTokenFailures = 5

[<Literal>]
let private windowMinutes = 5.0

let private tokenFailures =
    System.Collections.Concurrent.ConcurrentDictionary<string, int * DateTime>()

let private clientIp (ctx: HttpContext) : string =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | ip -> string ip

let private isThrottled (ip: string) : bool =
    match tokenFailures.TryGetValue ip with
    | true, (count, windowStart) when (DateTime.UtcNow - windowStart).TotalMinutes < windowMinutes ->
        count >= maxTokenFailures
    | _ -> false

let private recordTokenFailure (ip: string) : unit =
    tokenFailures.AddOrUpdate(
        ip,
        (fun _ -> (1, DateTime.UtcNow)),
        (fun _ (count, windowStart) ->
            if (DateTime.UtcNow - windowStart).TotalMinutes < windowMinutes then
                (count + 1, windowStart)
            else
                (1, DateTime.UtcNow))
    )
    |> ignore

let private resolveLogger (ctx: HttpContext) : ILogger option =
    match ctx.RequestServices.GetService(typeof<ILogger>) with
    | :? ILogger as l -> Some l
    | _ -> None

/// Decide the gate outcome and resolve the audit actor. Returns
/// `Ok actor` when the caller is admitted (either via the Phase 4b
/// `PlatformRole` path or via the Phase 22 token fallback); `Error
/// failure` otherwise (the handler maps every failure to a uniform 403).
/// Pure on `HttpContext` reads — the handler writes the response.
let private resolveGate (ctx: HttpContext) : Result<string, GateFailure> =
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
        // access path.
        match readEnvToken () with
        | None -> Error NoCredentials
        | Some envToken ->
            match readHeader ctx AdminTokenHeader with
            | None -> Error TokenMissing
            | Some headerToken ->
                if constantTimeEquals envToken headerToken then
                    Ok(resolveActor ctx)
                else
                    Error TokenInvalid

let private destroyScopeKey (scopeId: string) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let ip = clientIp ctx
        let logger = resolveLogger ctx

        // Phase 232 — per-IP throttle on token brute-force.
        if isThrottled ip then
            logger
            |> Option.iter (fun l ->
                l.Warn(sprintf "[encryption-admin] throttled: too many failed token attempts from %s" ip))

            ctx.Response.StatusCode <- 429
            return! ctx.WriteTextAsync "encryption-admin: too many attempts; try again later"
        else
            // Step 1: gate. Role-OR-token (Phase 4b commit 4g).
            match resolveGate ctx with
            | Error failure ->
                // Phase 232 — log + throttle the attempt and respond with a
                // UNIFORM 403 that discloses neither whether a token is
                // configured nor which part failed.
                match failure with
                | TokenMissing
                | TokenInvalid ->
                    recordTokenFailure ip

                    logger
                    |> Option.iter (fun l ->
                        l.Warn(
                            sprintf
                                "[encryption-admin] failed token attempt (%A) from %s on destroy-scope-key/%s"
                                failure
                                ip
                                scopeId
                        ))
                | NoCredentials ->
                    logger
                    |> Option.iter (fun l ->
                        l.Warn(
                            sprintf
                                "[encryption-admin] rejected (no PlatformAdmin role and no admin token configured) from %s"
                                ip
                        ))

                ctx.Response.StatusCode <- 403

                return!
                    ctx.WriteTextAsync
                        "encryption-admin: forbidden (assign the PlatformAdmin role or present a valid admin token)"
            | Ok actor ->
                // Phase 232 — log token-gated access (the canonical
                // EncryptionKeyDestroyed audit fires on the destroy itself).
                if actor.StartsWith "token:" then
                    logger
                    |> Option.iter (fun l ->
                        l.Info(sprintf "[encryption-admin] token-gated access granted from %s, actor=%s" ip actor))

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
                    // impls) don't support per-scope destruction.
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