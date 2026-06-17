// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

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
    /// Persist (create or overwrite) a mapping, keyed by its `Fingerprint`.
    abstract Save: scopeId: string * mapping: ColumnMapping -> Async<Result<unit, string>>
    /// Look up a saved mapping by source-CSV fingerprint. `None` when no
    /// mapping exists for that column-structure in this scope.
    abstract Get: scopeId: string * fingerprint: string -> Async<ColumnMapping option>
    /// Every mapping saved in this scope.
    abstract List: scopeId: string -> Async<ColumnMapping list>
    /// Forget a saved mapping by fingerprint. Succeeds (idempotently)
    /// even when no mapping exists for the fingerprint.
    abstract Delete: scopeId: string * fingerprint: string -> Async<Result<unit, string>>