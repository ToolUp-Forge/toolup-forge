// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.ServerPartsBaselineTests

open System.IO
open System.Xml.Linq
open Expecto

// ─── The server tier's parts stay a layered partition ────────────────
//
// ToolUp.Platform.Server is an umbrella over part assemblies cut along its
// compile order (docs/migrations/1193-platform-server-subsystem-assemblies.md).
// At the cut commit this pack proved, once, that the parts' baselines union to
// the pre-cut Server baseline token for token; that check read a frozen copy of
// the old file and would fail on the next legitimate surface change, so it was
// retired with its fixture. What stays true of every later tree is held here:
//
//   * the umbrella references every part, and each part references only the
//     parts listed before it, so an edit to a part never recompiles an earlier
//     one;
//   * the parts partition the tier's surface: no public token is in the
//     baselines of two of them.

let private umbrella = "ToolUp.Platform.Server"

/// The token lines of a baseline: everything but its `#` header and blanks.
let tokens (text: string) : string list =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))
    |> List.ofArray

/// The `ToolUp.Platform.Server.*` projects a project file references, in
/// declaration order.
let partReferences (projectText: string) : string list =
    XDocument.Parse(projectText).Descendants()
    |> Seq.filter (fun e -> e.Name.LocalName = "ProjectReference")
    |> Seq.map (fun e -> Path.GetFileNameWithoutExtension(e.Attribute(XName.Get "Include").Value.Replace('\\', '/')))
    |> Seq.filter (fun name -> name.StartsWith(umbrella + "."))
    |> List.ofSeq

/// Findings for a layering: each part, in order, may reference only the
/// parts before it.
let layeringFindings (parts: (string * string list) list) : string list =
    let order = parts |> List.map fst

    parts
    |> List.collect (fun (part, refs) ->
        let earlier = order |> List.takeWhile ((<>) part)

        refs
        |> List.filter (fun r -> not (List.contains r earlier))
        |> List.map (sprintf "%s references %s, which is not a part before it" part))

/// Tokens that appear in more than one of the named baselines.
let shared (baselines: (string * string list) list) : (string * string list) list =
    baselines
    |> List.collect (fun (name, ts) -> ts |> List.distinct |> List.map (fun t -> t, name))
    |> List.groupBy fst
    |> List.choose (fun (t, owners) ->
        match owners |> List.map snd with
        | [ _ ] -> None
        | many -> Some(t, many))

let tests =
    testList "Phase 1193 — the server tier's parts stay a layered partition" [
        test "layeringFindings names a reference to a later part" {
            let findings = layeringFindings [ "A", []; "B", [ "A"; "C" ]; "C", [ "A"; "B" ] ]

            Expect.equal
                findings
                [ "B references C, which is not a part before it" ]
                "only the forward reference is a finding"
        }

        test "shared finds a token two baselines both carry" {
            Expect.equal
                (shared [ "A", [ "x"; "y" ]; "B", [ "y"; "z" ] ])
                [ "y", [ "A"; "B" ] ]
                "the one shared token, with both owners"

            Expect.isEmpty
                (shared [ "A", [ "x"; "x" ]; "B", [ "y" ] ])
                "a token repeated inside one baseline is not shared"
        }

        test "each part references only the parts before it" {
            let root = GateReconcileTests.repoRoot

            let read name =
                File.ReadAllText(Path.Combine(root, "src", name, name + ".fsproj"))

            let parts = partReferences (read umbrella)
            Expect.isGreaterThan parts.Length 1 "CONTROL: the umbrella references its parts"

            let findings =
                parts |> List.map (fun p -> p, partReferences (read p)) |> layeringFindings

            Expect.isEmpty findings "the parts are layered in the umbrella's reference order"
        }

        test "no public token is in the baselines of two server assemblies" {
            let root = GateReconcileTests.repoRoot

            let parts =
                partReferences (File.ReadAllText(Path.Combine(root, "src", umbrella, umbrella + ".fsproj")))

            let baselines =
                umbrella :: parts
                |> List.map (fun name ->
                    name,
                    File.ReadAllText(Path.Combine(root, "api-baselines", name + ".approved.txt"))
                    |> tokens)

            Expect.isGreaterThan (baselines |> List.sumBy (snd >> List.length)) 1000 "CONTROL: the baselines were read"

            let both = shared baselines
            Expect.isEmpty both (sprintf "tokens two server assemblies both declare: %A" (List.truncate 20 both))
        }
    ]