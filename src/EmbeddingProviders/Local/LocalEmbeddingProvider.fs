module LocalEmbeddingProvider

open System
open System.Collections.Concurrent
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.VectorKnowledgeTypes

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
//
// ─── Phase 14z — scope-keyed IDF state ────────────────────────────
//
// The single global IDF dictionary above is shared by every team
// embedding in the same process, so Team A's term frequencies shape
// Team B's query vectors. The chunks themselves stay scope-isolated
// (that is `IVectorStore` + `IRetrievalPipeline`'s job) — what leaks is
// *embedding-quality variance*, a metadata channel rather than content.
//
// `ScopedLocalEmbeddingProviders` closes it structurally: one
// `TfIdfState` per `VectorScope`, keyed by a canonical, injectively
// escaped scope key, each persisting to its own blob under
// `_platform/embeddings/{scopeKey}/_local-tfidf-state.json`. Two scopes
// share no `df`, no `docCount`, and no vocabulary, so no term a team
// ever embeds is observable — even statistically — from another team's
// vectors. The Platform-scope vocabulary is likewise its own dictionary,
// so Platform Admin uploads do not shape team query embeddings.
//
// **Each scope-keyed provider reports `ModelId =
// "local-tfidf-v2#{scopeKey}"`** because the embedding function
// genuinely differs per scope: vector geometry is a function of the
// vocabulary, and a per-scope vocabulary is a different vocabulary. The
// `local-tfidf-v2` half separates the family from the unscoped `v1`, so
// `ReembeddingService` re-embeds once on the swap rather than silently
// mixing two sparse spaces; the `#{scopeKey}` half keeps two scopes out
// of one another's `IEmbeddingCache` entries and lets the reembed
// staleness check measure a chunk against its OWN scope's embedder (see
// `scopedModelIdFor`).
//
// **GP 11 — the unscoped factories are untouched.** `create ()` and
// `createPersistent blob` keep their signatures, their single global
// state, their legacy blob path, and `ModelId = "local-tfidf-v1"`, so an
// existing deployment that does not opt in is byte-for-byte unchanged
// and pays no reembed.
//
// **Cross-scope retrieval — resolved (Option 1).** A TF-IDF vector's
// geometry is a function of its vocabulary, so under per-scope
// vocabularies dimension `i` denotes a different term in each scope and
// one query vector cannot be compared across scopes. `RetrievalPipeline`
// therefore embeds the query ONCE PER AUTHORISED SCOPE and merges, gated
// on the `IScopedEmbeddingProviderFactory` capability probe — so the N
// embeds land only on this in-process dev provider, and every stateless
// production embedder keeps exactly one query vector on a byte-identical
// path. The family below implements that capability (and
// `IEmbeddingProvider`, so it composes directly into
// `RAGServerApp.create`).

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

/// Model id reported by the unscoped (global-vocabulary) providers.
[<Literal>]
let GlobalModelId = "local-tfidf-v1"

/// Model-id FAMILY prefix for the scope-keyed providers. Distinct from
/// `GlobalModelId` because a per-scope vocabulary is a different
/// embedding function — `ReembeddingService` re-embeds once on the swap
/// rather than mixing two sparse spaces in one index. No provider
/// reports this bare value: each reports `scopedModelIdFor` its own
/// scope (see below).
[<Literal>]
let ScopedModelId = "local-tfidf-v2"

/// Canonical, blob-path-safe key for a `VectorScope`.
module ScopeKey =

    /// Escape a scope's variable segment so no two distinct scopes can
    /// render to one key and no key can escape its blob prefix. `%` is
    /// escaped first, which makes the mapping injective; `/` and `\`
    /// would otherwise let a team id introduce a path separator, and
    /// `.` is escaped so `..` can never appear in a blob name.
    let escapeSegment (segment: string) =
        (if isNull segment then "" else segment)
            .Replace("%", "%25")
            .Replace("/", "%2F")
            .Replace("\\", "%5C")
            .Replace(".", "%2E")

    /// `platform` | `deployment` | `team-{id}` | `user-{id}`. The
    /// case prefixes are disjoint from one another and from any escaped
    /// id, so a team named `"admins"` and a user named `"admins"` are
    /// distinct keys.
    let ofScope (scope: VectorScope) : string =
        match scope with
        | VectorScope.Platform -> "platform"
        | VectorScope.Deployment -> "deployment"
        | VectorScope.Team teamId -> "team-" + escapeSegment teamId
        | VectorScope.User userId -> "user-" + escapeSegment userId

/// `embeddings/{scopeKey}/_local-tfidf-state.json` — the per-scope
/// persistence path under the `_platform` container.
let private scopedStateBlobName (scopeKey: string) =
    sprintf "embeddings/%s/_local-tfidf-state.json" scopeKey

/// The model id a scope-keyed provider reports: the family prefix plus
/// the canonical scope key, e.g. `local-tfidf-v2#team-acme`.
///
/// **The scope component is load-bearing, not decorative.**
/// `EmbeddingCacheKey` (`ToolUp.Platform.IEmbeddingCache`) is
/// `{ Version; TextHash }` where `Version` is
/// `{ ProviderId; ModelId; Dimensions }` — there is no tenant component
/// anywhere in it. Two scopes reporting one `ModelId` would therefore
/// share cache entries, and the first scope to embed a string would
/// serve ITS vector to every other scope asking for the same text: the
/// cross-scope coupling this phase removed from the IDF state, silently
/// re-created one layer up, and invisible because a cached vector is
/// indistinguishable from a computed one.
///
/// The same id rides `_embedModel` chunk metadata, so `ReembeddingService`
/// measures a chunk against its own scope's embedder rather than against
/// whichever scope happened to be composed.
let scopedModelIdFor (scope: VectorScope) : string =
    ScopedModelId + "#" + ScopeKey.ofScope scope

// ─── Persisted wire format ───────────────────────────────────────
//
// `{"docCount": N, "df": {"term": count, ...}}`, written and read by
// hand with `Utf8JsonWriter` / `JsonDocument`.
//
// **Why hand-rolled and not a DTO + reflection (Phase 14z).** This used
// to be a `type private TfIdfStateDto()` serialised with
// `JsonSerializer.Serialize`. `System.Text.Json`'s reflection resolver
// finds no serialisable members on a NON-PUBLIC type and emits `{}` —
// silently, with no exception — so every persisted state blob written
// since the feature shipped was the two bytes `{}`, and every restart
// re-hydrated an empty IDF dictionary. Nothing failed: `deserializeState`
// swallows errors by design and an empty state is indistinguishable from
// a first run, so `createPersistent` behaved exactly like `create ()`
// while its doc comment promised the opposite. Making the DTO public
// would fix it and put an implementation-detail type on the package's
// public API; writing the two dozen lines here fixes it without either
// the reflection dependency or the surface growth, and the format
// becomes what the doc comment always claimed.
//
// Reads are case-insensitive on the two property names so a blob
// written by any earlier shape is still accepted.

let private serializeState (docCount: int) (df: ConcurrentDictionary<string, int>) : byte[] =
    use stream = new System.IO.MemoryStream()
    let writer = new System.Text.Json.Utf8JsonWriter(stream)
    writer.WriteStartObject()
    writer.WriteNumber("docCount", docCount)
    writer.WritePropertyName "df"
    writer.WriteStartObject()

    // Ordered so re-serialising an unchanged state is byte-identical —
    // a blob that churns on every flush is indistinguishable from one
    // recording a real change when a reader diffs storage. `ToArray()`
    // is the atomic-snapshot read (see the vocab-rebuild note below for
    // why iterating the dictionary directly is not).
    for kv in df.ToArray() |> Array.sortBy _.Key do
        writer.WriteNumber(kv.Key, kv.Value)

    writer.WriteEndObject()
    writer.WriteEndObject()
    writer.Flush()
    writer.Dispose()
    stream.ToArray()

let private deserializeState (bytes: byte[]) : (int * Map<string, int>) option =
    try
        use doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString bytes)
        let root = doc.RootElement

        if root.ValueKind <> System.Text.Json.JsonValueKind.Object then
            None
        else
            let property (name: string) =
                root.EnumerateObject()
                |> Seq.tryFind (fun p -> String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                |> Option.map _.Value

            let docCount =
                match property "docCount" with
                | Some v when v.ValueKind = System.Text.Json.JsonValueKind.Number -> v.GetInt32()
                | _ -> 0

            let df =
                match property "df" with
                | Some v when v.ValueKind = System.Text.Json.JsonValueKind.Object ->
                    v.EnumerateObject()
                    |> Seq.choose (fun p ->
                        if p.Value.ValueKind = System.Text.Json.JsonValueKind.Number then
                            Some(p.Name, p.Value.GetInt32())
                        else
                            None)
                    |> Map.ofSeq
                | _ -> Map.empty

            Some(docCount, df)
    with _ ->
        None

// ─── Implementation ──────────────────────────────────────────────

type private LocalEmbeddingProviderImpl
    (
        persister: ((int * ConcurrentDictionary<string, int>) -> Async<unit>) option,
        initial: (int * Map<string, int>) option,
        modelId: string
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
        //
        // Phase 14z: supplied by the factory rather than hard-coded —
        // `GlobalModelId` for the unscoped providers, `ScopedModelId`
        // for a scope-keyed one, because a per-scope vocabulary IS a
        // different embedding function. Constant for the instance's
        // lifetime either way, as the interface requires.
        member _.ModelId = modelId
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
    LocalEmbeddingProviderImpl(None, None, GlobalModelId) :> IEmbeddingProvider

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

    LocalEmbeddingProviderImpl(Some persister, initial, GlobalModelId) :> IEmbeddingProvider

// ─── Phase 14z — scope-keyed factory ─────────────────────────────

/// A family of TF-IDF providers, one per `VectorScope`, sharing nothing
/// but the tokeniser and the stop-word list. This is the shape that
/// closes the cross-team IDF leak: `For (Team "a")` and `For (Team "b")`
/// each own a private `df` / `docCount` / vocabulary triple, so neither
/// team's term frequencies can shape the other's vectors, and the
/// `Platform` scope's vocabulary is separate from every team's.
///
/// Providers are created on first request per scope and cached, so
/// repeated `For` calls for one scope return the same instance and the
/// same accumulating state. Creation is `Lazy`-guarded: a
/// `ConcurrentDictionary` factory may run more than once under
/// contention, and two states for one scope would silently drop one
/// side's document counts.
///
/// **Persistence is optional.** Constructed with `Some blobStorage`,
/// each scope's state is hydrated at first use from
/// `_platform/embeddings/{scopeKey}/_local-tfidf-state.json` and written
/// back on every document; with `None` the family is memory-only (tests,
/// CI, ephemeral deployments).
///
/// **Dev-only, single-process** — the same Phase 9c rule-4 exception the
/// unscoped provider carries. State lives in this object; a second
/// process has its own.
type ScopedLocalEmbeddingProviders(blobStorage: IBlobStorage option) =

    let providers = ConcurrentDictionary<string, Lazy<IEmbeddingProvider>>()

    let createFor (scopeKey: string) =
        // `local-tfidf-v2#{scopeKey}` — see `scopedModelIdFor` for why the
        // scope component has to reach the model id.
        let scopedModelId = ScopedModelId + "#" + scopeKey

        match blobStorage with
        | None -> LocalEmbeddingProviderImpl(None, None, scopedModelId) :> IEmbeddingProvider
        | Some storage ->
            let blobName = scopedStateBlobName scopeKey

            let initial =
                async {
                    let! result = storage.Download(platformContainer, blobName)

                    return
                        match result with
                        | Ok bytes -> deserializeState bytes
                        | Error _ -> None
                }
                |> Async.RunSynchronously

            let persister (count: int, dict: ConcurrentDictionary<string, int>) = async {
                let bytes = serializeState count dict
                let! _ = storage.Upload(platformContainer, blobName, bytes)
                return ()
            }

            LocalEmbeddingProviderImpl(Some persister, initial, scopedModelId) :> IEmbeddingProvider

    /// The provider for `scope`. Same instance (and same accumulated IDF
    /// state) on every call for the same scope.
    member _.For(scope: VectorScope) : IEmbeddingProvider =
        let key = ScopeKey.ofScope scope

        providers.GetOrAdd(key, fun k -> Lazy<IEmbeddingProvider>((fun () -> createFor k), true)).Value

    /// Scope keys with live state in this process, in the canonical
    /// `ScopeKey.ofScope` form. Diagnostic surface — a caller checking
    /// that a reset actually dropped a scope should read this rather
    /// than infer it from embedding output.
    member _.KnownScopes() : string list =
        providers.Keys |> Seq.toList |> List.sort

    /// Drop `scope`'s IDF state — in memory and, when persistence is
    /// wired, on disk. The natural companion to `KnowledgeApi.ResetIndex`:
    /// wiping a team's chunks should wipe the vocabulary those chunks
    /// built, and because the state is scope-keyed this is now a
    /// structurally safe operation — no other scope is touched.
    /// Idempotent (`IBlobStorage.Delete` is), so resetting a scope that
    /// never embedded anything succeeds.
    ///
    /// A `For` call racing a `ResetScope` may re-create the scope's
    /// state before the blob delete lands; callers serialise reset
    /// against ingestion for that scope, exactly as they must for the
    /// chunk deletion itself.
    member _.ResetScope(scope: VectorScope) : Async<unit> = async {
        let key = ScopeKey.ofScope scope
        providers.TryRemove key |> ignore

        match blobStorage with
        | None -> ()
        | Some storage ->
            let! _ = storage.Delete(platformContainer, scopedStateBlobName key)
            return ()
    }

    // ─── The composable surfaces (Phase 14z, Option 1) ───────────
    //
    // The family is itself an `IEmbeddingProvider` so a deployment can
    // hand it straight to `RAGServerApp.create`, and an
    // `IScopedEmbeddingProviderFactory` so the RAG pipeline's capability
    // probe finds the per-scope embedders behind it. Every caller that
    // does NOT probe (a health check, a diagnostic, a consumer holding
    // the DI singleton) still gets a working embedder rather than a
    // throw.
    //
    // That fallback resolves to the `Deployment` scope deliberately:
    // `Deployment` is the deployment-wide shared scope, so an unscoped
    // call cannot land in some tenant's vocabulary — and the family
    // reports exactly that scope's `ModelId`, so the identity it
    // advertises is the identity of the vectors it produces. Reporting
    // the bare `local-tfidf-v2` family prefix here would have been a
    // quiet lie: the cache would then key an unscoped call and a
    // `Deployment`-scoped call differently while both computed the same
    // vector from the same state.

    interface IEmbeddingProvider with
        member _.Dimensions = dimensions
        member _.ProviderId = "local"

        member this.ModelId =
            (this :> IScopedEmbeddingProviderFactory).For(VectorScope.Deployment).ModelId

        member this.GenerateEmbedding(text: string) =
            (this :> IScopedEmbeddingProviderFactory).For(VectorScope.Deployment).GenerateEmbedding text

        member this.GenerateEmbeddings(texts: string seq) =
            (this :> IScopedEmbeddingProviderFactory).For(VectorScope.Deployment).GenerateEmbeddings texts

    interface IScopedEmbeddingProviderFactory with
        member this.For(scope: VectorScope) = this.For scope
        member this.ResetScope(scope: VectorScope) = this.ResetScope scope

/// Create a memory-only scope-keyed provider family. Every scope starts
/// empty and is wiped on restart — the shape tests and CI want.
let createScoped () : ScopedLocalEmbeddingProviders = ScopedLocalEmbeddingProviders(None)

/// Create a scope-keyed provider family whose per-scope IDF state
/// persists to `blobStorage` under
/// `_platform/embeddings/{scopeKey}/_local-tfidf-state.json`. Per-scope
/// hydration happens on that scope's first `For` call, so a deployment
/// with fifty teams does not read fifty blobs at boot.
let createScopedPersistent (blobStorage: IBlobStorage) : ScopedLocalEmbeddingProviders =
    ScopedLocalEmbeddingProviders(Some blobStorage)