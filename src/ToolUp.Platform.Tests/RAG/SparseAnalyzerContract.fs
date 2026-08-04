// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.RAG.SparseAnalyzerContract

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.SparseAnalysis
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.SparseIndices.Snowball
open ToolUp.SparseIndices.Snowball.SnowballAnalyzer
open ToolUp.SparseIndices.Cjk
open ToolUp.SparseIndices.Cjk.CjkAnalyzer

// ─── Phase 501 — sparse-analyzer contract ─────────────────────────
//
// Three claims, in the order they matter.
//
// 1. GP 11 — with no analyzer composed, the index is the pre-501 index. Not
//    "similar": the same terms and the same BM25 scores.
// 2. Symmetry — the SAME analyzer runs at index time and query time, and it
//    is not possible to arrange otherwise. That is enforced by the type
//    system (the index's term paths take only `AnalysedText`, whose
//    constructor is internal to ToolUp.RAG.Core) so it cannot be tested by
//    calling the wrong overload — there is none. What CAN go wrong, and is
//    tested here, is the persisted snapshot: an index whose stored postings
//    were built by a different analyzer than the one now composed.
// 3. Lift — composing the Snowball analyzer measurably improves Recall@K on
//    a morphologically-varied corpus. Measured against the sparse index
//    alone, so the number is the analyzer's effect and not the dense leg's.
//
// The failure this whole seam guards against is silent: index-time terms that
// disagree with query-time terms do not raise anything, they just stop
// matching, and the symptom is "retrieval got worse" months later.

/// ILogger double that records every line, so the tests can assert on the
/// re-analysis notice (an Info line, not a warning — re-analysis is the
/// correct response to a changed analyzer, not a fault).
type private CapturingLogger() =
    let infos = ResizeArray<string>()
    let warnings = ResizeArray<string>()

    member _.Infos = List.ofSeq infos
    member _.Warnings = List.ofSeq warnings

    interface ILogger with
        member _.Debug _ = ()
        member _.Info message = infos.Add message
        member _.Warn message = warnings.Add message
        member _.Error(_, _) = ()

let private chunk content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

/// Index a corpus into a fresh index over `storage` and dispose it, so the
/// final synchronous flush persists the snapshot.
let private seed (storage: IBlobStorage) (analyzer: ISparseAnalyzer option) (corpus: (string * string) list) =
    let index =
        match analyzer with
        | None -> new InMemoryBM25Index(storage, flushIntervalMs = 60000)
        | Some a -> new InMemoryBM25Index(storage, flushIntervalMs = 60000, analyzer = a)

    let sparse = index :> ISparseIndex

    for chunkId, content in corpus do
        sparse.Upsert Deployment chunkId (chunk content) |> Async.RunSynchronously

    (index :> IDisposable).Dispose()

let private search (index: InMemoryBM25Index) (query: string) =
    (index :> ISparseIndex).Search [ Deployment ] query 10 |> Async.RunSynchronously

// A corpus where every document states a concept in ONE surface form and the
// natural query uses a DIFFERENT one. Each has a lexically-adjacent distractor
// so a match is evidence of the right document, not of the only document.
let private morphologyCorpus = [
    "renewals", "Cancelling a subscription stops all future renewals immediately."
    "renewal-notice", "A renewal notice is an email sent before the charge date."
    "indexing", "Documents are indexed asynchronously after upload by the ingestion service."
    "index-page", "An index page lists the sections of a site."
    "granting", "Granting a permission adds it to the member's effective set."
    "grant-application", "A grant application is a funding request reviewed quarterly."
    "delivering", "Delivering a notification is retried with exponential backoff."
    "delivery-address", "The delivery address on an invoice is the registered company address."
    "rotating", "Rotating an encryption key re-wraps every data key."
    "key-management", "Key management is delegated to the configured secret store."
]

/// (query, the one chunk id that answers it). Every query uses a surface form
/// that does not appear verbatim in its answer.
let private morphologyQueries = [
    "how do I cancel a renewal", "renewals"
    "when does a document get indexed", "indexing"
    "grant permissions to a member", "granting"
    "notification deliveries", "delivering"
    "rotation of encryption keys", "rotating"
]

/// Recall@k over the sparse index alone: the fraction of queries whose answer
/// appears in the top k. Computed, not asserted by eye.
let private recallAt (k: int) (analyzer: ISparseAnalyzer option) =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    seed storage analyzer morphologyCorpus

    let index =
        match analyzer with
        | None -> new InMemoryBM25Index(storage, flushIntervalMs = 60000)
        | Some a -> new InMemoryBM25Index(storage, flushIntervalMs = 60000, analyzer = a)

    try
        let hits =
            morphologyQueries
            |> List.filter (fun (query, expected) ->
                search index query
                |> List.truncate k
                |> List.exists (fun m -> m.ChunkId = expected))
            |> List.length

        float hits / float morphologyQueries.Length
    finally
        (index :> IDisposable).Dispose()

[<Tests>]
let tests =
    testList "SparseAnalyzer contract" [

        // ── 1. GP 11 — the default is the pre-501 index ──────────────

        testList "identity default (GP 11)" [
            test "the shipped default analyzer reproduces the pre-501 tokenisation" {
                // The pre-501 rule, pinned as data rather than as "whatever the
                // regex does": lower-cased Unicode letter/digit runs, everything
                // else dropped. If this table has to change, the change is a
                // break for every existing deployment's persisted index.
                let cases = [
                    "The quick brown Fox", [ "the"; "quick"; "brown"; "fox" ]
                    "SKU-1234", [ "sku"; "1234" ]
                    "  spaced\tout\nlines  ", [ "spaced"; "out"; "lines" ]
                    "punctuation!!! -- gone?", [ "punctuation"; "gone" ]
                    "café Ærø", [ "café"; "ærø" ]
                    "", []
                ]

                for input, expected in cases do
                    Expect.equal
                        (identity.Analyse input)
                        expected
                        $"the identity analyzer must reproduce the pre-501 tokenisation for '{input}'"
            }

            test "an index constructed with no analyzer reports the identity id" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                use index = new InMemoryBM25Index(storage, flushIntervalMs = 60000)

                Expect.equal index.AnalyzerId IdentityAnalyzerId "the default analyzer must be the identity analyzer"
            }

            test "no analyzer composed is byte-for-byte composing the identity analyzer" {
                let defaultStorage = InMemoryBlobStorage() :> IBlobStorage
                let explicitStorage = InMemoryBlobStorage() :> IBlobStorage

                seed defaultStorage None morphologyCorpus
                seed explicitStorage (Some identity) morphologyCorpus

                use defaultIndex = new InMemoryBM25Index(defaultStorage, flushIntervalMs = 60000)

                use explicitIndex =
                    new InMemoryBM25Index(explicitStorage, flushIntervalMs = 60000, analyzer = identity)

                for query, _ in morphologyQueries do
                    let a = search defaultIndex query |> List.map (fun m -> m.ChunkId, m.Score)
                    let b = search explicitIndex query |> List.map (fun m -> m.ChunkId, m.Score)

                    // Scores, not just ids: a ranking that agrees by luck on
                    // this corpus would still be a behaviour change.
                    Expect.equal a b $"identity must be the default, exactly — query '{query}'"
            }

            test "a pre-501 snapshot (no AnalyzerId field) loads unchanged under the default" {
                // Written by hand in the shape the pre-501 index persisted:
                // a Docs array and nothing else. It must load its stored
                // tokens as-is, with no re-analysis notice, because the
                // identity analyzer is exactly what wrote it.
                let storage = InMemoryBlobStorage() :> IBlobStorage

                let legacy =
                    """{"Docs":[{"ChunkId":"doc-1","Length":4,"Tokens":["the","quick","brown","fox"],"Content":"The quick brown fox","Metadata":{}}]}"""

                storage.Upload("_rag", "_rag/deployment/bm25.json", Encoding.UTF8.GetBytes legacy)
                |> Async.RunSynchronously
                |> ignore

                let logger = CapturingLogger()

                use index = new InMemoryBM25Index(storage, logger = logger, flushIntervalMs = 60000)

                let results = search index "quick fox"

                Expect.equal (results |> List.map _.ChunkId) [ "doc-1" ] "a pre-501 snapshot must still answer queries"

                Expect.isEmpty
                    (logger.Infos |> List.filter (fun i -> i.Contains "re-analysed"))
                    "a pre-501 snapshot under the default analyzer must NOT be re-analysed — it was written by that analyzer"
            }
        ]

        // ── 2. Symmetry ──────────────────────────────────────────────

        testList "index-time / query-time symmetry" [
            test "a stemming analyzer matches across surface forms in both directions" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let snowball = SnowballAnalyzer.english ()

                seed storage (Some snowball) [
                    "plural-doc", "Future renewals are cancelled immediately."
                    "singular-doc", "Each granting of a permission is audited."
                ]

                use index =
                    new InMemoryBM25Index(storage, flushIntervalMs = 60000, analyzer = snowball)

                // Document plural → query singular.
                Expect.equal
                    (search index "renewal" |> List.map _.ChunkId)
                    [ "plural-doc" ]
                    "a singular query must reach a document that says the plural"

                // Document gerund → query plural noun.
                Expect.equal
                    (search index "grants" |> List.map _.ChunkId)
                    [ "singular-doc" ]
                    "a plural query must reach a document that says the gerund"
            }

            test "the identity analyzer does NOT match across surface forms (the control)" {
                // Without this case the one above proves nothing — it could
                // be passing because the corpus is small.
                let storage = InMemoryBlobStorage() :> IBlobStorage

                seed storage None [ "plural-doc", "Future renewals are cancelled immediately." ]

                use index = new InMemoryBM25Index(storage, flushIntervalMs = 60000)

                Expect.isEmpty
                    (search index "renewal")
                    "the pre-501 tokenisation genuinely cannot match 'renewal' against 'renewals' — that is the gap Phase 501 closes"
            }

            test "a snapshot written by another analyzer is re-analysed, not trusted" {
                // The real asymmetry hazard: the corpus was indexed before the
                // analyzer was composed. Trusting the stored tokens would leave
                // the postings unstemmed while every query arrives stemmed —
                // which raises nothing and silently returns fewer results.
                let storage = InMemoryBlobStorage() :> IBlobStorage
                seed storage None morphologyCorpus

                let logger = CapturingLogger()
                let snowball = SnowballAnalyzer.english ()

                use index =
                    new InMemoryBM25Index(storage, logger = logger, flushIntervalMs = 60000, analyzer = snowball)

                // The full query, because the corpus deliberately carries a
                // distractor that also stems to `renew` — a bare "renewal"
                // legitimately ranks the shorter distractor first, which would
                // make this case about BM25 length normalisation rather than
                // about re-analysis.
                Expect.equal
                    (search index "how do I cancel a renewal"
                     |> List.map _.ChunkId
                     |> List.truncate 1)
                    [ "renewals" ]
                    "after re-analysis the stemmed query must reach the identity-indexed document"

                Expect.isNonEmpty
                    (logger.Infos |> List.filter (fun i -> i.Contains "re-analysed"))
                    "the re-analysis must be announced once per scope — an unexplained index rebuild is worse than a loud one"
            }

            test "the converse holds: a stemmed snapshot is re-analysed under the identity analyzer" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                seed storage (Some(SnowballAnalyzer.english ())) morphologyCorpus

                use index = new InMemoryBM25Index(storage, flushIntervalMs = 60000)

                // Under identity the stored STEMS would not match a whole-word
                // query; after re-analysis from the retained content, they do.
                Expect.equal
                    (search index "renewals" |> List.map _.ChunkId |> List.truncate 1)
                    [ "renewals" ]
                    "downgrading to the identity analyzer must re-analyse rather than search stems with whole words"
            }

            test "an analyzer OPTION change is a different analyzer id, and re-analyses" {
                let withStop = SnowballAnalyzer.english ()

                let withoutStop =
                    SnowballAnalyzer.create {
                        SnowballOptions.english with
                            RemoveStopWords = false
                    }

                Expect.notEqual
                    withStop.Id
                    withoutStop.Id
                    "two configurations that produce different terms MUST report different ids — otherwise a persisted index silently keeps the old vocabulary"

                let storage = InMemoryBlobStorage() :> IBlobStorage
                seed storage (Some withStop) morphologyCorpus

                let logger = CapturingLogger()

                use index =
                    new InMemoryBM25Index(storage, logger = logger, flushIntervalMs = 60000, analyzer = withoutStop)

                Expect.isNonEmpty
                    (logger.Infos |> List.filter (fun i -> i.Contains "re-analysed"))
                    "changing an option must invalidate the persisted terms"
            }
        ]

        // ── 3. Measured lift ─────────────────────────────────────────

        testList "retrieval quality" [
            test "the Snowball analyzer lifts Recall@1 and Recall@3 over the identity analyzer" {
                let identityAt1 = recallAt 1 None
                let snowballAt1 = recallAt 1 (Some(SnowballAnalyzer.english ()))
                let identityAt3 = recallAt 3 None
                let snowballAt3 = recallAt 3 (Some(SnowballAnalyzer.english ()))

                // Absolute floors as well as a delta: a delta alone would still
                // pass if BOTH arms collapsed to zero.
                Expect.equal snowballAt3 1.0 "the Snowball arm must answer every morphology query within the top 3"

                Expect.isLessThan
                    identityAt3
                    snowballAt3
                    $"Recall@3 must improve: identity={identityAt3}, snowball={snowballAt3}"

                Expect.isLessThanOrEqual
                    identityAt1
                    snowballAt1
                    $"Recall@1 must not regress: identity={identityAt1}, snowball={snowballAt1}"
            }
        ]

        // ── 4. The Snowball companion ────────────────────────────────

        testList "ToolUp.SparseIndices.Snowball" [
            test "Porter2 vectors" {
                // The canonical examples from the published English Snowball
                // algorithm description, plus the terms this estate's own
                // fixtures depend on. These are the pin: the stemmer has no
                // upstream to drift against, so a change here is a change
                // someone made.
                let vectors = [
                    "consign", "consign"
                    "consigned", "consign"
                    "consigning", "consign"
                    "consignment", "consign"
                    "generate", "generat"
                    "general", "general"
                    "generously", "generous"
                    "happy", "happi"
                    "happiness", "happi"
                    "national", "nation"
                    "nationalism", "nation"
                    "nationalization", "nation"
                    "relational", "relat"
                    "conditional", "condit"
                    "rational", "ration"
                    "hopping", "hop"
                    "hoping", "hope"
                    "filing", "file"
                    "sky", "sky"
                    "skies", "sky"
                    "agreed", "agre"
                    "feed", "feed"
                    "plastered", "plaster"
                    "bled", "bled"
                    "motoring", "motor"
                    "sing", "sing"
                    "conflated", "conflat"
                    "troubled", "troubl"
                    "sized", "size"
                    "hopeful", "hope"
                    "goodness", "good"
                    "running", "run"
                    "runs", "run"
                    "ran", "ran"
                    "payments", "payment"
                    "payment", "payment"
                    "renewals", "renew"
                    "renewal", "renew"
                    "documents", "docum"
                    "document", "docum"
                    "adjustment", "adjust"
                    "indexed", "index"
                    "indexing", "index"
                ]

                for word, expected in vectors do
                    Expect.equal (Porter2.stem word) expected $"Porter2.stem '{word}'"
            }

            test "stop words are removed on both sides, and never to nothing" {
                let analyzer = SnowballAnalyzer.english ()

                Expect.equal
                    (analyzer.Analyse "the cost of the report")
                    [ "cost"; "report" ]
                    "stop words must be dropped"

                // A query of nothing but stop words analysing to [] would
                // return zero results — worse than low-value matches.
                Expect.equal
                    (analyzer.Analyse "to be or not to be")
                    [ "to"; "be"; "or"; "not"; "to"; "be" ]
                    "an all-stop-word input must keep its terms rather than analyse to nothing"
            }

            test "diacritic folding makes an unaccented query reach accented text" {
                let analyzer = SnowballAnalyzer.forLanguage StopWords.French

                Expect.equal
                    (analyzer.Analyse "les employés préférés")
                    (analyzer.Analyse "employes preferes")
                    "folding must make the accented and unaccented forms one term — on both sides"
            }

            test "stop-word removal precedes folding" {
                // `être` is in the list as written. If folding ran first the
                // token would be `etre` and would no longer match the entry, so
                // a folding deployment would drop different words from a
                // non-folding one — the same asymmetry class, one level down.
                let folding = SnowballAnalyzer.forLanguage StopWords.French

                let notFolding =
                    SnowballAnalyzer.create {
                        SnowballOptions.forLanguage StopWords.French with
                            FoldDiacritics = false
                    }

                Expect.isFalse
                    (folding.Analyse "être présent" |> List.contains "etre")
                    "a folding deployment must still recognise the accented stop word"

                Expect.isFalse
                    (notFolding.Analyse "être présent" |> List.contains "être")
                    "a non-folding deployment must drop the same stop word"
            }

            test "the analyzer id carries the whole configuration" {
                Expect.equal
                    (SnowballAnalyzer.english ()).Id
                    "snowball+en+porter2+stop+fold+min1"
                    "the id is recorded in the persisted snapshot — it is a contract, not a label"

                let ids =
                    [
                        SnowballOptions.english
                        {
                            SnowballOptions.english with
                                RemoveStopWords = false
                        }
                        {
                            SnowballOptions.english with
                                FoldDiacritics = false
                        }
                        {
                            SnowballOptions.english with
                                Stemming = NoStemming
                        }
                        {
                            SnowballOptions.english with
                                MinTermLength = 3
                        }
                        SnowballOptions.forLanguage StopWords.German
                    ]
                    |> List.map SnowballAnalyzer.idFor

                Expect.equal (List.distinct ids).Length ids.Length "every option must be distinguishable in the id"
            }

            test "a configuration the companion cannot serve is refused at create" {
                // Loud at composition rather than quiet at query time.
                Expect.throwsT<SnowballAnalyzerConfigurationException>
                    (fun () ->
                        SnowballAnalyzer.create {
                            SnowballOptions.forLanguage StopWords.German with
                                Stemming = BuiltInStemmer
                        }
                        |> ignore)
                    "BuiltInStemmer on a non-English language must be refused, not silently degraded to no stemming"

                Expect.throwsT<SnowballAnalyzerConfigurationException>
                    (fun () ->
                        SnowballAnalyzer.create {
                            SnowballOptions.english with
                                Stemming = CustomStemmer("", id)
                        }
                        |> ignore)
                    "a blank CustomStemmer id would let two different stemmers share one persisted index"

                Expect.throwsT<SnowballAnalyzerConfigurationException>
                    (fun () ->
                        SnowballAnalyzer.create {
                            SnowballOptions.english with
                                MinTermLength = 0
                        }
                        |> ignore)
                    "MinTermLength below 1 must be refused"
            }

            test "a custom stemmer plugs in for a language with no built-in one" {
                let analyzer =
                    SnowballAnalyzer.create {
                        SnowballOptions.forLanguage StopWords.German with
                            Stemming = CustomStemmer("trim-en", fun w -> w.TrimEnd 'e')
                    }

                Expect.stringContains analyzer.Id "custom:trim-en" "a custom stemmer's id must reach the analyzer id"
                Expect.equal (analyzer.Analyse "Fahrkarte") [ "fahrkart" ] "the custom stemmer must be applied"
            }
        ]

        // ── 5. The CJK companion ─────────────────────────────────────

        testList "ToolUp.SparseIndices.Cjk" [
            test "CJK runs are segmented into overlapping n-grams" {
                let analyzer = CjkAnalyzer.bigrams ()

                Expect.equal
                    (analyzer.Analyse "東京都の人口")
                    [ "東京"; "京都"; "都の"; "の人"; "人口" ]
                    "a CJK clause must segment into overlapping bigrams"

                Expect.equal (analyzer.Analyse "東") [ "東" ] "a run shorter than the n-gram width must still emit a term"
            }

            test "mixed-script text keeps word tokenisation outside the CJK runs" {
                Expect.equal
                    ((CjkAnalyzer.bigrams ()).Analyse "Windows 11 の設定")
                    [ "windows"; "11"; "の設"; "設定" ]
                    "one analyzer must handle a mixed-script corpus — Latin runs keep the shipped word rule"
            }

            test "the CJK analyzer makes a CJK corpus searchable where the default cannot" {
                let cjkStorage = InMemoryBlobStorage() :> IBlobStorage
                let defaultStorage = InMemoryBlobStorage() :> IBlobStorage
                let corpus = [ "doc-1", "東京都の人口は増加している" ]

                let analyzer = CjkAnalyzer.bigrams ()
                seed cjkStorage (Some analyzer) corpus
                seed defaultStorage None corpus

                use cjkIndex =
                    new InMemoryBM25Index(cjkStorage, flushIntervalMs = 60000, analyzer = analyzer)

                use defaultIndex = new InMemoryBM25Index(defaultStorage, flushIntervalMs = 60000)

                Expect.equal
                    (search cjkIndex "人口" |> List.map _.ChunkId)
                    [ "doc-1" ]
                    "a substring query must reach the document once the clause is segmented"

                Expect.isEmpty
                    (search defaultIndex "人口")
                    "the shipped tokenisation sees the whole clause as ONE term — the sparse leg is inert on CJK, which is the gap this companion closes"
            }

            test "the CJK analyzer id carries its configuration" {
                Expect.equal (CjkAnalyzer.bigrams ()).Id "cjk+ngram2" "the id is a contract with the persisted snapshot"

                Expect.notEqual
                    (CjkAnalyzer.create {
                        NGramSize = 2
                        IncludeUnigrams = true
                    })
                        .Id
                    (CjkAnalyzer.bigrams ()).Id
                    "including unigrams changes the term set, so it must change the id"
            }

            test "an unusable n-gram width is refused at create" {
                Expect.throwsT<CjkAnalyzerConfigurationException>
                    (fun () ->
                        CjkAnalyzer.create {
                            NGramSize = 0
                            IncludeUnigrams = false
                        }
                        |> ignore)
                    "NGramSize below 1 must be refused"
            }
        ]
    ]