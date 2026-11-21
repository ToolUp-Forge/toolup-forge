// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SynapseBindingTests

open System
open System.Data
open System.Data.SqlTypes
open System.Text
open Expecto
open Microsoft.Data.SqlClient
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Synapse
open ToolUp.DataSources.Tests.Support.TestFakes
open ToolUp.Platform.Testing

// ─── ToolUp.DataSources.Synapse — bound statements ────────────────
//
// The marker reading and the request the connector builds are pure, so they
// are tested without a workspace: the request is the `SqlCommand` SqlClient
// sends, read before any connection exists. The marker reading is held to
// the SQL connector's T-SQL reading on a shared corpus, so the two cannot
// drift apart. The binding laws of the data source contract pack then run
// against a real Synapse SQL endpoint as an armed leg: Pending without its
// variables, never Failed.

let private statementOf (text: string) (parameters: (string * BoundValue) list) =
    parameters
    |> List.fold (fun s (n, v) -> BoundStatement.bind n v s) (BoundStatement.ofText text)

let private built (statement: BoundStatement) =
    match SynapseBinding.command statement with
    | Ok command -> command
    | Error refusal -> failtestf "expected %s to build; got %s" statement.Text (BoundStatementError.describe refusal)

let private refusedWith (expected: BindingRefusal) (statement: BoundStatement) =
    match SynapseBinding.command statement with
    | Error(BoundStatementError.Refused refusal) -> Expect.equal refusal expected $"{statement.Text} is refused"
    | Error other -> failtestf "expected %A; got %s" expected (BoundStatementError.describe other)
    | Ok command ->
        command.Dispose()
        failtestf "expected %A; the command was built" expected

let private refusedText (needle: string) (statement: BoundStatement) =
    match SynapseBinding.command statement with
    | Error(BoundStatementError.Failed(SchemaMismatch message)) ->
        Expect.stringContains message needle $"{statement.Text}: the refusal says why"
    | Error other -> failtestf "expected a refusal naming '%s'; got %s" needle (BoundStatementError.describe other)
    | Ok command ->
        command.Dispose()
        failtestf "expected a refusal naming '%s'; the command was built" needle

let private parameters (command: SqlCommand) =
    command.Parameters |> Seq.cast<SqlParameter> |> List.ofSeq

// ─── The marker reading, held to the SQL connector's ──────────────

let private corpus = [
    "SELECT a FROM t WHERE x = @p AND y > @q OR x = @p"
    "SELECT '@a', \"@b\", 'it''s @c' -- @d\n, @real /* @e */ FROM t"
    "SELECT [@a]]@b] FROM t WHERE x = @p"
    "SELECT /* a /* b */ @x */ @p"
    "SELECT @@ROWCOUNT, @p"
    "SELECT N'@n', @p"
    "SELECT 'a\\' , @p"
    "SELECT 1--@p"
    "SELECT 1 # @a\n, @p"
    "SELECT `@a`, @p"
    "SELECT $1, :name, ?, @p"
    "SELECT @p;"
    "SELECT @p ; -- done\n"
    "SELECT ';', @p"
    "SELECT @p; DROP TABLE t"
    "SELECT 1;SELECT 2"
    "SELECT 'open, @p"
    "SELECT \"open, @p"
    "SELECT [open, @p"
    "SELECT 1 /* open @p"
    "SELECT @p$x, @_y1, @9z"
    "SELECT @p\r\n-- a comment at the end"
    ""
]

let private markerTests =
    testList "the T-SQL marker reading" [
        test "agrees with the SQL connector's SQL Server reading on every text of the corpus" {
            for text in corpus do
                let sqlServer =
                    SqlBinding.scan SqlServer text
                    |> Result.map (
                        List.choose (fun piece ->
                            match piece with
                            | StatementPiece.Marker name -> Some name
                            | StatementPiece.Verbatim _ -> None)
                    )

                Expect.equal (SynapseBinding.markers text) sqlServer $"the two readings of {text} agree"
        }

        test "a marker inside a literal, a quoted identifier or a comment is left alone" {
            Expect.equal
                (SynapseBinding.markers "SELECT '@a', \"@b\", [@c], 'it''s @d' -- @e\n, @real /* @f */ FROM t")
                (Ok [ "real" ])
                "only the real marker"
        }

        test "a second statement is refused; a trailing separator is not" {
            match SynapseBinding.markers "SELECT @p; DROP TABLE t" with
            | Error reason -> Expect.stringContains reason "more than one statement" "the reason"
            | Ok found -> failtestf "expected the second statement refused; read %A" found

            Expect.equal (SynapseBinding.markers "SELECT @p;") (Ok [ "p" ]) "a trailing separator"
        }
    ]

// ─── The request the connector builds ─────────────────────────────

// Built from char values, so the source file holds no control byte.
let private nul = string (char 0)

let private hostile = [ "it's"; "x'); --"; "a -- b"; "a /* b */ c"; "a; b"; $"a{nul}b"; "@q" ]

let private requestTests =
    testList "the request" [
        test "the text is sent exactly as written and no value is rendered into it" {
            for value in hostile do
                let text = "SELECT x FROM t WHERE name = @p"
                use command = built (statementOf text [ "p", BoundValue.Text value ])
                Expect.equal command.CommandText text $"{value}: the text is the statement's"
                Expect.equal command.CommandType CommandType.Text "a text command, sent through sp_executesql"

                match parameters command with
                | [ p ] ->
                    Expect.equal p.ParameterName "@p" "bound by name"
                    Expect.equal p.Value (box value) $"{value} travels as the parameter's value"
                | other -> failtestf "expected one parameter; got %d" other.Length
        }

        test "each kind binds as its own SqlDbType" {
            let stamp = DateTimeOffset(2026, 11, 11, 9, 30, 15, 123, TimeSpan.FromHours 1.0)

            let cases = [
                BoundValue.Float 0.1, SqlDbType.Float, box 0.1
                BoundValue.Int Int64.MaxValue, SqlDbType.BigInt, box Int64.MaxValue
                BoundValue.Bool true, SqlDbType.Bit, box true
                BoundValue.Text "plain", SqlDbType.NVarChar, box "plain"
                BoundValue.Categorical "level-a", SqlDbType.NVarChar, box "level-a"
                BoundValue.Timestamp stamp, SqlDbType.DateTimeOffset, box stamp
            ]

            for value, sqlType, expected in cases do
                use command = built (statementOf "SELECT @p" [ "p", value ])
                let p = List.exactlyOne (parameters command)
                Expect.equal p.SqlDbType sqlType $"{value}"
                Expect.equal p.Value expected $"{value} is the provider's own value"
        }

        test "an exact decimal binds as a SqlDecimal holding every one of its digits" {
            for digits in
                [
                    "1234567890123456789012345678.9012345678"
                    "-0.000000000000000000000000000000000001"
                    String('9', 38)
                    "0"
                ] do
                use command = built (statementOf "SELECT @d" [ "d", BoundValue.Decimal digits ])
                let p = List.exactlyOne (parameters command)
                Expect.equal p.SqlDbType SqlDbType.Decimal "DECIMAL"

                match p.SqlValue with
                | :? SqlDecimal as exact ->
                    Expect.equal (exact.ToString()) digits $"{digits} round-trips digit for digit"
                    Expect.equal p.Precision exact.Precision "the precision is the value's"
                    Expect.equal p.Scale exact.Scale "the scale is the value's"
                | other -> failtestf "expected a SqlDecimal; got %A" other
        }

        test "an absent value binds as a typed null" {
            for kind in BoundValue.kinds do
                use command = built (statementOf "SELECT @p" [ "p", BoundValue.Null kind ])
                let p = List.exactlyOne (parameters command)
                Expect.equal p.Value (box DBNull.Value) $"a null {kind}"
        }

        test "a repeated marker is one parameter, and parameters follow the markers' order" {
            use command =
                built (statementOf "SELECT @b, @a, @b" [ "a", BoundValue.Int 1L; "b", BoundValue.Int 2L ])

            Expect.equal (parameters command |> List.map _.ParameterName) [ "@b"; "@a" ] "in marker order, once each"
        }

        test "the statement's timeout is the command's" {
            use command =
                built {
                    statementOf "SELECT 1" [] with
                        Timeout = Some(TimeSpan.FromSeconds 4.2)
                }

            Expect.equal command.CommandTimeout 5 "rounded up to whole seconds"
        }

        test "what the binding laws refuse is refused before any request exists" {
            refusedWith (BindingRefusal.MissingParameter "b") (statementOf "SELECT @a, @b" [ "a", BoundValue.Int 1L ])

            refusedWith
                (BindingRefusal.UnusedParameter "b")
                (statementOf "SELECT @a" [ "a", BoundValue.Int 1L; "b", BoundValue.Int 2L ])

            refusedWith
                (BindingRefusal.DuplicateName "a")
                (statementOf "SELECT @a" [ "a", BoundValue.Int 1L; "a", BoundValue.Int 2L ])

            refusedWith
                (BindingRefusal.NonCanonicalDecimal "d")
                (statementOf "SELECT @d" [ "d", BoundValue.Decimal "1.50" ])

            refusedWith (BindingRefusal.MissingParameter "p") (statementOf "SELECT '@q', @p" [ "q", BoundValue.Int 1L ])
        }

        test "a decimal beyond DECIMAL(38, s), a second statement and an unterminated literal are refused by name" {
            refusedText
                "parameter 'd' is a decimal beyond DECIMAL(38, s)"
                (statementOf "SELECT @d" [ "d", BoundValue.Decimal(String('9', 39)) ])

            refusedText "more than one statement" (statementOf "SELECT @p; DROP TABLE t" [ "p", BoundValue.Int 1L ])

            refusedText "ends inside a string literal" (statementOf "SELECT 'open, @p" [ "p", BoundValue.Int 1L ])
        }
    ]

// ─── The connector ────────────────────────────────────────────────

let private connectorTests =
    let unreachable =
        config "syn-bind" "Synapse" [ "server", "127.0.0.1"; "port", "1"; "database", "d"; "user", "u" ]

    testList "the connector" [
        test "declares Binds, over every kind" {
            match ConnectorCapabilities.binds (SynapseDataSource.createWithDefaultCredentials ()) with
            | Ok binds -> Expect.equal binds.BindableKinds (Set.ofList BoundValue.kinds) "every kind"
            | Error refusal -> failtestf "expected Binds; %s" (CapabilityNotDeclared.describe refusal)
        }

        testCaseAsync "refuses a statement before it opens a connection"
        <| async {
            let source = SynapseDataSource.create (TestSecretStore())
            let ctx = context "test-scope" None unreachable

            let! result = BoundQuery.run source ctx (statementOf "SELECT @a, @b" [ "a", BoundValue.Int 1L ])

            Expect.equal
                result
                (Error(BoundStatementError.Refused(BindingRefusal.MissingParameter "b")))
                "refused by name, with no credential asked for and no connection tried"
        }

        testCaseAsync "a statement it admits goes on to the connection, which asks for its credential"
        <| async {
            let source = SynapseDataSource.create (TestSecretStore())
            let ctx = context "test-scope" None unreachable
            let! result = BoundQuery.run source ctx (statementOf "SELECT @a" [ "a", BoundValue.Int 1L ])

            match result with
            | Error(BoundStatementError.Failed(CredentialMissing key)) ->
                Expect.equal key "synapse-credential" "the credential the connection needs"
            | other -> failtestf "expected the connection step's CredentialMissing; got %A" other
        }

        testCaseAsync "a scope it cannot read fails the statement, naming the key"
        <| async {
            let source = SynapseDataSource.createWithDefaultCredentials ()

            let ctx =
                config "syn-bind" "Synapse" [ "database", "d" ] |> context "test-scope" None

            let! result = BoundQuery.run source ctx (statementOf "SELECT @a" [ "a", BoundValue.Int 1L ])

            match result with
            | Error(BoundStatementError.Failed(SchemaMismatch message)) ->
                Expect.stringContains message "server" "names the missing key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

// ─── The armed leg ────────────────────────────────────────────────

/// The connector with every call pointed at the leg's endpoint: the contract
/// pack's call context carries no scope and no credential of its own.
let private scopedTo (scope: (string * string) list) (credential: string option) (inner: IDataSource) : IDataSource =
    let rebind (ctx: DataSourceCallContext) = {
        ctx with
            Config = {
                ctx.Config with
                    Kind = inner.Kind
                    ConnectionScope = Map.ofList scope
            }
            Credential = credential
    }

    let innerBinds = inner.Capabilities.Binds.Value

    let binds =
        { new IBindsStatements with
            member _.BindableKinds = innerBinds.BindableKinds

            member _.QueryBound(ctx, statement) =
                innerBinds.QueryBound(rebind ctx, statement)
        }

    { new IDataSource with
        member _.Kind = inner.Kind

        member _.Capabilities = {
            inner.Capabilities with
                Binds = Some binds
        }

        member _.Connect ctx = inner.Connect(rebind ctx)
        member _.ListTables ctx = inner.ListTables(rebind ctx)
        member _.GetSchema(ctx, table) = inner.GetSchema(rebind ctx, table)
        member _.Query(ctx, sql) = inner.Query(rebind ctx, sql)
    }

/// Seed a table through the connector itself: the DDL as statement text, each
/// row's cells bound as parameters. Every column is `NVARCHAR(4000)`, which a
/// dedicated pool's default columnstore table holds.
let private seeder (source: IDataSource) (schema: string) : IDataSourceContract.Seeder =
    fun sourceId table bytes ->
        let ctx = config sourceId "Synapse" [] |> context "test-scope" None
        let name = $"[%s{schema}].[%s{table}]"

        let run (result: Async<Result<byte[], 'e>>) =
            match Async.RunSynchronously result with
            | Ok _ -> ()
            | Error err -> failtestf "seeding %s: %A" table err

        let lines =
            (Encoding.UTF8.GetString bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        let header = (List.head lines).Split ','
        run (source.Query(ctx, $"IF OBJECT_ID(N'%s{name}', N'U') IS NOT NULL DROP TABLE %s{name}"))

        let columns = header |> Array.map (fun c -> $"[%s{c}] NVARCHAR(4000)")
        run (source.Query(ctx, $"""CREATE TABLE %s{name} (%s{String.Join(", ", columns)})"""))

        for row in List.tail lines do
            let cells =
                row.Split ',' |> Array.mapi (fun i cell -> $"c%d{i}", BoundValue.Text cell)

            let markers = cells |> Array.map (fst >> BoundStatement.marker)

            run (
                BoundQuery.run
                    source
                    ctx
                    (statementOf
                        $"""INSERT INTO %s{name} VALUES (%s{String.Join(", ", markers)})"""
                        (List.ofArray cells))
            )

let private armedLeg =
    let required = [ "TOOLUP_SYNAPSE_BIND_SERVER"; "TOOLUP_SYNAPSE_BIND_DATABASE" ]

    testList "a Synapse SQL endpoint (armed leg)" [
        match required |> List.filter (env >> Option.isNone) with
        | [] ->
            let schema = envOr "TOOLUP_SYNAPSE_BIND_SCHEMA" "dbo"

            let scope = [
                yield "server", envOr "TOOLUP_SYNAPSE_BIND_SERVER" ""
                yield "database", envOr "TOOLUP_SYNAPSE_BIND_DATABASE" ""
                yield "schema", schema
                yield "auth", envOr "TOOLUP_SYNAPSE_BIND_AUTH" "aad-default"
                match env "TOOLUP_SYNAPSE_BIND_USER" with
                | Some user -> yield "user", user
                | None -> ()
            ]

            let source () =
                scopedTo
                    scope
                    (env "TOOLUP_SYNAPSE_BIND_CREDENTIAL")
                    (SynapseDataSource.createWithDefaultCredentials ())

            IDataSourceContract.capabilityLaws "SynapseDataSource (armed)" source

            IDataSourceContract.bindingLaws "SynapseDataSource (armed)" (SqlBindingTests.harness SqlServer) (fun () ->
                let bound = source ()
                bound, seeder bound schema)
        | missing ->
            let named = String.Join(", ", missing)
            ptestCase $"set %s{named} to a scratch dedicated SQL pool" ignore
    ]

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Synapse — bound statements" [ markerTests; requestTests; connectorTests; armedLeg ]