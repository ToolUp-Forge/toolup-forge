// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `Flags.parse`: the forge driver's one reading of its command line. Each
/// option a target reads is pinned here, so a target that grows an option
/// declares it in `VerifyGate.driverFlags` or the driver refuses it by name.
module ToolUp.Platform.Build.Tests.FlagsTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Forge

let private parse (argv: string list) =
    match Flags.parse VerifyGate.driverFlags argv with
    | Ok flags -> flags
    | Error why -> failtestf "expected %A to parse, got: %s" argv why

let private refusal (argv: string list) =
    match Flags.parse VerifyGate.driverFlags argv with
    | Ok flags -> failtestf "expected %A to be refused, got %A" argv flags
    | Error why -> why

/// Every switch a forge target reads, with the target that reads it.
let private switches = [
    "--check", "AddHeaders / Codemod / GenerateSdkManifest / Setup / Changelog"
    "--propose", "VerifySemVerBump"
    "--update-baseline", "VerifyDocSnippets"
    "--require-ready", "V1Readiness"
    "--draft", "Changelog"
    "--fail-on-dead", "DeadCodeReport"
    "--verbose", "DeadCodeReport"
    "--json", "DeadCodeReport"
]

/// Every valued option a forge target reads, with the target that reads it.
let private valued = [
    "--module", "execute"
    "--root", "VerifyUploadResults / VerifyScopeLiterals / VerifyServiceConfigFold"
    "--assemblies", "MergeBaselines"
    "--base", "Select"
    "--head", "Select"
    "--paths", "Select"
    "--declared", "Select"
    "--out", "Select"
    "--release", "RefreshFuaranUiFloors"
    "--ui-version", "VerifyFuaranCoBuild"
    "--toolup-version", "VerifyFuaranCoBuild"
    "--props", "VerifyFuaranCoResolution"
    "--snapshot", "VerifyFuaranCoResolution"
    "--boundary", "VerifyFuaranCoResolution"
]

[<Tests>]
let tests =
    testList "Flags.parse" [

        testList "every switch" [
            for name, target in switches do
                test (sprintf "%s (%s)" name target) {
                    let flags = parse [ "Target"; name ]
                    Expect.isTrue (Flags.has name flags) "the switch is read"
                    Expect.equal flags.Target "Target" "the target is unchanged"
                    Expect.isEmpty flags.Positionals "a switch consumes no argument"
                }
        ]

        testList "every valued option" [
            for name, target in valued do
                test (sprintf "%s <value> (%s)" name target) {
                    let flags = parse [ "Target"; name; "v1" ]
                    Expect.equal (Flags.value name flags) (Some "v1") "the value is the next argument"
                    Expect.isEmpty flags.Positionals "the value is not a positional"
                }
        ]

        test "an unknown option is refused by name" {
            let why = refusal [ "VerifyAll"; "--chek" ]
            Expect.stringContains why "--chek" "names the option"
            Expect.stringContains why "--check" "and lists what is accepted"
        }

        test "a valued option with no value is refused" {
            Expect.stringContains (refusal [ "MergeBaselines"; "--assemblies" ]) "needs a value" "says why"
        }

        test "the target is the first argument when it is not an option, else Run — execute's rule" {
            Expect.equal (parse [ "VerifyAll"; "--check" ]).Target "VerifyAll" "first argument"
            Expect.equal (parse [ "--module"; "x" ]).Target "Run" "an option first means Run"
            Expect.equal (parse []).Target "Run" "no arguments means Run"

            Expect.equal
                (VerifyGate.requestedTarget [| "--module"; "x"; "VerifyAll" |])
                "Run"
                "the gate reads the same rule"
        }

        test "non-option arguments after the target are positionals, in order" {
            let flags = parse [ "Codemod"; "--check"; "src/consumer"; "more" ]
            Expect.equal flags.Positionals [ "src/consumer"; "more" ] "Codemod reads the first"
            Expect.isTrue (Flags.has "--check" flags) "beside the switch"
        }

        test "the last occurrence of a valued option wins" {
            Expect.equal (parse [ "Select"; "--out"; "a"; "--out"; "b" ] |> Flags.value "--out") (Some "b") "last wins"
        }

        test "lenient reads an undeclared option as a switch and never refuses" {
            let flags = Flags.lenient Flags.sdk [ "Run"; "--consumer-flag"; "--module" ]
            Expect.isTrue (Flags.has "--consumer-flag" flags) "a consumer's own option"
            Expect.isTrue (Flags.has "--module" flags) "a valued option with no value"
            Expect.equal (Flags.value "--module" flags) None "has no value"
        }
    ]