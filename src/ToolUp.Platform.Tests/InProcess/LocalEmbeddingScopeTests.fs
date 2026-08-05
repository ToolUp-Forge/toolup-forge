module ToolUp.Platform.Tests.InProcess.LocalEmbeddingScopeTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 14z — scope-keyed LocalEmbeddingProvider ──────────────────
//
// The claim under test is an ISOLATION claim, not an invariance one, and
// the distinction is the whole phase. `LocalEmbeddingProvider` is a
// TF-IDF embedder: a vector is a function of the text AND of the
// vocabulary the provider has accumulated. One global vocabulary shared
// by every team therefore means Team A's uploads move Team B's query
// vectors — chunks stay scope-isolated (that is the vector store's job),
// but embedding-quality variance leaks across the tenant boundary.
//
// So these cases do NOT assert that identical text embeds identically in
// two scopes. Under per-scope vocabularies it deliberately does not, and
// asserting otherwise would pin the very coupling the phase removes.
// What they assert is the falsifiable pair:
//
//   1. **Independence** — a scope's vector is unaffected by everything
//      every other scope has ever embedded. Each isolation case is
//      written as a differential against a PRISTINE family fed only the
//      scope's own history, so sharing state across scopes makes it red.
//   2. **Determinism** — two families fed the same per-scope history in
//      the same order produce identical vectors, so "isolated" does not
//      quietly mean "arbitrary".
//
// Plus the operational surface the isolation makes safe: a per-scope
// reset that cannot touch a sibling, per-scope persistence paths, and
// the GP 11 guarantee that the unscoped factories are untouched.

let private embed (p: IEmbeddingProvider) (text: string) =
    p.GenerateEmbedding text |> Async.RunSynchronously

/// Feed a scope a small corpus, then return the vector it produces for
/// `query`. Deliberately goes through the public `For` surface only.
let private feedThenQuery
    (family: LocalEmbeddingProvider.ScopedLocalEmbeddingProviders)
    (scope: VectorScope)
    (corpus: string list)
    (query: string)
    : float32 array =
    let provider = family.For scope

    for text in corpus do
        embed provider text |> ignore

    embed provider query

let private teamACorpus: string list = [
    "quarterly revenue forecast for the northern division"
    "northern division headcount planning and attrition"
    "revenue recognition policy for multi-year contracts"
]

let private teamBCorpus = [
    "greenhouse irrigation schedule for the tomato beds"
    "soil moisture sensors and irrigation controller wiring"
]

let private platformCorpus = [
    "company-wide information security policy and incident response"
    "acceptable use policy for corporate devices"
]

let private query = "northern division revenue"

/// A stand-in for every production embedder: a pure function of the
/// text, with no state to key per scope and no way to answer the Phase
/// 14z capability probe. Deterministic bag-of-words so retrieval over it
/// still ranks.
type private StatelessEmbedder() =
    static member val Dim = 32 with get

    static member Bow(text: string) : float32 array =
        let v = Array.zeroCreate<float32> StatelessEmbedder.Dim

        let words =
            text
                .ToLowerInvariant()
                .Split([| ' '; '\n'; '\t'; '.'; ','; '-' |], System.StringSplitOptions.RemoveEmptyEntries)

        for w in words do
            let h = (abs (w.GetHashCode())) % StatelessEmbedder.Dim
            v[h] <- v[h] + 1.0f

        let mag = v |> Array.sumBy (fun x -> x * x) |> sqrt

        if mag > 0.0f then
            for i in 0 .. v.Length - 1 do
                v[i] <- v[i] / mag

        v

    interface IEmbeddingProvider with
        member _.Dimensions = StatelessEmbedder.Dim
        member _.ProviderId = "stateless-test"
        member _.ModelId = "bow-v1"
        member _.GenerateEmbedding text = async { return StatelessEmbedder.Bow text }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map StatelessEmbedder.Bow |> Seq.toArray }

// ─── Scope-key derivation ────────────────────────────────────────────

let scopeKeyTests =
    testList "Phase 14z — scope-key derivation" [
        test "each VectorScope case renders a distinct, disjoint key prefix" {
            Expect.equal (LocalEmbeddingProvider.ScopeKey.ofScope VectorScope.Platform) "platform" "platform"

            Expect.equal (LocalEmbeddingProvider.ScopeKey.ofScope VectorScope.Deployment) "deployment" "deployment"

            Expect.equal (LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.Team "acme")) "team-acme" "team"

            Expect.equal (LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.User "acme")) "user-acme" "user"
        }

        test "a team and a user with the same id do not collide" {
            let t = LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.Team "admins")
            let u = LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.User "admins")
            Expect.notEqual t u "team-{id} and user-{id} are distinct keys"
        }

        test "path separators in an id cannot escape the blob prefix" {
            let key = LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.Team "../../etc")

            Expect.isFalse (key.Contains "/") "no forward slash survives escaping"
            Expect.isFalse (key.Contains "\\") "no backslash survives escaping"
            Expect.isFalse (key.Contains ".") "no dot survives, so '..' is unconstructable"
        }

        test "escaping is injective — % is escaped before the characters it encodes" {
            // Without escaping `%` first, `Team "a%2Fb"` and `Team "a/b"`
            // would both render `team-a%2Fb` and silently share one IDF
            // dictionary — a cross-team leak reachable by naming a team.
            let literal = LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.Team "a%2Fb")
            let slashed = LocalEmbeddingProvider.ScopeKey.ofScope (VectorScope.Team "a/b")
            Expect.notEqual literal slashed "distinct ids must render distinct keys"
        }
    ]

// ─── Isolation: the leak the phase closes ────────────────────────────

let isolationTests =
    testList "Phase 14z — per-scope IDF isolation" [
        test "one team's corpus does not move another team's query vector" {
            // Contaminated family: Team A embeds a large, topically
            // overlapping corpus BEFORE Team B queries.
            let contaminated = LocalEmbeddingProvider.createScoped ()
            feedThenQuery contaminated (VectorScope.Team "a") teamACorpus query |> ignore

            let bUnderContamination =
                feedThenQuery contaminated (VectorScope.Team "b") teamBCorpus query

            // Pristine family: Team B's own history, and nothing else.
            let pristine = LocalEmbeddingProvider.createScoped ()
            let bAlone = feedThenQuery pristine (VectorScope.Team "b") teamBCorpus query

            Expect.sequenceEqual
                bUnderContamination
                bAlone
                "Team B's vector must be a function of Team B's history alone — a shared vocabulary makes these differ"
        }

        test "the Platform-scope vocabulary is separate from every team's" {
            let contaminated = LocalEmbeddingProvider.createScoped ()

            feedThenQuery contaminated VectorScope.Platform platformCorpus query |> ignore

            let teamUnderPlatformUploads =
                feedThenQuery contaminated (VectorScope.Team "a") teamACorpus query

            let pristine = LocalEmbeddingProvider.createScoped ()
            let teamAlone = feedThenQuery pristine (VectorScope.Team "a") teamACorpus query

            Expect.sequenceEqual
                teamUnderPlatformUploads
                teamAlone
                "Platform Admin uploads must not shape team query embeddings"
        }

        test "isolation runs in both directions — a team cannot move Platform's vectors" {
            let contaminated = LocalEmbeddingProvider.createScoped ()
            feedThenQuery contaminated (VectorScope.Team "a") teamACorpus query |> ignore

            let platformUnderTeamTraffic =
                feedThenQuery contaminated VectorScope.Platform platformCorpus query

            let pristine = LocalEmbeddingProvider.createScoped ()
            let platformAlone = feedThenQuery pristine VectorScope.Platform platformCorpus query

            Expect.sequenceEqual
                platformUnderTeamTraffic
                platformAlone
                "team uploads must not shape Platform-scope embeddings"
        }

        test "isolated does not mean arbitrary — the per-scope function is deterministic" {
            let first = LocalEmbeddingProvider.createScoped ()
            let second = LocalEmbeddingProvider.createScoped ()

            let a = feedThenQuery first (VectorScope.Team "a") teamACorpus query
            let b = feedThenQuery second (VectorScope.Team "a") teamACorpus query

            Expect.sequenceEqual a b "same scope, same history, same order ⇒ same vector"
        }

        test "the guard is load-bearing — a differently-fed scope DOES differ" {
            // The control for every case above: if `sequenceEqual` on
            // these vectors could not fail, the isolation assertions
            // would be vacuous. Feeding one scope a different corpus
            // must move its vector.
            let family = LocalEmbeddingProvider.createScoped ()
            let withCorpus = feedThenQuery family (VectorScope.Team "a") teamACorpus query

            let withoutCorpus =
                feedThenQuery (LocalEmbeddingProvider.createScoped ()) (VectorScope.Team "a") [] query

            Expect.isFalse
                (System.Linq.Enumerable.SequenceEqual(withCorpus, withoutCorpus))
                "corpus history must change the vector, else the isolation assertions prove nothing"
        }

        test "For returns one accumulating instance per scope" {
            let family = LocalEmbeddingProvider.createScoped ()
            let first = family.For(VectorScope.Team "a")
            let second = family.For(VectorScope.Team "a")
            Expect.isTrue (System.Object.ReferenceEquals(first, second)) "same scope ⇒ same provider instance"

            let other = family.For(VectorScope.Team "b")
            Expect.isFalse (System.Object.ReferenceEquals(first, other)) "distinct scopes ⇒ distinct providers"
        }
    ]

// ─── Per-scope reset ─────────────────────────────────────────────────

let resetTests =
    testList "Phase 14z — per-scope reset" [
        test "ResetScope wipes only the named scope" {
            let family = LocalEmbeddingProvider.createScoped ()
            feedThenQuery family (VectorScope.Team "a") teamACorpus query |> ignore
            let bBeforeReset = feedThenQuery family (VectorScope.Team "b") teamBCorpus query

            family.ResetScope(VectorScope.Team "a") |> Async.RunSynchronously

            Expect.isFalse
                (family.KnownScopes() |> List.contains "team-a")
                "the reset scope is dropped from the live set"

            Expect.isTrue (family.KnownScopes() |> List.contains "team-b") "the sibling scope survives"

            // Team B keeps embedding into the state it already had — a
            // reset of A must be invisible to B. Compared against the
            // same family continuing, not a fresh one.
            let bAfterReset = family.For(VectorScope.Team "b") |> fun p -> embed p query
            Expect.notEqual (Array.length bAfterReset) 0 "team B still embeds"
            Expect.equal (Array.length bAfterReset) (Array.length bBeforeReset) "team B's space is unchanged in shape"
        }

        test "a reset scope restarts from an empty vocabulary" {
            let family = LocalEmbeddingProvider.createScoped ()
            feedThenQuery family (VectorScope.Team "a") teamACorpus query |> ignore
            family.ResetScope(VectorScope.Team "a") |> Async.RunSynchronously
            let afterReset = feedThenQuery family (VectorScope.Team "a") [] query

            let pristine =
                feedThenQuery (LocalEmbeddingProvider.createScoped ()) (VectorScope.Team "a") [] query

            Expect.sequenceEqual afterReset pristine "post-reset state is indistinguishable from never-used"
        }

        test "ResetScope is idempotent on a scope that never embedded" {
            let family = LocalEmbeddingProvider.createScoped ()
            family.ResetScope(VectorScope.Team "never") |> Async.RunSynchronously
            family.ResetScope(VectorScope.Team "never") |> Async.RunSynchronously
            Expect.isEmpty (family.KnownScopes()) "no state was created by resetting"
        }
    ]

// ─── Persistence ─────────────────────────────────────────────────────

let persistenceTests =
    testList "Phase 14z — per-scope persistence" [
        test "each scope persists to its own blob, and never to the legacy global path" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let family = LocalEmbeddingProvider.createScopedPersistent storage
            feedThenQuery family (VectorScope.Team "a") teamACorpus query |> ignore
            feedThenQuery family VectorScope.Platform platformCorpus query |> ignore

            let names = storage.List("_platform", "embeddings/") |> Async.RunSynchronously

            Expect.contains names "embeddings/team-a/_local-tfidf-state.json" "team state at its scope-keyed path"

            Expect.contains names "embeddings/platform/_local-tfidf-state.json" "platform state at its scope-keyed path"

            Expect.isFalse
                (names |> List.contains "embeddings/_local-tfidf-state.json")
                "the scoped family must not write the unscoped provider's legacy blob"
        }

        test "a restart rehydrates each scope from its own blob" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let first = LocalEmbeddingProvider.createScopedPersistent storage
            let beforeRestart = feedThenQuery first (VectorScope.Team "a") teamACorpus query

            // A second family over the same storage stands in for a
            // process restart: it has no in-memory state at all.
            let second = LocalEmbeddingProvider.createScopedPersistent storage
            let afterRestart = second.For(VectorScope.Team "a") |> fun p -> embed p query

            Expect.equal
                (Array.length afterRestart)
                (Array.length beforeRestart)
                "the rehydrated scope embeds into the same-sized space, so existing chunks stay comparable"
        }

        test "the persisted state is real JSON, not an empty object" {
            // Regression guard for the defect Phase 14z found in the
            // shipped path: the state DTO was a NON-PUBLIC type, and
            // System.Text.Json's reflection resolver finds no members on
            // one — so it emitted `{}`, silently, and every persisted
            // blob since the feature shipped was two bytes. Nothing
            // failed: an empty state is indistinguishable from a first
            // run, so `createPersistent` quietly behaved like `create ()`.
            // Asserting only the round-trip would not pin the shape, so
            // the bytes are inspected directly.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let family = LocalEmbeddingProvider.createScopedPersistent storage
            feedThenQuery family (VectorScope.Team "a") teamACorpus query |> ignore

            let raw =
                storage.Download("_platform", "embeddings/team-a/_local-tfidf-state.json")
                |> Async.RunSynchronously

            match raw with
            | Error e -> failtestf "expected a persisted state blob, got %s" e
            | Ok bytes ->
                let json = System.Text.Encoding.UTF8.GetString bytes
                Expect.isGreaterThan json.Length 2 "the blob must carry state, not '{}'"
                Expect.stringContains json "docCount" "the document count is persisted"
                Expect.stringContains json "northern" "the accumulated vocabulary is persisted"
        }

        test "the unscoped createPersistent round-trips too — same defect, same fix" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let first = LocalEmbeddingProvider.createPersistent storage

            for text in teamACorpus do
                embed first text |> ignore

            let beforeRestart = embed first query

            let second = LocalEmbeddingProvider.createPersistent storage
            let afterRestart = embed second query

            Expect.equal
                (Array.length afterRestart)
                (Array.length beforeRestart)
                "a restart must re-hydrate the IDF dictionary, which is what createPersistent exists to do"
        }

        test "ResetScope deletes the scope's persisted state" {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let family = LocalEmbeddingProvider.createScopedPersistent storage
            feedThenQuery family (VectorScope.Team "a") teamACorpus query |> ignore
            feedThenQuery family (VectorScope.Team "b") teamBCorpus query |> ignore

            family.ResetScope(VectorScope.Team "a") |> Async.RunSynchronously

            let names = storage.List("_platform", "embeddings/") |> Async.RunSynchronously

            Expect.isFalse
                (names |> List.contains "embeddings/team-a/_local-tfidf-state.json")
                "the reset scope's blob is gone"

            Expect.contains names "embeddings/team-b/_local-tfidf-state.json" "the sibling scope's blob survives"
        }
    ]

// ─── GP 11 — the unscoped surface is untouched ───────────────────────

let backwardCompatibilityTests =
    testList "Phase 14z — GP 11 backward compatibility" [
        test "the unscoped providers still report local-tfidf-v1" {
            let p = LocalEmbeddingProvider.create ()
            Expect.equal p.ModelId LocalEmbeddingProvider.GlobalModelId "unscoped model id is unchanged"
            Expect.equal p.ModelId "local-tfidf-v1" "and its literal value is pinned"
        }

        test "scope-keyed providers report a distinct model id so ReembeddingService re-indexes on the swap" {
            let family = LocalEmbeddingProvider.createScoped ()
            let p = family.For(VectorScope.Team "a")

            Expect.equal p.ModelId (LocalEmbeddingProvider.scopedModelIdFor (VectorScope.Team "a")) "scoped model id"

            Expect.equal p.ModelId "local-tfidf-v2#team-a" "and its literal value is pinned"

            Expect.stringStarts p.ModelId LocalEmbeddingProvider.ScopedModelId "the family prefix is the v2 literal"

            Expect.notEqual
                p.ModelId
                (LocalEmbeddingProvider.create ()).ModelId
                "a per-scope vocabulary is a different embedding function — the versions must not be confusable"
        }

        test "provider identity and dimensions are unchanged across both shapes" {
            let unscoped = LocalEmbeddingProvider.create ()
            let scoped = (LocalEmbeddingProvider.createScoped ()).For VectorScope.Platform
            Expect.equal scoped.ProviderId unscoped.ProviderId "same provider family"
            Expect.equal scoped.Dimensions unscoped.Dimensions "same dimensionality"
        }

        test "the unscoped provider keeps its single global vocabulary — deliberately" {
            // The old behaviour is not a bug that leaked into the new
            // surface; it is the opt-out. Pinning it here means a future
            // change that quietly scope-keys `create ()` fails loudly
            // rather than forcing an unannounced corpus reembed.
            let shared = LocalEmbeddingProvider.create ()

            for text in teamACorpus do
                embed shared text |> ignore

            let contaminated = embed shared query

            let fresh = LocalEmbeddingProvider.create ()
            let clean = embed fresh query

            Expect.isFalse
                (System.Linq.Enumerable.SequenceEqual(contaminated, clean))
                "the unscoped provider still shares one vocabulary across callers, as before"
        }
    ]
// ─── Phase 14z, Option 1 — the capability probe ──────────────────────
//
// Everything below wires the scope-keyed family into the RAG pipeline.
// The design is a capability PROBE (`IScopedEmbeddingProviderFactory`),
// not a widened `IEmbeddingProvider`, and the probe's most important
// property is the one it must NOT have: a stateless provider must never
// answer it. Cross-scope retrieval costs one query embed per authorised
// scope, which is right for an in-process TF-IDF embedder and
// indefensible against a metered API — so "the probe fails on a
// stateless provider" is a correctness property of the cost model, not a
// tidiness preference, and it is pinned as hard as the positive cases.

let capabilityProbeTests =
    testList "Phase 14z — scope-keyed capability probe" [
        test "the scoped family answers the probe; the unscoped providers do not" {
            let family = LocalEmbeddingProvider.createScoped () :> IEmbeddingProvider

            Expect.isTrue (ScopedEmbedding.isScopeKeyed family) "the scoped family is scope-keyed"

            Expect.isFalse
                (ScopedEmbedding.isScopeKeyed (LocalEmbeddingProvider.create ()))
                "the unscoped local provider is NOT — it still shares one vocabulary (GP 11)"

            Expect.isFalse
                (ScopedEmbedding.isScopeKeyed (StatelessEmbedder() :> IEmbeddingProvider))
                "a stateless embedder can never be scope-keyed"
        }

        test "forScope is the identity on a provider that is not scope-keyed" {
            let stateless = StatelessEmbedder() :> IEmbeddingProvider

            let resolved = ScopedEmbedding.forScope stateless (VectorScope.Team "a")

            Expect.isTrue
                (System.Object.ReferenceEquals(resolved, stateless))
                "no wrapper, no substitution — the same instance the caller composed"
        }

        test "forScope routes to the scope's own accumulating provider" {
            let family = LocalEmbeddingProvider.createScoped ()
            let asProvider = family :> IEmbeddingProvider

            let viaHelper = ScopedEmbedding.forScope asProvider (VectorScope.Team "a")
            let viaFamily = family.For(VectorScope.Team "a")

            Expect.isTrue (System.Object.ReferenceEquals(viaHelper, viaFamily)) "the probe reaches the same instance"
        }

        test "the family composes AS an IEmbeddingProvider, reporting the Deployment scope's identity" {
            // It has to be an `IEmbeddingProvider` at all — `RAGServerApp.create`
            // takes one — and any caller that does not probe (a health check,
            // a diagnostic) must get a working embedder rather than a throw.
            let family = LocalEmbeddingProvider.createScoped ()
            let asProvider = family :> IEmbeddingProvider

            Expect.equal asProvider.ProviderId "local" "provider family is unchanged"
            Expect.equal asProvider.Dimensions 512 "dimensionality is unchanged"

            Expect.equal
                asProvider.ModelId
                (family.For VectorScope.Deployment).ModelId
                "the identity it advertises is the identity of the vectors it produces"
        }

        test "resetScope is a no-op on a provider that is not scope-keyed" {
            // Load-bearing: the UNSCOPED local provider holds ONE global
            // vocabulary shared by every tenant. If a scope's ResetIndex
            // could wipe it, a single tenant's reset would degrade every
            // other tenant's retrieval — a cross-tenant side effect, which
            // is the opposite of what the reset wiring is for.
            let shared = LocalEmbeddingProvider.create ()

            for text in teamACorpus do
                embed shared text |> ignore

            let before = embed shared query

            ScopedEmbedding.resetScope shared (VectorScope.Team "a")
            |> Async.RunSynchronously

            let after = embed shared query

            Expect.sequenceEqual after before "the global vocabulary survives a scope reset"
        }
    ]

// ─── Embedding-cache keying ──────────────────────────────────────────
//
// `EmbeddingCacheKey` is `{ Version; TextHash }` where `Version` is
// `{ ProviderId; ModelId; Dimensions }` — no tenant component anywhere.
// So per-scope IDF state closes the leak only if the scope key reaches
// the cache key too: two scopes reporting one `ModelId` would share
// entries, and the first scope to embed a string would serve ITS vector
// to every other scope asking for the same text. That failure is
// invisible by construction (a cached vector is indistinguishable from a
// computed one), which is exactly why it is pinned rather than reasoned
// about.

/// Counting `IEmbeddingCache` — the assertion surface is how many
/// DISTINCT keys were written, which is the property the scope
/// qualification exists to control.
type private CountingEmbeddingCache() =
    let entries =
        System.Collections.Concurrent.ConcurrentDictionary<
            ToolUp.Platform.IEmbeddingCache.EmbeddingCacheKey,
            float32 array
         >()

    member _.Keys = entries.Keys |> Seq.toList
    member _.Count = entries.Count

    interface ToolUp.Platform.IEmbeddingCache.IEmbeddingCache with
        member _.TryGet key = async {
            match entries.TryGetValue key with
            | true, v -> return Some v
            | _ -> return None
        }

        member _.Set key embedding = async { entries[key] <- embedding }
        member _.Clear() = async { entries.Clear() }

        member _.HitRate() = async { return 0.0 }

let cacheKeyingTests =
    testList "Phase 14z — embedding-cache keying under a scope-keyed provider" [
        test "the same text in two scopes writes TWO cache entries" {
            let cache = CountingEmbeddingCache()
            let family = LocalEmbeddingProvider.createScoped () :> IEmbeddingProvider

            let cached =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    family
                    (cache :> ToolUp.Platform.IEmbeddingCache.IEmbeddingCache)

            let factory =
                match ScopedEmbedding.tryFactory cached with
                | Some f -> f
                | None -> failtest "the caching decorator must forward the scope-keyed capability"

            let text = "annual leave entitlement"
            embed (factory.For(VectorScope.Team "a")) text |> ignore
            embed (factory.For(VectorScope.Team "b")) text |> ignore

            Expect.equal cache.Count 2 "one entry per scope — a shared entry would serve A's vector to B"

            let modelIds =
                cache.Keys |> List.map _.Version.ModelId |> List.distinct |> List.sort

            Expect.equal
                modelIds
                [ "local-tfidf-v2#team-a"; "local-tfidf-v2#team-b" ]
                "the scope reaches the cache key through the model id"
        }

        test "a repeat embed in the SAME scope is a cache hit, not a third entry" {
            // The control: if every call wrote a new key the case above
            // would pass for the wrong reason.
            let cache = CountingEmbeddingCache()
            let family = LocalEmbeddingProvider.createScoped () :> IEmbeddingProvider

            let cached =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    family
                    (cache :> ToolUp.Platform.IEmbeddingCache.IEmbeddingCache)

            let factory = (ScopedEmbedding.tryFactory cached).Value
            let scoped = factory.For(VectorScope.Team "a")

            let text = "annual leave entitlement"
            let first = embed scoped text
            let second = embed scoped text

            Expect.equal cache.Count 1 "one key for one (scope, text)"
            Expect.sequenceEqual second first "and the second call is served from it"
        }

        test "a STATELESS provider still writes exactly one entry for one text" {
            // GP 11 — the byte-identical path. A stateless embedder cannot
            // answer the probe, so its wrapper is the pre-14z
            // `CachingEmbeddingProvider` and its cache behaviour is
            // unchanged: one key per text, shared by every scope, which is
            // correct because the vector genuinely does not depend on
            // scope.
            let cache = CountingEmbeddingCache()
            let stateless = StatelessEmbedder() :> IEmbeddingProvider

            let cached =
                ToolUp.RAG.CachingEmbeddingProvider.create
                    stateless
                    (cache :> ToolUp.Platform.IEmbeddingCache.IEmbeddingCache)

            Expect.isFalse
                (ScopedEmbedding.isScopeKeyed cached)
                "the wrapper must not claim a capability its inner provider lacks"

            let text = "annual leave entitlement"
            embed (ScopedEmbedding.forScope cached (VectorScope.Team "a")) text |> ignore
            embed (ScopedEmbedding.forScope cached (VectorScope.Team "b")) text |> ignore

            Expect.equal cache.Count 1 "one entry, shared across scopes — the pre-14z behaviour"
        }
    ]

// ─── Cross-scope retrieval — Option 1, the acceptance criterion ──────
//
// This is what the phase was gated on. Per-scope vocabularies make
// dimension `i` denote a different term in each scope, so ONE query
// vector searched across every authorised scope compares coordinates
// that do not denote the same thing. Phase 4b's acceptance criterion —
// "a team-KB document and a Platform-KB document about the same topic
// both surface in retrieval, ranked by relevance regardless of scope" —
// is exactly what that breaks.
//
// Option 1: embed the query once per authorised scope, search each with
// its own vector, merge. Gated on the capability probe, so a stateless
// provider keeps one query vector on a byte-identical path.

/// Thread-safe embed counter — the per-scope embeds run under
/// `Async.Parallel`, so a plain mutable would under-count and quietly
/// turn the cost assertions into wishes.
type private CallCounter() =
    let mutable n = 0

    member _.Bump() =
        System.Threading.Interlocked.Increment(&n) |> ignore

    member _.Count = n

    member _.Reset() =
        System.Threading.Interlocked.Exchange(&n, 0) |> ignore

/// Counts every `GenerateEmbedding`, without otherwise altering the
/// wrapped provider (`ModelId` included, so cache keying is unchanged).
let private counted (counter: CallCounter) (inner: IEmbeddingProvider) =
    { new IEmbeddingProvider with
        member _.Dimensions = inner.Dimensions
        member _.ProviderId = inner.ProviderId
        member _.ModelId = inner.ModelId

        member _.GenerateEmbedding text = async {
            counter.Bump()
            return! inner.GenerateEmbedding text
        }

        member _.GenerateEmbeddings texts = inner.GenerateEmbeddings texts
    }

/// The scope-keyed family with every per-scope embed counted. Memoised
/// so `For` still honours the capability's same-instance-per-scope
/// contract.
type private CountingScopedFamily(family: LocalEmbeddingProvider.ScopedLocalEmbeddingProviders, counter: CallCounter) =
    let wrapped =
        System.Collections.Concurrent.ConcurrentDictionary<VectorScope, IEmbeddingProvider>()

    interface IEmbeddingProvider with
        member _.Dimensions = (family :> IEmbeddingProvider).Dimensions
        member _.ProviderId = (family :> IEmbeddingProvider).ProviderId
        member _.ModelId = (family :> IEmbeddingProvider).ModelId

        member _.GenerateEmbedding text =
            (family :> IEmbeddingProvider).GenerateEmbedding text

        member _.GenerateEmbeddings texts =
            (family :> IEmbeddingProvider).GenerateEmbeddings texts

    interface IScopedEmbeddingProviderFactory with
        member _.For scope =
            wrapped.GetOrAdd(scope, fun s -> counted counter (family.For s))

        member _.ResetScope scope = family.ResetScope scope

type private RetrievalHarness = {
    Pipeline: IRetrievalPipeline
    Dispose: unit -> unit
}

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// A pipeline over an in-memory store, seeded through
/// `IRetrievalPipeline.Index` — the production ingestion path, so the
/// write side goes through the same scope resolution the read side does.
let private harness (embedder: IEmbeddingProvider) (rows: (VectorScope * string * string) list) : RetrievalHarness =
    let storage = InMemoryBlobStorage() :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = silentLogger, flushIntervalMs = 60000)

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
            store :> IVectorStore,
            embedder,
            platformKnowledgeBase = EnabledPlatformKnowledgeBase
        )
        :> IRetrievalPipeline

    for scope, chunkId, body in rows do
        pipeline.Index chunkId { Content = body; Metadata = Map.empty } scope
        |> Async.RunSynchronously

    {
        Pipeline = pipeline
        Dispose = fun () -> (store :> System.IDisposable).Dispose()
    }

/// A caller in team `acme` who may also read the Platform KB.
let private teamCaller =
    AccessContext.unrestricted (Subject.TeamMember("user-1", "acme"))

/// The shared topic. Both on-topic documents are about it; the two noise
/// documents (one per scope) are not — so "surfaced" cannot pass
/// vacuously on a pipeline that simply returns everything.
let private leaveQuery = "annual leave entitlement carry over"

let private leaveRows = [
    VectorScope.Team "acme",
    "team-leave",
    "annual leave entitlement for acme staff is twenty eight days with carry over"
    VectorScope.Platform, "platform-leave", "company annual leave entitlement policy explains carry over and accrual"
    VectorScope.Team "acme", "team-noise", "office kitchen rota and dishwasher loading conventions"
    VectorScope.Platform, "platform-noise", "corporate travel booking tool onboarding walkthrough"
]

// ── The acceptance case's embedder, and why it is not the TF-IDF one ──
//
// The claim Option 1 has to satisfy is a claim about the PIPELINE: when
// two scopes have incomparable vector spaces, retrieval must still rank
// a document from each together. `ScopedBowEmbedder` states exactly that
// geometry and nothing else — a deterministic bag-of-words whose
// dimension assignment is ROTATED per scope, so the same word occupies a
// different coordinate in each scope and a query vector from one scope
// is orthogonal to every chunk of another. Collapse the pipeline back to
// one global query vector and every cross-scope score goes to zero,
// which is what makes the falsification bite.
//
// The real `ScopedLocalEmbeddingProviders` is deliberately NOT used
// here, and the reason is a pre-existing property of the TF-IDF
// provider rather than anything this phase introduced: its vocabulary is
// re-sorted by document frequency on EVERY embed, and previously-indexed
// chunks are not re-embedded (the file header calls this "approximate
// but sufficient for dev"). On a corpus of four documents each new embed
// therefore permutes the dimension→term assignment, so a chunk indexed
// early is already in a stale space by query time — with or without
// scope-keying, and on a single scope just as much as across two. It is
// filed to TIDY-UP rather than fixed here. That provider's own scope
// behaviour is covered by the isolation / persistence / reset lists
// above, and by the two cases below that use it for what it can pin:
// which scopes get searched at all, and what provenance survives the
// merge.

let private bowDim = 64

/// Deterministic, collision-free, and — the point — different per scope.
let private shiftFor (scope: VectorScope) =
    match scope with
    | Platform -> 0
    | Deployment -> 7
    | Team teamId -> 13 + (teamId.Length % 5)
    | User userId -> 29 + (userId.Length % 5)

type private ScopedBowEmbedder() =
    static member Bow (shift: int) (text: string) : float32 array =
        let v = Array.zeroCreate<float32> bowDim

        let words =
            text
                .ToLowerInvariant()
                .Split([| ' '; '\n'; '\t'; '.'; ','; '-' |], System.StringSplitOptions.RemoveEmptyEntries)

        for w in words do
            let h = ((abs (w.GetHashCode())) + shift) % bowDim
            v[h] <- v[h] + 1.0f

        let mag = v |> Array.sumBy (fun x -> x * x) |> sqrt

        if mag > 0.0f then
            for i in 0 .. v.Length - 1 do
                v[i] <- v[i] / mag

        v

    member private _.ForShift(shift: int, modelId: string) =
        { new IEmbeddingProvider with
            member _.Dimensions = bowDim
            member _.ProviderId = "scoped-bow-test"
            member _.ModelId = modelId
            member _.GenerateEmbedding text = async { return ScopedBowEmbedder.Bow shift text }

            member _.GenerateEmbeddings texts = async {
                return texts |> Seq.map (ScopedBowEmbedder.Bow shift) |> Seq.toArray
            }
        }

    interface IEmbeddingProvider with
        member this.Dimensions = bowDim
        member this.ProviderId = "scoped-bow-test"
        member this.ModelId = "scoped-bow-v1#deployment"

        member this.GenerateEmbedding text =
            (this :> IScopedEmbeddingProviderFactory).For(Deployment).GenerateEmbedding text

        member this.GenerateEmbeddings texts =
            (this :> IScopedEmbeddingProviderFactory).For(Deployment).GenerateEmbeddings texts

    interface IScopedEmbeddingProviderFactory with
        member this.For scope =
            this.ForShift(shiftFor scope, "scoped-bow-v1#" + LocalEmbeddingProvider.ScopeKey.ofScope scope)

        member _.ResetScope _ = async.Return()

let private retrieveLeave (h: RetrievalHarness) (topK: int) =
    let request =
        RetrievalRequest.create leaveQuery [ VectorScope.Team "acme"; VectorScope.Platform ] topK Interleaved

    h.Pipeline.Retrieve request teamCaller |> Async.RunSynchronously

let crossScopeRetrievalTests =
    testList "Phase 14z — cross-scope retrieval under a scope-keyed embedder" [
        test "ACCEPTANCE (Phase 4b) — a team doc and a Platform doc on one topic both surface, ranked" {
            // Sanity on the fixture before the assertion depends on it:
            // the two scopes must genuinely have incomparable spaces, or
            // the case would pass on a pipeline that never learned to
            // embed per scope.
            Expect.notEqual (shiftFor (Team "acme")) (shiftFor Platform) "the two scopes rotate differently"

            let h = harness (ScopedBowEmbedder() :> IEmbeddingProvider) leaveRows

            try
                let results = retrieveLeave h 4
                let ids = results |> List.map _.ChunkId |> Set.ofList

                Expect.isTrue (ids.Contains "team-leave") "the team-scope document surfaces"
                Expect.isTrue (ids.Contains "platform-leave") "the Platform-scope document surfaces"

                // "Ranked", not merely "returned": under one global query
                // vector against per-scope vocabularies the on-topic chunks
                // score at or near zero and lose to noise, so the ordering
                // is the assertion with teeth.
                let topTwo = results |> List.truncate 2 |> List.map _.ChunkId |> Set.ofList

                Expect.equal
                    topTwo
                    (Set.ofList [ "team-leave"; "platform-leave" ])
                    "the two on-topic documents outrank the off-topic ones from both scopes"

                let onTopicScores =
                    results
                    |> List.filter (fun m -> m.ChunkId = "team-leave" || m.ChunkId = "platform-leave")
                    |> List.map _.Score

                for s in onTopicScores do
                    Expect.isGreaterThan s 0.1 "each on-topic match carries real similarity, not a near-zero artefact"
            finally
                h.Dispose()
        }

        test "ISOLATION — an unauthorised team's document never enters the merge" {
            let rows =
                leaveRows
                @ [
                    VectorScope.Team "other",
                    "other-leave",
                    "annual leave entitlement for other staff with carry over rules"
                ]

            let h = harness (LocalEmbeddingProvider.createScoped () :> IEmbeddingProvider) rows

            try
                let ids = retrieveLeave h 8 |> List.map _.ChunkId |> Set.ofList

                Expect.isFalse (ids.Contains "other-leave") "a scope the caller cannot read is never searched"
                Expect.isTrue (ids.Contains "team-leave") "and the caller's own scope still surfaces"
            finally
                h.Dispose()
        }

        test "the merge keeps each match's own scope — provenance is not flattened" {
            let h =
                harness (LocalEmbeddingProvider.createScoped () :> IEmbeddingProvider) leaveRows

            try
                let byId = retrieveLeave h 4 |> List.map (fun m -> m.ChunkId, m.Scope) |> Map.ofList

                Expect.equal (byId.TryFind "team-leave") (Some(VectorScope.Team "acme")) "team match keeps its scope"

                Expect.equal
                    (byId.TryFind "platform-leave")
                    (Some VectorScope.Platform)
                    "platform match keeps its scope"
            finally
                h.Dispose()
        }

        test "GP 11 — a STATELESS embedder takes the single-vector path, one embed per query" {
            // The cost guard. N embeds per query against a metered provider
            // is what the capability probe exists to prevent, and counting
            // the calls is the only way to observe it: the RESULTS look the
            // same either way, which is precisely why this is pinned.
            let counter = CallCounter()

            let h =
                harness (counted counter (StatelessEmbedder() :> IEmbeddingProvider)) leaveRows

            try
                counter.Reset()
                let results = retrieveLeave h 4

                Expect.equal counter.Count 1 "exactly ONE query embed across two authorised scopes"

                Expect.isNonEmpty results "and it still retrieves"
            finally
                h.Dispose()
        }

        test "a scope-keyed embedder spends one query embed PER authorised scope — deliberately" {
            // The other side of the same guard, recorded rather than
            // discovered later. Two authorised scopes, two embeds:
            // affordable only because the probe fails on every API-backed
            // provider.
            let counter = CallCounter()

            let family =
                CountingScopedFamily(LocalEmbeddingProvider.createScoped (), counter) :> IEmbeddingProvider

            let h = harness family leaveRows

            try
                counter.Reset()
                retrieveLeave h 4 |> ignore
                Expect.equal counter.Count 2 "one embed per authorised scope"
            finally
                h.Dispose()
        }
    ]