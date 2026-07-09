// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 448 — dataset substrate types (Fable-safe) ───────────────────
//
// Rectangular typed datasets as first-class, immutable, versioned platform
// objects. A dataset *version* ("vintage") is the addressable unit a
// downstream consumer pins to: assembly produces one, a fit reads one, a
// score writes a new one. These types are the **shared, Fable-safe** surface
// — schema description + paged typed reads — so a client can render a
// dataset's schema and page through its rows. The wire encoding of the rows
// (Parquet, or the BCL JSON-frame default) is a **server-only** concern
// behind `IDatasetStore` / `IDatasetCodec`; **Parquet never reaches the
// client** (GP 1 keeps the vendor codec in a companion; GP 10 keeps these
// crossing types in the shared Core project alongside `DataObjectTypes`).
//
// **Long-format with declared panel keys (D8).** A dataset is one long table;
// time-series, cross-sections, and panels all share the same shape and differ
// only in which columns carry the `PanelUnit` / `PanelPeriod` / `Target`
// roles. Wide pivots are a projection a consumer computes, never a storage
// shape here.

/// Closed dtype for a dataset column. `[<RequireQualifiedAccess>]` — `Float`
/// / `Int` / `Bool` / `Text` / `Timestamp` / `Categorical` are far too
/// generic to sit unqualified in the widely-opened `ToolUp.Platform`
/// namespace.
[<RequireQualifiedAccess>]
type DatasetDType =
    | Float
    | Int
    | Bool
    | Text
    | Timestamp
    | Categorical

module DatasetDType =
    /// Stable case-name string (metadata sidecar, telemetry, companion-codec
    /// mapping). Round-trips through `parse`.
    let name =
        function
        | DatasetDType.Float -> "Float"
        | DatasetDType.Int -> "Int"
        | DatasetDType.Bool -> "Bool"
        | DatasetDType.Text -> "Text"
        | DatasetDType.Timestamp -> "Timestamp"
        | DatasetDType.Categorical -> "Categorical"

    /// Inverse of `name`. `None` for an unknown tag (forward-compat: a newer
    /// producer's dtype the reader does not model).
    let parse =
        function
        | "Float" -> Some DatasetDType.Float
        | "Int" -> Some DatasetDType.Int
        | "Bool" -> Some DatasetDType.Bool
        | "Text" -> Some DatasetDType.Text
        | "Timestamp" -> Some DatasetDType.Timestamp
        | "Categorical" -> Some DatasetDType.Categorical
        | _ -> None

/// The role a column plays in the long-format shape (D8). `Plain` is an
/// ordinary feature/value column; `PanelUnit` / `PanelPeriod` declare the
/// panel key (the entity and the time bucket); `Target` marks the modelled
/// outcome. Roles are **descriptive metadata** the store round-trips and a
/// consumer queries — the store never interprets them (GP 1: no modelling
/// semantics in forge).
[<RequireQualifiedAccess>]
type DatasetColumnRole =
    | Plain
    | PanelUnit
    | PanelPeriod
    | Target

module DatasetColumnRole =
    let name =
        function
        | DatasetColumnRole.Plain -> "Plain"
        | DatasetColumnRole.PanelUnit -> "PanelUnit"
        | DatasetColumnRole.PanelPeriod -> "PanelPeriod"
        | DatasetColumnRole.Target -> "Target"

    let parse =
        function
        | "PanelUnit" -> Some DatasetColumnRole.PanelUnit
        | "PanelPeriod" -> Some DatasetColumnRole.PanelPeriod
        | "Target" -> Some DatasetColumnRole.Target
        | "Plain" -> Some DatasetColumnRole.Plain
        // Unknown / absent role degrades to Plain — a column with an
        // unrecognised role is still an ordinary column, not an error.
        | _ -> None

/// One named, typed column. `Nullable` governs whether a `DatasetValue.Null`
/// cell is permitted; `Role` defaults to `Plain` for the common case.
type DatasetColumn = {
    Name: string
    DType: DatasetDType
    Nullable: bool
    Role: DatasetColumnRole
}

/// The ordered typed columns of a dataset. Row cells align positionally to
/// `Columns` — cell `i` is a value of `Columns[i].DType`.
type DatasetSchema = { Columns: DatasetColumn list }

/// A single typed cell value. Positionally aligned to a `DatasetSchema`
/// column. `[<RequireQualifiedAccess>]` for the same namespace-hygiene reason
/// as `DatasetDType`. `Int` is `int64` (dataset counts / ids routinely exceed
/// `int32`); `Timestamp` is `DateTimeOffset` (identical precision contract to
/// `TimeSeriesPoint`).
[<RequireQualifiedAccess>]
type DatasetValue =
    | Float of float
    | Int of int64
    | Bool of bool
    | Text of string
    | Timestamp of DateTimeOffset
    | Categorical of string
    | Null

module DatasetValue =
    /// The dtype a non-null value carries; `None` for `Null` (a null cell is
    /// compatible with any column whose `Nullable = true`).
    let dtypeOf =
        function
        | DatasetValue.Float _ -> Some DatasetDType.Float
        | DatasetValue.Int _ -> Some DatasetDType.Int
        | DatasetValue.Bool _ -> Some DatasetDType.Bool
        | DatasetValue.Text _ -> Some DatasetDType.Text
        | DatasetValue.Timestamp _ -> Some DatasetDType.Timestamp
        | DatasetValue.Categorical _ -> Some DatasetDType.Categorical
        | DatasetValue.Null -> None

    /// Does `value` fit a column of `dtype` with the given nullability?
    let fits (dtype: DatasetDType) (nullable: bool) (value: DatasetValue) : bool =
        match value with
        | DatasetValue.Null -> nullable
        | _ -> dtypeOf value = Some dtype

    /// Total-ish comparison for filtering. `Some sign` when the two values
    /// are of the same, ordered case (numeric / timestamp / text /
    /// categorical); `Some 0` / non-zero for `Bool` and `Null` equality; and
    /// `None` when the two are of different cases (incomparable — the store
    /// rejects a filter whose literal dtype does not match its column, so an
    /// incomparable pair never reaches an ordering test at read time).
    let compare (a: DatasetValue) (b: DatasetValue) : int option =
        match a, b with
        | DatasetValue.Float x, DatasetValue.Float y -> Some(compare x y)
        | DatasetValue.Int x, DatasetValue.Int y -> Some(compare x y)
        | DatasetValue.Bool x, DatasetValue.Bool y -> Some(compare x y)
        | DatasetValue.Text x, DatasetValue.Text y -> Some(String.CompareOrdinal(x, y))
        | DatasetValue.Categorical x, DatasetValue.Categorical y -> Some(String.CompareOrdinal(x, y))
        | DatasetValue.Timestamp x, DatasetValue.Timestamp y -> Some(compare x y)
        | DatasetValue.Null, DatasetValue.Null -> Some 0
        | DatasetValue.Null, _
        | _, DatasetValue.Null -> None
        | _ -> None

/// One row: a value per schema column, positionally aligned to
/// `DatasetSchema.Columns`.
type DatasetRow = { Cells: DatasetValue list }

module DatasetSchema =
    /// Columns carrying a given role, preserving schema order.
    let columnsWithRole (role: DatasetColumnRole) (schema: DatasetSchema) : DatasetColumn list =
        schema.Columns |> List.filter (fun c -> c.Role = role)

    /// The single `PanelUnit` column, if the schema declares exactly one.
    let panelUnit (schema: DatasetSchema) : DatasetColumn option =
        columnsWithRole DatasetColumnRole.PanelUnit schema |> List.tryExactlyOne

    /// The single `PanelPeriod` column, if the schema declares exactly one.
    let panelPeriod (schema: DatasetSchema) : DatasetColumn option =
        columnsWithRole DatasetColumnRole.PanelPeriod schema |> List.tryExactlyOne

    /// The `Target` columns (a dataset may declare zero, one, or several).
    let targets (schema: DatasetSchema) : DatasetColumn list =
        columnsWithRole DatasetColumnRole.Target schema

    /// Zero-based index of a column by name, or `None`.
    let indexOf (columnName: string) (schema: DatasetSchema) : int option =
        schema.Columns |> List.tryFindIndex (fun c -> c.Name = columnName)

    /// Validate a row against the schema: exactly one cell per column, each
    /// cell fitting its column's dtype + nullability. `Ok ()` or a
    /// human-readable reason (the store lifts it to
    /// `DatasetError.SchemaMismatch`).
    let validateRow (schema: DatasetSchema) (row: DatasetRow) : Result<unit, string> =
        let expected = List.length schema.Columns
        let actual = List.length row.Cells

        if expected <> actual then
            Error $"row has {actual} cells; schema declares {expected} columns"
        else
            let mismatch =
                List.zip schema.Columns row.Cells
                |> List.tryPick (fun (col, cell) ->
                    if DatasetValue.fits col.DType col.Nullable cell then
                        None
                    else
                        Some col.Name)

            match mismatch with
            | Some colName -> Error $"cell for column '{colName}' does not fit its declared dtype/nullability"
            | None -> Ok()

/// A comparison operator for a page filter, expressed as **data** (GP 12
/// rule 3) so any store backend honours it without a caller-supplied
/// callback.
[<RequireQualifiedAccess>]
type DatasetFilterOp =
    | Eq
    | Ne
    | Lt
    | Lte
    | Gt
    | Gte

/// A single column predicate applied before paging. Multiple filters combine
/// with AND. `Value`'s dtype must match `Column`'s dtype (the store rejects a
/// mismatched filter with `SchemaMismatch`).
type DatasetFilter = {
    Column: string
    Op: DatasetFilterOp
    Value: DatasetValue
}

/// A paged, filtered read request. `Offset` / `Limit` page over the rows that
/// match `Filters`, in the dataset's stored (insertion) order — so paging is
/// deterministic across calls (448.E). `Limit` is capped by the store to a
/// safe maximum.
type DatasetPageQuery = {
    Offset: int64
    Limit: int
    Filters: DatasetFilter list
}

module DatasetPageQuery =
    /// A first page of `limit` rows, no filter.
    let firstPage (limit: int) : DatasetPageQuery = {
        Offset = 0L
        Limit = limit
        Filters = []
    }

/// One page of typed rows. `TotalRows` is the count of rows matching the
/// query's filters (across all pages), so a consumer can drive pagination
/// without re-reading. `Schema` is echoed so a page is self-describing.
type DatasetPage = {
    Schema: DatasetSchema
    Rows: DatasetRow list
    Offset: int64
    TotalRows: int64
}

/// Immutable metadata for one dataset version ("vintage"). The store writes
/// one per `Create`; `Version` numbers are linear per `(ScopeId, DatasetId)`
/// starting at 1 (inherited from `IDataObjectStore` versioning). The row
/// bytes live in a content blob addressed by `ContentHash`; `Format` names
/// the wire encoding of those bytes (`"toolup-frame-v1"` for the BCL default
/// codec, `"parquet"` when a Parquet companion codec is composed).
type DatasetVersion = {
    DatasetId: string
    Version: int
    ScopeId: string
    Schema: DatasetSchema
    RowCount: int64
    Format: string
    ContentHash: string
    CreatedAt: DateTimeOffset
    CreatedBy: string
    Metadata: Map<string, string>
}

/// Typed errors from `IDatasetStore`. `[<RequireQualifiedAccess>]` —
/// `NotFound` / `StorageFailure` collide with `DataObjectError` /
/// `TimeSeriesError` cases in the widely-opened namespace.
[<RequireQualifiedAccess>]
type DatasetError =
    /// No dataset with that id exists in the scope.
    | NotFound
    /// The dataset exists but the requested version does not.
    | VersionNotFound of int
    /// Rows did not match the declared schema, or a filter's literal dtype
    /// did not match its column. `reason` is diagnostic, not stable.
    | SchemaMismatch of reason: string
    /// The page query was malformed (negative offset / non-positive limit).
    | InvalidPage of reason: string
    /// `Delete` was refused because the dataset's versioning policy forbids
    /// it (immutable vintages — GP 5).
    | DeleteForbidden
    /// The stored content is in a format the composed codec cannot read.
    | UnreadableFormat of format: string
    /// Underlying storage / codec failure. `reason` is diagnostic only.
    | StorageFailure of reason: string

module DatasetError =
    let describe =
        function
        | DatasetError.NotFound -> "dataset not found"
        | DatasetError.VersionNotFound v -> $"dataset version {v} not found"
        | DatasetError.SchemaMismatch r -> $"dataset schema mismatch: {r}"
        | DatasetError.InvalidPage r -> $"invalid dataset page query: {r}"
        | DatasetError.DeleteForbidden -> "dataset delete forbidden by versioning policy"
        | DatasetError.UnreadableFormat f -> $"dataset content format '{f}' is not readable by the composed codec"
        | DatasetError.StorageFailure r -> $"dataset storage failure: {r}"