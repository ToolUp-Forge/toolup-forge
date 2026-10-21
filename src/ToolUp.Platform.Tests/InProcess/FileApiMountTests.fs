// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FileApiMountTests

// ─── Phase 1046 — the client calls the file API only where the server mounts it ───
//
// The server mounts `FileManagementApi` only when some composed module
// declares a data type, and the client shell used to call it whenever its
// data-manager slot was filled — so a deployment that filled the slot with a
// module that does not use forge's file API paid a 404 boot degradation and a
// recurring epoch-check failure. The fix is ONE predicate on the server
// (`BuildRouteHandlers.fileManagementMounted`) that both mounts the API and is
// advertised on `PlatformInfo.FileManagementMounted`; the client reads that
// advertisement before any file-API call.
//
// The claims are pinned where they are decided:
//
//   1. Through the REAL composed router, the advertisement equals the mount:
//      with no data type `ListFiles` is a 404 and `GetPlatformInfo` says
//      `false`; with one, the route answers and it says `true`. A test of the
//      predicate alone would pass unchanged if the router stopped using it.
//   2. The client's boot decision, fed the advertisement the server actually
//      sent, loads the snapshot exactly where the route exists — so the two
//      sides cannot drift.
//   3. `SessionEpoch` makes no read while unarmed (no data types: no call, no
//      "epoch check failed"), and armed it reads and still detects a reset.

open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ProcessedDataTypes
open ToolUp.Platform
open ToolUp.Platform.FileProcessor

// ─── Fixtures ─────────────────────────────────────────────────────────

/// A minimal `DataType` whose `Detect` / `Process` are never invoked — the
/// route composition reads only whether the list is empty.
let private stubDataType (id: string) : DataType = {
    Info = {
        Id = id
        DisplayName = id
        Schema = None
    }
    Id = id
    SchemaVersion = DataTypes.initialSchemaVersion
    Migrations = []
    Detect = fun _ -> async { return false }
    Process = fun _ -> async { return failwith "stub DataType.Process is never called by route composition" }
}

/// The route list exactly as `compose` mounts it: the real
/// `buildRouteHandlers` over `ServerConfig.defaults` with only the data
/// types varied.
let private composedRouter (dataTypes: DataType list) : HttpHandler list =
    let handlers =
        BuildRouteHandlers.buildRouteHandlers
            ServerConfig.defaults
            []
            dataTypes
            ComposeExtensions.empty
            []
            None
            NoNotifications
            (ref None)
            (ref None)

    [ handlers.Router [] ]

/// A logger that drops everything — the remoting error handler resolves one,
/// and a mounted-but-unprovisioned file API (no store registered here) must
/// answer with an error status rather than throw past the host.
let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// POST each remoting path against one fresh host; returns status and body
/// per path, in order.
let private postAll (routes: HttpHandler list) (paths: string list) = async {
    use host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<ILogger>(silentLogger) |> ignore)
                    .Configure(fun (app: IApplicationBuilder) ->
                        // A mounted file API reaches its handler, which fails
                        // here for want of a store; the error handler turns that
                        // into a 500, so "mounted" reads as any status but 404 —
                        // the route matched — exactly as an unmounted one 404s.
                        app
                            .UseGiraffeErrorHandler(fun _ _ -> setStatusCode 500 >=> text "handler reached")
                            .UseGiraffe(choose routes))
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()

    let! results =
        paths
        |> List.map (fun path -> async {
            use content = new StringContent("[]", Encoding.UTF8, "application/json")
            use! resp = client.PostAsync(path, content) |> Async.AwaitTask
            let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return resp.StatusCode, text
        })
        |> Async.Sequential

    do! host.StopAsync() |> Async.AwaitTask
    return List.ofArray results
}

let private platformInfoPath = "/api/PlatformInfoApi/GetPlatformInfo"
let private listFilesPath = "/api/FileManagementApi/ListFiles"
let private sessionInfoPath = "/api/FileManagementApi/GetSessionInfo"

/// What a client sees for a composition: the advertisement it decodes from
/// `GetPlatformInfo`, and whether the file API's routes resolve at all.
let private observe (dataTypes: DataType list) = async {
    let! results = postAll (composedRouter dataTypes) [ platformInfoPath; listFilesPath; sessionInfoPath ]

    match results with
    | [ (infoStatus, infoBody); (listStatus, _); (sessionStatus, _) ] ->
        Expect.equal infoStatus HttpStatusCode.OK "PlatformInfoApi is always mounted and ungated"
        use doc = JsonDocument.Parse infoBody
        let root = doc.RootElement

        let info: PlatformInfo = {
            RequiresAuth = root.GetProperty("RequiresAuth").GetBoolean()
            FileManagementMounted = root.GetProperty("FileManagementMounted").GetBoolean()
        }

        let routeMounted =
            listStatus <> HttpStatusCode.NotFound
            && sessionStatus <> HttpStatusCode.NotFound

        let routeAbsent =
            listStatus = HttpStatusCode.NotFound && sessionStatus = HttpStatusCode.NotFound

        Expect.isTrue (routeMounted || routeAbsent) "ListFiles and GetSessionInfo are mounted together or not at all"
        return info, routeMounted
    | other -> return failtestf "expected three responses, got %A" other
}

let private compositions = [ "no data types", []; "one data type", [ stubDataType "SalesData" ] ]

/// A counting epoch source: each read returns the next epoch in `epochs`
/// (the last one repeating), so a test sees both how many reads a check
/// made and what a reset looks like.
let private countingSource (epochs: System.Guid list) =
    let mutable calls = 0

    let read () = async {
        let epoch = epochs |> List.item (min calls (epochs.Length - 1))
        calls <- calls + 1

        return ({ Epoch = epoch; FileCount = 0 }: SessionStoreInfo)
    }

    read, (fun () -> calls)

/// Leave the process-wide `SessionEpoch` exactly as a fresh tab has it.
let private disarm () =
    SessionEpoch.useSessionInfoSource None
    SessionEpoch.reset ()

[<Tests>]
let tests =
    testList "Phase 1046 — the client calls the file API only where the server mounts it" [

        test "the server predicate is 'some module declares a data type'" {
            Expect.isFalse (BuildRouteHandlers.fileManagementMounted []) "no data type ⇒ not mounted"

            Expect.isTrue
                (BuildRouteHandlers.fileManagementMounted [ stubDataType "SalesData" ])
                "a data type ⇒ mounted"
        }

        testAsync "no data types: the file API 404s and PlatformInfo advertises it as not mounted" {
            let! info, routeMounted = observe []
            Expect.isFalse routeMounted "no data type ⇒ FileManagementApi is not mounted"
            Expect.isFalse info.FileManagementMounted "the advertisement says so"
        }

        testAsync "data types: the file API answers and PlatformInfo advertises it as mounted" {
            let! info, routeMounted = observe [ stubDataType "SalesData" ]
            Expect.isTrue routeMounted "a data type ⇒ FileManagementApi is mounted"
            Expect.isTrue info.FileManagementMounted "the advertisement says so"
        }

        testAsync "client and server agree: the boot snapshot load happens exactly where the route exists" {
            for name, dataTypes in compositions do
                let! info, routeMounted = observe dataTypes

                // A filled data-manager slot — the FortnumInsights shape the
                // phase is about, and the only case the shell ever loaded in.
                Expect.equal
                    (Client.loadsDataSnapshotOnBoot (Some "data-manager") info)
                    routeMounted
                    (sprintf "%s: the client loads the snapshot iff the server mounted the API" name)

                Expect.isFalse
                    (Client.loadsDataSnapshotOnBoot None info)
                    (sprintf "%s: an empty data-manager slot never loads the snapshot" name)
        }

        testSequenced
        <| testList "SessionEpoch reads only where the file API is mounted" [

            testAsync "unarmed (no data types): a check makes no read and reports nothing" {
                try
                    let read, calls = countingSource [ System.Guid.NewGuid() ]
                    SessionEpoch.useSessionInfoSource (Some read)
                    // The advertisement for a composition with no data type.
                    SessionEpoch.setFileApiMounted false

                    let! first = SessionEpoch.check ()
                    let! second = SessionEpoch.check ()

                    Expect.equal (calls ()) 0 "no GetSessionInfo call while the file API is not mounted"
                    Expect.isNone first "no reset reported"
                    Expect.isNone second "no reset reported"
                    Expect.isNone (SessionEpoch.current ()) "nothing cached"
                    Expect.isFalse (SessionEpoch.fileApiMounted ()) "disarmed"
                finally
                    disarm ()
            }

            testAsync "armed (data types): the check reads the epoch and still detects a reset" {
                try
                    let before, after = System.Guid.NewGuid(), System.Guid.NewGuid()
                    let read, calls = countingSource [ before; after ]
                    SessionEpoch.useSessionInfoSource (Some read)

                    do! SessionEpoch.prime ()
                    Expect.equal (SessionEpoch.current ()) (Some before) "prime seeds the epoch"

                    let! reset = SessionEpoch.check ()
                    Expect.equal (calls ()) 2 "one read per check"

                    match reset with
                    | Some notice ->
                        Expect.equal notice.PreviousEpoch (Some before) "the epoch this tab held"
                        Expect.equal notice.CurrentEpoch after "the store now behind it"
                    | None -> failtest "a moved epoch is a reset"
                finally
                    disarm ()
            }

            test "disarming drops the cached epoch" {
                try
                    let read, _ = countingSource [ System.Guid.NewGuid() ]
                    SessionEpoch.useSessionInfoSource (Some read)
                    SessionEpoch.prime () |> Async.RunSynchronously
                    Expect.isSome (SessionEpoch.current ()) "seeded"

                    SessionEpoch.setFileApiMounted false
                    Expect.isNone (SessionEpoch.current ()) "an epoch from an unmounted store is not kept"
                finally
                    disarm ()
            }
        ]
    ]