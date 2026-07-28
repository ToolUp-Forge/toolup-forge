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

// ─── Phase 584 — typed module query contracts ─────────────────────
//
// A stringly query makes caller and handler agree on three things by
// hand (target module, query key, payload shape) and every mismatch
// surfaces at request time. A `ModuleQueryContract` collapses all three
// into one value declared in the providing module's shared tier, so a
// drift at either end is a compile error instead.
//
// What is asserted here is the part a compile error cannot prove: that
// the contract path lowers onto — and stays wire-compatible with — the
// stringly envelope underneath it (GP 11), and that a payload that does
// not decode surfaces as a typed error naming the contract rather than
// as an opaque failure.

type private LatestReq = { DatasetId: string; Top: int }

type private LatestResp = { Label: string; Score: decimal }

let private latestContract: ModuleQueryContract<LatestReq, LatestResp> =
    ModuleQueryBus.contract<LatestReq, LatestResp> "Reports" "latest"

let private access =
    AccessContext.unrestricted (Subject.AnonymousSession "phase-584")

/// Build a bus over an explicit registration list, the same way the
/// composition root does (`ServerApp` folds each module's
/// `QueryHandlers` into exactly this shape).
let private busOf (registrations: (string * ModuleQueryHandler) list) : IModuleQueryBus =
    let logger: ILogger = ConsoleLogger.ConsoleLogger()

    ModuleQueryBus.InMemoryModuleQueryBus(
        ModuleQueryBus.buildRegistry registrations,
        logger,
        NoOpActivitySink() :> IActivitySink
    )
    :> IModuleQueryBus

/// The provider side as a real `ServerModule`, so the test exercises
/// `withQueryContract`'s lowering rather than calling
/// `ModuleQueryContract.handler` directly.
let private reportsModule () : ServerModule =
    ServerModule.create "Reports"
    |> ServerModule.withQueryContract latestContract (fun _ req -> async {
        return {
            Label = sprintf "%s/%d" req.DatasetId req.Top
            Score = 1.5m
        }
    })

let private registrationsOf (m: ServerModule) =
    m.QueryHandlers |> List.map (fun h -> m.Name, h)

let private expectSomeOk (label: string) (result: Result<'T, ModuleQueryError> option) : 'T =
    match result with
    | Some(Ok value) -> value
    | Some(Error err) -> failtestf "%s: expected Ok, got Error %A" label err
    | None -> failtestf "%s: expected Some, got None (module not registered)" label

let private expectHandlerFailed (label: string) (result: Result<'T, ModuleQueryError> option) : string =
    match result with
    | Some(Error(HandlerFailed message)) -> message
    | other -> failtestf "%s: expected Some (Error (HandlerFailed _)), got %A" label other

let private contractTypedTests =
    testList "Phase 584 — typed query contracts" [
        test "caller and handler sharing a contract round-trip" {
            let bus = busOf (registrationsOf (reportsModule ()))

            let response =
                ModuleQueryBus.askContract bus access latestContract { DatasetId = "ds-1"; Top = 3 }
                |> Async.RunSynchronously
                |> expectSomeOk "contract round-trip"

            Expect.equal response.Label "ds-1/3" "the handler saw the decoded typed request"
            Expect.equal response.Score 1.5m "the caller decoded the typed response"
        }

        // The contract adds no registration shape of its own — it lowers
        // onto the same `QueryHandlers` list `withQueryHandlers` fills,
        // which is why the registry, the Phase 579 duplicate rejection
        // and `ModuleSurface` need no knowledge of contracts.
        test "withQueryContract lowers onto the ordinary handler list" {
            let m = reportsModule ()
            Expect.hasLength m.QueryHandlers 1 "exactly one lowered handler"
            Expect.equal m.QueryHandlers.Head.QueryKey "latest" "registered under the contract's key"
        }

        test "a module registering a contract it does not own is a compose-time defect" {
            let message =
                captureFailure (fun () ->
                    ServerModule.create "Catalog"
                    |> ServerModule.withQueryContract latestContract (fun _ _ -> async {
                        return { Label = ""; Score = 0m }
                    })
                    |> ignore)

            Expect.stringContains message "Catalog" "failure names the registering module"
            Expect.stringContains message "Reports" "failure names the contract's TargetModule"
            Expect.stringContains message "Compose-time defect" "framed as a compose-time defect"
        }

        // ── The wire shape is unchanged: both directions interoperate ──

        test "a contract-registered handler answers a stringly ask" {
            let bus = busOf (registrationsOf (reportsModule ()))

            let response =
                ModuleQueryBus.ask<LatestReq, LatestResp> bus access "Reports" "latest" { DatasetId = "ds-2"; Top = 7 }
                |> Async.RunSynchronously
                |> expectSomeOk "stringly ask against a contract handler"

            Expect.equal response.Label "ds-2/7" "the stringly caller's payload decoded through the contract"
        }

        test "a stringly-registered handler answers a contract ask" {
            let stringlyHandler =
                ModuleQueryBus.ModuleQueryHandler.typed<LatestReq, LatestResp> "latest" (fun _ req -> async {
                    return {
                        Label = sprintf "stringly:%s" req.DatasetId
                        Score = 2m
                    }
                })

            let bus = busOf [ "Reports", stringlyHandler ]

            let response =
                ModuleQueryBus.askContract bus access latestContract { DatasetId = "ds-3"; Top = 1 }
                |> Async.RunSynchronously
                |> expectSomeOk "contract ask against a stringly handler"

            Expect.equal response.Label "stringly:ds-3" "the contract decoded the stringly handler's payload"
        }

        test "stringly and contract registrations coexist on one module" {
            let m =
                reportsModule ()
                |> ServerModule.withQueryHandlers [
                    ModuleQueryBus.ModuleQueryHandler.typed<LatestReq, string> "history" (fun _ req -> async {
                        return sprintf "history:%s" req.DatasetId
                    })
                ]

            let bus = busOf (registrationsOf m)

            let viaContract =
                ModuleQueryBus.askContract bus access latestContract { DatasetId = "ds-4"; Top = 2 }
                |> Async.RunSynchronously
                |> expectSomeOk "contract key still answers"

            let viaStringly =
                ModuleQueryBus.ask<LatestReq, string> bus access "Reports" "history" { DatasetId = "ds-4"; Top = 2 }
                |> Async.RunSynchronously
                |> expectSomeOk "stringly key still answers"

            Expect.equal viaContract.Label "ds-4/2" "contract handler unaffected by the stringly sibling"
            Expect.equal viaStringly "history:ds-4" "stringly handler unaffected by the contract sibling"
        }

        // ── Decode failures name the contract, not just "something threw" ──

        test "a request that does not decode surfaces as a typed error naming the contract" {
            let bus = busOf (registrationsOf (reportsModule ()))

            // A stringly caller on the contract's key sending a payload
            // the contract does not declare — the exact drift a shared
            // contract value removes between two F# call sites, and the
            // one the interop fallback can still produce.
            let message =
                bus.Ask(
                    access,
                    {
                        TargetModule = "Reports"
                        QueryKey = "latest"
                        Payload = "{ this is not json"
                    }
                )
                |> Async.RunSynchronously
                |> expectHandlerFailed "malformed request payload"

            Expect.stringContains message "Reports.latest" "the error names the contract"
            Expect.stringContains message "request" "the error names the failing direction"
        }

        test "a response that does not decode surfaces as a typed error naming the contract" {
            // Handler registered stringly on the contract's key, answering
            // with a shape the contract's response codec cannot read.
            let mismatched = {
                QueryKey = "latest"
                Handle = fun _ -> async { return "\"not-a-record\"" }
            }

            let bus = busOf [ "Reports", mismatched ]

            let message =
                ModuleQueryBus.askContract bus access latestContract { DatasetId = "ds-5"; Top = 1 }
                |> Async.RunSynchronously
                |> expectHandlerFailed "mismatched response payload"

            Expect.stringContains message "Reports.latest" "the error names the contract"
            Expect.stringContains message "response" "the error names the failing direction"
        }

        // ── Graceful degradation is unchanged (GP 11) ──

        test "a contract ask against an absent module still returns None" {
            let bus = busOf []

            let result =
                ModuleQueryBus.askContract bus access latestContract { DatasetId = "ds-6"; Top = 1 }
                |> Async.RunSynchronously

            Expect.isNone result "absent target module degrades to None, as the stringly ask does"
        }

        test "a contract ask on an unregistered key still returns NoHandler" {
            let other =
                ModuleQueryBus.contract<LatestReq, LatestResp> "Reports" "not-registered"

            let bus = busOf (registrationsOf (reportsModule ()))

            let result =
                ModuleQueryBus.askContract bus access other { DatasetId = "ds-7"; Top = 1 }
                |> Async.RunSynchronously

            match result with
            | Some(Error(NoHandler(moduleName, queryKey))) ->
                Expect.equal moduleName "Reports" "NoHandler names the module"
                Expect.equal queryKey "not-registered" "NoHandler names the key"
            | other -> failtestf "expected NoHandler, got %A" other
        }

        // ── The shared-tier helpers are tier-neutral by construction ──

        test "the request envelope a contract emits is the stringly envelope" {
            let envelope =
                ModuleQueryContract.request latestContract { DatasetId = "ds-8"; Top = 4 }

            Expect.equal envelope.TargetModule "Reports" "same routing key"
            Expect.equal envelope.QueryKey "latest" "same query key"

            Expect.stringContains envelope.Payload "ds-8" "the payload is the module's own serialised request"
        }

        test "a codec built from explicit functions needs no JSON stack" {
            // The shared tier carries no serialiser: a module may supply
            // any encode/decode pair its tier can execute.
            let plain =
                ModuleQueryContract.create
                    "Reports"
                    "plain"
                    (ModuleQueryContract.codec id (fun s -> Ok s))
                    (ModuleQueryContract.codec string (fun s ->
                        match System.Int32.TryParse s with
                        | true, v -> Ok v
                        | _ -> Error(sprintf "\"%s\" is not an integer" s)))

            let bus =
                busOf [
                    "Reports", ModuleQueryContract.handler plain (fun _ req -> async { return req.Length })
                ]

            let length =
                ModuleQueryBus.askContract bus access plain "abcd"
                |> Async.RunSynchronously
                |> expectSomeOk "explicit-codec round-trip"

            Expect.equal length 4 "the module's own codecs round-tripped through the envelope"
        }
    ]

let tests =
    testList "ModuleQueryBus" [ contractTests; duplicateRejectionTests; contractTypedTests ]