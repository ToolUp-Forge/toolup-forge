module ToolUp.Platform.Tests.Contracts.IDataSourceContract

open System
open System.Globalization
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra

// ─── IDataSource contract pack ────────────────────────────────────
//
// Parametrised tests for any `IDataSource` implementation. The
// factory takes a seed callback so tests can pre-populate the
// connector with `(sourceId, table) → bytes` content before
// exercising the contract methods.
//
// Coverage targets the interface contract — Kind discriminator,
// Connect/ListTables/GetSchema/Query round-trips, error shapes for
// missing content, the declared capability record, and the binding laws
// for a connector that declares `Binds`. Connector-specific behaviour (a
// dialect, connection-string parsing) is tested in each connector's own
// integration tests.

type Seeder = DataSourceId -> string -> byte[] -> unit

// ─── The capability record ─────────────────────────────────────────
//
// A connector's abilities beyond the five members are its declared
// `Capabilities` record, never its runtime type. The laws: a declared
// capability is honoured (the reader hands back exactly the declared
// instance); an undeclared one asked for is refused by name (the
// connector's `Kind` and the capability); the record is a constant of the
// connector; and a decorator forwarding the record declares exactly its
// inner connector's capabilities, whatever the decorator's own type is.

/// A connector forwarding every member to `inner`, declaring `capabilities`.
let private declaring (capabilities: ConnectorCapabilities) (inner: IDataSource) =
    { new IDataSource with
        member _.Kind = inner.Kind
        member _.Capabilities = capabilities
        member _.Connect ctx = inner.Connect ctx
        member _.ListTables ctx = inner.ListTables ctx
        member _.GetSchema(ctx, t) = inner.GetSchema(ctx, t)
        member _.Query(ctx, sql) = inner.Query(ctx, sql)
    }

/// A connector forwarding every member, its record included, to `inner`.
let private forwarding (inner: IDataSource) = declaring inner.Capabilities inner

let private same (declared: 'T option) (read: Result<'T, CapabilityNotDeclared>) =
    match declared, read with
    | Some d, Ok r -> obj.ReferenceEquals(d, r)
    | None, Error _ -> true
    | _ -> false

/// The capability laws for the connector `factory` builds.
let capabilityLaws (name: string) (factory: unit -> IDataSource) =
    testList $"{name} — IDataSource capabilities" [

        test "a declared capability is honoured; an undeclared one asked for is refused by name" {
            let source = factory ()

            for capability in ConnectorCapability.all do
                match ConnectorCapabilities.require capability source with
                | Ok() ->
                    Expect.isTrue
                        (ConnectorCapabilities.declares capability source.Capabilities)
                        $"{ConnectorCapability.name capability} is granted only when declared"
                | Error refusal ->
                    Expect.equal
                        refusal
                        {
                            Kind = source.Kind
                            Capability = capability
                        }
                        "the refusal names the connector and the capability"

                    let sentence = CapabilityNotDeclared.describe refusal
                    Expect.stringContains sentence source.Kind "the sentence names the connector"
                    Expect.stringContains sentence (ConnectorCapability.name capability) "and the capability"

            Expect.isTrue
                (same source.Capabilities.Watchable (ConnectorCapabilities.watchable source))
                "the watch read is the declared watch"

            Expect.isTrue
                (same source.Capabilities.Native (ConnectorCapabilities.native source))
                "the native read is the declared emission"

            Expect.isTrue
                (same source.Capabilities.Binds (ConnectorCapabilities.binds source))
                "the binding read is the declared binding"

            Expect.isTrue
                (same source.Capabilities.NativeBound (ConnectorCapabilities.nativeBound source))
                "the native bound read is the declared emission"
        }

        test "a connector emitting its native format for a bound statement also binds" {
            let capabilities = (factory ()).Capabilities

            if capabilities.NativeBound.IsSome then
                Expect.isSome
                    capabilities.Binds
                    "NativeBound is declared beside Binds, whose kinds it is checked against"
        }

        test "the record is a constant of the connector" {
            let source = factory ()

            Expect.equal
                (ConnectorCapabilities.declared source.Capabilities)
                (ConnectorCapabilities.declared source.Capabilities)
                "two reads declare the same capabilities"
        }

        test "a decorator forwarding the record declares exactly its inner connector's capabilities" {
            let source = factory ()
            let decorated = forwarding source

            for capability in ConnectorCapability.all do
                Expect.equal
                    (ConnectorCapabilities.require capability decorated |> Result.isOk)
                    (ConnectorCapabilities.require capability source |> Result.isOk)
                    $"{ConnectorCapability.name capability} reads through the decorator"
        }
    ]

/// The capability laws, plus the pin that the connector declares exactly
/// `expected` — what a shipped connector's probes answered before the record.
let capabilities (name: string) (expected: ConnectorCapability list) (factory: unit -> IDataSource) =
    testList name [
        test "declares exactly what it implements" {
            Expect.equal (ConnectorCapabilities.declared (factory ()).Capabilities) expected "the declared capabilities"
        }
        capabilityLaws name factory
    ]

let private mkConfig sourceId : DataSourceConfig = {
    Id = sourceId
    Name = $"Test source {sourceId}"
    Kind = "InMemory"
    ConnectionScope = Map.empty
    CredentialKey = "test-credential"
    Tables = None
    Tags = Map.empty
}

let private mkContext sourceId : DataSourceCallContext = {
    ScopeId = "test-scope"
    Config = mkConfig sourceId
    Credential = None
}

// ─── The binding laws ─────────────────────────────────────────────
//
// A connector that declares `Binds` takes a statement's values beside its
// text. The laws, for any connector that declares it: every kind it binds
// round-trips through a select of the parameter; a value holding a quote, a
// comment marker, a statement separator or a NUL comes back as data and
// changes nothing in the source; a parameter named twice is refused, and so
// is one the text does not use and a marker no parameter supplies; an absent
// value binds as a typed null; a kind the connector does not bind is refused
// before it runs; and a connector that does not declare the capability is
// refused by name, with nothing falling back to rendering.

/// How the binding laws speak a connector's dialect and read its result.
type BindingHarness = {
    /// The statement selecting the named parameters, each once, as one row
    /// in the order given.
    Select: string list -> string
    /// The statement reading the seeded table the value of the named
    /// parameter names; `None` for a connector that cannot bind a table name.
    Lookup: (string -> string) option
    /// The cells of the one row a select returned, `None` for an absent cell.
    Row: byte[] -> Result<string option list, string>
    /// The value a returned cell reads as, for the kind it was bound as.
    Read: DatasetDType -> string -> BoundValue option
}

/// Harnesses for the binding laws.
[<RequireQualifiedAccess>]
module BindingHarness =

    // RFC 4180 records, each field with whether it was quoted, so the
    // family's null convention can tell `,,` from `,"",`.
    let private records (text: string) : (string * bool) list list =
        let rows = ResizeArray<(string * bool) list>()
        let fields = ResizeArray<string * bool>()
        let field = StringBuilder()
        let mutable quoted = false
        let mutable inQuotes = false
        let mutable i = 0

        let commit () =
            fields.Add(field.ToString(), quoted)
            field.Clear() |> ignore
            quoted <- false

        let endRow () =
            commit ()
            rows.Add(List.ofSeq fields)
            fields.Clear()

        while i < text.Length do
            let c = text[i]

            if inQuotes then
                if c = '"' && i + 1 < text.Length && text[i + 1] = '"' then
                    field.Append c |> ignore
                    i <- i + 1
                elif c = '"' then
                    inQuotes <- false
                else
                    field.Append c |> ignore
            elif c = '"' then
                inQuotes <- true
                quoted <- true
            elif c = ',' then
                commit ()
            elif c = '\r' && i + 1 < text.Length && text[i + 1] = '\n' then
                endRow ()
                i <- i + 1
            elif c = '\n' then
                endRow ()
            else
                field.Append c |> ignore

            i <- i + 1

        if field.Length > 0 || fields.Count > 0 then
            endRow ()

        List.ofSeq rows

    /// The family's CSV under its null convention (an unquoted empty field is
    /// absent): a header, then the one row.
    let csvRow (bytes: byte[]) : Result<string option list, string> =
        match records (Encoding.UTF8.GetString bytes) with
        | [ _header; row ] ->
            Ok(
                row
                |> List.map (fun (text, quoted) -> if text.Length = 0 && not quoted then None else Some text)
            )
        | other -> Error $"expected a header and one row, read {other.Length} records"

    /// A cell read as invariant-culture text for `kind`.
    let invariantRead (kind: DatasetDType) (cell: string) : BoundValue option =
        let parsed (ok, value) = if ok then Some value else None

        if kind = DatasetDType.Float then
            Double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture)
            |> parsed
            |> Option.map BoundValue.Float
        elif kind = DatasetDType.Int then
            Int64.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture)
            |> parsed
            |> Option.map BoundValue.Int
        elif kind = DatasetDType.Bool then
            match cell with
            | "true" -> Some(BoundValue.Bool true)
            | "false" -> Some(BoundValue.Bool false)
            | _ -> None
        elif kind = DatasetDType.Timestamp then
            DateTimeOffset.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.None)
            |> parsed
            |> Option.map BoundValue.Timestamp
        elif kind = DatasetDType.Text then
            Some(BoundValue.Text cell)
        elif kind = DatasetDType.Categorical then
            Some(BoundValue.Categorical cell)
        else
            None

    /// The in-memory source's dialect, the reference binder.
    let reference: BindingHarness = {
        Select = InMemoryDataSource.InMemoryBinding.select
        Lookup = Some InMemoryDataSource.InMemoryBinding.table
        Row = csvRow
        Read = invariantRead
    }

let private samples: BoundValue list = [
    BoundValue.Float 0.1
    BoundValue.Float -2.5e10
    BoundValue.Int Int64.MaxValue
    BoundValue.Int -7L
    BoundValue.Bool true
    BoundValue.Bool false
    BoundValue.Text "plain"
    BoundValue.Timestamp(DateTimeOffset(2026, 11, 11, 9, 30, 15, 123, TimeSpan.FromHours 1.0))
    BoundValue.Categorical "level-a"
]

// Built from char values, so the source file holds no control byte.
let private nul = string (char 0)

let private hostile: string list = [
    "it's"
    "x'); --"
    "a -- b"
    "a /* b */ c"
    "a; b"
    $"a{nul}b"
    "\"quoted\", and, commas"
]

/// The binding laws for the connector `factory` builds, in the dialect
/// `harness` speaks.
let bindingLaws (name: string) (harness: BindingHarness) (factory: unit -> IDataSource * Seeder) =
    let ctx = mkContext "bind-src"

    let bindable (source: IDataSource) =
        match source.Capabilities.Binds with
        | Some binds -> binds.BindableKinds
        | None -> Set.empty

    let select (source: IDataSource) (parameters: (string * BoundValue) list) (names: string list) =
        let statement =
            parameters
            |> List.fold (fun s (n, v) -> BoundStatement.bind n v s) (BoundStatement.ofText (harness.Select names))

        BoundQuery.run source ctx statement

    let row (result: Result<byte[], BoundStatementError>) =
        match result with
        | Ok bytes ->
            match harness.Row bytes with
            | Ok cells -> cells
            | Error reason -> failtestf "the result row did not read: %s" reason
        | Error error -> failtestf "the bound statement did not run: %s" (BoundStatementError.describe error)

    let refused (expected: BindingRefusal) (result: Result<byte[], BoundStatementError>) (message: string) =
        match result with
        | Error(BoundStatementError.Refused refusal) -> Expect.equal refusal expected message
        | Error other -> failtestf "%s: expected %A, got %s" message expected (BoundStatementError.describe other)
        | Ok _ -> failtestf "%s: expected %A, the statement ran" message expected

    testList $"{name} — bound statements" [

        testCaseAsync "every kind the connector binds round-trips through a select of the parameter"
        <| async {
            let source, _ = factory ()
            let kinds = bindable source
            Expect.isNonEmpty kinds "a connector that declares Binds binds at least one kind"

            for sample in samples |> List.filter (fun v -> kinds.Contains(BoundValue.kind v)) do
                let! result = select source [ "p", sample ] [ "p" ]

                match row result with
                | [ Some cell ] ->
                    Expect.equal
                        (harness.Read (BoundValue.kind sample) cell)
                        (Some sample)
                        $"{sample} comes back as itself"
                | cells -> failtestf "%A: expected one present cell, read %A" sample cells
        }

        testCaseAsync
            "a value holding a quote, a comment marker, a separator or a NUL comes back as data and changes nothing"
        <| async {
            let source, seed = factory ()

            Expect.isTrue
                ((bindable source).Contains DatasetDType.Text)
                "the law binds text, so the connector must bind it"

            let kept = Encoding.UTF8.GetBytes "k\r\n1\r\n"
            seed ctx.Config.Id "kept" kept
            let! before = source.ListTables ctx

            for value in hostile do
                let! result = select source [ "p", BoundValue.Text value ] [ "p" ]
                Expect.equal (row result) [ Some value ] $"{value} comes back as data"

            match harness.Lookup with
            | None -> ()
            | Some lookup ->
                let named table =
                    BoundStatement.ofText (lookup "t")
                    |> BoundStatement.bind "t" (BoundValue.Text table)

                let! found = BoundQuery.run source ctx (named "kept")
                Expect.equal (found |> Result.toOption) (Some kept) "a table named by a parameter is looked up"

                for value in hostile do
                    let! result = BoundQuery.run source ctx (named value)
                    Expect.isError result $"a table named {value} is not found"

            let! after = source.ListTables ctx
            Expect.equal after before "the source's tables are what they were"
            let! read = source.Query(ctx, "kept")
            Expect.equal (read |> Result.toOption) (Some kept) "the seeded table is what it was"
        }

        testCaseAsync "a parameter named twice is refused"
        <| async {
            let source, _ = factory ()
            let! result = select source [ "p", BoundValue.Int 1L; "p", BoundValue.Int 2L ] [ "p" ]
            refused (BindingRefusal.DuplicateName "p") result "the duplicate is named"
        }

        testCaseAsync "a parameter the text does not use is refused"
        <| async {
            let source, _ = factory ()
            let! result = select source [ "a", BoundValue.Int 1L; "b", BoundValue.Int 2L ] [ "a" ]
            refused (BindingRefusal.UnusedParameter "b") result "the unused parameter is named"
        }

        testCaseAsync "a marker no parameter supplies is refused"
        <| async {
            let source, _ = factory ()
            let! result = select source [ "a", BoundValue.Int 1L ] [ "a"; "b" ]
            refused (BindingRefusal.MissingParameter "b") result "the unsupplied marker is named"
        }

        testCaseAsync "an absent value binds as a typed null"
        <| async {
            let source, _ = factory ()

            for kind in BoundValue.kinds |> List.filter (bindable source).Contains do
                let! result = select source [ "p", BoundValue.Null kind ] [ "p" ]
                Expect.equal (row result) [ None ] $"a null {kind} comes back absent"
        }

        testCaseAsync "a kind the connector does not bind is refused before the connector runs"
        <| async {
            let source, _ = factory ()

            for kind in BoundValue.kinds |> List.filter (fun k -> not ((bindable source).Contains k)) do
                let! result = select source [ "p", BoundValue.Null kind ] [ "p" ]

                refused
                    (BindingRefusal.UnbindableKind("p", kind))
                    result
                    $"{kind} is refused by the connector's declaration"

            let mutable calls = 0

            let narrowed =
                { new IBindsStatements with
                    member _.BindableKinds = Set.empty

                    member _.QueryBound(c, s) =
                        calls <- calls + 1
                        source.Capabilities.Binds.Value.QueryBound(c, s)
                }

            let decorated =
                declaring
                    {
                        source.Capabilities with
                            Binds = Some narrowed
                    }
                    source

            let! result = select decorated [ "p", BoundValue.Int 1L ] [ "p" ]
            refused (BindingRefusal.UnbindableKind("p", DatasetDType.Int)) result "the undeclared kind is named"
            Expect.equal calls 0 "the connector never saw the statement"
        }

        testCaseAsync "a connector that does not declare the capability is refused by name"
        <| async {
            let source, _ = factory ()
            let undeclared = declaring ConnectorCapabilities.none source

            let statement =
                BoundStatement.ofText (harness.Select [ "p" ])
                |> BoundStatement.bind "p" (BoundValue.Int 1L)

            let expected =
                BoundStatementError.NotDeclared {
                    Kind = source.Kind
                    Capability = ConnectorCapability.Binds
                }

            let! result = BoundQuery.run undeclared ctx statement
            Expect.equal result (Error expected) "the refusal names the connector and the capability"
            let! native = BoundQuery.runNative undeclared ctx statement
            Expect.equal native (Error expected) "the native path refuses it the same way"
            Expect.stringContains (BoundStatementError.describe expected) source.Kind "the sentence names the connector"
        }
    ]

/// The contract pack for the connector `factory` builds, with the binding
/// laws in the dialect `harness` speaks when the connector declares `Binds`.
let testsWith (name: string) (harness: BindingHarness) (factory: unit -> IDataSource * Seeder) =

    testList $"{name} — IDataSource contract" [

        capabilityLaws name (factory >> fst)

        if ConnectorCapabilities.declares ConnectorCapability.Binds (fst (factory ())).Capabilities then
            bindingLaws name harness factory

        test "Kind is non-empty" {
            let source, _ = factory ()
            Expect.isNonEmpty source.Kind "Kind discriminator must not be empty"
        }

        testCaseAsync "Connect succeeds for valid context"
        <| async {
            let source, _ = factory ()
            let ctx = mkContext "src-1"

            match! source.Connect ctx with
            | Ok() -> ()
            | Error err -> failtestf "Connect failed: %A" err
        }

        testCaseAsync "ListTables returns the seeded table names"
        <| async {
            let source, seed = factory ()
            let sourceId = "src-list"
            seed sourceId "table-a" (Encoding.UTF8.GetBytes "a-content")
            seed sourceId "table-b" (Encoding.UTF8.GetBytes "b-content")

            let ctx = mkContext sourceId

            match! source.ListTables ctx with
            | Ok tables ->
                let asSet = Set.ofList tables
                Expect.isTrue (asSet.Contains "table-a") "table-a present"
                Expect.isTrue (asSet.Contains "table-b") "table-b present"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "ListTables is scoped to the requested source id"
        <| async {
            let source, seed = factory ()
            seed "src-A" "table-only-on-A" (Encoding.UTF8.GetBytes "x")
            seed "src-B" "table-only-on-B" (Encoding.UTF8.GetBytes "y")

            let ctxA = mkContext "src-A"

            match! source.ListTables ctxA with
            | Ok tables ->
                Expect.contains tables "table-only-on-A" "src-A's table is listed"
                Expect.isFalse (List.contains "table-only-on-B" tables) "src-B's table is NOT listed under src-A"
            | Error err -> failtestf "ListTables failed: %A" err
        }

        testCaseAsync "GetSchema returns published schema or empty columns"
        <| async {
            let source, seed = factory ()
            seed "src-schema" "tbl" (Encoding.UTF8.GetBytes "x")
            let ctx = mkContext "src-schema"

            match! source.GetSchema(ctx, "tbl") with
            | Ok schema -> Expect.equal schema.TableName "tbl" "schema.TableName matches request"
            // The InMemoryDataSource's auto-Seed-overload uses an
            // empty column list; impls that publish columns will
            // assert non-empty in their own integration tests.
            | Error err -> failtestf "GetSchema failed: %A" err
        }

        testCaseAsync "Query returns the seeded bytes for a known table"
        <| async {
            let source, seed = factory ()
            let payload = Encoding.UTF8.GetBytes "{\"v\":42}"
            seed "src-query" "rollup" payload
            let ctx = mkContext "src-query"

            match! source.Query(ctx, "rollup") with
            | Ok bytes -> Expect.equal (Encoding.UTF8.GetString bytes) "{\"v\":42}" "bytes round-trip"
            | Error err -> failtestf "Query failed: %A" err
        }

        testCaseAsync "Query for unknown table returns Error"
        <| async {
            let source, _ = factory ()
            let ctx = mkContext "src-empty"

            match! source.Query(ctx, "no-such-table") with
            | Ok _ -> failtest "Expected Error for unknown table; got Ok"
            | Error _ -> ()
        }
    ]

/// The contract pack, with the binding laws in the reference dialect
/// (`BindingHarness.reference`) when the connector declares `Binds`. A
/// connector with a dialect of its own runs `testsWith`.
let tests (name: string) (factory: unit -> IDataSource * Seeder) =
    testsWith name BindingHarness.reference factory