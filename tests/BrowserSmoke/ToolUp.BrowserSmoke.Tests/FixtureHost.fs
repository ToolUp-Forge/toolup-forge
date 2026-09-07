// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The server half of the browser smoke harness: the built fixture
/// bundle plus a module-owned co-editing API over a real
/// `ICrdtDocumentStore`.
module ToolUp.BrowserSmoke.Tests.FixtureHost

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform

// ─── Phase 761 — the harness's server ────────────────────────────────
//
// Deliberately `HttpListener` and not an ASP.NET Core host. What the
// scenarios need from a server is a static file root and two JSON
// endpoints; standing up Kestrel for that would put the SDK's whole
// composition root — configuration, DI, middleware order, auth — inside
// a smoke gate, where every one of those is a way for the gate to fail
// for a reason that is not the browser. `HttpListener` is BCL, so this
// harness adds exactly one package to the repo's dependency graph
// (`Microsoft.Playwright`, already present since Phase 126) and none at
// all to any shipped one (GP 1 / GP 2).
//
// The CRDT store, by contrast, is the REAL one:
// `InMemoryCrdtDocumentStore` off `ToolUp.Platform.Server`. The wire
// endpoints below are the ten lines Phase 535 says every consumer
// writes for itself — it registers the substrate and mounts no route —
// so the harness is a consumer of the seam rather than a re-statement
// of it.
//
// ## The port
//
// An OS-assigned free loopback port, taken by binding a `TcpListener`
// to port 0 and reading back what the OS gave. The harness therefore
// CLAIMS NOTHING in the workspace port bands: it is a test process, not
// an app, and two smoke runs on one machine (or a run beside a
// developer's own dev server) must not collide. The bind-close-rebind
// window is a race in principle; in practice the OS does not re-issue
// the port within it, and a failure here is a loud bind error rather
// than a silent wrong answer.

/// The scope every request resolves to. **Server-side, always.** The
/// client names only the document; a client that could name a scope
/// could name another team's (GP 4), which is why `CrdtDocRef` is
/// assembled here and the wire carries a bare `doc`.
[<Literal>]
let private Scope = "browser-smoke"

let private freeLoopbackPort () =
    let probe = TcpListener(IPAddress.Loopback, 0)
    probe.Start()
    let port = (probe.LocalEndpoint :?> IPEndPoint).Port
    probe.Stop()
    port

let private contentTypeOf (path: string) =
    match Path.GetExtension(path).ToLowerInvariant() with
    | ".html" -> "text/html; charset=utf-8"
    | ".js" -> "text/javascript; charset=utf-8"
    | ".css" -> "text/css; charset=utf-8"
    | ".json"
    | ".map" -> "application/json; charset=utf-8"
    | ".png" -> "image/png"
    | ".svg" -> "image/svg+xml"
    | ".woff2" -> "font/woff2"
    | _ -> "application/octet-stream"

let private readBody (request: HttpListenerRequest) =
    use reader = new StreamReader(request.InputStream, request.ContentEncoding)
    reader.ReadToEnd()

let private writeJson (response: HttpListenerResponse) (json: string) =
    let bytes = Encoding.UTF8.GetBytes json
    response.StatusCode <- 200
    response.ContentType <- "application/json; charset=utf-8"
    response.ContentLength64 <- int64 bytes.Length
    response.OutputStream.Write(bytes, 0, bytes.Length)

/// A running harness host. `BaseUrl` is what a page is pointed at;
/// `Store` is the same instance the endpoints write through, so an
/// assertion can ask the SERVER what it holds rather than believing the
/// page's own account of itself.
type Host = {
    BaseUrl: string
    Store: ICrdtDocumentStore
    /// Every update logged for `docId`, oldest first.
    Updates: string -> CrdtUpdate list
    Stop: unit -> unit
}

/// Serve `distDir` (the Vite bundle) plus the co-editing API.
let start (distDir: string) : Host =
    if not (Directory.Exists distDir) then
        failwithf
            "browser-smoke: the fixture bundle is missing at %s. Build it with the VerifyBrowserSmoke target, which runs npm ci + dotnet fable + npm run build in tests/BrowserSmoke/fixture."
            distDir

    let store = InMemoryCrdtDocumentStore() :> ICrdtDocumentStore
    let port = freeLoopbackPort ()
    let baseUrl = sprintf "http://127.0.0.1:%d" port
    let listener = new HttpListener()
    listener.Prefixes.Add(sprintf "http://127.0.0.1:%d/" port)
    listener.Start()

    let cancellation = new CancellationTokenSource()

    let refFor (docId: string) = CrdtDocRef.create Scope docId

    let handleAppend (body: string) (response: HttpListenerResponse) =
        use parsed = JsonDocument.Parse body
        let root = parsed.RootElement
        let docId = root.GetProperty("doc").GetString()
        let session = root.GetProperty("session").GetString()
        let payload = Convert.FromBase64String(root.GetProperty("payload").GetString())
        store.Append(refFor docId, payload, session) |> Async.RunSynchronously |> ignore
        writeJson response "{}"

    let handleDiff (body: string) (response: HttpListenerResponse) =
        use parsed = JsonDocument.Parse body
        let root = parsed.RootElement
        let docId = root.GetProperty("doc").GetString()

        let since =
            StateVector.ofBytes (Convert.FromBase64String(root.GetProperty("since").GetString()))

        let docRef = refFor docId

        let updates = store.GetDiff(docRef, since) |> Async.RunSynchronously
        let vector = store.GetStateVector docRef |> Async.RunSynchronously

        // Hand-written rather than serialised through the SDK's
        // converter set: the fixture parses this with `JSON.parse` and a
        // handful of dynamic reads, so the wire shape is three fields
        // and stating it literally is shorter than agreeing on a codec.
        let rows =
            updates
            |> List.map (fun update ->
                sprintf
                    """{"payload":%s,"session":%s,"sequence":%d}"""
                    (JsonSerializer.Serialize(Convert.ToBase64String update.Payload))
                    (JsonSerializer.Serialize update.OriginSession)
                    update.Sequence)
            |> String.concat ","

        writeJson
            response
            (sprintf
                """{"updates":[%s],"vector":%s}"""
                rows
                (JsonSerializer.Serialize(Convert.ToBase64String vector.Bytes)))

    let serveStatic (path: string) (response: HttpListenerResponse) =
        let relative = if path = "/" then "index.html" else path.TrimStart '/'
        let full = Path.GetFullPath(Path.Combine(distDir, relative))

        // Containment check: a smoke harness serves a build directory to
        // a browser it also drives, so traversal is not a threat model
        // here — but a harness that can be talked out of its root is a
        // harness whose failures are hard to read.
        if
            full.StartsWith(Path.GetFullPath distDir, StringComparison.Ordinal)
            && File.Exists full
        then
            let bytes = File.ReadAllBytes full
            response.StatusCode <- 200
            response.ContentType <- contentTypeOf full
            response.ContentLength64 <- int64 bytes.Length
            response.OutputStream.Write(bytes, 0, bytes.Length)
        else
            response.StatusCode <- 404

    let handle (context: HttpListenerContext) =
        try
            try
                match context.Request.Url.AbsolutePath with
                | "/api/coedit/append" -> handleAppend (readBody context.Request) context.Response
                | "/api/coedit/diff" -> handleDiff (readBody context.Request) context.Response
                | path -> serveStatic path context.Response
            with ex ->
                printfn "browser-smoke [host] 500 on %s: %s" context.Request.Url.AbsolutePath ex.Message
                context.Response.StatusCode <- 500
                let bytes = Encoding.UTF8.GetBytes(ex.Message)
                context.Response.OutputStream.Write(bytes, 0, bytes.Length)
        finally
            try
                context.Response.OutputStream.Close()
            with _ ->
                ()

    let loop = async {
        while not cancellation.IsCancellationRequested do
            let! context = listener.GetContextAsync() |> Async.AwaitTask
            // One request per work item: the scenarios drive two browser
            // contexts that poll concurrently, and a serial loop would
            // make the fan-out latency a property of the harness.
            ThreadPool.QueueUserWorkItem(fun _ -> handle context) |> ignore
    }

    Async.Start(loop, cancellation.Token)

    {
        BaseUrl = baseUrl
        Store = store
        Updates = fun docId -> store.GetDiff(refFor docId, StateVector.empty) |> Async.RunSynchronously
        Stop =
            fun () ->
                cancellation.Cancel()

                try
                    listener.Stop()
                    (listener :> IDisposable).Dispose()
                with _ ->
                    ()
    }