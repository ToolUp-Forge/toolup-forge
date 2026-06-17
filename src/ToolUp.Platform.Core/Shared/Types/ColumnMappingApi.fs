// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// ToolUp.Remoting contract for the reusable column-mapping store. The
/// mapping-aware Data Manager fetches a saved mapping by CSV
/// `Fingerprint` on upload, and persists the confirmed mapping so the
/// same column-structure auto-applies next time.
///
/// Data-path methods are dispatcher-anonymous by design — exactly like
/// `FileManagementApi`: `StorageScope` isolation is the enforcement, so
/// Anonymous-mode deployments read/write mappings within their own
/// session scope. Mounted only when `ServerConfig.ColumnMapping =
/// EnabledColumnMapping`.
module ColumnMappingApi

// Auth/audit attributes (`AllowAnonymous`, `Audit`) live under
// `namespace ToolUp.Platform` (AuthAttributes.fs); this is a top-level
// module, so open it explicitly.
open ToolUp.Platform
open ColumnMappingTypes

type IColumnMappingApi = {
    /// Look up a saved mapping by source-CSV fingerprint. `None` when no
    /// mapping exists for that column-structure in this scope.
    [<AllowAnonymous>]
    GetMapping: string -> Async<ColumnMapping option>
    /// Every mapping saved in this scope (admin / review surface).
    [<AllowAnonymous>]
    ListMappings: unit -> Async<ColumnMapping list>
    /// Persist (create or overwrite) a mapping, keyed by its
    /// `Fingerprint`.
    [<AllowAnonymous>]
    [<Audit "Custom:ColumnMappingSaved">]
    SaveMapping: ColumnMapping -> Async<Result<unit, string>>
    /// Forget a saved mapping by fingerprint.
    [<AllowAnonymous>]
    [<Audit "Custom:ColumnMappingDeleted">]
    DeleteMapping: string -> Async<Result<unit, string>>
}