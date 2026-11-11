module ToolUp.Platform.Infra.InMemoryDataSource

open System
open System.Collections.Concurrent
open System.Globalization
open System.Text
open ToolUp.Platform

// ─── InMemoryDataSource ──────────────────────────────────────────
//
// Test / dev `IDataSource` implementation that holds preconfigured
// `(table → bytes)` content in memory. Used by:
//   - The `IDataSourceContract` test pack (verifies the SDK
//     interface contract without any external deps).
//   - The `TestHarness` for module-development workflows.
//   - Deployments that want a stub-source for staging or for
//     demoing the data-ingestion UI without real cloud integration.
//
// **`Kind` discriminator: `"InMemory"`.** Matches `DataSourceConfig.Kind`
// for routing through `IDataIngestor`.
//
// The content map is `(sourceId, table) → bytes` so one in-memory
// source can serve multiple configured `DataSourceConfig` records
// with disjoint table namespaces. Test factories typically construct
// a single source per test and seed it with one table.
//
// Connect / ListTables / GetSchema / Query never touch `ISecretStore`
// — there are no credentials to resolve. Tests that exercise the
// credential-missing failure mode must use a connector that does.
//
// **The reference binder.** The source binds every kind a bound value
// carries, and its bound dialect has two forms, each a keyword followed by
// markers and nothing else:
//
//   select @a, @b   one CSV row, a column per marker, holding the bound values
//   table @t        the bytes seeded for the table the value of `t` names
//
// A value is substituted into the source's own lookup or its result row,
// never into text: the statement text is read for its markers and is never
// rebuilt. The result is CSV under the family's null convention (an
// unquoted empty field is an absent value, `""` the empty string). A
// statement costs nothing here, so `Timeout` is not consulted.

/// The bound statement forms the in-memory source reads.
[<RequireQualifiedAccess>]
module InMemoryBinding =

    /// The `select` statement over `names`, each as its marker.
    let select (names: string list) : string =
        "select " + String.Join(", ", names |> List.map BoundStatement.marker)

    /// The `table` statement over the parameter `name`.
    let table (name: string) : string = "table " + BoundStatement.marker name

    /// A bound value as the source writes it into a result cell: invariant
    /// culture, round-trippable, `null` for an absent value.
    let internal render (value: BoundValue) : string =
        match value with
        | BoundValue.Float f -> f.ToString("R", CultureInfo.InvariantCulture)
        | BoundValue.Int i -> i.ToString(CultureInfo.InvariantCulture)
        | BoundValue.Bool b -> if b then "true" else "false"
        | BoundValue.Text s
        | BoundValue.Categorical s -> s
        | BoundValue.Timestamp t -> t.ToString("O", CultureInfo.InvariantCulture)
        | BoundValue.Null _ -> null

    // The family's CSV field rule: an absent value is the unquoted empty
    // field, the empty string is `""`, and a field holding a separator, a
    // quote or a line break is quoted with its quotes doubled. The family's
    // writer lives in a companion this assembly cannot reference.
    let private field (text: string) : string =
        if isNull text then
            ""
        elif text.Length = 0 then
            "\"\""
        elif text.IndexOfAny [| ','; '"'; '\n'; '\r' |] >= 0 then
            "\"" + text.Replace("\"", "\"\"") + "\""
        else
            text

    let private row (cells: string list) =
        String.Join(",", cells |> List.map field) + "\r\n"

    /// The one CSV row `select` returns: a header of `names`, then each
    /// name's bound value, in marker order.
    let internal selectRow (names: string list) (values: Map<string, BoundValue>) : byte[] =
        let header = row names
        let body = row (names |> List.map (fun n -> render values[n]))
        Encoding.UTF8.GetBytes(header + body)

    /// A statement form the source reads.
    type internal Form =
        /// `select @a, @b`.
        | Select of names: string list
        /// `table @t`.
        | Table of name: string

    let private markerName (term: string) =
        if term.StartsWith "@" && BoundStatement.isValidName (term.Substring 1) then
            Some(term.Substring 1)
        else
            None

    /// The form a statement text takes, or `None` when it is neither.
    let internal parse (text: string) : Form option =
        let text = if isNull text then "" else text.Trim()
        let space = text.IndexOfAny [| ' '; '\t'; '\r'; '\n' |]

        if space < 0 then
            None
        else
            let keyword = text.Substring(0, space)

            let terms =
                text.Substring(space + 1).Split(',') |> Array.map _.Trim() |> List.ofArray

            let names = terms |> List.choose markerName

            if names.Length <> terms.Length then
                None
            elif String.Equals(keyword, "select", StringComparison.OrdinalIgnoreCase) then
                Some(Select names)
            elif
                String.Equals(keyword, "table", StringComparison.OrdinalIgnoreCase)
                && names.Length = 1
            then
                Some(Table names.Head)
            else
                None

    /// The first parameter refusal a statement using `names` earns: a marker
    /// no parameter supplies, then a parameter no marker uses.
    let internal unbound (names: string list) (parameters: BoundParameter list) : BindingRefusal option =
        let supplied = parameters |> List.map _.Name |> Set.ofList

        match names |> List.tryFind (supplied.Contains >> not) with
        | Some missing -> Some(BindingRefusal.MissingParameter missing)
        | None ->
            parameters
            |> List.tryFind (fun p -> not (List.contains p.Name names))
            |> Option.map (fun p -> BindingRefusal.UnusedParameter p.Name)

type InMemoryDataSource() =

    let content = ConcurrentDictionary<string * string, byte[] * TableSchema>()

    /// Seed a (sourceId, table) → (bytes, schema) entry. Tests call
    /// this before invoking the SDK's ingestion path.
    member _.Seed(sourceId: DataSourceId, table: string, bytes: byte[], schema: TableSchema) =
        content[(sourceId, table)] <- (bytes, schema)

    /// Convenience overload — schema defaults to empty columns when
    /// the test only cares about the bytes round-trip.
    member this.Seed(sourceId: DataSourceId, table: string, bytes: byte[]) =
        this.Seed(sourceId, table, bytes, { TableName = table; Columns = [] })

    /// Convenience: seed string content as UTF-8 bytes.
    member this.SeedText(sourceId: DataSourceId, table: string, text: string) =
        this.Seed(sourceId, table, Encoding.UTF8.GetBytes text)

    // No credential: the ingestor does not read the secret
    // store for this source.
    interface IDeclaresCredentialNeed with
        member _.CredentialNeed = CredentialNeed.NoCredential

    interface IDataSource with
        member _.Kind = "InMemory"

        member _.Connect(_) = async { return Ok() }

        member _.ListTables(ctx) = async {
            let tables =
                content.Keys
                |> Seq.filter (fun (s, _) -> s = ctx.Config.Id)
                |> Seq.map snd
                |> Seq.distinct
                |> List.ofSeq

            return Ok tables
        }

        member _.GetSchema(ctx, table) = async {
            match content.TryGetValue((ctx.Config.Id, table)) with
            | true, (_, schema) -> return Ok schema
            | false, _ -> return Ok { TableName = table; Columns = [] }
        }

        member _.Query(ctx, sql) = async {
            // Treat `sql` as the table name for the in-memory source —
            // connector docs make this explicit (each connector is
            // free to interpret the dialect; in-memory has no SQL
            // engine). Credentials are ignored — the in-memory
            // source doesn't authenticate.
            match content.TryGetValue((ctx.Config.Id, sql)) with
            | true, (bytes, _) -> return Ok bytes
            | false, _ ->
                return Error(SchemaMismatch $"InMemoryDataSource: no content seeded for ({ctx.Config.Id}, {sql})")
        }

        // The reference record: an in-memory source has no location to
        // watch and no format richer than the bytes it was seeded with, and
        // it binds what it is given.
        member this.Capabilities = {
            ConnectorCapabilities.none with
                Binds = Some(this :> IBindsStatements)
        }

    interface IBindsStatements with
        member _.BindableKinds = Set.ofList BoundValue.kinds

        member _.QueryBound(ctx, statement) = async {
            let failed message =
                Error(BoundStatementError.Failed(SchemaMismatch $"InMemoryDataSource: {message}"))

            let values =
                statement.Parameters |> List.map (fun p -> p.Name, p.Value) |> Map.ofList

            match InMemoryBinding.parse statement.Text with
            | None -> return failed $"'{statement.Text}' is not a statement this source reads"
            | Some form ->
                let names =
                    match form with
                    | InMemoryBinding.Select names -> names
                    | InMemoryBinding.Table name -> [ name ]

                match InMemoryBinding.unbound names statement.Parameters, form with
                | Some refusal, _ -> return Error(BoundStatementError.Refused refusal)
                | None, InMemoryBinding.Select names -> return Ok(InMemoryBinding.selectRow names values)
                | None, InMemoryBinding.Table name ->
                    match values[name] with
                    | BoundValue.Text table
                    | BoundValue.Categorical table ->
                        match content.TryGetValue((ctx.Config.Id, table)) with
                        | true, (bytes, _) -> return Ok bytes
                        | false, _ -> return failed $"no content seeded for ({ctx.Config.Id}, {table})"
                    | other -> return failed $"a table is named by text, and '{name}' is {BoundValue.kind other}"
        }

    // Seeded bytes are returned verbatim and the family's
    // convention is CSV, so this source declares `Csv` explicitly rather
    // than by omission.
    interface IDeclaresPayloadFormat with
        member _.PayloadFormat = PayloadFormat.Csv

let create () = InMemoryDataSource()