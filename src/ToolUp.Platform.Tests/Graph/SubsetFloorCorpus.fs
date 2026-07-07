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

/// One corpus case: a query plus the shape every conforming store must
/// return against the fixture.
type CorpusCase = {
    Name: string
    Query: CypherQuery
    ExpectedColumns: string list
    ExpectedRowCount: int
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
        ExpectedRowCount = 5
    }
    {
        Name = "label filter"
        Query = q "MATCH (n:Person) RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 4
    }
    {
        Name = "label + inline string prop"
        Query = q "MATCH (n:Person {city: 'London'}) RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    {
        Name = "inline prop via parameter"
        Query = qp "MATCH (n:Person {city: $city}) RETURN n" [ "city", PString "Paris" ]
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 1
    }
    // ── RETURN projection ──────────────────────────────────────
    {
        Name = "return scalar property"
        Query = q "MATCH (n:Person) RETURN n.name"
        ExpectedColumns = [ "n.name" ]
        ExpectedRowCount = 4
    }
    {
        Name = "return with alias"
        Query = q "MATCH (n:Person) RETURN n.name AS personName"
        ExpectedColumns = [ "personName" ]
        ExpectedRowCount = 4
    }
    {
        Name = "return multiple items"
        Query = q "MATCH (n:Person) RETURN n.name AS name, n.city AS city"
        ExpectedColumns = [ "name"; "city" ]
        ExpectedRowCount = 4
    }
    // ── WHERE comparisons ──────────────────────────────────────
    {
        Name = "where equality (string)"
        Query = q "MATCH (n:Person) WHERE n.city = 'London' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    {
        Name = "where greater-than (numeric)"
        Query = q "MATCH (n:Person) WHERE n.age > 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    {
        Name = "where greater-than-or-equal"
        Query = q "MATCH (n:Person) WHERE n.age >= 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 3
    }
    {
        Name = "where not-equal"
        Query = q "MATCH (n:Person) WHERE n.city <> 'London' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    {
        Name = "where AND"
        Query = q "MATCH (n:Person) WHERE n.city = 'London' AND n.age > 30 RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 1
    }
    {
        Name = "where OR"
        Query = q "MATCH (n:Person) WHERE n.city = 'Paris' OR n.city = 'Berlin' RETURN n"
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    {
        Name = "where via numeric parameter"
        Query = qp "MATCH (n:Person) WHERE n.age >= $minAge RETURN n" [ "minAge", PInt 35L ]
        ExpectedColumns = [ "n" ]
        ExpectedRowCount = 2
    }
    // ── Single-hop relationships ───────────────────────────────
    {
        Name = "single-hop outgoing"
        Query = q "MATCH (a:Person)-[:KNOWS]->(b:Person) RETURN a.name AS a, b.name AS b"
        ExpectedColumns = [ "a"; "b" ]
        ExpectedRowCount = 5
    }
    {
        Name = "single-hop with endpoint WHERE"
        Query = q "MATCH (a:Person)-[:KNOWS]->(b:Person) WHERE a.name = 'Alice' RETURN b.name AS friend"
        ExpectedColumns = [ "friend" ]
        ExpectedRowCount = 2
    }
    {
        Name = "single-hop incoming"
        Query = q "MATCH (a:Person)<-[:KNOWS]-(b:Person) WHERE a.name = 'Alice' RETURN b.name AS knower"
        ExpectedColumns = [ "knower" ]
        ExpectedRowCount = 1
    }
    {
        Name = "single-hop to a different label"
        Query = q "MATCH (a:Person)-[:WORKS_AT]->(c:Company) RETURN a.name AS person, c.name AS company"
        ExpectedColumns = [ "person"; "company" ]
        ExpectedRowCount = 1
    }
    // ── Variable-length paths ──────────────────────────────────
    {
        Name = "variable-length 1..2"
        Query = q "MATCH (a:Person {name: 'Alice'})-[:KNOWS*1..2]->(b:Person) RETURN b.name AS reachable"
        ExpectedColumns = [ "reachable" ]
        ExpectedRowCount = 3
    }
    {
        // Within 5 hops Alice is reachable back to herself through the
        // cycle (Alice→Carol→Dave→Alice = 3 hops), so the distinct
        // reachable set is all four people. Terminates via the visited
        // (node, depth) state guard.
        Name = "variable-length across a cycle (terminates)"
        Query = q "MATCH (a:Person {name: 'Alice'})-[:KNOWS*1..5]->(b:Person) RETURN b.name AS reachable"
        ExpectedColumns = [ "reachable" ]
        ExpectedRowCount = 4
    }
    // ── ORDER BY + LIMIT ───────────────────────────────────────
    {
        Name = "order by desc + limit"
        Query = q "MATCH (n:Person) RETURN n.name AS name ORDER BY n.age DESC LIMIT 2"
        ExpectedColumns = [ "name" ]
        ExpectedRowCount = 2
    }
    {
        Name = "limit only"
        Query = q "MATCH (n:Person) RETURN n.name AS name LIMIT 3"
        ExpectedColumns = [ "name" ]
        ExpectedRowCount = 3
    }
]