module ToolUp.Platform.Tests.Graph.GraphStoreContract

open System.Text.Json
open Expecto
open ToolUp.Graph
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Tests.Graph.SubsetFloorCorpus

// ─── Phase 68.E — IGraphStore conformance pack ──────────────────────
//
// Parametrised over a `StoreTier` and an `IGraphStore` factory returning
// `(store, scopeA, scopeB)`, so every graph companion (in-memory default,
// 68a Kùzu / 68b Neo4j / 68c AGE) binds to the *identical* suite. The pack
// is the GP-12 six-rule portability audit expressed as executable tests,
// plus tenant-isolation (68.B), the subset-floor corpus (68.F), cycle-safe
// termination, and the "never silently wrong" acceptance property.
//
// ── Phase 752 — capability tiers (the Phase 607 residue) ────────────
//
// Until Phase 752 the pack asserted the in-memory interpreter's *subset*
// laws unconditionally on every binding, so an engine tier that supports
// full Cypher was scored as FAILING for doing what it is supposed to do:
// Phase 607's first-ever live AGE run reported exactly four red cases, all
// of this one cause, and recorded that the Neo4j tier would fail the same
// four identically the day anyone provisioned a server. The fix is not to
// weaken the bar — it is to say which bar applies to which binding.
//
// A binding therefore DECLARES ITS TIER at bind time, and the pack is the
// union of two lists:
//
//   SHARED LAWS — asserted on every binding at every tier. The six
//     portability rules, tenant isolation, Neighbours, delete semantics,
//     the DanglingEdge and MalformedQuery data-not-throw laws, cycle-safe
//     termination-and-reachability, and all 22 subset-floor corpus cases.
//     (Two corpus cases carry a per-tier row cardinality — `CorpusRows.ByTier`
//     — because the interpreter's node-set answer and the engine's
//     path-multiset answer are both correct. Both are asserted; neither
//     case is skipped on either tier.)
//
//   TIER LAWS — asserted only on the declared tier, and every law lives in
//     exactly one of the two lists:
//       `InterpreterSubset` (the in-memory default) — an out-of-subset
//         construct THROWS `CypherSubsetException` naming the clause, and
//         variable-length traversal yields each reachable node exactly once.
//       `FullEngine` (Neo4j 68b / AGE 68c) — `CREATE` EXECUTES rather than
//         throwing and the created node is then visible; a multi-hop pattern
//         runs; variable-length traversal returns the openCypher path
//         multiset over the same reachable set.
//
// **No law is silently dropped.** `lawCensus` below asserts the per-tier
// case counts, so removing a law — or letting a tier quietly stop running
// one — fails the pack by name rather than shrinking a green run.

/// The Cypher capability tier a binding declares. It selects which tier
/// laws apply; the shared laws apply regardless.
type StoreTier =
    /// The in-memory portability floor: a bounded openCypher subset,
    /// interpreted, refusing anything outside it rather than guessing.
    | InterpreterSubset
    /// A full openCypher engine (Neo4j / AGE): every subset construct plus
    /// the ones the floor refuses, with engine path-multiset semantics.
    | FullEngine

module StoreTier =
    let name =
        function
        | InterpreterSubset -> "interpreter-subset"
        | FullEngine -> "full-engine"

    /// The corpus row cardinality this tier must return for a case.
    let expectedRows tier (rows: CorpusRows) =
        match rows, tier with
        | AllTiers n, _ -> n
        | ByTier(interpreterRows, _), InterpreterSubset -> interpreterRows
        | ByTier(_, engineRows), FullEngine -> engineRows

let private jsonOptions = FableConverters.create ()

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

/// A store wrapper that injects `n` transient failures on `UpsertNode`
/// before delegating — used to prove rule 3 (transient failure surfaces as
/// retryable *data*, not a thrown exception across the async boundary).
type private FlakyStore(inner: IGraphStore, remainingFailures: int ref) =
    interface IGraphStore with
        member _.UpsertNode(scopeId, node) = async {
            if remainingFailures.Value > 0 then
                remainingFailures.Value <- remainingFailures.Value - 1
                return Error(TransientFailure "injected transient fault")
            else
                return! inner.UpsertNode(scopeId, node)
        }

        member _.UpsertEdge(scopeId, edge) = inner.UpsertEdge(scopeId, edge)
        member _.DeleteNode(scopeId, id) = inner.DeleteNode(scopeId, id)
        member _.DeleteEdge(scopeId, id) = inner.DeleteEdge(scopeId, id)
        member _.GetNode(scopeId, id) = inner.GetNode(scopeId, id)
        member _.Neighbours(scopeId, id, dir, lbl) = inner.Neighbours(scopeId, id, dir, lbl)
        member _.Query(scopeId, query) = inner.Query(scopeId, query)

/// Extract the projected scalar string cells of a single column.
let private stringColumn (col: string) (rs: GraphResultSet) : string list =
    rs.Rows
    |> List.choose (fun row ->
        match row.TryFind col with
        | Some(VProperty(PString s)) -> Some s
        | _ -> None)

/// A tight self-loop plus a 2-cycle — `s1 →L s1`, `s1 →L s2`, `s2 →L s1`.
/// Every traversal law that must TERMINATE runs against this, at both tiers:
/// the interpreter stops on its visited-set guard, an engine stops on
/// openCypher relationship-uniqueness. Different mechanisms, same guarantee.
let private seedCyclic (store: IGraphStore) (scopeA: string) : Async<unit> = async {
    let! _ = store.UpsertNode(scopeA, node "s1" [ "N" ] [ "name", PString "S1" ])
    let! _ = store.UpsertNode(scopeA, node "s2" [ "N" ] [ "name", PString "S2" ])
    let! _ = store.UpsertEdge(scopeA, edge "l0" "L" "s1" "s1") // self-loop
    let! _ = store.UpsertEdge(scopeA, edge "l1" "L" "s1" "s2")
    let! _ = store.UpsertEdge(scopeA, edge "l2" "L" "s2" "s1") // back-edge → cycle
    ()
}

/// The unbounded-if-unguarded traversal over `seedCyclic`'s graph.
let private cyclicQuery =
    CypherQuery.ofText "MATCH (a:N {name: 'S1'})-[:L*1..10]->(b:N) RETURN b.name AS r"

let tests (name: string) (tier: StoreTier) (factory: unit -> IGraphStore * string * string) =

    // ── SHARED LAWS — every binding, every tier ────────────────
    let sharedLaws: Test list = [

        // ── Rule 1 — identity by value ─────────────────────────
        testCaseAsync "rule 1 — a node round-trips through serialise/deserialise with stable ids"
        <| async {
            let store, scopeA, _ = factory ()

            let n =
                node "acct-42" [ "Account" ] [ "owner", PString "alice"; "balance", PFloat 12.5 ]

            let! saved = store.UpsertNode(scopeA, n)
            Expect.equal (okOrFail "UpsertNode" saved) (NodeId "acct-42") "returns the value id, not a handle"

            // Pure-data round-trip: no live handle can survive JSON.
            let json = JsonSerializer.Serialize(n, jsonOptions)
            let back = JsonSerializer.Deserialize<GraphNode>(json, jsonOptions)
            Expect.equal back n "node is value-identical after a JSON round-trip"

            match! store.GetNode(scopeA, NodeId "acct-42") with
            | Some loaded ->
                Expect.equal loaded.Id (NodeId "acct-42") "id stable"
                Expect.equal loaded.Properties n.Properties "properties preserved"
            | None -> failtest "expected the node back"
        }

        // ── Rule 2 — async at every boundary ───────────────────
        testCaseAsync "rule 2 — every method is awaitable (async boundary)"
        <| async {
            let store, scopeA, _ = factory ()
            let n = node "x" [ "T" ] []
            let! _ = store.UpsertNode(scopeA, n)
            let! _ = store.GetNode(scopeA, NodeId "x")
            let! _ = store.Neighbours(scopeA, NodeId "x", Both, None)
            let! _ = store.Query(scopeA, CypherQuery.ofText "MATCH (n) RETURN n")
            let! _ = store.DeleteNode(scopeA, NodeId "x")
            () // compiles ⇒ each method returned Async<_>
        }

        // ── Rule 3 — retry / supervision as data ───────────────
        testCaseAsync "rule 3 — transient failure surfaces as retryable data, not a thrown exception"
        <| async {
            let inner, scopeA, _ = factory ()
            let counter = ref 1
            let flaky = FlakyStore(inner, counter) :> IGraphStore
            let n = node "n1" [ "T" ] []

            // First call: the transient fault comes back as data — no throw.
            match! flaky.UpsertNode(scopeA, n) with
            | Error(TransientFailure _) -> ()
            | other -> failtestf "expected Error(TransientFailure), got %A" other

            // The caller's own retry (rule 3 is data, so a plain loop works).
            match! flaky.UpsertNode(scopeA, n) with
            | Ok id -> Expect.equal id (NodeId "n1") "retry succeeds after the transient fault clears"
            | Error e -> failtestf "expected Ok on retry, got %A" e
        }

        // ── Rule 4 — stateless between invocations ─────────────
        testCaseAsync "rule 4 — results derive from stored state, not an in-process cache"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            let! before = store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person) RETURN n")
            Expect.equal (okOrFail "query" before).Rows.Length 4 "fixture has 4 people"

            let! _ = store.UpsertNode(scopeA, node "p5" [ "Person" ] [ "name", PString "Eve"; "age", PInt 50L ])

            let! after = store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person) RETURN n")
            Expect.equal (okOrFail "query" after).Rows.Length 5 "new node visible to the very next query"
        }

        // ── Rule 5 — no cross-shard ordering without ORDER BY ──
        testCaseAsync "rule 5 — unordered RETURN promises a set; ORDER BY promises the order"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            // Unordered: assert the SET, never the order.
            let! unordered = store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person) RETURN n.name AS name")

            let names = stringColumn "name" (okOrFail "unordered" unordered) |> List.sort
            Expect.equal names [ "Alice"; "Bob"; "Carol"; "Dave" ] "same set regardless of physical order"

            // ORDER BY: the order IS the contract.
            let! ordered =
                store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person) RETURN n.name AS name ORDER BY n.age ASC")

            let ascByAge = stringColumn "name" (okOrFail "ordered" ordered)
            Expect.equal ascByAge [ "Bob"; "Alice"; "Carol"; "Dave" ] "ORDER BY n.age ASC is honoured"
        }

        // ── Rule 6 — precision at the lower bound ──────────────
        testCaseAsync "rule 6 — PFloat / PInt round-trip at float / int64, no silent widening"
        <| async {
            let store, scopeA, _ = factory ()
            let pi = 3.14159265358979

            let n =
                node "m" [ "Measure" ] [ "ratio", PFloat pi; "count", PInt 9007199254740993L ]

            let! _ = store.UpsertNode(scopeA, n)

            match! store.GetNode(scopeA, NodeId "m") with
            | Some loaded ->
                Expect.equal (loaded.Properties.TryFind "ratio") (Some(PFloat pi)) "float precision preserved"

                Expect.equal
                    (loaded.Properties.TryFind "count")
                    (Some(PInt 9007199254740993L))
                    "int64 beyond 2^53 preserved exactly"
            | None -> failtest "expected the node back"
        }

        // ── 68.B — tenant isolation is structural ──────────────
        testCaseAsync "tenant isolation — scope A's subgraph is invisible from scope B"
        <| async {
            let store, scopeA, scopeB = factory ()
            do! seed store scopeA

            let! bGet = store.GetNode(scopeB, NodeId "p1")
            Expect.isNone bGet "scope B cannot GetNode a scope-A node"

            let! bAll = store.Query(scopeB, CypherQuery.ofText "MATCH (n) RETURN n")
            Expect.equal (okOrFail "query B" bAll).Rows.Length 0 "scope B query observes none of scope A's nodes"

            // Same id in scope B is a distinct node; scope A is untouched.
            let! _ = store.UpsertNode(scopeB, node "p1" [ "Person" ] [ "name", PString "Mallory" ])
            let! aGet = store.GetNode(scopeA, NodeId "p1")

            match aGet with
            | Some n -> Expect.equal (n.Properties.TryFind "name") (Some(PString "Alice")) "scope A's node unchanged"
            | None -> failtest "scope A's node vanished"
        }

        // ── Neighbours ─────────────────────────────────────────
        testCaseAsync "Neighbours honours direction + label filter"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            let! outKnows = store.Neighbours(scopeA, NodeId "p1", Outgoing, Some "KNOWS")
            let outIds = outKnows |> List.map _.Id |> List.sort
            Expect.equal outIds [ NodeId "p2"; NodeId "p3" ] "Alice knows Bob + Carol (outgoing KNOWS)"

            let! inKnows = store.Neighbours(scopeA, NodeId "p1", Incoming, Some "KNOWS")
            Expect.equal (inKnows |> List.map _.Id) [ NodeId "p4" ] "Dave knows Alice (incoming KNOWS)"

            let! anyLabel = store.Neighbours(scopeA, NodeId "p1", Outgoing, None)
            let anyIds = anyLabel |> List.map _.Id |> List.sort
            Expect.equal anyIds [ NodeId "c1"; NodeId "p2"; NodeId "p3" ] "no label filter includes WORKS_AT → Acme"
        }

        // ── Delete semantics ───────────────────────────────────
        testCaseAsync "DeleteNode removes the node and its incident edges; idempotent"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            let! del = store.DeleteNode(scopeA, NodeId "p1")
            Expect.isOk del "delete succeeds"

            let! gone = store.GetNode(scopeA, NodeId "p1")
            Expect.isNone gone "node removed"

            // Dave's KNOWS→Alice edge is gone: Dave now has no outgoing KNOWS.
            let! daveOut = store.Neighbours(scopeA, NodeId "p4", Outgoing, Some "KNOWS")
            Expect.isEmpty daveOut "incident edge removed with the node"

            let! again = store.DeleteNode(scopeA, NodeId "p1")
            Expect.isOk again "second delete is idempotent"
        }

        testCaseAsync "UpsertEdge with a missing endpoint returns DanglingEdge"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = store.UpsertNode(scopeA, node "a" [ "T" ] [])

            match! store.UpsertEdge(scopeA, edge "bad" "LINK" "a" "nonexistent") with
            | Error(DanglingEdge(_, NodeId "nonexistent")) -> ()
            | other -> failtestf "expected DanglingEdge naming the missing endpoint, got %A" other
        }

        testCaseAsync "malformed (in-shape) query returns MalformedQuery data, not a throw"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            // A referenced parameter that was never supplied.
            match! store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person {city: $missing}) RETURN n") with
            | Error(MalformedQuery _) -> ()
            | other -> failtestf "expected Error(MalformedQuery) for a missing parameter, got %A" other
        }

        // ── Cycle-safe variable-length traversal (acceptance) ──
        //
        // The SHARED half is termination + reachability: the query returns
        // at all, and the set of nodes it reaches is exactly {S1, S2}. The
        // CARDINALITY of that answer is tier-specific and is asserted in the
        // tier laws below — the interpreter returns each node once, an
        // engine returns one row per relationship-unique path.
        testCaseAsync "variable-length traversal over a cyclic graph terminates and reaches the right set"
        <| async {
            let store, scopeA, _ = factory ()
            do! seedCyclic store scopeA

            // If neither termination guard were present this would not return.
            let! result = store.Query(scopeA, cyclicQuery)

            let reached =
                stringColumn "r" (okOrFail "cyclic query" result) |> List.distinct |> List.sort

            Expect.equal reached [ "S1"; "S2" ] "terminates with the reachable set"
        }
    ]

    // ── The subset-floor corpus — every case, every binding ────
    //
    // Tier-invariant except for the two variable-length cases, whose
    // expected cardinality is selected by `StoreTier.expectedRows`. No case
    // is skipped on either tier.
    let corpusLaws: Test list =
        cases
        |> List.map (fun case ->
            testCaseAsync $"subset-floor corpus — {case.Name}"
            <| async {
                let store, scopeA, _ = factory ()
                do! seed store scopeA

                let expectedRows = StoreTier.expectedRows tier case.ExpectedRows

                match! store.Query(scopeA, case.Query) with
                | Ok rs ->
                    Expect.equal rs.Columns case.ExpectedColumns $"columns for '{case.Name}'"

                    Expect.equal
                        rs.Rows.Length
                        expectedRows
                        $"row cardinality for '{case.Name}' at the {StoreTier.name tier} tier"
                | Error e -> failtestf "corpus query '%s' errored: %A" case.Name e
            })

    // ── INTERPRETER-SUBSET tier laws — the in-memory floor ─────
    //
    // The floor's defining promise is that it never guesses: a construct
    // outside the documented subset is refused by name rather than answered
    // wrongly. An engine tier is not held to this, and Phase 607's live AGE
    // run failing it was the pack scoring an engine for exceeding the floor.
    let interpreterSubsetLaws: Test list = [

        testCaseAsync "out-of-subset query throws CypherSubsetException naming the clause"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            let! createThrew = async {
                try
                    let! _ = store.Query(scopeA, CypherQuery.ofText "CREATE (n:Person {name: 'X'})")
                    return None
                with :? CypherSubsetException as ex ->
                    return Some ex.Clause
            }

            match createThrew with
            | Some clause -> Expect.stringContains clause "CREATE" "the exception names the unsupported clause"
            | None -> failtest "expected CypherSubsetException for CREATE"

            // Multi-hop pattern is also out of subset.
            let! multiHopThrew = async {
                try
                    let! _ =
                        store.Query(scopeA, CypherQuery.ofText "MATCH (a:Person)-[:KNOWS]->(b)-[:KNOWS]->(c) RETURN a")

                    return false
                with :? CypherSubsetException ->
                    return true
            }

            Expect.isTrue multiHopThrew "multi-hop pattern is out of subset"
        }

        testCaseAsync "variable-length traversal yields each reachable node exactly once"
        <| async {
            let store, scopeA, _ = factory ()
            do! seedCyclic store scopeA

            let! result = store.Query(scopeA, cyclicQuery)
            let reached = stringColumn "r" (okOrFail "cyclic query" result) |> List.sort

            Expect.equal reached [ "S1"; "S2" ] "the interpreter returns the node SET, not a path multiset"
        }
    ]

    // ── FULL-ENGINE tier laws — Neo4j (68b) / AGE (68c) ────────
    //
    // The mirror image: an engine must actually RUN what the floor refuses,
    // and must answer variable-length patterns with openCypher's path
    // semantics. These are the four Phase 607 red cases, asserted at the
    // tier where they are true rather than deleted.
    let fullEngineLaws: Test list = [

        testCaseAsync "CREATE executes rather than throwing, and the created node is visible"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            let! createResult = async {
                try
                    let! r = store.Query(scopeA, CypherQuery.ofText "CREATE (n:Person {name: 'Xavier'})")
                    return Ok r
                with :? CypherSubsetException as ex ->
                    return Error ex.Clause
            }

            match createResult with
            | Error clause -> failtestf "an engine tier must execute CREATE, but it threw naming '%s'" clause
            | Ok r -> Expect.isOk r "CREATE returns Ok on an engine tier"

            match! store.Query(scopeA, CypherQuery.ofText "MATCH (n:Person {name: 'Xavier'}) RETURN n") with
            | Ok rs -> Expect.equal rs.Rows.Length 1 "the CREATEd node is readable in the same scope"
            | Error e -> failtestf "reading back the CREATEd node errored: %A" e
        }

        testCaseAsync "multi-hop pattern runs and returns the openCypher path count"
        <| async {
            let store, scopeA, _ = factory ()
            do! seed store scopeA

            // Two-hop KNOWS chains with distinct relationships, over the
            // fixture's five KNOWS edges: [e1,e2] [e2,e3] [e4,e3] [e3,e5]
            // [e5,e1] [e5,e4] — six.
            let! result = async {
                try
                    let! r =
                        store.Query(scopeA, CypherQuery.ofText "MATCH (a:Person)-[:KNOWS]->(b)-[:KNOWS]->(c) RETURN a")

                    return Ok r
                with :? CypherSubsetException as ex ->
                    return Error ex.Clause
            }

            match result with
            | Error clause -> failtestf "an engine tier must run a multi-hop pattern, but it threw naming '%s'" clause
            | Ok r ->
                let rs = okOrFail "multi-hop" r
                Expect.equal rs.Columns [ "a" ] "projection column"
                Expect.equal rs.Rows.Length 6 "six relationship-unique two-hop paths over the fixture"
        }

        testCaseAsync "variable-length traversal returns the path multiset over the reachable set"
        <| async {
            let store, scopeA, _ = factory ()
            do! seedCyclic store scopeA

            let! result = store.Query(scopeA, cyclicQuery)
            let rows = stringColumn "r" (okOrFail "cyclic query" result)

            // Relationship-unique paths from s1 over {l0: s1→s1, l1: s1→s2,
            // l2: s2→s1}: [l0] [l1] · [l0,l1] [l1,l2] · [l0,l1,l2] [l1,l2,l0].
            // Nothing longer exists — every extension repeats a relationship,
            // which is the engine's own termination guarantee.
            Expect.equal rows.Length 6 "six relationship-unique paths of length 1..10"

            Expect.equal
                (rows |> List.distinct |> List.sort)
                [ "S1"; "S2" ]
                "every path ends inside the reachable set — a multiset OF it, never beyond it"
        }
    ]

    let tierLaws =
        match tier with
        | InterpreterSubset -> interpreterSubsetLaws
        | FullEngine -> fullEngineLaws

    // ── The census — no law silently dropped ───────────────────
    //
    // The counts are the whole point of the tier split being safe: a law
    // deleted, or a tier quietly stopping running one, shrinks a list and
    // fails HERE by name instead of shrinking a green run nobody reads.
    let lawCensus =
        testCase $"law tiering — the {StoreTier.name tier} tier runs its full census"
        <| fun _ ->
            Expect.equal (List.length sharedLaws) 12 "shared laws (every binding, every tier)"
            Expect.equal (List.length corpusLaws) 22 "subset-floor corpus cases (every binding, every tier)"

            Expect.equal
                (cases
                 |> List.filter (fun c ->
                     match c.ExpectedRows with
                     | ByTier _ -> true
                     | AllTiers _ -> false)
                 |> List.length)
                2
                "corpus cases whose row cardinality is tier-specific"

            Expect.equal (List.length interpreterSubsetLaws) 2 "interpreter-subset tier laws"
            Expect.equal (List.length fullEngineLaws) 3 "full-engine tier laws"

            let expectedTierLaws =
                match tier with
                | InterpreterSubset -> 2
                | FullEngine -> 3

            Expect.equal (List.length tierLaws) expectedTierLaws $"laws selected by the {StoreTier.name tier} tier"

    testList $"{name} — IGraphStore contract ({StoreTier.name tier} tier)" [
        yield! sharedLaws
        yield! corpusLaws
        yield! tierLaws
        lawCensus
    ]