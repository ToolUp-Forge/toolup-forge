module ToolUp.Platform.Tests.InProcess.MappingDryRunValidationTests

open Expecto
open DataManagementTypes
open ColumnMappingTypes
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// Phase 218 — dry-run validation for the CSV column-mapping flow. The
// validator inspects the *mapped* (canonical-shape) CSV against the
// target type's coarse `DataTypeSchema`, returning a per-row / per-cell
// report as data (GP 12.3) — and crucially never invokes
// `DataType.Process` (no commit).

let private col name typ required = {
    Name = name
    Type = typ
    Required = required
    Description = None
}

let private schema cols : DataTypeSchema = { Description = "test"; Columns = cols }

let private validator = MappingDryRunValidator.create ()

/// Mirror the handler's path: rewrite raw → canonical shape, then validate.
let private validate (sc: DataTypeSchema) mapping remediation (raw: string) : DryRunReport =
    let mapped = ColumnMapping.rewriteCsv sc mapping remediation raw
    validator.Validate(sc, mapped)

let private issueFor (report: DryRunReport) column =
    report.CellIssues |> List.tryFind (fun i -> i.Column = column)

let tests =
    testList "MappingDryRunValidation" [
        testList "validate (clean)" [
            test "a clean mapped CSV reports zero failures" {
                let sc = schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ]
                let raw = "Area,Turnover\nNorth,100\nSouth,200"
                let mapping = Map.ofList [ "Region", "Area"; "Revenue", "Turnover" ]
                let report = validate sc mapping Map.empty raw

                Expect.equal report.TotalRows 2 "two data rows"
                Expect.equal report.FailedRows 0 "no failures"
                Expect.equal report.PassedRows 2 "all pass"
                Expect.isEmpty report.CellIssues "no cell issues"
                Expect.isEmpty report.RowIssues "no structural issues"
                Expect.isFalse report.Truncated "not truncated"
            }

            test "remediated values validate cleanly (currency / ISO dates)" {
                let sc = schema [ col "Revenue" NumberColumn true; col "Day" DateColumn true ]
                let raw = "Amount,When\n\"$1,234.50\",01/02/2024\n$900,13/02/2024"
                let mapping = Map.ofList [ "Revenue", "Amount"; "Day", "When" ]

                let remediation =
                    Map.ofList [
                        "Amount", [ Trim; StripCurrency "$"; StripThousandsSeparators ]
                        "When", [ ParseDateToIso DayFirst ]
                    ]

                let report = validate sc mapping remediation raw
                Expect.equal report.FailedRows 0 "cleaned numbers + ISO dates all parse"
                Expect.equal report.PassedRows 2 "both rows pass"
            }
        ]

        testList "validate (violations)" [
            test "type + required violations pinpoint the exact failing cells, no commit" {
                let sc = schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ]
                // row 2: Revenue is text (type fail); row 3: Region is empty (required fail)
                let raw = "Area,Turnover\nNorth,abc\n,200"
                let mapping = Map.ofList [ "Region", "Area"; "Revenue", "Turnover" ]
                let report = validate sc mapping Map.empty raw

                Expect.equal report.TotalRows 2 "two data rows"
                Expect.equal report.FailedRows 2 "both rows fail"
                Expect.equal report.PassedRows 0 "none pass"

                let revenue = issueFor report "Revenue" |> Option.get
                Expect.equal revenue.Row 2 "Revenue failure on row 2"
                Expect.equal revenue.Actual "abc" "raw failing value carried"
                Expect.equal revenue.Expected "a number" "expected wording"
                Expect.isNone revenue.Violation "type-parse failure carries no constraint"

                let region = issueFor report "Region" |> Option.get
                Expect.equal region.Row 3 "Region failure on row 3"
                Expect.equal region.Actual "" "empty cell"
                Expect.equal region.Violation (Some "required value missing") "required-missing constraint"
            }

            test "an unmapped required field surfaces as a structural row issue" {
                let sc = schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ]
                // Revenue is not mapped → absent from the canonical header.
                let raw = "Area\nNorth\nSouth"
                let mapping = Map.ofList [ "Region", "Area" ]
                let report = validate sc mapping Map.empty raw

                Expect.isNonEmpty report.RowIssues "a structural issue is reported"

                Expect.isTrue
                    (report.RowIssues |> List.exists (fun r -> r.Detail.Contains "Revenue"))
                    "names the unmapped required field"
            }
        ]

        testList "DataType.Process is never invoked" [
            test "running the dry-run does not call the target type's Process" {
                let processCalled = ref false

                let dataType: DataType = {
                    Info = {
                        Id = "SalesData"
                        DisplayName = "Sales Data"
                        Schema = Some(schema [ col "Region" StringColumn true; col "Revenue" NumberColumn true ])
                    }
                    Id = "SalesData"
                    SchemaVersion = DataTypes.initialSchemaVersion
                    Migrations = []
                    Detect = fun _ -> async { return false }
                    Process =
                        fun _ ->
                            processCalled.Value <- true
                            async { return raise (exn "DataType.Process must not run during a dry-run") }
                }

                let sc = dataType.Info.Schema |> Option.get
                let raw = "Area,Turnover\nNorth,abc\nSouth,200"
                let mapping = Map.ofList [ "Region", "Area"; "Revenue", "Turnover" ]
                let report = validate sc mapping Map.empty raw

                Expect.isFalse processCalled.Value "Process was never invoked"
                Expect.equal report.FailedRows 1 "the bad row is reported as data, not thrown"
            }
        ]

        testList "commitBlocked policy" [
            let failing = {
                TargetTypeId = "T"
                TotalRows = 3
                PassedRows = 2
                FailedRows = 1
                CellIssues = []
                RowIssues = []
                Truncated = false
                CommitBlocked = false
            }

            let clean = {
                failing with
                    FailedRows = 0
                    PassedRows = 3
            }

            test "warn-only never blocks, even with failures" {
                Expect.isFalse
                    (MappingDryRunValidator.commitBlocked WarnOnValidationFailure failing)
                    "warn allows commit"
            }

            test "block blocks when rows fail" {
                Expect.isTrue
                    (MappingDryRunValidator.commitBlocked BlockOnValidationFailure failing)
                    "block refuses commit"
            }

            test "block allows commit when nothing fails" {
                Expect.isFalse (MappingDryRunValidator.commitBlocked BlockOnValidationFailure clean) "clean commits"
            }

            test "block blocks on a structural row issue alone" {
                let structural = {
                    clean with
                        RowIssues = [
                            {
                                Row = 0
                                Detail = "Required field 'X' is not mapped."
                            }
                        ]
                }

                Expect.isTrue
                    (MappingDryRunValidator.commitBlocked BlockOnValidationFailure structural)
                    "structural blocks"
            }
        ]

        testList "validator does not stamp the target type id" [
            test "TargetTypeId is left for the handler to stamp" {
                let sc = schema [ col "Region" StringColumn true ]

                let report =
                    validate sc (Map.ofList [ "Region", "Region" ]) Map.empty "Region\nNorth"

                Expect.equal report.TargetTypeId "" "validator leaves TargetTypeId empty"
            }
        ]
    ]