// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.UserSchemaApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.TeamManagement

// ─── Phase 7b — IUserSchemaApi Fable.Remoting handler ─────────────────
//
// Builds a per-request `IUserSchemaApi` from the resolved `AccessContext`
// (scope / userId / role) and the DI-resolved `IUserSchemaStore`. Mirrors
// `FormApiHandler.formApi` / `WebhookApiHandler.webhookApi`.
//
// **Scope isolation + write gating:**
//   - Every call targets the caller's resolved `scopeId` — cross-scope
//     reads/writes are structurally impossible (the store keys on the
//     scope the handler resolves server-side; GP 4).
//   - Reads (List / Get / GetVersion / History) are open to any
//     authenticated caller in the scope.
//   - Writes (Save / Migrate / Delete) require `TeamRoles.canWriteTeamConfig`
//     (Owner / Admin) in Team / MultiTeam modes; Individual /
//     AuthenticatedEphemeral users own their user scope outright.
//   - `Owner` on a submitted schema is server-set from the resolved scope
//     — client-supplied identity is never trusted.

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.Items.TryGetValue "ToolUp.AccessContext" with
    | true, (:? AccessContext as ac) -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private resolveScopeId (ctx: HttpContext) (accessContext: AccessContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> scope.ScopeId
    | _ -> accessContext.UserId

let private isWriteAllowed (ctx: HttpContext) (accessContext: AccessContext) : Async<bool> = async {
    match accessContext.Subject with
    | TeamMember(userId, teamId) ->
        match ctx.RequestServices.GetService(typeof<ITeamStore>) with
        | :? ITeamStore as ts ->
            let! role = ts.GetMemberRole(teamId, userId)

            return
                match role with
                | Some r -> TeamRoles.canWriteTeamConfig r
                | None -> false
        | _ -> return false
    | _ -> return true
}

/// Build the `IUserSchemaApi` handler. Resolves `IUserSchemaStore` +
/// `AccessContext` lazily from DI per request.
let userSchemaApi (ctx: HttpContext) : IUserSchemaApi =
    let store =
        ctx.RequestServices.GetService(typeof<IUserSchemaStore>) :?> IUserSchemaStore

    let accessContext = resolveAccessContext ctx
    let scopeId = resolveScopeId ctx accessContext
    let actor = accessContext.UserId

    let withWrite (f: unit -> Async<Result<'a, UserSchemaError>>) : Async<Result<'a, UserSchemaError>> = async {
        let! allowed = isWriteAllowed ctx accessContext

        if allowed then
            return! f ()
        else
            return Error(UserSchemaUnauthorized "Owner/Admin role required to author schemas")
    }

    {
        ListSchemas =
            fun () -> async {
                let! schemas = store.List scopeId
                return Ok schemas
            }
        GetSchema = fun schemaId -> store.Get(scopeId, schemaId)
        GetSchemaVersion = fun (schemaId, version) -> store.GetVersion(scopeId, schemaId, version)
        GetSchemaHistory =
            fun schemaId -> async {
                let! versions = store.History(scopeId, schemaId)
                return Ok versions
            }
        SaveSchema = fun schema -> withWrite (fun () -> store.Save(scopeId, { schema with Owner = scopeId }, actor))
        MigrateSchema =
            fun (schemaId, migrations) ->
                withWrite (fun () -> store.ExecuteMigration(scopeId, schemaId, migrations, actor))
        DeleteSchema = fun schemaId -> withWrite (fun () -> store.Delete(scopeId, schemaId, actor))
    }