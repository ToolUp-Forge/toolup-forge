// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.EntityTypes

open System
open Microsoft.FSharp.Reflection

// ─── Entity store substrate ─────────────────────────────────────────
//
// Typed entity store on top of `IDataObjectStore` (versioned blobs)
// and `BlobIndex` (secondary indexes). Lets a module store
// domain records — appointments, line items, contacts, comments —
// and query them by declared indexed fields without rolling its own
// persistence layer.
//
// Domain shape: an "entity" is any F# record carrying three required
// fields:
//
//     type Appointment = {
//         Id: EntityId        // unique identifier within (entityType, scope)
//         Type: string        // entity-type discriminator, e.g. "Appointment"
//         Version: int        // monotonic, set by the store on save
//         // ... user-defined fields
//         PatientId: string
//         ProviderId: string
//         Date: DateOnly
//     }
//
// Entities are versioned by `IDataObjectStore` with `Versioned`
// policy — every Save bumps the version; the previous version is
// preserved for audit / rollback. `Delete` removes the head version's
// metadata; the underlying `IDataObjectStore` decides whether
// historical versions persist (typically yes — soft-delete-aware).
//
// `IEntity`-shape is enforced by convention plus runtime reflection,
// not a marker interface. Marker interfaces don't survive Fable
// erasure cleanly and would force every entity-record consumer to
// implement an interface that adds no static value. The
// `tryGetEntityFields` helper extracts the three required fields
// from any record and returns `Error` when they're absent.

/// Stable identifier for an entity instance. `string` (not `Guid`)
/// because some domains have natural keys (SKU codes, ISIN, USDOT
/// numbers); the SDK doesn't impose a key shape, only that it
/// round-trips through the index segment naming and stays unique
/// within `(entityType, scope)`.
type EntityId = string

/// Lightweight reference to an entity, returned by index lookups
/// before the full record is downloaded. `EntityRef` carries enough
/// information to fetch the full entity (`Id` + `Type`) plus the
/// version it was stored at (so callers can compare "do I already
/// have the latest?"). `'T` is a phantom-typed marker; the actual
/// downloaded entity may carry additional fields beyond the three
/// required ones.
type EntityRef<'T> = {
    Id: EntityId
    Type: string
    Version: int
}

/// Why an entity-store operation failed.
type EntityError =
    /// No entity with this `(type, id)` exists in the scope.
    | NotFound of entityType: string * entityId: EntityId
    /// Save attempted to write a version older than the current one
    /// — typically indicates a stale read-modify-write race. The
    /// caller refreshes and retries.
    | VersionConflict of entityType: string * entityId: EntityId * expected: int * actual: int
    /// The entity type wasn't registered via `ServerApp.withEntities`.
    | UnknownEntityType of entityType: string
    /// Predicate or `FindByIndex` referenced an index that wasn't
    /// declared for this entity type. Surface includes the index
    /// name so the caller can correct the registration.
    | InvalidIndex of indexName: string
    /// Reflection-time validation failed — the supplied record is
    /// missing the required `Id`/`Type`/`Version` fields, or one of
    /// them has the wrong type. Diagnostic only; message is for
    /// developer-facing logs, not API clients.
    | InvalidEntityShape of message: string
    /// Underlying storage layer (blob / data-object) returned an
    /// error that couldn't be mapped to a more specific case.
    | StorageFailure of message: string

module EntityError =
    let message (err: EntityError) =
        match err with
        | NotFound(t, id) -> sprintf "Entity not found: %s/%s" t id
        | VersionConflict(t, id, exp, act) -> sprintf "Version conflict on %s/%s: expected %d, actual %d" t id exp act
        | UnknownEntityType t -> sprintf "Unknown entity type: %s (not registered via ServerApp.withEntities)" t
        | InvalidIndex name -> sprintf "Invalid index reference: %s" name
        | InvalidEntityShape msg -> sprintf "Invalid entity shape: %s" msg
        | StorageFailure msg -> sprintf "Entity store storage failure: %s" msg

/// Core fields every entity record must carry. Used by the runtime
/// reflection helper to extract them from any user record.
type EntityFieldsCore = {
    Id: EntityId
    Type: string
    Version: int
}

/// Extract the three required fields from any record. Returns
/// `Error` when the record doesn't carry them. Used by the entity
/// store to validate user-supplied records before persisting.
///
/// The reflection cost is paid once per Save / Get call — not on a
/// hot path inside the store. For a high-throughput inner loop, the
/// caller can cache the resolved field readers per `Type`.
///
/// Marked `inline` so Fable can resolve `typeof<'T>` at the call site
/// — Fable erases generics at runtime, so generic reflection only
/// works through the call-site-inlining path. The .NET compile is
/// unchanged (BCL handles generic reflection natively at runtime).
let inline tryGetEntityFields (entity: 'T) : Result<EntityFieldsCore, string> =
    let t = typeof<'T>

    if not (FSharpType.IsRecord t) then
        Error(sprintf "Entity must be an F# record, got %s" t.FullName)
    else
        let fields = FSharpType.GetRecordFields t
        let values = FSharpValue.GetRecordFields entity

        let lookup name =
            let idx = fields |> Array.tryFindIndex (fun f -> f.Name = name)

            match idx with
            | Some i -> Some(fields[i], values[i])
            | None -> None

        match lookup "Id", lookup "Type", lookup "Version" with
        | Some(idField, idValue), Some(_, typeValue), Some(_, versionValue) ->
            if idField.PropertyType <> typeof<string> then
                Error "Id field must be of type string"
            elif typeValue :? string |> not then
                Error "Type field must be of type string"
            elif versionValue :? int |> not then
                Error "Version field must be of type int"
            else
                Ok {
                    Id = idValue :?> string
                    Type = typeValue :?> string
                    Version = versionValue :?> int
                }
        | None, _, _ -> Error "Entity record is missing required field: Id"
        | _, None, _ -> Error "Entity record is missing required field: Type"
        | _, _, None -> Error "Entity record is missing required field: Version"

/// One declared index on an entity type. Maps an entity instance to
/// a `string` key — the segment name in `BlobIndex`. Compound
/// indexes serialise the key tuple to a single string with a
/// pipe-character separator (`|`); collisions are caller's
/// responsibility.
type EntityIndex<'T> = {
    /// Unique-within-this-entity-type index name. Used in
    /// `FindByIndex` and `EntityQuery.Predicate` references.
    Name: string
    /// Field extractor. Receives the entity instance and returns the
    /// indexed value as a `string`. The `EntityRegistration.withIndex`
    /// builder erases the typed extractor to `'T -> string` here so
    /// the registration list can be heterogeneous.
    Extract: 'T -> string
    /// `true` for compound indexes constructed via
    /// `withCompoundIndex`. Compound indexes only match `And`-of-`Eq`
    /// predicates against their declared key tuple — single-field
    /// predicates won't fire them. Non-compound indexes match `Eq`
    /// /`Ne`/`Gt`/`Lt`/`Gte`/`Lte`/`In` predicates as expected.
    IsCompound: bool
}

// ─── Phase 19c — declarative relationships ──────────────────────────
//
// A relationship declares a link from one entity type to another as
// first-class registration metadata rather than an ad-hoc foreign-key
// field the consumer traverses by hand. Declared once, it drives (a)
// relationship-aware queries (`EntityQuery.relatedTo`, which compiles to
// an indexed `Eq`/`In` over the foreign-key field) and (b) an
// enumerable edge set for a graph projection. It is additive and opt-in
// — an entity with no declared relationships behaves exactly as before
// (GP 11 / GP 13). Relationship metadata is identity-by-value (entity
// *ids* as strings, no live handles — GP 12).

/// Multiplicity of a declared relationship.
[<RequireQualifiedAccess>]
type Cardinality =
    | OneToOne
    | OneToMany
    | ManyToOne
    | ManyToMany

/// Direction the relationship points relative to the declaring entity.
/// `Outgoing` — this entity references the target (the common case, the
/// declaring entity carries the foreign key). `Incoming` — the target
/// references this entity (the inverse view).
[<RequireQualifiedAccess>]
type RelationshipDirection =
    | Outgoing
    | Incoming

/// The association ("join") entity that resolves a `ManyToMany`
/// relationship. The source side of the link uses the relationship's
/// `ForeignKeyField` (the join entity's field referencing the declaring
/// entity); this record names the join entity type and the field
/// carrying the *target* entity's id. Resolution is two indexed lookups:
/// query the join entity by the source key, then load the targets by the
/// extracted `TargetKeyField` values. Both fields should be indexed on
/// the join entity's own registration.
type JoinEntitySpec = {
    /// The join / association entity type name.
    EntityType: string
    /// The field on the join entity carrying the target entity's id.
    TargetKeyField: string
}

/// A declared relationship between entity types. Attach with
/// `EntityRegistration.withRelationship`. `ForeignKeyField` carries the
/// related id: for `OneToOne` / `OneToMany` / `ManyToOne` it is a field
/// on THIS entity; for `ManyToMany` it is the field on the join entity
/// (`JoinEntity.EntityType`) referencing this entity. `Name` is unique
/// within the declaring entity type.
type Relationship = {
    Name: string
    /// The target entity type this relationship points at.
    Target: string
    Cardinality: Cardinality
    /// The field carrying the related id (see the type doc for which
    /// entity it lives on per cardinality).
    ForeignKeyField: string
    Direction: RelationshipDirection
    /// Required for `ManyToMany` (the association entity + its
    /// target-key field); `None` for the other cardinalities.
    JoinEntity: JoinEntitySpec option
}

/// Registration of an entity type with the store. Built via
/// `EntityRegistration.create<'T> typeName |> withIndex ...`. The
/// resulting record is passed to `ServerApp.withEntities` at compose
/// time. `EntityType` matches the `Type` field on instances of `'T`.
type EntityRegistration<'T> = {
    EntityType: string
    Indexes: EntityIndex<'T> list
    /// Phase 19c — declared relationships to other entity types.
    /// Empty (the default) — an entity with no declared relationships is
    /// byte-identical to a pre-19c registration (GP 11).
    Relationships: Relationship list
}

module EntityRegistration =
    /// Start building a registration for an entity type. `entityType`
    /// must equal the `Type` field on instances of `'T` — the store
    /// validates this at Save time and returns `Error InvalidEntityShape`
    /// on mismatch.
    let create<'T> (entityType: string) : EntityRegistration<'T> = {
        EntityType = entityType
        Indexes = []
        Relationships = []
    }

    /// Declare a single-field index. The extractor is invoked on
    /// every `Save` to keep the index in sync.
    let withIndex (name: string) (extractor: 'T -> string) (reg: EntityRegistration<'T>) : EntityRegistration<'T> = {
        reg with
            Indexes =
                reg.Indexes
                @ [
                    {
                        Name = name
                        Extract = extractor
                        IsCompound = false
                    }
                ]
    }

    /// Declare a compound index over a key tuple. The extractor
    /// returns a list of strings; the index combines them with a
    /// pipe (`|`) separator to produce the segment key. Compound
    /// indexes match only `And`-of-`Eq` predicates that hit every
    /// field in the same order.
    let withCompoundIndex
        (name: string)
        (extractor: 'T -> string list)
        (reg: EntityRegistration<'T>)
        : EntityRegistration<'T> =
        let combined entity =
            extractor entity |> List.map _.Replace("|", "_") |> String.concat "|"

        {
            reg with
                Indexes =
                    reg.Indexes
                    @ [
                        {
                            Name = name
                            Extract = combined
                            IsCompound = true
                        }
                    ]
        }

    /// Find an index by name. Returns `None` when the index isn't
    /// declared.
    let tryFindIndex (name: string) (reg: EntityRegistration<'T>) : EntityIndex<'T> option =
        reg.Indexes |> List.tryFind (fun i -> i.Name = name)

    /// Phase 19c — declare a relationship on the entity type. Validates
    /// at registration time (fail-fast at compose) and — for the
    /// foreign-key cardinalities — auto-registers a `BlobIndex` on the
    /// `ForeignKeyField` so relationship-aware queries are indexed
    /// lookups, not scans.
    ///
    /// Validation:
    ///  - a duplicate relationship `Name` is rejected;
    ///  - `ManyToMany` requires a `JoinEntity` (else rejected with a
    ///    message pointing at the join-entity pattern);
    ///  - every other cardinality requires `ForeignKeyField` to be a
    ///    real field on the entity record `'T` (reflection check, the
    ///    same shape-by-reflection approach as `tryGetEntityFields`) —
    ///    and `JoinEntity` must be `None`.
    ///
    /// Auto-index: for a foreign-key cardinality, appends an
    /// `EntityIndex` over `ForeignKeyField` unless one with that `Name`
    /// is already declared (idempotent with an explicit `withIndex` on
    /// the same field). For `ManyToMany` the key lives on the join
    /// entity, so no index is added here — index the two keys on the
    /// join entity's own registration.
    ///
    /// `inline` so `typeof<'T>` resolves at the call site under Fable
    /// (the same reason `tryGetEntityFields` is inline).
    let inline withRelationship (relationship: Relationship) (reg: EntityRegistration<'T>) : EntityRegistration<'T> =
        if reg.Relationships |> List.exists (fun r -> r.Name = relationship.Name) then
            failwithf "Duplicate relationship name '%s' on entity type '%s'." relationship.Name reg.EntityType

        match relationship.Cardinality with
        | Cardinality.ManyToMany ->
            match relationship.JoinEntity with
            | Some _ -> ()
            | None ->
                failwithf
                    "ManyToMany relationship '%s' on '%s' requires a JoinEntity (the association entity + its target-key field). Declare it via JoinEntitySpec, or model the link as a direct foreign key with a ManyToOne/OneToMany cardinality."
                    relationship.Name
                    reg.EntityType
        | _ ->
            match relationship.JoinEntity with
            | Some _ ->
                failwithf
                    "Relationship '%s' on '%s' declares a JoinEntity but is not ManyToMany — a join entity only resolves a ManyToMany link. Drop the JoinEntity or set Cardinality = ManyToMany."
                    relationship.Name
                    reg.EntityType
            | None -> ()

            let recordFields =
                let t = typeof<'T>

                if FSharpType.IsRecord t then
                    FSharpType.GetRecordFields t |> Array.map (fun f -> f.Name)
                else
                    [||]

            if not (Array.contains relationship.ForeignKeyField recordFields) then
                failwithf
                    "Relationship '%s' on '%s' declares ForeignKeyField '%s', which is not a field on the entity record. Available fields: %s."
                    relationship.Name
                    reg.EntityType
                    relationship.ForeignKeyField
                    (String.concat ", " recordFields)

        // Auto-index the foreign-key field (foreign-key cardinalities
        // only; idempotent with an explicit withIndex on the same field).
        let withAutoIndex (r: EntityRegistration<'T>) : EntityRegistration<'T> =
            match relationship.Cardinality with
            | Cardinality.ManyToMany -> r
            | _ ->
                if r.Indexes |> List.exists (fun i -> i.Name = relationship.ForeignKeyField) then
                    r
                else
                    let fkField = relationship.ForeignKeyField

                    let fieldIndex =
                        FSharpType.GetRecordFields(typeof<'T>)
                        |> Array.findIndex (fun f -> f.Name = fkField)

                    let extract (entity: 'T) : string =
                        let value = (FSharpValue.GetRecordFields(box entity))[fieldIndex]
                        if isNull value then "" else string value

                    {
                        r with
                            Indexes =
                                r.Indexes
                                @ [
                                    {
                                        Name = fkField
                                        Extract = extract
                                        IsCompound = false
                                    }
                                ]
                    }

        withAutoIndex {
            reg with
                Relationships = reg.Relationships @ [ relationship ]
        }

    /// Declare several relationships in one call (fold of
    /// `withRelationship`, so the same validation + auto-index applies to
    /// each). `inline` for the same Fable reason as `withRelationship`.
    let inline withRelationships
        (relationships: Relationship list)
        (reg: EntityRegistration<'T>)
        : EntityRegistration<'T> =
        relationships |> List.fold (fun acc rel -> withRelationship rel acc) reg

    /// Phase 19c — read accessor for the declared relationships. The
    /// seam a graph-projection bridge reads to emit an entity's edges;
    /// a pure read of registration metadata (no coupling to any graph
    /// package).
    let relationships (reg: EntityRegistration<'T>) : Relationship list = reg.Relationships