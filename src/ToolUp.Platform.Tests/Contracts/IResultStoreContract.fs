module ToolUp.Platform.Tests.Contracts.IResultStoreContract

open System
open System.Text
open Expecto
open ToolUp.Platform

/// Contract test list for any `IResultStore` implementation. The
/// factory takes nothing and returns a fresh `(store, scopeA, scopeB)`
/// triple — scopeA and scopeB are GUID-suffixed so cross-scope
/// isolation can be exercised even when the underlying substrate is
/// shared.
let tests (name: string) (factory: unit -> IResultStore * string * string) =

    let bytesOf (s: string) = Encoding.UTF8.GetBytes s
    let textOf (b: byte[]) = Encoding.UTF8.GetString b

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    testList $"{name} — IResultStore contract" [

        testCaseAsync "SaveResult then GetLatest round-trips"
        <| async {
            let store, scopeA, _ = factory ()

            let! saved = store.SaveResult(scopeA, "SalesAnalysis", "Quarterly", bytesOf "{\"v\":1}", "alice", [])
            let _ = okOrFail "Save" saved

            let obj, content =
                okOrFail "GetLatest" (store.GetLatest(scopeA, "SalesAnalysis", "Quarterly") |> Async.RunSynchronously)

            Expect.equal obj.Version 1 "first save is v1"
            Expect.equal obj.CreatedBy "alice" "createdBy preserved"
            Expect.equal obj.Policy StrictlyVersioned "policy is sticky StrictlyVersioned"
            Expect.equal (textOf content) "{\"v\":1}" "content round-trip"
        }

        testCaseAsync "Three saves produce three versions"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":1}", "alice", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":2}", "alice", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":3}", "alice", [])

            let! results = store.ListResults(scopeA, "M", None)
            Expect.equal results.Length 3 "three versions present"
            let versions = results |> List.map _.Version |> Set.ofList
            Expect.equal versions (Set.ofList [ 1; 2; 3 ]) "versions are 1, 2, 3"
        }

        testCaseAsync "GetLatest returns the most recent version"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":1}", "alice", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":2}", "alice", [])

            let obj, content =
                okOrFail "GetLatest" (store.GetLatest(scopeA, "M", "T") |> Async.RunSynchronously)

            Expect.equal obj.Version 2 "latest is v2"
            Expect.equal (textOf content) "{\"v\":2}" "v2 content returned"
        }

        testCaseAsync "GetLatest of unknown result returns NotFound"
        <| async {
            let store, scopeA, _ = factory ()

            match! store.GetLatest(scopeA, "Ghost", "Nope") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound; got %A" other
        }

        testCaseAsync "ListResults filters by moduleName"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "ModuleA", "T1", bytesOf "{}", "u", [])
            let! _ = store.SaveResult(scopeA, "ModuleA", "T2", bytesOf "{}", "u", [])
            let! _ = store.SaveResult(scopeA, "ModuleB", "T1", bytesOf "{}", "u", [])

            let! aResults = store.ListResults(scopeA, "ModuleA", None)
            Expect.equal aResults.Length 2 "ModuleA has two results"

            let! bResults = store.ListResults(scopeA, "ModuleB", None)
            Expect.equal bResults.Length 1 "ModuleB has one result"
        }

        testCaseAsync "ListResults filters by date range"
        <| async {
            let store, scopeA, _ = factory ()

            let beforeAll = DateTime.UtcNow.AddMinutes -10.0
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{}", "u", [])
            let afterAll = DateTime.UtcNow.AddMinutes 10.0

            let! inRange = store.ListResults(scopeA, "M", Some(beforeAll, afterAll))
            Expect.equal inRange.Length 1 "in-range save returned"

            let pastWindow = DateTime.UtcNow.AddDays -2.0
            let pastWindowEnd = DateTime.UtcNow.AddDays -1.0
            let! outOfRange = store.ListResults(scopeA, "M", Some(pastWindow, pastWindowEnd))
            Expect.equal outOfRange.Length 0 "out-of-range returns empty"
        }

        testCaseAsync "Cross-scope isolation: scopeB cannot see scopeA's results"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{}", "u", [])

            match! store.GetLatest(scopeB, "M", "T") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound in scopeB; got %A" other

            let! resultsB = store.ListResults(scopeB, "M", None)
            Expect.equal resultsB.Length 0 "scopeB ListResults empty"
        }

        testCaseAsync "CompareVersions returns leaf-level changes"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"revenue\":100,\"region\":\"NA\"}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"revenue\":150,\"region\":\"NA\"}", "u", [])

            let objectId = ResultObjectId.make "M" "T"
            let! diffResult = store.CompareVersions(scopeA, objectId, 1, 2)
            let diff = okOrFail "CompareVersions" diffResult

            Expect.equal diff.FromVersion 1 "from"
            Expect.equal diff.ToVersion 2 "to"
            Expect.equal diff.Changes.Length 1 "single field changed"
            Expect.equal diff.Changes[0].Path "revenue" "revenue path"
            Expect.equal diff.Changes[0].From (Some "100") "old value"
            Expect.equal diff.Changes[0].To (Some "150") "new value"
        }

        testCaseAsync "CompareVersions returns empty Changes for identical content"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"x\":1}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"x\":1}", "u", [])

            let objectId = ResultObjectId.make "M" "T"
            let! diffResult = store.CompareVersions(scopeA, objectId, 1, 2)
            let diff = okOrFail "CompareVersions" diffResult
            Expect.equal diff.Changes [] "no leaf differences"
        }

        testCaseAsync "CompareVersions detects added and removed keys"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"a\":1}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"b\":2}", "u", [])

            let objectId = ResultObjectId.make "M" "T"
            let! diffResult = store.CompareVersions(scopeA, objectId, 1, 2)
            let diff = okOrFail "CompareVersions" diffResult

            // Two changes: "a" removed, "b" added.
            Expect.equal diff.Changes.Length 2 "two leaf changes"

            let byPath = diff.Changes |> List.map _.Path |> Set.ofList
            Expect.equal byPath (Set.ofList [ "a"; "b" ]) "paths a and b"

            let aChange = diff.Changes |> List.find (fun c -> c.Path = "a")
            Expect.equal aChange.From (Some "1") "a removed: From=1"
            Expect.equal aChange.To None "a removed: To=None"

            let bChange = diff.Changes |> List.find (fun c -> c.Path = "b")
            Expect.equal bChange.From None "b added: From=None"
            Expect.equal bChange.To (Some "2") "b added: To=2"
        }

        testCaseAsync "CompareVersions returns StorageFailure for non-JSON content"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "not json", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "still not json", "u", [])

            let objectId = ResultObjectId.make "M" "T"

            match! store.CompareVersions(scopeA, objectId, 1, 2) with
            | Error(StorageFailure msg) -> Expect.stringContains msg "JSON" "error mentions JSON requirement"
            | other -> failtestf "Expected StorageFailure; got %A" other
        }

        testCaseAsync "CompareVersions handles nested object diff"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"summary\":{\"revenue\":100,\"units\":5}}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"summary\":{\"revenue\":200,\"units\":5}}", "u", [])

            let objectId = ResultObjectId.make "M" "T"
            let! diffResult = store.CompareVersions(scopeA, objectId, 1, 2)
            let diff = okOrFail "CompareVersions" diffResult

            Expect.equal diff.Changes.Length 1 "one nested change"
            Expect.equal diff.Changes[0].Path "summary.revenue" "dotted path"
        }

        // ─── Delete surface (Phase 158) ──────────────────────────────

        testCaseAsync "DeleteResult removes every version; GetLatest then NotFound and ListResults empty"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":1}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":2}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{\"v\":3}", "u", [])

            let! deleted = store.DeleteResult(scopeA, "M", "T")
            okOrFail "DeleteResult" deleted

            match! store.GetLatest(scopeA, "M", "T") with
            | Error NotFound -> ()
            | other -> failtestf "Expected NotFound after delete; got %A" other

            let! remaining = store.ListResults(scopeA, "M", None)
            Expect.equal remaining.Length 0 "no versions remain after delete"
        }

        testCaseAsync "DeleteResult on an absent result is Ok (idempotent)"
        <| async {
            let store, scopeA, _ = factory ()

            let! first = store.DeleteResult(scopeA, "Ghost", "Nope")
            okOrFail "DeleteResult absent" first

            // Deleting an existing result twice is also Ok the second time.
            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{}", "u", [])
            let! once = store.DeleteResult(scopeA, "M", "T")
            okOrFail "DeleteResult first" once
            let! twice = store.DeleteResult(scopeA, "M", "T")
            okOrFail "DeleteResult repeat" twice
        }

        testCaseAsync "DeleteResult isolates by module — deleting ModuleA/T leaves ModuleB/T"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "ModuleA", "T", bytesOf "{\"a\":1}", "u", [])
            let! _ = store.SaveResult(scopeA, "ModuleB", "T", bytesOf "{\"b\":1}", "u", [])

            let! deleted = store.DeleteResult(scopeA, "ModuleA", "T")
            okOrFail "DeleteResult ModuleA" deleted

            match! store.GetLatest(scopeA, "ModuleA", "T") with
            | Error NotFound -> ()
            | other -> failtestf "Expected ModuleA/T gone; got %A" other

            let _ =
                okOrFail "ModuleB survives" (store.GetLatest(scopeA, "ModuleB", "T") |> Async.RunSynchronously)

            ()
        }

        testCaseAsync "DeleteByPrefix removes matching results and leaves non-matching"
        <| async {
            let store, scopeA, _ = factory ()

            let! _ = store.SaveResult(scopeA, "M", "q1-sales", bytesOf "{}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "q1-costs", bytesOf "{}", "u", [])
            let! _ = store.SaveResult(scopeA, "M", "q2-sales", bytesOf "{}", "u", [])

            let! deleted = store.DeleteByPrefix(scopeA, "M", "q1-")
            okOrFail "DeleteByPrefix" deleted

            match! store.GetLatest(scopeA, "M", "q1-sales") with
            | Error NotFound -> ()
            | other -> failtestf "Expected q1-sales gone; got %A" other

            match! store.GetLatest(scopeA, "M", "q1-costs") with
            | Error NotFound -> ()
            | other -> failtestf "Expected q1-costs gone; got %A" other

            let _ =
                okOrFail "q2-sales survives" (store.GetLatest(scopeA, "M", "q2-sales") |> Async.RunSynchronously)

            ()
        }

        testCaseAsync "DeleteByPrefix on a prefix matching nothing is Ok (idempotent)"
        <| async {
            let store, scopeA, _ = factory ()
            let! _ = store.SaveResult(scopeA, "M", "keep", bytesOf "{}", "u", [])

            let! deleted = store.DeleteByPrefix(scopeA, "M", "nomatch-")
            okOrFail "DeleteByPrefix no match" deleted

            let _ =
                okOrFail "unrelated survives" (store.GetLatest(scopeA, "M", "keep") |> Async.RunSynchronously)

            ()
        }

        testCaseAsync "DeleteByPrefix isolates by scope — scopeA delete leaves scopeB intact"
        <| async {
            let store, scopeA, scopeB = factory ()

            let! _ = store.SaveResult(scopeA, "M", "T", bytesOf "{}", "u", [])
            let! _ = store.SaveResult(scopeB, "M", "T", bytesOf "{}", "u", [])

            // Empty prefix matches every resultType under the module.
            let! deleted = store.DeleteByPrefix(scopeA, "M", "")
            okOrFail "DeleteByPrefix scopeA" deleted

            match! store.GetLatest(scopeA, "M", "T") with
            | Error NotFound -> ()
            | other -> failtestf "Expected scopeA/M/T gone; got %A" other

            let _ =
                okOrFail "scopeB survives" (store.GetLatest(scopeB, "M", "T") |> Async.RunSynchronously)

            ()
        }
    ]