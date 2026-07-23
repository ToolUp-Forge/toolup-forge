// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Neo4j

open System
open System.Collections.Generic
open Neo4j.Driver
open ToolUp.Graph

// ─── Phase 68b — Neo4jGraphStore (distributed openCypher IGraphStore) ─────────
//
// The distributed-tier `IGraphStore`, binding the shared `IGraphStoreContract`
// pack over the Apache-2.0-licensed `Neo4j.Driver`. A query developed against
// the in-memory default runs unchanged here; full openCypher beyond the
// documented subset floor also runs (Neo4j is the openCypher reference).
//
// Build-once / read-per-call (task 68b.A). The `IDriver` is a host-lifetime
// singleton, expensive to build and pooled internally. Tenant context is NOT
// snapshotted onto it at construction; every call opens a fresh session for the
// *current* `scopeId` via `SessionScope.openSession`, so a scope change between
// two calls is reflected on the second.
//
// Retry-as-data (GP 12 rule 3, task 68b.D). Cluster transients (connection
// blips, leader re-election, deadlocks) never cross the async boundary as a
// thrown `TransientException` — `CypherTranslation.classifyError` folds them
// into `GraphError.TransientFailure`, the retryable value the caller loops on.
//
// Tenant isolation (task 68b.C). `MultiDatabase` (default) targets a database
// per scope — the clean isolation; arbitrary `Query` runs verbatim.
// `PropertyPartition` stamps a reserved `_scope` on every node/relationship and
// injects a scope guard into arbitrary `Query` Cypher (fail-closed).

/// Distributed `IGraphStore` over a Neo4j server. Construct via the
/// `Neo4jGraphStore` module (`connect` / `connectWith` / `ofDriver`), register
/// the result as a DI singleton, and select `ServerConfig.GraphStore =
/// CustomGraphStore`. Implements `IDisposable` so the host disposes the driver
/// (closing the connection pool) on shutdown when the store owns it.
type Neo4jGraphStore(driver: IDriver, config: Neo4jGraphStoreConfig, ownsDriver: bool) =

    // A no-op transaction-config for the auto-commit RunAsync overload (the
    // overload's third argument is required from F#).
    static let noTxConfig = Action<TransactionConfigBuilder>(fun _ -> ())

    /// The reserved-`_scope` value for the current isolation mode: `Some
    /// scopeId` under property-partition (constrain on it), `None` under
    /// multi-database (the database is the boundary).
    let scopeValue (scopeId: string) : string option =
        match config.Isolation with
        | PropertyPartition _ -> Some scopeId
        | MultiDatabase _ -> None

    /// The inline `, _scope: $scope` clause for a node pattern (empty under
    /// multi-database).
    let scopeClause (scopeId: string) : string =
        match scopeValue scopeId with
        | Some _ -> sprintf ", %s: $scope" CypherTranslation.ScopeKey
        | None -> ""

    /// Add the `$scope` parameter when property-partitioning.
    let addScopeParam (scopeId: string) (parameters: Dictionary<string, obj>) =
        match scopeValue scopeId with
        | Some s -> parameters.["scope"] <- box s
        | None -> ()

    /// Open a fresh session for `scopeId`, run `body`, and always close it.
    /// Driver exceptions are folded to `GraphError` (rule 3); the close is
    /// best-effort so a fault during cleanup never masks the result.
    let withSession
        (scopeId: string)
        (access: AccessMode)
        (body: IAsyncSession -> Async<Result<'a, GraphError>>)
        : Async<Result<'a, GraphError>> =
        async {
            let session = SessionScope.openSession driver config scopeId access

            let! outcome = async {
                try
                    return! body session
                with ex ->
                    return Error(CypherTranslation.classifyError ex)
            }

            try
                do! session.CloseAsync() |> Async.AwaitTask
            with _ ->
                ()

            return outcome
        }

    /// Run a mutating auto-commit statement and consume its result.
    let runWrite
        (scopeId: string)
        (cypher: string)
        (parameters: Dictionary<string, obj>)
        : Async<Result<unit, GraphError>> =
        withSession scopeId AccessMode.Write (fun session -> async {
            let! cursor =
                session.RunAsync(cypher, parameters :> IDictionary<string, obj>, noTxConfig)
                |> Async.AwaitTask

            let! _ = cursor.ConsumeAsync() |> Async.AwaitTask
            return Ok()
        })

    /// Fetch every record of a read statement.
    let runRead
        (scopeId: string)
        (cypher: string)
        (parameters: Dictionary<string, obj>)
        : Async<Result<IRecord list, GraphError>> =
        withSession scopeId AccessMode.Read (fun session -> async {
            let! cursor =
                session.RunAsync(cypher, parameters :> IDictionary<string, obj>, noTxConfig)
                |> Async.AwaitTask

            let acc = ResizeArray<IRecord>()
            let mutable go = true

            while go do
                let! has = cursor.FetchAsync() |> Async.AwaitTask

                if has then acc.Add cursor.Current else go <- false

            return Ok(List.ofSeq acc)
        })

    /// Dispose the driver (closing the connection pool) when this store owns it.
    /// The host calls this on shutdown for a singleton-registered store.
    member _.Dispose() =
        if ownsDriver then
            try
                // Synchronous dispose of the driver (closes the connection pool)
                // — the non-deprecated path; the host calls this on shutdown.
                (driver :> IDisposable).Dispose()
            with _ ->
                ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    interface IGraphStore with

        member _.UpsertNode(scopeId, node) = async {
            let scope = scopeValue scopeId

            let mergeKeys =
                match scope with
                | Some _ -> sprintf "%s: $id, %s: $scope" CypherTranslation.IdKey CypherTranslation.ScopeKey
                | None -> sprintf "%s: $id" CypherTranslation.IdKey

            let labelSet =
                if node.Labels.IsEmpty then
                    ""
                else
                    sprintf " SET n%s" (CypherTranslation.labelSuffix node.Labels)

            let cypher = sprintf "MERGE (n {%s}) SET n = $props%s" mergeKeys labelSet
            let parameters = Dictionary<string, obj>()
            parameters.["id"] <- box (NodeId.value node.Id)
            parameters.["props"] <- box (CypherTranslation.nodePropertyMap scope node)
            addScopeParam scopeId parameters

            match! runWrite scopeId cypher parameters with
            | Ok() -> return Ok node.Id
            | Error e -> return Error e
        }

        member _.UpsertEdge(scopeId, edge) = async {
            let scope = scopeValue scopeId
            let sc = scopeClause scopeId

            // Phase 1 — both endpoints must exist in this scope, else
            // DanglingEdge naming the missing one (matches the in-memory
            // contract). Two round-trips keeps the diagnostic precise.
            let checkCypher =
                sprintf
                    "OPTIONAL MATCH (a {%s: $from%s}) OPTIONAL MATCH (b {%s: $to%s}) RETURN a IS NOT NULL AS hasA, b IS NOT NULL AS hasB"
                    CypherTranslation.IdKey
                    sc
                    CypherTranslation.IdKey
                    sc

            let checkParams = Dictionary<string, obj>()
            checkParams.["from"] <- box (NodeId.value edge.From)
            checkParams.["to"] <- box (NodeId.value edge.To)
            addScopeParam scopeId checkParams

            match! runRead scopeId checkCypher checkParams with
            | Error e -> return Error e
            | Ok records ->
                let hasA, hasB =
                    match records with
                    | r :: _ -> r.Get<bool>("hasA"), r.Get<bool>("hasB")
                    | [] -> false, false

                if not hasA then
                    return Error(DanglingEdge(edge.Id, edge.From))
                elif not hasB then
                    return Error(DanglingEdge(edge.Id, edge.To))
                else
                    // Phase 2 — MERGE the relationship on its `_id`, then
                    // replace its properties.
                    let cypher =
                        sprintf
                            "MATCH (a {%s: $from%s}), (b {%s: $to%s}) MERGE (a)-[r:%s {%s: $eid}]->(b) SET r = $props"
                            CypherTranslation.IdKey
                            sc
                            CypherTranslation.IdKey
                            sc
                            (CypherTranslation.escapeIdent edge.Label)
                            CypherTranslation.IdKey

                    let parameters = Dictionary<string, obj>()
                    parameters.["from"] <- box (NodeId.value edge.From)
                    parameters.["to"] <- box (NodeId.value edge.To)
                    parameters.["eid"] <- box (EdgeId.value edge.Id)
                    parameters.["props"] <- box (CypherTranslation.edgePropertyMap scope edge)
                    addScopeParam scopeId parameters

                    match! runWrite scopeId cypher parameters with
                    | Ok() -> return Ok edge.Id
                    | Error e -> return Error e
        }

        member _.DeleteNode(scopeId, id) = async {
            let cypher =
                sprintf "MATCH (n {%s: $id%s}) DETACH DELETE n" CypherTranslation.IdKey (scopeClause scopeId)

            let parameters = Dictionary<string, obj>()
            parameters.["id"] <- box (NodeId.value id)
            addScopeParam scopeId parameters
            return! runWrite scopeId cypher parameters
        }

        member _.DeleteEdge(scopeId, id) = async {
            // A relationship carries `_id` (+ `_scope` in partition mode);
            // match it in either direction. Idempotent — 0 matches ⇒ Ok.
            let scopeMatch =
                match scopeValue scopeId with
                | Some _ -> sprintf ", %s: $scope" CypherTranslation.ScopeKey
                | None -> ""

            let cypher =
                sprintf "MATCH ()-[r {%s: $eid%s}]-() DELETE r" CypherTranslation.IdKey scopeMatch

            let parameters = Dictionary<string, obj>()
            parameters.["eid"] <- box (EdgeId.value id)
            addScopeParam scopeId parameters
            return! runWrite scopeId cypher parameters
        }

        member _.GetNode(scopeId, id) = async {
            let cypher =
                sprintf "MATCH (n {%s: $id%s}) RETURN n LIMIT 1" CypherTranslation.IdKey (scopeClause scopeId)

            let parameters = Dictionary<string, obj>()
            parameters.["id"] <- box (NodeId.value id)
            addScopeParam scopeId parameters

            // Absence (and, per the interface's read contract, a transient
            // read fault) is modelled as None.
            match! runRead scopeId cypher parameters with
            | Ok(r :: _) -> return Some(CypherTranslation.nodeOf (r.Get<INode>("n")))
            | Ok [] -> return None
            | Error _ -> return None
        }

        member _.Neighbours(scopeId, id, direction, edgeLabel) = async {
            let relType =
                match edgeLabel with
                | Some l -> ":" + CypherTranslation.escapeIdent l
                | None -> ""

            let arrow =
                match direction with
                | Outgoing -> sprintf "-[r%s]->" relType
                | Incoming -> sprintf "<-[r%s]-" relType
                | Both -> sprintf "-[r%s]-" relType

            let mPattern =
                match scopeValue scopeId with
                | Some _ -> sprintf "(m {%s: $scope})" CypherTranslation.ScopeKey
                | None -> "(m)"

            let cypher =
                sprintf
                    "MATCH (n {%s: $id%s})%s%s RETURN DISTINCT m"
                    CypherTranslation.IdKey
                    (scopeClause scopeId)
                    arrow
                    mPattern

            let parameters = Dictionary<string, obj>()
            parameters.["id"] <- box (NodeId.value id)
            addScopeParam scopeId parameters

            // The interface returns a plain list (rule 3: a read models its
            // only failure — absence — as the empty list).
            match! runRead scopeId cypher parameters with
            | Ok records -> return records |> List.map (fun r -> CypherTranslation.nodeOf (r.Get<INode>("m")))
            | Error _ -> return []
        }

        member _.Query(scopeId, query) = async {
            // MultiDatabase runs the query verbatim (the database is the
            // isolation boundary). PropertyPartition injects a scope guard
            // into node patterns, fail-closed on a pattern it cannot scope.
            let cypherResult =
                match config.Isolation with
                | MultiDatabase _ -> Ok query.Text
                | PropertyPartition _ ->
                    try
                        Ok(CypherTranslation.injectScopeGuard query.Text)
                    with :? CypherScopeGuardException as ex ->
                        Error(MalformedQuery ex.Message)

            match cypherResult with
            | Error e -> return Error e
            | Ok cypher ->
                let parameters = Dictionary<string, obj>()

                for kv in query.Parameters do
                    parameters.[kv.Key] <- CypherTranslation.toNeo4j kv.Value

                match scopeValue scopeId with
                | Some s -> parameters.[CypherTranslation.ScopeParam] <- box s
                | None -> ()

                // Arbitrary Cypher may write, so route as a write (a write
                // server serves reads too; a read replica would reject a
                // write). Documented in README "Causal-cluster routing".
                return!
                    withSession scopeId AccessMode.Write (fun session -> async {
                        let! cursor =
                            session.RunAsync(cypher, parameters :> IDictionary<string, obj>, noTxConfig)
                            |> Async.AwaitTask

                        let! keys = cursor.KeysAsync() |> Async.AwaitTask
                        let columns = keys |> List.ofSeq
                        let rows = ResizeArray<Map<string, GraphValue>>()
                        let mutable go = true

                        while go do
                            let! has = cursor.FetchAsync() |> Async.AwaitTask

                            if has then
                                let record = cursor.Current

                                let row =
                                    columns
                                    |> List.map (fun c -> c, CypherTranslation.graphValueOf record.[c])
                                    |> Map.ofList

                                rows.Add row
                            else
                                go <- false

                        return
                            Ok {
                                Columns = columns
                                Rows = List.ofSeq rows
                            }
                    })
        }

/// Construct a `Neo4jGraphStore`. `connect` / `connectWith` build the driver
/// (host-lifetime singleton) from a bolt URI + credentials; `ofDriver` wraps a
/// driver the consumer already built (e.g. with custom TLS). In a composition
/// the URI + credentials come from `ISecretStore` / the `fromEnv` config
/// helpers — never hard-coded (companion-authoring guide).
[<RequireQualifiedAccess>]
module Neo4jGraphStore =

    /// Build the driver with the config's pool sizing + retry window, and wrap
    /// it in a store that owns (and will dispose) it. `uri` is a bolt /
    /// bolt+routing / neo4j / neo4j+s URI (a `neo4j://` scheme enables
    /// causal-cluster routing).
    let connectWith (config: Neo4jGraphStoreConfig) (uri: string) (username: string) (password: string) : IGraphStore =
        let driver =
            GraphDatabase.Driver(
                uri,
                AuthTokens.Basic(username, password),
                fun (cb: ConfigBuilder) ->
                    cb
                        .WithMaxConnectionPoolSize(config.MaxConnectionPoolSize)
                        .WithConnectionAcquisitionTimeout(config.ConnectionAcquisitionTimeout)
                        .WithMaxTransactionRetryTime(config.MaxTransactionRetryTime)
                    |> ignore
            )

        new Neo4jGraphStore(driver, config, ownsDriver = true) :> IGraphStore

    /// As `connectWith` with the default config (multi-database isolation,
    /// driver-default pool sizing).
    let connect (uri: string) (username: string) (password: string) : IGraphStore =
        connectWith Neo4jGraphStoreConfig.defaults uri username password

    /// Wrap a driver the consumer already built. The store does NOT own the
    /// driver — the consumer disposes it. Use when custom TLS / auth-token
    /// management is needed beyond `connectWith`.
    let ofDriver (config: Neo4jGraphStoreConfig) (driver: IDriver) : IGraphStore =
        new Neo4jGraphStore(driver, config, ownsDriver = false) :> IGraphStore