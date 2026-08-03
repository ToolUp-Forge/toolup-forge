// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.VectorStores.Pgvector.Health

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform
open ToolUp.Platform.IVectorStore

// ─── Phase 507 — Pgvector vector-store health probe ──────────────────
//
// Calls `IVectorStore.ListScopes ()` against the registered store — a
// `SELECT DISTINCT scope` round-trip that proves the pool can reach the
// database, the table still exists, and the role can still read it.
//
// Unlike the in-process stores (whose probe only proves in-memory state
// survived), this one is a genuine liveness signal for the dependency
// the deployment cannot restart itself: the database. It is therefore a
// `Readiness` check — a replica that cannot reach the corpus should be
// taken out of rotation rather than serve ungrounded answers.
//
// Usage from a deployment that wires Pgvector:
//   let vectorStore = PgvectorVectorStore.create connStr options (Some logger)
//   ...
//   |> RAGServerApp.withHealthCheck (Health.create vectorStore)

type PgvectorVectorStoreHealthCheck(store: IVectorStore) =
    interface IHealthCheck with
        member _.Name = "vector_store:pgvector"
        member _.Kind = Readiness

        // A network round-trip, not an in-memory read — a second is a
        // generous ceiling for a pooled `SELECT DISTINCT` and still fast
        // enough that a wedged database is reported rather than waited on.
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Check() = async {
            try
                let! _ = store.ListScopes()
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

let create (store: IVectorStore) : IHealthCheck =
    PgvectorVectorStoreHealthCheck(store) :> IHealthCheck