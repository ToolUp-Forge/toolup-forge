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
open DataManagementTypes
open ColumnMappingTypes

type IColumnMappingApi = {
    /// All saved mappings for a source-CSV fingerprint (a column-structure
    /// can carry several — one per target type — so a single file can
    /// spawn multiple data objects on re-upload). Empty when none exist.
    [<AllowAnonymous>]
    GetMappings: string -> Async<ColumnMapping list>
    /// Every mapping saved in this scope (admin / review surface).
    [<AllowAnonymous>]
    ListMappings: unit -> Async<ColumnMapping list>
    /// Persist a mapping, keyed by its `(Fingerprint, TargetTypeId)` —
    /// additive: saving a mapping to a new target type for an existing
    /// fingerprint does not overwrite the others.
    [<AllowAnonymous>]
    [<Audit "Custom:ColumnMappingSaved">]
    SaveMapping: ColumnMapping -> Async<Result<unit, string>>
    /// Forget one saved mapping, identified by its fingerprint + target
    /// type.
    [<AllowAnonymous>]
    [<Audit "Custom:ColumnMappingDeleted">]
    DeleteMapping: string * DataTypeId -> Async<Result<unit, string>>
}