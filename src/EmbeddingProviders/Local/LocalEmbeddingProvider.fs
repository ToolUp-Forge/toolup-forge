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
// A hashed TF-IDF provider for offline / dev use.
//
// - A term's DIMENSION is a pure function of the term itself: a
//   deterministic, culture-invariant hash into the fixed 512 slots
//   (`featureSlot` below). Nothing about the corpus, its arrival order,
//   or how much has been embedded can move it.
// - A term's WEIGHT is TF-IDF, and IDF still adapts as documents arrive
//   (documents already embedded are NOT re-embedded; this is approximate
//   but sufficient for dev). Adaptive weighting only rescales
//   coordinates — it can no longer permute them.
//
// Quality is lower than neural embeddings (no semantic understanding),
// but works offline with no dependencies and no API key. Suitable for:
// - Local development and CI without API keys
// - Demos where retrieval quality is not critical
// - Offline environments
//
// Dimensions: 512 (fixed hashed feature slots; terms that collide share
// a slot, which the signed hashing below leaves unbiased in expectation)
//
// ─── Why hashing, and what it replaced ───────────────────────────
//
// This provider used to carry a `vocab` array — the terms ranked by
// document frequency, truncated to 512 — and dimension `i` denoted
// `vocab[i]`. The array was rebuilt on EVERY embed, and previously
// indexed chunks were never re-embedded, so a chunk indexed early kept
// coordinates in a vocabulary the provider had since re-sorted. On a
// small corpus each new embed (a *query* embeds too, and so updates the
// IDF state) permuted enough of the head that dimension 0 denoted a
// different term than it had at index time, and cosine similarity
// between a query and a chunk became meaningless.
//
// The failure was silent and total rather than degrading: retrieval
// returned confidently ranked nonsense, which reads as "the dev embedder
// is low quality" (true, and the documented expectation) rather than
// "your stored vectors are in a space the query no longer shares". It
// was observed writing Phase 14z's cross-scope acceptance case — an
// on-topic document lost to an unrelated one from the same scope, with
// no scope-keying involved at all — and worked around there with a
// fixed-assignment test double.
//
// Fixing it by hashing rather than by "freeze the vocabulary once built"
// or "re-embed the corpus when the assignment moves" is the choice that
// keeps IDF adaptivity at no cost and needs no staleness machinery: with
// the assignment fixed by construction there is no assignment change to
// detect, announce, or recover from.
//
// **Vectors written by the pre-hashing scheme are not readable by this
// one — a dev-tier reset, stated plainly.** Their coordinates denote
// whatever the vocabulary happened to be at their index time; nothing
// can recover that. The recovery is automatic rather than manual: both
// `ModelId` literals are bumped below (`local-tfidf-v3` / `-v4`), so
// `ReembeddingService` sees an `EmbeddingVersion` mismatch on every
// stored chunk and re-embeds the corpus ONCE, exactly as it does for any
// other algorithm change. That is also why this file does not emit a
// startup notice about a moved assignment: the assignment no longer
// moves, and the one historical move is handled by the mechanism the SDK
// already has for it. A persisted IDF state blob stays readable and is
// still hydrated — only the vectors are invalidated.
//
// NOTE on Guiding Principle 12 / Rule 4 (stateless handlers between
// invocations): this provider deliberately retains mutable IDF state
// (`df`, `docCount`) across `GenerateEmbedding` calls so TF-IDF scores
// can adapt as documents arrive. That is acceptable for an in-process
// dev provider but would violate Rule 4 in a distributed setting.
// Production providers (OpenAI, Anthropic, etc.) must be stateless
// between invocations — model state lives server-side at the API
// boundary, not in the `IEmbeddingProvider` instance.
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
// "{ScopedModelId}#{scopeKey}"`** because the embedding function
// genuinely differs per scope: the IDF weights a scope applies are a
// function of the documents that scope has seen. The family half
// separates the scope-keyed providers from the unscoped ones, so
// `ReembeddingService` re-embeds once on the swap rather than silently
// mixing two weightings; the `#{scopeKey}` half keeps two scopes out
// of one another's `IEmbeddingCache` entries and lets the reembed
// staleness check measure a chunk against its OWN scope's embedder (see
// `scopedModelIdFor`).
//
// **GP 11 — the unscoped factories are untouched.** `create ()` and
// `createPersistent blob` keep their signatures, their single global
// state, and their legacy blob path, so an existing deployment that does
// not opt in composes exactly as before. (Their `ModelId` literal DOES
// move with the hashing change — that is the one-off corpus reembed the
// header describes, and it applies to every shape of this provider
// alike.)
//
// **Cross-scope retrieval — resolved (Option 1).** `RetrievalPipeline`
// embeds the query ONCE PER AUTHORISED SCOPE and merges, gated on the
// `IScopedEmbeddingProviderFactory` capability probe — so the N embeds
// land only on this in-process dev provider, and every stateless
// production embedder keeps exactly one query vector on a byte-identical
// path. The family below implements that capability (and
// `IEmbeddingProvider`, so it composes directly into
// `RAGServerApp.create`).
//
// The ORIGINAL motivation for that design was sharper than it is now:
// under ranked vocabularies dimension `i` denoted a different term in
// each scope, so one query vector could not be compared across scopes at
// all. Under feature hashing the coordinate MEANING is shared — a term
// occupies the same slot everywhere — and what still differs per scope is
// the IDF weighting. So the pipeline's per-scope embed has gone from
// repairing incomparable geometry to applying each scope's own
// weighting: less dramatic, still correct, and still what the pipeline
// does. Nothing here is removed on the strength of that: the capability
// is also what keeps a metered production embedder off the N-embed path,
// which was never about geometry.

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

// ─── Feature hashing — the fixed term → dimension assignment ─────
//
// The hash MUST be deterministic across processes and machines and
// independent of culture, because a chunk indexed by one process is
// queried by another and the two must agree on what dimension `i`
// denotes. `String.GetHashCode` satisfies neither: .NET randomises it
// per process by default, so it would reproduce the exact bug this
// replaces — worse, invisibly, since a single-process test would pass.
//
// FNV-1a (64-bit) over the term's UTF-8 bytes, finished with
// MurmurHash3's `fmix64` avalanche step. The finaliser is load-bearing:
// FNV-1a's LOW bits are its weak ones (each `hash * prime` step
// propagates low → high only), and `dimensions` is a power of two, so
// taking the remainder reads exactly those weak bits. `fmix64` mixes
// every bit into every other, after which the low bits used for the slot
// and the top bit used for the sign are effectively independent.

let private termHash (term: string) : uint64 =
    let bytes = Encoding.UTF8.GetBytes term
    let mutable h = 14695981039346656037UL // FNV-1a 64-bit offset basis

    for b in bytes do
        h <- (h ^^^ uint64 b) * 1099511628211UL // FNV prime

    // MurmurHash3 fmix64
    h <- h ^^^ (h >>> 33)
    h <- h * 0xFF51AFD7ED558CCDUL
    h <- h ^^^ (h >>> 33)
    h <- h * 0xC4CEB9FE1A85EC53UL
    h <- h ^^^ (h >>> 33)
    h

/// The slot a term occupies, and the sign its weight carries there.
///
/// Signed hashing (Weinberger et al.) is the standard mitigation for the
/// one cost hashing has over a ranked vocabulary: two distinct terms can
/// share a slot. With every weight positive, collisions would only ever
/// ADD, inflating every pairwise similarity in one direction; with the
/// sign drawn from an independent bit of the same avalanched hash, a
/// collision's contribution cancels in expectation instead. It is
/// deterministic and symmetric, so the index and query sides still agree
/// exactly — which is the whole property this function exists to hold.
let private featureSlot (term: string) : int * float =
    let h = termHash term
    let slot = int (h % uint64 dimensions)
    let sign = if h &&& 0x8000000000000000UL = 0UL then 1.0 else -1.0
    slot, sign

// ─── Persistence shape ───────────────────────────────────────────

[<Literal>]
let private platformContainer = "_platform"

[<Literal>]
let private stateBlobName = "embeddings/_local-tfidf-state.json"

/// Model id reported by the unscoped (single shared vocabulary)
/// providers.
///
/// **The version digit names the embedding FUNCTION, and both halves of
/// it move.** `v1` was the unscoped ranked-vocabulary embedder and `v2`
/// the scope-keyed one; feature hashing (see the header) is a genuinely
/// different function on both, so they advance together to `v3` / `v4`
/// rather than one of them reusing a retired literal. A chunk carrying
/// `v1` or `v2` in its `_embedModel` metadata is therefore re-embedded
/// once by `ReembeddingService` — the automatic recovery for vectors
/// written in a vocabulary that no longer exists.
[<Literal>]
let GlobalModelId = "local-tfidf-v3"

/// Model-id FAMILY prefix for the scope-keyed providers. Distinct from
/// `GlobalModelId` because a per-scope vocabulary is a different
/// embedding function — `ReembeddingService` re-embeds once on the swap
/// rather than mixing two sparse spaces in one index. No provider
/// reports this bare value: each reports `scopedModelIdFor` its own
/// scope (see below).
[<Literal>]
let ScopedModelId = "local-tfidf-v4"

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
    // is the atomic-snapshot read: iterating a ConcurrentDictionary
    // directly routes through `ICollection.CopyTo`, which sizes the
    // destination from `df.Count` at one moment and then copies, so a
    // concurrent `AddOrUpdate` between the two overflows it and throws.
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
    let stateLock = obj ()

    // Hydrate from persisted state if any. Runs once at construction;
    // safe because no other thread can see the instance yet.
    //
    // There is nothing to recompute afterwards: dimension assignment is a
    // function of the term, so a hydrated provider and a freshly fed one
    // holding the same `df` produce the same geometry with no derived
    // snapshot to rebuild.
    do
        match initial with
        | None -> ()
        | Some(loadedDocCount, loadedDf) ->
            docCount <- loadedDocCount

            for kv in loadedDf do
                df[kv.Key] <- kv.Value

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

    // Serialises the read-modify-write on `docCount` and the persist.
    // `df` itself is a ConcurrentDictionary and already atomic per key, but
    // the increment of `docCount` and the snapshot handed to the persister
    // must appear consistent to concurrent embedders.
    //
    // (Before feature hashing this lock also guarded a vocabulary rebuild
    // that sorted a `df.ToArray()` snapshot on every call. That rebuild is
    // gone with the vocabulary — the assignment it produced is what this
    // change replaced — and with it the `ICollection.CopyTo` hazard the
    // snapshot form existed to dodge under `ReembeddingService.scanScope`'s
    // per-chunk `Async.Start` fan-out.)
    let updateDF (terms: string array) =
        let distinct = terms |> Array.distinct

        for term in distinct do
            df.AddOrUpdate(term, 1, fun _ v -> v + 1) |> ignore

        lock stateLock (fun () ->
            docCount <- docCount + 1
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

        // Always the full declared dimensionality. The pre-hashing form
        // sized this by the vocabulary, so an early call returned a vector
        // SHORTER than `Dimensions` advertised — a second way the same
        // corpus could yield incomparable vectors.
        let vec = Array.zeroCreate<float32> dimensions
        let total = float terms.Length |> max 1.0
        let docs = docCount

        for kv in tf do
            let term = kv.Key
            let tfScore = float kv.Value / total
            let idf = Math.Log(float (docs + 1) / float (df.GetOrAdd(term, 1) + 1) + 1.0)
            let slot, sign = featureSlot term
            // `+`, not `=`: two terms may hash to one slot, and a collision
            // must accumulate rather than let the last term written win.
            vec[slot] <- vec[slot] + float32 (sign * tfScore * idf)

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
        // tax. Bump the version literal (in `GlobalModelId` /
        // `ScopedModelId`) on a real algorithm change — different
        // normalisation, different dimension assignment, different
        // tokeniser — so existing chunks get re-indexed once. Feature
        // hashing was exactly such a change, and both literals moved.
        //
        // Phase 14z: supplied by the factory rather than hard-coded —
        // `GlobalModelId` for the unscoped providers, `ScopedModelId`
        // for a scope-keyed one, because a per-scope IDF state IS a
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

/// Phase 671 — the resolver entry point for `EmbeddingProviderEnv.fromEnv`
/// (`TOOLUP_EMBEDDING_PROVIDER=local`). `createPersistent` when the
/// deployment threads its `IBlobStorage` in, `create` when it does not.
///
/// It reads no environment variable of its own: this provider has no
/// model or dimension to select — the model id is a build constant
/// (`GlobalModelId`) and the dimensionality is fixed at 512 by the
/// hashed feature space, so `TOOLUP_EMBEDDING_MODEL` /
/// `TOOLUP_EMBEDDING_DIMENSIONS` have nothing to say to it. It is
/// therefore total: `None` from a resolver means "selected, but not
/// constructible here", and that cannot arise for this companion.
///
/// Returns `IEmbeddingProvider option` rather than `IEmbeddingProvider`
/// because the resolver contract is one shape for every companion — an
/// API-backed one legitimately declines.
let fromEnv (blobStorage: IBlobStorage option) : IEmbeddingProvider option =
    match blobStorage with
    | Some blob -> Some(createPersistent blob)
    | None -> Some(create ())

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