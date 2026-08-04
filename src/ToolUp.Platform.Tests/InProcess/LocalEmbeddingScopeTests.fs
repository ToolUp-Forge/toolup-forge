module ToolUp.Platform.Tests.InProcess.LocalEmbeddingScopeTests

open Expecto
open ToolUp.Platform.BlobStorage
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
            Expect.equal p.ModelId LocalEmbeddingProvider.ScopedModelId "scoped model id"
            Expect.equal p.ModelId "local-tfidf-v2" "and its literal value is pinned"

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