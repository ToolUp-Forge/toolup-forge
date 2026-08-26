module ToolUp.Platform.Tests.InProcess.ProvenanceGraphTests

open System
open Expecto
open ToolUp.Platform

// ─── Phase 524 — provenance chain traversal ──────────────────────────
//
// Seeds a real `ILineageStore` (a data-object → result link) plus an
// in-memory `IFactEvidenceSource` (a fact evidenced by that result,
// derived from that data object, superseding a predecessor), then walks
// the composed chain both directions and checks scope isolation.

/// In-memory, scope-aware `IFactEvidenceSource`. Returns evidence only for
/// its own scope so the scope-isolation test is meaningful.
type FakeFactEvidence(scope: string, facts: Map<string, FactEvidence>) =
    interface IFactEvidenceSource with
        member _.GetFact(scopeId, factId) = async {
            return (if scopeId = scope then Map.tryFind factId facts else None)
        }

        member _.FactsForResult(scopeId, resultId) = async {
            return
                if scopeId = scope then
                    facts
                    |> Map.toList
                    |> List.map snd
                    |> List.filter (fun e -> e.ResultRef = Some resultId)
                else
                    []
        }

// Seed identities.
let private scopeA = "team-A"
let private obj1 = "obj-1"
let private res1 = "res-1"
let private fact1 = "fact-1" // predecessor
let private fact2 = "fact-2" // current head
let private msg1 = "msg-1"

let private seededGraph () : IProvenanceGraph * ILineageStore =
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let lineage = LineageStore.EventStoreLineageStore(eventStore) :> ILineageStore

    // res-1 was derived from obj-1.
    let link: LineageLink = {
        LinkId = Guid.NewGuid()
        FromObjectId = obj1
        ToObjectId = res1
        ModuleName = "analytics"
        LinkType = Derived
        Timestamp = DateTime.UtcNow
    }

    lineage.Record(scopeA, link) |> Async.RunSynchronously |> ignore

    let facts =
        Map.ofList [
            fact2,
            {
                FactId = fact2
                Subject = "geography/uk"
                Metric = "revenue"
                Disclosure = "Surfaceable"
                ResultRef = Some res1
                InputHashes = [ obj1 ]
                Supersedes = Some fact1
            }
            fact1,
            {
                FactId = fact1
                Subject = "geography/uk"
                Metric = "revenue"
                Disclosure = "Internal"
                ResultRef = Some res1
                InputHashes = [ obj1 ]
                Supersedes = None
            }
        ]

    ProvenanceGraph.createWithFacts lineage (FakeFactEvidence(scopeA, facts)), lineage

let private hasNode (c: ProvenanceChain) id =
    c.Nodes |> List.exists (fun n -> n.Id = id)

let private hasEdge (c: ProvenanceChain) f t k =
    c.Edges |> List.exists (fun e -> e.From = f && e.To = t && e.Kind = k)

let tests =
    testList "IProvenanceGraph (Phase 524)" [

        testCaseAsync "upstream from a fact reaches its result, input data object, and predecessor"
        <| async {
            let graph, _ = seededGraph ()
            let! chain = graph.GetChain(scopeA, FactRef fact2, Upstream, 5)

            Expect.isTrue (hasNode chain fact2) "fact node present"
            Expect.isTrue (hasNode chain res1) "result node reached"
            Expect.isTrue (hasNode chain obj1) "data-object reached"
            Expect.isTrue (hasNode chain fact1) "predecessor fact reached"

            Expect.isTrue (hasEdge chain fact2 res1 EvidenceFor) "fact --EvidenceFor--> result"
            Expect.isTrue (hasEdge chain fact2 obj1 DerivedFrom) "fact --DerivedFrom--> data object"
            Expect.isTrue (hasEdge chain fact2 fact1 Supersedes) "fact --Supersedes--> predecessor"
            // The result's own lineage to the data object is stitched in too.
            Expect.isTrue (hasEdge chain res1 obj1 DerivedFrom) "result --DerivedFrom--> data object (lineage)"
        }

        testCaseAsync "the fact node carries its disclosure class (524.D)"
        <| async {
            let graph, _ = seededGraph ()
            let! chain = graph.GetChain(scopeA, FactRef fact2, Upstream, 5)

            let factNode = chain.Nodes |> List.tryFind (fun n -> n.Id = fact2)

            Expect.equal
                (factNode |> Option.bind _.Disclosure)
                (Some "Surfaceable")
                "disclosure carried on the fact node"
        }

        testCaseAsync "GetChainForMessage reaches the originating data-object version (acceptance)"
        <| async {
            let graph, _ = seededGraph ()
            let! chain = graph.GetChainForMessage(scopeA, msg1, [ fact2 ], 5)

            Expect.equal chain.Root msg1 "rooted at the message"
            Expect.isTrue (hasEdge chain msg1 fact2 CitesFact) "message --CitesFact--> fact"
            Expect.isTrue (hasNode chain obj1) "chain reaches the originating data object"
        }

        testCaseAsync "a MessageRef root walks to nothing — GetChainForMessage is the route (documented contract)"
        <| async {
            // Pins the documented behaviour rather than leaving it as an
            // unexplained no-op arm. No store composed here — or anywhere
            // in the SDK — answers "which facts did this message cite";
            // that is an assertion the answer made, and it arrives as
            // GetChainForMessage's `citedFactIds`. The Phase 648 wire
            // contract therefore reports `Absent` for a MessageRef, which
            // means "ask with the cited facts", not "no such message".
            let graph, _ = seededGraph ()
            let! chain = graph.GetChain(scopeA, MessageRef msg1, Upstream, 5)

            // The seed node is minted for every root before the walk
            // starts, so the answer is the bare message and nothing else:
            // no disclosure, a label equal to its own id, no edges. That
            // shape scores 0 on `ProvenanceApiHandler.evidenceScore`,
            // which is why the wire contract reports `Absent` rather than
            // `Found` — the walk learned nothing about the ref.
            Expect.equal (chain.Nodes |> List.map _.Id) [ msg1 ] "the walk adds nothing to the seed node it was given"

            let seeded = List.exactlyOne chain.Nodes

            Expect.equal seeded.Kind ConversationMessage "and it is typed as the message it is"
            Expect.isNone seeded.Disclosure "with nothing annotated onto it"
            Expect.equal seeded.Label seeded.Id "and no label beyond its own id — evidenceScore 0"
            Expect.isEmpty chain.Edges "no edges: nothing here knows what the message cited"
            Expect.equal chain.Root msg1 "the root is still echoed, so the caller can tell what it asked about"

            // The falsifier: the SAME message, asked the supported way,
            // does answer — so the emptiness above is about the ROUTE, not
            // about this fixture having no provenance to find.
            let! viaCitedFacts = graph.GetChainForMessage(scopeA, msg1, [ fact2 ], 5)

            Expect.isGreaterThan
                (List.length viaCitedFacts.Nodes)
                1
                "the same message answers when its cited facts are supplied"

            Expect.isNonEmpty viaCitedFacts.Edges "and the CitesFact edges the other route cannot produce are there"
        }

        testCaseAsync "downstream from the data object reaches the result and the facts (the inverse)"
        <| async {
            let graph, _ = seededGraph ()
            let! chain = graph.GetChain(scopeA, DataObjectRef obj1, Downstream, 5)

            Expect.isTrue (hasNode chain res1) "result reached downstream"
            Expect.isTrue (hasEdge chain res1 obj1 DerivedFrom) "result --DerivedFrom--> data object"
            Expect.isTrue (hasNode chain fact2) "fact computed from the result reached downstream"
            Expect.isTrue (hasEdge chain fact2 res1 EvidenceFor) "fact --EvidenceFor--> result"
        }

        testCaseAsync "a chain never crosses a team scope (GP 4)"
        <| async {
            let graph, _ = seededGraph ()
            // Same graph, queried under a different scope: the lineage store
            // and the fact source are both scope-bounded, so nothing from
            // scopeA leaks.
            let! chain = graph.GetChain("team-B", ResultRef res1, Upstream, 5)

            Expect.isFalse (hasNode chain obj1) "no scopeA data object visible from scopeB"
            Expect.isFalse (hasNode chain fact2) "no scopeA fact visible from scopeB"
        }

        testCaseAsync "with no fact-evidence source, only the lineage portion walks"
        <| async {
            let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let lineage = LineageStore.EventStoreLineageStore(eventStore) :> ILineageStore

            lineage.Record(
                scopeA,
                {
                    LinkId = Guid.NewGuid()
                    FromObjectId = obj1
                    ToObjectId = res1
                    ModuleName = "analytics"
                    LinkType = Derived
                    Timestamp = DateTime.UtcNow
                }
            )
            |> Async.RunSynchronously
            |> ignore

            let graph = ProvenanceGraph.create lineage
            let! chain = graph.GetChain(scopeA, ResultRef res1, Upstream, 5)

            Expect.isTrue (hasNode chain obj1) "lineage still walks without a fact source"
            Expect.isFalse (hasNode chain fact2) "fact nodes absent when no source is composed"
        }
    ]