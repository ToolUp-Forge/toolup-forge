// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SyntheticSampleGenerator

open System
open System.Text
open ToolUp.Platform
open DataManagementTypes

// ─── Deterministic, schema-driven synthetic-row generator ───────────
//
// Phase 30d substrate. Generates `count` synthetic rows from a
// `DataTypeSchema`, seeded by an `int` so the same `(typeId, count,
// seed)` produces byte-identical CSV bytes. Powers
// `IDataCatalog.GetSyntheticSample` and, indirectly, the
// `ModulePermission.SchemaOnly` partner-sandbox role — a partner who
// holds only `SchemaOnly` can discover what data exists (`GetSchema`)
// and iterate against synthetic samples (`GetSyntheticSample`) without
// ever seeing real customer rows.
//
// Determinism contract: the generator uses `System.Random(seed)` and
// emits values in column order, then row order. No wall-clock reads,
// no `Guid.NewGuid()`, no Fable-time non-determinism — the same seed
// + the same schema produce the same bytes on every machine, on every
// runtime, forever. The choice of `System.Random` is deliberate:
// `System.Security.Cryptography.RandomNumberGenerator` would give
// better randomness but at the cost of cross-platform determinism
// (the algorithm is implementation-defined), and synthetic-sample
// determinism is the load-bearing property here, not unpredictability.
//
// Sentinel container identity: synthetic samples never sit in a real
// scope. The returned `DataObject` carries `ScopeId = "_synthetic"`
// and `ObjectId = $"_synthetic/{typeId}/{seed}-{count}"` so a sample
// can be cached, audited, and forensically traced without colliding
// with any real `IDataObjectStore.Save` write.

/// Reserved synthetic-scope id. Sample `DataObject` records carry
/// this verbatim — `IDataObjectStore.Save` writes never produce a
/// `DataObject` under this scope, so the value is a safe sentinel
/// for "this metadata describes a synthesised, not real, sample".
[<Literal>]
let SyntheticScopeId = "_synthetic"

/// Reserved `CreatedBy` for synthetic samples — distinguishes
/// synthesised metadata from any real user / `"system"` write.
[<Literal>]
let SyntheticCreatedBy = "_synthetic"

/// Default sample-row cap when a deployment's
/// `_platform.notification_prefs.schemaOnly.maxSampleRows` is unset.
/// Mirrors the SDK's safe-quiet default: generous enough for partner
/// iteration, small enough that a malicious `count` parameter can't
/// burn server CPU or memory.
[<Literal>]
let DefaultMaxSampleRows = 100

/// Wire-format identifier the sample-cap field uses inside the
/// `_platform.notification_prefs` schema. Centralised so the schema
/// declaration and the catalog gating site read the same key.
[<Literal>]
let SchemaOnlyMaxSampleRowsKey = "schemaOnly.maxSampleRows"

// ─── CSV emission ───────────────────────────────────────────────────

let private escapeCell (cell: string) =
    if
        cell.Contains ','
        || cell.Contains '"'
        || cell.Contains '\n'
        || cell.Contains '\r'
    then
        "\"" + cell.Replace("\"", "\"\"") + "\""
    else
        cell

let private appendHeader (sb: StringBuilder) (columns: DataTypeColumn list) =
    let names = columns |> List.map (fun c -> escapeCell c.Name)
    sb.Append(String.concat "," names).Append('\n') |> ignore

let private cellForColumn (rnd: Random) (rowIndex: int) (col: DataTypeColumn) =
    // Each column's generator pulls from the seeded PRNG in column
    // order. The deterministic-order pull is what gives byte-identical
    // output for the same seed: changing column order changes the
    // sample, which is the documented behaviour.
    match col.Type with
    | StringColumn ->
        // Three-letter token: rotated by column name length so two
        // string columns in the same row don't collide on the same
        // PRNG draw. `rowIndex` keeps the row-major sweep stable
        // even when columns are reordered (the values move with the
        // header, not with the position).
        let suffix = rnd.Next(0, 1000)
        sprintf "%s-%d-%d" (col.Name.ToLowerInvariant()) rowIndex suffix
    | NumberColumn ->
        // Range chosen to fit a single Int32 and avoid scientific-
        // notation rendering surprises for downstream CSV parsers.
        let n = rnd.Next(0, 1_000_000)
        string n
    | DateColumn ->
        // Anchored on a fixed epoch (the SDK's own Phase 30d ship
        // date is too brittle — any rebuild would shift it). 2000-
        // 01-01 is the deterministic anchor; the per-row offset is
        // 0..3650 days giving a ~10-year synthetic spread.
        let offsetDays = rnd.Next(0, 3650)
        let anchor = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        anchor.AddDays(float offsetDays).ToString("yyyy-MM-dd")
    | BooleanColumn -> if rnd.Next(0, 2) = 0 then "false" else "true"

let private appendRow (sb: StringBuilder) (rnd: Random) (rowIndex: int) (columns: DataTypeColumn list) =
    let cells =
        columns |> List.map (fun col -> col |> cellForColumn rnd rowIndex |> escapeCell)

    sb.Append(String.concat "," cells).Append('\n') |> ignore

/// Generate `count` synthetic rows for `schema`, seeded by `seed`.
/// Returns CSV bytes (UTF-8, LF line endings). Header row is always
/// present even when `count = 0`. `count` is clamped to `[0,
/// maxRows]` — values outside the range are coerced (not refused) so
/// the caller never sees an exception; gating on `maxRows` is the
/// catalog's job, not the generator's.
let generateCsvBytes (schema: DataTypeSchema) (count: int) (seed: int) (maxRows: int) : byte[] =
    let sb = StringBuilder()
    appendHeader sb schema.Columns

    let clamped = max 0 (min count maxRows)

    if clamped > 0 then
        let rnd = Random(seed)

        for rowIndex in 0 .. clamped - 1 do
            appendRow sb rnd rowIndex schema.Columns

    Encoding.UTF8.GetBytes(sb.ToString())

/// Construct the sentinel `DataObject` metadata for a synthetic
/// sample. `ContentHash` is SHA-256 of the bytes — reproducible for
/// the same `(typeId, count, seed)` input, so the metadata's
/// `ContentHash` is itself part of the determinism contract.
let buildSyntheticDataObject (typeId: DataTypeId) (count: int) (seed: int) (bytes: byte[]) : DataObject =
    use sha = System.Security.Cryptography.SHA256.Create()
    let hash = sha.ComputeHash(bytes)
    let hashHex = Convert.ToHexString(hash).ToLowerInvariant()

    {
        ObjectId = sprintf "%s/%s/%d-%d" SyntheticScopeId typeId seed count
        Version = 1
        // Anchored UTC timestamp — synthetic samples carry a stable
        // pseudo-time so the `DataObject` itself remains determi-
        // nistic for the same inputs.
        CreatedAt = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        CreatedBy = SyntheticCreatedBy
        ScopeId = SyntheticScopeId
        DataType = typeId
        ContentHash = hashHex
        Policy = Unversioned
        Metadata =
            Map.ofList [
                "synthetic.seed", string seed
                "synthetic.count", string count
                "synthetic.generated_by", "ToolUp.Platform.SyntheticSampleGenerator"
            ]
    }

/// One-shot generator for `IDataCatalog.GetSyntheticSample`. Returns
/// (metadata, bytes). When `schema` is `None` (the type publishes no
/// schema — Phase 7a `Schema = None` case), returns a header-only
/// CSV with a single sentinel column so the caller still receives
/// well-formed bytes. The "unknown type" case (no registration at
/// all) is handled by the catalog itself — this function presumes
/// the type id resolved.
let generate
    (typeId: DataTypeId)
    (schema: DataTypeSchema option)
    (count: int)
    (seed: int)
    (maxRows: int)
    : DataObject * byte[] =
    let resolved =
        match schema with
        | Some s when not (List.isEmpty s.Columns) -> s
        | _ -> {
            Description = "No schema published — synthetic sample contains header only."
            Columns = [
                {
                    Name = "_synthetic_empty"
                    Type = StringColumn
                    Required = false
                    Description = Some "Placeholder column for types that publish no DataTypeSchema."
                }
            ]
          }

    let bytes = generateCsvBytes resolved count seed maxRows
    let obj = buildSyntheticDataObject typeId count seed bytes
    obj, bytes