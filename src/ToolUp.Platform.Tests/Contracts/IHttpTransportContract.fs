module ToolUp.Platform.Tests.Contracts.IHttpTransportContract

open System
open Expecto
open ToolUp.Platform.Transport

// ─── IHttpTransport contract pack (Phase 128) ───────────────────────
//
// Parametrised tests for any `IHttpTransport` implementation — a host
// adapter (the BCL `HttpClientTransport`) or a decorator over another
// transport (the outbound rate budget). The pack owns a STUB SERVER: a
// pure function from the request as it arrived on the wire (absolute URL)
// to the reply, or `None` for "no connection". A binding's factory wires
// its implementation to that stub at `StubBase` without any network, and
// the pack asserts what the seam promises its callers:
//
//   1. A 2xx comes back as a response: status, body and response headers
//      carried through; the verb, request headers and body reach the wire.
//   2. A non-2xx comes back as a RESPONSE, never as an exception — status
//      classification is the caller's (`TransportClassifier`).
//   3. A connection failure RAISES. `HttpCall.attempt` relies on exactly
//      this shape: it classifies the exception as `TransportError.Network`.
//   4. A relative URL resolves against the transport's base address; an
//      absolute URL (a provider's next-page link) is used as given.
//
// These were the seam's de-facto behaviour since Phase 251; the pack is
// where they are first written down as a contract.

/// The base address every binding configures its transport with.
let StubBase = Uri "http://stub.test/"

/// The request as the stub server sees it: the absolute URL it arrived on.
type WireRequest = {
    Method: string
    Url: string
    Headers: (string * string) list
    Body: string option
}

/// The stub server: the reply to a request, or `None` for a connection
/// failure (the binding raises, as a real socket would).
type StubServer = WireRequest -> HttpResponse option

let private server: StubServer =
    fun request ->
        let path = Uri(request.Url).AbsolutePath

        match path with
        | "/refused" -> None
        | "/missing" ->
            Some {
                StatusCode = 404
                Headers = [ "x-reason", "no such item" ]
                Body = "not found"
            }
        | "/broken" ->
            Some {
                StatusCode = 503
                Headers = []
                Body = "down"
            }
        | _ ->
            let echoedHeader =
                request.Headers
                |> List.tryFind (fun (k, _) -> String.Equals(k, "x-request", StringComparison.OrdinalIgnoreCase))
                |> Option.map snd
                |> Option.defaultValue ""

            Some {
                StatusCode = 200
                Headers = [ "x-trace", "trace-1"; "x-echo", echoedHeader ]
                Body = $"{request.Method} {request.Url} {defaultArg request.Body String.Empty}"
            }

let private header (name: string) (response: HttpResponse) =
    response.Headers
    |> List.tryFind (fun (k, _) -> String.Equals(k, name, StringComparison.OrdinalIgnoreCase))
    |> Option.map snd

/// Bind an implementation: `factory server` returns a transport whose wire
/// leads to `server`, configured with base address `StubBase`.
let tests (name: string) (factory: StubServer -> IHttpTransport) =
    testList $"{name} — IHttpTransport contract" [
        testCaseAsync "a 2xx comes back with status, body and response headers"
        <| async {
            let transport = factory server
            let! response = transport.Send(HttpCall.get "/items" [ "x-request", "r-1" ])
            Expect.equal response.StatusCode 200 "status carried"
            Expect.equal response.Body "GET http://stub.test/items " "body carried"
            Expect.equal (header "x-trace" response) (Some "trace-1") "response headers carried"
            Expect.equal (header "x-echo" response) (Some "r-1") "request headers reached the wire"
            Expect.isTrue (HttpResponse.isSuccess response) "isSuccess"
        }

        testCaseAsync "the verb and the request body reach the wire"
        <| async {
            let transport = factory server
            let! response = transport.Send(HttpRequest.post "/items" [] """{"a":1}""")
            Expect.equal response.Body """POST http://stub.test/items {"a":1}""" "verb, URL and body on the wire"
        }

        testCaseAsync "a non-2xx comes back as a response, not an exception"
        <| async {
            let transport = factory server
            let! missing = transport.Send(HttpCall.get "/missing" [])
            Expect.equal missing.StatusCode 404 "404 returned as data"
            Expect.equal missing.Body "not found" "error body carried"
            Expect.equal (header "x-reason" missing) (Some "no such item") "error headers carried"
            let! broken = transport.Send(HttpCall.get "/broken" [])
            Expect.equal broken.StatusCode 503 "5xx returned as data"
            Expect.isFalse (HttpResponse.isSuccess broken) "isSuccess false"
        }

        testCaseAsync "a connection failure raises — the shape HttpCall.attempt classifies as Network"
        <| async {
            let transport = factory server
            let! outcome = transport.Send(HttpCall.get "/refused" []) |> Async.Catch

            match outcome with
            | Choice2Of2 _ -> ()
            | Choice1Of2 response -> failtestf "expected the send to raise, got %A" response

            let! classified = HttpCall.attempt transport (HttpCall.get "/refused" [])

            match classified with
            | Error(TransportError.Network _) -> ()
            | other -> failtestf "expected TransportError.Network, got %A" other
        }

        testCaseAsync "a relative URL resolves against the base address; an absolute one is used as given"
        <| async {
            let transport = factory server
            let! relative = transport.Send(HttpCall.get "/v1/items?after=2" [])
            Expect.equal relative.Body "GET http://stub.test/v1/items?after=2 " "relative resolved against the base"
            let! absolute = transport.Send(HttpCall.get "http://other.test/v1/items?offset=4" [])
            Expect.equal absolute.Body "GET http://other.test/v1/items?offset=4 " "absolute used as given"
        }
    ]