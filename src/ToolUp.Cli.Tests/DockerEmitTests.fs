// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.DockerEmitTests

open System.IO
open Expecto
open ToolUp.Cli
open ToolUp.Cli.Dispatch

let private tempDir () =
    let path =
        Path.Combine(Path.GetTempPath(), "toolup-cli-test-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory path |> ignore
    path

let private validArgs (outDir: string) = [
    "--server-project"
    "MyApp-Server"
    "--server-dll"
    "MyApp-Server"
    "--image-name"
    "myapp"
    "--output-dir"
    outDir
]

let tests =
    testList "DockerEmit" [
        test "substitute replaces known tokens and leaves unknown intact" {
            let out =
                Templating.substitute [ "image-name", "myapp" ] "image: {{image-name}}:dev {{other}}"

            Expect.equal out "image: myapp:dev {{other}}" "only bound tokens are replaced"
        }

        test "bindings rejects a missing required option" {
            let opts: DockerEmitCommand.Options = {
                ServerProject = None
                ServerDll = Some "x"
                ImageName = Some "y"
                HostPort = "8080"
                OutputDir = "."
                Force = false
            }

            Expect.isError (DockerEmitCommand.bindings opts) "server-project missing → Error"
        }

        test "bindings rejects a non-integer host-port" {
            let opts: DockerEmitCommand.Options = {
                ServerProject = Some "a"
                ServerDll = Some "b"
                ImageName = Some "c"
                HostPort = "not-a-number"
                OutputDir = "."
                Force = false
            }

            Expect.isError (DockerEmitCommand.bindings opts) "bad host-port → Error"
        }

        test "bindings yields all four tokens when valid" {
            let opts: DockerEmitCommand.Options = {
                ServerProject = Some "MyApp-Server"
                ServerDll = Some "MyApp-Server"
                ImageName = Some "myapp"
                HostPort = "9000"
                OutputDir = "."
                Force = false
            }

            match DockerEmitCommand.bindings opts with
            | Ok tokens ->
                Expect.equal (List.length tokens) 4 "four tokens"

                Expect.equal
                    (tokens |> List.tryFind (fst >> (=) "host-port") |> Option.map snd)
                    (Some "9000")
                    "host-port bound"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        test "emit writes the four artefacts with tokens substituted" {
            let dir = tempDir ()

            try
                let code = DockerEmitCommand.command.Run(validArgs dir)
                Expect.equal code ExitOk "emit succeeds"

                for name in [ "Dockerfile"; ".dockerignore"; "healthcheck.sh"; "compose.yml" ] do
                    Expect.isTrue (File.Exists(Path.Combine(dir, name))) (sprintf "%s emitted" name)

                let compose = File.ReadAllText(Path.Combine(dir, "compose.yml"))
                Expect.stringContains compose "myapp:dev" "image token substituted in compose.yml"
                Expect.isFalse (compose.Contains "{{image-name}}") "no unsubstituted tokens remain"

                let dockerfile = File.ReadAllText(Path.Combine(dir, "Dockerfile"))
                Expect.stringContains dockerfile "MyApp-Server" "server-project token substituted in Dockerfile"
            finally
                Directory.Delete(dir, true)
        }

        test "emit refuses to overwrite without --force, then obeys --force" {
            let dir = tempDir ()

            try
                Expect.equal (DockerEmitCommand.command.Run(validArgs dir)) ExitOk "first emit"
                // Second emit without --force must refuse (runtime error).
                Expect.equal (DockerEmitCommand.command.Run(validArgs dir)) ExitRuntimeError "clash without --force → 1"
                // With --force it overwrites and succeeds.
                Expect.equal (DockerEmitCommand.command.Run(validArgs dir @ [ "--force" ])) ExitOk "force → 0"
            finally
                Directory.Delete(dir, true)
        }

        test "emit reports a usage error for missing required args" {
            Expect.equal (DockerEmitCommand.command.Run [ "--image-name"; "x" ]) ExitUsage "missing required → 2"
        }
    ]