module ToolUp.Stripe.Server.Tests.IdempotencyStoreContractTests

open Expecto
open ToolUp.Stripe.Server
open ToolUp.Platform.Testing.Fakes

/// The `IWebhookIdempotencyStore` conformance suite. Every
/// implementation validates against the same bar — `makeStore` returns
/// a fresh, independent store each call.
let private contract (name: string) (makeStore: unit -> IWebhookIdempotencyStore) : Test =
    testList name [
        test "first claim for an id wins" {
            let store = makeStore ()
            let won = store.TryClaim "evt_first" |> Async.RunSynchronously
            Expect.isTrue won "first claim wins"
        }
        test "second claim for the same id loses" {
            let store = makeStore ()
            store.TryClaim "evt_dup" |> Async.RunSynchronously |> ignore
            let second = store.TryClaim "evt_dup" |> Async.RunSynchronously
            Expect.isFalse second "replay loses"
        }
        test "distinct ids each win independently" {
            let store = makeStore ()
            let a = store.TryClaim "evt_a" |> Async.RunSynchronously
            let b = store.TryClaim "evt_b" |> Async.RunSynchronously
            Expect.isTrue a "a wins"
            Expect.isTrue b "b wins"
        }
        test "concurrent claims for one id yield exactly one winner" {
            let store = makeStore ()

            let results =
                [ for _ in 1..50 -> async { return! store.TryClaim "evt_race" } ]
                |> Async.Parallel
                |> Async.RunSynchronously

            let winners = results |> Array.filter id |> Array.length
            Expect.equal winners 1 "exactly one concurrent claim wins"
        }
    ]

[<Tests>]
let tests =
    testList "IWebhookIdempotencyStore" [
        // In-memory default (dev-only).
        contract "InMemoryIdempotencyStore" (fun () -> InMemoryIdempotencyStore() :> IWebhookIdempotencyStore)

        // Durable IBlobStorage-backed (production-ready). Each store
        // gets its own backing blob storage so the cases stay isolated.
        contract "DurableIdempotencyStore" (fun () ->
            DurableIdempotencyStore(standardBlobStorage ()) :> IWebhookIdempotencyStore)

        testList "DurableIdempotencyStore durability" [
            test "claim survives a simulated process restart" {
                // One backing store, two store instances: the second
                // models a fresh process over the same durable backing.
                let blob = standardBlobStorage ()
                let store1 = DurableIdempotencyStore(blob) :> IWebhookIdempotencyStore
                let first = store1.TryClaim "evt_restart" |> Async.RunSynchronously
                Expect.isTrue first "first process claims"

                let store2 = DurableIdempotencyStore(blob) :> IWebhookIdempotencyStore
                let afterRestart = store2.TryClaim "evt_restart" |> Async.RunSynchronously
                Expect.isFalse afterRestart "claim persists across the restart"
            }
            test "in-memory store does NOT survive a restart (contrast)" {
                // Documents the gap the durable store closes: a fresh
                // InMemoryIdempotencyStore has no memory of prior claims.
                let store1 = InMemoryIdempotencyStore() :> IWebhookIdempotencyStore
                store1.TryClaim "evt_mem" |> Async.RunSynchronously |> ignore

                let store2 = InMemoryIdempotencyStore() :> IWebhookIdempotencyStore
                let afterRestart = store2.TryClaim "evt_mem" |> Async.RunSynchronously
                Expect.isTrue afterRestart "fresh in-memory store re-claims (no durability)"
            }
        ]
    ]