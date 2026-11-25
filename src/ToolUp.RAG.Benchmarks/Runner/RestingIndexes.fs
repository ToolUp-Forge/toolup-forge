// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.Benchmarks.RestingIndexes

open System
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex

// ─── Seeded indexes, at rest ────────────────────────────────────────
//
// The in-process retrieval indexes persist on a timer: a mutation marks its
// scope dirty and returns, and a background pass writes the whole scope back,
// reading the blob before it writes it. A load cell seeds thousands of chunks,
// so when the seed returns, passes are still owed, and on a busy machine they
// run seconds into the measured rounds. A pass's read landing after the
// counters are reset is counted as a retrieval's read: one read in a thousand
// retrievals, on some runs and not others.
//
// So the seed's persistence is finished before anything is measured: each
// index is disposed (its flush loop stops, a pass already writing is waited
// for, and the dirty set is written once more) and opened again over the same
// storage. The reopened index loads the scope from its blob at the first
// access, which the warm-up pays, exactly as a restarted replica would.

/// How a load cell opens its retrieval indexes over its blob storage.
type IndexOpeners = {
    /// Opens the vector store again over the same storage, or `None` for a
    /// store that does not persist through the blob storage (a database-backed
    /// one), which is kept open as it is.
    ReopenVector: (unit -> IVectorStore) option
    /// Opens the sparse index again over the same storage.
    OpenSparse: unit -> ISparseIndex
}

let private close (index: obj) =
    match index with
    | :? IDisposable as disposable -> disposable.Dispose()
    | _ -> ()

/// Finish the seed's persistence and reopen the indexes: when this returns,
/// no write owed by the seed is in flight or pending, so every blob read
/// counted after it is a read the measured work made.
let atRest (openers: IndexOpeners) (vector: IVectorStore) (sparse: ISparseIndex) : IVectorStore * ISparseIndex =
    close sparse
    let reopenedSparse = openers.OpenSparse()

    match openers.ReopenVector with
    | Some reopen ->
        close vector
        reopen (), reopenedSparse
    | None -> vector, reopenedSparse