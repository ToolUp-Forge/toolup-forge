// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup version` — the smallest real command, present so the host has
/// at least one end-to-end-runnable leaf (no arguments, no IO surprises)
/// to validate dispatch + help wiring against.
module ToolUp.Cli.VersionCommand

open System.Reflection
open ToolUp.Cli.Dispatch

/// The tool's informational version (from the packed assembly metadata),
/// with any `+<git-sha>` build-metadata suffix trimmed for display.
let private toolVersion () =
    let asm = Assembly.GetExecutingAssembly()

    let raw =
        match asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
        | null -> asm.GetName().Version.ToString()
        | attr -> attr.InformationalVersion

    match raw.IndexOf '+' with
    | -1 -> raw
    | i -> raw.Substring(0, i)

let command = {
    Path = [ "version" ]
    Summary = "Print the toolup CLI version."
    Help = [
        "Usage: toolup version"
        ""
        "Prints the version of the installed toolup CLI tool."
    ]
    Run =
        fun _ ->
            printfn "toolup %s" (toolVersion ())
            ExitOk
}