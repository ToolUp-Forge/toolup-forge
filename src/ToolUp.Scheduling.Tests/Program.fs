module ToolUp.Scheduling.Tests.Program

open Expecto
open ToolUp.Scheduling.Tests.InProcess
open System.Reflection
open ToolUp.Platform.Tests.Support

let private registeredTests =
    testList "ToolUp.Scheduling.Tests" [
        RecurrenceExpanderTests.tests
        iCalendarTests.tests
        BookingConflictDetectorTests.tests
        BookingSchedulerTests.tests
        WorkedExampleTests.tests
        // Phase 20a — the calendar-bridge seam: the contract pack bound
        // twice (the in-memory fake and the real CalDAV bridge over a
        // stub server), plus the sync engine's own behaviour.
        InMemoryCalendarBridgeTests.tests
        CalDAVCalendarBridgeTests.tests
        CalendarSyncTests.tests
        // Phase 831 — the Microsoft Graph bridge: the pack's third binding,
        // plus tokens, delta paging and the subscription route.
        MicrosoftGraphCalendarBridgeTests.tests
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