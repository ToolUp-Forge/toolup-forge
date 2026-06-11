module ToolUp.AI.Client.Tests.NodeTest

open Fable.Core
open Fable.Core.JsInterop

// ─── node:test + node:assert/strict shim ────────────────────────────
//
// Thin Expecto-style facade over Node.js' built-in test runner
// (`node:test`, stable in Node 20+) and built-in strict-mode
// assertion library (`node:assert/strict`). Zero npm transitive
// deps — the runner ships with Node itself.
//
// Why this exists. `Fable.Mocha` is the standard Fable testing
// library, but it bridges to npm `mocha`, whose 2026-06 transitive
// dep tree carries unaddressed advisories on `serialize-javascript`
// (RCE, CVSS 8.1) and `diff` (DoS) that no current `mocha` version
// clears. The Mocha team's stance is that these aren't reachable
// for test-runner usage (no untrusted-input vector), but `npm audit`
// doesn't make that distinction — every contributor running it
// sees "3 vulnerabilities, 1 high." `node:test` sidesteps the noise
// entirely because there are no transitive deps to audit.
//
// API. Mirrors the subset of `Fable.Mocha` / `Expecto` we actually
// use — `testCase`, `testList`, `Expect.equal` / `isTrue` /
// `isFalse` / `isEmpty`. Nested `testList`s map to nested
// `describe` blocks. Each `testCase` body is a `unit -> unit`
// thunk; thrown exceptions (from F# `failwith*` or from a failed
// `Expect.*` assertion) bubble to `node:test` as test failures.
//
// Run procedure (from `src/ToolUp.AI.Client.Tests/`):
//     dotnet fable -o output --noCache
//     node --import ./register-loader.mjs --test output/Program.js
//
// See `docs/platform/testing-conventions.md` "Testing client-tier
// MVU update functions" for the full convention.

// ─── node:test bindings ─────────────────────────────────────────────

[<Import("test", from = "node:test")>]
let private nodeTestFn (name: string) (body: unit -> unit) : unit = jsNative

/// Callback-style `test(name, (t, done) => …)` binding for cases whose
/// assertions run after a timer hop (e.g. the Elmish `AsyncHelpers.start`
/// 1ms-delayed dispatch). The delegate shape compiles to a two-arg JS
/// function so node:test recognises the done-callback arity.
[<Import("test", from = "node:test")>]
let private nodeTestDoneFn (name: string) (body: System.Action<obj, System.Action<obj>>) : unit = jsNative

[<Import("describe", from = "node:test")>]
let private nodeDescribeFn (name: string) (body: unit -> unit) : unit = jsNative

// ─── node:assert/strict bindings ────────────────────────────────────

let private nodeAssert: obj = import "*" "node:assert/strict"

// ─── Test tree shape ────────────────────────────────────────────────

type TestItem =
    | Case of name: string * body: (unit -> unit)
    /// Deferred case: `setup` runs synchronously and returns the
    /// assertion thunk, which runs after `delayMs` (one or more timer
    /// hops later). Assertion failures route through the node:test
    /// done-callback as test failures, not uncaught exceptions.
    | DeferredCase of name: string * delayMs: int * setup: (unit -> (unit -> unit))
    | List of name: string * items: TestItem list

let testCase (name: string) (body: unit -> unit) : TestItem = Case(name, body)

/// A case whose assertions must run after pending timer callbacks
/// (e.g. an Elmish effect's delayed dispatch). `setup` performs the
/// action under test and returns the assertions to run `delayMs`
/// later.
let testCaseDeferred (name: string) (delayMs: int) (setup: unit -> (unit -> unit)) : TestItem =
    DeferredCase(name, delayMs, setup)

let testList (name: string) (items: TestItem list) : TestItem = List(name, items)

let rec private runItem (item: TestItem) : unit =
    match item with
    | Case(name, body) -> nodeTestFn name body
    | DeferredCase(name, delayMs, setup) ->
        nodeTestDoneFn
            name
            (System.Action<obj, System.Action<obj>>(fun _t done' ->
                let assertions = setup ()

                JS.setTimeout
                    (fun () ->
                        try
                            assertions ()
                            done'.Invoke(null)
                        with ex ->
                            done'.Invoke(box ex))
                    delayMs
                |> ignore))
    | List(name, items) ->
        nodeDescribeFn name (fun () ->
            for i in items do
                runItem i)

/// Schedule every case in `root` with node:test. Returns 0 — the
/// runner sets the actual process exit code based on results when
/// invoked under `node --test`.
let runTests (root: TestItem) : int =
    runItem root
    0

// ─── Expecto-shaped assertion facade ────────────────────────────────

module Expect =
    let equal (actual: 'a) (expected: 'a) (message: string) : unit =
        nodeAssert?deepStrictEqual (actual, expected, message)

    let isTrue (actual: bool) (message: string) : unit = nodeAssert?ok (actual, message)

    let isFalse (actual: bool) (message: string) : unit = nodeAssert?ok (not actual, message)

    let isEmpty (actual: seq<'a>) (message: string) : unit =
        nodeAssert?ok (Seq.isEmpty actual, message)

    let isNone (actual: 'a option) (message: string) : unit =
        nodeAssert?ok (Option.isNone actual, message)