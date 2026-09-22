module ToolUp.Scheduling.Tests.InProcess.InMemoryCalendarBridgeTests

open System
open Expecto
open ToolUp.Scheduling.ICalendarBridge
open ToolUp.Scheduling.Tests.Contracts
open ToolUp.Scheduling.Tests.InProcess.InMemoryCalendarBridge

// ─── Binding 1 of the ICalendarBridge contract pack ─────────────────
//
// The in-memory fake. It exists so the pack's laws are known to be
// satisfiable by a bridge that is nothing but the seam — if a law fails
// here it is a law about the SEAM, and if it fails only on the CalDAV
// binding it is a law about CalDAV.

[<Literal>]
let private CalendarId = "cal-in-memory"

let tests =
    let factory () : ICalendarBridgeContract.BridgeHarness =
        let bridge = InMemoryCalendarBridge [ CalendarId ]

        {
            Bridge = bridge
            Link = {
                ScopeId = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                ResourceId = "room-101"
                ExternalCalendarId = CalendarId
                UserId = "alice"
            }
            MissingCalendarId = "cal-that-does-not-exist"
            ExternalEdit = fun eventId transform at -> bridge.ExternalEdit(CalendarId, eventId, transform, at)
        }

    ICalendarBridgeContract.tests "in-memory" factory