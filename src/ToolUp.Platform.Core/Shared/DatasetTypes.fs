// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Dataset substrate types (Fable-safe) ───────────────────
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
/// / `Int` / `Bool` / `Text` / `Timestamp` / `Categorical` / `Decimal` are
/// far too generic to sit unqualified in the widely-opened `ToolUp.Platform`
/// namespace.
[<RequireQualifiedAccess>]
type DatasetDType =
    | Float
    | Int
    | Bool
    | Text
    | Timestamp
    | Categorical
    /// An exact decimal: a database `DECIMAL` / `NUMERIC`, the
    /// Parquet decimal logical type. Its values are `DatasetValue.Decimal`,
    /// canonical decimal text (`DatasetDecimal`), never a float.
    | Decimal

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
        | DatasetDType.Decimal -> "Decimal"

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
        | "Decimal" -> Some DatasetDType.Decimal
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

// ─── The exact decimal is canonical decimal text ─────────────
//
// **Decision (measured 2026-11-11; the migration doc carries the table).**
// The widest exact-decimal column the shipped SQL backends declare is 38
// significant digits (SQL Server / Synapse / Redshift / Athena
// `DECIMAL(38, s)`, Snowflake `NUMBER(38, s)`, BigQuery `NUMERIC(38, 9)`);
// BigQuery's `BIGNUMERIC` reaches 76 digits with scale 38, PostgreSQL's
// unconstrained `NUMERIC` is unbounded, and the Parquet decimal logical type
// over a byte array is unbounded too. `System.Decimal` holds 28 to 29
// significant digits: on .NET a 38-digit integer is REFUSED and a 38-digit
// fraction is silently ROUNDED to 28 places; under Fable the host decimal
// (big.js) rounds at 29 significant digits, so the two hosts round the SAME
// text to DIFFERENT values. A host decimal therefore cannot be the value: it
// holds less than the sources produce, and disagrees with itself across the
// wire. Canonical decimal text holds every digit on both hosts. What would
// overturn this: a host decimal of at least 76 significant digits that .NET
// and Fable implement identically.
//
// **The canonical form** is what `DatasetDecimal.tryParse` returns: an
// optional `-`, the integer digits with no leading zero (a lone `0` when
// there are none), and — only when a non-zero fractional digit exists — a
// `.` and the fractional digits with no trailing zero. No exponent, no `+`,
// no `-0`. So textual equality IS numeric equality, and every codec round
// trip is the identity. A column's declared scale is schema information (the
// connector's raw `ColumnInfo.DataType`, `NUMERIC(10, 2)`), not a property of
// each cell.

/// Canonical exact-decimal text: parsing, ordering, and the
/// bridge to the host decimal where a value fits it. Fable-safe: string
/// work only, except `tryToDecimal`, which is the host decimal by intent.
module DatasetDecimal =

    let private isDigit (c: char) = c >= '0' && c <= '9'

    /// The canonical text for `text`, or `None` when `text` is not a plain
    /// decimal: `[+-]?digits[.digits]` (either side of the point may be
    /// empty, not both). No exponent, separator or whitespace is accepted —
    /// a source that writes those is not handing over exact digits.
    let tryParse (text: string) : string option =
        if isNull text || text.Length = 0 then
            None
        else
            let negative = text[0] = '-'

            let body =
                if text[0] = '-' || text[0] = '+' then
                    text.Substring 1
                else
                    text

            let point = body.IndexOf '.'

            let integerPart, fractionPart =
                if point < 0 then
                    body, ""
                else
                    body.Substring(0, point), body.Substring(point + 1)

            let wellFormed =
                (integerPart.Length + fractionPart.Length) > 0
                && Seq.forall isDigit integerPart
                && Seq.forall isDigit fractionPart

            if not wellFormed then
                None
            else
                let trimmed = integerPart.TrimStart '0'
                let integerDigits = if trimmed.Length = 0 then "0" else trimmed
                let fractionDigits = fractionPart.TrimEnd '0'

                let magnitude =
                    if fractionDigits.Length = 0 then
                        integerDigits
                    else
                        integerDigits + "." + fractionDigits

                if negative && magnitude <> "0" then
                    Some("-" + magnitude)
                else
                    Some magnitude

    /// Is `text` already in canonical form? A `DatasetValue.Decimal` cell
    /// that is not fits no column (`DatasetValue.fits`).
    let isCanonical (text: string) : bool = tryParse text = Some text

    /// Numeric order of two CANONICAL texts (`-1` / `0` / `1`). Exact at any
    /// width: sign, then integer-digit count, then the digits themselves.
    let compare (a: string) (b: string) : int =
        let split (s: string) =
            match s.IndexOf '.' with
            | -1 -> s, ""
            | i -> s.Substring(0, i), s.Substring(i + 1)

        let magnitudeOrder (x: string) (y: string) =
            let xi, xf = split x
            let yi, yf = split y

            if xi.Length <> yi.Length then
                Operators.compare xi.Length yi.Length
            else
                let byInteger = sign (String.CompareOrdinal(xi, yi))

                if byInteger <> 0 then
                    byInteger
                else
                    let width = max xf.Length yf.Length
                    sign (String.CompareOrdinal(xf.PadRight(width, '0'), yf.PadRight(width, '0')))

        match a.StartsWith "-", b.StartsWith "-" with
        | false, true -> 1
        | true, false -> -1
        | false, false -> magnitudeOrder a b
        | true, true -> magnitudeOrder (b.Substring 1) (a.Substring 1)

    /// The canonical text of a host decimal. Exact: every host decimal is a
    /// plain decimal the canonical form holds.
    let ofDecimal (value: decimal) : string =
        match tryParse (string value) with
        | Some canonical -> canonical
        | None -> failwith $"host decimal rendered as non-decimal text: {value}"

    /// The host decimal holding exactly `text`'s value, or `None` when it
    /// cannot: more significant digits than the host decimal holds, or a
    /// magnitude beyond its range. Never rounds — the refusal is the point,
    /// because the host decimal is narrower than a database column.
    let tryToDecimal (text: string) : decimal option =
        match tryParse text with
        | None -> None
        | Some canonical ->
            try
                let value = decimal canonical

                if ofDecimal value = canonical then Some value else None
            with _ ->
                None

/// A single typed cell value. Positionally aligned to a `DatasetSchema`
/// column. `[<RequireQualifiedAccess>]` for the same namespace-hygiene reason
/// as `DatasetDType`. `Int` is `int64` (dataset counts / ids routinely exceed
/// `int32`); `Timestamp` is `DateTimeOffset` (identical precision contract to
/// `TimeSeriesPoint`); `Decimal` is canonical decimal text (
/// `DatasetDecimal`).
[<RequireQualifiedAccess>]
type DatasetValue =
    | Float of float
    | Int of int64
    | Bool of bool
    | Text of string
    | Timestamp of DateTimeOffset
    | Categorical of string
    | Null
    /// An exact decimal, as canonical decimal text
    /// (`DatasetDecimal.tryParse`): every digit the source held, never a
    /// float.
    | Decimal of digits: string

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
        | DatasetValue.Decimal _ -> Some DatasetDType.Decimal
        | DatasetValue.Null -> None

    /// Does `value` fit a column of `dtype` with the given nullability? A
    /// `Decimal` fits only in canonical form, so a store never holds two
    /// spellings of one number.
    let fits (dtype: DatasetDType) (nullable: bool) (value: DatasetValue) : bool =
        match value with
        | DatasetValue.Null -> nullable
        | DatasetValue.Decimal digits -> dtype = DatasetDType.Decimal && DatasetDecimal.isCanonical digits
        | _ -> dtypeOf value = Some dtype

    /// Total-ish comparison for filtering. `Some sign` when the two values
    /// are of the same, ordered case (numeric / decimal / timestamp / text /
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
        | DatasetValue.Decimal x, DatasetValue.Decimal y -> Some(DatasetDecimal.compare x y)
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

/// A privacy-provenance label carried by a dataset version.
/// Labels **travel with the data** so a downstream reader can tell gated
/// data from ordinary data long after it leaves the gate. The DU shape is
/// forge's; the tag vocabulary in `Classified` is the caller's.
/// `[<RequireQualifiedAccess>]` — `CleanRoomDerived` / `Classified` are
/// generic enough to want qualification in the widely-opened namespace.
[<RequireQualifiedAccess>]
type DataProvenanceLabel =
    /// The version is derived from a clean-room-gated computation (Phase
    /// 18b). `gateRef` names the gate decision that seeded it; `epsilonSpent`
    /// is the differential-privacy budget consumed (0.0 when the gate does
    /// not meter ε). Immutable once set — labels are provenance, not opinion.
    | CleanRoomDerived of gateRef: string * epsilonSpent: float
    /// A free classification tag from the caller's own vocabulary (e.g.
    /// `"pii"`, `"restricted"`). Forge round-trips it and enforces policy on
    /// it, but never interprets the tag string (GP 1).
    | Classified of tag: string

module DataProvenanceLabel =
    /// A stable identity string for a label — the value set-union dedupes on,
    /// so two `CleanRoomDerived` labels naming the same gate + ε collapse to
    /// one, and equal `Classified` tags collapse to one.
    let key =
        function
        | DataProvenanceLabel.CleanRoomDerived(gate, eps) -> sprintf "cleanroom:%s:%f" gate eps
        | DataProvenanceLabel.Classified tag -> "classified:" + tag

    /// The union of two label sets, deduped by `key`, preserving the order of
    /// first appearance (left labels first). The propagation primitive: a
    /// derived version inherits the union of its inputs' labels.
    let union (a: DataProvenanceLabel list) (b: DataProvenanceLabel list) : DataProvenanceLabel list =
        (a @ b)
        |> List.fold
            (fun (seen: Set<string>, acc) label ->
                let k = key label

                if Set.contains k seen then
                    seen, acc
                else
                    Set.add k seen, label :: acc)
            (Set.empty, [])
        |> snd
        |> List.rev

/// How faithfully a virtual source reproduces a past read at a
/// pinned watermark. `Exact` — the source has snapshot semantics (a snapshot
/// id / MVCC read) so the same watermark re-reads byte-identical rows;
/// `Approximate` — the watermark is a best-effort high-water mark (a
/// timestamp / query hash) and a re-read may drift if the source mutated
/// history. Recorded honestly so a consumer knows a virtual vintage's
/// reproducibility contract.
[<RequireQualifiedAccess>]
type SnapshotFidelity =
    | Exact
    | Approximate

/// A virtual dataset version's binding: the data stays in the
/// deployment's own store and is read through at page time, so the "vintage"
/// is a **source watermark**, not copied bytes. Immutable *as a definition*
/// (GP 5): re-binding at a new watermark is a new version; the same watermark
/// re-read is reproducible to the source's `Fidelity`.
type VirtualBinding = {
    /// `IDataSource.Kind` of the connector the version reads through.
    SourceRef: string
    /// Connector-interpreted query (the `IDataSource.Query` `sql`). Forge
    /// never parses it.
    QuerySpec: string
    /// The vintage identity — a snapshot id / high-water timestamp / query
    /// hash. Re-binding at a new watermark is a new version.
    Watermark: string
    /// The source's snapshot-reproducibility contract for this watermark.
    Fidelity: SnapshotFidelity
}

/// The materiality of a dataset version. `Materialised` — the
/// default: rows live as encoded content in `IBlobStorage`.
/// `Virtual` — the rows stay in the deployment's own store and are read
/// through a `VirtualBinding` at page time (no durable copy). A deployment
/// that never binds a virtual version is byte-for-byte unchanged (GP 13).
[<RequireQualifiedAccess>]
type DatasetMateriality =
    | Materialised
    | Virtual of VirtualBinding

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
    /// Privacy-provenance labels carried by this version. Empty
    /// for ordinary (unlabelled) data — the default, so a deployment that
    /// never labels is byte-for-byte unchanged (GP 13). Populated by the
    /// store from the reserved label sidecar; propagated automatically by the
    /// producing executors (assembly, scoring).
    Labels: DataProvenanceLabel list
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