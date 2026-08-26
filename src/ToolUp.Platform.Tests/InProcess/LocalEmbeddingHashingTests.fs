module ToolUp.Platform.Tests.InProcess.LocalEmbeddingHashingTests

open Expecto
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Feature-hashed dimension assignment ─────────────────────────────
//
// `LocalEmbeddingProvider` used to assign dimension `i` to `vocab[i]`,
// where `vocab` was the terms ranked by document frequency and rebuilt
// on EVERY embed. Previously-indexed chunks were never re-embedded, so a
// chunk indexed early kept coordinates in a vocabulary the provider had
// since re-sorted — and because a *query* embeds too, the assignment
// moved underneath the very comparison it was about to be used for. On a
// small corpus that made cosine similarity meaningless, and the failure
// was silent and total: retrieval returned confidently ranked nonsense
// rather than degrading.
//
// The fix hashes a term to a fixed slot, so the assignment is a function
// of the term and nothing else. These cases assert that as a falsifiable
// property rather than as an implementation detail:
//
//   1. **Fixed assignment** — the dimensions a text occupies do not move
//      as the provider's corpus grows. This is the bug, stated directly.
//   2. **Alignment** — a chunk indexed BEFORE a query is issued is still
//      comparable to it, which is the observable consequence and the
//      shape the Phase 14z acceptance case tripped over.
//   3. **Determinism** — the assignment is identical across independent
//      provider instances and independent of the order documents
//      arrived in, so "fixed" does not quietly mean "fixed per process".
//
// What is deliberately NOT asserted: that a text embeds to the same
// VECTOR regardless of history. It does not, and it should not — IDF
// weighting still adapts as documents arrive. Adaptive weighting
// rescales coordinates; the bug was that it also permuted them. The
// cases below separate those two by asserting on the support (which
// dimensions carry weight) where the claim is about assignment, and on
// ranking where the claim is about comparability.

let private embed (p: IEmbeddingProvider) (text: string) =
    p.GenerateEmbedding text |> Async.RunSynchronously

/// The dimensions a vector actually occupies. The claim about assignment
/// is a claim about this set — not about the weights, which IDF is still
/// free to move.
let private support (v: float32 array) =
    v
    |> Array.indexed
    |> Array.filter (fun (_, x) -> x <> 0.0f)
    |> Array.map fst
    |> Set.ofArray

/// Zips on the shorter side rather than assuming equal lengths. Under
/// the pre-hashing scheme a vector was sized by the vocabulary known at
/// embed time, so an early chunk and a later query genuinely had
/// different lengths — a helper that assumed otherwise would throw
/// instead of scoring, and the non-vacuity check below would then be
/// reading an exception rather than a ranking.
let private cosine (a: float32 array) (b: float32 array) =
    let n = min a.Length b.Length
    let mutable dot = 0.0
    let mutable ma = 0.0
    let mutable mb = 0.0

    for i in 0 .. n - 1 do
        dot <- dot + (float a[i] * float b[i])
        ma <- ma + (float a[i] * float a[i])
        mb <- mb + (float b[i] * float b[i])

    if ma = 0.0 || mb = 0.0 then
        0.0
    else
        dot / (sqrt ma * sqrt mb)

let private probe = "annual leave entitlement carry over"

/// Deliberately disjoint from `probe` in vocabulary, and large enough to
/// have re-ranked a 512-term vocabulary several times over.
let private growthCorpus = [
    "quarterly revenue forecast for the northern division"
    "northern division headcount planning and attrition"
    "revenue recognition policy for multi-year contracts"
    "greenhouse irrigation schedule for the tomato beds"
    "soil moisture sensors and irrigation controller wiring"
    "office kitchen rota and dishwasher loading conventions"
    "corporate travel booking tool onboarding walkthrough"
    "warehouse pallet racking inspection and load ratings"
]

let dimensionAssignmentTests =
    testList "Feature-hashed dimension assignment" [
        test "a text's dimensions do not move as the provider's corpus grows" {
            let p = LocalEmbeddingProvider.create ()

            let before = embed p probe

            for text in growthCorpus do
                embed p text |> ignore

            let after = embed p probe

            Expect.isNonEmpty (support before) "the probe must occupy some dimensions, or the comparison is vacuous"

            Expect.equal
                (support after)
                (support before)
                "the dimensions a term occupies are a function of the term — a corpus that re-ranked the vocabulary must not move them"
        }

        test "the weights DO still move — the case above is not asserting a frozen embedder" {
            // The control for the case above. Feature hashing fixes the
            // dimension assignment and nothing else: IDF weighting is
            // still adaptive, so the same probe against a grown corpus
            // must produce different WEIGHTS on the same dimensions. If
            // this ever went green by the vector being identical, the
            // assignment case would be passing for the wrong reason.
            let p = LocalEmbeddingProvider.create ()
            let before = embed p probe

            for text in growthCorpus @ [ "annual leave carry over is capped at five days" ] do
                embed p text |> ignore

            let after = embed p probe

            Expect.isFalse
                (System.Linq.Enumerable.SequenceEqual(before, after))
                "IDF still adapts — a corpus that changes the probe's term frequencies must change its weights"
        }

        test "every vector carries the full declared dimensionality" {
            // The pre-hashing form sized each vector by the vocabulary
            // known at embed time, so an early call returned a vector
            // SHORTER than `Dimensions` advertised — a second way two
            // vectors from one provider could fail to be comparable.
            let p = LocalEmbeddingProvider.create ()
            let first = embed p probe
            Expect.equal first.Length p.Dimensions "the very first embed is already full-width"

            for text in growthCorpus do
                embed p text |> ignore

            let later = embed p probe
            Expect.equal later.Length p.Dimensions "and so is one taken after the corpus has grown"
        }

        test "the assignment is identical across independent provider instances" {
            let a = LocalEmbeddingProvider.create ()
            let b = LocalEmbeddingProvider.create ()

            for text in growthCorpus do
                embed a text |> ignore
                embed b text |> ignore

            let va = embed a probe
            let vb = embed b probe

            Expect.sequenceEqual vb va "same history ⇒ same vector, in a second process as much as this one"
        }

        test "arrival order does not change what a corpus produces" {
            // A hash is order-blind by construction, but the property is
            // worth pinning rather than inferring: it is the one a
            // deployment relies on when documents are ingested
            // concurrently and no two runs see the same order.
            let forwards = LocalEmbeddingProvider.create ()
            let backwards = LocalEmbeddingProvider.create ()

            for text in growthCorpus do
                embed forwards text |> ignore

            for text in List.rev growthCorpus do
                embed backwards text |> ignore

            Expect.sequenceEqual
                (embed backwards probe)
                (embed forwards probe)
                "the same corpus in two arrival orders must leave the provider in the same embedding state"
        }

        test "the hash is culture-invariant and process-stable, not String.GetHashCode" {
            // `String.GetHashCode` is randomised per process on .NET, so
            // a provider built on it would reproduce the original bug
            // across restarts while every single-process test stayed
            // green. The observable proxy for "not GetHashCode" is that
            // the slots are a fixed function of the term — pinned here as
            // literal values, so a future change to the hash is a
            // deliberate act with a corpus reembed attached rather than a
            // silent geometry change.
            let p = LocalEmbeddingProvider.create ()
            let v = embed p "entitlement"

            Expect.equal
                (support v |> Set.toList)
                [ 40 ]
                "one term occupies one fixed, hash-derived slot — change this only alongside a ModelId bump"
        }
    ]

// ─── Query / index space alignment ───────────────────────────────────

let private onTopic =
    "annual leave entitlement for staff is twenty eight days with carry over"

let private offTopic = [
    "office kitchen rota and dishwasher loading conventions"
    "corporate travel booking tool onboarding walkthrough"
    "warehouse pallet racking inspection and load ratings"
]

let spaceAlignmentTests =
    testList "Query / index space alignment" [
        test "a chunk indexed BEFORE the query still outranks unrelated ones" {
            // The Phase 14z regression shape, on the smallest corpus that
            // can show it: index four documents one at a time — which is
            // what ingestion does — and then issue a query, which embeds
            // too. Under the ranked vocabulary the on-topic document's
            // coordinates were stale by the time the query existed, and
            // it lost to documents sharing not one word with the query.
            let p = LocalEmbeddingProvider.create ()

            let onTopicVec = embed p onTopic
            let offTopicVecs = offTopic |> List.map (embed p)

            // The query embeds LAST, so every stored vector predates it —
            // the ordering is the point, not an artefact of the fixture.
            let queryVec = embed p probe

            let onTopicScore = cosine queryVec onTopicVec
            let offTopicScores = offTopicVecs |> List.map (cosine queryVec)

            Expect.isGreaterThan
                onTopicScore
                0.2
                "the on-topic document must carry real similarity, not a near-zero artefact"

            for score in offTopicScores do
                Expect.isGreaterThan
                    onTopicScore
                    score
                    "an on-topic chunk indexed before the query must outrank one sharing no vocabulary with it"
        }

        test "the ranking is not vacuous — an unrelated query loses to nothing" {
            // The control: if `cosine` returned something monotone in the
            // stored vector alone, the case above would pass whatever the
            // query was. A query drawn from an off-topic document must
            // rank THAT document first instead.
            let p = LocalEmbeddingProvider.create ()

            let onTopicVec = embed p onTopic
            let kitchenVec = embed p offTopic[0]
            let kitchenQuery = embed p "kitchen rota dishwasher"

            Expect.isGreaterThan
                (cosine kitchenQuery kitchenVec)
                (cosine kitchenQuery onTopicVec)
                "the ranking follows the query, not a fixed preference for one stored chunk"
        }

        test "alignment holds per scope on the scope-keyed family too" {
            // The scope-keyed providers share this embed path, so the
            // property is inherited rather than re-implemented — but the
            // Phase 14z acceptance case had to work around its absence
            // with a fixed-assignment test double, so it is worth pinning
            // that the real provider now carries it.
            let family = LocalEmbeddingProvider.createScoped ()
            let p = family.For(VectorScope.Team "acme")

            let onTopicVec = embed p onTopic
            let offTopicVecs = offTopic |> List.map (embed p)
            let queryVec = embed p probe

            let onTopicScore = cosine queryVec onTopicVec

            for score in offTopicVecs |> List.map (cosine queryVec) do
                Expect.isGreaterThan onTopicScore score "per-scope IDF state does not disturb the shared assignment"
        }
    ]