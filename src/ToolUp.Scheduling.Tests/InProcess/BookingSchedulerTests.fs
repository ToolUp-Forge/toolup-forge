module ToolUp.Scheduling.Tests.InProcess.BookingSchedulerTests

open System
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Scheduling.IBookingScheduler
open ToolUp.Scheduling.BookingScheduler
open ToolUp.Scheduling.Tests.InProcess.InMemoryEntityStore
open ToolUp.Scheduling.Tests.InProcess.InMemoryEventStore
open ToolUp.Scheduling.Tests.Contracts

/// Bind the IBookingScheduler contract pack to the default
/// BookingScheduler default impl over the in-memory IEntityStore +
/// IEventStore stubs in this test project. A future distributed
/// companion (Akka.NET / Orleans) would bind the same pack against
/// its own factory and prove portability — same shape as
/// IEntityStoreContract / IJobSchedulerContract in
/// ToolUp.Platform.Tests.
let tests =
    let factory () =
        let entityStore = InMemoryEntityStore() :> IEntityStore
        let eventStore = InMemoryEventStore()

        let scheduler =
            BookingScheduler(entityStore, eventStore :> IEventStore) :> IBookingScheduler

        let scopeId = "team-test-" + Guid.NewGuid().ToString("N").Substring(0, 8)
        scheduler, (fun () -> eventStore.Events), scopeId

    IBookingSchedulerContract.tests "BookingScheduler (in-memory)" factory