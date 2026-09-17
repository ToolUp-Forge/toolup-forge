// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module internal ToolUp.Remoting.Generator.Program

open System
open System.IO
open System.Reflection
open ToolUp.Remoting.Generator

// =============================================================================
// Phase 69k.A — the generator's command line
// =============================================================================
//
// A dev-time tool over a BUILT assembly, not an in-compiler generator. `Plan`'s
// header carries the argument for that; the consequence here is that the entry
// point takes an assembly path rather than a compilation.
//
// Three modes, and the census is not an afterthought:
//
//   census   — every API record an assembly declares, with the wire types its
//              methods return and whether each is expressible in the closed
//              algebra. This is the measurement Phase 69k was triggered on.
//              The Phase 785 facet classifies the records a composition root
//              DECLARES, so the facet structurally cannot report the records
//              nobody declared; this can.
//   decoders — emit a registration module for the named API records.
//   dispatch — emit the typed argument-parse table for one API record.

let private usage =
    """ToolUp.Remoting.Generator — Phase 69k

  census   --assembly <path>
  decoders --assembly <path> --out <file> [--namespace N] [--module M]
           [--open NS]... [--api-record FullName]... [--corpus-covered FullName]...
  dispatch --assembly <path> --api-record <FullName> --out <file> [--namespace N]

With no --api-record, `decoders` and `census` cover every API record the
assembly declares."""

/// Collect `--flag value` pairs; repeated flags accumulate in order.
let private parse (argv: string list) =
    let rec go acc =
        function
        | (flag: string) :: value :: rest when flag.StartsWith "--" -> go ((flag.Substring 2, value) :: acc) rest
        | [] -> List.rev acc
        | unexpected :: _ -> failwithf "unexpected argument '%s'" unexpected

    go [] argv

/// Phase 804 — a repeated flag also accepts a `;`-separated list in one
/// value (`--api-record "A;B"`), because that is the shape MSBuild item
/// metadata arrives in: the build-time target hands `%(ApiRecords)`
/// through verbatim rather than re-splitting it in XML.
let private values name (args: (string * string) list) =
    args
    |> List.filter (fst >> (=) name)
    |> List.collect (fun (_, v) ->
        v.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
        |> Array.toList)

let private value name args = values name args |> List.tryLast

let private required name args =
    match value name args with
    | Some v -> v
    | None -> failwithf "--%s is required" name

/// Phase 804 — write only when the text differs. The build-time target runs
/// this tool after every build of the declaring project, and the emitted
/// file is a `<Compile>` input of a sibling project; rewriting identical
/// bytes would move its timestamp and force that sibling to recompile on
/// every build, which is the incremental-build cost the generator's design
/// otherwise avoids. Returns whether anything was written.
let private writeIfChanged (path: string) (text: string) : bool =
    let unchanged = File.Exists path && File.ReadAllText path = text

    if not unchanged then
        let dir = Path.GetDirectoryName(Path.GetFullPath path)

        if not (String.IsNullOrEmpty dir) then
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(path, text)

    not unchanged

/// Load the assembly and let its dependencies resolve from beside it —
/// the tool runs against a build output directory, so siblings are there.
let private load (path: string) =
    let full = Path.GetFullPath path
    let dir = Path.GetDirectoryName full

    AppDomain.CurrentDomain.add_AssemblyResolve (
        ResolveEventHandler(fun _ e ->
            let name = AssemblyName(e.Name).Name
            let candidate = Path.Combine(dir, name + ".dll")

            if File.Exists candidate then
                Assembly.LoadFrom candidate
            else
                null)
    )

    Assembly.LoadFrom full

let private selectedRecords (assembly: Assembly) (args: (string * string) list) =
    match values "api-record" args with
    | [] -> Plan.apiRecordsIn assembly
    | names ->
        names
        |> List.map (fun n ->
            match assembly.GetType(n, false) with
            | null -> failwithf "assembly declares no type '%s'" n
            | t -> t)

let private census (assembly: Assembly) (records: Type list) =
    printfn "%d API record(s) in %s" (List.length records) (assembly.GetName().Name)
    printfn ""

    let mutable algebra = 0

    for record in records do
        let returns = Plan.returnTypes record
        let plan = Plan.forTypes returns
        let expressible = List.isEmpty plan.Refusals

        if expressible then
            algebra <- algebra + 1

        printfn
            "  %-44s %d return type(s)  %s"
            (Plan.simpleName record)
            (List.length returns)
            (if expressible then "algebra" else "reflection")

        for refusal in plan.Refusals do
            printfn "      refused %s — %s" refusal.RefusedType refusal.Why

    printfn ""

    printfn "%d of %d record(s) are wholly expressible in the closed algebra" algebra (List.length records)

let private decoders (assembly: Assembly) (records: Type list) (args: (string * string) list) =
    let corpusCovered = values "corpus-covered" args |> Set.ofList
    let roots = records |> List.collect Plan.returnTypes |> List.distinct
    let plan = Plan.forTypes roots

    let covered =
        records
        |> List.filter (fun r ->
            Plan.returnTypes r
            |> List.forall (fun t -> plan.Roots |> List.exists (fun p -> p.RootFullName = t.FullName)))

    let apiRecords =
        covered
        |> List.map (fun r ->
            Plan.simpleName r, (Plan.returnTypes r |> List.map Plan.typeSpelling), corpusCovered.Contains r.FullName)

    let options = {
        Namespace = defaultArg (value "namespace" args) "ToolUp.Remoting"
        ModuleName = defaultArg (value "module" args) "GeneratedDecoders"
        Opens = values "open" args
        ApiRecords = apiRecords
    }

    let out = required "out" args
    let written = writeIfChanged out (Emit.compilationUnit options plan)

    printfn
        "%s %s — %d decoder(s), %d registration(s)"
        (if written then "wrote" else "unchanged")
        out
        (List.length plan.Bindings)
        (List.length (Emit.coveredSpellings plan))

    printfn "%s" (Emit.refusalReport plan)

let private dispatch (records: Type list) (args: (string * string) list) =
    let record =
        match records with
        | [ single ] -> single
        | _ -> failwith "dispatch takes exactly one --api-record"

    let out = required "out" args
    let ns = defaultArg (value "namespace" args) "ToolUp.Remoting.Server.Generated"
    let table = Dispatch.tableFor record

    let written =
        writeIfChanged out (Dispatch.compilationUnit ns (values "open" args) table)

    printfn "%s %s — %d method(s)" (if written then "wrote" else "unchanged") out (List.length table.Methods)

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | [] ->
        eprintfn "%s" usage
        1
    | command :: rest ->
        try
            let args = parse rest
            let assembly = load (required "assembly" args)
            let records = selectedRecords assembly args

            match command with
            | "census" ->
                census assembly records
                0
            | "decoders" ->
                decoders assembly records args
                0
            | "dispatch" ->
                dispatch records args
                0
            | other ->
                eprintfn "unknown command '%s'\n\n%s" other usage
                1
        with ex ->
            eprintfn "%s" ex.Message
            1