module ToolUp.Platform.Tests.Graph.SubsetFloorCorpus

open ToolUp.Graph

// ─── Phase 68.F — subset-floor query corpus ─────────────────────────
//
// A frozen set of openCypher queries — one per documented subset
// construct — plus the fixture graph they run against. This is the
// **portability baseline**: every engine companion (68a Kùzu / 68b Neo4j
// / 68c AGE) runs the identical corpus against the identical fixture and
// asserts the same `GraphResultSet` shape (column names + row
// cardinality; row order only where the query carries `ORDER BY`). That
// makes "develop against in-memory, deploy against an engine" a tested
// guarantee, not a hope.
//
// The corpus is deliberately assertion-light on *values* (a set, not an
// order, unless `ORDER BY` is present) so an engine that returns rows in a
// different physical order still conforms.
//
// **Phase 752 — two cases are tier-split, none is tier-skipped.** Every
// case still runs on every binding. Twenty of the twenty-two assert one
// cardinality at both tiers (`AllTiers`); the two variable-length cases
// assert the in-memory interpreter's node-set count on an interpreter-tier
// binding and openCypher's path-multiset count on a full-engine binding
// (`ByTier`), because those are two different correct answers to the same
// query rather than one answer and one bug. See `CorpusRows` below.

/// Build a node with string labels + a property list.
let node (id: string) (labels: string list) (props: (string * PropertyValue) list) : GraphNode = {
    Id = NodeId id
    Labels = Set.ofList labels
    Properties = Map.ofList props
}

/// Build an edge.
let edge (id: string) (label: string) (fromId: string) (toId: string) : GraphEdge = {
    Id = EdgeId id
    Label = label
    From = NodeId fromId
    To = NodeId toId
    Properties = Map.empty
}

/// The fixture: a small social graph with a deliberate cycle
/// (Alice → Bob → Carol → Dave → Alice) plus a WORKS_AT edge to a Company
/// node, so the corpus exercises label filters, inline props, WHERE
/// comparisons, single-hop + variable-length traversal (across the cycle),
/// ORDER BY, and LIMIT.
let fixtureNodes: GraphNode list = [
    node "p1" [ "Person" ] [ "name", PString "Alice"; "age", PInt 30L; "city", PString "London" ]
    node "p2" [ "Person" ] [ "name", PString "Bob"; "age", PInt 25L; "city", PString "Paris" ]
    node "p3" [ "Person" ] [ "name", PString "Carol"; "age", PInt 35L; "city", PString "London" ]
    node "p4" [ "Person" ] [ "name", PString "Dave"; "age", PInt 40L; "city", PString "Berlin" ]
    node "c1" [ "Company" ] [ "name", PString "Acme" ]
]

let fixtureEdges: GraphEdge list = [
    edge "e1" "KNOWS" "p1" "p2" // Alice → Bob
    edge "e2" "KNOWS" "p2" "p3" // Bob → Carol
    edge "e3" "KNOWS" "p3" "p4" // Carol → Dave
    edge "e4" "KNOWS" "p1" "p3" // Alice → Carol
    edge "e5" "KNOWS" "p4" "p1" // Dave → Alice (closes the cycle)
    edge "e6" "WORKS_AT" "p1" "c1" // Alice → Acme
]

/// Seed the fixture into a store scope. Fails the caller if any upsert
/// errors (a dangling edge would indicate a broken fixture).
let seed (store: IGraphStore) (scopeId: string) : Async<unit> = async {
    for n in fixtureNodes do
        match! store.UpsertNode(scopeId, n) with
        | Ok _ -> ()
        | Error e -> failwithf "fixture node upsert failed: %s" (GraphError.message e)

    for e in fixtureEdges do
        match! store.UpsertEdge(scopeId, e) with
        | Ok _ -> ()
        | Error err -> failwithf "fixture edge upsert failed: %s" (GraphError.message err)
}

/// The row cardinality a corpus case expects.
///
/// Most cases are tier-invariant: the in-memory interpreter and a full
/// Cypher engine agree exactly, and `AllTiers` says so. The variable-length
/// cases are NOT, and that difference is a *specified* property rather than
/// an engine defect (Phase 752, the [Phase 607] residue): the in-memory
/// interpreter returns the reachable NODE SET (each node once), while an
/// engine answers openCypher's PATH MULTISET — one row per
/// relationship-unique path. `ByTier` carries both, so a case that differs
/// still runs on every binding and is asserted on every binding.
type CorpusRows =
    /// Every binding, at every tier, returns exactly this many rows.
    | AllTiers of int
    /// The in-memory interpreter's node-set count, then the engine tier's
    /// openCypher path-multiset count. Both are asserted; neither is skipped.
    | ByTier of interpreterRows: int * engineRows: int

/// One corpus case: a query plus the shape every conforming store must
/// return against the fixture.
type CorpusCase = {
    Name: string
    Query: CypherQuery
    ExpectedColumns: string list
    ExpectedRows: CorpusRows
}

let private q (text: string) : CypherQuery = CypherQuery.ofText text

let private qp (text: string) (parameters: (string * PropertyValue) list) : CypherQuery =
    CypherQuery.create text (Map.ofList parameters)

/// The frozen corpus. Each case names the subset construct it pins.
let cases: CorpusCase list = [
    // ── Node patterns ──────────────────────────────────────────
    {
        Name = "all nodes"
        Query = q "MATCH (n) RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 5
    }
    {
        Name = "label filter"
        Query = q "MATCH (n:Person) RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 4
    }
    {
        Name = "label + inline string prop"
        Query = q "MATCH (n:Person {city: 'London'}) RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "inline prop via parameter"
        Query = qp "MATCH (n:Person {city: $city}) RETURN n" [ "city", PString "Paris" ]
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 1
    }
    // ── RETURN projection ──────────────────────────────────────
    {
        Name = "return scalar property"
        Query = q "MATCH (n:Person) RETURN n.name"
        ExpectedColumns = [ "n.name" ]
        ExpectedRows = AllTiers 4
    }
    {
        Name = "return with alias"
        Query = q "MATCH (n:Person) RETURN n.name AS personName"
        ExpectedColumns = [ "personName" ]
        ExpectedRows = AllTiers 4
    }
    {
        Name = "return multiple items"
        Query = q "MATCH (n:Person) RETURN n.name AS name, n.city AS city"
        ExpectedColumns = [ "name"; "city" ]
        ExpectedRows = AllTiers 4
    }
    // ── WHERE comparisons ──────────────────────────────────────
    {
        Name = "where equality (string)"
        Query = q "MATCH (n:Person) WHERE n.city = 'London' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "where greater-than (numeric)"
        Query = q "MATCH (n:Person) WHERE n.age > 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "where greater-than-or-equal"
        Query = q "MATCH (n:Person) WHERE n.age >= 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 3
    }
    {
        Name = "where not-equal"
        Query = q "MATCH (n:Person) WHERE n.city <> 'London' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "where AND"
        Query = q "MATCH (n:Person) WHERE n.city = 'London' AND n.age > 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 1
    }
    {
        Name = "where OR"
        Query = q "MATCH (n:Person) WHERE n.city = 'Paris' OR n.city = 'Berlin' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "where via numeric parameter"
        Query = qp "MATCH (n:Person) WHERE n.age >= $minAge RETURN n" [ "minAge", PInt 35L ]
        ExpectedColumns = [ "n" ]
        ExpectedRows = AllTiers 2
    }
    // ── Single-hop relationships ───────────────────────────────
    {
        Name = "single-hop outgoing"
        Query = q "MATCH (a:Person)-[:KNOWS]->(b:Person) RETURN a.name AS a, b.name AS b"
        ExpectedColumns = [ "a"; "b" ]
        ExpectedRows = AllTiers 5
    }
    {
        Name = "single-hop with endpoint WHERE"
        Query = q "MATCH (a:Person)-[:KNOWS]->(b:Person) WHERE a.name = 'Alice' RETURN b.name AS friend"
        ExpectedColumns = [ "friend" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "single-hop incoming"
        Query = q "MATCH (a:Person)<-[:KNOWS]-(b:Person) WHERE a.name = 'Alice' RETURN b.name AS knower"
        ExpectedColumns = [ "knower" ]
        ExpectedRows = AllTiers 1
    }
    {
        Name = "single-hop to a different label"
        Query = q "MATCH (a:Person)-[:WORKS_AT]->(c:Company) RETURN a.name AS person, c.name AS company"
        ExpectedColumns = [ "person"; "company" ]
        ExpectedRows = AllTiers 1
    }
    // ── Variable-length paths ──────────────────────────────────
    //
    // These are the two TIER-SPLIT cases (Phase 752). Both tiers run both
    // queries and both assert an exact cardinality — what differs is WHICH
    // cardinality is correct, and each is derivable from the fixture:
    //
    //   interpreter (in-memory)  the reachable NODE SET, each node once
    //   engine (openCypher)      the PATH MULTISET, one row per path whose
    //                            relationships are pairwise distinct
    //                            (openCypher relationship-uniqueness)
    {
        // Interpreter: Alice reaches {Bob, Carol, Dave} = 3 nodes.
        // Engine: 4 relationship-unique paths of length 1..2 —
        //   [e1] Alice→Bob · [e4] Alice→Carol
        //   [e1,e2] Alice→Bob→Carol · [e4,e3] Alice→Carol→Dave
        Name = "variable-length 1..2"
        Query = q "MATCH (a:Person {name: 'Alice'})-[:KNOWS*1..2]->(b:Person) RETURN b.name AS reachable"
        ExpectedColumns = [ "reachable" ]
        ExpectedRows = ByTier(3, 4)
    }
    {
        // Interpreter: within 5 hops Alice is reachable back to herself
        // through the cycle (Alice→Carol→Dave→Alice = 3 hops), so the
        // distinct reachable set is all four people. Terminates via the
        // visited (node, depth) state guard.
        //
        // Engine: 10 relationship-unique paths, two at each length 1..5 —
        //   1  [e1] · [e4]
        //   2  [e1,e2] · [e4,e3]
        //   3  [e1,e2,e3] · [e4,e3,e5]
        //   4  [e1,e2,e3,e5] · [e4,e3,e5,e1]
        //   5  [e1,e2,e3,e5,e4] · [e4,e3,e5,e1,e2]
        // Every longer walk repeats a relationship, which is what makes the
        // engine's answer finite too — a different mechanism from the
        // interpreter's visited-set, reaching the same guarantee.
        Name = "variable-length across a cycle (terminates)"
        Query = q "MATCH (a:Person {name: 'Alice'})-[:KNOWS*1..5]->(b:Person) RETURN b.name AS reachable"
        ExpectedColumns = [ "reachable" ]
        ExpectedRows = ByTier(4, 10)
    }
    // ── ORDER BY + LIMIT ───────────────────────────────────────
    {
        Name = "order by desc + limit"
        Query = q "MATCH (n:Person) RETURN n.name AS name ORDER BY n.age DESC LIMIT 2"
        ExpectedColumns = [ "name" ]
        ExpectedRows = AllTiers 2
    }
    {
        Name = "limit only"
        Query = q "MATCH (n:Person) RETURN n.name AS name LIMIT 3"
        ExpectedColumns = [ "name" ]
        ExpectedRows = AllTiers 3
    }
]