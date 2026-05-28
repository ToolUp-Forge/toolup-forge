// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.AnalyserCache

open System
open ToolUp.Platform
open ToolUp.Forms.FormSchema
open ToolUp.Forms.AggregationTypes

// ─── Phase 21c — Analyser-output cache ───────────────────────────────
//
// Persists `IFormSubmissionAnalyser` outputs in an `IResultStore`-
// backed entry keyed by `(schemaId, analyserName, schemaVersion)`.
// `IFormApi.GetAggregations` consults the cache before invoking the
// live analyser; cache misses fall through to the live call and
// then write the result back so the next request is a hit.
//
// **Invalidation:**
//   - Schema version bump → key changes naturally; old entry
//     becomes unreachable but is left in place (storage cost is
//     trivial; the next `RebuildAnalyserOutputs` flags stragglers
//     via `MarkStale`).
//   - New submission → `MarkStale` is called for that schema at
//     the current schema version. The next miss-path compute
//     overwrites the entry, evicting the stale payload.
//   - `RebuildAnalyserOutputs schemaId` → `MarkStale schemaId None`
//     flags every entry for that schema (all versions, all
//     analyser names); the next read recomputes.
//
// `IResultStore` has no delete-by-key surface today, so `MarkStale`
// takes no action and returns the count of would-be-cleared entries
// as a telemetry proxy. The actual eviction path is the submission-
// write hook's compute-then-store overwrite. See
// `FUTURE-STRATEGIC-GAPS.md` for the substrate gap a real
// `Invalidate` would require.
//
// **No-op default.** Deployments without `withAnalyserCache` use
// `NoOpAnalyserCache` — every `tryLookup` returns `None`, every
// `store` / `markStale` succeeds-and-does-nothing. Preserves the
// pre-Phase-21c "no cache, every read re-runs" behaviour
// byte-for-byte.

/// Stable identifier triple. The third component bumps on every
/// `FormSchema` edit so prior-version cache entries become
/// unreachable. The pair `(schemaId, schemaVersion)` is the
/// "schema-at-submit-time" replay anchor — historical submissions
/// against an older version still get re-analysed against the
/// version they submitted under if the cache miss path runs.
type AnalyserCacheKey = {
    SchemaId: FormSchemaId
    AnalyserName: string
    SchemaVersion: int
}

/// Server-side cache layer over `IFormSubmissionAnalyser` outputs.
/// Implementations satisfy `IFormSubmissionAnalyserContract`'s cache
/// extension tests.
type IAnalyserCache =
    /// Read a cached entry. `None` = not in cache (caller should
    /// invoke the live analyser); `Some outputs` = cache hit.
    abstract TryLookup: AnalyserCacheKey -> Async<AnalyserOutput list option>

    /// Persist analyser outputs under the given key. The store may
    /// version internally; the cache treats every write as
    /// "overwrite latest".
    abstract Store: AnalyserCacheKey * AnalyserOutput list -> Async<Result<unit, string>>

    /// Mark every cached entry whose `SchemaId` matches and (optionally)
    /// whose `SchemaVersion` matches as stale. Used by the submission-
    /// write hook (passes the current version) and by
    /// `RebuildAnalyserOutputs` (passes `None` to flag every version).
    /// Returns the count of entries that *would* be cleared given a
    /// delete-by-key surface — `IResultStore` has no such surface today
    /// (see FUTURE-STRATEGIC-GAPS), so the count is a telemetry proxy
    /// and the actual eviction happens on the next overwrite via the
    /// submission-write hook's compute-then-store path.
    abstract MarkStale: schemaId: FormSchemaId * schemaVersion: int option -> Async<int>

/// Default `IAnalyserCache` for deployments that haven't opted in
/// via `withAnalyserCache`. Reads always miss; writes succeed
/// silently; invalidation is a no-op. Preserves the pre-Phase-21c
/// behaviour byte-for-byte.
type NoOpAnalyserCache() =
    interface IAnalyserCache with
        member _.TryLookup(_) = async { return None }
        member _.Store(_, _) = async { return Ok() }
        member _.MarkStale(_, _) = async { return 0 }

// ─── ResultStore-backed implementation ──────────────────────────────

[<Literal>]
let private CacheModuleName = "_FormsAnalyserCache"

/// Resource type used by `IResultStore.SaveResult` so cache entries
/// can be inspected via `IResultStore.ListResults` without
/// collisions against domain analytical results.
let private resultTypeFor (key: AnalyserCacheKey) : string =
    sprintf "%s|%s|%d" key.SchemaId key.AnalyserName key.SchemaVersion

/// Serialise / deserialise the cached payload. Format: simple
/// pipe-delimited records — one line per `AnalyserOutput`. Avoids
/// committing to a JSON serialiser at the cache layer; the
/// `AnalyserOutput` shape is small (analyser-name + field-key +
/// payload-string) and stable.
module private CachePayload =
    let private separator = "" // ASCII Unit Separator

    let serialise (outputs: AnalyserOutput list) : byte[] =
        outputs
        |> List.map (fun o ->
            sprintf "%s%s%s%s%s" o.AnalyserName separator o.FieldKey separator (o.Payload.Replace("\n", "\\n")))
        |> String.concat "\n"
        |> System.Text.Encoding.UTF8.GetBytes

    let deserialise (bytes: byte[]) : AnalyserOutput list =
        let text = System.Text.Encoding.UTF8.GetString bytes

        text.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList
        |> List.choose (fun line ->
            match line.Split([| separator |], StringSplitOptions.None) with
            | [| analyser; fieldKey; payload |] ->
                // Phase 21c follow-up — 3-token wire format including
                // FieldKey. New blobs land here.
                Some {
                    AnalyserName = analyser
                    FieldKey = fieldKey
                    Payload = payload.Replace("\\n", "\n")
                }
            | [| analyser; payload |] ->
                // Legacy 2-token blob — pre-cache-wiring deployments
                // that never populated FieldKey. Recover the row with
                // an empty FieldKey; renderers treat it as "field
                // unknown" rather than crashing on a parse miss.
                Some {
                    AnalyserName = analyser
                    FieldKey = ""
                    Payload = payload.Replace("\\n", "\n")
                }
            | _ -> None)

/// `IResultStore`-backed cache. Persists across restarts; shared
/// across multi-instance deployments via the underlying store.
/// The cache is scoped per-request via `scopeIdResolver` (typically
/// the resolved `StorageScope.ScopeId`) so per-team isolation is
/// preserved.
type ResultStoreAnalyserCache(resultStore: IResultStore, scopeIdResolver: unit -> string) =
    interface IAnalyserCache with
        member _.TryLookup(key) = async {
            let scopeId = scopeIdResolver ()
            let! result = resultStore.GetLatest(scopeId, CacheModuleName, resultTypeFor key)

            return
                match result with
                | Ok(_, bytes) -> Some(CachePayload.deserialise bytes)
                | Error _ -> None
        }

        member _.Store(key, outputs) = async {
            let scopeId = scopeIdResolver ()
            let bytes = CachePayload.serialise outputs

            let! saveResult = resultStore.SaveResult(scopeId, CacheModuleName, resultTypeFor key, bytes, "_system", [])

            return
                match saveResult with
                | Ok _ -> Ok()
                | Error err -> Error(sprintf "%A" err)
        }

        member _.MarkStale(schemaId, schemaVersion) = async {
            let scopeId = scopeIdResolver ()
            let! entries = resultStore.ListResults(scopeId, CacheModuleName, None)

            let prefix = sprintf "%s|" schemaId

            let versionMatches (resultType: string) =
                match schemaVersion with
                | None -> true
                | Some v -> resultType.EndsWith(sprintf "|%d" v)

            // `IResultStore` doesn't currently expose a delete-by-key surface
            // (the design assumes results are versioned forwards — see
            // `FUTURE-STRATEGIC-GAPS.md` for the substrate gap). Marking-as-
            // stale therefore takes no action; the count of would-be-cleared
            // entries is returned as a telemetry proxy. The submission-write
            // hook in FormApiHandler achieves correct read semantics by
            // writing a fresh version on the next `GetAggregations` miss
            // path, which overwrites the stale entry's latest pointer.
            return
                entries
                |> List.filter (fun obj -> obj.DataType.StartsWith prefix && versionMatches obj.DataType)
                |> List.length
        }

// ─── Module-level helpers ────────────────────────────────────────────

/// Construct an `IAnalyserCache` over an `IResultStore` with the
/// caller-provided scope resolver.
let createResultStoreBacked (resultStore: IResultStore) (scopeIdResolver: unit -> string) : IAnalyserCache =
    ResultStoreAnalyserCache(resultStore, scopeIdResolver) :> _

/// The default no-op cache.
let noOpCache: IAnalyserCache = NoOpAnalyserCache() :> _

/// Convenience read-through. Looks up `key` in `cache`; on miss
/// invokes `compute`, persists the result, and returns it.
let readThrough
    (cache: IAnalyserCache)
    (key: AnalyserCacheKey)
    (compute: unit -> Async<AnalyserOutput list>)
    : Async<AnalyserOutput list> =
    async {
        match! cache.TryLookup key with
        | Some hit -> return hit
        | None ->
            let! computed = compute ()
            // Fire-and-forget write: a transient write failure
            // shouldn't fail the read path. The next read still
            // misses, the next compute runs, etc.
            let! _ = cache.Store(key, computed)
            return computed
    }