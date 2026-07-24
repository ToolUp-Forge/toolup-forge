// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open DataManagementTypes

// ─── Phase 7b — Schema-First User Authoring (substrate slice) ────────
//
// Promotes `DataTypeSchema` (Phase 7a) from a server-side / module-author
// concern into a **user-owned authoring artefact**: an end user (Citizen
// Developer / Vibe-Coder persona) defines, evolves, and versions a data
// schema without writing F# code.
//
// **Substrate slice only.** These are the value types + the wire contract
// the substrate half of Phase 7b ships. The AI-proposes-user-approves
// orchestration flow (an `IAIProvider` tool that produces a candidate
// schema for review) and the three visual/JSON/typed authoring surfaces
// are the *other* half and are NOT defined here. The substrate must
// RECORD AI-proposed provenance (`AuthoredBy.AIWithApproval`) so an
// approved AI proposal is durable + auditable — but the proposing flow
// itself lives elsewhere.
//
// Fable-shared (GP 10): the schema types + the `IUserSchemaApi` remoting
// contract cross the client/server boundary, so they sit in
// `ToolUp.Platform.Core` and compile under both the .NET server and the
// Fable client. No server-tier dependency; pure values + BCL primitives.

/// Scope owning a user-authored schema. String alias matching the forge
/// scope-id convention (`user-{id}` / `team-{id}` / `session-{id}` /
/// `_platform`). Team A's schemas are invisible to Team B — isolation is
/// carried by the owning scope, structurally (GP 4).
type ScopeId = string

/// Stable identity for a user-authored schema (natural key, e.g.
/// `expense-claim`, `intake-v3`). Type alias for `string` so natural keys
/// flow through unchanged — mirrors `FormSchemaId` / `DataTypeId`.
type SchemaId = string

/// Who authored / proposed a schema version. The substrate records this
/// on every committed version so an AI-proposed-then-approved schema is
/// distinguishable in the audit trail from a hand-authored one.
type AuthoredBy =
    /// A human authored (or edited-and-saved) this version directly.
    | Human
    /// The AI proposed this version and a human approved it. Carries the
    /// originating conversation id so the proposal→approval trace is
    /// recoverable. The *proposing* flow that mints this is the Diametrical
    /// half of Phase 7b; the substrate only records the approved outcome.
    | AIWithApproval of conversationId: string

/// BI-friendly semantic column type (Vision §18.4). A deliberate superset
/// of the coarse `DataTypeSchema.ColumnType` primitives (String / Number /
/// Boolean / Date), carrying the semantic meaning the AI and the
/// declarative-UI primitive use to pick appropriate renderers + validators.
/// Every case projects back down to a `ColumnType` via
/// `BIFriendlyType.toColumnType` so a `UserAuthoredSchema` stays queryable
/// through `IDataCatalog` (which speaks `DataTypeSchema`).
///
/// `RequireQualifiedAccess` — the case names (`String` / `Number` / `Date`
/// / …) are deliberately generic and would shadow BCL identifiers if
/// unqualified in the shared `ToolUp.Platform` namespace; callers write
/// `BIFriendlyType.String` etc.
[<RequireQualifiedAccess>]
type BIFriendlyType =
    /// Free text.
    | String
    /// Any numeric value.
    | Number
    /// A boolean flag.
    | Boolean
    /// A calendar date (no time component).
    | Date
    /// A date + time instant.
    | DateTime
    /// A monetary amount in the ISO 4217 currency `code` (e.g. `"USD"`).
    | Currency of code: string
    /// A percentage rendered / validated to `decimals` decimal places.
    | Percentage of decimals: int
    /// An ISO 3166 country code.
    | CountryCode
    /// A closed enumeration over `values`.
    | Enum of values: string list
    /// A reference to a row of another schema/type, keyed by `typeId`.
    | Ref of typeId: string
    /// An opaque identifier (primary/foreign key).
    | Id
    /// An email address.
    | Email
    /// An absolute URL.
    | Url

module BIFriendlyType =
    /// Project a `BIFriendlyType` down to the coarse `DataTypeSchema`
    /// `ColumnType` so a user-authored schema is discoverable + queryable
    /// through the Phase 7a `IDataCatalog` surface without that surface
    /// needing to know the richer semantic vocabulary.
    let toColumnType (t: BIFriendlyType) : ColumnType =
        match t with
        | BIFriendlyType.Number
        | BIFriendlyType.Currency _
        | BIFriendlyType.Percentage _ -> NumberColumn
        | BIFriendlyType.Boolean -> BooleanColumn
        | BIFriendlyType.Date
        | BIFriendlyType.DateTime -> DateColumn
        | BIFriendlyType.String
        | BIFriendlyType.CountryCode
        | BIFriendlyType.Enum _
        | BIFriendlyType.Ref _
        | BIFriendlyType.Id
        | BIFriendlyType.Email
        | BIFriendlyType.Url -> StringColumn

    /// Stable, human-readable label for the semantic type — surfaces in
    /// authoring UIs and the migration preview. Value-stable (do not
    /// rename cases' labels without a wire-version bump).
    let label (t: BIFriendlyType) : string =
        match t with
        | BIFriendlyType.String -> "String"
        | BIFriendlyType.Number -> "Number"
        | BIFriendlyType.Boolean -> "Boolean"
        | BIFriendlyType.Date -> "Date"
        | BIFriendlyType.DateTime -> "DateTime"
        | BIFriendlyType.Currency code -> sprintf "Currency(%s)" code
        | BIFriendlyType.Percentage d -> sprintf "Percentage(%d)" d
        | BIFriendlyType.CountryCode -> "CountryCode"
        | BIFriendlyType.Enum values -> sprintf "Enum(%d)" (List.length values)
        | BIFriendlyType.Ref typeId -> sprintf "Ref(%s)" typeId
        | BIFriendlyType.Id -> "Id"
        | BIFriendlyType.Email -> "Email"
        | BIFriendlyType.Url -> "Url"

/// Per-field data-sensitivity classification (Vision §18). Carried on
/// every column so downstream substrates can act on it: Phase 9h DSR
/// exports redact `PII` / `PHI` / `Financial` / `Secret` appropriately,
/// and Phase 22 per-field encryption-at-rest can key on the same flag.
/// `Public` is the least-sensitive default.
///
/// `RequireQualifiedAccess` — `Public` / `Internal` / `Secret` would
/// otherwise shadow the `ClassificationTypes.ClassificationLevel` cases in
/// the shared namespace; callers write `FieldSensitivity.PII` etc.
[<RequireQualifiedAccess>]
type FieldSensitivity =
    /// No restriction — safe to surface anywhere.
    | Public
    /// Internal-only; not for external distribution.
    | Internal
    /// Personally-identifiable information.
    | PII
    /// Protected health information.
    | PHI
    /// Financial data (account numbers, balances, amounts).
    | Financial
    /// A secret / credential — never surfaced, encrypt at rest.
    | Secret

/// A single field in a user-authored schema. `Name` is the persistence
/// key; `Type` carries the BI-friendly semantic type; `Sensitivity`
/// drives redaction / encryption downstream.
type UserSchemaField = {
    Name: string
    Type: BIFriendlyType
    Required: bool
    Description: string option
    Sensitivity: FieldSensitivity
}

/// Typed schema-evolution step (Vision §18.6). A `UserAuthoredSchema`
/// carries a `MigrationPlan: SchemaMigration list` describing how it
/// evolved from its predecessor; the store's migration executor applies
/// the plan to stored instances (`AddField` / `RemoveField` /
/// `RenameField` transform instance values; the type-change cases are
/// schema-only and leave instance values intact).
type SchemaMigration =
    /// Add `field`, populating every existing instance with `defaultValue`
    /// (the serialised default). This is the migration the acceptance
    /// criteria exercise — v1 → v2 with a new defaulted field.
    | AddField of field: UserSchemaField * defaultValue: string
    /// Drop the field named `fieldName` (removed from every instance).
    | RemoveField of fieldName: string
    /// Rename `fromName` → `toName` (moves the value on every instance).
    | RenameField of fromName: string * toName: string
    /// Narrow a field's type (`fromType` → `toType`). Schema-only — the
    /// stored value is preserved; validation against the new type happens
    /// on subsequent writes.
    | TightenType of fieldName: string * fromType: BIFriendlyType * toType: BIFriendlyType
    /// Widen a field's type (`fromType` → `toType`). Schema-only.
    | LooseType of fieldName: string * fromType: BIFriendlyType * toType: BIFriendlyType
    /// Re-point a `Ref` field from `fromTypeId` to `toTypeId`. Schema-only.
    | ChangeRef of fieldName: string * fromTypeId: string * toTypeId: string

/// A user-owned, versioned data schema. Extends the Phase 7a
/// `DataTypeSchema` (which it projects to via
/// `UserAuthoredSchema.toDataTypeSchema`) with ownership, version
/// provenance, and an evolution/migration lineage.
type UserAuthoredSchema = {
    /// Stable natural key for the schema (identity across versions).
    SchemaId: SchemaId
    /// Human-readable name shown in authoring UIs.
    DisplayName: string
    /// Free-form description (projected to `DataTypeSchema.Description`).
    Description: string
    /// The owning scope. Isolation is by scope (GP 4).
    Owner: ScopeId
    /// Monotonic numeric version, assigned/overwritten by the store on
    /// every save (mirrors the Phase 19 entity-store reflection contract).
    Version: int
    /// Human-facing version label (`"v1"`, `"2026-Q1"`, …) — free-form,
    /// distinct from the store-assigned numeric `Version`.
    VersionLabel: string
    /// Whether this version was hand-authored or an approved AI proposal.
    ProposedBy: AuthoredBy
    /// The schema this version evolved from, when it is a non-trivial
    /// evolution. `None` for a freshly-authored v1.
    EvolvedFrom: SchemaId option
    /// The typed migration steps applied relative to `EvolvedFrom`. Empty
    /// for v1 and for trivial (non-structural) edits.
    MigrationPlan: SchemaMigration list
    /// The schema's fields.
    Fields: UserSchemaField list
}

module UserAuthoredSchema =
    /// Reserved `IDataObjectStore` `DataType` tag under which the store
    /// persists schema *definitions* (versioned). Instance rows of a
    /// schema are tagged with the schema's own `SchemaId`, so the two
    /// never collide.
    [<Literal>]
    let dataType = "_user-schema"

    /// Construct a freshly-authored v1 schema. `Version` defaults to 1
    /// (the store overwrites it on save); `EvolvedFrom` / `MigrationPlan`
    /// are empty; `ProposedBy` defaults to `Human`.
    let create
        (schemaId: SchemaId)
        (displayName: string)
        (owner: ScopeId)
        (fields: UserSchemaField list)
        : UserAuthoredSchema =
        {
            SchemaId = schemaId
            DisplayName = displayName
            Description = ""
            Owner = owner
            Version = 1
            VersionLabel = "v1"
            ProposedBy = Human
            EvolvedFrom = None
            MigrationPlan = []
            Fields = fields
        }

    /// Project to the Phase 7a `DataTypeSchema` so the schema is queryable
    /// through `IDataCatalog.GetSchema`. The BI-friendly semantic types
    /// collapse to their coarse `ColumnType`; the semantic nuance is
    /// preserved only on the `UserAuthoredSchema` itself.
    let toDataTypeSchema (schema: UserAuthoredSchema) : DataTypeSchema = {
        Description = schema.Description
        Columns =
            schema.Fields
            |> List.map (fun f -> {
                Name = f.Name
                Type = BIFriendlyType.toColumnType f.Type
                Required = f.Required
                Description = f.Description
            })
    }

    /// Project to a Phase 7a `DataTypeInfo` (so the schema surfaces in the
    /// data catalog alongside module-declared types).
    let toDataTypeInfo (schema: UserAuthoredSchema) : DataTypeInfo = {
        Id = schema.SchemaId
        DisplayName = schema.DisplayName
        Schema = Some(toDataTypeSchema schema)
    }

    /// Look up a field by name.
    let tryFindField (name: string) (schema: UserAuthoredSchema) : UserSchemaField option =
        schema.Fields |> List.tryFind (fun f -> f.Name = name)

/// Outcome of a `MigrationPlan` execution — reported by the store's
/// migration executor and by the `SchemaMigrationJobHandler` that runs it
/// through `IJobScheduler`. Identity-by-value (GP 12 rule 1).
type MigrationOutcome = {
    /// The schema that was evolved.
    SchemaId: SchemaId
    /// The schema version the migration started from.
    FromVersion: int
    /// The schema version produced by the migration.
    ToVersion: int
    /// Number of stored instance rows the migration transformed.
    InstancesMigrated: int
    /// Number of migration steps applied.
    AppliedMigrations: int
}

/// Typed error surface for the user-schema substrate. Fable-safe (all
/// cases carry values only) so it round-trips over the remoting boundary.
type UserSchemaError =
    /// No schema with this id exists in the caller's scope.
    | SchemaNotFound of schemaId: SchemaId
    /// The schema exists but not at the requested version.
    | SchemaVersionNotFound of schemaId: SchemaId * version: int
    /// The submitted schema failed structural validation.
    | InvalidSchema of reason: string
    /// A migration could not be applied.
    | MigrationFailed of reason: string
    /// The caller lacked the required Owner/Admin authority.
    | UserSchemaUnauthorized of reason: string
    /// The backing store failed.
    | UserSchemaStorageFailure of message: string

// ─── IUserSchemaApi — Fable.Remoting contract ────────────────────────
//
// User-facing CRUD + evolution surface over `UserAuthoredSchema`. Write
// paths are Owner/Admin gated (Phase 69d authorization attributes +
// server-side `TeamRoles.canWriteTeamConfig` in the handler, mirroring
// `IWebhookApi`); reads are tenant-scoped. Scope is resolved server-side
// from `AccessContext` — a caller can never read or write another scope's
// schemas (GP 4). Every state-changing method carries an `[<Audit>]`
// marker so the dispatcher emits the audit row.

/// Fable.Remoting API for authoring, versioning, and evolving
/// user-owned data schemas. Auto-mounted only when
/// `ServerConfig.UserSchemaAuthoring = EnabledUserSchemaAuthoring`
/// (default off — zero cost when unused, GP 13).
type IUserSchemaApi = {
    /// Every schema in the caller's scope, latest version only.
    [<TenantScoped>]
    ListSchemas: unit -> Async<Result<UserAuthoredSchema list, UserSchemaError>>

    /// Latest version of a single schema. `SchemaNotFound` when it does
    /// not exist in the caller's scope (cross-scope reads are impossible).
    [<TenantScoped>]
    GetSchema: SchemaId -> Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// A specific historical version of a schema.
    [<TenantScoped>]
    GetSchemaVersion: SchemaId * int -> Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// Full version history of a schema, oldest-first.
    [<TenantScoped>]
    GetSchemaHistory: SchemaId -> Async<Result<UserAuthoredSchema list, UserSchemaError>>

    /// Author or evolve a schema — commits a new version. Owner/Admin
    /// only. When the committed `ProposedBy` is `AIWithApproval`, this IS
    /// the human-approval act and the store emits `SchemaApproved` in
    /// addition to `SchemaChanged`.
    [<TenantScoped>]
    [<Audit "Custom:SchemaChanged">]
    SaveSchema: UserAuthoredSchema -> Async<Result<UserAuthoredSchema, UserSchemaError>>

    /// Apply a typed migration plan to a schema — evolves the schema to a
    /// new version and transforms stored instances. Owner/Admin only.
    /// Long-running executions run through `IJobScheduler`
    /// (`SchemaMigrationJobHandler`); the synchronous surface here is the
    /// substrate entry point the job handler and the contract pack call.
    [<TenantScoped>]
    [<Audit "Custom:SchemaMigrated">]
    MigrateSchema: SchemaId * SchemaMigration list -> Async<Result<MigrationOutcome, UserSchemaError>>

    /// Delete a schema (all versions) in the caller's scope. Owner/Admin
    /// only. Idempotent — deleting an unknown schema succeeds.
    [<TenantScoped>]
    [<Audit "Custom:SchemaDeleted">]
    DeleteSchema: SchemaId -> Async<Result<unit, UserSchemaError>>
}

module UserSchemaApi =
    /// Fable.Remoting endpoint prefix. Matches the pattern used by
    /// `PlatformApi`, `IWebhookApi`, `IConfigApi`, etc.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"