module ToolUp.Platform.Tests.InProcess.ProvenanceGraphWireTests

open System
open Microsoft.FSharp.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 648 — the provenance walk as a read-only wire contract ────
//
// Seeds the same ingest → run → fact chain the Phase 524 pack walks,
// mounts `ProvenanceApiHandler` over it, and exercises the shipped
// contract rather than the graph beneath it. Four properties carry the
// phase and each has its own arm:
//
//   * the wire records mirror the server records (a case added
//     server-side fails HERE, at the moment it is added, rather than as
//     an unrepresentable node at a consumer later);
//   * a chain round-trips through the contract unchanged;
//   * an over-cap request is refused typed, never truncated;
//   * a suppressed node crosses as a marker and an unknown one as
//     `Absent` — the two must not be the same answer.

// ── Doubles ──────────────────────────────────────────────────────────

/// In-memory, scope-aware `IFactEvidenceSource` — evidence for its own
/// scope only, so the scope-isolation arm means something.
type private FakeFactEvidence(scope: string, facts: Map<string, FactEvidence>) =
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

/// Preset-verdict gate double, recording its last call so an arm can pin
/// the surface the handler judged at. An id absent from `verdicts` denies
/// as `unknown-fact` — the conservative contract the real gate honours.
type private PresetGate(verdicts: Map<string, FactDisclosureVerdict>) =
    let mutable lastCall: (string * string * FactEgressSurface * string list) option =
        None

    member _.LastCall = lastCall

    interface IFactDisclosureGate with
        member _.Check(scopeId, principal, surface, factIds) = async {
            lastCall <- Some(scopeId, principal, surface, factIds)

            return
                factIds
                |> List.distinct
                |> List.map (fun id ->
                    id, (verdicts.TryFind id |> Option.defaultValue (FactNotDisclosable "unknown-fact")))
                |> Map.ofList
        }

// ── Seed ─────────────────────────────────────────────────────────────

let private scopeA = "team-A"
let private obj1 = "obj-1"
let private res1 = "res-1"
let private fact1 = "fact-1" // predecessor, classified Internal
let private fact2 = "fact-2" // current head, Surfaceable
let private principal = "user-1"

let private seededGraph () : IProvenanceGraph =
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

    ProvenanceGraph.createWithFacts lineage (FakeFactEvidence(scopeA, facts))

let private caps = WireProvenanceCaps.defaults

/// The contract with no disclosure gate — a deployment with no fact tier.
let private ungatedApi () =
    ProvenanceApiHandler.create (seededGraph ()) caps scopeA

/// The contract with a gate that permits `fact-2` and refuses `fact-1`.
let private gatedApi () =
    let gate =
        PresetGate(Map.ofList [ fact2, FactDisclosable; fact1, FactNotDisclosable "Internal" ])

    gate, ProvenanceApiHandler.createWithDisclosureGate gate principal (seededGraph ()) caps scopeA

let private upstreamFrom root depth : WireProvenanceChainRequest = {
    Root = root
    Direction = WireProvenanceDirection.Upstream
    Depth = depth
}

// ── Reflection helpers for the shape pins ────────────────────────────

let private unionCaseNames (t: Type) =
    FSharpType.GetUnionCases t |> Array.map _.Name |> Array.toList |> List.sort

let private recordFieldNames (t: Type) =
    FSharpType.GetRecordFields t |> Array.map _.Name |> Array.toList |> List.sort

/// Strip `Async<…>` and then a `Result<…, _>` wrapper, leaving the type a
/// method actually answers with.
let private answerType (returnType: Type) =
    let stripGeneric (definition: Type) (t: Type) =
        if t.IsGenericType && t.GetGenericTypeDefinition() = definition then
            Some(t.GetGenericArguments()[0])
        else
            None

    match stripGeneric typedefof<Async<_>> returnType with
    | None -> None
    | Some inner ->
        match stripGeneric typedefof<Result<_, _>> inner with
        | Some ok -> Some ok
        | None -> Some inner

let tests =
    testList "IProvenanceQueryApi (Phase 648)" [

        // ── The mirror stays complete ────────────────────────────────
        //
        // The wire records are a pinned snapshot of the server records,
        // not a shared type. Nothing in the compiler holds them together
        // in the direction that matters — a case added to the SERVER
        // union compiles fine against a wire union that lacks it, and
        // the loss shows up as a node an out-of-process consumer cannot
        // name. These arms are what makes that a failing build instead.

        testList "wire mirror conformance" [
            test "node kinds mirror the server union case-for-case" {
                Expect.equal
                    (unionCaseNames typeof<WireProvenanceNodeKind>)
                    (unionCaseNames typeof<ProvenanceNodeKind>)
                    "WireProvenanceNodeKind must mirror ProvenanceNodeKind — add the missing case to the wire union"
            }

            test "edge kinds mirror the server union case-for-case" {
                Expect.equal
                    (unionCaseNames typeof<WireProvenanceEdgeKind>)
                    (unionCaseNames typeof<ProvenanceEdgeKind>)
                    "WireProvenanceEdgeKind must mirror ProvenanceEdgeKind"
            }

            test "refs mirror the server union case-for-case" {
                Expect.equal
                    (unionCaseNames typeof<WireProvenanceRef>)
                    (unionCaseNames typeof<ProvenanceRef>)
                    "WireProvenanceRef must mirror ProvenanceRef"
            }

            test "directions mirror the server union case-for-case" {
                Expect.equal
                    (unionCaseNames typeof<WireProvenanceDirection>)
                    (unionCaseNames typeof<ProvenanceDirection>)
                    "WireProvenanceDirection must mirror ProvenanceDirection"
            }

            test "node and edge records mirror the server records field-for-field" {
                Expect.equal
                    (recordFieldNames typeof<WireProvenanceNode>)
                    (recordFieldNames typeof<ProvenanceNode>)
                    "WireProvenanceNode must mirror ProvenanceNode"

                Expect.equal
                    (recordFieldNames typeof<WireProvenanceEdge>)
                    (recordFieldNames typeof<ProvenanceEdge>)
                    "WireProvenanceEdge must mirror ProvenanceEdge"
            }
        ]

        // ── Read-only by construction ────────────────────────────────

        testList "read-only shape" [
            // The pin below rests entirely on `answerType` unwrapping
            // `Async<Result<_, _>>` down to the value a method answers
            // with. An unwrap that stopped one layer short would still
            // satisfy "not unit" for every member — a probe answering a
            // slightly different question than the one asked. So pin the
            // unwrap itself against a known member first.
            test "the answer-type unwrap reaches the value, not its wrapper" {
                let getChain =
                    FSharpType.GetRecordFields typeof<IProvenanceQueryApi>
                    |> Array.find (fun f -> f.Name = "GetChain")

                let _, range = FSharpType.GetFunctionElements getChain.PropertyType

                Expect.equal
                    (answerType range)
                    (Some typeof<WireProvenanceChainPage>)
                    "Async<Result<WireProvenanceChainPage, _>> must unwrap to the page"
            }

            test "every contract member is a query returning a non-unit Async" {
                let fields = FSharpType.GetRecordFields typeof<IProvenanceQueryApi>

                Expect.isGreaterThan
                    fields.Length
                    0
                    "the contract must expose methods — an empty record would pass vacuously"

                for field in fields do
                    let fieldType = field.PropertyType

                    Expect.isTrue
                        (FSharpType.IsFunction fieldType)
                        (sprintf "%s must be a function — a non-function member is not a query" field.Name)

                    let _, range = FSharpType.GetFunctionElements fieldType

                    match answerType range with
                    | None ->
                        failtestf "%s must return Async<_> — every contract method is asynchronous (GP 12)" field.Name
                    | Some answer ->
                        // A `unit` answer is the shape a mutation takes:
                        // a caller that gets nothing back asked for an
                        // effect, not a value. There is no such member
                        // here and this arm is what keeps it that way.
                        Expect.notEqual
                            answer
                            typeof<unit>
                            (sprintf
                                "%s answers with unit — that is a mutation's shape, and this contract is read-only"
                                field.Name)
            }
        ]

        // ── Round-trip ───────────────────────────────────────────────

        testCaseAsync "a chain round-trips through the contract unchanged"
        <| async {
            let graph = seededGraph ()
            let api = ProvenanceApiHandler.create graph caps scopeA

            let! serverChain = graph.GetChain(scopeA, FactRef fact2, Upstream, 5)
            let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

            match answer with
            | Result.Error e -> failtestf "expected a chain, got %s" (ProvenanceQueryError.describe e)
            | Result.Ok page ->
                Expect.equal page.Root serverChain.Root "rooted where the walk rooted it"
                Expect.equal page.Depth 5 "the bound the answer was produced under is echoed"

                Expect.equal
                    (page.Nodes |> List.map _.Id |> List.sort)
                    (serverChain.Nodes |> List.map _.Id |> List.sort)
                    "every node the walk reached crosses"

                Expect.equal
                    (List.length page.Edges)
                    (List.length serverChain.Edges)
                    "every edge the walk found crosses"

                Expect.isEmpty page.Withheld "no gate composed ⇒ nothing withheld"

                let factNode = page.Nodes |> List.find (fun n -> n.Id = fact2)
                Expect.equal factNode.Kind WireProvenanceNodeKind.FactNode "the kind mirror was applied"
                Expect.equal factNode.Disclosure (Some "Surfaceable") "the disclosure annotation rides the wire node"

                Expect.isTrue
                    (page.Edges
                     |> List.exists (fun e ->
                         e.From = fact2 && e.To = res1 && e.Kind = WireProvenanceEdgeKind.EvidenceFor))
                    "the edge-kind mirror was applied"

                Expect.isTrue
                    (page.Nodes |> List.exists (fun n -> n.Id = obj1))
                    "the chain reaches the originating data object"
        }

        testCaseAsync "the declared caps are readable through the contract"
        <| async {
            let api = ungatedApi ()
            let! declared = api.GetCaps()

            Expect.equal declared caps "a consumer sizes its walk from what the deployment declares"
        }

        testCaseAsync "a chain never crosses a team scope (GP 4)"
        <| async {
            let api = ProvenanceApiHandler.create (seededGraph ()) caps "team-B"
            let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.ResultRef res1) 5)

            match answer with
            | Result.Error e -> failtestf "expected an empty chain, got %s" (ProvenanceQueryError.describe e)
            | Result.Ok page ->
                Expect.isFalse
                    (page.Nodes |> List.exists (fun n -> n.Id = obj1))
                    "no scopeA data object visible from scopeB"

                Expect.isFalse (page.Nodes |> List.exists (fun n -> n.Id = fact2)) "no scopeA fact visible from scopeB"
        }

        // ── Node lookup + edge enumeration ───────────────────────────

        testCaseAsync "GetNode resolves a fact, and an unrecorded ref is Absent"
        <| async {
            let api = ungatedApi ()

            let! found = api.GetNode(WireProvenanceRef.FactRef fact2)

            match found with
            | WireProvenanceNodeAnswer.Found node ->
                Expect.equal node.Id fact2 "the node asked for"
                Expect.equal node.Kind WireProvenanceNodeKind.FactNode "typed by kind"
                Expect.equal node.Disclosure (Some "Surfaceable") "carrying its classification"
            | other -> failtestf "expected Found, got %A" other

            let! missing = api.GetNode(WireProvenanceRef.FactRef "no-such-fact")

            Expect.equal
                missing
                WireProvenanceNodeAnswer.Absent
                "an id this scope has no provenance for is Absent, not an empty Found"
        }

        testCaseAsync "GetNode resolves a node whose only provenance is downstream"
        <| async {
            // `obj-1` has no ancestors — it is where the chain starts. A
            // one-directional probe would report the data object every
            // other node derives from as having no provenance at all.
            let api = ungatedApi ()
            let! answer = api.GetNode(WireProvenanceRef.DataObjectRef obj1)

            match answer with
            | WireProvenanceNodeAnswer.Found node ->
                Expect.equal node.Kind WireProvenanceNodeKind.DataObjectVersion "the ingested object resolves"
            | other -> failtestf "expected Found for the root data object, got %A" other
        }

        testCaseAsync "GetEdges splits incident edges by direction"
        <| async {
            let api = ungatedApi ()

            let! fromFact = api.GetEdges(WireProvenanceRef.FactRef fact2)
            Expect.equal fromFact.Ref fact2 "the ref the edges were enumerated for"

            Expect.isTrue
                (fromFact.Outgoing
                 |> List.exists (fun e -> e.To = res1 && e.Kind = WireProvenanceEdgeKind.EvidenceFor))
                "fact --EvidenceFor--> result is outgoing"

            Expect.isTrue
                (fromFact.Outgoing
                 |> List.exists (fun e -> e.To = fact1 && e.Kind = WireProvenanceEdgeKind.Supersedes))
                "fact --Supersedes--> predecessor is outgoing"

            Expect.isEmpty fromFact.Incoming "nothing was derived from the head fact"

            let! toObject = api.GetEdges(WireProvenanceRef.DataObjectRef obj1)

            Expect.isTrue
                (toObject.Incoming
                 |> List.exists (fun e -> e.From = res1 && e.Kind = WireProvenanceEdgeKind.DerivedFrom))
                "result --DerivedFrom--> data object is incoming at the object"

            Expect.isEmpty toObject.Outgoing "the ingested object was derived from nothing"
        }

        // ── Caps refuse, they do not truncate ────────────────────────

        testList "bounded walk" [
            testCaseAsync "a depth above the declared cap is refused, naming the request and the cap"
            <| async {
                let api = ungatedApi ()
                let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) (caps.MaxDepth + 1))

                match answer with
                | Result.Error(ProvenanceDepthExceedsCap(requested, cap)) ->
                    Expect.equal requested (caps.MaxDepth + 1) "the refusal names what was asked"
                    Expect.equal cap caps.MaxDepth "and the cap that refused it"
                | other -> failtestf "expected a depth-cap refusal, got %A" other
            }

            testCaseAsync "a depth below one is refused rather than answered with the bare seed"
            <| async {
                let api = ungatedApi ()
                let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 0)

                match answer with
                | Result.Error(ProvenanceDepthInvalid requested) ->
                    Expect.equal requested 0 "the refusal names what was asked"
                | other -> failtestf "expected an invalid-depth refusal, got %A" other
            }

            testCaseAsync "a chain above the node cap is refused whole, never truncated"
            <| async {
                let narrow = { caps with MaxNodes = 1 }
                let api = ProvenanceApiHandler.create (seededGraph ()) narrow scopeA

                let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

                match answer with
                | Result.Error(ProvenanceChainExceedsNodeCap(nodes, cap)) ->
                    Expect.isGreaterThan nodes 1 "the refusal names how many the walk actually reached"
                    Expect.equal cap 1 "and the cap it exceeded"
                | other -> failtestf "expected a node-cap refusal, got %A" other

                // The same walk under a cap that admits it returns
                // EVERY node — so the refusal above was a refusal, not a
                // truncation dressed up as one.
                let roomy = { caps with MaxNodes = 100 }
                let generous = ProvenanceApiHandler.create (seededGraph ()) roomy scopeA
                let! full = generous.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

                match full with
                | Result.Ok page ->
                    Expect.isGreaterThan (List.length page.Nodes) 1 "the complete answer carries the whole chain"
                | other -> failtestf "expected the complete chain, got %A" other
            }
        ]

        // ── Withheld is not absent ───────────────────────────────────

        testList "disclosure egress" [
            testCaseAsync "a refused node crosses as a marker, and an unknown ref still reads Absent"
            <| async {
                let _, api = gatedApi ()

                let! withheld = api.GetNode(WireProvenanceRef.FactRef fact1)

                match withheld with
                | WireProvenanceNodeAnswer.Withheld marker ->
                    Expect.equal marker.Id fact1 "the marker names the node"
                    Expect.equal marker.Kind WireProvenanceNodeKind.FactNode "and its kind"
                    Expect.equal marker.PolicyRef "Internal" "and the policy that refused it — never the value"
                | other -> failtestf "expected a withheld marker, got %A" other

                let! missing = api.GetNode(WireProvenanceRef.FactRef "no-such-fact")

                Expect.equal
                    missing
                    WireProvenanceNodeAnswer.Absent
                    "a refusal and an absence must not be the same answer — that distinction is the phase"

                Expect.notEqual withheld missing "withheld and absent are distinguishable at the contract"
            }

            testCaseAsync "a chain seals a refused node's content and keeps its edges"
            <| async {
                let _, api = gatedApi ()
                let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

                match answer with
                | Result.Error e -> failtestf "expected a chain, got %s" (ProvenanceQueryError.describe e)
                | Result.Ok page ->
                    Expect.isFalse
                        (page.Nodes |> List.exists (fun n -> n.Id = fact1))
                        "the refused fact's content does not cross"

                    Expect.isTrue
                        (page.Withheld |> List.exists (fun w -> w.Id = fact1 && w.PolicyRef = "Internal"))
                        "it crosses as a marker instead"

                    Expect.isTrue (page.Nodes |> List.exists (fun n -> n.Id = fact2)) "the permitted fact is unaffected"

                    Expect.isTrue
                        (page.Edges
                         |> List.exists (fun e ->
                             e.From = fact2 && e.To = fact1 && e.Kind = WireProvenanceEdgeKind.Supersedes))
                        "chain SHAPE survives the refusal — the edge to the withheld node stays"
            }

            testCaseAsync "the answer is judged at the shipped export door, not a second predicate"
            <| async {
                let gate, api = gatedApi ()
                let! _ = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

                match gate.LastCall with
                | None -> failtest "the gate was never consulted"
                | Some(scopeId, caller, surface, factIds) ->
                    Expect.equal surface FactExport "judged at the shipped FactExport surface"
                    Expect.equal scopeId scopeA "at the caller's resolved scope (GP 4)"
                    Expect.equal caller principal "against the resolved principal the gate audits denies for"
                    Expect.containsAll factIds [ fact1; fact2 ] "fact ids only — one gate call for the whole answer"
            }

            testCaseAsync "no gate composed ⇒ the pre-648 pass-through, nothing withheld (GP 11 / GP 13)"
            <| async {
                let api = ungatedApi ()
                let! answer = api.GetChain(upstreamFrom (WireProvenanceRef.FactRef fact2) 5)

                match answer with
                | Result.Error e -> failtestf "expected a chain, got %s" (ProvenanceQueryError.describe e)
                | Result.Ok page ->
                    Expect.isEmpty page.Withheld "a deployment with no fact tier classifies nothing"

                    Expect.isTrue
                        (page.Nodes |> List.exists (fun n -> n.Id = fact1))
                        "and therefore withholds nothing — the Internal fact crosses uninspected"
            }
        ]
    ]