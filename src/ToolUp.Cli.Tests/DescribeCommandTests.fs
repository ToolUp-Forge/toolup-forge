// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.DescribeCommandTests

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open Expecto
open ToolUp.Cli
open ToolUp.Cli.DescribeCommand
open ToolUp.Platform

// ─── Phase 782 — `toolup describe` ───────────────────────────────────
//
// The composition-root form is pinned at its two seams — the command line
// it runs (the root's own `--describe` startup mode, whose documents the
// platform pack pins against the reflection tools) and what it does with
// the child's exit and output file. The module form is pinned end to end:
// the CLI loads a built module and the SDK it was built against in an
// isolated context, and its `surface` must be byte-identical to
// `ModuleSurface.describeJson` over the same module computed here, in the
// test's own context — the descriptor the module contract pack reads
// (its server half).
//
// The module fixture is the `platformsdk-module-packaged` template's own
// server registration, compiled into this assembly from the template's
// source (see the fsproj), so the scaffold a consumer instantiates is the
// shape exercised. `samples/HelloWorld`'s module is the other fixture: it
// assembles its `ServerModule` inside the composition root and exports
// none, and the CLI must say so rather than emit an empty surface.

/// A second exported `ServerModule`, so the ambiguity path is reachable.
let otherModule: ServerModule = ServerModule.create "describe-fixture-other"

let private thisAssembly = Reflection.Assembly.GetExecutingAssembly().Location

let private packagedMember = "MODULE_NAMESPACE_ROOT.Server.serverModule"

let private helloWorldModule =
    Path.Combine(AppContext.BaseDirectory, "HelloWorld.Module.dll")

let private parseOk (args: string list) =
    match parse args with
    | Ok opts -> opts
    | Error e -> failtestf "expected %A to parse: %s" args e

let private scratchDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-describe-tests-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

let private parseTests =
    testList "parse" [
        test "--json is required, so a later text rendering is not a breaking change" {
            Expect.isError (parse []) "bare"
            Expect.equal (parseOk [ "--json" ]).Target (RootProject ".") "defaults to the project here"
        }

        test "one target at most" {
            Expect.isError (parse [ "--json"; "--project"; "a"; "--dll"; "b.dll" ]) "two targets"
        }

        test "--section and --panel belong to a root; --member and --version to a module" {
            Expect.isError (parse [ "--json"; "--module"; "m.dll"; "--section"; "composition" ]) "section on a module"
            Expect.isError (parse [ "--json"; "--member"; "X.y" ]) "member on a root"
            Expect.isOk (parse [ "--json"; "--module"; "m.dll"; "--member"; "X.y" ]) "member on a module"
        }

        test "repeatable sections, a panel and a timeout" {
            let opts =
                parseOk [
                    "--json"
                    "--dll"
                    "app.dll"
                    "--section"
                    "composition"
                    "--section"
                    "module-reach"
                    "--panel"
                    "rules"
                    "--timeout"
                    "30"
                ]

            Expect.equal opts.Sections [ "composition"; "module-reach" ] "sections"
            Expect.equal opts.Panel (Some "rules") "panel"
            Expect.equal opts.TimeoutSeconds 30 "timeout"
            Expect.isError (parse [ "--json"; "--timeout"; "soon" ]) "a bad timeout"
            Expect.isError (parse [ "--json"; "--frobnicate" ]) "an unknown flag"
        }
    ]

let private rootTests =
    testList "composition root" [
        test "a built root runs as `dotnet <dll> --describe`, modifiers passed through" {
            let opts =
                parseOk [ "--json"; "--dll"; "app.dll"; "--section"; "composition"; "--panel"; "rules" ]

            let file, args = rootInvocation opts "out.json"
            Expect.equal file "dotnet" "dotnet"

            Expect.equal
                args
                [
                    "app.dll"
                    "--describe"
                    "--describe-out"
                    "out.json"
                    "--describe-section"
                    "composition"
                    "--describe-panel"
                    "rules"
                ]
                "the root's own startup-mode flags"
        }

        test "a project root runs through `dotnet run`, with the flags after `--`" {
            let _, args = rootInvocation (parseOk [ "--json"; "--project"; "src/App" ]) "o.json"

            Expect.equal
                args
                [
                    "run"
                    "--project"
                    "src/App"
                    "--no-launch-profile"
                    "--"
                    "--describe"
                    "--describe-out"
                    "o.json"
                ]
                "dotnet run"
        }

        test "the description the root writes is printed, and the root's exit code is returned" {
            let writer: ProcessRunner =
                fun _ args _ ->
                    let out = args |> List.skipWhile ((<>) "--describe-out") |> List.item 1
                    File.WriteAllText(out, "{\"composition\":{}}")
                    Some 1

            use captured = new StringWriter()
            let code = runRoot writer captured (parseOk [ "--json"; "--dll"; "a.dll" ])

            Expect.equal code 1 "the verification report's exit code passes through"
            Expect.equal (captured.ToString().Trim()) "{\"composition\":{}}" "the document, verbatim"
        }

        test "a root that exits without a description, or never exits, is a runtime failure" {
            use captured = new StringWriter()
            let opts = parseOk [ "--json"; "--dll"; "a.dll" ]

            Expect.equal (runRoot (fun _ _ _ -> Some 0) captured opts) Dispatch.ExitRuntimeError "exit 0, no file"
            Expect.equal (runRoot (fun _ _ _ -> Some 3) captured opts) 3 "its own failure code is kept"
            Expect.equal (runRoot (fun _ _ _ -> None) captured opts) Dispatch.ExitRuntimeError "timed out"
            Expect.equal (captured.ToString()) "" "nothing on stdout"
        }
    ]

let private moduleTests =
    testList "module" [
        test "the packaged-module template's surface equals ModuleSurface.describeJson over the same module" {
            match describeModule thisAssembly (Some packagedMember) (globalPackagesFolder ()) with
            | Error e -> failtestf "describe refused: %s" e
            | Ok document ->
                use doc = JsonDocument.Parse document
                let root = doc.RootElement

                Expect.equal
                    (root.GetProperty("surface").GetRawText())
                    (ModuleSurface.describeJson MODULE_NAMESPACE_ROOT.Server.serverModule)
                    "byte-identical to the module's own SDK descriptor"

                Expect.equal (root.GetProperty("member").GetString()) packagedMember "names the value it read"
                Expect.equal (root.GetProperty("declaredBy").GetString()) "module" "declared by the module"
                Expect.equal (root.GetProperty("note").GetString()) ModuleDeclaredNote "says what it is not"
        }

        test "an assembly exporting several ServerModule values needs --member, and names them" {
            match describeModule thisAssembly None (globalPackagesFolder ()) with
            | Ok _ -> failtest "picked one of several without being told which"
            | Error e ->
                Expect.stringContains e "--member" "the remedy"
                Expect.stringContains e packagedMember "the candidates"
                Expect.stringContains e "otherModule" "every candidate"
        }

        test "samples/HelloWorld's module exports no ServerModule, and describe says so" {
            Expect.isTrue (File.Exists helloWorldModule) "the sample module is built beside the tests"

            match describeModule helloWorldModule None (globalPackagesFolder ()) with
            | Ok document -> failtestf "described a module that declares nothing: %s" document
            | Error e -> Expect.stringContains e "exports no ServerModule value" "the honest absence"
        }

        test "a module reference resolves as an assembly path, a .nupkg, or a package id" {
            let scratch = scratchDir ()

            try
                let packages = Path.Combine(scratch, "packages")
                let lib = Path.Combine(packages, "acme.sales", "1.2.0", "lib", "net10.0")
                Directory.CreateDirectory lib |> ignore
                File.WriteAllText(Path.Combine(lib, "Acme.Sales.dll"), "")

                Expect.equal
                    (resolveModuleAssembly packages (Path.Combine(scratch, "x")) "Acme.Sales" None)
                    (Ok(Path.Combine(lib, "Acme.Sales.dll")))
                    "package id"

                let nupkg = Path.Combine(scratch, "Acme.Sales.1.2.0.nupkg")

                do
                    use zip = ZipFile.Open(nupkg, ZipArchiveMode.Create)
                    zip.CreateEntry("lib/net10.0/Acme.Sales.dll") |> ignore
                    zip.CreateEntry("Acme.Sales.nuspec") |> ignore

                let extracted = Path.Combine(scratch, "extracted")

                Expect.equal
                    (resolveModuleAssembly packages extracted nupkg None)
                    (Ok(Path.Combine(extracted, "lib", "net10.0", "Acme.Sales.dll")))
                    ".nupkg"

                Expect.isError (resolveModuleAssembly packages extracted "Not.There" None) "an unknown id"
                Expect.isError (resolveModuleAssembly packages extracted "missing.dll" None) "a missing path"
            finally
                Directory.Delete(scratch, true)
        }

        test "the describe command reports a module failure as a runtime error, a bad invocation as usage" {
            Expect.equal (command.Run [ "--module"; helloWorldModule; "--json" ]) Dispatch.ExitRuntimeError "runtime"
            Expect.equal (command.Run [ "--module"; helloWorldModule ]) Dispatch.ExitUsage "usage"
        }
    ]

let tests =
    testList "Phase 782 — toolup describe" [ parseTests; rootTests; moduleTests ]