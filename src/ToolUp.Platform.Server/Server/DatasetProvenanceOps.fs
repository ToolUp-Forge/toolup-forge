// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// --- Phase 482 -- dataset declassification (the only label-removal path) --
//
// Privacy-provenance labels are immutable provenance: once a version carries
// a label, no ordinary code can strip it (propagation is automatic in the
// producing executors; forgetting is impossible). The *sole* removal path is
// an explicit, audited admin act -- declassification -- which produces a new,
// unlabelled version copying the labelled version's rows, and records a
// `DatasetDeclassified` row naming the actor + justification (GP 4 / GP 6).
// The original labelled version is left intact (GP 5): declassification adds
// a version, it does not mutate one.

module DatasetProvenance =
    /// Declassify a labelled dataset version: create a new version with the
    /// same rows but no labels, audited. Owner / Admin only -- the caller
    /// enforces the role (GP 4); forge records the actor. A version that
    /// carries no labels is returned unchanged (no new version, no audit --
    /// there is nothing to declassify). `reason` is the operator's
    /// justification, recorded on the audit row.
    let declassify
        (datasets: IDatasetStore)
        (audit: IAuditLog)
        (scopeId: string)
        (datasetId: string)
        (version: int)
        (actor: string)
        (reason: string)
        : Async<Result<DatasetVersionRef, DatasetError>> =
        async {
            match! datasets.GetVersion(scopeId, datasetId, version) with
            | Error e -> return Error e
            | Ok v when List.isEmpty v.Labels ->
                // Nothing labelled — declassify is a no-op on unlabelled data.
                return
                    Ok {
                        ScopeId = scopeId
                        DatasetId = datasetId
                        Version = version
                    }
            | Ok v ->
                // Read every row of the labelled version, then re-create with
                // the same schema + metadata but no labels. `v.Metadata` is
                // already label-free (the store strips the reserved sidecar on
                // read), so the re-created version is unlabelled by construction.
                let rec readAll (offset: int64) (acc: DatasetRow list) = async {
                    let query: DatasetPageQuery = {
                        Offset = offset
                        Limit = 1000
                        Filters = []
                    }

                    match! datasets.ReadPage(scopeId, datasetId, version, query) with
                    | Error e -> return Error e
                    | Ok page ->
                        let acc = acc @ page.Rows
                        let read = offset + int64 (List.length page.Rows)

                        if List.isEmpty page.Rows || read >= page.TotalRows then
                            return Ok acc
                        else
                            return! readAll read acc
                }

                match! readAll 0L [] with
                | Error e -> return Error e
                | Ok rows ->
                    match! datasets.Create(scopeId, datasetId, v.Schema, rows, actor, v.Metadata, Versioned) with
                    | Error e -> return Error e
                    | Ok nv ->
                        do!
                            audit.Record(
                                scopeId,
                                DatasetDeclassified {
                                    Actor = actor
                                    ScopeId = scopeId
                                    DatasetId = datasetId
                                    FromVersion = version
                                    ToVersion = nv.Version
                                    LabelCount = List.length v.Labels
                                    Reason = reason
                                }
                            )

                        return
                            Ok {
                                ScopeId = scopeId
                                DatasetId = datasetId
                                Version = nv.Version
                            }
        }