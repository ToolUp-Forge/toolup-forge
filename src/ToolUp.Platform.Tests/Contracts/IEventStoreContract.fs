module ToolUp.Platform.Tests.Contracts.IEventStoreContract

open System
open Expecto
open ToolUp.Platform

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

            do! store.Write event
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
            do! store.Write middle
            do! store.Write older
            do! store.Write newer

            let! events = store.ReadAll scope
            let ids = events |> List.map _.Id

            Expect.equal ids [ newer.Id; middle.Id; older.Id ] "newer events first"
        }

        testCaseAsync "Scope isolation — ReadAll does not return events from other scopes"
        <| async {
            let store = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            do! store.Write(makeEvent scopeA "Private" "secret" 0.0)

            let! eventsInB = store.ReadAll scopeB
            Expect.isEmpty eventsInB "scope B never sees scope A's events"
        }

        testCaseAsync "ReadByType returns only matching event type"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! store.Write(makeEvent scope "AnalysisStarted" "m" 0.0)
            do! store.Write(makeEvent scope "AnalysisCompleted" "m" 0.0)
            do! store.Write(makeEvent scope "AnalysisStarted" "m" -1.0)

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

            do! store.Write(makeEvent scopeA "AnalysisStarted" "m" 0.0)

            let! events = store.ReadByType(scopeB, "AnalysisStarted")
            Expect.isEmpty events "scope B never sees scope A's type queries"
        }

        testCaseAsync "ReadBySource returns only matching source module"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! store.Write(makeEvent scope "E" "SkuAnalysis" 0.0)
            do! store.Write(makeEvent scope "E" "PriceElasticity" 0.0)
            do! store.Write(makeEvent scope "E" "SkuAnalysis" -1.0)

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

            do! store.Write(makeEvent scopeA "E" "SkuAnalysis" 0.0)

            let! events = store.ReadBySource(scopeB, "SkuAnalysis")
            Expect.isEmpty events "scope B never sees scope A's source queries"
        }

        testCaseAsync "Empty-result queries return [] (ReadByType with no matches)"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            do! store.Write(makeEvent scope "OnlyOne" "m" 0.0)

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

            do! store.Write(makeEvent scopeA "E" "m" 0.0)
            do! store.Write(makeEvent scopeA "E" "m" -1.0)
            do! store.Write(makeEvent scopeB "E" "m" 0.0)
            do! store.Write(makeEvent scopeC "E" "m" 0.0)

            let! scopes = store.ListScopes()
            let asSet = Set.ofList scopes

            Expect.equal asSet (Set.ofList [ scopeA; scopeB; scopeC ]) "all three scopes enumerated"
        }

        testCaseAsync "ListScopes deduplicates — one entry per scope regardless of event count"
        <| async {
            let store = factory ()
            let scope = uniqueScope ()

            for i in 0..9 do
                do! store.Write(makeEvent scope $"E{i}" "m" (float -i))

            let! scopes = store.ListScopes()
            let matching = scopes |> List.filter (fun s -> s = scope)
            Expect.hasLength matching 1 "scope enumerated exactly once across 10 events"
        }
    ]