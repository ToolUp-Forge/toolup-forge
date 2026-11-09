module ToolUp.Platform.Tests.Contracts.IEventStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra

/// Contract test list for any `IEventStore` implementation. Factory
/// must produce a fresh, empty store per test. Tests GUID-suffix their
/// scope IDs so shared-state implementations stay isolated.
///
/// Scope isolation is the load-bearing property: events from team A
/// must never be returned by a query scoped to team B. Every read
/// path asserts this.
let tests (name: string) (factory: unit -> IEventStore) =
    let uniqueScope () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "team-" + suffix

    let makeEvent (scopeId: string) (eventType: string) (sourceModule: string) (offsetMinutes: float) : ModuleEvent =
        let now = DateTime.UtcNow.AddMinutes offsetMinutes

        {
            Id = Guid.NewGuid()
            OccurredAt = now
            ScopeId = scopeId
            SourceModule = sourceModule
            EventType = eventType
            Payload = "{}"
        }

    testList $"{name} — IEventStore contract" [
        testCaseAsync "ReadAll on empty store returns []"
        <| async {
            let store = factory ()
            let! events = store.ReadAll(uniqueScope ())
            Expect.isEmpty events "fresh store is empty"
        }

        testCaseAsync "Write then ReadAll returns the event"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let event = makeEvent scope "AnalysisStarted" "sku" 0.0

            do! EventStore.writeOrRaise store event
            let! events = store.ReadAll scope

            Expect.hasLength events 1 "exactly one event"
            Expect.equal events.Head.Id event.Id "same event back"
        }

        testCaseAsync "ReadAll is reverse-chronological by OccurredAt"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()
            let older = makeEvent scope "A" "m1" -10.0
            let middle = makeEvent scope "B" "m2" -5.0
            let newer = makeEvent scope "C" "m3" 0.0

            // Intentionally write out of order — ordering should come
            // from OccurredAt, not insertion time.
            do! EventStore.writeOrRaise store middle
            do! EventStore.writeOrRaise store older
            do! EventStore.writeOrRaise store newer

            let! events = store.ReadAll scope
            let ids = events |> List.map _.Id

            Expect.equal ids [ newer.Id; middle.Id; older.Id ] "newer events first"
        }

        testCaseAsync "Scope isolation — ReadAll does not return events from other scopes"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scopeA "Private" "secret" 0.0)

            let! eventsInB = store.ReadAll scopeB
            Expect.isEmpty eventsInB "scope B never sees scope A's events"
        }

        testCaseAsync "ReadByType returns only matching event type"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scope "AnalysisStarted" "m" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scope "AnalysisCompleted" "m" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scope "AnalysisStarted" "m" -1.0)

            let! started = store.ReadByType(scope, "AnalysisStarted")
            let! completed = store.ReadByType(scope, "AnalysisCompleted")

            Expect.hasLength started 2 "two AnalysisStarted events"
            Expect.hasLength completed 1 "one AnalysisCompleted event"

            for e in started do
                Expect.equal e.EventType "AnalysisStarted" "only the asked-for type"
        }

        testCaseAsync "Scope isolation — ReadByType"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scopeA "AnalysisStarted" "m" 0.0)

            let! events = store.ReadByType(scopeB, "AnalysisStarted")
            Expect.isEmpty events "scope B never sees scope A's type queries"
        }

        testCaseAsync "ReadBySource returns only matching source module"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scope "E" "SkuAnalysis" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scope "E" "PriceElasticity" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scope "E" "SkuAnalysis" -1.0)

            let! sku = store.ReadBySource(scope, "SkuAnalysis")
            let! price = store.ReadBySource(scope, "PriceElasticity")

            Expect.hasLength sku 2 "two SkuAnalysis events"
            Expect.hasLength price 1 "one PriceElasticity event"

            for e in sku do
                Expect.equal e.SourceModule "SkuAnalysis" "only the asked-for source"
        }

        testCaseAsync "Scope isolation — ReadBySource"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scopeA "E" "SkuAnalysis" 0.0)

            let! events = store.ReadBySource(scopeB, "SkuAnalysis")
            Expect.isEmpty events "scope B never sees scope A's source queries"
        }

        testCaseAsync "Empty-result queries return [] (ReadByType with no matches)"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scope "OnlyOne" "m" 0.0)

            let! events = store.ReadByType(scope, "OtherType")
            Expect.isEmpty events "unknown type returns empty, not Error"
        }

        testCaseAsync "ListScopes on empty store returns []"
        <| async {
            let store = factory ()
            let! scopes = store.ListScopes()
            Expect.isEmpty scopes "fresh store has no scopes"
        }

        testCaseAsync "ListScopes returns every scope with at least one event"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()
            let scopeC = uniqueScope ()

            do! EventStore.writeOrRaise store (makeEvent scopeA "E" "m" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scopeA "E" "m" -1.0)
            do! EventStore.writeOrRaise store (makeEvent scopeB "E" "m" 0.0)
            do! EventStore.writeOrRaise store (makeEvent scopeC "E" "m" 0.0)

            let! scopes = store.ListScopes()
            let asSet = Set.ofList scopes

            Expect.equal asSet (Set.ofList [ scopeA; scopeB; scopeC ]) "all three scopes enumerated"
        }

        testCaseAsync "ListScopes deduplicates — one entry per scope regardless of event count"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            for i in 0..9 do
                do! EventStore.writeOrRaise store (makeEvent scope $"E{i}" "m" (float -i))

            let! scopes = store.ListScopes()
            let matching = scopes |> List.filter (fun s -> s = scope)
            Expect.hasLength matching 1 "scope enumerated exactly once across 10 events"
        }
    ]

/// The failing-backend arm. `factory` builds the implementation
/// over a backend that refuses every write — the implementation's own
/// failure mode, not a double of the interface. A store with no backend to
/// fail (a purely in-memory one) has no honest binding for this arm and
/// does not bind it.
///
/// What it holds every implementation to: a write that did not persist
/// answers `EventStoreError.WriteFailed` naming the event's scope and id —
/// never `Ok`, never an exception — and nothing a later read returns.
let failingBackendTests (name: string) (factory: unit -> IEventStore) =
    let event (scopeId: string) : ModuleEvent = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = scopeId
        SourceModule = "contract"
        EventType = "FailingBackendProbe"
        Payload = "{}"
    }

    testList $"{name} — IEventStore contract, failing backend" [
        testCaseAsync "a write the backend refused answers WriteFailed, naming the scope and the event"
        <| async {
            let store = factory ()
            let scope = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let evt = event scope

            let! written = Async.Catch(store.Write evt)

            match written with
            | Choice1Of2(Error(EventStoreError.WriteFailed(scopeId, eventId, location, reason))) ->
                Expect.equal scopeId scope "the scope is the event's"
                Expect.equal eventId evt.Id "the event is named"
                Expect.isNotEmpty location "what was not written is named"
                Expect.isNotEmpty reason "the backend's account is carried"
            | Choice1Of2(Ok()) -> failtest "a write the backend refused must not answer Ok"
            | Choice2Of2 ex -> failtestf "a refused write is data, not an exception: %s" ex.Message
        }

        testCaseAsync "a refused write leaves nothing a read returns"
        <| async {
            let store = factory ()
            let scope = "team-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let! _ = store.Write(event scope)
            let! events = store.ReadAll scope
            Expect.isEmpty events "an Error persisted nothing"
        }
    ]