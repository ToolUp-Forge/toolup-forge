// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.VectorisationTypes

open DataManagementTypes
open ProcessedDataTypes
open ToolUp.Platform.VectorKnowledgeTypes

/// Optional vectorisation declaration for a module's processed data.
/// Registered separately from `DataType` — no existing `DataType` instantiation
/// site needs to change. When an `IRetrievalPipeline` is in DI and a matching
/// handler is registered, the platform calls `Vectorise` after each successful
/// `SessionFileStore.AddFile` and enqueues the chunks for background embedding.
///
/// The module owns the extraction logic — only the module author knows which
/// fields of a processed record are semantically meaningful for retrieval.
/// The SDK handles embedding generation, storage, and scoping.
type VectorisationHandler = {
    /// Must match the `DataType.Id` of the module's registered data type.
    DataTypeId: DataTypeId
    /// Extract retrieval-ready text chunks from a processed payload.
    /// Called with the `ProcessedData` returned by `DataType.Process`.
    /// Return an empty list to skip indexing for a particular record.
    Vectorise: ProcessedData -> TextChunk list
    /// Optional: produce a single high-level summary chunk for the
    /// document. When `Some f`, the SDK calls `f processedData` *after*
    /// `Vectorise` returns and indexes the resulting chunk alongside the
    /// per-paragraph chunks under the `_isSummary = "true"` metadata flag.
    /// Retrieval applies a configurable score boost so "what is this
    /// document about?" queries reliably hit the summary chunk first.
    ///
    /// Returns `None` to skip summarisation for a particular record.
    /// Implementations typically call `IAIProvider` with the assembled
    /// chunk text — they receive the same `ProcessedData` so they don't
    /// need to re-derive it.
    ///
    /// `None` (the default) means "no summary handler" — existing
    /// modules need no migration.
    Summarise: (ProcessedData -> Async<TextChunk option>) option
}