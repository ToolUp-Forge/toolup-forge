// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Wire.Conformance.NodeTest

open Fable.Core
open Fable.Core.JsInterop

// Thin Expecto-shaped facade over Node.js' built-in test runner
// (`node:test`, stable in Node 20+) and its strict-mode assertion library
// (`node:assert/strict`). Zero npm transitive deps — both ship with Node.
//
// The signatures (testCase / testList / Expect.equal / Expect.isTrue /
// Expect.isNone) deliberately mirror Expecto's, so the SAME shared suite
// source (ConformanceSuite.fs) compiles unchanged against either facade —
// the Fable host opens this module, the .NET host opens Expecto. That single
// shared suite is the whole point of the conformance pack: the dual-run
// assertion logic is expressed ONCE and so cannot drift between hosts.
//
// Mirrors the sibling ToolUp.AI.Wire.Fable.Tests.NodeTest shim; kept as its
// own copy so the conformance project stays self-contained.

[<Import("test", from = "node:test")>]
let private nodeTestFn (name: string) (body: unit -> unit) : unit = jsNative

[<Import("describe", from = "node:test")>]
let private nodeDescribeFn (name: string) (body: unit -> unit) : unit = jsNative

let private nodeAssert: obj = import "*" "node:assert/strict"

type TestItem =
    | Case of name: string * body: (unit -> unit)
    | List of name: string * items: TestItem list

let testCase (name: string) (body: unit -> unit) : TestItem = Case(name, body)
let testList (name: string) (items: TestItem list) : TestItem = List(name, items)

let rec private runItem (item: TestItem) : unit =
    match item with
    | Case(name, body) -> nodeTestFn name body
    | List(name, items) ->
        nodeDescribeFn name (fun () ->
            for i in items do
                runItem i)

/// Schedule every case in `root` with node:test. Returns 0 — the runner
/// sets the process exit code from results under `node --test`.
let runTests (root: TestItem) : int =
    runItem root
    0

module Expect =
    let equal (actual: 'a) (expected: 'a) (message: string) : unit =
        nodeAssert?deepStrictEqual (actual, expected, message)

    let isTrue (actual: bool) (message: string) : unit = nodeAssert?ok (actual, message)

    let isNone (actual: 'a option) (message: string) : unit =
        nodeAssert?ok (Option.isNone actual, message)