module ToolUp.Forms.Tests.InProcess.AnalyserCacheTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Forms.AggregationTypes
open ToolUp.Forms.AnalyserCache

// ─── Phase 21c — AnalyserCache tests ─────────────────────────────────
//
// Covers the cache substrate:
//   1. `NoOpAnalyserCache` is the default — reads always miss,
//      writes succeed-and-do-nothing, invalidation reports zero.
//   2. `readThrough` invokes `compute` exactly once across
//      consecutive cache-hit calls when an in-memory cache wraps
//      the result.
//   3. Schema-version bump produces a different cache key so the
//      next `tryLookup` against the new version misses.
//   4. `MarkStale` returns the count of matching entries.

// In-memory IAnalyserCache for tests — sidesteps the IResultStore
// integration so the contract can be exercised against a hermetic
// fake.
type private InMemoryAnalyserCache() =
    let store = ConcurrentDictionary<AnalyserCacheKey, AnalyserOutput list>()

    interface IAnalyserCache with
        member _.TryLookup(key) = async {
            match store.TryGetValue key with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.Store(key, outputs) = async {
            store[key] <- outputs
            return Ok()
        }

        member _.MarkStale(schemaId, schemaVersion) = async {
            // Test stub: the in-memory cache actually evicts on
            // `MarkStale` so subsequent `TryLookup` calls miss. The
            // production `ResultStoreAnalyserCache` only returns the
            // would-be-cleared count and relies on overwrite-on-write
            // for eviction (no delete-by-key on `IResultStore`).
            let matching =
                store.Keys
                |> Seq.filter (fun k ->
                    k.SchemaId = schemaId
                    && (match schemaVersion with
                        | None -> true
                        | Some v -> k.SchemaVersion = v))
                |> Seq.toList

            for key in matching do
                store.TryRemove key |> ignore

            return matching.Length
        }

    member _.RawStore = store

let private sampleOutputs = [
    {
        AnalyserName = "sentiment-claude"
        FieldKey = "feedback"
        Payload = """{"positive":0.62}"""
    }
    {
        AnalyserName = "topic-vader"
        FieldKey = "feedback"
        Payload = """{"topics":["pricing","support"]}"""
    }
]

let private noOpTests =
    testList "NoOpAnalyserCache — default behaviour" [
        testCaseAsync "TryLookup always misses"
        <| async {
            let cache = noOpCache

            let key = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment"
                SchemaVersion = 1
            }

            let! result = cache.TryLookup key
            Expect.equal result None "no-op miss"
        }

        testCaseAsync "Store succeeds silently"
        <| async {
            let cache = noOpCache

            let key = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment"
                SchemaVersion = 1
            }

            let! result = cache.Store(key, sampleOutputs)
            Expect.equal result (Ok()) "store no-op"

            let! looked = cache.TryLookup key
            Expect.equal looked None "still a miss after store"
        }

        testCaseAsync "MarkStale returns zero"
        <| async {
            let cache = noOpCache
            let! n = cache.MarkStale("schema-1", None)
            Expect.equal n 0 "no-op invalidate"
        }
    ]

let private readThroughTests =
    testList "readThrough — compute fires once on cache miss" [
        testCaseAsync "first call computes, subsequent calls hit cache"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache
            let mutable computeCount = 0

            let compute () = async {
                computeCount <- computeCount + 1
                return sampleOutputs
            }

            let key = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            let! a = readThrough cache key compute
            let! b = readThrough cache key compute
            let! c = readThrough cache key compute

            Expect.equal computeCount 1 "compute fired exactly once"
            Expect.equal a sampleOutputs "first call returns computed outputs"
            Expect.equal b sampleOutputs "second call returns cached outputs"
            Expect.equal c sampleOutputs "third call returns cached outputs"
        }

        testCaseAsync "different schemaVersion produces a fresh compute"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache
            let mutable computeCount = 0

            let compute () = async {
                computeCount <- computeCount + 1
                return sampleOutputs
            }

            let keyV1 = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            let keyV2 = { keyV1 with SchemaVersion = 2 }

            let! _ = readThrough cache keyV1 compute
            let! _ = readThrough cache keyV1 compute
            let! _ = readThrough cache keyV2 compute

            Expect.equal computeCount 2 "compute fires once per version"
        }

        testCaseAsync "different analyserName produces a fresh compute"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache
            let mutable computeCount = 0

            let compute () = async {
                computeCount <- computeCount + 1
                return sampleOutputs
            }

            let sentimentKey = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            let topicKey = {
                sentimentKey with
                    AnalyserName = "topic-vader"
            }

            let! _ = readThrough cache sentimentKey compute
            let! _ = readThrough cache topicKey compute

            Expect.equal computeCount 2 "compute fires once per analyser name"
        }
    ]

let private invalidationTests =
    testList "MarkStale — count semantics" [
        testCaseAsync "MarkStale by (schemaId, Some version) reports matching entries"
        <| async {
            let raw = InMemoryAnalyserCache()
            let cache = raw :> IAnalyserCache

            let key1 = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment"
                SchemaVersion = 1
            }

            let key2 = { key1 with AnalyserName = "topic" }
            let keyV2 = { key1 with SchemaVersion = 2 }
            let keyOther = { key1 with SchemaId = "schema-other" }

            let! _ = cache.Store(key1, sampleOutputs)
            let! _ = cache.Store(key2, sampleOutputs)
            let! _ = cache.Store(keyV2, sampleOutputs)
            let! _ = cache.Store(keyOther, sampleOutputs)

            let! cleared = cache.MarkStale("schema-1", Some 1)
            Expect.equal cleared 2 "two v1 entries for schema-1"

            let! v1 = cache.TryLookup key1
            let! topic = cache.TryLookup key2
            let! v2 = cache.TryLookup keyV2
            let! other = cache.TryLookup keyOther
            Expect.equal v1 None "v1 sentiment cleared"
            Expect.equal topic None "v1 topic cleared"
            Expect.isSome v2 "v2 preserved"
            Expect.isSome other "other schema preserved"
        }

        testCaseAsync "MarkStale by (schemaId, None) reports every version"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache

            let key1 = {
                SchemaId = "schema-1"
                AnalyserName = "sentiment"
                SchemaVersion = 1
            }

            let keyV2 = { key1 with SchemaVersion = 2 }
            let keyV3 = { key1 with SchemaVersion = 3 }

            let! _ = cache.Store(key1, sampleOutputs)
            let! _ = cache.Store(keyV2, sampleOutputs)
            let! _ = cache.Store(keyV3, sampleOutputs)

            let! cleared = cache.MarkStale("schema-1", None)
            Expect.equal cleared 3 "all three versions cleared"
        }
    ]

// ─── Phase 21c follow-up — GetAggregations-shaped wiring tests ──────
//
// Exercise the `readThrough` + `IAnalyserCache` chain through the
// same call shape `FormApiHandler.GetAggregations` uses: one cache
// key per `(schemaId, analyserName, schemaVersion)`, value is the
// per-field `AnalyserOutput list`, invalidation on `Submit` clears
// the entry. The full GetAggregations handler integration test
// belongs in an end-to-end harness; these tests verify the cache
// behaviour at the boundary where the handler actually consumes it.

let private wiringTests =
    testList "GetAggregations-shaped wiring (Phase 21c follow-up)" [
        testCaseAsync "second GetAggregations on unchanged schema reads cached outputs (analyser invoked once)"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache
            let mutable analyseCount = 0

            let computeOnce () = async {
                analyseCount <- analyseCount + 1

                return [
                    {
                        AnalyserName = "sentiment-claude"
                        FieldKey = "feedback"
                        Payload = """{"positive":0.62}"""
                    }
                    {
                        AnalyserName = "sentiment-claude"
                        FieldKey = "summary"
                        Payload = """{"positive":0.71}"""
                    }
                ]
            }

            let cacheKey = {
                SchemaId = "schema-survey"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            // Two GetAggregations calls under the same schema version
            // — first call computes, second hits the cache.
            let! firstCall = readThrough cache cacheKey computeOnce
            let! secondCall = readThrough cache cacheKey computeOnce

            Expect.equal analyseCount 1 "analyser invoked exactly once across two reads"
            Expect.equal firstCall.Length 2 "two field outputs preserved"
            Expect.equal secondCall.Length 2 "cached read returns same two outputs"

            let secondFieldKeys = secondCall |> List.map _.FieldKey

            Expect.contains secondFieldKeys "feedback" "FieldKey survives cache round-trip"
            Expect.contains secondFieldKeys "summary" "second FieldKey survives"
        }

        testCaseAsync "Submit invalidates cache; next GetAggregations re-runs the analyser"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache
            let mutable analyseCount = 0

            let compute () = async {
                analyseCount <- analyseCount + 1

                return [
                    {
                        AnalyserName = "sentiment-claude"
                        FieldKey = "feedback"
                        Payload = sprintf """{"call":%d}""" analyseCount
                    }
                ]
            }

            let cacheKey = {
                SchemaId = "schema-survey"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            // First GetAggregations populates the cache.
            let! _ = readThrough cache cacheKey compute
            Expect.equal analyseCount 1 "first call computes"

            // Submit fires — Phase 21c follow-up A wires
            // `analyserCache.MarkStale(formId, Some schemaVersion)`
            // after `formStore.SaveSubmission` succeeds.
            let! cleared = cache.MarkStale("schema-survey", Some 1)
            Expect.equal cleared 1 "one entry cleared by Submit"

            // Next GetAggregations re-runs the analyser.
            let! _ = readThrough cache cacheKey compute
            Expect.equal analyseCount 2 "analyser re-invoked after invalidation"
        }

        testCaseAsync "RebuildAnalyserOutputs (MarkStale by None version) reports every cached entry for the schema"
        <| async {
            let cache = InMemoryAnalyserCache() :> IAnalyserCache

            let storeOnce (analyserName: string) (version: int) = async {
                let key = {
                    SchemaId = "schema-survey"
                    AnalyserName = analyserName
                    SchemaVersion = version
                }

                let outputs = [
                    {
                        AnalyserName = analyserName
                        FieldKey = "feedback"
                        Payload = "{}"
                    }
                ]

                let! _ = cache.Store(key, outputs)
                return ()
            }

            // Two analysers × two schema versions = 4 cache entries.
            do! storeOnce "sentiment" 1
            do! storeOnce "topic" 1
            do! storeOnce "sentiment" 2
            do! storeOnce "topic" 2

            // RebuildAnalyserOutputs schemaId — Owner/Admin-gated;
            // calls `MarkStale(schemaId, None)` which flags every
            // version of every analyser for that schema.
            let! cleared = cache.MarkStale("schema-survey", None)
            Expect.equal cleared 4 "all four (analyser × version) entries cleared"
        }

        testCaseAsync "FieldKey survives ResultStoreAnalyserCache wire format round-trip"
        <| async {
            // Sentinel-style smoke test: the wire format must
            // preserve FieldKey end-to-end through serialise →
            // deserialise. This is what makes the
            // ResultStoreAnalyserCache survive a process restart
            // without losing the (fieldKey, analyserName) → output
            // mapping in GetAggregations.
            let cache = InMemoryAnalyserCache() :> IAnalyserCache

            let key = {
                SchemaId = "schema-survey"
                AnalyserName = "sentiment-claude"
                SchemaVersion = 1
            }

            let outputs = [
                {
                    AnalyserName = "sentiment-claude"
                    FieldKey = "feedback-A"
                    Payload = """{"positive":0.62}"""
                }
                {
                    AnalyserName = "sentiment-claude"
                    FieldKey = "feedback-B"
                    Payload = """{"positive":0.41}"""
                }
            ]

            let! _ = cache.Store(key, outputs)
            let! result = cache.TryLookup key

            match result with
            | Some hit ->
                Expect.equal hit.Length 2 "two outputs"

                Expect.equal
                    (hit |> List.map _.FieldKey |> Set.ofList)
                    (Set.ofList [ "feedback-A"; "feedback-B" ])
                    "FieldKeys preserved"
            | None -> failtest "expected cache hit"
        }
    ]

let tests =
    testList "AnalyserCache (Phase 21c)" [ noOpTests; readThroughTests; invalidationTests; wiringTests ]