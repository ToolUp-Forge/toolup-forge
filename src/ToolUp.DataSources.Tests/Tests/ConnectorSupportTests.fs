// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.ConnectorSupportTests

open System
open System.Globalization
open System.Text
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Common
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Common — always-on ────────────────────────
//
// Everything here runs on a credential-free checkout. It is the half
// of the connector family that CAN be proved offline: the
// configuration reader, the credential thunk's ordering rules, CSV
// escaping and value rendering, and the native-type classifier.

let private scope =
    Map.ofList [ "present", "value"; "blank", "   "; "number", "42"; "flag", "yes" ]

let private connectionScopeTests =
    testList "ConnectionScope" [
        test "require returns a present value" {
            Expect.equal (ConnectionScope.require scope "present") (Ok "value") "present key"
        }

        test "require treats a whitespace-only value as absent" {
            // An admin UI that persists an empty text box must not
            // produce a connector that fails later with a vendor
            // message nobody can map back to the blank field.
            match ConnectionScope.require scope "blank" with
            | Ok value -> failtestf "expected an error for a blank value; got %A" value
            | Error(SchemaMismatch message) -> Expect.stringContains message "blank" "the failure names the key"
            | Error other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "require names the missing key" {
            match ConnectionScope.require scope "absent" with
            | Ok value -> failtestf "expected an error; got %A" value
            | Error(SchemaMismatch message) -> Expect.stringContains message "absent" "the failure names the key"
            | Error other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "optional reads blank as None" {
            Expect.equal (ConnectionScope.optional scope "blank") None "blank"
            Expect.equal (ConnectionScope.optional scope "present") (Some "value") "present"
            Expect.equal (ConnectionScope.optionalOr scope "absent" "fallback") "fallback" "fallback"
        }

        test "optionalInt parses, and REFUSES a mistyped value" {
            Expect.equal (ConnectionScope.optionalInt scope "number") (Ok(Some 42)) "parses"
            Expect.equal (ConnectionScope.optionalInt scope "absent") (Ok None) "absent is not an error"

            // Silently falling back to a default here would turn a
            // mistyped port into a connection against the wrong host.
            match ConnectionScope.optionalInt scope "present" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch for a non-integer; got %A" other
        }

        test "optionalBool accepts the three affirmative spellings" {
            for raw in [ "true"; "TRUE"; "1"; "yes" ] do
                let m = Map.ofList [ "flag", raw ]
                Expect.equal (ConnectionScope.optionalBool m "flag") (Ok(Some true)) $"'{raw}' reads true"

            for raw in [ "false"; "0"; "No" ] do
                let m = Map.ofList [ "flag", raw ]
                Expect.equal (ConnectionScope.optionalBool m "flag") (Ok(Some false)) $"'{raw}' reads false"

            let m = Map.ofList [ "flag", "maybe" ]

            match ConnectionScope.optionalBool m "flag" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch for 'maybe'; got %A" other
        }

        test "optionalEnum names every accepted value in its failure" {
            let m = Map.ofList [ "auth", "kerberos" ]

            match ConnectionScope.optionalEnum m "auth" [ "sql"; "aad-token" ] with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "sql" "names the first accepted value"
                Expect.stringContains message "aad-token" "names the second accepted value"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

let private credentialTests =
    testList "Credentials" [
        testCaseAsync "a pre-resolved context credential short-circuits the store"
        <| async {
            let store = InMemorySecretStore()
            store.Put("scope-1", "sql-credential", "from-store")
            let ctx = config "s1" "Sql" [] |> context "scope-1" (Some "from-context")

            let! resolved = Credentials.resolve (Some(store :> _)) ctx
            Expect.equal resolved (Ok "from-context") "the ingestor's pre-resolved value wins"

            Expect.equal
                (store.ReadCount("scope-1", "sql-credential"))
                0
                "the store is not touched when the context already carries the credential"
        }

        testCaseAsync "an absent context credential falls back to the store, per call"
        <| async {
            let store = InMemorySecretStore()
            store.Put("scope-1", "sql-credential", "v1")
            let ctx = config "s1" "Sql" [] |> context "scope-1" None

            let! first = Credentials.resolve (Some(store :> _)) ctx
            Expect.equal first (Ok "v1") "first read"

            // Rotation WITHOUT reconstructing anything is the whole
            // point of the thunk pattern — the second call must see
            // the new value.
            store.Put("scope-1", "sql-credential", "v2")
            let! second = Credentials.resolve (Some(store :> _)) ctx
            Expect.equal second (Ok "v2") "the rotated value is picked up on the next call"
            Expect.equal (store.ReadCount("scope-1", "sql-credential")) 2 "the store is read on EVERY call"
        }

        testCaseAsync "a blank stored value is CredentialMissing, not an empty credential"
        <| async {
            let store = InMemorySecretStore()
            store.Put("scope-1", "sql-credential", "   ")
            let ctx = config "s1" "Sql" [] |> context "scope-1" None

            let! resolved = Credentials.resolve (Some(store :> _)) ctx
            Expect.equal resolved (Error(CredentialMissing "sql-credential")) "blank is missing"
        }

        testCaseAsync "no store and no context credential is CredentialMissing"
        <| async {
            let ctx = config "s1" "Sql" [] |> context "scope-1" None
            let! resolved = Credentials.resolve None ctx
            Expect.equal resolved (Error(CredentialMissing "sql-credential")) "named key"
        }

        testCaseAsync "resolveOptional turns every failure into None"
        <| async {
            let ctx = config "s1" "Sql" [] |> context "scope-1" None
            let! resolved = Credentials.resolveOptional None ctx
            Expect.equal resolved None "absent"
        }

        testCaseAsync "the store is scoped — another scope's secret is not visible"
        <| async {
            let store = InMemorySecretStore()
            store.Put("team-a", "sql-credential", "a-secret")
            let ctx = config "s1" "Sql" [] |> context "team-b" None

            let! resolved = Credentials.resolve (Some(store :> _)) ctx
            Expect.equal resolved (Error(CredentialMissing "sql-credential")) "team-b cannot read team-a's secret"
        }
    ]

let private csvTests =
    testList "Csv" [
        test "escapeField quotes only when RFC 4180 requires it" {
            Expect.equal (Csv.escapeField "plain") "plain" "no quoting"
            Expect.equal (Csv.escapeField "a,b") "\"a,b\"" "comma"
            Expect.equal (Csv.escapeField "say \"hi\"") "\"say \"\"hi\"\"\"" "embedded quotes are doubled"
            Expect.equal (Csv.escapeField "line\nbreak") "\"line\nbreak\"" "newline"
            Expect.equal (Csv.escapeField null) "" "null (an absent cell) renders as the unquoted empty field"
            Expect.equal (Csv.escapeField "") "\"\"" "a present empty string is quoted, so it is not NULL"
            Expect.equal (Csv.escapeField " ") " " "whitespace is not empty and stays unquoted"
        }

        test "renderValue is invariant-culture, so a comma-decimal host cannot corrupt numbers" {
            let original = Thread.CurrentThread.CurrentCulture

            try
                Thread.CurrentThread.CurrentCulture <- CultureInfo "de-DE"
                Expect.equal (Csv.renderValue (box 1234.5)) "1234.5" "float uses a dot under de-DE"
                Expect.equal (Csv.renderValue (box 12.75m)) "12.75" "decimal uses a dot under de-DE"
            finally
                Thread.CurrentThread.CurrentCulture <- original
        }

        test "renderValue maps nulls, DBNull, dates, bytes and bools" {
            Expect.isNull (Csv.renderValue null) "null is ABSENT, not the empty string"
            Expect.isNull (Csv.renderValue (box DBNull.Value)) "DBNull is ABSENT, not the empty string"
            Expect.equal (Csv.renderValue (box "")) "" "an empty string stays an empty string"
            Expect.equal (Csv.renderValue (box true)) "true" "bool true"
            Expect.equal (Csv.renderValue (box false)) "false" "bool false"

            Expect.equal
                (Csv.renderValue (box (DateTime(2026, 8, 27, 9, 30, 0, DateTimeKind.Utc))))
                "2026-08-27T09:30:00.0000000Z"
                "DateTime round-trips as ISO-8601 'O'"

            Expect.equal (Csv.renderValue (box [| 1uy; 2uy; 3uy |])) "AQID" "byte[] renders base64"
        }

        test "toBytes emits a CRLF-terminated header and body, with no BOM" {
            let bytes = Csv.toBytes [ "a"; "b" ] [ [ "1"; "x,y" ]; [ "2"; "z" ] ]
            let text = Encoding.UTF8.GetString bytes
            Expect.equal text "a,b\r\n1,\"x,y\"\r\n2,z\r\n" "exact payload"

            // A BOM appears as a stray character in the first header
            // cell for every naive downstream parser.
            Expect.isFalse (bytes.Length >= 3 && bytes[0] = 0xEFuy) "no UTF-8 BOM"
        }

        test "toBytes with no rows still emits the header" {
            let text = Csv.toBytes [ "only" ] [] |> Encoding.UTF8.GetString
            Expect.equal text "only\r\n" "header-only payload"
        }
    ]

// ─── Phase 833 — CSV null fidelity ────────────────────────────────
//
// NULL and the empty string must survive writer → bytes → reader as
// DIFFERENT values. The reader side is `ToolUp.Tabular` (reached here
// through the Excel companion's reference), named in full because this
// file's `Csv` is the connector writer.

module private NullFidelity =

    module TabularCsv = ToolUp.Tabular.Csv

    /// The pre-833 writer, verbatim — the collapse the go-red probe
    /// runs against.
    let legacyEscape (field: string) =
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

    let legacyRender (value: obj) =
        match Csv.renderValue value with
        | null -> ""
        | text -> text

    /// One record's fields, with quoting, off a CSV line.
    let fieldsOf (line: string) : ToolUp.Tabular.Csv.CsvField[] =
        use reader = new IO.StringReader(line + "\r\n")

        match TabularCsv.parseFields ',' reader |> Seq.exactlyOne with
        | _, Ok fields -> fields
        | _, Error message -> failtestf "unparseable %s: %s" line message

    /// What the connector reader makes of one line under a payload format.
    let readLine (payloadFormat: string option) (line: string) =
        fieldsOf line
        |> Array.map (fun field -> Csv.readField payloadFormat field.Text field.Quoted)

    /// The probe: do a NULL and an empty string come back as different
    /// values after the round trip? `render`/`escape` pick the writer.
    let nullAndEmptyStayDistinct (render: obj -> string) (escape: string -> string) =
        let line =
            [ box "a"; null; box ""; box DBNull.Value; box "b" ]
            |> List.map (render >> escape)
            |> String.concat ","

        match readLine (Some ToolUp.Platform.IngestedPayload.CurrentPayloadFormat) line with
        | [| _; nullCell; emptyCell; dbNullCell; _ |] -> nullCell <> emptyCell && nullCell = dbNullCell
        | other -> failtestf "expected five fields, got %A" other

let private csvNullFidelityTests =
    testList "Csv null fidelity (Phase 833)" [
        test "NULL and the empty string round-trip renderValue -> renderRow -> parseFields to DIFFERENT values" {
            let line =
                Csv.renderRow ([ box "a"; null; box ""; box DBNull.Value ] |> List.map Csv.renderValue)

            Expect.equal line "a,,\"\"," "absent cells unquoted, the empty string quoted"

            let fields = NullFidelity.fieldsOf line
            Expect.equal (fields |> Array.map _.Quoted) [| false; false; true; false |] "quoting is surfaced"
            Expect.equal (fields |> Array.map _.Text) [| "a"; ""; ""; "" |] "text alone cannot tell them apart"

            Expect.equal
                (NullFidelity.readLine (Some ToolUp.Platform.IngestedPayload.CurrentPayloadFormat) line)
                [|
                    Csv.PayloadField.Value "a"
                    Csv.PayloadField.Null
                    Csv.PayloadField.Value ""
                    Csv.PayloadField.Null
                |]
                "a payload of the current format reads NULL as Null and \"\" as the empty string"
        }

        test "the go-red probe: the old collapse fails it, the new rule passes it" {
            Expect.isFalse
                (NullFidelity.nullAndEmptyStayDistinct NullFidelity.legacyRender NullFidelity.legacyEscape)
                "under the pre-833 writer NULL and \"\" are the same bytes - the probe must be able to fail"

            Expect.isTrue
                (NullFidelity.nullAndEmptyStayDistinct Csv.renderValue Csv.escapeField)
                "under the null convention they stay distinct"
        }

        test "a probe that passes under the old collapse fails under the new rule" {
            // The pre-833 invariant: `a,,b` and `a,"",b` mean the same.
            let sameMeaning (payloadFormat: string option) =
                NullFidelity.readLine payloadFormat "a,,b" = NullFidelity.readLine payloadFormat "a,\"\",b"

            Expect.isTrue (sameMeaning None) "a pre-832 payload keeps the old reading"
            Expect.isTrue (sameMeaning (Some "1")) "a Phase-832 payload keeps the old reading"

            Expect.isFalse
                (sameMeaning (Some ToolUp.Platform.IngestedPayload.CurrentPayloadFormat))
                "a current payload no longer collapses them"
        }

        test "every non-empty field renders byte-identically to the RFC 4180 writer it replaced" {
            for value in [ "plain"; "a,b"; "say \"hi\""; "line\nbreak"; "cr\rhere"; " "; "0" ] do
                Expect.equal (Csv.escapeField value) (NullFidelity.legacyEscape value) $"field %A{value}"

            Expect.equal (Csv.escapeField null) (NullFidelity.legacyEscape null) "an absent cell too"
        }

        test "a pre-convention payload reports its empties as ambiguous, resolved neither way" {
            for format in [ None; Some "1"; Some "not-a-number"; Some "" ] do
                Expect.equal (Csv.readField format "" false) Csv.PayloadField.AmbiguousEmpty $"unquoted, %A{format}"
                Expect.equal (Csv.readField format "" true) Csv.PayloadField.AmbiguousEmpty $"quoted, %A{format}"
                Expect.equal (Csv.readField format "x" false) (Csv.PayloadField.Value "x") $"a value, %A{format}"
        }

        test "distinguishesNull keys on the payload-format token, and the ingestor stamps one that declares it" {
            Expect.isFalse (Csv.distinguishesNull None) "no token - written before Phase 832"
            Expect.isFalse (Csv.distinguishesNull (Some "1")) "Phase 832 payloads predate the convention"
            Expect.isTrue (Csv.distinguishesNull (Some "2")) "the Phase 833 token"
            Expect.isTrue (Csv.distinguishesNull (Some " 3 ")) "later formats keep it"
            Expect.isFalse (Csv.distinguishesNull (Some "2a")) "an unreadable token is not evidence"

            Expect.isTrue
                (Csv.distinguishesNull (Some ToolUp.Platform.IngestedPayload.CurrentPayloadFormat))
                "the token the ingestor writes today declares the convention the writer uses"
        }

        test "a user-uploaded CSV that quotes every field parses exactly as before" {
            let parse (text: string) =
                use reader = new IO.StringReader(text)
                ToolUp.Tabular.Csv.parseRecords ',' reader |> List.ofSeq

            Expect.equal
                (parse "\"a\",\"\",\"b\"\r\n\"1\",\"\",\"2\"\r\n")
                (parse "a,,b\r\n1,,2\r\n")
                "parseRecords interprets nothing: quoted and unquoted empties read alike"

            let schema =
                ToolUp.Tabular.TableSchema.make [
                    ToolUp.Tabular.ColumnSchema.make "a" ToolUp.Tabular.ColumnType.Text
                    ToolUp.Tabular.ColumnSchema.make "note" ToolUp.Tabular.ColumnType.Text
                ]

            let read (text: string) =
                ToolUp.Tabular.TabularReader.readCsvBytes
                    schema
                    ToolUp.Tabular.CsvReadOptions.defaults
                    (Encoding.UTF8.GetBytes text)

            let quotedEverything = read "\"a\",\"note\"\r\n\"x\",\"\"\r\n"
            Expect.equal quotedEverything (read "a,note\r\nx,\r\n") "the default reader sees no difference"

            Expect.equal
                (quotedEverything.Rows |> List.map (Map.find "note"))
                [ ToolUp.Tabular.TabularValue.Empty ]
                "an upload's empty stays Empty, as today"
        }

        test "TabularReader carries absence through a connector payload at the type level" {
            let payload =
                Csv.toBytes [ "name"; "note"; "qty" ] [
                    [ box "a"; null; box 1 ] |> Seq.map Csv.renderValue
                    [ box "b"; box ""; box DBNull.Value ] |> Seq.map Csv.renderValue
                ]

            let schema =
                ToolUp.Tabular.TableSchema.make [
                    ToolUp.Tabular.ColumnSchema.make "name" ToolUp.Tabular.ColumnType.Text
                    ToolUp.Tabular.ColumnSchema.make "note" ToolUp.Tabular.ColumnType.Text
                    ToolUp.Tabular.ColumnSchema.make "qty" ToolUp.Tabular.ColumnType.Integer
                ]

            let read convention =
                ToolUp.Tabular.TabularReader.readCsvBytesWithConvention
                    Ok
                    convention
                    schema
                    ToolUp.Tabular.CsvReadOptions.defaults
                    payload

            let convention =
                if Csv.distinguishesNull (Some ToolUp.Platform.IngestedPayload.CurrentPayloadFormat) then
                    ToolUp.Tabular.Csv.EmptyFieldConvention.UnquotedEmptyIsNull
                else
                    ToolUp.Tabular.Csv.EmptyFieldConvention.Undistinguished

            let under = read convention
            Expect.isEmpty under.CellErrors "no cell errors"

            Expect.equal
                (under.Rows |> List.map (fun row -> row["note"], row["qty"]))
                [
                    ToolUp.Tabular.TabularValue.Empty, ToolUp.Tabular.TabularValue.Integer 1L
                    ToolUp.Tabular.TabularValue.Text "", ToolUp.Tabular.TabularValue.Empty
                ]
                "NULL binds Empty, the empty string binds Text \"\""

            let legacy = read ToolUp.Tabular.Csv.EmptyFieldConvention.Undistinguished

            Expect.equal
                (legacy.Rows |> List.map (fun row -> row["note"]))
                [ ToolUp.Tabular.TabularValue.Empty; ToolUp.Tabular.TabularValue.Empty ]
                "without the convention both stay the ambiguous Empty, as today"
        }

        test "an all-NULL row is a row under the convention, not a skipped blank line" {
            let payload = Csv.toBytes [ "only" ] [ [ Csv.renderValue null ]; [ "x" ] ]
            Expect.equal (Encoding.UTF8.GetString payload) "only\r\n\r\nx\r\n" "a one-column NULL row is a blank line"

            let schema =
                ToolUp.Tabular.TableSchema.make [
                    ToolUp.Tabular.ColumnSchema.make "only" ToolUp.Tabular.ColumnType.Text
                ]

            let read convention =
                ToolUp.Tabular.TabularReader.readCsvBytesWithConvention
                    Ok
                    convention
                    schema
                    ToolUp.Tabular.CsvReadOptions.defaults
                    payload

            Expect.equal
                ((read ToolUp.Tabular.Csv.EmptyFieldConvention.UnquotedEmptyIsNull).Rows
                 |> List.map (Map.find "only"))
                [ ToolUp.Tabular.TabularValue.Empty; ToolUp.Tabular.TabularValue.Text "x" ]
                "the NULL row survives"

            Expect.equal
                ((read ToolUp.Tabular.Csv.EmptyFieldConvention.Undistinguished).Rows
                 |> List.map (Map.find "only"))
                [ ToolUp.Tabular.TabularValue.Text "x" ]
                "the default reader still skips blank lines"
        }

        test "Redshift renders a NULL field absent, not as an empty string" {
            let field = Amazon.RedshiftDataAPIService.Model.Field(IsNull = true)
            Expect.isNull (ToolUp.DataSources.Redshift.RedshiftDataSource.renderField field) "NULL is absent"

            let empty = Amazon.RedshiftDataAPIService.Model.Field(StringValue = "")
            Expect.equal (ToolUp.DataSources.Redshift.RedshiftDataSource.renderField empty) "" "\"\" stays \"\""
        }
    ]

let private credentialJsonTests =
    testList "CredentialJson" [
        test "parseObject lowercases keys and keeps string values" {
            match CredentialJson.parseObject "k" """{"AccessKeyId":"AK","secretAccessKey":"SK"}""" with
            | Ok fields ->
                Expect.equal (fields.TryFind "accesskeyid") (Some "AK") "key folded to lower case"
                Expect.equal (fields.TryFind "secretaccesskey") (Some "SK") "second key"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "parseObject renders non-string values as raw JSON" {
            match CredentialJson.parseObject "k" """{"n":42,"nested":{"a":1},"nil":null}""" with
            | Ok fields ->
                Expect.equal (fields.TryFind "n") (Some "42") "number"
                Expect.equal (fields.TryFind "nested") (Some """{"a":1}""") "nested object kept as raw JSON"
                Expect.equal (fields.TryFind "nil") (Some "") "null renders empty"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "parseObject refuses a non-object and names the credential" {
            for payload in [ "[]"; "\"just-a-string\""; "not json at all"; "" ] do
                match CredentialJson.parseObject "my-key" payload with
                | Error(SchemaMismatch message) -> Expect.stringContains message "my-key" "names the credential"
                | other -> failtestf "expected SchemaMismatch for '%s'; got %A" payload other
        }

        test "tryFind accepts every spelling a vendor might use" {
            let fields = Map.ofList [ "aws_access_key_id", "AK" ]

            Expect.equal
                (CredentialJson.tryFind fields [ "accessKeyId"; "aws_access_key_id" ])
                (Some "AK")
                "second spelling matches"

            Expect.equal (CredentialJson.tryFind fields [ "nope" ]) None "no match"
        }
    ]

let private sqlIdentifierTests =
    testList "SqlIdentifier" [
        test "accepts plain identifiers" {
            for value in [ "orders"; "Order_Items"; "t1"; "_staging"; "a$b" ] do
                Expect.isTrue (SqlIdentifier.isSafe value) $"'{value}' is a plain identifier"
        }

        test "refuses everything that could terminate a literal or qualify a name" {
            // This is the connectors' whole defence against a
            // catalogue query being steered by a table name, so the
            // cases are enumerated rather than sampled.
            for value in
                [
                    "orders; DROP TABLE x"
                    "o'brien"
                    "schema.table"
                    "with space"
                    "1leading_digit"
                    "--comment"
                    "\"quoted\""
                    "back`tick"
                    ""
                    "   "
                    null
                ] do
                Expect.isFalse (SqlIdentifier.isSafe value) $"'%A{value}' must be refused"
        }

        test "require names the label and the offending value" {
            match SqlIdentifier.require "table" "bad name" with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "table" "names the label"
                Expect.stringContains message "bad name" "names the value"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "quoteLiteral doubles single quotes" {
            Expect.equal (SqlIdentifier.quoteLiteral "o'brien") "o''brien" "doubled"
            Expect.equal (SqlIdentifier.quoteLiteral null) "" "null renders empty"
        }
    ]

let private typeMapTests =
    testList "TypeMap" [
        test "normalise strips BOTH bracket styles and folds case" {
            Expect.equal (TypeMap.normalise "DECIMAL(38, 9)") "decimal" "SQL parameter list"
            Expect.equal (TypeMap.normalise "  VarChar ") "varchar" "trimmed + folded"
            Expect.equal (TypeMap.normalise null) "" "null"
            // Hive-family catalogues spell generic types with angle
            // brackets. Cutting only at '(' would leave `array<int>`
            // reading as its ELEMENT type — i.e. a number.
            Expect.equal (TypeMap.normalise "array<int>") "array" "Hive generic"
            Expect.equal (TypeMap.normalise "map<string,int>") "map" "Hive map"
            Expect.equal (TypeMap.normalise "struct<a:int,b:string>") "struct" "Hive struct"
            Expect.equal (TypeMap.ansi "array<int>") StringColumn "and therefore does not classify as a number"
        }

        test "ansi classifies the four coarse families" {
            let cases = [
                "boolean", BooleanColumn
                "BIT", BooleanColumn
                "timestamp without time zone", DateColumn
                "datetime2", DateColumn
                "DATE", DateColumn
                "integer", NumberColumn
                "BIGINT", NumberColumn
                "numeric(38,9)", NumberColumn
                "double precision", NumberColumn
                "money", NumberColumn
                "varchar(255)", StringColumn
                "text", StringColumn
                "", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (TypeMap.ansi native) expected $"'{native}'"
        }

        test "classify prefers an override and otherwise defers to ansi" {
            let overrides =
                function
                | "variant" -> Some StringColumn
                | _ -> None

            Expect.equal (TypeMap.classify overrides "VARIANT") StringColumn "override wins"
            Expect.equal (TypeMap.classify overrides "bigint") NumberColumn "falls through to ansi"
        }

        test "column keeps the RAW native type name" {
            // The coarse ColumnType is a projection, not a
            // replacement: an admin UI renders NUMERIC(38,9), which is
            // strictly more informative than 'NumberColumn'.
            let column = TypeMap.column "amount" "NUMERIC(38, 9)" true
            Expect.equal column.DataType "NUMERIC(38, 9)" "raw name preserved verbatim"
            Expect.equal column.Name "amount" "name"
            Expect.isTrue column.Nullable "nullable"
        }
    ]

let private errorsTests =
    testList "Errors" [
        test "classify maps transport-shaped exceptions to SourceUnreachable" {
            match Errors.classify "ctx" (TimeoutException "slow") with
            | SourceUnreachable message -> Expect.stringContains message "ctx" "context is prefixed"
            | other -> failtestf "expected SourceUnreachable; got %A" other

            match Errors.classify "ctx" (System.Net.Http.HttpRequestException "no route") with
            | SourceUnreachable _ -> ()
            | other -> failtestf "expected SourceUnreachable; got %A" other
        }

        test "classify maps anything else to UnexpectedFailure" {
            match Errors.classify "ctx" (InvalidOperationException "boom") with
            | UnexpectedFailure message -> Expect.stringContains message "boom" "inner message kept"
            | other -> failtestf "expected UnexpectedFailure; got %A" other
        }

        testCaseAsync "guard converts a thrown exception into a classified Error"
        <| async {
            let! result = Errors.guard "probe" (fun () -> async { return failwith "kaboom" })

            match result with
            | Error(UnexpectedFailure message) -> Expect.stringContains message "kaboom" "inner message"
            | other -> failtestf "expected a classified Error; got %A" other
        }

        testCaseAsync "guard passes an Ok and an already-typed Error straight through"
        <| async {
            let! ok = Errors.guard "probe" (fun () -> async { return Ok 7 })
            Expect.equal ok (Ok 7) "Ok"

            let! err = Errors.guard "probe" (fun () -> async { return Error(CredentialMissing "k") })
            Expect.equal err (Error(CredentialMissing "k")) "typed Error is not reclassified"
        }
    ]

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Common" [
        connectionScopeTests
        credentialTests
        csvTests
        csvNullFidelityTests
        credentialJsonTests
        sqlIdentifierTests
        typeMapTests
        errorsTests
    ]