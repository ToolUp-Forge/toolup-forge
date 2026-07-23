// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Graph.Neo4jConformanceTests

open System
open Expecto
open Neo4j.Driver
open ToolUp.Graph
open ToolUp.Graph.Neo4j

// ─── Phase 68b — ToolUp.Graph.Neo4j conformance + translation unit pack ───────
//
// Two layers:
//
// 1. `pureTests` (ALWAYS ON) — the Cypher-translation + session-scope logic is
//    pure, so it is covered on a fresh checkout with no live server: value
//    mapping (PropertyValue ⇄ Bolt), the property-partition scope guard,
//    database-name derivation (build-once/read-per-call seam), label escaping,
//    and transient-fault classification.
//
// 2. `liveTests` (ENV-GATED) — binds the shared `IGraphStoreContract` pack to a
//    real Neo4j when `TOOLUP_TEST_NEO4J_URI` (+ `_USER` / `_PASSWORD`) is set;
//    unset (the fresh-checkout default) → a single skipped case, never Failed
//    (mirrors the Postgres / Timescale env-gated companions). Docker is not
//    required — point it at any running Neo4j (Community works via the
//    property-partition mode used here). The live arm runs `PropertyPartition`
//    isolation over the default database so it exercises the scope guard and
//    the `_scope` partition on a single-database server.

// ── Pure unit pack — always on ───────────────────────────────────────

let private roundTripsScalar (v: PropertyValue) =
    CypherTranslation.toNeo4j v |> CypherTranslation.ofNeo4jScalar

let pureTests =
    testList "Phase 68b — Neo4j translation (pure)" [

        testCase "PropertyValue ⇄ Bolt round-trips at the precision floor (rule 6)"
        <| fun _ ->
            Expect.equal (roundTripsScalar (PString "hi")) (Some(PString "hi")) "string"
            Expect.equal (roundTripsScalar (PBool true)) (Some(PBool true)) "bool"
            Expect.equal (roundTripsScalar (PInt 9007199254740993L)) (Some(PInt 9007199254740993L)) "int64 beyond 2^53"
            Expect.equal (roundTripsScalar (PFloat 3.14159265358979)) (Some(PFloat 3.14159265358979)) "float"
            let dt = DateTime(2026, 7, 23, 10, 30, 0, DateTimeKind.Utc)
            Expect.equal (roundTripsScalar (PDateTime dt)) (Some(PDateTime dt)) "datetime"

        testCase "ofNeo4jScalar widens Bolt Int32 / single into the int64 / float floor"
        <| fun _ ->
            Expect.equal (CypherTranslation.ofNeo4jScalar (box 42)) (Some(PInt 42L)) "int32 → PInt"
            Expect.equal (CypherTranslation.ofNeo4jScalar (box 1.5f)) (Some(PFloat 1.5)) "single → PFloat"
            Expect.equal (CypherTranslation.ofNeo4jScalar null) None "null → None"

        testCase "nodePropertyMap stamps _id, and _scope only under property-partition"
        <| fun _ ->
            let n = {
                Id = NodeId "acct-1"
                Labels = set [ "Account" ]
                Properties = Map.ofList [ "owner", PString "alice" ]
            }

            let noScope = CypherTranslation.nodePropertyMap None n
            Expect.equal (noScope.[CypherTranslation.IdKey]) (box "acct-1") "id stamped"
            Expect.isFalse (noScope.ContainsKey CypherTranslation.ScopeKey) "no _scope in multi-db mode"
            Expect.equal (noScope.["owner"]) (box "alice") "user property carried"

            let scoped = CypherTranslation.nodePropertyMap (Some "team-a") n
            Expect.equal (scoped.[CypherTranslation.ScopeKey]) (box "team-a") "_scope stamped in partition mode"

        testCase "edgePropertyMap carries endpoints as _from / _to for value-model round-trip"
        <| fun _ ->
            let e = {
                Id = EdgeId "e1"
                Label = "KNOWS"
                From = NodeId "p1"
                To = NodeId "p2"
                Properties = Map.empty
            }

            let m = CypherTranslation.edgePropertyMap None e
            Expect.equal (m.[CypherTranslation.IdKey]) (box "e1") "edge id"
            Expect.equal (m.["_from"]) (box "p1") "from endpoint"
            Expect.equal (m.["_to"]) (box "p2") "to endpoint"

        testCase "escapeIdent / labelSuffix backtick-escape identifiers"
        <| fun _ ->
            Expect.equal (CypherTranslation.escapeIdent "Person") "`Person`" "plain label"
            Expect.equal (CypherTranslation.escapeIdent "a`b") "`a``b`" "internal backtick doubled"
            Expect.equal (CypherTranslation.labelSuffix (set [ "Person" ])) ":`Person`" "single label suffix"
            Expect.equal (CypherTranslation.labelSuffix Set.empty) "" "no labels → empty suffix"

        testList "property-partition scope guard (injectScopeGuard)" [

            testCase "injects _scope into a bare node pattern"
            <| fun _ ->
                let out = CypherTranslation.injectScopeGuard "MATCH (n) RETURN n"
                Expect.stringContains out "_scope: $__scope" "scope predicate injected"
                Expect.stringContains out "(n {_scope: $__scope})" "bare pattern gets a prop map"

            testCase "merges into an existing inline property map"
            <| fun _ ->
                let out =
                    CypherTranslation.injectScopeGuard "MATCH (n:Person {city: 'London'}) RETURN n"

                Expect.stringContains out "_scope: $__scope" "scope merged"
                Expect.stringContains out "city: 'London'" "original inline prop preserved"

            testCase "scopes both node patterns of a relationship pattern, leaving the relationship untouched"
            <| fun _ ->
                let out =
                    CypherTranslation.injectScopeGuard "MATCH (a:Person)-[:KNOWS]->(b:Person) RETURN a"

                Expect.stringContains out "(a:Person {_scope: $__scope})" "left node scoped"
                Expect.stringContains out "(b:Person {_scope: $__scope})" "right node scoped"
                Expect.stringContains out "-[:KNOWS]->" "relationship pattern untouched"

            testCase "does NOT rewrite a function call (parens preceded by an identifier)"
            <| fun _ ->
                let out = CypherTranslation.injectScopeGuard "MATCH (n:Person) RETURN count(n)"
                Expect.stringContains out "count(n)" "count(n) not rewritten as a node pattern"
                Expect.stringContains out "(n:Person {_scope: $__scope})" "the actual node pattern is scoped"
        ]

        testList "database-name derivation (build-once/read-per-call seam)" [

            testCase "is a valid Neo4j database name (letter-initial, [a-z0-9-], ≤63)"
            <| fun _ ->
                let db = SessionScope.databaseName "tenant" "Team-A/42:weird"
                Expect.isTrue (db.Length >= 3 && db.Length <= 63) "length within 3..63"
                Expect.isTrue (Char.IsLetter db.[0]) "starts with a letter"

                Expect.isTrue
                    (db
                     |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-'))
                    "only lowercase alphanumerics + dash"

            testCase "is deterministic and distinct for distinct scopes"
            <| fun _ ->
                let a1 = SessionScope.databaseName "tenant" "team-a"
                let a2 = SessionScope.databaseName "tenant" "team-a"
                let b = SessionScope.databaseName "tenant" "team-b"
                Expect.equal a1 a2 "same scope → same database (deterministic)"
                Expect.notEqual a1 b "distinct scopes → distinct databases"
        ]

        testCase "classifyError folds an unrecognised exception to StorageFailure (retry channel is data)"
        <| fun _ ->
            match CypherTranslation.classifyError (Exception "boom") with
            | StorageFailure msg -> Expect.stringContains msg "boom" "message carried"
            | other -> failtestf "expected StorageFailure, got %A" other
    ]

// ── Live conformance arm — env-gated ─────────────────────────────────

let liveTests =
    match Environment.GetEnvironmentVariable "TOOLUP_TEST_NEO4J_URI" with
    | null
    | "" ->
        testList "Phase 68b — Neo4jGraphStore (live)" [
            ptestCase "skipped — TOOLUP_TEST_NEO4J_URI not set" <| fun _ -> ()
        ]
    | uri ->
        let user =
            Environment.GetEnvironmentVariable "TOOLUP_TEST_NEO4J_USER"
            |> Option.ofObj
            |> Option.defaultValue "neo4j"

        let password =
            Environment.GetEnvironmentVariable "TOOLUP_TEST_NEO4J_PASSWORD"
            |> Option.ofObj
            |> Option.defaultValue "neo4j"

        // A single shared driver (host-lifetime singleton). PropertyPartition
        // over the default database, so the arm runs on any Neo4j edition; each
        // factory call uses GUID-suffixed scopes so concurrent cases isolate.
        let driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password))

        let config = Neo4jGraphStoreConfig.propertyPartition "neo4j"

        let store = Neo4jGraphStore.ofDriver config driver

        let factory () =
            let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
            store, "team-a-" + suffix, "team-b-" + suffix

        GraphStoreContract.tests "Neo4jGraphStore" factory