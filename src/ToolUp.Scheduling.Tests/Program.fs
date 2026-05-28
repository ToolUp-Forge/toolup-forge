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

[<EntryPoint>]
let main argv = runTestsWithCLIArgs [] argv allTests