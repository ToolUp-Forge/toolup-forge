module ToolUp.Scheduling.Tests.Program

open Expecto
open ToolUp.Scheduling.Tests.InProcess

let allTests =
    testList "ToolUp.Scheduling.Tests" [
        RecurrenceExpanderTests.tests
        iCalendarTests.tests
        BookingConflictDetectorTests.tests
        BookingSchedulerTests.tests
        WorkedExampleTests.tests
    ]

// Sequenced by default — Expecto deadlocks when parallel tests write to
// the console (the subject's own ConsoleLogger / compose warnings are enough).
// `--parallel` still overrides. See docs/platform/testing-conventions.md
// § "Every Expecto pack runs sequenced by default". (Phase 617.)
[<EntryPoint>]
let main argv =
    runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests