module ToolUp.Platform.Tests.InProcess.ModuleQueryBusTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tracing
open ToolUp.Platform.Tests.Contracts

// ─── InMemoryModuleQueryBus — IModuleQueryBus contract binding ───
//
// Binds the `IModuleQueryBus` contract pack to the Phase 6b shipped
// in-process implementation. The bus takes its registry as a
// constructor argument, so the contract factory builds the registry
// from the test's `(moduleName, handler) list` via the public
// `ModuleQueryBus.buildRegistry` helper.

let private contractTests =
    let logger: ILogger = ConsoleLogger.ConsoleLogger()

    let factory (registrations: (string * ModuleQueryHandler) list) : IModuleQueryBus =
        let registry = ModuleQueryBus.buildRegistry registrations
        ModuleQueryBus.InMemoryModuleQueryBus(registry, logger, NoOpActivitySink() :> IActivitySink) :> IModuleQueryBus

    IModuleQueryBusContract.tests "InMemoryModuleQueryBus" factory

// ─── Phase 579 — compose-time duplicate query-handler rejection ───
//
// Both buses route `(TargetModule, QueryKey)` to exactly one handler.
// Before Phase 579 a duplicate pair folded silently (last registration
// won) and the shadowed handler only surfaced as `NoHandler` — or as
// the *wrong* answer — at request time. The rejection is structural
// (`ModuleQueryRegistry.build`, tier-shared in `ToolUp.Platform.Core`),
// so the SDK names no module (GP 9); each tier's `buildRegistry`
// delegates to it, and both are exercised here from .NET.

let private handler (queryKey: string) (reply: string) : ModuleQueryHandler = {
    QueryKey = queryKey
    Handle = fun _ -> async { return reply }
}

/// Run `build`, expecting it to fail; return the failure message.
let private captureFailure (build: unit -> unit) : string =
    let mutable message = None

    try
        build ()
    with ex ->
        message <- Some ex.Message

    match message with
    | Some m -> m
    | None -> failtest "expected a compose-time failure, but registry construction succeeded"

/// The two tier entry points under test. Each `buildRegistry` is a thin
/// delegation to `ModuleQueryRegistry.build`, so the same assertions run
/// against both — proof that neither tier retains a last-wins path.
let private tiers: (string * ((string * ModuleQueryHandler) list -> Map<string, Map<string, ModuleQueryHandler>>)) list = [
    "server", ModuleQueryBus.buildRegistry
    "client", ModuleQueryClient.buildRegistry
]

let private duplicateRejectionTests =
    testList "Phase 579 — duplicate query-handler rejection" [
        for tierName, buildRegistry in tiers do
            testList tierName [
                test "duplicate (module, queryKey) fails composition naming module and key" {
                    let message =
                        captureFailure (fun () ->
                            [ "Reports", handler "latest" "first"; "Reports", handler "latest" "second" ]
                            |> buildRegistry
                            |> ignore)

                    Expect.stringContains message "Reports" "failure names the offending module"
                    Expect.stringContains message "latest" "failure names the offending query key"

                    Expect.stringContains message "Compose-time defect" "failure is framed as a compose-time defect"
                }

                test "every colliding pair is reported, not just the first" {
                    let message =
                        captureFailure (fun () ->
                            [
                                "Reports", handler "latest" "a"
                                "Reports", handler "latest" "b"
                                "Reports", handler "history" "c"
                                "Catalog", handler "search" "d"
                                "Catalog", handler "search" "e"
                            ]
                            |> buildRegistry
                            |> ignore)

                    Expect.stringContains message "Reports" "first collision's module named"
                    Expect.stringContains message "latest" "first collision's key named"
                    Expect.stringContains message "Catalog" "second collision's module named"
                    Expect.stringContains message "search" "second collision's key named"
                }

                test "three registrations of one pair report the registration count" {
                    let message =
                        captureFailure (fun () ->
                            [
                                "Reports", handler "latest" "a"
                                "Reports", handler "latest" "b"
                                "Reports", handler "latest" "c"
                            ]
                            |> buildRegistry
                            |> ignore)

                    Expect.stringContains message "3 registrations" "failure reports how many registrations collided"
                }

                // GP 11 — a composition with no duplicate is
                // byte-identical to the pre-Phase-579 last-wins fold.
                test "distinct keys within one module still register" {
                    let registry =
                        [ "Reports", handler "latest" "a"; "Reports", handler "history" "b" ]
                        |> buildRegistry

                    let inner = registry |> Map.find "Reports"
                    Expect.equal (Map.count registry) 1 "one module in the registry"
                    Expect.equal (Map.count inner) 2 "both handlers registered"
                    Expect.isTrue (inner |> Map.containsKey "latest") "latest registered"
                    Expect.isTrue (inner |> Map.containsKey "history") "history registered"
                }

                test "the same queryKey in two different modules is not a collision" {
                    let registry =
                        [ "Reports", handler "latest" "a"; "Catalog", handler "latest" "b" ]
                        |> buildRegistry

                    Expect.equal (Map.count registry) 2 "both modules in the registry"

                    Expect.isTrue
                        (registry |> Map.find "Reports" |> Map.containsKey "latest")
                        "Reports keeps its handler"

                    Expect.isTrue
                        (registry |> Map.find "Catalog" |> Map.containsKey "latest")
                        "Catalog keeps its handler"
                }

                test "an empty registration list builds an empty registry" {
                    let registry = buildRegistry []
                    Expect.isTrue (Map.isEmpty registry) "no modules registered"
                }
            ]
    ]

let tests = testList "ModuleQueryBus" [ contractTests; duplicateRejectionTests ]