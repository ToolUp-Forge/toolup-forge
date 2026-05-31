module ToolUp.Platform.PlatformAdminApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.RemotingHelpers

// ─── Phase 4b — PlatformAdminApi handler ─────────────────────────────
//
// Server-side implementation of the `PlatformAdminApi` Fable.Remoting
// record (defined in `Core/Shared/PlatformAdminApi.fs`). Resolves
// `IPlatformAdminStore` and `AccessContext` lazily from DI per request
// — same idiom as `HealthMonitorApiHandler` / `WebhookApiHandler` /
// `ConfigHandler`.
//
// **Gating.** `IsPlatformAdmin` reads `AccessContext.PlatformRole`
// directly — no store call. `ListPlatformAdmins` is visible to every
// authenticated caller (the path is `/api/*` and so already
// authentication-gated by `AuthEnforcementMiddleware` in non-Anonymous
// modes; Anonymous mode returns the empty list because the store is
// empty in any deployment that hasn't bootstrapped a Platform Admin).
// `AssignPlatformAdmin` and `RevokePlatformAdmin` are gated on
// `AccessContext.canModifyPlatformConfig` (the predicate added in
// commit 4a). Failure short-circuits with `Error "platform admin role
// required"` — same wording the doc-comment on
// `canModifyPlatformConfig` cites so client-side error banners render
// uniformly.

/// Auth-context resolver mirroring `HealthMonitorApiHandler.resolveAccessContext`
/// / `WebhookApiHandler.accessContext`. Falls back to a synthetic
/// unrestricted context for tests that bypass `ScopeResolutionMiddleware`
/// — production paths always populate the scoped DI service via the
/// middleware.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Resolve `IPlatformAdminStore` from DI. Returns `Error` when the
/// service is unavailable — should never happen in production because
/// `compose` registers the store unconditionally, but tests that bypass
/// `compose` may not register it.
let private resolveStore (ctx: HttpContext) : Result<IPlatformAdminStore, string> =
    match ctx.RequestServices.GetService(typeof<IPlatformAdminStore>) with
    | :? IPlatformAdminStore as store -> Ok store
    | _ -> Error "platform admin store not available"

/// Resolve `IPlatformRuntimeConfigStore` from DI. Same shape as
/// `resolveStore` — should never fail in production.
let private resolveRuntimeConfig (ctx: HttpContext) : Result<IPlatformRuntimeConfigStore, string> =
    match ctx.RequestServices.GetService(typeof<IPlatformRuntimeConfigStore>) with
    | :? IPlatformRuntimeConfigStore as store -> Ok store
    | _ -> Error "platform runtime config store not available"

/// Build the `PlatformAdminApi` handler. Services are resolved lazily
/// from `ctx.RequestServices` at request time — mirrors the existing
/// `platformApiHandler` / `healthMonitorApi` pattern.
let platformAdminApi (ctx: HttpContext) : PlatformAdminApi =
    let accessContext = resolveAccessContext ctx

    let withGate (work: IPlatformAdminStore -> Async<Result<unit, string>>) = async {
        if not (AccessContext.canModifyPlatformConfig accessContext) then
            return Error "platform admin role required"
        else
            match resolveStore ctx with
            | Ok store -> return! work store
            | Error msg -> return Error msg
    }

    {
        IsPlatformAdmin = fun () -> async { return accessContext.PlatformRole = Some PlatformRole.PlatformAdmin }

        ListPlatformAdmins =
            fun () -> async {
                match resolveStore ctx with
                | Ok store -> return! store.ListPlatformAdmins()
                | Error _ ->
                    // Test-bypass path: the store isn't registered.
                    // Return an empty list rather than failing — the
                    // call is read-only and the absence of the store
                    // means no admins can possibly exist.
                    return []
            }

        AssignPlatformAdmin =
            fun targetUserId -> withGate (fun store -> store.AssignPlatformAdmin(accessContext.UserId, targetUserId))

        RevokePlatformAdmin =
            fun targetUserId -> withGate (fun store -> store.RevokePlatformAdmin(accessContext.UserId, targetUserId))

        GetPlatformKnowledgeBase =
            fun () -> async {
                match resolveRuntimeConfig ctx with
                | Ok rc -> return! rc.GetPlatformKnowledgeBase()
                | Error _ ->
                    // Test-bypass path: return the safe default.
                    return NoPlatformKnowledgeBase
            }

        SetPlatformKnowledgeBase =
            fun mode -> async {
                if not (AccessContext.canModifyPlatformConfig accessContext) then
                    return Error "platform admin role required"
                else
                    match resolveRuntimeConfig ctx with
                    | Ok rc -> return! rc.SetPlatformKnowledgeBase mode
                    | Error msg -> return Error msg
            }
    }