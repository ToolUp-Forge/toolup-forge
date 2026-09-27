// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open Browser
open Browser.Types
open Fable.Core
open Fable.Core.JsInterop

module Http =

    /// Constructs default values for HttpRequest
    let private defaultRequestConfig: HttpRequest = {
        HttpMethod = GET
        Url = "/"
        Headers = []
        WithCredentials = false
        RequestBody = Empty
    }

    /// Creates a GET request to the specified url
    let get (url: string) : HttpRequest = {
        defaultRequestConfig with
            Url = url
            HttpMethod = GET
    }

    /// Creates a POST request to the specified url
    let post (url: string) : HttpRequest = {
        defaultRequestConfig with
            Url = url
            HttpMethod = POST
    }

    /// Creates a request using the given method and url
    let request method url = {
        defaultRequestConfig with
            Url = url
            HttpMethod = method
    }

    /// Appends a request with headers as key-value pairs
    let withHeaders headers (req: HttpRequest) = { req with Headers = headers }

    /// Sets the withCredentials option on the XHR request, useful for CORS requests
    let withCredentials withCredentials (req: HttpRequest) = {
        req with
            WithCredentials = withCredentials
    }

    /// Appends a request with string body content
    let withBody body (req: HttpRequest) = { req with RequestBody = body }

    /// Phase 855 — which browser API carries a proxy's request.
    [<RequireQualifiedAccess>]
    type Transport =
        /// `fetch` — the default. Connection reuse (HTTP/1.1 keep-alive,
        /// HTTP/2 multiplexing) is the browser's own behaviour for every
        /// `fetch`; the request-guard's `fetch` wrapper attaches identity
        /// and `X-CSRF-Token` to the request's `Headers` before dispatch, so
        /// this path needs no `XMLHttpRequest.prototype` patch.
        | Fetch
        /// `XMLHttpRequest` — the pre-855 transport, unchanged, for a
        /// consumer that opts out with `Http.useTransport Transport.Xhr`.
        | Xhr

    let mutable private transport = Transport.Fetch

    /// Phase 855 — select the transport every proxy request is sent with,
    /// page-wide, from the next request on. Streaming (`RemoteStream`)
    /// always uses `fetch` and is unaffected.
    let useTransport (selected: Transport) : unit = transport <- selected

    /// The transport proxy requests are currently sent with.
    let currentTransport () : Transport = transport

    let private sendAndReadXhr (preparation: (XMLHttpRequest -> unit) option) resultMapper (req: HttpRequest) = async {
        let! token = Async.CancellationToken

        let request =
            Async.FromContinuations
            <| fun (resolve, _, cancel) ->
                let xhr = XMLHttpRequest.Create()

                match req.HttpMethod with
                | GET -> xhr.``open`` ("GET", req.Url)
                | POST -> xhr.``open`` ("POST", req.Url)

                match preparation with
                | Some f -> f xhr
                | _ -> ignore ()

                let cancellationTokenRegistration =
                    token.Register(fun _ ->
                        xhr.abort ()
                        cancel (System.OperationCanceledException(token)))

                // set the headers, must be after opening the request
                for (key, value) in req.Headers do
                    xhr.setRequestHeader (key, value)

                xhr.withCredentials <- req.WithCredentials

                xhr.onreadystatechange <-
                    fun _ ->
                        match xhr.readyState with
                        | ReadyState.Done when not token.IsCancellationRequested ->
                            (cancellationTokenRegistration :> System.IDisposable).Dispose()
                            xhr |> resultMapper |> resolve
                        | _ -> ignore ()

                match req.RequestBody with
                | Empty -> xhr.send ()
                | RequestBody.Json content -> xhr.send (content)
                | Multipart blobs ->
                    let form = InternalUtilities.createFormData ()

                    for i in 0 .. blobs.Length - 1 do
                        form?append (i.ToString(), blobs.[i])

                    xhr.send form

        return! request
    }

    // ── Phase 855 — the fetch transport ─────────────────────────────────
    //
    // `keepalive: true` is deliberately NOT set. It does not mean HTTP
    // keep-alive (every `fetch` reuses connections already); it lets a
    // request outlive the page, and the browser caps the bodies of all
    // in-flight keepalive requests at 64 KiB together — a larger body is
    // refused before it is sent. Setting it would turn every large upload
    // into a network error.

    [<Emit("new AbortController()")>]
    let private newAbortController () : obj = jsNative

    // Resolved at CALL time, so the request-guard's wrapper (installed after
    // this module loads) is the `fetch` that runs.
    [<Emit("fetch($0, $1)")>]
    let private fetchRaw (url: string) (init: obj) : JS.Promise<obj> = jsNative

    // Settles with `[status, text]` (or `[status, ArrayBuffer]`); `ok` or
    // `failed` is called exactly once.
    [<Emit("""$0.then(function (r) { return $1 ? r.arrayBuffer().then(function (b) { return [r.status, b]; }) : r.text().then(function (t) { return [r.status, t]; }); }).then(function (v) { $2(v); }, function (e) { $3(e); })""")>]
    let private readResponse
        (response: JS.Promise<obj>)
        (binary: bool)
        (ok: int * obj -> unit)
        (failed: obj -> unit)
        : unit =
        jsNative

    let private sendAndReadFetch (binary: bool) (req: HttpRequest) : Async<int * obj> = async {
        let! token = Async.CancellationToken

        return!
            Async.FromContinuations
            <| fun (resolve, _, cancel) ->
                let controller = newAbortController ()
                let mutable settled = false

                let registration =
                    token.Register(fun _ ->
                        if not settled then
                            settled <- true
                            controller?abort ()
                            cancel (System.OperationCanceledException(token)))

                let init =
                    createObj [
                        "method"
                        ==> (match req.HttpMethod with
                             | GET -> "GET"
                             | POST -> "POST")
                        // Pairs, not an object: a repeated name is sent
                        // repeated, as `setRequestHeader` appends it.
                        "headers"
                        ==> (req.Headers |> List.map (fun (k, v) -> [| k; v |]) |> Array.ofList)
                        // XHR with `withCredentials = false` still sends
                        // same-origin cookies; `same-origin` is exactly that.
                        "credentials" ==> (if req.WithCredentials then "include" else "same-origin")
                        "signal" ==> controller?signal
                    ]

                match req.RequestBody with
                | Empty -> ()
                | RequestBody.Json content -> init?body <- content
                | Multipart blobs ->
                    let form = InternalUtilities.createFormData ()

                    for i in 0 .. blobs.Length - 1 do
                        form?append (i.ToString(), blobs.[i])

                    init?body <- form

                let finish (result: int * obj) =
                    if not settled then
                        settled <- true
                        (registration :> System.IDisposable).Dispose()
                        resolve result

                // A rejected `fetch` is a network failure (an abort has
                // already been answered by the registration above). XHR
                // reports that failure as status 0 with an empty body, and
                // the proxy's error categorisation reads exactly that — so
                // it is reported here in the same shape.
                let failed (_: obj) =
                    finish (
                        0,
                        (if binary then
                             box (InternalUtilities.createUInt8Array [||])
                         else
                             box "")
                    )

                try
                    readResponse (fetchRaw req.Url init) binary finish failed
                with error ->
                    failed (box error)
    }

    let private sendText (req: HttpRequest) : Async<HttpResponse> =
        match transport with
        | Transport.Xhr ->
            sendAndReadXhr
                None
                (fun xhr -> {
                    StatusCode = unbox xhr.status
                    ResponseBody = xhr.responseText
                })
                req
        | Transport.Fetch -> async {
            let! (status, body) = sendAndReadFetch false req

            return {
                StatusCode = status
                ResponseBody = unbox<string> body
            }
          }

    let private sendBinary (req: HttpRequest) : Async<byte[] * int> =
        match transport with
        | Transport.Xhr ->
            sendAndReadXhr
                (Some(fun xhr -> xhr.responseType <- "arraybuffer"))
                (fun xhr ->
                    // read response as byte array
                    let bytes = InternalUtilities.createUInt8Array xhr.response
                    (bytes, xhr.status))
                req
        | Transport.Fetch -> async {
            let! (status, body) = sendAndReadFetch true req
            return InternalUtilities.createUInt8Array body, status
          }
    // `send` / `sendAndReadBinary` stay function VALUES, as they were before
    // Phase 855 (a partial application then), so their public surface is
    // unchanged; the transport is read per call inside.
    let private asValue (send: HttpRequest -> 'r) : HttpRequest -> 'r = send

    /// Sends the request to the server and asynchronously returns a response
    let send: HttpRequest -> Async<HttpResponse> = asValue sendText

    /// Sends the request to the server and asynchronously returns the response as byte array
    let sendAndReadBinary: HttpRequest -> Async<byte[] * int> = asValue sendBinary