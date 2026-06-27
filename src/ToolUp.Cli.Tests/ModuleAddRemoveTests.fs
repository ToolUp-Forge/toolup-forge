// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.ModuleAddRemoveTests

open System
open System.IO
open Expecto
open ToolUp.Cli
open ToolUp.Cli.Dispatch

// ─── Phase 168 — transactional module add/remove ────────────────────────
//
// The acceptance contract: `module add` produces a registered, scaffolded
// module; `module remove` restores the tree byte-for-byte. These tests
// build a minimal fixture app (a server fsproj + a client Client.fs, each
// carrying the toolup:modules marker), snapshot every file, run add then
// remove, and assert the tree is byte-identical to the snapshot.

let private newAppRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-mod-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(Path.Combine(root, "src", "App-Server")) |> ignore
    Directory.CreateDirectory(Path.Combine(root, "src", "App-Client")) |> ignore

    let serverProj = Path.Combine(root, "src", "App-Server", "App-Server.fsproj")

    File.WriteAllText(
        serverProj,
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <!-- toolup:modules -->\n  </ItemGroup>\n</Project>\n"
    )

    let clientFs = Path.Combine(root, "src", "App-Client", "Client.fs")

    File.WriteAllText(clientFs, "module App.Client\n\nlet modules = [\n    // toolup:modules\n]\n")

    root, serverProj, clientFs

/// Snapshot every file under `root` as (relative-path → bytes).
let private snapshot (root: string) : Map<string, byte[]> =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.map (fun f -> Path.GetRelativePath(root, f), File.ReadAllBytes f)
    |> Map.ofSeq

let tests =
    testList "Phase 168 — module add/remove" [

        test "add scaffolds the four-file module and registers it" {
            let root, serverProj, clientFs = newAppRoot ()

            try
                let code =
                    ModuleCommand.addCommand.Run [
                        "--name"
                        "Sales"
                        "--app-root"
                        root
                        "--register"
                        serverProj
                        "--register"
                        clientFs
                    ]

                Expect.equal code ExitOk "add succeeds"

                let modDir = Path.Combine(root, "Modules", "Sales")

                for f in
                    [
                        "SharedTypes.fs"
                        "Server.fs"
                        "ClientModel.fs"
                        "ClientView.fs"
                        "Sales.fsproj"
                        "Sales.Client.props"
                    ] do
                    Expect.isTrue (File.Exists(Path.Combine(modDir, f))) (sprintf "%s scaffolded" f)

                // Token substitution: the scaffolded module is named Sales,
                // not MyModule.
                let shared = File.ReadAllText(Path.Combine(modDir, "SharedTypes.fs"))
                Expect.stringContains shared "module Sales.SharedTypes" "module renamed"
                Expect.isFalse (shared.Contains "MyModule") "no MyModule token left"

                // Registrations landed at the markers.
                Expect.stringContains
                    (File.ReadAllText serverProj)
                    "Sales.fsproj"
                    "server fsproj got a ProjectReference"

                Expect.stringContains
                    (File.ReadAllText clientFs)
                    "Sales.ClientView.register()"
                    "client got a register() call"
            finally
                Directory.Delete(root, true)
        }

        test "add → remove restores the tree byte-for-byte" {
            let root, serverProj, clientFs = newAppRoot ()

            try
                let before = snapshot root

                ModuleCommand.addCommand.Run [
                    "--name"
                    "Sales"
                    "--app-root"
                    root
                    "--register"
                    serverProj
                    "--register"
                    clientFs
                ]
                |> ignore
                // The tree changed (module dir + registrations + ledger).
                Expect.notEqual (snapshot root |> Map.count) (Map.count before) "add mutated the tree"

                let removeCode =
                    ModuleCommand.removeCommand.Run [ "--name"; "Sales"; "--app-root"; root ]

                Expect.equal removeCode ExitOk "remove succeeds"

                let after = snapshot root

                Expect.equal
                    (after |> Map.toList |> List.map fst)
                    (before |> Map.toList |> List.map fst)
                    "same file set after round-trip"

                for KeyValue(path, bytes) in before do
                    Expect.equal (after |> Map.find path) bytes (sprintf "%s byte-identical after round-trip" path)
            finally
                Directory.Delete(root, true)
        }

        test "add refuses when a --register file lacks the marker (transactional pre-check)" {
            let root, serverProj, _ = newAppRoot ()

            try
                let noMarker = Path.Combine(root, "src", "App-Server", "NoMarker.fsproj")
                File.WriteAllText(noMarker, "<Project></Project>\n")

                let code =
                    ModuleCommand.addCommand.Run [ "--name"; "Sales"; "--app-root"; root; "--register"; noMarker ]

                Expect.equal code ExitRuntimeError "refuses without a marker"

                Expect.isFalse
                    (Directory.Exists(Path.Combine(root, "Modules", "Sales")))
                    "nothing scaffolded on a refused add"
            finally
                Directory.Delete(root, true)
        }

        test "two adds against the same marker each append their own line (append-only)" {
            let root, serverProj, _ = newAppRoot ()

            try
                ModuleCommand.addCommand.Run [ "--name"; "Sales"; "--app-root"; root; "--register"; serverProj ]
                |> ignore

                ModuleCommand.addCommand.Run [ "--name"; "Inventory"; "--app-root"; root; "--register"; serverProj ]
                |> ignore

                let proj = File.ReadAllText serverProj
                Expect.stringContains proj "Sales.fsproj" "first module registered"
                Expect.stringContains proj "Inventory.fsproj" "second module registered"

                // Removing one leaves the other intact.
                ModuleCommand.removeCommand.Run [ "--name"; "Sales"; "--app-root"; root ]
                |> ignore

                let proj2 = File.ReadAllText serverProj
                Expect.isFalse (proj2.Contains "Sales.fsproj") "removed module's registration gone"
                Expect.stringContains proj2 "Inventory.fsproj" "sibling registration untouched"
            finally
                Directory.Delete(root, true)
        }

        test "remove without a ledger is a clean runtime error" {
            let root, _, _ = newAppRoot ()

            try
                Expect.equal
                    (ModuleCommand.removeCommand.Run [ "--name"; "Ghost"; "--app-root"; root ])
                    ExitRuntimeError
                    "no ledger → error, no crash"
            finally
                Directory.Delete(root, true)
        }
    ]