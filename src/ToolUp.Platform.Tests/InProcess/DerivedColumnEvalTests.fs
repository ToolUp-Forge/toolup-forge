module ToolUp.Platform.Tests.InProcess.DerivedColumnEvalTests

// ─── Phase 219 — derived/computed columns in CSV mapping ────────────
//
// Each `ColumnExpr` kind evaluates correctly and composes with the
// existing `CellTransform` cleaning; derived columns round-trip through
// the persisted `Conversion` (and a pre-219 blob without the field reads
// back as no derived columns); and a mapping with NO derived columns
// produces byte-for-byte-identical canonical output to Phase 172 (GP 13).

open System
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open DataManagementTypes
open ColumnMappingTypes

let private col name typ required = {
    Name = name
    Type = typ
    Required = required
    Description = None
}

let private schema cols : DataTypeSchema = { Description = "test"; Columns = cols }

/// A cell resolver over raw (un-transformed) values, for the evaluator
/// unit tests — the rewrite path supplies the remediated resolver.
let private cellsOf (pairs: (string * string) list) =
    let m = Map.ofList pairs
    fun name -> m |> Map.tryFind name |> Option.defaultValue ""

let tests =
    testList "DerivedColumnEval" [
        testList "evalColumnExpr" [
            test "SourceColumn reads the named cell; an unbound name → empty" {
                let cell = cellsOf [ "A", "hello" ]
                Expect.equal (ColumnMapping.evalColumnExpr cell (SourceColumn "A")) "hello" "bound"
                Expect.equal (ColumnMapping.evalColumnExpr cell (SourceColumn "Z")) "" "unbound → empty"
            }
            test "Constant is row-independent" {
                Expect.equal (ColumnMapping.evalColumnExpr (cellsOf []) (Constant "lit")) "lit" "constant"
            }
            test "Concat joins parts with the separator" {
                let cell = cellsOf [ "First", "John"; "Last", "Smith" ]
                let e = Concat([ SourceColumn "First"; SourceColumn "Last" ], " ")
                Expect.equal (ColumnMapping.evalColumnExpr cell e) "John Smith" "joined"
            }
            test "SplitTake takes the nth part; out-of-range → empty" {
                let cell = cellsOf [ "Full", "John Smith" ]
                Expect.equal (ColumnMapping.evalColumnExpr cell (SplitTake(SourceColumn "Full", " ", 0))) "John" "first"

                Expect.equal
                    (ColumnMapping.evalColumnExpr cell (SplitTake(SourceColumn "Full", " ", 1)))
                    "Smith"
                    "second"

                Expect.equal
                    (ColumnMapping.evalColumnExpr cell (SplitTake(SourceColumn "Full", " ", 9)))
                    ""
                    "out of range"
            }
            test "Substring is bounds-safe (never throws)" {
                let cell = cellsOf [ "S", "abcdef" ]
                Expect.equal (ColumnMapping.evalColumnExpr cell (Substring(SourceColumn "S", 0, 3))) "abc" "prefix"
                Expect.equal (ColumnMapping.evalColumnExpr cell (Substring(SourceColumn "S", 4, 99))) "ef" "len clamped"
                Expect.equal (ColumnMapping.evalColumnExpr cell (Substring(SourceColumn "S", 99, 3))) "" "start clamped"
            }
            test "Format substitutes {0}, {1}, … placeholders" {
                let cell = cellsOf [ "A", "x"; "B", "y" ]
                let e = Format("{0}-{1}", [ SourceColumn "A"; SourceColumn "B" ])
                Expect.equal (ColumnMapping.evalColumnExpr cell e) "x-y" "templated"
            }
            test "expressions nest" {
                let cell = cellsOf [ "Full", "John Smith" ]
                let e = Concat([ SplitTake(SourceColumn "Full", " ", 0); Constant "!" ], "")
                Expect.equal (ColumnMapping.evalColumnExpr cell e) "John!" "nested compose"
            }
        ]

        testList "rewriteCsvWithDerived" [
            test "a derived column appears in the canonical output" {
                let sc = schema [ col "Region" StringColumn true; col "FullKey" StringColumn true ]
                let raw = "Area,Code\nNorth,A1\nSouth,B2"
                let mapping = Map.ofList [ "Region", "Area" ]

                let derived = [
                    {
                        Field = "FullKey"
                        Expr = Concat([ SourceColumn "Area"; SourceColumn "Code" ], "/")
                    }
                ]

                let result = ColumnMapping.rewriteCsvWithDerived sc mapping Map.empty derived raw
                Expect.equal result "Region,FullKey\nNorth,North/A1\nSouth,South/B2" "derived emitted in schema order"
            }

            test "a derived column composes with the source column's remediation" {
                let sc = schema [ col "Label" StringColumn true ]
                let raw = "Price,Code\n\"$1,200\",AB"

                let transforms =
                    Map.ofList [ "Price", [ StripCurrency "$"; StripThousandsSeparators ] ]

                let derived = [
                    {
                        Field = "Label"
                        Expr = Concat([ SourceColumn "Price"; SourceColumn "Code" ], "-")
                    }
                ]

                let result = ColumnMapping.rewriteCsvWithDerived sc Map.empty transforms derived raw
                Expect.equal result "Label\n1200-AB" "cleaned currency feeds the derived value"
            }

            test "a derived column satisfies a schema field with no 1:1 map" {
                let sc = schema [ col "Year" StringColumn true ]
                let raw = "When\n2024-06-30"

                let derived = [
                    {
                        Field = "Year"
                        Expr = SplitTake(SourceColumn "When", "-", 0)
                    }
                ]

                let result = ColumnMapping.rewriteCsvWithDerived sc Map.empty Map.empty derived raw
                Expect.equal result "Year\n2024" "derived-only field still emits"
            }

            test "no derived columns → byte-for-byte identical to Phase 172 rewriteCsv (GP 13)" {
                let sc = schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ]
                let raw = "Area,Notes,Turnover\nNorth,ignore,100\nSouth,ignore,200"
                let mapping = Map.ofList [ "Region", "Area"; "Revenue", "Turnover" ]

                let legacy = ColumnMapping.rewriteCsv sc mapping Map.empty raw
                let withDerived = ColumnMapping.rewriteCsvWithDerived sc mapping Map.empty [] raw
                Expect.equal withDerived legacy "delegating path matches"

                Expect.equal
                    withDerived
                    "Region,Revenue\nNorth,100\nSouth,200"
                    "and matches the canonical Phase-172 output"
            }
        ]

        testList "validateDerivedColumns" [
            test "an unbound source reference is flagged" {
                let derived = [ { Field = "X"; Expr = SourceColumn "Z" } ]
                let errs = ColumnMapping.validateDerivedColumns [ "A"; "B" ] derived
                Expect.equal errs.Length 1 "one error"
                Expect.equal errs.Head.Field "X" "names the field"
                Expect.stringContains errs.Head.Detail "Z" "names the unknown column"
            }
            test "a reference to another derived field is flagged (cycle vector)" {
                let derived = [
                    { Field = "X"; Expr = SourceColumn "Y" }
                    { Field = "Y"; Expr = SourceColumn "A" }
                ]

                let errs = ColumnMapping.validateDerivedColumns [ "A" ] derived
                Expect.isTrue (errs |> List.exists (fun e -> e.Field = "X")) "X→Y derived ref flagged"
            }
            test "a valid expression over real columns passes" {
                let derived = [
                    {
                        Field = "X"
                        Expr = Concat([ SourceColumn "A"; SourceColumn "B" ], "-")
                    }
                ]

                Expect.isEmpty (ColumnMapping.validateDerivedColumns [ "A"; "B" ] derived) "no errors"
            }
        ]

        testList "persistence round-trip" [
            let opts = FableConverters.create ()

            let conv = {
                Fingerprint = "fp"
                TargetTypeId = "T"
                Mapping = Map.ofList [ "Region", "Area" ]
                Remediation = Map.empty
                SourceHeaders = [ "Area"; "Code" ]
                Derived = [
                    {
                        Field = "Key"
                        Expr = Concat([ SourceColumn "Area"; SourceColumn "Code" ], "/")
                    }
                ]
                CreatedBy = "tester"
                CreatedAt = DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }

            test "a Conversion with derived columns round-trips through STJ" {
                let json = JsonSerializer.Serialize(conv, opts)
                let back = JsonSerializer.Deserialize<Conversion>(json, opts)
                Expect.equal back.Derived conv.Derived "derived columns survive the round-trip"
                Expect.equal back conv "the whole conversion round-trips"
            }

            test "a pre-219 recipe (no Derived field) re-imports as no derived columns" {
                let legacy =
                    """{"Fingerprint":"fp","TargetTypeId":"T","Mapping":{"Region":"Area"},"Remediation":{},"SourceHeaders":["Area"],"CreatedBy":"tester","CreatedAt":"2024-01-01T00:00:00Z"}"""

                let back = JsonSerializer.Deserialize<Conversion>(legacy, opts)
                Expect.equal back.Fingerprint "fp" "the rest of the recipe still reads"
                // The record deserialiser fills the absent field with `null`
                // (F# `[]` is a real object, not null), so the rewrite must
                // coerce it rather than NRE on a `List` operation.
                Expect.isTrue (isNull (box back.Derived)) "absent field deserialises to null, not []"

                let sc = schema [ col "Region" StringColumn true ]

                let result =
                    ColumnMapping.rewriteCsvWithDerived sc back.Mapping back.Remediation back.Derived "Area\nNorth"

                Expect.equal result "Region\nNorth" "old recipe rewrites cleanly with no derived columns"
            }
        ]
    ]