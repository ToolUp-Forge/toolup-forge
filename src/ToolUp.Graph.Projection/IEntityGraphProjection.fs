// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Projection

open System
open Microsoft.FSharp.Reflection
open ToolUp.Graph
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Entity→Graph projection: the pure core + the seam (68d.A) ──────
//
// The bridge that makes an `IEntityStore` the system of record and an
// `IGraphStore` a *derived read-model*. An entity becomes a node (label =
// entity `Type`, properties = fields), a Phase-19c declared relationship
// becomes an edge. The mapping is a **pure function** —
// `EntityProjection.projectEntity` — so it is testable without a live
// store, and every downstream path (incremental sync, rebuild) runs the
// same function so the two never drift.
//
// Six-rule portability audit (GP 12) — the projector:
//   1. Identity by value       — a node's id is `entity:{Type}:{Id}`, a
//                                deterministic string; re-projecting the
//                                same entity yields the same `NodeId`
//                                (no live handle, no generated surrogate).
//   6. Precision at lower bound — property mapping tops out at `int64` /
//                                `float`; `decimal` is downcast to `float`
//                                (the graph `PropertyValue` has no decimal
//                                case — an engine could not honour it).
// Rules 2/3/4/5 land on `EntityGraphProjector` (`EntityGraphProjector.fs`).

/// The pure projection function + its property/id helpers. Depends only on
/// the entity registration metadata (Platform.Core) and the graph value
/// model (Graph.Core) — no store, no async, no side effects.
module EntityProjection =

    /// The deterministic node id for an entity. Identity-by-value (rule 1):
    /// the same `(entityType, entityId)` always yields the same `NodeId`,
    /// so a re-projection is an idempotent upsert, never a duplicate.
    let nodeIdFor (entityType: string) (entityId: string) : NodeId =
        NodeId(sprintf "entity:%s:%s" entityType entityId)

    /// Recover the entity-type label from a projected node id
    /// (`entity:{type}:{id}` → `type`). Used to label a stub endpoint the
    /// incremental path creates for a not-yet-projected relationship
    /// target. `""` when the id is not an entity-projected id.
    let internal labelOfNodeId (id: NodeId) : string =
        let s = NodeId.value id
        let prefix = "entity:"

        if s.StartsWith(prefix, StringComparison.Ordinal) then
            let rest = s.Substring prefix.Length

            match rest.IndexOf ':' with
            | -1 -> ""
            | i -> rest.Substring(0, i)
        else
            ""

    /// Map a boxed field value to a graph `PropertyValue`, honouring the
    /// precision floor (rule 6): integers at `int64`, reals at `float`,
    /// `decimal` downcast to `float`, timestamps at `DateTime`. `None` is
    /// returned for a `null` / F# `None` field so the property is simply
    /// omitted rather than represented as a null cell.
    let rec internal propertyValueOfObj (value: obj) : PropertyValue option =
        match value with
        | null -> None
        | :? string as s -> Some(PString s)
        | :? bool as b -> Some(PBool b)
        | :? sbyte as n -> Some(PInt(int64 n))
        | :? byte as n -> Some(PInt(int64 n))
        | :? int16 as n -> Some(PInt(int64 n))
        | :? uint16 as n -> Some(PInt(int64 n))
        | :? int as n -> Some(PInt(int64 n))
        | :? uint32 as n -> Some(PInt(int64 n))
        | :? int64 as n -> Some(PInt n)
        | :? uint64 as n -> Some(PInt(int64 n))
        | :? single as f -> Some(PFloat(float f))
        | :? float as f -> Some(PFloat f)
        // Precision floor (rule 6): the graph model has no decimal case;
        // an engine (Kùzu / Neo4j / AGE) standardises on 64-bit int +
        // IEEE-754 double, so decimal is downcast to float here rather than
        // promising a precision no engine can honour.
        | :? decimal as d -> Some(PFloat(float d))
        | :? DateTime as dt -> Some(PDateTime dt)
        | :? DateTimeOffset as dto -> Some(PDateTime dto.UtcDateTime)
        | :? DateOnly as d -> Some(PDateTime(d.ToDateTime TimeOnly.MinValue))
        | :? TimeOnly as t -> Some(PString(t.ToString "o"))
        | :? Guid as g -> Some(PString(string g))
        | _ ->
            let t = value.GetType()

            if
                t.IsGenericType
                && t.GetGenericTypeDefinition() = typedefof<Option<_>>
                && FSharpType.IsUnion t
            then
                // F# `Some x` — unwrap and recurse; `None` is `null` and is
                // handled by the top branch (property omitted).
                let case, fields = FSharpValue.GetUnionFields(value, t)

                if case.Name = "Some" && fields.Length = 1 then
                    propertyValueOfObj fields[0]
                else
                    None
            else
                // Anything else (enums, custom value types): stringify. The
                // graph substrate imposes no key shape — a stable string is
                // a faithful, engine-portable representation.
                Some(PString(string value))

    /// Read a foreign-key / id field value as a string, unwrapping an F#
    /// option. `""` for a `null` / `None` field (the relationship edge is
    /// then skipped).
    let rec internal idStringOfObj (value: obj) : string =
        match value with
        | null -> ""
        | :? string as s -> s
        | _ ->
            let t = value.GetType()

            if
                t.IsGenericType
                && t.GetGenericTypeDefinition() = typedefof<Option<_>>
                && FSharpType.IsUnion t
            then
                let case, fields = FSharpValue.GetUnionFields(value, t)

                if case.Name = "Some" && fields.Length = 1 then
                    idStringOfObj fields[0]
                else
                    ""
            else
                string value

    /// Project an entity to its graph node + declared-relationship edges
    /// (68d.A). Pure — reflection over `'T`'s record fields, no store, no
    /// async. Node label = the entity `Type`; `NodeId` = `entity:{Type}:{Id}`
    /// (deterministic); properties = every record field mapped to a
    /// `PropertyValue` (precision floor). Edges = the Phase-19c declared
    /// relationships whose foreign key lives on THIS entity (`Outgoing`
    /// foreign-key cardinalities). `Incoming` inverse views are projected
    /// from the entity that carries the key; `ManyToMany` join-resolved
    /// edges are out of scope (they need the join entity's data).
    let projectEntity (reg: EntityRegistration<'T>) (entity: 'T) : GraphNode * GraphEdge list =
        let entityType = reg.EntityType
        let recordType = typeof<'T>

        let fields, values =
            if FSharpType.IsRecord recordType then
                FSharpType.GetRecordFields recordType, FSharpValue.GetRecordFields(box entity)
            else
                [||], [||]

        let pairs = Array.zip fields values

        let idString =
            pairs
            |> Array.tryPick (fun (f, v) -> if f.Name = "Id" then Some(idStringOfObj v) else None)
            |> Option.defaultValue ""

        let sourceId = nodeIdFor entityType idString

        let properties =
            pairs
            |> Array.choose (fun (f, v) -> propertyValueOfObj v |> Option.map (fun pv -> f.Name, pv))
            |> Map.ofArray

        let node = {
            Id = sourceId
            Labels = Set.singleton entityType
            Properties = properties
        }

        let fieldValue (name: string) =
            pairs |> Array.tryPick (fun (f, v) -> if f.Name = name then Some v else None)

        let edges =
            reg.Relationships
            |> List.choose (fun rel ->
                match rel.Cardinality, rel.Direction with
                // ManyToMany resolves through a join entity whose data this
                // entity does not carry — not projectable here (out of scope).
                | Cardinality.ManyToMany, _ -> None
                // Incoming is the inverse view: the foreign key is on the
                // OTHER entity, so the edge is emitted from that entity's
                // own Outgoing projection — not from here.
                | _, RelationshipDirection.Incoming -> None
                | _, RelationshipDirection.Outgoing ->
                    match fieldValue rel.ForeignKeyField with
                    | None -> None
                    | Some v ->
                        let targetId = idStringOfObj v

                        if String.IsNullOrEmpty targetId then
                            None
                        else
                            let toId = nodeIdFor rel.Target targetId

                            Some {
                                // Deterministic edge id (identity by value):
                                // re-projecting yields the same edge, so an
                                // upsert is idempotent.
                                Id =
                                    EdgeId(
                                        sprintf
                                            "entity-edge:%s:%s:%s"
                                            (NodeId.value sourceId)
                                            rel.Name
                                            (NodeId.value toId)
                                    )
                                Label = rel.Name
                                From = sourceId
                                To = toId
                                Properties = Map.empty
                            })

        node, edges

/// A per-entity-type projection enrollment. Captures `'T` in closures so
/// the incremental-sync and rebuild paths can load + project an entity by
/// id without re-deriving its type at runtime. Built with
/// `ProjectedEntityType.ofRegistration`.
type ProjectedEntityType = {
    /// The entity-type name (matches `EntityRegistration.EntityType`).
    EntityType: string
    /// Load entity `entityId` from the store within `scopeId` and project
    /// it. `None` when the entity is absent (a delete race). Signature:
    /// `store -> scopeId -> entityId -> ...`.
    ProjectById: IEntityStore -> string -> string -> Async<(GraphNode * GraphEdge list) option>
    /// Every entity id of this type in `scopeId` (paged through
    /// `IEntityStore.ListAll`). Used by rebuild to enumerate the source set.
    /// Signature: `store -> scopeId -> ...`.
    ListIds: IEntityStore -> string -> Async<string list>
}

[<RequireQualifiedAccess>]
module ProjectedEntityType =
    [<Literal>]
    let private PageSize = 200

    /// Build a projection enrollment from a typed entity registration. The
    /// consumer enlists one per entity type they want mirrored into the
    /// graph — the same registration values they pass to
    /// `ServerApp.withEntity<'T>`.
    let ofRegistration (reg: EntityRegistration<'T>) : ProjectedEntityType = {
        EntityType = reg.EntityType
        ProjectById =
            fun (store: IEntityStore) (scopeId: string) (entityId: string) -> async {
                match! store.Get<'T>(scopeId, reg.EntityType, entityId) with
                | Ok entity -> return Some(EntityProjection.projectEntity reg entity)
                | Error _ -> return None
            }
        ListIds =
            fun (store: IEntityStore) (scopeId: string) -> async {
                let ids = System.Collections.Generic.List<string>()
                let mutable skip = 0
                let mutable more = true

                while more do
                    let! (refs: EntityRef<'T> list) = store.ListAll<'T>(scopeId, reg.EntityType, skip, PageSize)

                    for r in refs do
                        ids.Add r.Id

                    if refs.Length < PageSize then
                        more <- false
                    else
                        skip <- skip + PageSize

                return List.ofSeq ids
            }
    }

/// The opt-in entity→graph projection seam (68d). A derived read-model kept
/// in sync with the entity store: `SyncEntity` / `RemoveEntity` are the
/// lifecycle-signal handlers (driven by `EntityCreated` / `EntityUpdated` /
/// `EntityDeleted`), `RebuildProjection` is the one-shot bootstrap +
/// drift-heal. Scope-isolated by construction — every method takes
/// `scopeId`, so a tenant's entities project only into that tenant's graph
/// scope (inherits the `IGraphStore` structural isolation, GP 4).
type IEntityGraphProjection =
    /// Project (create / update) the entity into the graph: upsert its node
    /// and declared edges. Idempotent — deterministic ids make a re-apply a
    /// no-op. Failures surface as `ProjectionError` data (rule 3), never a
    /// throw.
    abstract SyncEntity: scopeId: string * entityType: string * entityId: string -> Async<Result<unit, ProjectionError>>

    /// Remove the entity's node (and, by cascade, its incident edges) from
    /// the graph. Idempotent — removing an absent node is `Ok`.
    abstract RemoveEntity:
        scopeId: string * entityType: string * entityId: string -> Async<Result<unit, ProjectionError>>

    /// Reconcile the whole `scopeId` graph to match the entity store: upsert
    /// every present entity, remove orphaned projected nodes whose source
    /// entity is gone. Returns the change counts. Idempotent — a second run
    /// over an unchanged store is a no-op (`ProjectionReport.isNoOp`).
    ///
    /// Note (deviation from the phase's `unit -> ...` sketch): rebuild is
    /// scope-parameterised because the graph + entity stores are
    /// scope-partitioned and `IEntityStore` exposes no cross-scope
    /// enumeration — a `unit`-shaped rebuild could not honour tenant
    /// isolation. Bootstrap / heal one scope at a time.
    abstract RebuildProjection: scopeId: string -> Async<ProjectionReport>