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
// Before Giraffe: its `HttpHandler` is the one this file means.
open ToolUp.Remoting.AspNetCore
open Giraffe
open ToolUp.Remoting
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

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

// ─── The shared seam pack: both adapters run the same stages ─────────────

type SeamInput = {
    [<MinLength 3>]
    Name: string
}

type SeamApi = {
    [<RequiresRole "Admin">]
    Gated: int -> Async<int>
    [<AllowAnonymous>]
    Validated: SeamInput -> Async<int>
    [<AllowAnonymous; RateLimit(1, 3600)>]
    Limited: int -> Async<int>
    [<AllowAnonymous; Idempotent>]
    Once: int -> Async<int>
    [<AllowAnonymous>]
    Count: StreamRequest -> IAsyncEnumerable<int>
}

let private seamImpl: SeamApi = {
    Gated = fun n -> async { return n }
    Validated = fun input -> async { return input.Name.Length }
    Limited = fun n -> async { return n }
    Once = fun n -> async { return n + 1 }
    Count =
        fun request ->
            AsyncStream.fromCallback (fun n -> n >= request.Upto) (fun emit -> async {
                for i in 1 .. request.Upto do
                    emit i
            })
}

/// One composition, every seam armed, as each adapter mounts it.
let private seamOptions () =
    Remoting.createApi ()
    |> Remoting.fromValue seamImpl
    |> Remoting.withAuthContext (fun (_: HttpContext) -> async { return Caller [] :> IAuthContext })
    |> Remoting.withRateLimitStore (InMemoryRateLimitStore())
    |> Remoting.withIdempotencyStore (InMemoryIdempotencyStore())

let private adapters: (string * (unit -> IHost)) list = [
    "Giraffe", (fun () -> giraffeHost (seamOptions () |> Remoting.buildHttpHandler))
    "AspNetCore",
    (fun () ->
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost.UseTestServer().Configure(fun (app: IApplicationBuilder) -> app.UseRemoting(seamOptions ()))
                |> ignore)
            .Build())
]

/// The answers one host gives to the pack's requests, in order.
let private seamAnswers (host: IHost) = task {
    do! host.StartAsync()
    use client = host.GetTestClient()
    let! gated = post client "/SeamApi/Gated" "[1]"
    let! invalid = post client "/SeamApi/Validated" """[{"Name":"ab"}]"""
    let! malformed = post client "/SeamApi/Validated" """[{"Name":42}]"""
    let! firstLimited = post client "/SeamApi/Limited" "[1]"
    let! secondLimited = post client "/SeamApi/Limited" "[1]"
    let! keyless = post client "/SeamApi/Once" "[1]"
    let! stream = post client "/SeamApi/Count" """[{"Upto":2}]"""
    do! host.StopAsync()
    return [ gated; invalid; malformed; firstLimited; secondLimited; keyless; stream ]
}

let private adapterParity =
    testList "both adapters run every seam" [
        for name, build in adapters do
            testT name (fun () -> task {
                use host = build ()
                let! answers = seamAnswers host

                match answers with
                | [ gated; invalid; malformed; firstLimited; secondLimited; keyless; stream ] ->
                    Expect.equal (fst gated) 401 "authorisation refuses a caller without the role"
                    Expect.equal (fst invalid) 400 "validation refuses a short name"
                    Expect.stringContains (snd invalid) "violations" "with its violations"
                    Expect.equal (fst malformed) 400 "a body that does not decode is refused"
                    Expect.stringContains (snd malformed) "decodeError" "naming the decode error"
                    Expect.equal (fst firstLimited) 200 "the first call is within its budget"
                    Expect.equal (fst secondLimited) 429 "the second is over it"
                    Expect.equal (fst keyless) 400 "an idempotent method requires its key"
                    Expect.equal (fst stream) 200 "a stream is served"
                    Expect.stringContains (snd stream) "event: complete" "to its terminal frame"
                | other -> failtestf "expected seven answers, got %d" other.Length
            })
        testT "the two adapters answer byte for byte alike" (fun () -> task {
            let! giraffe = seamAnswers ((snd adapters[0]) ())
            let! aspNetCore = seamAnswers ((snd adapters[1]) ())

            // A stream's chunk ids carry each call's own correlation id.
            let comparable answers = answers |> List.take 6

            Expect.equal (comparable aspNetCore) (comparable giraffe) "the same status and body for every seam"
        })
    ]

[<Tests>]
let tests =
    testList "Phase 1056 — streaming inside the request pipeline" [ pins; adapterParity ]