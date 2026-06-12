module ToolUp.Platform.Tests.InProcess.GiraffeStockHelperTests

open System.IO
open System.Net
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Expecto
open ToolUp.Platform

// ─── Giraffe stock-helper DI defaults — regression coverage ──────────
//
// The SDK pipeline mounts Giraffe (`app.UseGiraffe` in
// `configurePipeline`) but, before `ComposeBootstrap.registerGiraffeDefaults`
// landed, the service composition never registered the services Giraffe's
// stock response helpers resolve from DI per request: `INegotiationConfig`
// (the negotiating `RequestErrors.*` / `ServerErrors.*` / `Successful.*`
// family plus `negotiate` / `negotiateWith`), `Json.ISerializer` (the
// `json` helper) and `Xml.ISerializer` (the `xml` helper). A consumer
// route handler reaching for any of them threw
// `Giraffe.MissingDependencyException` at request time — masked by the
// exception middleware as an opaque 500, even on the error branches
// (`RequestErrors.BAD_REQUEST`) that should have explained the problem.
//
// These tests stand up a `TestServer` whose ONLY Giraffe-related service
// registration is the real `ComposeBootstrap.registerGiraffeDefaults` —
// deliberately no test-side `services.AddGiraffe()` — and pin:
//
//   1. `RequestErrors.BAD_REQUEST` returns the intended 400 + body.
//   2. `json` returns 200 with **Fable-shaped** JSON (option payloads
//      unwrapped, nullary DU cases as bare strings, property names as
//      declared) — the FableConverters wire, not Giraffe's camelCase
//      default.
//   3. A consumer-registered `Json.ISerializer` / `INegotiationConfig`
//      beats the SDK default (the production ordering: the consumer's
//      `ServiceConfig` hook runs first, `registerGiraffeDefaults`'s
//      `TryAdd` registrations then no-op).

// Wire-shape fixtures. NOT `private`: the record/union converters
// reflect over the type shape, same constraint as the
// `PlatformPeerTests.DirectoryContract` precedent.
type WidgetStatus =
    | ActiveWidget
    | RetiredWidget

type Widget = {
    Name: string
    Count: int option
    Status: WidgetStatus
}

/// Consumer-shaped routes: the happy path uses the `json` helper, the
/// error path uses the negotiating `RequestErrors.BAD_REQUEST` — the
/// exact pair that 500'd in a consumer deployment before the fix.
let private searchRoutes: HttpHandler =
    route "/search"
    >=> fun next ctx ->
        match ctx.TryGetQueryStringValue "q" with
        | Some q ->
            json
                {
                    Name = q
                    Count = Some 5
                    Status = ActiveWidget
                }
                next
                ctx
        | None -> RequestErrors.BAD_REQUEST "Provide ?q=..." next ctx

/// `TestServer` host with the given service registrations + a Giraffe
/// terminal. Mirrors the `PlatformPeerTests.buildSellerHost` shape.
let private buildHost (configureServices: IServiceCollection -> unit) (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost
                .UseTestServer()
                .ConfigureServices(fun services -> configureServices services)
                .Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

let private startAndGet (configureServices: IServiceCollection -> unit) (path: string) = async {
    use host = buildHost configureServices searchRoutes
    do! host.StartAsync() |> Async.AwaitTask
    use client = host.GetTestClient()
    let! response = client.GetAsync(path) |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

    let contentType =
        match response.Content.Headers.ContentType with
        | null -> ""
        | ct -> ct.ToString()

    do! host.StopAsync() |> Async.AwaitTask
    return response.StatusCode, contentType, body
}

/// Consumer-registered `Json.ISerializer` stand-in — every serialize
/// surface emits a recognisable sentinel so the tests can prove which
/// serializer handled the response.
type SentinelJsonSerializer() =
    static member val Payload = "\"sentinel-serializer\""

    interface Json.ISerializer with
        member _.SerializeToString(_: 'T) = SentinelJsonSerializer.Payload

        member _.SerializeToBytes(_: 'T) =
            Encoding.UTF8.GetBytes SentinelJsonSerializer.Payload

        member _.SerializeToStreamAsync (_: 'T) (stream: Stream) =
            let bytes = Encoding.UTF8.GetBytes SentinelJsonSerializer.Payload
            stream.WriteAsync(bytes, 0, bytes.Length)

        member _.Deserialize<'T>(_: string) : 'T = failwith "not exercised by these tests"

        member _.Deserialize<'T>(_: byte array) : 'T = failwith "not exercised by these tests"

        member _.DeserializeAsync<'T>(_: Stream) : Task<'T> = failwith "not exercised by these tests"

/// Consumer-registered `INegotiationConfig` stand-in — a single rule
/// that stamps a recognisable status + body regardless of payload.
type SentinelNegotiationConfig() =
    interface INegotiationConfig with
        member _.Rules =
            dict [ "*/*", (fun (_: obj) -> setStatusCode 418 >=> text "negotiated-by-consumer") ]

        member _.UnacceptableHandler = setStatusCode 406 >=> text "unacceptable"

let tests =
    testList "Giraffe stock-helper DI defaults (registerGiraffeDefaults)" [

        testCaseAsync "RequestErrors.BAD_REQUEST returns 400 + message body with no consumer-side AddGiraffe"
        <| async {
            let! status, _, body = startAndGet ComposeBootstrap.registerGiraffeDefaults "/search"

            Expect.equal status HttpStatusCode.BadRequest "negotiating error helper resolves INegotiationConfig"

            // No Accept header → the first negotiation rule (*/* → json)
            // serialises the message; the FableConverters string writer
            // emits the JSON-encoded string.
            Expect.stringContains body "Provide ?q=" "the error branch explains itself instead of an opaque 500"
        }

        testCaseAsync "json helper returns 200 with Fable-shaped JSON (FableConverters wire, not Giraffe's default)"
        <| async {
            let! status, contentType, body = startAndGet ComposeBootstrap.registerGiraffeDefaults "/search?q=widget"

            Expect.equal status HttpStatusCode.OK "json helper resolves Json.ISerializer"
            Expect.stringStarts contentType "application/json" "json helper stamps the JSON content type"

            // Fable.SimpleJson wire: property names as declared (no
            // camelCase policy), `Some 5` unwrapped to `5`, nullary DU
            // case as the bare string `"ActiveWidget"`. Giraffe's own
            // default serializer produces none of these shapes.
            Expect.equal
                body
                """{"Name":"widget","Count":5,"Status":"ActiveWidget"}"""
                "json helper output matches the platform wire format"

            // Belt-and-braces: the body round-trips through the SDK's
            // shared wire options back to the original value.
            let roundTripped =
                System.Text.Json.JsonSerializer.Deserialize<Widget>(
                    body,
                    ToolUp.Remoting.Json.SystemTextJson.FableConverters.shared
                )

            Expect.equal
                roundTripped
                {
                    Name = "widget"
                    Count = Some 5
                    Status = ActiveWidget
                }
                "wire round-trips through FableConverters.shared"
        }

        testCaseAsync "consumer-registered Json.ISerializer wins over the SDK default"
        <| async {
            // Production ordering: the consumer/companion `ServiceConfig`
            // hook runs BEFORE `registerGiraffeDefaults`, whose `TryAdd`
            // registrations then no-op against the existing descriptor.
            let configure (services: IServiceCollection) =
                services.AddSingleton<Json.ISerializer>(SentinelJsonSerializer()) |> ignore
                ComposeBootstrap.registerGiraffeDefaults services

            let! status, _, body = startAndGet configure "/search?q=widget"

            Expect.equal status HttpStatusCode.OK "route still serves"
            Expect.equal body SentinelJsonSerializer.Payload "consumer serializer handled the response"
        }

        testCaseAsync "consumer-registered INegotiationConfig wins over the SDK default"
        <| async {
            let configure (services: IServiceCollection) =
                services.AddSingleton<INegotiationConfig>(SentinelNegotiationConfig()) |> ignore

                ComposeBootstrap.registerGiraffeDefaults services

            // The error branch negotiates; the consumer rule stamps its
            // own status + body over BAD_REQUEST's 400.
            let! status, _, body = startAndGet configure "/search"

            Expect.equal status ((enum<HttpStatusCode> 418)) "consumer negotiation rule set the status"
            Expect.equal body "negotiated-by-consumer" "consumer negotiation rule wrote the body"
        }
    ]