module ToolUp.Remoting.Analyzers.Tests.KeptListIndexAnalyzerTests

open Expecto
open FSharp.Analyzers.SDK
open ToolUp.Remoting.Analyzers

// ─── TUA004 — a message over a kept or tree-built list carries the row's identity ───
//
// Each index-binding shape has a fixture that fires; the identity form of the
// same view, and an index over a list nothing keeps or builds as a tree, do
// not. In memory, through the same parse-and-analyse path the VerifyAll leg
// takes, so the pack stays in the `pure` lane.

let private sites (source: string) =
    match TreeScan.scanSource "fixture.fs" source with
    | Ok sites -> sites |> List.filter (fun (code, _) -> code = KeptListIndexAnalyzer.Code)
    | Error e -> failtestf "fixture does not parse: %s" e

let private lines source = sites source |> List.map snd

/// The failing shape the authoring guide shows: a kept row keyed on its
/// position, fed through `Keep.list` over `List.indexed`.
let private keptByIndex =
    """module Demo
open Toolup.UIToolkit

type Row = { Id: string; Name: string; Count: int }
type Msg = Bump of int | Remove of int

let private rowNode =
    Keep.create (fun (i, row: Row) ->
        Nodes.Layout.row (Nodes.Ids.keyed "row" row.Id) [
            Nodes.text (Nodes.Ids.keyed "row-name" row.Id) row.Name
            Nodes.Actions.primary (Nodes.Ids.keyed "row-bump" row.Id) "Bump" (Bump i)
        ])

let tree (rows: Row list) =
    Nodes.Layout.column "rows" (Keep.list rowNode (List.indexed rows))
"""

/// The fixed shape: the same view, the message carrying the row's identity and
/// the keep keyed on the row.
let private keptById =
    """module Demo
open Toolup.UIToolkit

type Row = { Id: string; Name: string; Count: int }
type Msg = Bump of string | Remove of string

let private rowNode =
    Keep.create (fun (row: Row) ->
        Nodes.Layout.row (Nodes.Ids.keyed "row" row.Id) [
            Nodes.text (Nodes.Ids.keyed "row-name" row.Id) row.Name
            Nodes.Actions.primary (Nodes.Ids.keyed "row-bump" row.Id) "Bump" (Bump row.Id)
        ])

let tree (rows: Row list) =
    Nodes.Layout.column "rows" (Keep.list rowNode rows)
"""

let private typedIndexInKeep =
    """module Demo
let rowNode = Keep.create (fun (i: int, row) -> Nodes.Actions.danger "x" "Remove" (Remove i))
"""

let private treeBuiltByMapi =
    """module Demo
let tree rows =
    rows
    |> List.mapi (fun i row -> Nodes.Actions.primary (Nodes.Ids.keyed "row" row.Id) "Pick" (Msg.Select(Some i, row.Name)))
"""

let private treeBuiltOverIndexed =
    """module Demo
let a rows = rows |> List.indexed |> List.map (fun (i, row) -> Nodes.Actions.primary "a" "A" (Bump i))
let b rows = List.map (fun (i, row) -> Nodes.Actions.primary "b" "B" (Bump (i + 1))) (List.indexed rows)
let c rows = [ for (i, row) in List.indexed rows -> Nodes.Actions.primary "c" "C" (Bump i) ]
let d (rows: _ list) = [ for i in 0 .. rows.Length - 1 -> Fuaran.button "d" (Bump i) ]
"""

/// A Feliz list nothing keeps: the index is a bad habit there, not a stale
/// node, and the rule stays out of it.
let private felizByIndex =
    """module Demo
let view rows dispatch =
    Html.ul [ for i, row in List.indexed rows -> Html.li [ prop.onClick (fun _ -> dispatch (Bump i)) ] ]
let other rows dispatch = rows |> List.mapi (fun i row -> Html.button [ prop.onClick (fun _ -> dispatch (Bump i)) ])
"""

/// Inside a tree, an index that reaches no message is not reported: a label
/// showing the row's number, an option, a result.
let private indexOutsideMessages =
    """module Demo
let tree rows =
    rows
    |> List.mapi (fun i row ->
        let shown = Some i
        Nodes.text (Nodes.Ids.keyed "n" row.Id) (string (i + 1)))
"""

let private tests' =
    testList "TUA004 — a message over a kept list carries the row's identity" [
        test "the failing shape fires: Bump i over a kept list fed List.indexed" {
            Expect.equal (lines keptByIndex) [ 11 ] "the one message, on the kept row"
        }

        test "the fixed shape is clean: Bump row.Id over a keep keyed on the row" {
            Expect.isEmpty (sites keptById) "identity is the sanctioned form"
        }

        test "an int component of a Keep.create build is an index wherever the keep is fed" {
            Expect.equal (lines typedIndexInKeep) [ 2 ] "the typed index"
        }

        test "a tree built by mapi fires once on the outermost message" {
            Expect.equal (lines treeBuiltByMapi) [ 4 ] "Select (Some i, …) is one site, not two"
        }

        test "every indexed shape that builds a tree fires: piped, direct, a comprehension, a range loop" {
            Expect.equal (lines treeBuiltOverIndexed) [ 2; 3; 4; 5 ] "one site per shape"
        }

        test "a Feliz list nothing keeps is not reported" {
            Expect.isEmpty (sites felizByIndex) "no tree is built, so no node is kept"
        }

        test "an index reaching no message is not reported" {
            Expect.isEmpty (sites indexOutsideMessages) "labels and options are not messages"
        }

        test "TUA004 is a known rule whose message names the identity form" {
            let message =
                KeptListIndexAnalyzer.toMessage {
                    Code = KeptListIndexAnalyzer.Code
                    Range = FSharp.Compiler.Text.Range.range0
                }

            Expect.equal message.Severity Severity.Warning "a warning in the editor"
            Expect.stringContains message.Message "Sanctioned form" "the fix travels with the finding"
            Expect.stringContains message.Message "row.Id" "and names it"
        }
    ]

[<Tests>]
let tests = tests'