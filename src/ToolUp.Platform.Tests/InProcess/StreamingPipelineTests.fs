module ToolUp.Platform.Tests.InProcess.StreamingPipelineTests

open System
open System.Collections.Generic
open System.Net.Http
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Remoting
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
open ToolUp.Remoting.AspNetCore

// ─── A streaming call is a call ──────────────────────────────────────────
//
// The request pipeline is one list of stages, and a streaming method runs
// through all of it: the producer is handed the request's abort token, the
// bridge holds a producer that outruns its consumer, the argument decodes
// through the same seam as a unary call's (a malformed one is a 400, never
// a call on a constructed default), and a streaming method may carry an
// authorisation marker that the pipeline enforces.
//
// The second half is the shared seam pack: the same requests against the
// Giraffe adapter and the ASP.NET Core middleware, which run one list of
// stages and so answer identically.

// NOT private — the wire converters + dispatcher proxy reflect over the shapes.
type StreamRequest = { Upto: int }

type PipelineStreamApi = {
    [<AllowAnonymous>]
    Endless: int -> IAsyncEnumerable<int>
    [<AllowAnonymous>]
    Count: StreamRequest -> IAsyncEnumerable<int>
}

type GatedStreamApi = {
    [<RequiresRole "Admin">]
    Secret: int -> IAsyncEnumerable<int>
}

type private Caller(roles: string list) =
    interface IAuthContext with
        member _.HasRole role = List.contains role roles
        member _.HasClaim(_, _) = false
        member _.HasTenant() = false
        member _.IsAnonymous() = false
        member _.SubjectId = "user:pinned"

let private never (_: int) = false

/// A producer that emits forever until its token is signalled, and reports
/// the moment the token it was started with fires.
let private endlessImpl (signalled: TaskCompletionSource<bool>) : PipelineStreamApi = {
    Endless =
        fun _ ->
            AsyncStream.fromCallback never (fun emit -> async {
                let! token = Async.CancellationToken
                token.Register(fun () -> signalled.TrySetResult true |> ignore) |> ignore
                let mutable i = 0

                while not token.IsCancellationRequested do
                    emit i
                    i <- i + 1
                    do! Async.Sleep 10
            })
    Count =
        fun request ->
            AsyncStream.fromCallback (fun n -> n >= request.Upto) (fun emit -> async {
                for i in 1 .. request.Upto do
                    emit i
            })
}

let private gatedImpl: GatedStreamApi = {
    Secret = fun n -> AsyncStream.fromCallback (fun i -> i >= n) (fun emit -> async { emit n })
}

let private giraffeHost (handler: HttpHandler) : IHost =
    Host
        .CreateDefaultBuilder()
        .ConfigureWebHostDefaults(fun webHost ->
            webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseGiraffe handler)
            |> ignore)
        .Build()

let private post (client: HttpClient) (path: string) (body: string) = task {
    use request = new HttpRequestMessage(HttpMethod.Post, path)
    request.Headers.Add("x-remoting-proxy", "true")
    request.Content <- new StringContent(body, Encoding.UTF8, "application/json")
    let! response = client.SendAsync request
    let! text = response.Content.ReadAsStringAsync()
    return int response.StatusCode, text
}

/// A test whose body is a task, awaited on the test thread.
let private testT (name: string) (body: unit -> Task) =
    testCase name (fun () -> (body ()).GetAwaiter().GetResult())

let private pins =
    testList "the four pins" [

        testT "a client that aborts mid-stream signals the producer's token" (fun () -> task {
            let signalled =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let handler =
                Remoting.createApi ()
                |> Remoting.fromValue (endlessImpl signalled)
                |> Remoting.buildHttpHandler

            use host = giraffeHost handler
            do! host.StartAsync()
            use client = host.GetTestClient()
            use request = new HttpRequestMessage(HttpMethod.Post, "/PipelineStreamApi/Endless")
            request.Headers.Add("x-remoting-proxy", "true")
            request.Content <- new StringContent("[1]", Encoding.UTF8, "application/json")
            let! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            let! stream = response.Content.ReadAsStreamAsync()
            let buffer = Array.zeroCreate<byte> 256
            let! read = stream.ReadAsync(buffer, 0, buffer.Length)
            Expect.isGreaterThan read 0 "the stream started before the client left"
            // The client leaves: the response is disposed mid-stream.
            stream.Dispose()
            response.Dispose()

            let! fired = Task.WhenAny(signalled.Task, Task.Delay(TimeSpan.FromSeconds 10.0))
            Expect.isTrue (obj.ReferenceEquals(fired, signalled.Task)) "the producer's token was signalled"
            do! host.StopAsync()
        })

        testT "a producer faster than its consumer is held by back-pressure" (fun () -> task {
            let emitted = ref 0

            let source =
                AsyncStream.fromCallback never (fun emit -> async {
                    for i in 1..10000 do
                        emit i
                        Interlocked.Increment emitted |> ignore
                })

            let enumerator = source.GetAsyncEnumerator CancellationToken.None
            let! moved = enumerator.MoveNextAsync()
            Expect.isTrue moved "the first value arrived"
            do! Task.Delay 300
            Expect.isLessThan emitted.Value 10000 "the producer is held, not buffered without bound"
            do! enumerator.DisposeAsync()
        })

        testT "a malformed first argument on a streaming method is a 400 refusal envelope" (fun () -> task {
            let handler =
                Remoting.createApi ()
                |> Remoting.fromValue (endlessImpl (TaskCompletionSource<bool>()))
                |> Remoting.buildHttpHandler

            use host = giraffeHost handler
            do! host.StartAsync()
            use client = host.GetTestClient()
            let! status, body = post client "/PipelineStreamApi/Count" """[{"Upto":"not a number"}]"""
            Expect.equal status 400 "refused, not streamed from a default"
            Expect.stringContains body "\"category\":\"validation\"" "the validation envelope"
            Expect.stringContains body "decodeError" "naming the decode error"
            Expect.isFalse (body.Contains "event: chunk") "the method never ran"
            do! host.StopAsync()
        })

        testT "a role-gated streaming method composes and refuses an unauthorised caller" (fun () -> task {
            let compose roles =
                Remoting.createApi ()
                |> Remoting.fromValue gatedImpl
                |> Remoting.withAuthContext (fun (_: HttpContext) -> async { return Caller roles :> IAuthContext })
                |> Remoting.buildHttpHandler

            let refused = compose []
            use host = giraffeHost refused
            do! host.StartAsync()
            use client = host.GetTestClient()
            let! status, body = post client "/GatedStreamApi/Secret" "[2]"
            Expect.equal status 401 "an unauthorised caller is refused before the producer runs"
            Expect.stringContains body "auth-denied" "the redacted auth envelope"
            do! host.StopAsync()

            use admitted = giraffeHost (compose [ "Admin" ])
            do! admitted.StartAsync()
            use adminClient = admitted.GetTestClient()
            let! adminStatus, adminBody = post adminClient "/GatedStreamApi/Secret" "[2]"
            Expect.equal adminStatus 200 "an authorised caller streams"
            Expect.stringContains adminBody "event: complete" "to the end"
            do! admitted.StopAsync()
        })
    ]

[<Tests>]
let tests = testList "Phase 1056 — streaming inside the request pipeline" [ pins ]