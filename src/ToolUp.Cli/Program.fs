// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `dotnet toolup` host entry point. The registry is the one place
/// commands are wired; later phases append their leaf (Phase 166
/// `stamp`, Phase 168 `module add/remove`) here without touching the
/// dispatcher or sibling commands.
module ToolUp.Cli.Program

open ToolUp.Cli

[<EntryPoint>]
let main argv =
    let commands = [
        VersionCommand.command
        DockerEmitCommand.command
        StampCommand.command
        ModuleCommand.addCommand
        ModuleCommand.removeCommand
        MembershipsDoctorCommand.command
    ]

    Dispatch.run commands argv