// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.RemoteAndMemoTests

open Expecto
open ToolUp.Elmish
open ToolUp.Platform

// ─── The client's async-state and memo primitives ─────────────────────
//
// `Remote<'T,'E>` is the one state a client module holds for a read, and
// `Memo` the one-entry memo the shell's context values and dispatch wrappers
// are built on. Both are pure F# compiled into the client tiers, so their
// laws are pinned here on .NET.

type private E =
    | Boom
    | Other

/// Every state of a read, one of each.
let private states: Remote<int, E> list = [
    Remote.NotAsked
    Remote.Loading
    Remote.Reloading 1
    Remote.Loaded 2
    Remote.Failed Boom
]

let private remoteLaws =
    testList "Remote" [
        test "map id is id, and map composes" {
            let f x = x + 1
            let g x = x * 3

            for r in states do
                Expect.equal (Remote.map id r) r $"map id over {r}"
                Expect.equal (Remote.map (f >> g) r) (r |> Remote.map f |> Remote.map g) $"map composes over {r}"
        }

        test "bind: Loaded is its left identity, and Loaded its right identity" {
            let f x =
                if x > 1 then Remote.Loaded(x * 10) else Remote.Failed Other

            Expect.equal (Remote.bind f (Remote.Loaded 2)) (f 2) "left identity"
            Expect.equal (Remote.bind f (Remote.Loaded 1)) (f 1) "left identity, failing continuation"

            for r in states do
                let rightIdentity = Remote.bind Remote.Loaded r

                Expect.equal rightIdentity r $"right identity over {r}"
        }

        test "bind keeps a reload in flight, and passes the other non-answers through" {
            let f x = Remote.Loaded(x + 1)

            Expect.equal
                (Remote.bind f (Remote.Reloading 1))
                (Remote.Reloading 2)
                "a value derived from a stale one is stale"

            Expect.equal
                (Remote.bind (fun _ -> Remote.Failed Other) (Remote.Reloading 1))
                (Remote.Failed Other)
                "a failure derived from it is still a failure"

            Expect.equal (Remote.bind f Remote.NotAsked) Remote.NotAsked "not asked"
            Expect.equal (Remote.bind f Remote.Loading) Remote.Loading "loading"
            Expect.equal (Remote.bind f (Remote.Failed Boom)) (Remote.Failed Boom) "failed"
        }

        test "toOption sees current and stale answers only; withDefault agrees with it" {
            Expect.equal (states |> List.map Remote.toOption) [ None; None; Some 1; Some 2; None ] "toOption"

            for r in states do
                Expect.equal
                    (Remote.withDefault 0 r)
                    (Remote.toOption r |> Option.defaultValue 0)
                    $"withDefault over {r}"
        }

        test "isLoading and isSettled partition the states with NotAsked in neither" {
            Expect.equal
                (states |> List.map Remote.isLoading)
                [ false; true; true; false; false ]
                "in flight: Loading and Reloading"

            Expect.equal
                (states |> List.map Remote.isSettled)
                [ false; false; false; true; true ]
                "settled: Loaded and Failed"
        }

        test "reload keeps an answer on screen and otherwise starts a plain load" {
            Expect.equal
                (states |> List.map Remote.reload)
                [
                    Remote.Loading
                    Remote.Loading
                    Remote.Reloading 1
                    Remote.Reloading 2
                    Remote.Loading
                ]
                "reload over each state"

            for r in states do
                Expect.isTrue (Remote.isLoading (Remote.reload r)) $"a reload is in flight, from {r}"
                Expect.equal (Remote.toOption (Remote.reload r)) (Remote.toOption r) $"and keeps the answer, from {r}"
        }

        test "ofResult maps Ok to Loaded and Error to Failed; mapError touches only Failed" {
            Expect.equal (Remote.ofResult (Ok 3: Result<int, E>)) (Remote.Loaded 3) "Ok"
            Expect.equal (Remote.ofResult (Error Boom: Result<int, E>)) (Remote.Failed Boom) "Error"

            let named =
                states
                |> List.map (
                    Remote.mapError (function
                        | Boom -> "boom"
                        | Other -> "other")
                )

            Expect.equal
                named
                [
                    Remote.NotAsked
                    Remote.Loading
                    Remote.Reloading 1
                    Remote.Loaded 2
                    Remote.Failed "boom"
                ]
                "mapError"
        }
    ]

let private memoLaws =
    testList "Memo" [
        test "byRef: the same input reference gives the same result reference, without recomputing" {
            let calls = ref 0

            let memo =
                Memo.byRef (fun (xs: int list) ->
                    calls.Value <- calls.Value + 1
                    List.sum xs |> box)

            let input = [ 1; 2; 3 ]
            let first = memo input
            let second = memo input
            Expect.isTrue (obj.ReferenceEquals(first, second)) "same reference in, same reference out"
            Expect.equal calls.Value 1 "computed once"
        }

        test "byRef: a new input reference recomputes, even when it is equal by value" {
            let calls = ref 0

            let memo =
                Memo.byRef (fun (xs: int list) ->
                    calls.Value <- calls.Value + 1
                    List.sum xs |> box)

            let first = memo [ 1; 2; 3 ]
            let second = memo (List.map id [ 1; 2; 3 ])
            Expect.equal calls.Value 2 "recomputed for a new reference"
            Expect.isFalse (obj.ReferenceEquals(first, second)) "and the result is the new computation's"
        }

        test "byRef keeps one entry: going back to an old input recomputes" {
            let calls = ref 0

            let memo =
                Memo.byRef (fun (s: obj) ->
                    calls.Value <- calls.Value + 1
                    obj ())

            let a, b = obj (), obj ()
            memo a |> ignore
            memo b |> ignore
            memo a |> ignore
            Expect.equal calls.Value 3 "one entry: a, b, a is three computations"
        }

        test "byRef2: hits only when both inputs are the same references" {
            let calls = ref 0

            let memo =
                Memo.byRef2 (fun (a: obj) (b: obj) ->
                    calls.Value <- calls.Value + 1
                    obj ())

            let a, b, c = obj (), obj (), obj ()
            let first = memo a b
            Expect.isTrue (obj.ReferenceEquals(first, memo a b)) "same pair, same result"
            memo a c |> ignore
            memo c b |> ignore
            Expect.equal calls.Value 3 "either input moving recomputes"
        }

        test "oneEntryBy compares the key with the caller's sameness" {
            let calls = ref 0

            let memo =
                Memo.oneEntryBy (fun (x: string) y -> x.ToLowerInvariant() = y.ToLowerInvariant()) (fun s ->
                    calls.Value <- calls.Value + 1
                    s.Length |> box)

            let first = memo "Abc"
            Expect.isTrue (obj.ReferenceEquals(first, memo "aBC")) "an equal key by the caller's rule hits"
            memo "xyz" |> ignore
            Expect.equal calls.Value 2 "a different key recomputes"
        }
    ]

[<Tests>]
let tests = testList "Remote and Memo primitives" [ remoteLaws; memoLaws ]