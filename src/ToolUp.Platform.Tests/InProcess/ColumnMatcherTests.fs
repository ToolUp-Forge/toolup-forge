module ToolUp.Platform.Tests.InProcess.ColumnMatcherTests

open Expecto
open DataManagementTypes
open ColumnMappingTypes

let private col name typ required = {
    Name = name
    Type = typ
    Required = required
    Description = None
}

let private schema cols : DataTypeSchema = { Description = "test"; Columns = cols }

/// Find the suggestion for a named field.
let private fieldOf (s: MappingSuggestion) name =
    s.Fields |> List.find (fun f -> f.Field.Name = name)

let tests =
    testList "ColumnMatcher" [
        testList "nameSimilarity" [
            test "exact normalised match is 1.0" {
                Expect.equal (ColumnMapping.nameSimilarity "Region" "Region") 1.0 "exact"
            }
            test "case / separator differences still score 1.0 after normalisation" {
                Expect.equal (ColumnMapping.nameSimilarity "Unit Price" "unit_price") 1.0 "normalised exact"
            }
            test "related names score high" {
                Expect.isGreaterThan (ColumnMapping.nameSimilarity "Customer Name" "customer") 0.4 "token overlap"
            }
            test "unrelated names score low" {
                Expect.isLessThan (ColumnMapping.nameSimilarity "Region" "Quantity") 0.4 "unrelated"
            }
        ]

        testList "inferColumnType" [
            test "all-numeric → NumberColumn" {
                Expect.equal (ColumnMapping.inferColumnType [ "1"; "2.5"; "-3" ]) NumberColumn "numeric"
            }
            test "true/false tokens → BooleanColumn" {
                Expect.equal (ColumnMapping.inferColumnType [ "true"; "false"; "yes" ]) BooleanColumn "bool"
            }
            test "0/1 stay numeric, not boolean" {
                Expect.equal (ColumnMapping.inferColumnType [ "0"; "1"; "1" ]) NumberColumn "0/1 numeric"
            }
            test "parseable dates → DateColumn" {
                Expect.equal (ColumnMapping.inferColumnType [ "2024-01-01"; "2024-06-30" ]) DateColumn "dates"
            }
            test "mixed text → StringColumn" {
                Expect.equal (ColumnMapping.inferColumnType [ "alpha"; "12"; "beta" ]) StringColumn "mixed"
            }
            test "empty / all-blank → StringColumn" {
                Expect.equal (ColumnMapping.inferColumnType [ ""; "  " ]) StringColumn "blank"
            }
        ]

        testList "Fingerprint.ofHeaders" [
            test "order-independent" {
                let a = ColumnMapping.Fingerprint.ofHeaders [ "Region"; "Sales"; "Date" ]
                let b = ColumnMapping.Fingerprint.ofHeaders [ "Date"; "Sales"; "Region" ]
                Expect.equal a b "same set, different order"
            }
            test "case-insensitive and trims" {
                let a = ColumnMapping.Fingerprint.ofHeaders [ "Region "; "SALES" ]
                let b = ColumnMapping.Fingerprint.ofHeaders [ "region"; "sales" ]
                Expect.equal a b "case/whitespace normalised"
            }
            test "different column sets differ" {
                let a = ColumnMapping.Fingerprint.ofHeaders [ "Region"; "Sales" ]
                let b = ColumnMapping.Fingerprint.ofHeaders [ "Region"; "Sales"; "Date" ]
                Expect.notEqual a b "different sets"
            }
        ]

        testList "suggest" [
            test "exact-name, type-compatible field is Confident and maps to that column" {
                let sc = schema [ col "Region" StringColumn true ]
                let headers = [ "Region"; "Other" ]
                let samples = Map.ofList [ "Region", [ "North"; "South" ]; "Other", [ "x"; "y" ] ]
                let s = ColumnMapping.suggest "T" sc headers samples
                let f = fieldOf s "Region"
                Expect.equal f.Flag Confident "confident"
                Expect.equal f.SuggestedColumn (Some "Region") "maps to Region"
            }

            test "required field with no plausible column is Unmatched (no suggestion)" {
                let sc = schema [ col "Latitude" NumberColumn true ]
                let headers = [ "Region"; "Sales" ]
                let samples = Map.ofList [ "Region", [ "N" ]; "Sales", [ "10" ] ]
                let s = ColumnMapping.suggest "T" sc headers samples
                let f = fieldOf s "Latitude"
                Expect.equal f.Flag Unmatched "unmatched"
                Expect.isNone f.SuggestedColumn "no column"
            }

            test "strong name match but incompatible sample type is TypeMismatch" {
                let sc = schema [ col "Amount" NumberColumn true ]
                let headers = [ "Amount" ]
                let samples = Map.ofList [ "Amount", [ "small"; "large" ] ] // text, not numbers
                let s = ColumnMapping.suggest "T" sc headers samples
                let f = fieldOf s "Amount"
                Expect.equal f.Flag TypeMismatch "type mismatch"
                Expect.equal f.SuggestedColumn (Some "Amount") "still suggests the name match"
            }

            test "two near-identical-name columns flag Ambiguous" {
                let sc = schema [ col "Date" DateColumn true ]
                let headers = [ "Date"; "date" ]
                let samples = Map.ofList [ "Date", [ "2024-01-01" ]; "date", [ "2024-02-02" ] ]
                let s = ColumnMapping.suggest "T" sc headers samples
                let f = fieldOf s "Date"
                Expect.equal f.Flag Ambiguous "ambiguous"
            }

            test "fingerprint is attached to the suggestion" {
                let sc = schema [ col "A" StringColumn true ]
                let s = ColumnMapping.suggest "T" sc [ "A"; "B" ] Map.empty
                Expect.equal s.Fingerprint (ColumnMapping.Fingerprint.ofHeaders [ "A"; "B" ]) "fingerprint"
            }
        ]

        testList "rewriteCsv" [
            test "emits only mapped schema columns, in schema order, with canonical headers" {
                let sc = schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ]

                // source columns are renamed + reordered vs the schema,
                // plus an extra unmapped column that must be dropped.
                let raw = "Area,Notes,Turnover\nNorth,ignore,100\nSouth,ignore,200"
                let mapping = Map.ofList [ "Region", "Area"; "Revenue", "Turnover" ]
                let result = ColumnMapping.rewriteCsv sc mapping raw
                let expected = "Region,Revenue\nNorth,100\nSouth,200"
                Expect.equal result expected "rewritten to canonical shape"
            }

            test "unmapped optional field is omitted from the output" {
                let sc = schema [ col "Region" StringColumn true; col "Segment" StringColumn false ]

                let raw = "Area\nNorth\nSouth"
                let mapping = Map.ofList [ "Region", "Area" ] // Segment unmapped
                let result = ColumnMapping.rewriteCsv sc mapping raw
                Expect.equal result "Region\nNorth\nSouth" "only Region emitted"
            }

            test "values containing commas are re-quoted" {
                let sc = schema [ col "Name" StringColumn true ]
                let raw = "Title\n\"Smith, John\""
                let mapping = Map.ofList [ "Name", "Title" ]
                let result = ColumnMapping.rewriteCsv sc mapping raw
                Expect.equal result "Name\n\"Smith, John\"" "comma value re-quoted"
            }
        ]
    ]