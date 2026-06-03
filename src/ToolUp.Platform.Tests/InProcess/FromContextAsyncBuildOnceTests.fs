module ToolUp.Platform.Tests.InProcess.FromContextAsyncBuildOnceTests

open System.IO
open Expecto

// ─── Phase 69n — fromContextAsync build-once dispatcher table ──
//
// Closes Finding F4 from
// application-plans/toolup-remoting-hot-path-perf.md.
//
// Pre-69n the Giraffe adapter's `fromContextAsync` arm called
// `buildFromImplementation` per request, which rebuilt the entire
// dispatcher substrate (TypeShape proxy + six attribute-driven
// classifier maps + rmsManager + compose-time guards) every dispatch.
// The async resolver runs per request as intended, but the substrate
// rebuild was a load-bearing latent landmine for any consumer adopting
// the documented "build-once / read-per-call" pattern at scale.
//
// After 69n:
//   * `GiraffeUtil.buildDispatcherTable options` runs all the one-time
//     setup once and returns a `(HttpContext -> 'impl) -> HttpHandler`
//     function.
//   * `buildFromImplementation implBuilder options` is a thin shim:
//     `buildDispatcherTable options implBuilder`. Preserves the
//     pre-69n signature for the `StaticValue` / `FromContext` paths.
//   * `buildHttpHandler`'s `FromContextAsync` arm calls
//     `buildDispatcherTable` ONCE at compose time, stashes the
//     returned `dispatch` function, and the per-request closure just
//     `await`s the async resolver + calls `dispatch (fun _ -> impl)`.
//   * The `Remoting.fromContextAsync` docstring's "Performance caveat
//     (until Phase 69n ships)" block is retired in this phase.
//
// These checks are textual — same shape as Phase 69l + 69m. An
// integration-shape build-once test (compose `fromContextAsync` with a
// tracking resolver + a tracking `makeApiProxy` substitute, dispatch
// 100 requests, assert one `makeApiProxy` call) is deferred to the
// shared TestServer-scaffold follow-up TIDY-UP item.

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

[<Tests>]
let tests =
    testList "Phase 69n — fromContextAsync build-once" [

        test "GiraffeUtil.buildDispatcherTable function exists" {
            let adapterPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Giraffe",
                    "GiraffeAdapter.fs"
                )

            let contents = File.ReadAllText adapterPath

            Expect.stringContains
                contents
                "let buildDispatcherTable<'impl>"
                "GiraffeUtil.buildDispatcherTable is the carve-out the FromContextAsync arm \
                 calls once at compose time. Renaming/removing it silently re-introduces \
                 the per-request substrate rebuild."
        }

        test "buildFromImplementation is now a shim over buildDispatcherTable" {
            let adapterPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Giraffe",
                    "GiraffeAdapter.fs"
                )

            let contents = File.ReadAllText adapterPath

            Expect.stringContains
                contents
                "buildDispatcherTable options implBuilder"
                "buildFromImplementation must delegate to buildDispatcherTable to preserve \
                 the StaticValue / FromContext shape without duplicating the table-build code."
        }

        test "FromContextAsync arm calls buildDispatcherTable ONCE at compose time" {
            let adapterPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Giraffe",
                    "GiraffeAdapter.fs"
                )

            let contents = File.ReadAllText adapterPath

            // The compose-time call must live OUTSIDE the per-request
            // closure. Pinning the structural location: `let dispatch =
            // GiraffeUtil.buildDispatcherTable options` precedes the
            // `fun (next: HttpFunc) (ctx: HttpContext) -> task {` line.
            Expect.stringContains
                contents
                "let dispatch = GiraffeUtil.buildDispatcherTable options"
                "FromContextAsync arm must hoist the dispatcher build out of the per-request \
                 closure by binding `let dispatch = GiraffeUtil.buildDispatcherTable options` \
                 before the `fun next ctx ->` closure. Without this the per-request rebuild \
                 returns."

            // The per-request closure body must NOT contain another
            // `buildDispatcherTable` or `buildFromImplementation` call.
            // Locate the `FromContextAsync createImplementationFromAsync` arm
            // and assert the body doesn't re-trigger the rebuild.
            let asyncArmIdx =
                contents.IndexOf "| FromContextAsync createImplementationFromAsync ->"

            Expect.isGreaterThan asyncArmIdx -1 "FromContextAsync arm must exist in buildHttpHandler"

            // From the arm header to the next `|` (next match arm) or end,
            // confirm no per-request `buildFromImplementation` call hides
            // inside. Look at the next 1500 chars (the arm body).
            let armScope =
                contents.Substring(asyncArmIdx, min 1500 (contents.Length - asyncArmIdx))

            Expect.isFalse
                (armScope.Contains "GiraffeUtil.buildFromImplementation (fun _ -> impl) options next ctx")
                "FromContextAsync arm must NOT call buildFromImplementation per request — \
                 that's the pre-69n per-request rebuild defect. The arm should call \
                 `dispatch (fun _ -> impl) next ctx` instead."
        }

        test "FromContextAsync per-request closure calls dispatch with resolved impl" {
            let adapterPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Giraffe",
                    "GiraffeAdapter.fs"
                )

            let contents = File.ReadAllText adapterPath

            Expect.stringContains
                contents
                "dispatch (fun _ -> impl) next ctx"
                "Per-request closure must invoke the pre-built dispatcher with the resolved impl. \
                 This is the read-per-call half of build-once / read-per-call."
        }

        test "StaticValue + FromContext arms continue to use buildFromImplementation shim" {
            let adapterPath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "Giraffe",
                    "GiraffeAdapter.fs"
                )

            let contents = File.ReadAllText adapterPath

            Expect.stringContains
                contents
                "| StaticValue impl -> GiraffeUtil.buildFromImplementation (fun _ -> impl) options"
                "StaticValue arm must keep its existing call shape. The shim preserves it."

            Expect.stringContains
                contents
                "| FromContext createImplementationFrom -> GiraffeUtil.buildFromImplementation createImplementationFrom options"
                "FromContext arm must keep its existing call shape."
        }

        test "fromContextAsync docstring retires the performance caveat" {
            let remotingPath =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Server", "Server", "Remoting", "Remoting.fs")

            let contents = File.ReadAllText remotingPath

            Expect.isFalse
                (contents.Contains "Performance caveat (until Phase 69n ships)")
                "The TIDY-UP `Performance caveat` block must be retired now that Phase 69n \
                 ships. The docstring should plainly recommend the pattern."

            Expect.stringContains
                contents
                "Phase 69n"
                "The docstring should still cite Phase 69n explicitly so readers know \
                 the substrate cost was lifted to compose time."
        }

        test "AspNetCore middleware FromContextAsync refusal stays in place (parity scope)" {
            let middlewarePath =
                Path.Combine(
                    repoRoot (),
                    "src",
                    "ToolUp.Platform.Server",
                    "Server",
                    "Remoting",
                    "AspNetCore",
                    "Middleware.fs"
                )

            let contents = File.ReadAllText middlewarePath

            Expect.stringContains
                contents
                "Remoting.fromContextAsync is not yet supported by the AspNetCore middleware"
                "Phase 69n is Giraffe-only. The AspNetCore middleware adapter continues to \
                 refuse fromContextAsync for its existing Phase 69b–69k parity reasons; \
                 lifting the refusal is a separate question."
        }
    ]