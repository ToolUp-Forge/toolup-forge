// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `IColumnMappingApi` handler. Resolves the `IColumnMappingStore` and
/// the request's `StorageScope` from DI / `HttpContext.Items`, then maps
/// every method through to the store keyed by the scope's container —
/// the same scope-isolation posture as `FileManagement.fileManagementApi`.
/// Mounted by `BuildRouteHandlers` only when `ColumnMapping =
/// EnabledColumnMapping`.
module ToolUp.Platform.ColumnMappingApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ColumnMappingApi

/// The storage container for this request. Prefers the scope resolved by
/// `ScopeResolutionMiddleware` (cached in `HttpContext.Items`); falls
/// back to a user-derived container for tests / harnesses where the
/// middleware did not run — identical fallback shape to `FileManagement`.
let private resolveScopeId (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.Container
    | _ -> $"user-{FileManagement.getUserId ctx}"

let columnMappingApi (ctx: HttpContext) : IColumnMappingApi =
    let store =
        ctx.RequestServices.GetService(typeof<IColumnMappingStore>) :?> IColumnMappingStore

    let scopeId = resolveScopeId ctx

    {
        GetMapping = fun fingerprint -> store.Get(scopeId, fingerprint)
        ListMappings = fun () -> store.List scopeId
        SaveMapping = fun mapping -> store.Save(scopeId, mapping)
        DeleteMapping = fun fingerprint -> store.Delete(scopeId, fingerprint)
    }