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

/// Registration of an entity type with the store. Built via
/// `EntityRegistration.create<'T> typeName |> withIndex ...`. The
/// resulting record is passed to `ServerApp.withEntities` at compose
/// time. `EntityType` matches the `Type` field on instances of `'T`.
type EntityRegistration<'T> = {
    EntityType: string
    Indexes: EntityIndex<'T> list
}

module EntityRegistration =
    /// Start building a registration for an entity type. `entityType`
    /// must equal the `Type` field on instances of `'T` — the store
    /// validates this at Save time and returns `Error InvalidEntityShape`
    /// on mismatch.
    let create<'T> (entityType: string) : EntityRegistration<'T> = {
        EntityType = entityType
        Indexes = []
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