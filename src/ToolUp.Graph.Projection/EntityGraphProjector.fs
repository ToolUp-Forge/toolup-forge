// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Projection

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Text.RegularExpressions
open Microsoft.Extensions.DependencyInjection
open ToolUp.Graph
open ToolUp.Platform
open ToolUp.Platform.IEntityStore

// ─── EntityGraphProjector — sync + rebuild (68d.B / 68d.C) ──────────
//
// The default `IEntityGraphProjection`. Drives the pure
// `EntityProjection.projectEntity` against a live `IEntityStore` +
// `IGraphStore`. Everything mutating is scope-isolated (GP 4 — every method
// takes `scopeId`).
//
// Six-rule portability audit (GP 12):
//   1. Identity by value       — node/edge ids are the deterministic
//                                strings from `EntityProjection`; the
//                                projector holds no live handles.
//   2. Async at every boundary — every seam method returns `Async<_>`.
//   3. Retry/supervision as data — sync failures surface as
//                                `ProjectionError` (no callbacks); a
//                                failed / missed signal is reconcilable via
//                                `RebuildProjection` rather than a silent
//                                drop, and `lastProjectedVersion` records
//                                what was last projected per entity.
//   4. Stateless handlers      — a `SyncEntity` call re-derives everything
//                                from `scopeId` + the stores; the only
//                                retained state is the reconciliation
//                                bookkeeping cache, which is advisory (a
//                                rebuild re-derives it).
//   5. No cross-shard ordering — sync is ordered within a single entity
//                                (upsert-then-edges) but the projector
//                                promises no ordering across entities.
//   6. Precision at lower bound — inherited from `EntityProjection`.

/// The default `IEntityGraphProjection`. Construct over the resolved
/// `IEntityStore` + `IGraphStore` and the enrolled projected types; safe to
/// share across requests (scopes are partitioned by the stores, and the
/// bookkeeping cache is concurrent).
type EntityGraphProjector
    (entityStore: IEntityStore, graphStore: IGraphStore, projectedTypes: ProjectedEntityType list, ?logger: ILogger) =

    let byType = projectedTypes |> List.map (fun p -> p.EntityType, p) |> Map.ofList

    // Phase 68d.B — `lastProjectedVersion` per entity. Records the version
    // most recently projected so a missed / out-of-order signal is
    // reconcilable (a rebuild re-derives it). Advisory — never a hard gate;
    // the deterministic ids already make a re-apply a no-op.
    let lastProjectedVersion = ConcurrentDictionary<string, int>()

    // Entity-type names are developer-controlled identifiers; guard the
    // orphan-enumeration Cypher against anything that is not a bare
    // identifier so the label can never carry an injection.
    let identifier = Regex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled)

    let versionKey (scopeId: string) (entityType: string) (entityId: string) =
        sprintf "%s|%s|%s" scopeId entityType entityId

    let warn (message: string) =
        match logger with
        | Some l -> l.Warn(sprintf "[EntityGraphProjection] %s" message)
        | None -> ()

    /// Create a labelled stub for a not-yet-projected relationship endpoint
    /// so an edge into it does not dangle. The stub is a proper projected
    /// node id, so the target entity's own projection replaces it and a
    /// rebuild removes it if the target never materialises.
    let ensureEndpoint (scopeId: string) (id: NodeId) = async {
        match! graphStore.GetNode(scopeId, id) with
        | Some _ -> ()
        | None ->
            let label = EntityProjection.labelOfNodeId id

            let stub = {
                Id = id
                Labels = (if label = "" then Set.empty else Set.singleton label)
                Properties = Map.empty
            }

            let! _ = graphStore.UpsertNode(scopeId, stub)
            ()
    }

    /// Upsert an edge, tolerating a dangling endpoint by stubbing it and
    /// retrying once (the incremental path — a relationship target may not
    /// be projected yet).
    let upsertEdgeTolerant (scopeId: string) (edge: GraphEdge) = async {
        match! graphStore.UpsertEdge(scopeId, edge) with
        | Ok _ -> ()
        | Error(DanglingEdge(_, missing)) ->
            do! ensureEndpoint scopeId missing
            let! retry = graphStore.UpsertEdge(scopeId, edge)

            match retry with
            | Ok _ -> ()
            | Error e -> warn (sprintf "edge upsert failed after stubbing endpoint: %s" (GraphError.message e))
        | Error e -> warn (sprintf "edge upsert failed: %s" (GraphError.message e))
    }

    /// Whether an outgoing edge of the given label to the given target
    /// already exists — used to count only *novel* edges on rebuild
    /// (idempotency). Uses only the portable `Neighbours` API.
    let edgeExists (scopeId: string) (edge: GraphEdge) = async {
        let! neighbours = graphStore.Neighbours(scopeId, edge.From, Outgoing, Some edge.Label)
        return neighbours |> List.exists (fun n -> n.Id = edge.To)
    }

    let recordVersion (scopeId: string) (entityType: string) (entityId: string) (node: GraphNode) =
        match node.Properties.TryFind "Version" with
        | Some(PInt v) -> lastProjectedVersion[versionKey scopeId entityType entityId] <- int v
        | _ -> ()

    /// The version most recently projected for an entity, if the projector
    /// has seen it. Advisory reconciliation state (68d.B).
    member _.LastProjectedVersion(scopeId: string, entityType: string, entityId: string) : int option =
        match lastProjectedVersion.TryGetValue(versionKey scopeId entityType entityId) with
        | true, v -> Some v
        | _ -> None

    interface IEntityGraphProjection with

        member _.SyncEntity(scopeId, entityType, entityId) = async {
            match byType.TryFind entityType with
            | None -> return Error(UnknownProjectedType entityType)
            | Some pt ->
                match! pt.ProjectById entityStore scopeId entityId with
                | None ->
                    // The entity is gone by the time we load it (a delete
                    // racing an update signal). Keep the graph consistent by
                    // removing its node rather than leaving a stale one.
                    let! _ = graphStore.DeleteNode(scopeId, EntityProjection.nodeIdFor entityType entityId)
                    lastProjectedVersion.TryRemove(versionKey scopeId entityType entityId) |> ignore
                    return Ok()
                | Some(node, edges) ->
                    match! graphStore.UpsertNode(scopeId, node) with
                    | Error e -> return Error(GraphWriteFailed(GraphError.message e))
                    | Ok _ ->
                        // Ordered within this entity (node then its edges);
                        // no cross-entity ordering promise (rule 5).
                        for edge in edges do
                            do! upsertEdgeTolerant scopeId edge

                        recordVersion scopeId entityType entityId node
                        return Ok()
        }

        member _.RemoveEntity(scopeId, entityType, entityId) = async {
            match! graphStore.DeleteNode(scopeId, EntityProjection.nodeIdFor entityType entityId) with
            | Ok _ ->
                lastProjectedVersion.TryRemove(versionKey scopeId entityType entityId) |> ignore
                return Ok()
            | Error e -> return Error(GraphWriteFailed(GraphError.message e))
        }

        member _.RebuildProjection(scopeId) = async {
            // Pass 1 — project every present entity of every enrolled type.
            let projected = List<GraphNode * GraphEdge list>()

            for pt in projectedTypes do
                let! ids = pt.ListIds entityStore scopeId

                for id in ids do
                    match! pt.ProjectById entityStore scopeId id with
                    | Some nodeAndEdges -> projected.Add nodeAndEdges
                    | None -> ()

            let presentIds = projected |> Seq.map (fun (n, _) -> n.Id) |> Set.ofSeq

            // Pass 2a — upsert nodes, counting only genuine changes so a
            // second rebuild over an unchanged store reports zero (idempotency).
            let mutable nodesUpserted = 0

            for (node, _) in projected do
                let! existing = graphStore.GetNode(scopeId, node.Id)

                if existing <> Some node then
                    match! graphStore.UpsertNode(scopeId, node) with
                    | Ok _ ->
                        nodesUpserted <- nodesUpserted + 1
                        recordVersion scopeId (EntityProjection.labelOfNodeId node.Id) (NodeId.value node.Id) node
                    | Error e -> warn (sprintf "rebuild node upsert failed: %s" (GraphError.message e))

            // Pass 2b — upsert edges whose endpoints both project (skip an
            // edge into a non-existent target rather than stub it, since the
            // orphan pass would only remove the stub again). Count novel edges.
            let mutable edgesUpserted = 0

            for (_, edges) in projected do
                for edge in edges do
                    if presentIds.Contains edge.From && presentIds.Contains edge.To then
                        let! exists = edgeExists scopeId edge

                        match! graphStore.UpsertEdge(scopeId, edge) with
                        | Ok _ ->
                            if not exists then
                                edgesUpserted <- edgesUpserted + 1
                        | Error e -> warn (sprintf "rebuild edge upsert failed: %s" (GraphError.message e))

            // Pass 3 — orphan removal: a projected-type node in the graph
            // whose source entity is gone (a missed delete signal or an
            // out-of-band graph mutation). Enumerate per enrolled type via
            // the portable `MATCH (n:Label) RETURN n` subset.
            let mutable orphansRemoved = 0

            for pt in projectedTypes do
                if identifier.IsMatch pt.EntityType then
                    let query = CypherQuery.ofText (sprintf "MATCH (n:%s) RETURN n" pt.EntityType)

                    match! graphStore.Query(scopeId, query) with
                    | Ok resultSet ->
                        for row in resultSet.Rows do
                            match row.TryFind "n" with
                            | Some(VNode n) when not (presentIds.Contains n.Id) ->
                                match! graphStore.DeleteNode(scopeId, n.Id) with
                                | Ok _ ->
                                    orphansRemoved <- orphansRemoved + 1

                                    lastProjectedVersion.TryRemove(versionKey scopeId pt.EntityType (NodeId.value n.Id))
                                    |> ignore
                                | Error e -> warn (sprintf "orphan delete failed: %s" (GraphError.message e))
                            | _ -> ()
                    | Error e -> warn (sprintf "orphan enumeration query failed: %s" (GraphError.message e))

            return {
                NodesUpserted = nodesUpserted
                EdgesUpserted = edgesUpserted
                OrphansRemoved = orphansRemoved
            }
        }

/// Decorates an `IAuditLog` so the entity-store lifecycle signal
/// (`EntityCreated` / `EntityUpdated` / `EntityDeleted`) drives the
/// projector (68d.B). Every event is forwarded to the inner log unchanged —
/// audit behaviour is byte-identical — and entity events additionally
/// trigger a projection. Projection is **best-effort**: a failure is logged
/// and swallowed (never fails the audit `Record`, which itself must never
/// fail the primary write), and is reconcilable by a later
/// `RebuildProjection` (rule 3). The projector is supplied as a thunk so DI
/// can break the audit-log ⇆ entity-store ⇆ projector resolution cycle
/// (the thunk is invoked lazily, after the object graph is built).
type ProjectingAuditLog(inner: IAuditLog, projector: unit -> IEntityGraphProjection, ?logger: ILogger) =

    let drive (scopeId: string) (work: Async<Result<unit, ProjectionError>>) = async {
        try
            match! work with
            | Ok() -> ()
            | Error e ->
                match logger with
                | Some l -> l.Warn(sprintf "[EntityGraphProjection] %s (scope=%s)" (ProjectionError.message e) scopeId)
                | None -> ()
        with ex ->
            match logger with
            | Some l -> l.Warn(sprintf "[EntityGraphProjection] projection raised (scope=%s): %s" scopeId ex.Message)
            | None -> ()
    }

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            // Forward first — audit persistence is never gated on projection.
            do! inner.Record(scopeId, audit)

            match audit with
            | EntityCreated p
            | EntityUpdated p -> do! drive scopeId ((projector ()).SyncEntity(scopeId, p.EntityType, p.EntityId))
            | EntityDeleted p -> do! drive scopeId ((projector ()).RemoveEntity(scopeId, p.EntityType, p.EntityId))
            | _ -> ()
        }

        member _.GetAuditTrail(scopeId, dateRange, eventType) =
            inner.GetAuditTrail(scopeId, dateRange, eventType)

/// Composition helpers for the projection bridge (68d.D). The wiring lives
/// here (not in `ToolUp.Platform.Server`) because the bridge references
/// `IEntityStore` — a Platform.Server type — so a Platform.Server → bridge
/// reference would be a project cycle. This mirrors how a graph *engine*
/// companion is composed: the deployment opts in via `ServerConfig`
/// (`withEntityGraphProjection`) and wires the concrete bridge here.
[<RequireQualifiedAccess>]
module EntityGraphProjectionCompose =

    let private optLogger (sp: IServiceProvider) : ILogger option =
        match sp.GetService(typeof<ILogger>) with
        | null -> None
        | o -> Some(o :?> ILogger)

    /// Construct a projector over the given stores + enrollments.
    let create
        (entityStore: IEntityStore)
        (graphStore: IGraphStore)
        (enrollments: ProjectedEntityType list)
        (logger: ILogger option)
        : IEntityGraphProjection =
        match logger with
        | Some l -> EntityGraphProjector(entityStore, graphStore, enrollments, l) :> IEntityGraphProjection
        | None -> EntityGraphProjector(entityStore, graphStore, enrollments) :> IEntityGraphProjection

    /// Decorate an `IAuditLog` so entity lifecycle events drive `projector`.
    let projectingAuditLog
        (inner: IAuditLog)
        (projector: unit -> IEntityGraphProjection)
        (logger: ILogger option)
        : IAuditLog =
        match logger with
        | Some l -> ProjectingAuditLog(inner, projector, l) :> IAuditLog
        | None -> ProjectingAuditLog(inner, projector) :> IAuditLog

    /// The composition seam (68d.D). When the deployment opted in
    /// (`config.EntityGraphProjection = EnabledEntityGraphProjection`),
    /// registers `IEntityGraphProjection` over the resolved `IEntityStore` +
    /// `IGraphStore` and decorates the registered `IAuditLog` so entity
    /// mutations propagate to the graph automatically. When opted out (the
    /// default), this is a no-op — the entity store, the audit log, and the
    /// DI container are byte-identical to today (GP 13).
    ///
    /// Invoke after the base stores + audit log are registered — e.g. from a
    /// `ComposeExtensions.ServiceConfig` hook, which `compose` applies after
    /// the store registrations.
    let wire (services: IServiceCollection) (config: ServerConfig) (enrollments: ProjectedEntityType list) : unit =
        match config.EntityGraphProjection with
        | NoEntityGraphProjection -> ()
        | EnabledEntityGraphProjection ->
            services.AddSingleton<IEntityGraphProjection>(fun (sp: IServiceProvider) ->
                let es = sp.GetRequiredService<IEntityStore>()
                let gs = sp.GetRequiredService<IGraphStore>()
                create es gs enrollments (optLogger sp))
            |> ignore

            // Decorate the last-registered IAuditLog with the projecting
            // wrapper. The projector is resolved lazily inside the wrapper so
            // the (audit-log → entity-store → audit-log) resolution cycle is
            // broken — nothing is resolved during construction.
            let mutable auditIdx = -1

            for i in 0 .. services.Count - 1 do
                if services[i].ServiceType = typeof<IAuditLog> then
                    auditIdx <- i

            if auditIdx >= 0 then
                let baseDescriptor = services[auditIdx]

                let innerFactory (sp: IServiceProvider) : IAuditLog =
                    if not (isNull baseDescriptor.ImplementationInstance) then
                        baseDescriptor.ImplementationInstance :?> IAuditLog
                    elif not (isNull (box baseDescriptor.ImplementationFactory)) then
                        baseDescriptor.ImplementationFactory.Invoke sp :?> IAuditLog
                    else
                        sp.GetService baseDescriptor.ImplementationType :?> IAuditLog

                services[auditIdx] <-
                    ServiceDescriptor(
                        typeof<IAuditLog>,
                        (fun (sp: IServiceProvider) ->
                            let inner = innerFactory sp
                            let projectorThunk = fun () -> sp.GetRequiredService<IEntityGraphProjection>()
                            projectingAuditLog inner projectorThunk (optLogger sp) :> obj),
                        ServiceLifetime.Singleton
                    )