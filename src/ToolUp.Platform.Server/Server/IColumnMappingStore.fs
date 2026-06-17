// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open DataManagementTypes
open ColumnMappingTypes

/// Server-side store for reusable CSV column mappings, keyed within a
/// storage scope by the source CSV's column-structure fingerprint. Backs
/// the mapping-aware Data Manager (`ClientConfig.DataManager =
/// MappingDataManager` + `ServerConfig.ColumnMapping = EnabledColumnMapping`).
/// The default implementation (`ColumnMappingStore.create`) wraps
/// `IDataObjectStore`; the `scopeId` becomes the storage container, so
/// cross-scope reads are structurally impossible.
///
/// Portability audit (GP 12): identity by value (string `scopeId` +
/// `fingerprint` + the immutable `ColumnMapping` record), async at every
/// boundary, failures as `Result<_, string>` data, stateless between
/// calls, single-scope (no cross-shard ordering claim), no timing
/// surface. Contract-tested by `IColumnMappingStoreContract`.
type IColumnMappingStore =
    /// Persist a mapping, keyed by its `(Fingerprint, TargetTypeId)` —
    /// additive across target types, so one column-structure can hold
    /// several mappings (a single file → several data objects).
    abstract Save: scopeId: string * mapping: ColumnMapping -> Async<Result<unit, string>>
    /// All saved mappings for a source-CSV fingerprint. Empty when none
    /// exist for that column-structure in this scope.
    abstract GetByFingerprint: scopeId: string * fingerprint: string -> Async<ColumnMapping list>
    /// Every mapping saved in this scope.
    abstract List: scopeId: string -> Async<ColumnMapping list>
    /// Forget one saved mapping, identified by fingerprint + target type.
    /// Succeeds (idempotently) even when no such mapping exists.
    abstract Delete: scopeId: string * fingerprint: string * targetTypeId: DataTypeId -> Async<Result<unit, string>>