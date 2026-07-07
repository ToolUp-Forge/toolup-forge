module ToolUp.Platform.Tests.Graph.GraphStoreContract

open System.Text.Json
open Expecto
open ToolUp.Graph
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.Tests.Graph.SubsetFloorCorpus

// ─── Phase 68.E — IGraphStore conformance pack ──────────────────────
//
// Parametrised over an `IGraphStore` factory returning
// `(store, scopeA, scopeB)`, so every graph companion (in-memory default,
// 68a Kùzu / 68b Neo4j / 68c AGE) binds to the *identical* suite. The pack
// is the GP-12 six-rule portability audit expressed as executable tests,
// plus tenant-isolation (68.B), the subset-floor corpus (68.F), cycle-safe
// termination, and the "out-of-subset throws, never silently wrong"
// acceptance property.

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

let tests (name: string) (factory: unit -> IGraphStore * string * string) =

    testList $"{name} — IGraphStore contract" [

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

        // ── Out-of-subset throws, never silently wrong (accept.) ─
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
        testCaseAsync "variable-length traversal over a cyclic graph terminates"
        <| async {
            let store, scopeA, _ = factory ()
            // A tight self-loop plus a 2-cycle — the visited-set must stop it.
            let! _ = store.UpsertNode(scopeA, node "s1" [ "N" ] [ "name", PString "S1" ])
            let! _ = store.UpsertNode(scopeA, node "s2" [ "N" ] [ "name", PString "S2" ])
            let! _ = store.UpsertEdge(scopeA, edge "l0" "L" "s1" "s1") // self-loop
            let! _ = store.UpsertEdge(scopeA, edge "l1" "L" "s1" "s2")
            let! _ = store.UpsertEdge(scopeA, edge "l2" "L" "s2" "s1") // back-edge → cycle

            // If the visited-set guard were missing this would loop forever.
            let! result =
                store.Query(scopeA, CypherQuery.ofText "MATCH (a:N {name: 'S1'})-[:L*1..10]->(b:N) RETURN b.name AS r")

            let reached = stringColumn "r" (okOrFail "cyclic query" result) |> List.sort
            Expect.equal reached [ "S1"; "S2" ] "terminates with the reachable set (each node once)"
        }

        // ── 68.F — subset-floor corpus (the portability baseline) ─
        yield!
            cases
            |> List.map (fun case ->
                testCaseAsync $"subset-floor corpus — {case.Name}"
                <| async {
                    let store, scopeA, _ = factory ()
                    do! seed store scopeA

                    match! store.Query(scopeA, case.Query) with
                    | Ok rs ->
                        Expect.equal rs.Columns case.ExpectedColumns $"columns for '{case.Name}'"
                        Expect.equal rs.Rows.Length case.ExpectedRowCount $"row cardinality for '{case.Name}'"
                    | Error e -> failtestf "corpus query '%s' errored: %A" case.Name e
                })
    ]