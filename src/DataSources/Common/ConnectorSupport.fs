// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Common

open System
open System.Data.Common
open System.Globalization
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Secrets
open DataManagementTypes

// ─── Shared IDataSource connector support ─────────────────────────
//
// The pure, vendor-free half of every `IDataSource` companion under
// `src/DataSources/`. Six connectors need the same four things:
//
//   1. Typed reads out of `DataSourceConfig.ConnectionScope` (a
//      free-form `Map<string,string>`) that fail as `IngestionError`
//      rather than throwing.
//   2. The credential thunk — prefer the ingestor's pre-resolved
//      `DataSourceCallContext.Credential`, fall back to
//      `ISecretStore.GetSecret(ScopeId, Config.CredentialKey)` on
//      EVERY call so a rotated secret takes effect without
//      reconstructing the connector.
//   3. One tabular wire format. Every connector's `Query` returns
//      RFC 4180 CSV with a header row, UTF-8, `\r\n` terminated —
//      documented per connector, uniform across all of them, and
//      what Athena natively produces anyway.
//   4. Native-type-name → coarse `ColumnType` classification, so a
//      consumer can normalise a `TableSchema` without knowing which
//      warehouse produced it.
//
// This package carries NO vendor dependency (GP 1) — everything here
// is BCL, including `System.Data.Common`, so the ADO-shaped
// connectors and the HTTP/API-shaped connectors share it equally.

/// Maps connector exceptions onto the SDK's `IngestionError` taxonomy.
/// Connectors call `Errors.guard` around their vendor-SDK calls so a
/// thrown exception never escapes an `IDataSource` method — the
/// interface contract is `Result`-shaped on every path.
module Errors =

    /// Classify an exception raised while talking to a source.
    /// `context` names the operation ("BigQuery ListTables") and is
    /// prefixed onto the message so an operator reading an admin UI
    /// can tell which call failed.
    ///
    /// Network / socket / timeout / auth-shaped exceptions map to
    /// `SourceUnreachable` (infrastructure — retry may help);
    /// everything else maps to `UnexpectedFailure`. Connectors that
    /// can recognise a schema-shaped failure return `SchemaMismatch`
    /// themselves rather than routing it through here.
    let classify (context: string) (ex: exn) : IngestionError =
        // Vendor / driver exceptions can arrive here wrapped in
        // `AggregateException` (the class the first armed cloud-parity
        // run proved live in the AWS companions, 2026-08-27), which
        // would degrade every type test below to `UnexpectedFailure` —
        // so unwrap first: flatten and take the single inner exception
        // a one-Task await carries; a bare exception passes through
        // unchanged.
        let ex =
            match ex with
            | :? AggregateException as aggregate ->
                match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
                | Some inner -> inner
                | None -> ex
            | _ -> ex

        let message = $"%s{context}: %s{ex.Message}"

        match ex with
        | :? OperationCanceledException -> SourceUnreachable $"%s{message} (cancelled)"
        | :? TimeoutException
        | :? System.Net.Http.HttpRequestException
        | :? System.Net.Sockets.SocketException
        | :? DbException -> SourceUnreachable message
        | :? UnauthorizedAccessException -> SourceUnreachable message
        | _ -> UnexpectedFailure message

    /// Run an async body, converting any thrown exception into the
    /// classified `IngestionError`. The body may itself return
    /// `Error`; that value passes through untouched.
    let guard (context: string) (body: unit -> Async<Result<'T, IngestionError>>) : Async<Result<'T, IngestionError>> = async {
        try
            return! body ()
        with ex ->
            return Error(classify context ex)
    }

/// Typed, failure-explicit reads out of `DataSourceConfig.ConnectionScope`.
///
/// The map is deliberately free-form on the SDK side (connectors
/// interpret their own keys), so every connector needs the same
/// "required key missing" failure. `SchemaMismatch` is the right case:
/// the source was never reached, the *configuration shape* is wrong,
/// and an admin UI renders it as an operator-fixable problem — which
/// is exactly what a missing `project_id` is.
module ConnectionScope =

    /// Read a required key. Whitespace-only values count as absent —
    /// an admin UI that persists an empty text box must not produce a
    /// connector that fails later with a vendor-specific message.
    let require (scope: Map<string, string>) (key: string) : Result<string, IngestionError> =
        match scope.TryFind key with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Ok value
        | Some _
        | None -> Error(SchemaMismatch $"ConnectionScope is missing required key '%s{key}'")

    /// Read an optional key. Whitespace-only reads as absent.
    let optional (scope: Map<string, string>) (key: string) : string option =
        match scope.TryFind key with
        | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
        | Some _
        | None -> None

    /// Read an optional key, substituting `fallback` when absent.
    let optionalOr (scope: Map<string, string>) (key: string) (fallback: string) : string =
        optional scope key |> Option.defaultValue fallback

    /// Read an optional integer key. A present-but-unparseable value
    /// is an error rather than a silent fallback — a mistyped
    /// `port = 54332` must not quietly become the default.
    let optionalInt (scope: Map<string, string>) (key: string) : Result<int option, IngestionError> =
        match optional scope key with
        | None -> Ok None
        | Some raw ->
            match Int32.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value -> Ok(Some value)
            | false, _ -> Error(SchemaMismatch $"ConnectionScope key '%s{key}' is not an integer: '%s{raw}'")

    /// Read an optional boolean key. Accepts `true`/`false`/`1`/`0`
    /// case-insensitively; anything else is an error for the same
    /// reason `optionalInt` refuses to guess.
    let optionalBool (scope: Map<string, string>) (key: string) : Result<bool option, IngestionError> =
        match optional scope key with
        | None -> Ok None
        | Some raw ->
            match raw.Trim().ToLowerInvariant() with
            | "true"
            | "1"
            | "yes" -> Ok(Some true)
            | "false"
            | "0"
            | "no" -> Ok(Some false)
            | other -> Error(SchemaMismatch $"ConnectionScope key '%s{key}' is not a boolean: '%s{other}'")

    /// Read an optional key constrained to a closed set of allowed
    /// values, compared case-insensitively and returned lowercased.
    /// The failure names every accepted value, so an operator does
    /// not have to find the README to correct a typo.
    let optionalEnum
        (scope: Map<string, string>)
        (key: string)
        (allowed: string list)
        : Result<string option, IngestionError> =
        match optional scope key with
        | None -> Ok None
        | Some raw ->
            let normalised = raw.Trim().ToLowerInvariant()

            if allowed |> List.contains normalised then
                Ok(Some normalised)
            else
                let accepted = String.Join(", ", allowed)
                Error(SchemaMismatch $"ConnectionScope key '%s{key}' must be one of [%s{accepted}]; got '%s{raw}'")

/// The credential thunk every connector shares.
///
/// The shipped `DataIngestor` pre-resolves the credential and hands it
/// through `DataSourceCallContext.Credential`, so the common path
/// costs nothing. A connector constructed with an `ISecretStore`
/// still re-reads on every call when the context carries none — that
/// is what makes rotation take effect without reconstructing the
/// connector (the `ClaudeAIProvider` thunk pattern).
module Credentials =

    /// Resolve the credential for this call, or fail with
    /// `CredentialMissing` naming the key the operator must supply.
    let resolve
        (secretStore: ISecretStore option)
        (ctx: DataSourceCallContext)
        : Async<Result<string, IngestionError>> =
        async {
            match ctx.Credential with
            | Some credential when not (String.IsNullOrWhiteSpace credential) -> return Ok credential
            | Some _
            | None ->
                match secretStore with
                | None -> return Error(CredentialMissing ctx.Config.CredentialKey)
                | Some store ->
                    let! stored = store.GetSecret(ctx.ScopeId, ctx.Config.CredentialKey)

                    match stored with
                    | Some value when not (String.IsNullOrWhiteSpace value) -> return Ok value
                    | Some _
                    | None -> return Error(CredentialMissing ctx.Config.CredentialKey)
        }

    /// Resolve the credential if one is available, without failing.
    /// Used by connectors whose auth can legitimately fall back to an
    /// ambient provider chain (the AWS default credential chain, Azure
    /// `DefaultAzureCredential`, GCP application-default credentials)
    /// — there the absence of an explicit credential is a deployment
    /// choice, not an error.
    let resolveOptional (secretStore: ISecretStore option) (ctx: DataSourceCallContext) : Async<string option> = async {
        let! resolved = resolve secretStore ctx

        return
            match resolved with
            | Ok value -> Some value
            | Error _ -> None
    }

/// SQL identifier safety for the connectors that build catalogue
/// queries by hand.
///
/// The six ADO backends and the two warehouse ones do not share one
/// parameter marker (`@p` / `:p` / `{p:String}` / `?`), so a connector
/// whose safety depended on getting several markers right in several
/// places would be a connector waiting to be wrong. Every interpolated
/// identifier is instead validated against a deliberately NARROW
/// pattern first and rejected as `SchemaMismatch` if it does not
/// match, so nothing that could terminate a literal ever reaches a
/// string builder. `quoteLiteral` doubles quotes as a second,
/// independent line of defence.
module SqlIdentifier =

    let private pattern =
        System.Text.RegularExpressions.Regex(
            @"^[A-Za-z_][A-Za-z0-9_$]{0,127}$",
            System.Text.RegularExpressions.RegexOptions.Compiled
        )

    /// Does this string look like a plain, unqualified SQL
    /// identifier? Narrower than what any backend actually permits —
    /// quoted identifiers containing spaces, dots or Unicode are
    /// refused rather than escaped.
    let isSafe (value: string) : bool =
        not (String.IsNullOrWhiteSpace value) && pattern.IsMatch value

    /// Validate an identifier destined for interpolation. `label`
    /// names the thing for the operator ("table", "schema").
    let require (label: string) (value: string) : Result<string, IngestionError> =
        if isSafe value then
            Ok value
        else
            Error(
                SchemaMismatch
                    $"%s{label} '%s{value}' is not a plain SQL identifier (letters, digits, underscore, $; must not start with a digit)"
            )

    /// Double single quotes so a validated identifier can be
    /// interpolated into a catalogue query's string literal.
    let quoteLiteral (value: string) : string =
        (if isNull value then "" else value).Replace("'", "''")

/// Parsing for credentials that arrive as a JSON object rather than a
/// bare string — an AWS access-key triple, a Snowflake key-pair
/// bundle. Kept here (BCL `System.Text.Json` only) so the connectors
/// that need it do not each re-derive the failure message, and so the
/// parse is unit-testable without any cloud SDK.
module CredentialJson =

    /// Parse a JSON object into a case-insensitive `key → string` map.
    /// Nested objects and arrays are rendered back to their raw JSON
    /// text rather than flattened — a connector that wants structure
    /// there parses the value itself. `label` names the credential in
    /// the failure message.
    let parseObject (label: string) (json: string) : Result<Map<string, string>, IngestionError> =
        let malformed (detail: string) =
            Error(SchemaMismatch $"credential '%s{label}' is not a JSON object: %s{detail}")

        if String.IsNullOrWhiteSpace json then
            malformed "value is empty"
        else
            try
                use document = System.Text.Json.JsonDocument.Parse json

                if document.RootElement.ValueKind <> System.Text.Json.JsonValueKind.Object then
                    malformed $"root is %A{document.RootElement.ValueKind}, expected Object"
                else
                    let pairs =
                        document.RootElement.EnumerateObject()
                        |> Seq.map (fun property ->
                            let value =
                                match property.Value.ValueKind with
                                | System.Text.Json.JsonValueKind.String -> property.Value.GetString()
                                | System.Text.Json.JsonValueKind.Null -> ""
                                | System.Text.Json.JsonValueKind.Undefined
                                | System.Text.Json.JsonValueKind.Object
                                | System.Text.Json.JsonValueKind.Array
                                | System.Text.Json.JsonValueKind.Number
                                | System.Text.Json.JsonValueKind.True
                                | System.Text.Json.JsonValueKind.False -> property.Value.GetRawText()

                            property.Name.ToLowerInvariant(), (if isNull value then "" else value))
                        |> List.ofSeq

                    Ok(Map.ofList pairs)
            with ex ->
                malformed ex.Message

    /// Read one key out of a `parseObject` result, accepting several
    /// spellings — cloud vendors are not consistent about
    /// `accessKeyId` vs `aws_access_key_id` vs `AccessKeyId`, and a
    /// connector that accepts only one of them is a support ticket.
    let tryFind (fields: Map<string, string>) (names: string list) : string option =
        names
        |> List.tryPick (fun name ->
            match fields.TryFind(name.ToLowerInvariant()) with
            | Some value when not (String.IsNullOrWhiteSpace value) -> Some value
            | Some _
            | None -> None)

/// RFC 4180 CSV emission — the uniform `IDataSource.Query` wire format
/// across every connector in this family.
///
/// Why CSV and not JSON: the ingestor stores the bytes opaquely and
/// modules parse them, `ToolUp.Tabular` already reads CSV, and Athena
/// emits CSV natively — so one format serves the whole family without
/// a per-connector translation the operator has to know about. Types
/// are recovered from `GetSchema`, not from the payload.
module Csv =

    /// Round-trippable rendering of one cell. Nulls and `DBNull`
    /// become the empty field (indistinguishable from an empty
    /// string in CSV — documented, and why `GetSchema` carries
    /// `Nullable`). Everything else renders invariant-culture so a
    /// connector running on a comma-decimal host does not silently
    /// corrupt every number in the payload.
    let renderValue (value: obj) : string =
        match value with
        | null -> ""
        | :? DBNull -> ""
        | :? string as s -> s
        | :? bool as b -> if b then "true" else "false"
        | :? DateTime as d -> d.ToString("O", CultureInfo.InvariantCulture)
        | :? DateTimeOffset as d -> d.ToString("O", CultureInfo.InvariantCulture)
        | :? TimeSpan as t -> t.ToString("c", CultureInfo.InvariantCulture)
        | :? (byte[]) as bytes -> Convert.ToBase64String bytes
        | :? decimal as d -> d.ToString(CultureInfo.InvariantCulture)
        | :? float as f -> f.ToString("R", CultureInfo.InvariantCulture)
        | :? float32 as f -> f.ToString("R", CultureInfo.InvariantCulture)
        | :? IFormattable as f -> f.ToString(null, CultureInfo.InvariantCulture)
        | other -> string other

    /// Quote a field per RFC 4180 — quoted only when it contains a
    /// comma, a quote, or a line break; embedded quotes doubled.
    let escapeField (field: string) : string =
        let value = if isNull field then "" else field

        if
            value.Contains ','
            || value.Contains '"'
            || value.Contains '\n'
            || value.Contains '\r'
        then
            "\"" + value.Replace("\"", "\"\"") + "\""
        else
            value

    /// Render one record as a CSV line (no terminator).
    let renderRow (fields: string seq) : string =
        String.Join(",", fields |> Seq.map escapeField)

    /// Render a header row plus body rows as UTF-8 CSV bytes.
    /// `\r\n` terminated per RFC 4180, no BOM — a BOM would appear as
    /// a stray character in the first header cell for every naive
    /// downstream parser.
    let toBytes (header: string seq) (rows: string seq seq) : byte[] =
        let sb = StringBuilder()
        sb.Append(renderRow header).Append("\r\n") |> ignore

        for row in rows do
            sb.Append(renderRow row).Append("\r\n") |> ignore

        Encoding.UTF8.GetBytes(sb.ToString())

    /// Drain a `DbDataReader`'s current result set into CSV bytes.
    /// Shared by every ADO-shaped connector in the family (Sql,
    /// Synapse, Snowflake) — `System.Data.Common` is BCL, so this
    /// carries no vendor dependency.
    let ofReader (reader: DbDataReader) : Async<byte[]> = async {
        let! ct = Async.CancellationToken

        let header = [ for i in 0 .. reader.FieldCount - 1 -> reader.GetName i ]
        let sb = StringBuilder()
        sb.Append(renderRow header).Append("\r\n") |> ignore

        let mutable go = true

        while go do
            let! hasRow = reader.ReadAsync ct |> Async.AwaitTask

            if hasRow then
                let cells = [ for i in 0 .. reader.FieldCount - 1 -> renderValue (reader.GetValue i) ]
                sb.Append(renderRow cells).Append("\r\n") |> ignore
            else
                go <- false

        return Encoding.UTF8.GetBytes(sb.ToString())
    }

/// Native-type-name → coarse `ColumnType` classification.
///
/// `ColumnInfo.DataType` deliberately carries the connector's RAW
/// native type name (`NUMERIC(38,9)`, `timestamp with time zone`,
/// `VARIANT`) — that is the most informative thing an admin UI can
/// render, and throwing it away to store one of four coarse tokens
/// would be lossy for no gain. `classify` is the projection down to
/// the SDK's four-case `ColumnType` for consumers that need it; each
/// connector composes its own provider-specific overrides in front of
/// the ANSI table below.
module TypeMap =

    /// Strip a SQL type's parameter list so `DECIMAL(38, 9)` and
    /// `DECIMAL` classify alike. BOTH bracket styles are cut: SQL
    /// spells parameters with `(...)`, Hive-family catalogues (Athena,
    /// Glue) spell generic types with `<...>` — `array<int>`,
    /// `map<string,int>`, `struct<a:int>`. Without the angle-bracket
    /// cut those read as their ELEMENT type, so `array<int>` would
    /// classify as a number.
    let normalise (nativeType: string) : string =
        if String.IsNullOrWhiteSpace nativeType then
            ""
        else
            let trimmed = nativeType.Trim()

            let cutAt (c: char) (value: string) =
                match value.IndexOf c with
                | -1 -> value
                | i -> value.Substring(0, i)

            trimmed |> cutAt '(' |> cutAt '<' |> _.Trim() |> _.ToLowerInvariant()

    /// The ANSI / common-warehouse fallback table. Deliberately
    /// substring-tolerant on the families where every vendor spells
    /// the type differently ("timestamp without time zone",
    /// "TIMESTAMP_NTZ", "datetime2").
    let ansi (nativeType: string) : ColumnType =
        let t = normalise nativeType

        let contains (needle: string) =
            t.Contains(needle, StringComparison.Ordinal)

        if t = "" then
            StringColumn
        elif contains "bool" || t = "bit" then
            BooleanColumn
        elif
            contains "timestamp"
            || contains "datetime"
            || contains "date"
            || contains "time"
        then
            // `time`/`timestamp`/`date`/`datetime`/`interval` all
            // collapse to DateColumn — the coarse DU has no separate
            // time-of-day case, and the raw name is preserved on
            // `ColumnInfo.DataType` for anyone who needs the nuance.
            DateColumn
        elif
            contains "int"
            || contains "numeric"
            || contains "decimal"
            || contains "float"
            || contains "double"
            || contains "real"
            || contains "money"
            || contains "number"
            || contains "serial"
        then
            NumberColumn
        else
            StringColumn

    /// Compose a connector's own overrides in front of the ANSI
    /// table: `classify myOverrides "VARIANT"`. An override returning
    /// `None` defers to `ansi`.
    let classify (overrides: string -> ColumnType option) (nativeType: string) : ColumnType =
        match overrides (normalise nativeType) with
        | Some columnType -> columnType
        | None -> ansi nativeType

    /// Build a `ColumnInfo` from a native column description.
    let column (name: string) (nativeType: string) (nullable: bool) : ColumnInfo = {
        Name = name
        DataType = nativeType
        Nullable = nullable
    }

    /// Assemble a `TableSchema` for `table` from a column sequence.
    let schema (table: string) (columns: ColumnInfo seq) : TableSchema = {
        TableName = table
        Columns = List.ofSeq columns
    }