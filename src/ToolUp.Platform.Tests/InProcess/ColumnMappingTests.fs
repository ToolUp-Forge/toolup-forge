module ToolUp.Platform.Tests.InProcess.ColumnMappingTests

// Phase 835 — the column profiler defers to a declared schema (an ingested
// payload's recorded schema, Phase 832) and falls back to inference only
// when nothing was declared; every profile says which it did.

open System.Text.RegularExpressions
open Expecto
open DataManagementTypes
open ColumnMappingTypes

let private declared typ nullable =
    Some {
        DeclaredType = typ
        DeclaredNullable = nullable
    }

/// The pre-Phase-835 profile fields, rendered and whitespace-collapsed so a
/// `%A` line-wrapping difference cannot masquerade as a behaviour change.
let private legacyView (p: ColumnProfile) =
    Regex.Replace(sprintf "%A" (p.Column, p.InferredType, p.DetectedUnit, p.Issues), @"\s+", " ")

/// Profiles captured from the profiler as it stood BEFORE Phase 835 (tree
/// 1a99ca68), for a corpus spanning every issue kind. The no-schema path
/// must reproduce them exactly. The one deliberate exception (835.C, a
/// bare `-` in a text column) is pinned separately below.
let private legacyCorpus = [
    "Price",
    [ "$1,200"; "$950"; "$1,000.50" ],
    "(\"Price\", NumberColumn, Some \"$\", [{ Kind = NumbersFormattedAsText Detail = \"Numeric values rendered as text (symbols / separators / spacing).\" Examples = [\"$1,200\"; \"$950\"; \"$1,000.50\"] Suggested = [Trim; StripCurrency \"$\"; StripThousandsSeparators] Safe = true NeedsChoice = false }])"
    "Qty", [ "1"; "2"; "3" ], "(\"Qty\", NumberColumn, None, [])"
    "Qty",
    [ "1"; "n/a"; "3"; "NULL"; "" ],
    "(\"Qty\", NumberColumn, None, [{ Kind = NullMarkersPresent Detail = \"Null-marker tokens present — blank them so they don't parse as text.\" Examples = [\"n/a\"; \"NULL\"] Suggested = [BlankNullMarkers [\"n/a\"; \"na\"; \"null\"; \"none\"; \"nil\"; \"-\"; \"--\"; \"#n/a\"]] Safe = true NeedsChoice = false }])"
    "Amount",
    [ "10"; "-"; "20"; "30"; "--" ],
    "(\"Amount\", NumberColumn, None, [{ Kind = NullMarkersPresent Detail = \"Null-marker tokens present — blank them so they don't parse as text.\" Examples = [\"-\"; \"--\"] Suggested = [BlankNullMarkers [\"n/a\"; \"na\"; \"null\"; \"none\"; \"nil\"; \"-\"; \"--\"; \"#n/a\"]] Safe = true NeedsChoice = false }])"
    "When",
    [ "01/02/2024"; "03/04/2024"; "05/06/2024" ],
    "(\"When\", DateColumn, None, [{ Kind = AmbiguousDateFormat Detail = \"Ambiguous date order (e.g. 01/02/2024) — choose day-first or month-first.\" Examples = [\"01/02/2024\"; \"03/04/2024\"; \"05/06/2024\"] Suggested = [] Safe = false NeedsChoice = true }])"
    "When",
    [ "13/02/2024"; "N/A"; "28/02/2024" ],
    "(\"When\", DateColumn, None, [{ Kind = NullMarkersPresent Detail = \"Null-marker tokens present — blank them so they don't parse as text.\" Examples = [\"N/A\"] Suggested = [BlankNullMarkers [\"n/a\"; \"na\"; \"null\"; \"none\"; \"nil\"; \"-\"; \"--\"; \"#n/a\"]] Safe = true NeedsChoice = false }; { Kind = ResolvedDateFormat Detail = \"Dates resolved as day-first (a day value exceeds 12).\" Examples = [\"13/02/2024\"; \"28/02/2024\"] Suggested = [ParseDateToIso DayFirst] Safe = true NeedsChoice = false }])"
    "Name",
    [ " alice"; "bob "; "carol" ],
    "(\"Name\", StringColumn, None, [{ Kind = LeadingTrailingWhitespace Detail = \"Leading / trailing whitespace on values.\" Examples = [\" alice\"; \"bob \"] Suggested = [Trim] Safe = true NeedsChoice = false }])"
    "Pct",
    [ "10%"; "20%"; "35.5%" ],
    "(\"Pct\", NumberColumn, Some \"%\", [{ Kind = NumbersFormattedAsText Detail = \"Numeric values rendered as text (symbols / separators / spacing).\" Examples = [\"10%\"; \"20%\"; \"35.5%\"] Suggested = [Trim; StripPercent; StripThousandsSeparators] Safe = true NeedsChoice = false }])"
    "Flag", [ "yes"; "no"; "YES" ], "(\"Flag\", BooleanColumn, None, [])"
    "Empty", [ ""; "n/a"; "  " ], "(\"Empty\", StringColumn, None, [])"
    "Nothing", [], "(\"Nothing\", StringColumn, None, [])"
    "Code",
    [ "'0012"; "'0340"; "'0999" ],
    "(\"Code\", NumberColumn, None, [{ Kind = NumbersFormattedAsText Detail = \"Numeric values rendered as text (symbols / separators / spacing).\" Examples = [\"'0012\"; \"'0340\"; \"'0999\"] Suggested = [Trim; StripLeadingApostrophe; StripThousandsSeparators] Safe = true NeedsChoice = false }])"
]

let private markersOf (p: ColumnProfile) =
    p.Issues
    |> List.collect _.Suggested
    |> List.choose (function
        | BlankNullMarkers ms -> Some ms
        | _ -> None)

let tests =
    testList "Phase 835 ColumnMapping declared schema" [

        testList "declared schema wins over the marker vocabulary" [
            test "a column declared NOT NULL is not nullable because a cell reads n/a" {
                let p =
                    ColumnMapping.profileColumnWith (declared "varchar(32)" false) "Status" [ "open"; "n/a"; "closed" ]

                Expect.isFalse p.Nullable "declared NOT NULL"
                Expect.equal p.NullabilitySource Declared "nullability came from the schema"
                Expect.isEmpty (markersOf p) "no null-marker remediation proposed"

                Expect.isFalse
                    (p.Issues |> List.exists (fun i -> i.Kind = NullMarkersPresent))
                    "n/a is a value, not a marker"

                Expect.equal p.InferredType StringColumn "declared varchar"
                Expect.equal p.TypeSource Declared "type came from the schema"
            }

            test "a column declared nullable is nullable though no sampled cell is blank" {
                let p =
                    ColumnMapping.profileColumnWith (declared "INT64" true) "Qty" [ "1"; "2"; "3" ]

                Expect.isTrue p.Nullable "declared nullable"
                Expect.equal p.NullabilitySource Declared "declared"
                Expect.equal p.InferredType NumberColumn "INT64 is a number"
                Expect.equal p.TypeSource Declared "declared"
            }

            test "a column of hyphens under a declared varchar survives as text" {
                let p =
                    ColumnMapping.profileColumnWith (declared "varchar" false) "Ticker" [ "-"; "-"; "-" ]

                Expect.equal p.InferredType StringColumn "text"
                Expect.equal p.TypeSource Declared "declared"
                Expect.isFalse p.Nullable "declared NOT NULL"
                Expect.isEmpty p.Issues "nothing is blanked"
            }

            test "a numeric-looking column under a declared text type is not re-typed or cleaned" {
                let p =
                    ColumnMapping.profileColumnWith (declared "STRING" true) "Account" [ "$1,200"; "0012"; "950" ]

                Expect.equal p.InferredType StringColumn "declared text stays text"
                Expect.equal p.DetectedUnit None "no unit on a text column"

                Expect.isFalse
                    (p.Issues |> List.exists (fun i -> i.Kind = NumbersFormattedAsText))
                    "no numeric clean-up proposed against the declaration"
            }

            test "an unrecognised declared type falls back to inference and says so" {
                let p =
                    ColumnMapping.profileColumnWith (declared "GEOGRAPHY" false) "Qty" [ "1"; "2"; "3" ]

                Expect.equal p.InferredType NumberColumn "inferred from text"
                Expect.equal p.TypeSource Inferred "type was guessed"
                Expect.equal p.NullabilitySource Declared "nullability still declared"
                Expect.isFalse p.Nullable "declared NOT NULL"
            }

            test "an all-blank sample keeps the declared type" {
                let p = ColumnMapping.profileColumnWith (declared "DATE" true) "When" [ ""; "" ]
                Expect.equal p.InferredType DateColumn "declared date"
                Expect.equal p.TypeSource Declared "declared"
            }

            test "declared type names map onto the coarse column types" {
                let cases = [
                    "INT64", Some NumberColumn
                    "decimal(10,2)", Some NumberColumn
                    "double precision", Some NumberColumn
                    "BOOL", Some BooleanColumn
                    "timestamp without time zone", Some DateColumn
                    "TIMESTAMP_NTZ", Some DateColumn
                    "date", Some DateColumn
                    "character varying", Some StringColumn
                    "nvarchar(255)", Some StringColumn
                    "GEOGRAPHY", None
                    "", None
                ]

                for (name, expected) in cases do
                    Expect.equal (ColumnMapping.columnTypeOfDeclared name) expected name
            }
        ]

        testList "no schema — the inference fallback" [
            test "the upload path reproduces the pre-835 profiles exactly" {
                for (header, cells, expected) in legacyCorpus do
                    let p = ColumnMapping.profileColumn header cells
                    Expect.equal (legacyView p) expected header
                    Expect.equal p.TypeSource Inferred $"{header}: type inferred"
                    Expect.equal p.NullabilitySource Inferred $"{header}: nullability inferred"
            }

            test "profileColumn is profileColumnWith None" {
                for (header, cells, _) in legacyCorpus do
                    Expect.equal
                        (ColumnMapping.profileColumn header cells)
                        (ColumnMapping.profileColumnWith None header cells)
                        header
            }

            test "inferred nullability is seen from blank and marker cells" {
                Expect.isFalse (ColumnMapping.profileColumn "Qty" [ "1"; "2" ]).Nullable "all present"
                Expect.isTrue (ColumnMapping.profileColumn "Qty" [ "1"; "" ]).Nullable "a blank cell"
                Expect.isTrue (ColumnMapping.profileColumn "Qty" [ "1"; "NULL" ]).Nullable "a marker cell"
                Expect.isTrue (ColumnMapping.profileColumn "Qty" []).Nullable "no sample rules nothing out"
            }
        ]

        testList "835.C — a bare hyphen" [
            test "is text in a text column (not blanked, not a marker)" {
                let p = ColumnMapping.profileColumn "Code" [ "AB-1"; "-"; "XY-2"; "none" ]
                let markers = markersOf p |> List.concat
                Expect.isFalse (List.contains "-" markers) "hyphen is not in the blanking list"
                Expect.contains markers "none" "the other markers still apply"
                Expect.equal (ColumnMapping.applyTransforms (p.Issues |> List.collect _.Suggested) "-") "-" "survives"
            }

            test "a column of only hyphens is a column of text" {
                let p = ColumnMapping.profileColumn "Sign" [ "-"; "-" ]
                Expect.equal p.InferredType StringColumn "text"
                Expect.isFalse p.Nullable "nothing absent"
                Expect.isEmpty p.Issues "nothing to blank"
            }

            test "still reads as absent in a numeric column" {
                let p = ColumnMapping.profileColumn "Amount" [ "10"; "-"; "20" ]
                Expect.equal p.InferredType NumberColumn "number"
                Expect.isTrue p.Nullable "the hyphen is a placeholder here"
                Expect.contains (markersOf p |> List.concat) "-" "blanked"
            }

            test "still reads as absent in a date column" {
                let p = ColumnMapping.profileColumn "When" [ "13/02/2024"; "-"; "28/02/2024" ]
                Expect.equal p.InferredType DateColumn "date"
                Expect.contains (markersOf p |> List.concat) "-" "blanked"
            }
        ]
    ]