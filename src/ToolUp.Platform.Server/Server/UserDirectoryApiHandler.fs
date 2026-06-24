module ToolUp.Platform.UserDirectoryApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform

// ─── 0.5.7 — IUserDirectoryApi handler ──────────────────────────────
//
// Server-side implementation of the `IUserDirectoryApi` Fable.Remoting
// record (defined in `Core/Shared/Types/UserDirectoryTypes.fs`).
//
// Resolves `IUserDirectory` lazily from DI per request — mirrors the
// `PlatformAdminApiHandler` / `HealthMonitorApiHandler` idiom. When no
// `IUserDirectory` is registered (the substrate is optional — deployments
// without a directory companion register nothing), every `SearchUsers`
// call short-circuits to `Ok []`. The client typeahead degrades to a
// plain text input with no suggestions, which is exactly the pre-0.5.7
// behaviour.
//
// **Gating.** Authenticated subjects only — anonymous callers get
// `Error "authentication required"`. The standard
// `AuthEnforcementMiddleware` short-circuits anonymous requests to
// `/api/*` paths in non-Anonymous Modes anyway; this explicit check
// is the belt-and-braces for Anonymous-mode deployments where the
// middleware permits anonymous traffic. Anonymous Mode deployments
// typically don't wire a directory companion at all (the use case is
// invite-by-name in a multi-user team UI, which doesn't exist there),
// so the explicit check is mostly cosmetic — but cheap.
//
// The query is sanity-clamped here too: empty / whitespace-only
// strings short-circuit to `Ok []` without hitting the substrate, and
// `maxResults` is clamped to `[1, 50]` so a buggy client can't ask for
// 1000 matches. The companion is expected to enforce its own minimum
// prefix length (Microsoft Graph rate-limits aggressively below 3
// chars).

/// Resolve the optional `IUserDirectory` from DI. Returns `None` when
/// no companion is registered — the handler's well-defined empty-list
/// response keeps the typeahead well-behaved.
let private resolveDirectory (ctx: HttpContext) : IUserDirectory option =
    match ctx.RequestServices.GetService(typeof<IUserDirectory>) with
    | :? IUserDirectory as directory -> Some directory
    | _ -> None

/// Resolve the per-request `AccessContext` (populated by
/// `ScopeResolutionMiddleware`). Falls back to a synthetic anonymous
/// context for test paths that bypass the middleware — mirrors the
/// `PlatformAdminApiHandler.resolveAccessContext` shape.
let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

/// Construct the `IUserDirectoryApi` record per-request. Captures the
/// `HttpContext` for lazy DI resolution inside each method.
let userDirectoryApi (ctx: HttpContext) : IUserDirectoryApi = {
    SearchUsers =
        fun (query, maxResults) -> async {
            // Anonymous gating — refuse before touching the substrate.
            let accessContext = resolveAccessContext ctx

            match accessContext.Subject with
            | AnonymousSession _ -> return Error "authentication required"
            | _ ->
                let trimmed =
                    match query with
                    | null -> ""
                    | s -> s.Trim()

                if trimmed = "" then
                    return Ok []
                else
                    let clampedMax =
                        if maxResults < 1 then 1
                        elif maxResults > 50 then 50
                        else maxResults

                    match resolveDirectory ctx with
                    | None ->
                        // No companion registered — the typeahead degrades to
                        // a plain text input. Caller renders no suggestions.
                        return Ok []
                    | Some directory -> return! directory.SearchUsers(trimmed, clampedMax)
        }

    ResolveUsers =
        fun ids -> async {
            // Same anonymous gate as SearchUsers — no anonymous directory
            // enumeration, forward or reverse.
            let accessContext = resolveAccessContext ctx

            match accessContext.Subject with
            | AnonymousSession _ -> return Error "authentication required"
            | _ ->
                // De-dup + drop blanks before touching the substrate; an
                // all-blank / empty list short-circuits to Ok [] with no
                // provider round-trip. The companion clamps batch size to
                // the provider's per-request ceiling.
                let cleaned =
                    ids
                    |> List.choose (fun id ->
                        if System.String.IsNullOrWhiteSpace id then
                            None
                        else
                            Some(id.Trim()))
                    |> List.distinct

                if List.isEmpty cleaned then
                    return Ok []
                else
                    match resolveDirectory ctx with
                    | None ->
                        // No companion registered — nothing to resolve. The
                        // caller renders the raw ids, exactly as before.
                        return Ok []
                    | Some directory -> return! directory.ResolveUsers cleaned
        }
}