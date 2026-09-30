// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.RAG.VectorStores.Pgvector.Health

open System
open ToolUp.Platform.HealthChecks
open ToolUp.Platform
open ToolUp.Platform.IVectorStore
open ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore

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
// Phase 892 adds the store's POSTURE to the probe: the configured index
// kind and whether it is present on the table, the search width in
// force, whether iterative scanning is applied, and the extension
// version (`describe`). A posture problem the store is running around —
// iterative scanning requested but unsupported, a configured approximate
// index absent from the table, an extension upgraded since `create` — is
// reported `Degraded` with the full description; it does not take the
// replica out of rotation, because search still answers correctly.
// `HealthResult.Healthy` carries no message, so on a healthy probe the
// same description is available from `report`.
//
// Usage from a deployment that wires Pgvector:
//   let vectorStore = PgvectorVectorStore.createTuned connStr options PgvectorTuning.recommended (Some logger)
//   ...
//   |> RAGServerApp.withHealthCheck (Health.create vectorStore)
//   |> RAGServerApp.withConfigValidator (Health.validator vectorStore)

/// The configured approximate index, as an operator reads it.
let private indexKind (options: PgvectorOptions) : string =
    match options.AnnIndex with
    | NoAnnIndex -> "none (exact scan)"
    | HnswAnnIndex(m, efConstruction) -> sprintf "hnsw (m=%d, ef_construction=%d)" m efConstruction
    | IvfFlatAnnIndex lists -> sprintf "ivfflat (lists=%d)" lists

/// The access method a configured index should show in `pg_am`.
let private expectedMethod (options: PgvectorOptions) : string option =
    match options.AnnIndex with
    | NoAnnIndex -> None
    | HnswAnnIndex _ -> Some "hnsw"
    | IvfFlatAnnIndex _ -> Some "ivfflat"

/// The search width in force, and where it comes from.
let private searchWidth (d: PgvectorDiagnostics) : string =
    let tuned setting =
        match d.Tuning.SearchWidth with
        | Some w -> sprintf "%s = %d (per query)" setting w
        | None -> sprintf "%s = database default" setting

    let withDatabaseValue (setting: string) (value: string option) =
        match d.Tuning.SearchWidth, value with
        | None, Some v -> sprintf "%s = %s (database default)" setting v
        | _ -> tuned setting

    match d.Options.AnnIndex with
    | NoAnnIndex -> "n/a (exact scan)"
    | HnswAnnIndex _ -> withDatabaseValue "hnsw.ef_search" d.DatabaseHnswEfSearch
    | IvfFlatAnnIndex _ -> withDatabaseValue "ivfflat.probes" d.DatabaseIvfFlatProbes

/// Whether iterative scanning is applied, and why not when it is not.
let private iterativeScan (d: PgvectorDiagnostics) : string =
    match d.Options.AnnIndex, d.Tuning.IterativeScan with
    | NoAnnIndex, _ -> "n/a (exact scan)"
    | _, false -> "off"
    | _, true when ExtensionVersion.supportsIterativeScan d.ExtensionVersionAtCreate -> "relaxed_order (per query)"
    | _, true -> sprintf "requested, NOT applied (needs pgvector %O or later)" ExtensionVersion.iterativeScanSince

/// Phase 892 — the one-line posture report: extension version, configured
/// index and whether it is present, search width in force, iterative
/// scanning, statement ordering, the short-page fallback, multi-scope
/// concurrency and the estimated row count.
let describe (d: PgvectorDiagnostics) : string =
    let present =
        match expectedMethod d.Options with
        | None -> ""
        | Some m when List.contains m d.AnnIndexMethods -> " [present]"
        | Some _ -> " [ABSENT from the table]"

    let ordering =
        match d.Options.AnnIndex with
        | NoAnnIndex -> "exact, total order in SQL"
        | _ -> "index-ordered, page re-sorted"

    sprintf
        "pgvector %s; table %s; index: %s%s; search width: %s; iterative scan: %s; ordering: %s; exact fallback on a short page: %s; multi-scope concurrency: %d; rows ≈ %d"
        (d.ExtensionVersion |> Option.defaultValue "not installed")
        d.Options.Table
        (indexKind d.Options)
        present
        (searchWidth d)
        (iterativeScan d)
        ordering
        (if d.Tuning.ExactFallbackOnShortPage then "on" else "off")
        d.Tuning.MaxSearchConcurrency
        d.RowEstimate

/// Phase 892 — judge a posture read. `Degraded` (with the full `describe`
/// line) when the store is running around a problem an operator should
/// fix: iterative scanning requested but not applied, a configured
/// approximate index absent from the table, or an extension version that
/// changed since `create` (the per-query settings were chosen against the
/// old one — restart to re-read it). `Healthy` otherwise.
let assess (d: PgvectorDiagnostics) : HealthResult =
    let problems = [
        match d.Options.AnnIndex with
        | NoAnnIndex -> ()
        | _ ->
            if
                d.Tuning.IterativeScan
                && not (ExtensionVersion.supportsIterativeScan d.ExtensionVersionAtCreate)
            then
                "iterative scanning is requested but not applied"

            match expectedMethod d.Options with
            | Some m when not (List.contains m d.AnnIndexMethods) ->
                sprintf "the configured %s index is absent from the table, so every search is an exact scan" m
            | _ -> ()

        match d.ExtensionVersionAtCreate, d.ExtensionVersion with
        | Some atCreate, Some now when atCreate <> now ->
            sprintf "the vector extension moved from %s to %s since create — restart to re-read it" atCreate now
        | _ -> ()
    ]

    if List.isEmpty problems then
        Healthy
    else
        Degraded(sprintf "%s. %s" (String.concat "; " problems) (describe d))

/// Phase 892 — the preflight verdict: a `Warning` when no approximate
/// index is configured and the table's estimated row count is above
/// `PgvectorTuning.ExactScanWarningRows` (every search is then an exact
/// scan of the requested scopes); `Ok` otherwise.
let exactScanVerdict (d: PgvectorDiagnostics) : ConfigValidation.ValidationResult =
    match d.Options.AnnIndex with
    | NoAnnIndex when d.RowEstimate > d.Tuning.ExactScanWarningRows ->
        ConfigValidation.ValidationResult.Warning(
            sprintf
                "[PgvectorVectorStore] Table '%s' holds about %d rows and no approximate index is configured (AnnIndex = NoAnnIndex), so every search is an exact scan of the requested scopes — above the %d-row threshold this stops being cheap. Opt into HnswAnnIndex with PgvectorTuning.recommended (see the companion README), or raise ExactScanWarningRows if exact recall is the deliberate choice."
                d.Options.Table
                d.RowEstimate
                d.Tuning.ExactScanWarningRows
        )
    | _ -> ConfigValidation.ValidationResult.Ok

/// Phase 892 — read and describe a store's live posture. For a store that
/// is not a `PgvectorVectorStore`, says so.
let report (store: IVectorStore) : Async<string> = async {
    match box store with
    | :? PgvectorVectorStore as pg ->
        let! d = pg.Diagnose()
        return describe d
    | _ -> return "not a PgvectorVectorStore — no pgvector posture to report"
}

/// Readiness probe over a Pgvector store: reachability (`ListScopes`) and,
/// since Phase 892, its posture (`assess`).
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

                match box store with
                | :? PgvectorVectorStore as pg ->
                    let! d = pg.Diagnose()
                    return assess d
                | _ -> return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

/// The readiness probe for `store`.
let create (store: IVectorStore) : IHealthCheck =
    PgvectorVectorStoreHealthCheck(store) :> IHealthCheck

/// Phase 892 — preflight validator: warns (never aborts) when the table
/// is large and no approximate index is configured (`exactScanVerdict`).
/// An external-probe validator — it reads the database, so
/// `ServerConfig.SkipPreflight` bypasses it.
type PgvectorVectorStoreValidator(store: IVectorStore) =
    interface ConfigValidation.IConfigValidator with
        member _.Name = "vector_store:pgvector"
        member _.Timeout = TimeSpan.FromSeconds 5.0

        member _.Validate() = async {
            match box store with
            | :? PgvectorVectorStore as pg ->
                try
                    let! d = pg.Diagnose()
                    return exactScanVerdict d
                with ex ->
                    return
                        ConfigValidation.ValidationResult.Warning(
                            sprintf "[PgvectorVectorStore] Could not read the store's posture: %s" ex.Message
                        )
            | _ -> return ConfigValidation.ValidationResult.Ok
        }

/// The preflight validator for `store`.
let validator (store: IVectorStore) : ConfigValidation.IConfigValidator =
    PgvectorVectorStoreValidator(store) :> ConfigValidation.IConfigValidator