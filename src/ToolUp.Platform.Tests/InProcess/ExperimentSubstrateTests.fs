module ToolUp.Platform.Tests.InProcess.ExperimentSubstrateTests

open Expecto
open ToolUp.Experiments
open ToolUp.Experiments.Server

// ─── Phase 242 — A/B experiment substrate ────────────────────────────
//
// Proves: deterministic + weight-respecting assignment; assign None for
// degenerate experiments; only Running experiments assign; exposure logs
// exactly once per (scope, experiment, principal); store scope-isolation.

let private twoArm = {
    Id = "checkout-cta"
    Status = Running
    Variants = [ { Key = "control"; Weight = 0.5 }; { Key = "blue"; Weight = 0.5 } ]
}

let tests =
    testList "ExperimentSubstrate (Phase 242)" [
        test "assignment is deterministic across calls" {
            let a = Assignment.assign twoArm "user-1"
            let b = Assignment.assign twoArm "user-1"
            Expect.equal a b "same principal → same variant"
            Expect.isSome a "assigned"
        }

        test "assignment respects an all-weight-on-one-arm split" {
            let oneArm = {
                twoArm with
                    Variants = [ { Key = "only"; Weight = 1.0 }; { Key = "never"; Weight = 0.0 } ]
            }

            for i in 1..50 do
                Expect.equal
                    (Assignment.assign oneArm (sprintf "user-%d" i) |> Option.map _.Key)
                    (Some "only")
                    "0-weight arm never chosen"
        }

        test "assignment spreads roughly to weights over many principals" {
            let counts =
                [ 1..1000 ]
                |> List.choose (fun i -> Assignment.assign twoArm (sprintf "u-%d" i) |> Option.map _.Key)
                |> List.countBy id
                |> Map.ofList

            let control = counts |> Map.tryFind "control" |> Option.defaultValue 0
            // 50/50 split over 1000 — expect each arm well within [350, 650].
            Expect.isGreaterThan control 350 "control not starved"
            Expect.isLessThan control 650 "control not dominant"
        }

        test "assign None for no-variant / zero-weight experiments" {
            Expect.isNone (Assignment.assign { twoArm with Variants = [] } "u") "empty"

            Expect.isNone
                (Assignment.assign
                    {
                        twoArm with
                            Variants = [ { Key = "x"; Weight = 0.0 } ]
                    }
                    "u")
                "zero total weight"
        }

        test "only Running experiments assign" {
            let store = InMemoryExperimentStore() :> IExperimentStore

            store.Set("team-a", { twoArm with Status = Draft })
            |> Async.RunSynchronously
            |> ignore

            let sink = CollectingExposureSink()
            let svc = ExperimentService(store, sink)
            let r = svc.Assign("team-a", "checkout-cta", "user-1") |> Async.RunSynchronously
            Expect.isNone r "Draft does not assign"
            Expect.isEmpty sink.Recorded "no exposure for non-Running"
        }

        test "exposure logs exactly once per principal" {
            let store = InMemoryExperimentStore() :> IExperimentStore
            store.Set("team-a", twoArm) |> Async.RunSynchronously |> ignore
            let sink = CollectingExposureSink()
            let svc = ExperimentService(store, sink)

            svc.Assign("team-a", "checkout-cta", "user-1")
            |> Async.RunSynchronously
            |> ignore

            svc.Assign("team-a", "checkout-cta", "user-1")
            |> Async.RunSynchronously
            |> ignore

            svc.Assign("team-a", "checkout-cta", "user-2")
            |> Async.RunSynchronously
            |> ignore

            Expect.equal sink.Recorded.Length 2 "one exposure per distinct principal"
        }

        test "store is scope-isolated" {
            let store = InMemoryExperimentStore() :> IExperimentStore
            store.Set("team-a", twoArm) |> Async.RunSynchronously |> ignore
            let listB = store.List "team-b" |> Async.RunSynchronously
            Expect.isEmpty listB "scope b sees nothing from scope a"
        }
    ]