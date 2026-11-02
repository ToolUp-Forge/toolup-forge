// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.ServerPartsBaselineTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Xml.Linq
open Expecto

// ─── The server tier's parts carry exactly the surface it had ─────────
//
// ToolUp.Platform.Server was cut into part assemblies along its compile
// order (docs/migrations/1193-platform-server-subsystem-assemblies.md).
// The approval gate renders one baseline per assembly, so the one Server
// baseline became one per part plus the umbrella's own. The cut moved code
// between assemblies and no member: the parts' baselines, taken together,
// are the pre-cut baseline token for token. The pre-cut file is kept as a
// fixture so this is checked against the bytes the gate approved.
//
// One normalisation, and only one: the CLR name of an F# anonymous record
// type (`<>f__AnonymousType<hash>`) hashes the assembly that defines it, so
// a member returning one renders a different name from a new assembly with
// the same fields and the same position.

let private umbrella = "ToolUp.Platform.Server"

let private preCut =
    Path.Combine(
        "src",
        "ToolUp.Platform.Build.Tests",
        "fixtures",
        "server-parts",
        "ToolUp.Platform.Server.pre-cut.approved.txt"
    )

let private anonymousType =
    Regex(@"<>f__AnonymousType\d+", RegexOptions.CultureInvariant)

/// The token lines of a baseline: everything but its `#` header and blanks.
let tokens (text: string) : string list =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.filter (fun l -> l.Trim() <> "" && not (l.StartsWith "#"))
    |> List.ofArray

/// A token with any anonymous record type's assembly-hashed name erased.
let normalise (token: string) : string =
    anonymousType.Replace(token, "<>f__AnonymousType#")

/// The part assemblies the umbrella references, read from its project file.
let parts (umbrellaProjectText: string) : string list =
    XDocument.Parse(umbrellaProjectText).Descendants()
    |> Seq.filter (fun e -> e.Name.LocalName = "ProjectReference")
    |> Seq.map (fun e -> Path.GetFileNameWithoutExtension(e.Attribute(XName.Get "Include").Value.Replace('\\', '/')))
    |> Seq.filter (fun name -> name.StartsWith(umbrella + "."))
    |> List.ofSeq

/// Token-count differences between two baselines' multisets: tokens the
/// first holds more often than the second, each with the surplus.
let surplus (a: string list) (b: string list) : (string * int) list =
    let count xs = xs |> List.countBy id |> Map.ofList

    let ca, cb = count a, count b

    ca
    |> Map.toList
    |> List.choose (fun (t, n) ->
        let m = cb |> Map.tryFind t |> Option.defaultValue 0
        if n > m then Some(t, n - m) else None)

let tests =
    testList "Phase 1193 — the server parts' baselines union to the pre-cut Server baseline" [
        test "surplus finds a token one side holds more often" {
            Expect.equal
                (surplus [ "a"; "a"; "b" ] [ "a"; "c" ])
                [ "a", 1; "b", 1 ]
                "a surplus is counted, not just noticed"

            Expect.isEmpty (surplus [ "a"; "b" ] [ "b"; "a" ]) "order is not surplus"
        }

        test "normalise erases only an anonymous record type's hash" {
            Expect.equal
                (normalise "X.f() : <>f__AnonymousType4011771768`4[System.Int32]")
                (normalise "X.f() : <>f__AnonymousType640971477`4[System.Int32]")
                "two assemblies' names for one anonymous record compare equal"

            Expect.notEqual
                (normalise "X.f() : System.Int32")
                (normalise "X.g() : System.Int32")
                "a member name is not erased"
        }

        test "the union of the umbrella's and the parts' baselines is the pre-cut baseline" {
            let root = GateReconcileTests.repoRoot

            let umbrellaProject =
                File.ReadAllText(Path.Combine(root, "src", umbrella, umbrella + ".fsproj"))

            let names = umbrella :: parts umbrellaProject
            Expect.isGreaterThan names.Length 1 "CONTROL: the umbrella references its parts"

            let baseline name =
                File.ReadAllText(Path.Combine(root, "api-baselines", name + ".approved.txt"))
                |> tokens
                |> List.map normalise

            let union = names |> List.collect baseline

            let before =
                File.ReadAllText(Path.Combine(root, preCut)) |> tokens |> List.map normalise

            Expect.isGreaterThan before.Length 1000 "CONTROL: the fixture is the pre-cut baseline, not an empty file"

            let lost = surplus before union
            let gained = surplus union before

            Expect.isEmpty lost (sprintf "tokens the cut lost from ToolUp.Platform.Server: %A" (List.truncate 20 lost))

            Expect.isEmpty
                gained
                (sprintf "tokens the cut added or duplicated across parts: %A" (List.truncate 20 gained))
        }
    ]