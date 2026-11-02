// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.PublicApiPack

open System.Reflection
open Expecto
open ToolUp.Platform.Tests.InProcess
open ToolUp.Platform.Tests.Support

/// The explicitly-enumerated list this pack runs: the public-API approval gate, over the built
/// assemblies of every packable project. Which list is in which pack, and why:
/// docs/migrations/1194-platform-pack-partition.md. `[<Tests>]` alone does NOT get a list into this
/// run, which is why the registration guard runs over it.
let private registeredTests =
    testList "ToolUp.Platform.Tests.PublicApi" [ PublicApiApprovalTests.tests ]

let private deliberatelyUnregistered: TestRegistrationGuard.Exemption list = []

/// The pack's measured slow lists. The rule, and how to re-derive it, are
/// beside the residue pack's declaration in src/ToolUp.Platform.Tests/Program.fs.
let private laneDeclaration: TestLaneFilter.LaneDeclaration = {
    Slow = []
    // No list here is audited to the `pure` standard (no filesystem, process,
    // socket or environment variable), so that lane runs the guard alone.
    Pure = []
}

/// The guard's floor is a LOWER BOUND on the `[<Tests>]` bindings its sweep
/// finds (about 85 % of them), so adding a list never needs an edit here.
[<EntryPoint>]
let main argv =
    PackEntry.run
        {
            Row = "Platform.PublicApi"
            Assembly = Assembly.GetExecutingAssembly()
            GuardFloor = 1
            Exemptions = deliberatelyUnregistered
            Lanes = laneDeclaration
            Registered = registeredTests
        }
        argv