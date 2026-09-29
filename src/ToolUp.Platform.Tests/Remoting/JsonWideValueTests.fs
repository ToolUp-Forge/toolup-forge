// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 905 — the JSON value model's indexed reads answer exactly what the
/// scans they replaced answered.
///
/// `JsonValue.Array` / `Object` carry arrays, so `JsonDecode.index` reads
/// by position; and a WIDE object resolves a member name through an index
/// memoised against the value (`JsonValue.tryMember`). Both are pinned here
/// against a plain forward scan written out in the test, over objects on
/// BOTH sides of the width at which the memoised index takes over — so a
/// lookup that answered differently from the scan (a duplicate resolved to
/// its LAST occurrence, a position off by one, an answer carried over from
/// a different value) is a red run, whichever path served it.
module ToolUp.Platform.Tests.Remoting.JsonWideValueTests

open Expecto
open ToolUp.Remoting
open ToolUp.Remoting.Json

/// The reference: the FIRST member named `name`, by a forward scan.
let private firstMember (name: string) (members: (string * JsonValue)[]) : JsonValue option =
    members |> Array.tryPick (fun (k, v) -> if k = name then Some v else None)

/// An object of `width` members `F0..F{width-1}`, member `i` carrying `i`
/// plus `offset` — so two objects of one width tell their values apart.
let private wideObject (width: int) (offset: int) : (string * JsonValue)[] =
    Array.init width (fun i -> "F" + string i, JsonValue.Number(string (i + offset)))

/// Widths either side of the scan / index boundary, and well past it.
let private widths = [ 1; 8; 31; 32; 33; 64; 257 ]

let private render (result: Result<'T, DecodeError>) =
    match result with
    | Ok v -> sprintf "Ok %A" v
    | Error e -> "Error " + DecodeError.render e

[<Tests>]
let tests =
    testList "Phase 905 — the JSON value model's indexed reads" [

        testCase "every member of an object of any width is found where the scan finds it"
        <| fun () ->
            let mutable checkedLookups = 0

            for width in widths do
                let members = wideObject width 0
                let value = JsonValue.Object members

                for i in 0 .. width - 1 do
                    let name = "F" + string i
                    Expect.equal (JsonValue.tryMember name value) (firstMember name members) name

                    Expect.equal
                        (render (JsonDecode.field name JsonDecode.asInt32 value))
                        (render (Ok i: Result<int, DecodeError>))
                        (sprintf "field %s of %d" name width)

                    checkedLookups <- checkedLookups + 1

            Expect.isGreaterThan checkedLookups 400 "the sweep ran"

        testCase "a duplicate name resolves to its FIRST occurrence, narrow or wide"
        <| fun () ->
            for width in widths do
                let members =
                    Array.append (wideObject width 0) [| "F0", JsonValue.String "later duplicate" |]

                let value = JsonValue.Object members

                Expect.equal (JsonValue.tryMember "F0" value) (Some(JsonValue.Number "0")) (sprintf "width %d" width)

                Expect.equal
                    (JsonValue.tryMember "F0" value)
                    (firstMember "F0" members)
                    (sprintf "scan, width %d" width)

        testCase "an absent member is the same refusal on either side of the boundary"
        <| fun () ->
            let refusal width =
                render (JsonDecode.field "Missing" JsonDecode.asInt32 (JsonValue.Object(wideObject width 0)))

            let narrow = refusal 8

            Expect.stringContains narrow "no such member" "the narrow refusal"

            for width in widths do
                Expect.equal (refusal width) narrow (sprintf "width %d" width)
                Expect.isNone (JsonValue.tryMember "Missing" (JsonValue.Object(wideObject width 0))) "absent"

        testCase "an answer depends on the value asked, never on an earlier lookup"
        <| fun () ->
            // Same names, different positions and values: a lookup memoised
            // by name or by call site would answer the second from the first.
            let first = JsonValue.Object(wideObject 64 0)
            let second = JsonValue.Object(Array.rev (wideObject 64 1000))
            let decoder = JsonDecode.field "F10" JsonDecode.asInt32

            Expect.equal (decoder first) (Ok 10) "first value"
            Expect.equal (decoder second) (Ok 1010) "second value"
            Expect.equal (decoder first) (Ok 10) "first value again"

        testCase "index reads every element by position, and refuses outside the bounds as before"
        <| fun () ->
            for width in widths do
                let value = JsonValue.Array(Array.init width (fun i -> JsonValue.Number(string i)))

                for i in 0 .. width - 1 do
                    Expect.equal (JsonDecode.index i JsonDecode.asInt32 value) (Ok i) (sprintf "[%d] of %d" i width)

                for outside in [ -1; width; width + 1 ] do
                    Expect.equal
                        (render (JsonDecode.index outside JsonDecode.asInt32 value))
                        (sprintf
                            "Error %s"
                            (DecodeError.render (
                                DecodeError.create
                                    (sprintf "an array with an element at %d" outside)
                                    (sprintf "array of %d element(s)" width)
                            )))
                        (sprintf "position %d of %d" outside width)
    ]