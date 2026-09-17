// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.Program

open Expecto
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.Platform.Build.Tests" [
        SbomTests.tests
        PackagedModuleConformanceTests.tests
        // Phase 213 — the Lighthouse / Core-Web-Vitals budget gate: the
        // budget parser's refusals and the check over committed report
        // fixtures. No browser, no server, no network.
        CoreWebVitalsBudgetTests.tests
        // Phase 192 — the cold-start / hot-path perf-budget gate: the
        // budget parser's refusals, the check over committed measurement
        // fixtures (including the three shapes that look like a pass and
        // are not), and the shipped budget's own coherence. No process,
        // no port, no clock.
        PerfBudgetTests.tests
        // Phase 754 — the template gate's stranger's-package guard: a
        // declared gate id the repo's own pack did not emit. Pure, so
        // both directions are probed without packing anything.
        TemplateGateTests.tests
        // Phase 326 — the ToolUp.Sdk meta-manifest lists exactly the
        // package ids the Publish target pushes. Pure file scanning over
        // the checkout; no build output, no network.
        SdkManifestTests.tests
        // Phase 184 — the fresh-machine published-package smoke gate's
        // rules: the probe closure, the version under test, the
        // index-availability read, the outside-the-repo invariant and the
        // workflow lint, each proven in both directions. Pure over
        // fixtures plus the committed publish-nuget.yml; no network.
        PublishedSmokeTests.tests
        // Phase 259 — every replaceable seam carries a conformance pack,
        // and every pack is run by an implementation. Pure file scanning
        // over the checkout; no build output, no network.
        ConformanceCoverageTests.tests
        // Phase 260 — the release bump is derived from the api-baselines
        // diff, not typed by hand: the per-package classification, the
        // lockstep roll-up, both SemVer policy tables (0.x and 1.x), and
        // the under-bump refusal. Pure over strings; no git, no network.
        SemVerBumpTests.tests
        // Phase 735 — the VerifyAll gate's admission rules (free / held /
        // stale holder / abandoned / opt-out) over minted mutex names, and
        // the corpus-anchor memoisation's constant-spawn property. Named
        // OS mutexes and a handful of `git` subprocesses; no network.
        VerifyGateTests.tests
        CorpusAnchorTests.tests
        // Phase 183 — the consumer codemod for the 0.x breaking renames:
        // golden-file tests over the fixture consumer tree (every deterministic
        // rename lands, the non-mechanical sites are reported not guessed, a
        // second run is a no-op, BOM + CRLF survive). Pure over a scratch copy
        // of the fixtures; no build, no tool restore, no process.
        CodemodTests.tests
        // Phase 257 — the v1.0 readiness scorecard: a known-good fixture
        // scores all-green, one fixture per cause scores that row red,
        // every unavailable input is an explicit not-yet row, and the
        // committed inputs parse. Pure over strings; no git, no network.
        V1ReadinessTests.tests
    ]

/// Phase 722 — the registered list plus the guard that makes an
/// unregistered `[<Tests>]` binding fail loudly instead of vanishing:
/// this pack runs an explicitly-enumerated list, not Expecto's
/// `[<Tests>]` auto-discovery, so an attributed binding absent from the
/// list above would silently never run.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests