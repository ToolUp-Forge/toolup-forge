// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open DataManagementTypes
open ColumnMappingTypes

/// Server-side store for the conversion subsystem: the reusable
/// **conversion recipes** (`Conversion`, keyed by `(fingerprint,
/// targetType)`) and the per-object **provenance records**
/// (`ConversionRecord`, keyed by produced file). Backs the mapping-aware
/// Data Manager (`ClientConfig.DataManager = MappingDataManager` +
/// `ServerConfig.ColumnMapping = EnabledColumnMapping`). The default
/// implementation (`ColumnMappingStore.create`) wraps `IDataObjectStore`;
/// the `scopeId` becomes the storage container, so cross-scope reads are
/// structurally impossible.
///
/// Portability audit (GP 12): identity by value (strings + immutable
/// records), async at every boundary, failures as `Result<_, string>`
/// data, stateless between calls, single-scope (no cross-shard ordering
/// claim), no timing surface. Contract-tested by `IConversionStoreContract`.
type IConversionStore =
    /// Persist a conversion recipe, keyed by its `(Fingerprint,
    /// TargetTypeId)` — additive across target types, so one
    /// column-structure can hold several recipes (a single file → several
    /// data objects).
    abstract Save: scopeId: string * conversion: Conversion -> Async<Result<unit, string>>
    /// All saved conversion recipes for a source-CSV fingerprint. Empty
    /// when none exist for that column-structure in this scope.
    abstract GetByFingerprint: scopeId: string * fingerprint: string -> Async<Conversion list>
    /// Every conversion recipe saved in this scope.
    abstract List: scopeId: string -> Async<Conversion list>
    /// Forget one saved conversion recipe, identified by fingerprint +
    /// target type. Idempotent.
    abstract Delete: scopeId: string * fingerprint: string * targetTypeId: DataTypeId -> Async<Result<unit, string>>
    /// Persist the provenance record for one produced data object (keyed
    /// by `ProducedFile`; overwrites on re-record).
    abstract SaveRecord: scopeId: string * record: ConversionRecord -> Async<Result<unit, string>>
    /// Every conversion-provenance record in this scope.
    abstract ListRecords: scopeId: string -> Async<ConversionRecord list>