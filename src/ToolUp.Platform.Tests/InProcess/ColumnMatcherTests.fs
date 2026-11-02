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
                let result = ColumnMapping.rewriteCsv sc mapping Map.empty raw
                let expected = "Region,Revenue\nNorth,100\nSouth,200"
                Expect.equal result expected "rewritten to canonical shape"
            }

            test "unmapped optional field is omitted from the output" {
                let sc = schema [ col "Region" StringColumn true; col "Segment" StringColumn false ]

                let raw = "Area\nNorth\nSouth"
                let mapping = Map.ofList [ "Region", "Area" ] // Segment unmapped
                let result = ColumnMapping.rewriteCsv sc mapping Map.empty raw
                Expect.equal result "Region\nNorth\nSouth" "only Region emitted"
            }

            test "values containing commas are re-quoted" {
                let sc = schema [ col "Name" StringColumn true ]
                let raw = "Title\n\"Smith, John\""
                let mapping = Map.ofList [ "Name", "Title" ]
                let result = ColumnMapping.rewriteCsv sc mapping Map.empty raw
                Expect.equal result "Name\n\"Smith, John\"" "comma value re-quoted"
            }

            test "per-column transforms are applied during rewrite" {
                let sc = schema [ col "Revenue" NumberColumn true; col "Day" DateColumn true ]
                let raw = "Amount,When\n\"$1,234.50\",01/02/2024\n$900,13/02/2024"
                let mapping = Map.ofList [ "Revenue", "Amount"; "Day", "When" ]

                let transforms =
                    Map.ofList [
                        "Amount",
                        [
                            ColumnMappingTypes.Trim
                            ColumnMappingTypes.StripCurrency "$"
                            ColumnMappingTypes.StripThousandsSeparators
                        ]
                        "When", [ ColumnMappingTypes.ParseDateToIso ColumnMappingTypes.DayFirst ]
                    ]

                let result = ColumnMapping.rewriteCsv sc mapping transforms raw
                Expect.equal result "Revenue,Day\n1234.50,2024-02-01\n900,2024-02-13" "cleaned + ISO dates"
            }
        ]

        testList "toIsoDate" [
            test "day-first vs month-first disambiguate 01/02/2024" {
                Expect.equal (ColumnMapping.toIsoDate DayFirst "01/02/2024") (Some "2024-02-01") "day-first"
                Expect.equal (ColumnMapping.toIsoDate MonthFirst "01/02/2024") (Some "2024-01-02") "month-first"
            }
            test "ISO year-first parses" {
                Expect.equal (ColumnMapping.toIsoDate YearFirst "2024-03-09") (Some "2024-03-09") "iso"
            }
            test "out-of-range under an order returns None" {
                Expect.isNone (ColumnMapping.toIsoDate MonthFirst "13/02/2024") "month 13 invalid"
            }
        ]

        testList "applyTransform" [
            test "strips thousands separators and currency, keeps decimals" {
                let v =
                    ColumnMapping.applyTransforms [ Trim; StripCurrency "$"; StripThousandsSeparators ] " $1,234.50 "

                Expect.equal v "1234.50" "clean number"
            }
            test "DecimalCommaToDot handles European numbers" {
                Expect.equal (ColumnMapping.applyTransform DecimalCommaToDot "1.234,56") "1234.56" "euro decimal"
            }
            test "BlankNullMarkers blanks markers only" {
                let ts = [ BlankNullMarkers [ "N/A"; "-" ] ]
                Expect.equal (ColumnMapping.applyTransforms ts "N/A") "" "marker blanked"
                Expect.equal (ColumnMapping.applyTransforms ts "42") "42" "real value kept"
            }
        ]

        testList "profileColumn" [
            test "currency column is flagged NumbersFormattedAsText with detected unit" {
                let p = ColumnMapping.profileColumn "Price" [ "$1,200"; "$950"; "$1,000.50" ]
                Expect.equal p.InferredType NumberColumn "numeric after cleanup"
                Expect.equal p.DetectedUnit (Some "$") "unit captured"
                Expect.isTrue (p.Issues |> List.exists (fun i -> i.Kind = NumbersFormattedAsText)) "flagged"
                Expect.isTrue (p.Issues |> List.forall _.Safe) "safe fix"
            }
            test "ambiguous date column needs a choice" {
                let p =
                    ColumnMapping.profileColumn "When" [ "01/02/2024"; "03/04/2024"; "05/06/2024" ]

                let dateIssue = p.Issues |> List.find (fun i -> i.Kind = AmbiguousDateFormat)
                Expect.isTrue dateIssue.NeedsChoice "blocks until chosen"
                Expect.isFalse dateIssue.Safe "not auto-applied"
            }
            test "date column with a day > 12 resolves automatically (no choice)" {
                let p =
                    ColumnMapping.profileColumn "When" [ "13/02/2024"; "01/02/2024"; "28/02/2024" ]

                let dateIssue = p.Issues |> List.find (fun i -> i.Kind = ResolvedDateFormat)
                Expect.isFalse dateIssue.NeedsChoice "resolved as day-first"
                Expect.equal dateIssue.Suggested [ ParseDateToIso DayFirst ] "day-first transform"
            }
            test "clean column has no issues" {
                let p = ColumnMapping.profileColumn "Qty" [ "1"; "2"; "3" ]
                Expect.isEmpty p.Issues "nothing to fix"
            }
        ]
    ]