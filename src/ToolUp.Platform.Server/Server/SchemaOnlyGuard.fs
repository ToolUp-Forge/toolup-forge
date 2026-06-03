// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SchemaOnlyGuard

open ToolUp.Platform

// ─── SchemaOnly substrate guard ─────────────────────────────────────
//
// Phase 30d. The canonical "is this caller allowed to perform a
// real-row read on this module?" check. Real-data handlers wrap their
// `IDataObjectStore.Get` / `GetVersion` / module-specific blob-read
// invocations with `assertReadAllowed`; a caller whose effective
// `ModulePermission` set on the named module is exactly
// `[SchemaOnly]` (i.e. carries SchemaOnly but no Read / Write / Admin)
// is refused before any real bytes are read AND a
// `SchemaOnlyAccessAttempted` audit event is emitted under the call
// site's substrate label so per-partner refusal rates are dashboardable.
//
// The function is intentionally a free helper (not a decorator
// pattern on `IDataObjectStore`) because the store interface does
// not — and per GP 4 must not — carry caller identity. Identity
// rides on the request via `AccessContext`; handlers thread it into
// the guard explicitly. The guard is therefore a contract handlers
// MUST honour, not a structural decorator. The Phase 30d contract
// test pack (see `IDataCatalogContract.fs` SchemaOnly section)
// exercises the discipline against an instantiated store + catalog.
//
// **Why the check is "exactly `[SchemaOnly]`", not "contains
// SchemaOnly":** Admin / Write / Read all imply SchemaOnly (see
// `ModulePermission.implies`), so a caller who holds `[Read;
// SchemaOnly]` is a legitimate real-data reader and should pass the
// guard. A caller who holds *only* `[SchemaOnly]` is the partner
// sandbox case the guard exists to refuse. The check therefore asks
// "does the caller hold any of Read / Write / Admin?" — if no, the
// guard refuses.

/// Substrate-path labels emitted on `SchemaOnlyAccessAttempted`
/// payloads. Centralised so dashboards group refusals consistently
/// across handlers — a regressed shield is visible in the same
/// counter regardless of which handler invoked it.
module AttemptedPath =
    [<Literal>]
    let DataObjectStoreGet = "IDataObjectStore.Get"

    [<Literal>]
    let DataObjectStoreGetVersion = "IDataObjectStore.GetVersion"

    [<Literal>]
    let DataObjectStoreListObjects = "IDataObjectStore.ListObjects"

    [<Literal>]
    let DataCatalogListObjects = "IDataCatalog.ListObjects"

    [<Literal>]
    let FileManagementGetContent = "IFileManagementApi.GetFileContent"

    [<Literal>]
    let EntityStoreGet = "IEntityStore.Get"

/// True when the caller holds `ModulePermission.SchemaOnly` on
/// `moduleName` AND no real-data grant (Read / Write / Admin).
/// Returns `false` when `ModulePermissions` is empty (unrestricted)
/// or when any real-data grant is present.
let isSchemaOnlyCaller (ctx: AccessContext) (moduleName: string) : bool =
    if ctx.ModulePermissions.IsEmpty then
        // Empty map = unrestricted (the pre-Phase-4 default; see
        // `AccessContext.hasPermission`). A guard against an
        // unrestricted caller would lock the deployment out of its
        // own data — fail-open here is the correct policy.
        false
    else
        match Map.tryFind moduleName ctx.ModulePermissions with
        | None -> false
        | Some perms ->
            let hasRealReadGrant =
                perms
                |> List.exists (fun granted -> ModulePermission.implies granted ModulePermission.Read)

            let hasSchemaOnly = perms |> List.contains ModulePermission.SchemaOnly

            hasSchemaOnly && not hasRealReadGrant

/// Refuses real-data reads from `SchemaOnly`-only callers. Emits a
/// `SchemaOnlyAccessAttempted` audit event before returning
/// `Error "schema-only access refused"` so the caller's handler can
/// translate to its protocol-specific refusal (HTTP 403, Remoting
/// fault, etc.). Returns `Ok ()` for every other caller — the
/// real-data read proceeds unchanged.
///
/// `path` is the substrate label (see `AttemptedPath` module); `resource`
/// is the best-effort identifier of the requested object (object id,
/// file name, scope-qualified key — whatever the handler has handy).
/// Pass `""` when no identifier is available at the gate point.
///
/// Audit emission failures are swallowed (per `IAuditLog` contract —
/// audit is best-effort, never blocks the primary operation), but the
/// refusal still fires: a SchemaOnly caller must never see real data
/// even when the audit pipeline is down.
let assertReadAllowed
    (ctx: AccessContext)
    (moduleName: string)
    (path: string)
    (resource: string)
    (auditLog: IAuditLog)
    (scopeId: string)
    : Async<Result<unit, string>> =
    async {
        if isSchemaOnlyCaller ctx moduleName then
            let payload: SchemaOnlyAccessAttemptedPayload = {
                UserId = ctx.UserId
                ModuleName = moduleName
                AttemptedPath = path
                AttemptedResource = resource
            }

            try
                do! auditLog.Record(scopeId, SchemaOnlyAccessAttempted payload)
            with _ ->
                // Audit best-effort — swallow per IAuditLog contract.
                // The refusal still fires regardless.
                ()

            return Error "schema-only access refused"
        else
            return Ok()
    }