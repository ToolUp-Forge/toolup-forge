module LocalEmbeddingProvider

open System
open System.Collections.Concurrent
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEmbeddingProvider

// ─── TF-IDF embedding provider ────────────────────────────────────
//
// A vocabulary-building TF-IDF provider for offline / dev use.
//
// Vocabulary is built incrementally across all embedded texts:
// - Each document's terms are added to the global vocabulary.
// - Embeddings are 512-dimensional sparse vectors over that vocabulary.
// - IDF is updated on each new document (documents already embedded
//   are NOT re-embedded; this is approximate but sufficient for dev).
//
// Quality is lower than neural embeddings (no semantic understanding),
// but works offline with no dependencies and no API key. Suitable for:
// - Local development and CI without API keys
// - Demos where retrieval quality is not critical
// - Offline environments
//
// Dimensions: 512 (vocabulary capped at 512 terms by frequency rank)
//
// NOTE on Guiding Principle 12 / Rule 4 (stateless handlers between
// invocations): this provider deliberately retains mutable IDF state
// (`df`, `docCount`, `vocab`) across `GenerateEmbedding` calls so TF-IDF
// scores can adapt as documents arrive. That is acceptable for an
// in-process dev provider but would violate Rule 4 in a distributed
// setting. Production providers (OpenAI, Anthropic, etc.) must be
// stateless between invocations — model state lives server-side at the
// API boundary, not in the `IEmbeddingProvider` instance.
//
// NOTE on cross-restart behaviour: the IDF state can persist to disk
// via `LocalEmbeddingProvider.createPersistent`. When persistence is
// enabled (the default in App-Server), the state survives process
// restarts — chunks indexed in a previous run remain in the same
// vector space as queries from the new process, so cosine similarity
// stays meaningful and `ReembeddingService` does not need to re-embed
// the entire corpus on every startup.
//
// The non-persistent factory `LocalEmbeddingProvider.create ()` is
// retained for tests, ephemeral deployments, and CI — same in-memory
// behaviour as before; restarts wipe state (and `ReembeddingService`
// has no recovery path because `ModelId` does not change).
//
// Deletion correctness: `df` / `docCount` are monotonically additive
// on `GenerateEmbedding` — KB document deletion does NOT call back
// into the embedder to decrement counts. Deleted chunks are tombstoned
// in `IVectorStore` (filtered out of `Search` per the contract), so
// retrieval correctness is unaffected. With persistence, the
// "vocab pollution from deleted docs" property already true within
// a single process now also stretches across restarts; for dev
// volumes this is benign. A future maintenance call could rebuild
// `df` from the current `IVectorStore` contents to drop dead terms.

/// Tokenise a string into lower-cased words, stripping punctuation.
let private tokenise (text: string) =
    text
        .ToLowerInvariant()
        .Split(
            [|
                ' '
                '\t'
                '\n'
                '\r'
                '.'
                ','
                ';'
                ':'
                '!'
                '?'
                '"'
                '\''
                '('
                ')'
                '['
                ']'
                '{'
                '}'
                '/'
                '\\'
                '-'
                '_'
                '='
                '+'
                '*'
                '@'
                '#'
                '$'
                '%'
                '^'
                '&'
                '<'
                '>'
                '|'
            |],
            StringSplitOptions.RemoveEmptyEntries
        )
    |> Array.filter (fun s -> s.Length > 1) // Skip 1-character tokens

/// Stop words to exclude (improves signal over common articles/conjunctions).
let private stopWords =
    Set.ofList [
        "the"
        "a"
        "an"
        "and"
        "or"
        "but"
        "in"
        "on"
        "at"
        "to"
        "for"
        "of"
        "with"
        "by"
        "from"
        "is"
        "are"
        "was"
        "were"
        "be"
        "been"
        "being"
        "have"
        "has"
        "had"
        "do"
        "does"
        "did"
        "will"
        "would"
        "could"
        "should"
        "may"
        "might"
        "must"
        "shall"
        "can"
        "this"
        "that"
        "these"
        "those"
        "i"
        "you"
        "he"
        "she"
        "it"
        "we"
        "they"
        "what"
        "which"
        "who"
        "when"
        "where"
        "why"
        "how"
        "all"
        "each"
        "every"
        "both"
        "few"
        "more"
        "most"
        "other"
        "some"
        "such"
        "no"
        "nor"
        "not"
        "only"
        "own"
        "same"
        "so"
        "than"
        "too"
        "very"
        "s"
        "t"
        "just"
        "don"
        "now"
        "as"
        "if"
        "so"
        "up"
        "out"
        "about"
        "into"
        "than"
        "then"
    ]

let private dimensions = 512

// ─── Persistence shape ───────────────────────────────────────────

[<Literal>]
let private platformContainer = "_platform"

[<Literal>]
let private stateBlobName = "embeddings/_local-tfidf-state.json"

/// Wire-format DTO for the persisted IDF state. No F# DUs — plain
/// settable properties and a `Dictionary<string,int>` so the JSON
/// shape is `{"docCount": N, "df": {"term": count, ...}}`. STJ handles
/// this shape directly (case-insensitive matching enabled by the
/// FableConverters options preserves the prior wire format which used
/// camelCase property names).
type private TfIdfStateDto() =
    member val DocCount: int = 0 with get, set

    member val Df: System.Collections.Generic.Dictionary<string, int> =
        System.Collections.Generic.Dictionary() with get, set

let private localTfIdfJsonOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private serializeState (docCount: int) (df: ConcurrentDictionary<string, int>) : byte[] =
    let dto = TfIdfStateDto(DocCount = docCount)

    for kv in df do
        dto.Df[kv.Key] <- kv.Value

    let json = System.Text.Json.JsonSerializer.Serialize(dto, localTfIdfJsonOptions)
    Encoding.UTF8.GetBytes json

let private deserializeState (bytes: byte[]) : (int * Map<string, int>) option =
    try
        let json = Encoding.UTF8.GetString bytes

        let dto =
            System.Text.Json.JsonSerializer.Deserialize<TfIdfStateDto>(json, localTfIdfJsonOptions)

        if isNull (box dto) then
            None
        else
            let m = dto.Df |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
            Some(dto.DocCount, m)
    with _ ->
        None

// ─── Implementation ──────────────────────────────────────────────

type private LocalEmbeddingProviderImpl
    (
        persister: ((int * ConcurrentDictionary<string, int>) -> Async<unit>) option,
        initial: (int * Map<string, int>) option
    ) =
    // term → document frequency count
    let df = ConcurrentDictionary<string, int>()
    // total documents seen. Written only under `stateLock`; read lock-free
    // as a volatile int elsewhere — stale reads at most underestimate IDF
    // by one document which is tolerable for an approximate dev provider.
    let mutable docCount = 0
    // stable vocabulary snapshot. Replaced wholesale under `stateLock`; read
    // lock-free — consumers capture the reference once per embedding call.
    let mutable vocab: string array = [||]
    let stateLock = obj ()

    // Hydrate from persisted state if any. Runs once at construction;
    // safe because no other thread can see the instance yet.
    do
        match initial with
        | None -> ()
        | Some(loadedDocCount, loadedDf) ->
            docCount <- loadedDocCount

            for kv in loadedDf do
                df[kv.Key] <- kv.Value

            // Recompute vocab from the loaded df so the first embed call
            // doesn't trip an empty vocab. The `kv.Key` secondary sort key
            // is load-bearing: `df.ToArray()` enumerates a
            // ConcurrentDictionary in unspecified order and most terms tie
            // at df=1, so a score-only sort would assign vocab dimensions
            // (and pick the 512-cap survivors) non-deterministically —
            // every embedding's geometry would then drift run-to-run.
            let snapshot = df.ToArray()

            vocab <-
                snapshot
                |> Array.sortBy (fun (kv: System.Collections.Generic.KeyValuePair<string, int>) -> -kv.Value, kv.Key)
                |> Array.map _.Key
                |> Array.truncate dimensions

    /// Persist current state synchronously inside the caller's lock.
    /// Local-disk writes through LocalFileStorage are sub-millisecond;
    /// remote backends (S3 / Azure / GCS) would block embedders for the
    /// duration of the upload — the in-process LocalEmbeddingProvider
    /// is dev-only by design (production deployments swap to a stateless
    /// embedding provider like OpenAI), so the synchronous write is
    /// acceptable. Failures are swallowed: a missed flush is self-healing
    /// because the next `updateDF` writes the latest snapshot.
    let persistLocked () =
        match persister with
        | None -> ()
        | Some p ->
            try
                p (docCount, df) |> Async.RunSynchronously
            with _ ->
                ()

    // Serialises the read-modify-write on `docCount` and the vocab rebuild.
    // `df` itself is a ConcurrentDictionary and already atomic per key, but
    // the increment of `docCount` and the vocab recomputation must appear
    // consistent to concurrent embedders.
    //
    // Concurrency-safety note on the vocab rebuild: `df.ToArray()` returns
    // an atomic snapshot of the dictionary (internal locking inside
    // ConcurrentDictionary). Iterating `df` directly via `Seq.toArray` /
    // `Seq.sortByDescending` would route through `ICollection.CopyTo`,
    // which sizes the destination from `df.Count` at one moment and then
    // copies — a concurrent `AddOrUpdate` from another embedder between
    // sizing and copying overflows the destination and throws
    // `ArgumentException`. Surfaced under the parallel reembed pressure
    // from `ReembeddingService.scanScope`'s `Async.Start(processOne ...)`
    // fan-out (one Async per chunk in a scope).
    let updateDF (terms: string array) =
        let distinct = terms |> Array.distinct

        for term in distinct do
            df.AddOrUpdate(term, 1, fun _ v -> v + 1) |> ignore

        lock stateLock (fun () ->
            docCount <- docCount + 1

            let dfSnapshot = df.ToArray()

            // `kv.Key` secondary key makes the vocab a deterministic
            // function of the df snapshot regardless of ToArray() order —
            // see the hydrate site for why the df=1 tie block matters.
            vocab <-
                dfSnapshot
                |> Array.sortBy (fun (kv: System.Collections.Generic.KeyValuePair<string, int>) -> -kv.Value, kv.Key)
                |> Array.map _.Key
                |> Array.truncate dimensions

            persistLocked ())

    let embed (text: string) =
        let terms = tokenise text
        let tf = System.Collections.Generic.Dictionary<string, int>()

        for t in terms do
            if not (stopWords.Contains t) then
                match tf.TryGetValue t with
                | true, n -> tf[t] <- n + 1
                | _ -> tf[t] <- 1

        // Update IDF state with the new document
        updateDF (tf.Keys |> Seq.toArray)

        let currentVocab = vocab
        let vec = Array.zeroCreate<float32> (min dimensions currentVocab.Length)
        let total = float terms.Length |> max 1.0

        for i in 0 .. vec.Length - 1 do
            let term = currentVocab[i]

            match tf.TryGetValue term with
            | true, count ->
                let tfScore = float count / total
                let idf = Math.Log(float (docCount + 1) / float (df.GetOrAdd(term, 1) + 1) + 1.0)
                vec[i] <- float32 (tfScore * idf)
            | _ -> ()

        // Normalise
        let mag = vec |> Array.sumBy (fun x -> x * x) |> sqrt

        if mag > 0.0f then
            for i in 0 .. vec.Length - 1 do
                vec[i] <- vec[i] / mag

        vec

    interface IEmbeddingProvider with
        member _.Dimensions = dimensions
        member _.ProviderId = "local"
        // ModelId is stable across processes once persistence is wired
        // (load-on-startup means the IDF dictionary is the same as the
        // one used to embed existing chunks, so vectors live in the same
        // sparse space). `ReembeddingService` keys re-indexing on
        // `EmbeddingVersion` mismatch — a stable ModelId means restarts
        // do not trigger reembed passes, eliminating the 20–60s cold-start
        // tax. Bump the version literal here on a real algorithm change
        // (different normalisation, different vocab cap, different
        // tokeniser) so existing chunks get re-indexed once.
        member _.ModelId = "local-tfidf-v1"
        member _.GenerateEmbedding(text: string) = async { return embed text }

        // Local provider has no network round-trip, so batching is just a
        // tight loop. Going through `Async.Parallel` would add overhead
        // without benefit, and serialising the IDF updates outside the
        // existing per-call lock isn't necessary because `embed` already
        // calls `updateDF` under `stateLock`.
        member _.GenerateEmbeddings(texts: string seq) = async { return texts |> Seq.toArray |> Array.map embed }

// ─── Factory functions ───────────────────────────────────────────

/// Create a non-persistent local TF-IDF `IEmbeddingProvider`. IDF state
/// lives entirely in process memory and is wiped on restart. Suitable
/// for tests, CI, and ephemeral deployments where every restart is a
/// clean slate.
let create () : IEmbeddingProvider =
    LocalEmbeddingProviderImpl(None, None) :> IEmbeddingProvider

/// Create a local TF-IDF `IEmbeddingProvider` whose IDF state persists
/// to the supplied `IBlobStorage` at `_platform/embeddings/_local-tfidf-state.json`.
/// State is loaded once at construction (synchronous); subsequent
/// updates write the new snapshot inline (synchronous, local-disk-only
/// dev provider — see `persistLocked` note in the impl). Existing
/// chunks indexed in a previous process remain in the same vector
/// space as queries from this process, so retrieval works without a
/// reembed pass.
///
/// Failures during initial load (corrupt JSON, missing blob, storage
/// error) silently fall back to an empty IDF state — the next embed
/// rebuilds it from scratch, and `ReembeddingService` will pick up the
/// chunks on its normal startup scan if `ModelId` happens to differ.
let createPersistent (blobStorage: IBlobStorage) : IEmbeddingProvider =
    let initial =
        async {
            let! result = blobStorage.Download(platformContainer, stateBlobName)

            return
                match result with
                | Ok bytes -> deserializeState bytes
                | Error _ -> None
        }
        |> Async.RunSynchronously

    let persister (count: int, dict: ConcurrentDictionary<string, int>) = async {
        let bytes = serializeState count dict
        let! _ = blobStorage.Upload(platformContainer, stateBlobName, bytes)
        return ()
    }

    LocalEmbeddingProviderImpl(Some persister, initial) :> IEmbeddingProvider