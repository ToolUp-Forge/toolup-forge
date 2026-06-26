module ToolUp.AI.Wire.Fable.Tests.NodeTest

open Fable.Core
open Fable.Core.JsInterop

// Thin Expecto-style facade over Node.js' built-in test runner
// (`node:test`, stable in Node 20+) and its strict-mode assertion library
// (`node:assert/strict`). Zero npm transitive deps — both ship with Node.
// Mirrors the same shim used by the AI.Client Fable-tier harness; trimmed
// to the subset this smoke needs (testCase / testList / runTests /
// Expect.equal / Expect.isTrue / Expect.isNone). See
// docs/platform/testing-conventions.md for the convention.

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