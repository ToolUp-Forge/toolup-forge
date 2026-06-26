// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Cli.Tests.DispatchTests

open Expecto
open ToolUp.Cli.Dispatch

// A throwaway registry with a single-token leaf and a two-token leaf
// under a shared group, so resolution / group handling is exercised
// without depending on the real command set's exact shape.
let private leaf path summary = {
    Path = path
    Summary = summary
    Help = []
    Run = fun _ -> ExitOk
}

let private registry = [
    leaf [ "version" ] "v"
    leaf [ "docker"; "emit" ] "e"
    leaf [ "docker"; "ps" ] "p"
]

let tests =
    testList "Dispatch" [
        test "resolve picks the longest matching path" {
            match resolve registry [ "docker"; "emit"; "--force" ] with
            | Some(cmd, rest) ->
                Expect.equal cmd.Path [ "docker"; "emit" ] "longest path wins"
                Expect.equal rest [ "--force" ] "residual args follow the matched path"
            | None -> failtest "expected a resolution"
        }

        test "resolve returns None for an unknown leaf" {
            Expect.isNone (resolve registry [ "nope" ]) "no command matches"
        }

        test "resolve does not match a bare group token" {
            // `docker` alone is a group, not a leaf — resolve must miss it.
            Expect.isNone (resolve registry [ "docker" ]) "a group is not a leaf"
        }

        test "subcommandsOf lists a group's children" {
            let subs = subcommandsOf registry [ "docker" ]
            Expect.equal (List.length subs) 2 "docker has two subcommands"
        }

        test "isHelpToken recognises the help spellings" {
            Expect.isTrue (isHelpToken "--help") "--help"
            Expect.isTrue (isHelpToken "-h") "-h"
            Expect.isTrue (isHelpToken "help") "help"
            Expect.isFalse (isHelpToken "version") "a real command is not help"
        }

        test "run prints top-level help and exits 0 with no args" {
            Expect.equal (run registry [||]) ExitOk "no args → help → 0"
        }

        test "run treats --help as a 0-exit help request" {
            Expect.equal (run registry [| "--help" |]) ExitOk "--help → 0"
        }

        test "run dispatches a known leaf to its Run" {
            let mutable ran = false

            let reg = [
                {
                    Path = [ "go" ]
                    Summary = ""
                    Help = []
                    Run =
                        fun _ ->
                            ran <- true
                            ExitOk
                }
            ]

            Expect.equal (run reg [| "go" |]) ExitOk "leaf runs"
            Expect.isTrue ran "Run was invoked"
        }

        test "run returns a usage code for an unknown command" {
            Expect.equal (run registry [| "bogus" |]) ExitUsage "unknown → 2"
        }

        test "run returns a usage code for an incomplete group" {
            Expect.equal (run registry [| "docker" |]) ExitUsage "incomplete group → 2"
        }

        test "run renders group help on request with exit 0" {
            Expect.equal (run registry [| "docker"; "--help" |]) ExitOk "group + help → 0"
        }
    ]