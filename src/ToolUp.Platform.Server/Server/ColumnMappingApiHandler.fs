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
open ColumnMappingTypes

/// The storage container for this request. Prefers the scope resolved by
/// `ScopeResolutionMiddleware` (cached in `HttpContext.Items`); falls
/// back to a user-derived container for tests / harnesses where the
/// middleware did not run — identical fallback shape to `FileManagement`.
let private resolveScopeId (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s.Container
    | _ -> $"user-{FileManagement.getUserId ctx}"

let columnMappingApi (ctx: HttpContext) : IConversionApi =
    let store =
        ctx.RequestServices.GetService(typeof<IConversionStore>) :?> IConversionStore

    let scopeId = resolveScopeId ctx

    // Phase 218 — dry-run validation dependencies. The catalog resolves
    // the target type's schema authoritatively server-side; the validator
    // (default `MappingDryRunValidator`, registered under the same
    // `EnabledColumnMapping` flag) inspects the mapped shape; the policy
    // (`ServerConfig.MappingDryRun`) decides whether failures block commit.
    let catalog = ctx.RequestServices.GetService(typeof<IDataCatalog>) :?> IDataCatalog

    let validator =
        ctx.RequestServices.GetService(typeof<IMappingDryRunValidator>) :?> IMappingDryRunValidator

    let dryRunPolicy =
        match ctx.RequestServices.GetService(typeof<ServerConfig>) with
        | :? ServerConfig as config -> config.MappingDryRun
        | _ -> WarnOnValidationFailure

    {
        GetConversions = fun fingerprint -> store.GetByFingerprint(scopeId, fingerprint)
        ListConversions = fun () -> store.List scopeId
        SaveConversion = fun conversion -> store.Save(scopeId, conversion)
        DeleteConversion = fun (fingerprint, targetTypeId) -> store.Delete(scopeId, fingerprint, targetTypeId)
        RecordConversion = fun record -> store.SaveRecord(scopeId, record)
        ListConversionRecords = fun () -> store.ListRecords scopeId
        ValidateConversion =
            fun request -> async {
                let conversion = request.Conversion

                match! catalog.GetSchema conversion.TargetTypeId with
                | None -> return Error "The selected data type publishes no schema, so its mapping can't be validated."
                | Some schema ->
                    // Rewrite raw → canonical shape (pure, Core) then validate
                    // the mapped CSV. No write, no `DataType.Process`.
                    let mappedCsv =
                        ColumnMapping.rewriteCsv schema conversion.Mapping conversion.Remediation request.RawCsv

                    let report = validator.Validate(schema, mappedCsv)

                    return
                        Ok {
                            report with
                                TargetTypeId = conversion.TargetTypeId
                                CommitBlocked = MappingDryRunValidator.commitBlocked dryRunPolicy report
                        }
            }
    }