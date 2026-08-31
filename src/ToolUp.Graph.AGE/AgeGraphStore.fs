// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.AGE

open System
open System.Collections.Concurrent
open Npgsql
open ToolUp.Graph
open ToolUp.Graph.AGE.CypherToAgeSql

// ─── Phase 68c — AgeGraphStore (Postgres-colocated openCypher IGraphStore) ────
//
// The Postgres-colocated `IGraphStore`, binding the shared `IGraphStoreContract`
// pack over Apache AGE (an Apache-2.0 Postgres extension) reached through the
// already-pinned managed `Npgsql` — no native binding, no per-RID payload, the
// same lane as the shipped Postgres entity-store companion. A query developed
// against the in-memory default runs unchanged here.
//
// Build-once / read-per-call (task 68c.D). The `NpgsqlDataSource` is a
// host-lifetime pooled singleton. Tenant context is NOT snapshotted onto it:
// every call derives its AGE graph from the *current* `scopeId`
// (`AgeGraph.nameFor`), and the AGE session preamble is applied on every
// borrowed connection (`AgeSessionPreamble.applyAsync`), never once at
// construction — so a scope change between two calls is reflected on the second.
//
// Retry-as-data (GP 12 rule 3). Postgres transients never cross the async
// boundary as a thrown exception — `CypherToAgeSql.classifyError` folds them
// into `GraphError.TransientFailure`, the retryable value the caller loops on.
//
// Tenant isolation (task 68c.C). `GraphPerTenant` (default) gives each scope its
// own AGE graph — the clean isolation, arbitrary `Query` runs verbatim.
// `PropertyPartition` stamps a reserved `_scope` on every node/edge and injects
// a scope guard into arbitrary `Query` Cypher (fail-closed).

/// Cypher + agtype-parameter builders for the structured writes, shared by the
/// `IGraphStore` methods and the `AgeSharedTransaction` seam so both emit the
/// identical statement. Pure — `scope` is `Some scopeId` under property-
/// partition (constrain on `_scope`), `None` under graph-per-tenant.
module internal AgeWrites =

    let private scopeInline (scope: string option) : string =
        match scope with
        | Some _ -> sprintf ", %s: $scope" ScopeKey
        | None -> ""

    let private withScope (scope: string option) (args: (string * AgtypeArg) list) : Map<string, AgtypeArg> =
        match scope with
        | Some s -> ("scope", AScalar(PString s)) :: args |> Map.ofList
        | None -> Map.ofList args

    let private addScopeProp (scope: string option) (m: Map<string, PropertyValue>) : Map<string, PropertyValue> =
        match scope with
        | Some s -> m |> Map.add ScopeKey (PString s)
        | None -> m

    /// MERGE-upsert a node (whole-property replacement).
    ///
    /// The property map is bound through `WITH n, $props AS p SET n = p` rather
    /// than the direct `SET n = $props`. AGE refuses a parameter on the right of
    /// a `SET` map assignment — both `SET n = $props` and `SET n += $props` fail
    /// with `0A000: SET clause expects a map` — but accepts a WITH-bound
    /// variable. That keeps every VALUE in the bound `@p` parameter (no property
    /// data is ever interpolated into the Cypher text), and keeps whole-map
    /// REPLACEMENT semantics, which a per-property `SET n.k = $v` expansion would
    /// silently downgrade to a merge that leaves removed keys behind.
    let node (scope: string option) (n: GraphNode) : string * Map<string, AgtypeArg> =
        let label = escapeLabel (ageLabelOf n.Labels)

        let cypher =
            sprintf "MERGE (n:%s {%s: $id%s}) WITH n, $props AS p SET n = p" label IdKey (scopeInline scope)

        let props =
            n.Properties |> Map.add IdKey (PString(NodeId.value n.Id)) |> addScopeProp scope

        let args =
            withScope scope [ "id", AScalar(PString(NodeId.value n.Id)); "props", AMap props ]

        cypher, args

    /// The two-endpoint existence probe for an edge upsert (returns hasA/hasB).
    let edgeCheck (scope: string option) (e: GraphEdge) : string * Map<string, AgtypeArg> =
        let sc = scopeInline scope

        let cypher =
            sprintf
                "OPTIONAL MATCH (a {%s: $from%s}) OPTIONAL MATCH (b {%s: $to%s}) RETURN (a IS NOT NULL) AS hasA, (b IS NOT NULL) AS hasB"
                IdKey
                sc
                IdKey
                sc

        let args =
            withScope scope [
                "from", AScalar(PString(NodeId.value e.From))
                "to", AScalar(PString(NodeId.value e.To))
            ]

        cypher, args

    /// MERGE-upsert an edge, both endpoints already known present.
    let edge (scope: string option) (e: GraphEdge) : string * Map<string, AgtypeArg> =
        let sc = scopeInline scope
        let label = escapeLabel e.Label

        let cypher =
            sprintf
                // `WITH r, $props AS p SET r = p` for the same reason as `node`
                // above — AGE refuses a parameter on the right of a SET map
                // assignment (0A000).
                "MATCH (a {%s: $from%s}), (b {%s: $to%s}) MERGE (a)-[r:%s {%s: $eid}]->(b) WITH r, $props AS p SET r = p"
                IdKey
                sc
                IdKey
                sc
                label
                IdKey

        let props =
            e.Properties
            |> Map.add IdKey (PString(EdgeId.value e.Id))
            |> Map.add FromKey (PString(NodeId.value e.From))
            |> Map.add ToKey (PString(NodeId.value e.To))
            |> addScopeProp scope

        let args =
            withScope scope [
                "from", AScalar(PString(NodeId.value e.From))
                "to", AScalar(PString(NodeId.value e.To))
                "eid", AScalar(PString(EdgeId.value e.Id))
                "props", AMap props
            ]

        cypher, args

/// Low-level AGE execution primitives shared by the store and the shared-
/// transaction seam. Internal — not part of the public surface.
module internal AgeExec =
    /// Ensure the AGE graph exists on `conn` (idempotent, race-tolerant).
    let ensureGraph (timeout: int) (conn: NpgsqlConnection) (graphName: string) : Async<unit> = async {
        try
            use checkCmd =
                new NpgsqlCommand("SELECT count(*) FROM ag_catalog.ag_graph WHERE name = @g", conn)

            checkCmd.CommandTimeout <- timeout
            checkCmd.Parameters.AddWithValue("g", graphName) |> ignore
            let! countObj = checkCmd.ExecuteScalarAsync() |> Async.AwaitTask

            let exists =
                match countObj with
                | :? int64 as l -> l > 0L
                | :? int as i -> i > 0
                | _ -> false

            if not exists then
                use createCmd = new NpgsqlCommand("SELECT ag_catalog.create_graph(@g)", conn)
                createCmd.CommandTimeout <- timeout
                createCmd.Parameters.AddWithValue("g", graphName) |> ignore
                let! _ = createCmd.ExecuteNonQueryAsync() |> Async.AwaitTask
                ()
        with _ ->
            () // a concurrent creator won the race — a real fault surfaces on the query
    }

    /// Build + execute a wrapped Cypher statement on `conn` (optionally within
    /// `tx`), returning the projected column names and each row's agtype cells
    /// as text.
    let execute
        (timeout: int)
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction option)
        (graphName: string)
        (cypher: string)
        (parameters: Map<string, AgtypeArg>)
        : Async<Result<string list * string list list, GraphError>> =
        async {
            let columns = returnColumns cypher

            match wrapQuery graphName cypher (not (Map.isEmpty parameters)) columns with
            | Error msg -> return Error(MalformedQuery msg)
            | Ok(sql, effectiveColumns) ->
                try
                    use cmd = new NpgsqlCommand(sql, conn)
                    cmd.CommandTimeout <- timeout
                    tx |> Option.iter (fun t -> cmd.Transaction <- t)

                    // The projection is uncast `agtype`, an OID plain Npgsql has
                    // no mapping for. Reading every column as unknown hands each
                    // cell back in its agtype TEXT form — the annotated shape
                    // `agtypeToGraphValue` parses. See the CypherToAgeSql header.
                    cmd.AllResultTypesAreUnknown <- true

                    if not (Map.isEmpty parameters) then
                        // BARE + untyped: AGE requires `cypher()`'s third argument
                        // to be a plain Param node (a `::agtype` cast is rejected
                        // with 22023), and `Unknown` sends the agtype map as an
                        // untyped literal Postgres resolves from the signature.
                        let p = NpgsqlParameter("p", NpgsqlTypes.NpgsqlDbType.Unknown)
                        p.Value <- paramsJson parameters
                        cmd.Parameters.Add p |> ignore

                    use! reader = cmd.ExecuteReaderAsync() |> Async.AwaitTask
                    let rows = ResizeArray<string list>()
                    let fieldCount = effectiveColumns.Length
                    let mutable go = true

                    while go do
                        let! has = reader.ReadAsync() |> Async.AwaitTask

                        if has then
                            let cells = [
                                for i in 0 .. fieldCount - 1 -> if reader.IsDBNull i then null else reader.GetString i
                            ]

                            rows.Add cells
                        else
                            go <- false

                    return Ok(effectiveColumns, List.ofSeq rows)
                with ex ->
                    return Error(classifyError ex)
        }

/// Postgres-colocated `IGraphStore` over an AGE-enabled PostgreSQL. Construct via
/// the `AgeGraphStore` module (`connect` / `connectWith` / `ofDataSource`),
/// register the result as a DI singleton, and select `ServerConfig.GraphStore =
/// CustomGraphStore`. Implements `IDisposable` so the host disposes the data
/// source (closing the pool) on shutdown when the store owns it.
type AgeGraphStore(dataSource: NpgsqlDataSource, config: AgeGraphStoreConfig, ownsDataSource: bool) =

    let ensuredGraphs = ConcurrentDictionary<string, bool>()
    let timeout = config.CommandTimeoutSeconds

    let scopeValue (scopeId: string) : string option =
        match config.Isolation with
        | PropertyPartition _ -> Some scopeId
        | GraphPerTenant _ -> None

    /// Open a fresh connection, apply the preamble, ensure the scope's graph,
    /// and run `body`. The connection is always disposed; the preamble is
    /// applied per-open (never snapshotted).
    let withScopedConnection
        (scopeId: string)
        (body: NpgsqlConnection -> string -> Async<Result<'a, GraphError>>)
        : Async<Result<'a, GraphError>> =
        async {
            let graphName = AgeGraph.nameFor config scopeId

            try
                use! conn = dataSource.OpenConnectionAsync().AsTask() |> Async.AwaitTask
                do! AgeSessionPreamble.applyAsync conn

                if not (ensuredGraphs.ContainsKey graphName) then
                    do! AgeExec.ensureGraph timeout conn graphName
                    ensuredGraphs.[graphName] <- true

                return! body conn graphName
            with ex ->
                return Error(classifyError ex)
        }

    /// Execute one Cypher statement in a scoped connection.
    let run (scopeId: string) (cypher: string) (parameters: Map<string, AgtypeArg>) =
        withScopedConnection scopeId (fun conn graphName ->
            AgeExec.execute timeout conn None graphName cypher parameters)

    member _.Dispose() =
        if ownsDataSource then
            try
                dataSource.Dispose()
            with _ ->
                ()

    interface IDisposable with
        member this.Dispose() = this.Dispose()

    interface IGraphStore with

        member _.UpsertNode(scopeId, node) = async {
            let cypher, parameters = AgeWrites.node (scopeValue scopeId) node

            match! run scopeId cypher parameters with
            | Ok _ -> return Ok node.Id
            | Error e -> return Error e
        }

        member _.UpsertEdge(scopeId, edge) = async {
            let scope = scopeValue scopeId
            let checkCypher, checkParams = AgeWrites.edgeCheck scope edge

            match! run scopeId checkCypher checkParams with
            | Error e -> return Error e
            | Ok(_, rows) ->
                let hasA, hasB =
                    match rows with
                    | (a :: b :: _) :: _ -> agtypeBool a, agtypeBool b
                    | _ -> false, false

                if not hasA then
                    return Error(DanglingEdge(edge.Id, edge.From))
                elif not hasB then
                    return Error(DanglingEdge(edge.Id, edge.To))
                else
                    let cypher, parameters = AgeWrites.edge scope edge

                    match! run scopeId cypher parameters with
                    | Ok _ -> return Ok edge.Id
                    | Error e -> return Error e
        }

        member _.DeleteNode(scopeId, id) = async {
            let scopeInline =
                match scopeValue scopeId with
                | Some _ -> sprintf ", %s: $scope" ScopeKey
                | None -> ""

            let cypher = sprintf "MATCH (n {%s: $id%s}) DETACH DELETE n" IdKey scopeInline

            let parameters =
                match scopeValue scopeId with
                | Some s -> Map.ofList [ "id", AScalar(PString(NodeId.value id)); "scope", AScalar(PString s) ]
                | None -> Map.ofList [ "id", AScalar(PString(NodeId.value id)) ]

            match! run scopeId cypher parameters with
            | Ok _ -> return Ok()
            | Error e -> return Error e
        }

        member _.DeleteEdge(scopeId, id) = async {
            let scopeInline =
                match scopeValue scopeId with
                | Some _ -> sprintf ", %s: $scope" ScopeKey
                | None -> ""

            let cypher = sprintf "MATCH ()-[r {%s: $eid%s}]-() DELETE r" IdKey scopeInline

            let parameters =
                match scopeValue scopeId with
                | Some s -> Map.ofList [ "eid", AScalar(PString(EdgeId.value id)); "scope", AScalar(PString s) ]
                | None -> Map.ofList [ "eid", AScalar(PString(EdgeId.value id)) ]

            match! run scopeId cypher parameters with
            | Ok _ -> return Ok()
            | Error e -> return Error e
        }

        member _.GetNode(scopeId, id) = async {
            let scopeInline =
                match scopeValue scopeId with
                | Some _ -> sprintf ", %s: $scope" ScopeKey
                | None -> ""

            let cypher = sprintf "MATCH (n {%s: $id%s}) RETURN n LIMIT 1" IdKey scopeInline

            let parameters =
                match scopeValue scopeId with
                | Some s -> Map.ofList [ "id", AScalar(PString(NodeId.value id)); "scope", AScalar(PString s) ]
                | None -> Map.ofList [ "id", AScalar(PString(NodeId.value id)) ]

            match! run scopeId cypher parameters with
            | Ok(_, (cell :: _) :: _) ->
                match agtypeToGraphValue cell with
                | VNode n -> return Some n
                | _ -> return None
            | _ -> return None
        }

        member _.Neighbours(scopeId, id, direction, edgeLabel) = async {
            let relType =
                match edgeLabel with
                | Some l -> ":" + escapeLabel l
                | None -> ""

            let arrow =
                match direction with
                | Outgoing -> sprintf "-[r%s]->" relType
                | Incoming -> sprintf "<-[r%s]-" relType
                | Both -> sprintf "-[r%s]-" relType

            let scopeInline, mPattern =
                match scopeValue scopeId with
                | Some _ -> sprintf ", %s: $scope" ScopeKey, sprintf "(m {%s: $scope})" ScopeKey
                | None -> "", "(m)"

            let cypher =
                sprintf "MATCH (n {%s: $id%s})%s%s RETURN DISTINCT m" IdKey scopeInline arrow mPattern

            let parameters =
                match scopeValue scopeId with
                | Some s -> Map.ofList [ "id", AScalar(PString(NodeId.value id)); "scope", AScalar(PString s) ]
                | None -> Map.ofList [ "id", AScalar(PString(NodeId.value id)) ]

            match! run scopeId cypher parameters with
            | Ok(_, rows) ->
                return
                    rows
                    |> List.choose (fun row ->
                        match row with
                        | cell :: _ ->
                            match agtypeToGraphValue cell with
                            | VNode n -> Some n
                            | _ -> None
                        | [] -> None)
            | Error _ -> return []
        }

        member _.Query(scopeId, query) = async {
            let cypherResult =
                match config.Isolation with
                | GraphPerTenant _ -> Ok query.Text
                | PropertyPartition _ ->
                    try
                        Ok(injectScopeGuard query.Text)
                    with :? AgeScopeGuardException as ex ->
                        Error(MalformedQuery ex.Message)

            match cypherResult with
            | Error e -> return Error e
            | Ok cypher ->
                let parameters =
                    let scalars = ofScalars query.Parameters

                    match scopeValue scopeId with
                    | Some s -> scalars |> Map.add ScopeParam (AScalar(PString s))
                    | None -> scalars

                match! run scopeId cypher parameters with
                | Error e -> return Error e
                | Ok(columns, rows) ->
                    let mapped =
                        rows
                        |> List.map (fun cells ->
                            List.zip columns cells
                            |> List.map (fun (c, cell) -> c, agtypeToGraphValue cell)
                            |> Map.ofList)

                    return Ok { Columns = columns; Rows = mapped }
        }

/// Construct an `AgeGraphStore`. `connect` / `connectWith` build the
/// `NpgsqlDataSource` (host-lifetime pool) from a Postgres connection string;
/// `ofDataSource` wraps a data source the consumer already built (e.g. one it
/// also uses for its `IEntityStore`, enabling the shared-transaction seam). In a
/// composition the connection string comes from `ISecretStore` / the `fromEnv`
/// config helpers — never hard-coded (companion-authoring guide). The target
/// Postgres must already have the AGE extension installed (`CREATE EXTENSION
/// age;` — a consumer infrastructure prerequisite, documented, not automated).
[<RequireQualifiedAccess>]
module AgeGraphStore =

    /// Build the data source from a connection string and wrap it in a store that
    /// owns (and will dispose) it, with the given config.
    let connectWith (config: AgeGraphStoreConfig) (connectionString: string) : IGraphStore =
        let dataSource = NpgsqlDataSource.Create connectionString
        new AgeGraphStore(dataSource, config, ownsDataSource = true) :> IGraphStore

    /// As `connectWith` with the default config (graph-per-tenant isolation).
    let connect (connectionString: string) : IGraphStore =
        connectWith AgeGraphStoreConfig.defaults connectionString

    /// Wrap a data source the consumer already built. The store does NOT own it —
    /// the consumer disposes it. Use when the same `NpgsqlDataSource` backs the
    /// deployment's `IEntityStore` (the shared-transaction seam).
    let ofDataSource (config: AgeGraphStoreConfig) (dataSource: NpgsqlDataSource) : IGraphStore =
        new AgeGraphStore(dataSource, config, ownsDataSource = false) :> IGraphStore

    /// The AGE graph name a given `scopeId` resolves to under `config`. Exposed
    /// so a consumer using the shared-transaction seam targets the same tenant
    /// graph the store would.
    let graphNameFor (config: AgeGraphStoreConfig) (scopeId: string) : string = AgeGraph.nameFor config scopeId

// ─── Shared-transaction seam (task 68c.C) — AGE-specific, NON-PORTABLE ────────
//
// The unique AGE value-add: because graph data lives in the same Postgres as a
// consumer's relational data, a graph write and a relational write can share one
// `NpgsqlTransaction` and commit/roll back atomically. This is deliberately NOT
// on the `IGraphStore` interface — promoting it would break Kùzu / Neo4j
// portability, which make no cross-store-transaction promise. It is an opt-in
// affordance a consumer reaches for explicitly, on a connection + transaction it
// already opened (typically the same one its `IEntityStore` write uses).

/// Enlist AGE graph writes in a caller-supplied `NpgsqlConnection` +
/// `NpgsqlTransaction`, so they commit or roll back atomically with the caller's
/// relational writes. **AGE-specific and non-portable** — the base `IGraphStore`
/// contract makes no cross-store-transaction promise. The caller owns the
/// connection/transaction lifecycle (open, commit, rollback, dispose); these
/// functions only issue the graph statements on it.
[<RequireQualifiedAccess>]
module AgeSharedTransaction =

    let private isPartition (config: AgeGraphStoreConfig) =
        match config.Isolation with
        | PropertyPartition _ -> true
        | GraphPerTenant _ -> false

    let private scopeVal (config: AgeGraphStoreConfig) (scopeId: string) : string option =
        if isPartition config then Some scopeId else None

    /// Prepare `conn` for AGE within the caller's transaction: apply the preamble
    /// and ensure the scope's graph exists. Call once after opening the
    /// transaction, before `upsertNode` / `upsertEdge`. Returns the graph name.
    let prepare (config: AgeGraphStoreConfig) (conn: NpgsqlConnection) (scopeId: string) : Async<string> = async {
        do! AgeSessionPreamble.applyAsync conn
        let graphName = AgeGraph.nameFor config scopeId
        do! AgeExec.ensureGraph config.CommandTimeoutSeconds conn graphName
        return graphName
    }

    /// Upsert a node within the caller's transaction. `graphName` comes from
    /// `prepare` (or `AgeGraphStore.graphNameFor`).
    let upsertNode
        (config: AgeGraphStoreConfig)
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (graphName: string)
        (scopeId: string)
        (node: GraphNode)
        : Async<Result<NodeId, GraphError>> =
        async {
            let cypher, parameters = AgeWrites.node (scopeVal config scopeId) node

            match! AgeExec.execute config.CommandTimeoutSeconds conn (Some tx) graphName cypher parameters with
            | Ok _ -> return Ok node.Id
            | Error e -> return Error e
        }

    /// Upsert an edge within the caller's transaction (both endpoints must
    /// already exist in the scope, as with `IGraphStore.UpsertEdge`).
    let upsertEdge
        (config: AgeGraphStoreConfig)
        (conn: NpgsqlConnection)
        (tx: NpgsqlTransaction)
        (graphName: string)
        (scopeId: string)
        (edge: GraphEdge)
        : Async<Result<EdgeId, GraphError>> =
        async {
            let scope = scopeVal config scopeId
            let timeout = config.CommandTimeoutSeconds
            let checkCypher, checkParams = AgeWrites.edgeCheck scope edge

            match! AgeExec.execute timeout conn (Some tx) graphName checkCypher checkParams with
            | Error e -> return Error e
            | Ok(_, rows) ->
                let hasA, hasB =
                    match rows with
                    | (a :: b :: _) :: _ -> agtypeBool a, agtypeBool b
                    | _ -> false, false

                if not hasA then
                    return Error(DanglingEdge(edge.Id, edge.From))
                elif not hasB then
                    return Error(DanglingEdge(edge.Id, edge.To))
                else
                    let cypher, parameters = AgeWrites.edge scope edge

                    match! AgeExec.execute timeout conn (Some tx) graphName cypher parameters with
                    | Ok _ -> return Ok edge.Id
                    | Error e -> return Error e
        }