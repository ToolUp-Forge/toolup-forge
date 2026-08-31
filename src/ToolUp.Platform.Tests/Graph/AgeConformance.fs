// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Graph.AgeConformanceTests

open System
open Expecto
open ToolUp.Graph
open ToolUp.Graph.AGE
open ToolUp.Graph.AGE.CypherToAgeSql

// ─── Phase 68c — ToolUp.Graph.AGE conformance + translation unit pack ─────────
//
// Two layers, mirroring the 68b Neo4j pack:
//
// 1. `pureTests` (ALWAYS ON) — the cypher()-wrapping + agtype-mapping +
//    injection-binding + graph-name + scope-guard + transient-classification
//    logic is pure, so it is covered on a fresh checkout with no live Postgres.
//    This is where the real fresh-checkout coverage lives (Docker is NOT
//    available on this machine).
//
// 2. `liveTests` (ENV-GATED) — binds the shared `IGraphStoreContract` pack to a
//    real AGE-enabled Postgres when `TOOLUP_TEST_AGE_CONNSTRING` is set; unset
//    (the fresh-checkout default) → a single skipped case, never Failed (mirrors
//    the Neo4j / Postgres / Timescale env-gated companions). Point it at any
//    Postgres with `CREATE EXTENSION age;` run. The live arm uses graph-per-
//    tenant isolation so it exercises the per-scope AGE graph derivation.

// ── Pure unit pack — always on ───────────────────────────────────────

let private vertexText =
    """{"id": 281474976710657, "label": "Person", "properties": {"_id": "p1", "name": "Alice", "age": 30}}::vertex"""

let private edgeText =
    """{"id": 1, "label": "KNOWS", "start_id": 2, "end_id": 3, "properties": {"_id": "e1", "_from": "p1", "_to": "p2", "since": 2020}}::edge"""

let pureTests =
    testList "Phase 68c — AGE translation (pure)" [

        // ── agtype text → GraphValue ───────────────────────────────
        testCase "agtypeToGraphValue maps a ::vertex cell to a VNode (id from _id, label, props)"
        <| fun _ ->
            match agtypeToGraphValue vertexText with
            | VNode n ->
                Expect.equal n.Id (NodeId "p1") "NodeId from reserved _id, not AGE's internal graphid"
                Expect.equal n.Labels (Set.singleton "Person") "single AGE label → label set"
                Expect.equal (n.Properties.TryFind "name") (Some(PString "Alice")) "user string property"
                Expect.equal (n.Properties.TryFind "age") (Some(PInt 30L)) "user int property at the int64 floor"
                Expect.isFalse (n.Properties.ContainsKey "_id") "reserved key stripped from user properties"
            | other -> failtestf "expected VNode, got %A" other

        testCase "agtypeToGraphValue maps a ::edge cell to a VEdge with endpoints from _from / _to"
        <| fun _ ->
            match agtypeToGraphValue edgeText with
            | VEdge e ->
                Expect.equal e.Id (EdgeId "e1") "EdgeId from reserved _id"
                Expect.equal e.Label "KNOWS" "edge label"
                Expect.equal e.From (NodeId "p1") "from endpoint from _from"
                Expect.equal e.To (NodeId "p2") "to endpoint from _to"
            | other -> failtestf "expected VEdge, got %A" other

        testCase "agtypeToGraphValue maps scalars at the precision floor (rule 6)"
        <| fun _ ->
            Expect.equal (agtypeToGraphValue "\"hi\"") (VProperty(PString "hi")) "quoted string"
            Expect.equal (agtypeToGraphValue "9007199254740993") (VProperty(PInt 9007199254740993L)) "int64 beyond 2^53"
            Expect.equal (agtypeToGraphValue "3.5") (VProperty(PFloat 3.5)) "float"
            Expect.equal (agtypeToGraphValue "true") (VProperty(PBool true)) "bool"
            Expect.equal (agtypeToGraphValue "null") VNull "null"
            Expect.equal (agtypeToGraphValue "") VNull "blank cell"

        testCase "agtypeBool reads an IS NOT NULL projection cell"
        <| fun _ ->
            Expect.isTrue (agtypeBool "true") "true → true"
            Expect.isFalse (agtypeBool "false") "false → false"
            Expect.isFalse (agtypeBool "null") "null → false"

        // ── Safe parameter binding (injection class) ───────────────
        testList "safe parameter binding (task 68c.A)" [

            testCase "an injection-attempt value is an escaped JSON literal, never SQL"
            <| fun _ ->
                let evil = "'; DROP TABLE users; --"
                let json = paramsJson (Map.ofList [ "name", AScalar(PString evil) ])
                // The value is carried as DATA inside a JSON string (the quote is
                // even escaped to ' — safer still), and round-trips exactly.
                use doc = System.Text.Json.JsonDocument.Parse json

                Expect.equal
                    (doc.RootElement.GetProperty("name").GetString())
                    evil
                    "value carried as a literal map entry"

                Expect.stringContains json "\"name\":" "key rendered as a JSON key"

            testCase "the injection value never reaches the generated SQL body"
            <| fun _ ->
                let evil = "'; DROP TABLE users; --"
                // Parameters ride @p only; the SQL body carries the developer's
                // Cypher text + the derived graph name, never a parameter value.
                match wrapQuery "tenant_t_abc" "MATCH (n {_id: $id}) RETURN n" true [ "n" ] with
                | Ok(sql, _) ->
                    Expect.isFalse (sql.Contains evil) "the injection value is absent from the SQL"
                    Expect.stringContains sql "@p" "parameters ride the single bound @p"

                    Expect.isFalse
                        (sql.Contains "@p::agtype")
                        "@p is passed BARE — AGE rejects a cast third argument with 22023 (Phase 607)"
                | Error e -> failtestf "wrapQuery unexpectedly failed: %s" e

            testCase "paramsJson escapes embedded quotes / backslashes"
            <| fun _ ->
                let tricky = "a\"b\\c"
                let json = paramsJson (Map.ofList [ "k", AScalar(PString tricky) ])
                // System.Text.Json escapes the quote + backslash; parsing must round-trip.
                use doc = System.Text.Json.JsonDocument.Parse json
                let back = doc.RootElement.GetProperty("k").GetString()
                Expect.equal back tricky "value round-trips through JSON without breaking out"
        ]

        // ── cypher() SQL wrapping ──────────────────────────────────
        testList "cypher() wrapping" [

            testCase "wraps a read with uncast agtype columns + the graph literal"
            <| fun _ ->
                match wrapQuery "tenant_a_123" "MATCH (n) RETURN n" false [ "n" ] with
                | Ok(sql, cols) ->
                    Expect.stringContains sql "cypher('tenant_a_123'" "graph name embedded as a SQL literal"
                    Expect.stringContains sql "$ct$ MATCH (n) RETURN n $ct$" "Cypher dollar-quoted with the ct tag"
                    Expect.stringContains sql "SELECT c0 FROM" "agtype cell selected UNCAST"

                    // agtype::text is scalar-only (a vertex cell errors) and drops
                    // the JSON quoting a string cell needs, so the cast is a defect
                    // rather than a style choice — pinned so it cannot return.
                    Expect.isFalse (sql.Contains "c0::text") "the ::text cast is NOT reintroduced (Phase 607)"

                    Expect.stringContains sql "(c0 agtype)" "column definition list"
                    Expect.isFalse (sql.Contains "@p") "no parameter argument when there are no parameters"
                    Expect.equal cols [ "n" ] "the Cypher column names are returned for the result set"
                | Error e -> failtestf "unexpected Error: %s" e

            testCase "a write (no RETURN) projects a single sentinel column"
            <| fun _ ->
                match wrapQuery "g" "MATCH (n {_id: $id}) DETACH DELETE n" true [] with
                | Ok(sql, cols) ->
                    Expect.stringContains sql "(c0 agtype)" "exactly one column (AGE's no-RETURN rule)"
                    Expect.equal cols [ "_w" ] "sentinel column"
                | Error e -> failtestf "unexpected Error: %s" e

            testCase "fails closed on a dollar-quote-tag collision in the Cypher body"
            <| fun _ ->
                match wrapQuery "g" "RETURN $ct$injected$ct$" false [ "x" ] with
                | Error msg -> Expect.stringContains msg "dollar-quote tag" "refuses rather than emit a broken wrap"
                | Ok _ -> failtest "expected the wrap to be refused"
        ]

        // ── RETURN-projection column names ─────────────────────────
        testList "returnColumns" [
            testCase "bare node"
            <| fun _ -> Expect.equal (returnColumns "MATCH (n) RETURN n") [ "n" ] "n"

            testCase "scalar property"
            <| fun _ -> Expect.equal (returnColumns "MATCH (n:Person) RETURN n.name") [ "n.name" ] "n.name"

            testCase "AS alias"
            <| fun _ ->
                Expect.equal
                    (returnColumns "MATCH (n:Person) RETURN n.name AS personName")
                    [ "personName" ]
                    "alias wins"

            testCase "multiple items"
            <| fun _ ->
                Expect.equal
                    (returnColumns "MATCH (n:Person) RETURN n.name AS name, n.city AS city")
                    [ "name"; "city" ]
                    "two aliased columns"

            testCase "trailing ORDER BY / LIMIT are not columns"
            <| fun _ ->
                Expect.equal
                    (returnColumns "MATCH (n:Person) RETURN n.name AS name ORDER BY n.age DESC LIMIT 2")
                    [ "name" ]
                    "projection cut before ORDER BY"

            testCase "LIMIT after a bare return"
            <| fun _ -> Expect.equal (returnColumns "MATCH (n {_id: $id}) RETURN n LIMIT 1") [ "n" ] "n"

            testCase "a write has no columns"
            <| fun _ -> Expect.equal (returnColumns "MATCH (n) DETACH DELETE n") [] "no RETURN → empty"
        ]

        // ── AGE label mapping ──────────────────────────────────────
        testCase "ageLabelOf uses the sole label, else the _v sentinel"
        <| fun _ ->
            Expect.equal (ageLabelOf (Set.singleton "Person")) "Person" "single label"
            Expect.equal (ageLabelOf Set.empty) DefaultLabel "no label → sentinel"
            Expect.equal (ageLabelOf (set [ "A"; "B" ])) DefaultLabel "multi-label → sentinel (AGE is single-label)"

        // ── graph-name derivation (build-once/read-per-call seam) ──
        testList "graph-name derivation" [

            testCase "is a valid AGE graph name (letter-initial, [a-z0-9_], ≤63)"
            <| fun _ ->
                let g = AgeGraph.nameFor AgeGraphStoreConfig.defaults "Team-A/42:weird"
                Expect.isTrue (g.Length >= 1 && g.Length <= 63) "length within bounds"
                Expect.isTrue (Char.IsLetter g.[0]) "starts with a letter"

                Expect.isTrue
                    (g
                     |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_'))
                    "only lowercase alphanumerics + underscore"

            testCase "is deterministic and distinct for distinct scopes"
            <| fun _ ->
                let a1 = AgeGraph.nameFor AgeGraphStoreConfig.defaults "team-a"
                let a2 = AgeGraph.nameFor AgeGraphStoreConfig.defaults "team-a"
                let b = AgeGraph.nameFor AgeGraphStoreConfig.defaults "team-b"
                Expect.equal a1 a2 "same scope → same graph (deterministic)"
                Expect.notEqual a1 b "distinct scopes → distinct graphs"

            testCase "property-partition uses the single fixed graph for every scope"
            <| fun _ ->
                let cfg = AgeGraphStoreConfig.propertyPartition "shared"
                Expect.equal (AgeGraph.nameFor cfg "team-a") "shared" "fixed graph"
                Expect.equal (AgeGraph.nameFor cfg "team-b") "shared" "same fixed graph"
        ]

        // ── session preamble ───────────────────────────────────────
        testCase "the preamble loads AGE and puts ag_catalog on the search path"
        <| fun _ ->
            Expect.stringContains AgeSessionPreamble.Sql "LOAD 'age'" "AGE library loaded"

            Expect.stringContains
                AgeSessionPreamble.Sql
                "search_path = ag_catalog"
                "ag_catalog first on the search path"

        // ── property-partition scope guard ─────────────────────────
        testList "property-partition scope guard (injectScopeGuard)" [

            testCase "injects _scope into a bare node pattern"
            <| fun _ ->
                let out = injectScopeGuard "MATCH (n) RETURN n"
                Expect.stringContains out "_scope: $__scope" "scope predicate injected"

            testCase "merges into an existing inline property map"
            <| fun _ ->
                let out = injectScopeGuard "MATCH (n:Person {city: 'London'}) RETURN n"
                Expect.stringContains out "_scope: $__scope" "scope merged"
                Expect.stringContains out "city: 'London'" "original inline prop preserved"

            testCase "does NOT rewrite a function call (parens preceded by an identifier)"
            <| fun _ ->
                let out = injectScopeGuard "MATCH (n:Person) RETURN count(n)"
                Expect.stringContains out "count(n)" "count(n) not rewritten as a node pattern"
        ]

        // ── transient / malformed classification (GP 12 rule 3) ────
        testList "error classification" [

            testCase "transient SqlState classes fold to the retry channel"
            <| fun _ ->
                Expect.isTrue (isTransientSqlState "08006") "08 connection exception"
                Expect.isTrue (isTransientSqlState "40001") "40001 serialization failure"
                Expect.isTrue (isTransientSqlState "40P01") "40P01 deadlock detected"
                Expect.isTrue (isTransientSqlState "53300") "53 insufficient resources"
                Expect.isTrue (isTransientSqlState "57P01") "57P01 admin shutdown"
                Expect.isFalse (isTransientSqlState "42601") "42601 syntax is not transient"
                Expect.isFalse (isTransientSqlState "") "empty is not transient"

            testCase "class-42 folds to malformed, not transient"
            <| fun _ ->
                Expect.isTrue (isMalformedSqlState "42601") "42601 syntax error"
                Expect.isFalse (isMalformedSqlState "08006") "connection error is not a malformed query"

            testCase "an unrecognised exception folds to StorageFailure (retry channel is data)"
            <| fun _ ->
                match classifyError (Exception "boom") with
                | StorageFailure msg -> Expect.stringContains msg "boom" "message carried"
                | other -> failtestf "expected StorageFailure, got %A" other
        ]
    ]

// ── Live conformance arm — env-gated ─────────────────────────────────

let liveTests =
    match Environment.GetEnvironmentVariable "TOOLUP_TEST_AGE_CONNSTRING" with
    | null
    | "" ->
        testList "Phase 68c — AgeGraphStore (live)" [
            ptestCase "skipped — TOOLUP_TEST_AGE_CONNSTRING not set" <| fun _ -> ()
        ]
    | connString ->
        // A single shared data source (host-lifetime pool). Graph-per-tenant
        // isolation so the arm exercises per-scope AGE graph derivation; each
        // factory call uses GUID-suffixed scopes so concurrent cases isolate.
        let store = AgeGraphStore.connect connString

        let factory () =
            let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
            store, "teama" + suffix, "teamb" + suffix

        // Tier: `FullEngine` (Phase 752). AGE supports the constructs the
        // in-memory floor refuses — the four cases Phase 607's first live run
        // reported red were the pack asserting interpreter-subset laws here,
        // not AGE defects.
        GraphStoreContract.tests "AgeGraphStore" GraphStoreContract.FullEngine factory