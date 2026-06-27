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

type IConversionApi = {
    /// All saved conversion recipes for a source-CSV fingerprint (a
    /// column-structure can carry several — one per target type — so a
    /// single file can spawn multiple data objects on re-upload). Empty
    /// when none exist.
    [<AllowAnonymous>]
    GetConversions: string -> Async<Conversion list>
    /// Every conversion recipe saved in this scope (admin / review surface).
    [<AllowAnonymous>]
    ListConversions: unit -> Async<Conversion list>
    /// Persist a conversion recipe, keyed by its `(Fingerprint,
    /// TargetTypeId)` — additive: saving a recipe to a new target type for
    /// an existing fingerprint does not overwrite the others.
    [<AllowAnonymous>]
    [<Audit "Custom:ConversionSaved">]
    SaveConversion: Conversion -> Async<Result<unit, string>>
    /// Forget one saved conversion recipe, identified by fingerprint +
    /// target type.
    [<AllowAnonymous>]
    [<Audit "Custom:ConversionDeleted">]
    DeleteConversion: string * DataTypeId -> Async<Result<unit, string>>
    /// Record the provenance of one ingested data object — the conversion
    /// + remediation that produced it. Marks the conversion as part of the
    /// ingestion stage; emits a `DataConverted` audit event when an audit
    /// log is composed.
    [<AllowAnonymous>]
    [<Audit "Custom:DataConverted">]
    RecordConversion: ConversionRecord -> Async<Result<unit, string>>
    /// Every conversion-provenance record in this scope (joined to the
    /// file list to mark which objects were converted, with their steps).
    [<AllowAnonymous>]
    ListConversionRecords: unit -> Async<ConversionRecord list>
    /// Dry-run validate a confirmed conversion BEFORE commit: rewrite the
    /// raw CSV into the target type's canonical schema shape and run it
    /// through the server's schema validator, returning a per-row /
    /// per-cell error report as data — no write, no `DataType.Process`
    /// (GP 12.3). `Error` only for a non-validatable target (unknown type
    /// / no published schema).
    [<AllowAnonymous>]
    ValidateConversion: DryRunValidationRequest -> Async<Result<DryRunReport, string>>
}