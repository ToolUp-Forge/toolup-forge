// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.OpenXml.SvgRasterizer.Skia.Tests.Program

open System.Reflection
open Expecto
open ToolUp.OpenXml.SvgRasterizer.Skia.Tests.InProcess
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.OpenXml.SvgRasterizer.Skia.Tests" [ RasterizerTests.tests ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

/// Sequenced by default, for the reason `docs/platform/testing-
/// conventions.md` records: Expecto replaces the console writers with a
/// synchronized pair, and a parallel test writing through them can
/// deadlock against the real console stream lock. `--parallel`
/// overrides it.
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests