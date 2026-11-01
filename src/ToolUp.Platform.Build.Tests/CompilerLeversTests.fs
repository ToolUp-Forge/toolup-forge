// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.CompilerLeversTests

open System
open System.IO
open System.Xml.Linq
open Expecto

// ─── The compiler levers stay as measured ────────────────────────────
//
// Two compiler settings were measured on this tree and then decided; the
// tables are in docs/migrations/1195-compiler-levers.md. This pack holds
// both decisions, in both directions:
//
//   * `ProduceReferenceAssembly` is ON — declared `true` in an
//     unconditioned PropertyGroup of Directory.Build.props, and no project
//     turns it off. The SDK already defaults it on for an .fsproj targeting
//     net7.0+, so the declaration holds it against a project, an import or
//     a future SDK. It is what stops an implementation-only edit at the
//     edited project (2 projects recompile, not 129).
//   * `ParallelCompilation` is OFF — declared nowhere. Measured, it made
//     the two bottleneck projects slower on this machine, and the F# 10.0
//     compiler warns that the switch is for test purposes only (FS0075) on
//     every project it reaches. Switching it on means re-measuring first.

let private migrationDoc = "docs/migrations/1195-compiler-levers.md"

let private referenceAssembly = "ProduceReferenceAssembly"
let private parallelCompilation = "ParallelCompilation"

let private localName (e: XElement) = e.Name.LocalName

let private isTrue (e: XElement) =
    String.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)

let private unconditioned (e: XElement) =
    isNull (e.Attribute(XName.Get "Condition"))

/// Findings for `Directory.Build.props`. XML comments are not elements,
/// so a setting mentioned only in prose counts as absent.
let propsFindings (propsText: string) : string list =
    let doc = XDocument.Parse propsText

    let declaredUnconditioned name =
        doc.Root.Elements()
        |> Seq.filter (fun g -> localName g = "PropertyGroup" && unconditioned g)
        |> Seq.collect _.Elements()
        |> Seq.filter (fun p -> localName p = name && unconditioned p)
        |> List.ofSeq

    let reference =
        match declaredUnconditioned referenceAssembly with
        | [ one ] when isTrue one -> []
        | [] -> [
            sprintf
                "Directory.Build.props does not declare <%s>true</%s> in an unconditioned PropertyGroup — see %s"
                referenceAssembly
                referenceAssembly
                migrationDoc
          ]
        | many -> [
            sprintf
                "Directory.Build.props declares <%s> as %s; it must be one unconditioned `true` — see %s"
                referenceAssembly
                (many
                 |> List.map (fun e -> sprintf "'%s'" (e.Value.Trim()))
                 |> String.concat ", ")
                migrationDoc
          ]

    let parallel =
        doc.Descendants()
        |> Seq.filter (fun p -> localName p = parallelCompilation)
        |> Seq.map (fun p ->
            sprintf
                "Directory.Build.props declares <%s>%s</%s>; it was measured slower on this tree and is left unset (declaring it false also warns FS0075) — re-measure and update %s first"
                parallelCompilation
                (p.Value.Trim())
                parallelCompilation
                migrationDoc)
        |> List.ofSeq

    reference @ parallel

/// Findings for one project file: it turns reference assemblies off, or
/// declares parallel compilation at all.
let projectFindings (relPath: string) (projText: string) : string list =
    let doc = XDocument.Parse projText

    doc.Descendants()
    |> Seq.choose (fun p ->
        let name = localName p

        if name = referenceAssembly && not (isTrue p) then
            Some(
                sprintf
                    "%s sets <%s> to '%s', opting out of reference assemblies — see %s"
                    relPath
                    name
                    (p.Value.Trim())
                    migrationDoc
            )
        elif name = parallelCompilation then
            Some(sprintf "%s declares <%s>; it is left unset tree-wide — see %s" relPath name migrationDoc)
        else
            None)
    |> List.ofSeq

let private repoProjects (root: string) =
    [ "src"; "samples"; "tests"; "tools"; "templates"; "docs-snippets" ]
    |> List.map (fun d -> Path.Combine(root, d))
    |> List.filter Directory.Exists
    |> List.collect (fun d ->
        Directory.EnumerateFiles(d, "*.fsproj", SearchOption.AllDirectories)
        |> List.ofSeq)
    |> List.filter (fun p ->
        let parts = p.Replace('\\', '/').Split('/')

        not (
            Array.contains "node_modules" parts
            || Array.contains "bin" parts
            || Array.contains "obj" parts
        ))

let private withBody (body: string) =
    sprintf
        """<Project>
  <!-- <ProduceReferenceAssembly>true</ProduceReferenceAssembly> mentioned in prose -->
  <PropertyGroup>
    <WarningsAsErrors>FS0025</WarningsAsErrors>
  </PropertyGroup>
  %s
</Project>"""
        body

let private declared =
    "<PropertyGroup><ProduceReferenceAssembly>true</ProduceReferenceAssembly></PropertyGroup>"

let private expectNaming (findings: string list) (setting: string) =
    Expect.exists
        findings
        (fun f -> f.Contains(setting: string) && f.Contains migrationDoc)
        (sprintf "a finding naming %s and %s, got: %A" setting migrationDoc findings)

[<Tests>]
let tests =
    testList "Phase 1195 — compiler levers stay as measured" [
        testCase "the real Directory.Build.props holds both decisions"
        <| fun () ->
            let text =
                File.ReadAllText(Path.Combine(GateReconcileTests.repoRoot, "Directory.Build.props"))

            let findings = propsFindings text

            Expect.isEmpty findings (String.concat " | " findings)

        testCase "no project in the tree overrides a lever"
        <| fun () ->
            let root = GateReconcileTests.repoRoot
            let projects = repoProjects root
            Expect.isGreaterThan projects.Length 100 "the project scan found too few .fsproj files to mean anything"

            let findings =
                projects
                |> List.collect (fun p -> projectFindings (Path.GetRelativePath(root, p)) (File.ReadAllText p))

            Expect.isEmpty findings (String.concat " | " findings)

        testCase "the declared fixture is clean"
        <| fun () -> Expect.isEmpty (propsFindings (withBody declared)) "the clean fixture reported a finding"

        testCase "reference assemblies removed, or named only in a comment, is red and names the doc"
        <| fun () ->
            let findings = propsFindings (withBody "")
            Expect.hasLength findings 1 "removing the declaration"
            expectNaming findings referenceAssembly

        testCase "reference assemblies set false is red"
        <| fun () ->
            let body = declared.Replace(">true<", ">false<")
            expectNaming (propsFindings (withBody body)) referenceAssembly

        testCase "reference assemblies behind a Condition do not count"
        <| fun () ->
            let onProperty =
                declared.Replace(
                    "<ProduceReferenceAssembly>",
                    "<ProduceReferenceAssembly Condition=\"'$(Configuration)' == 'Release'\">"
                )

            expectNaming (propsFindings (withBody onProperty)) referenceAssembly

            let onGroup =
                declared.Replace("<PropertyGroup>", "<PropertyGroup Condition=\"'$(CI)' == 'true'\">")

            expectNaming (propsFindings (withBody onGroup)) referenceAssembly

        testCase "parallel compilation declared either way is red and names the doc"
        <| fun () ->
            for value in [ "true"; "false" ] do
                let body =
                    declared.Replace(
                        "</PropertyGroup>",
                        sprintf "<ParallelCompilation>%s</ParallelCompilation></PropertyGroup>" value
                    )

                let findings = propsFindings (withBody body)
                Expect.hasLength findings 1 (sprintf "declaring ParallelCompilation %s" value)
                expectNaming findings parallelCompilation

        testCase "a project overriding a lever is red and names the doc"
        <| fun () ->
            let proj (inner: string) =
                sprintf "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>%s</PropertyGroup></Project>" inner

            expectNaming
                (projectFindings "src/X/X.fsproj" (proj "<ProduceReferenceAssembly>false</ProduceReferenceAssembly>"))
                referenceAssembly

            expectNaming
                (projectFindings "src/X/X.fsproj" (proj "<ParallelCompilation>true</ParallelCompilation>"))
                parallelCompilation

            Expect.isEmpty (projectFindings "src/X/X.fsproj" (proj "")) "an ordinary project was reported"
    ]