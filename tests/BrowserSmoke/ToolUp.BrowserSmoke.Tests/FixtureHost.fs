// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The server half of the browser smoke harness: the built fixture
/// bundle plus a module-owned co-editing API over a real
/// `ICrdtDocumentStore`.
module ToolUp.BrowserSmoke.Tests.FixtureHost

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

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
// ## The relay (Phase 764)
//
// So is the fan-out. The store is wrapped in the shipped
// `NotifyingCrdtDocumentStore` over an `InMemoryNotificationChannel`,
// and `/api/notifications` below is a plain SSE endpoint over that
// channel — the same `event:`/`data:` framing the SDK's own handler
// uses (`SSE.namedFrame`), the same envelope JSON (`FableConverters`,
// so the `Notification` DU round-trips through `Fable.SimpleJson` on
// the page). What the harness supplies is the ENDPOINT, which is a
// deployment's job; the decorator, the channel, the framing and the
// client subscription are all shipped code, which is what makes a green
// scenario evidence about the SDK rather than about this file.
//
// It is `HttpListener` again rather than Kestrel, for the reason above,
// and each stream gets a dedicated background thread rather than a
// pooled one: an SSE connection lives as long as its page, and two
// co-editing contexts holding two pool threads for the length of a run
// is how a smoke harness acquires an intermittent hang.
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

    // Two handles on ONE log. `store` is what a deployment resolves —
    // the log wrapped in the shipped relay decorator. `silent` is the
    // same log with no relay, and it exists solely to serve the
    // `?fault=relay` go-red: an append through it is durable and
    // retrievable by diff, and announced to nobody. Cutting the fan-out
    // is therefore one branch on the write path, with every other path
    // — the client's subscription included — genuinely the shipped one.
    let silent = InMemoryCrdtDocumentStore() :> ICrdtDocumentStore

    let channel =
        NotificationChannel.InMemoryNotificationChannel(None) :> INotificationChannel

    let store = NotifyingCrdtDocumentStore(silent, channel) :> ICrdtDocumentStore

    // The client reads these frames with `Fable.SimpleJson`, so the DU
    // must be written in the shape that decoder expects — the same rule
    // (and the same converter set) as the SDK's own SSE handler.
    let jsonOptions = FableConverters.create ()

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

        // Absent means relay, so the field is a fixture affordance
        // rather than part of the shape a consumer copies.
        let relay =
            match root.TryGetProperty "relay" with
            | true, value -> value.ValueKind <> JsonValueKind.False
            | _ -> true

        let target = if relay then store else silent

        target.Append(refFor docId, payload, session)
        |> Async.RunSynchronously
        |> ignore

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

    /// One SSE stream, for the life of one page.
    ///
    /// Blocks its caller until the page goes away — which is why the
    /// accept loop hands this path a dedicated thread. The idle
    /// keepalive is not decoration: an `HttpListener` write is the only
    /// way this harness learns a browser context closed, so the comment
    /// frame doubles as the disconnect probe and bounds how long a dead
    /// stream keeps its subscription registered.
    let handleNotifications (response: HttpListenerResponse) =
        response.StatusCode <- 200
        response.ContentType <- "text/event-stream"
        response.Headers.Add("Cache-Control", "no-cache")
        response.SendChunked <- true

        use frames = new BlockingCollection<byte[]>()

        // Handed to `Subscribe`, so it runs on the publisher's thread:
        // it must do nothing but hand the frame off. Serialisation is
        // cheap and the queue is unbounded, and a throw here would be
        // swallowed by the channel anyway — better to drop one frame
        // than to leave a co-editor's publish half-done.
        let onEnvelope (envelope: NotificationEnvelope) =
            try
                let json = JsonSerializer.Serialize(envelope, jsonOptions)
                frames.Add(SSE.namedFrame (NotificationKind.ofNotification envelope.Notification) json)
            with _ ->
                ()

        let subscriptionId = channel.Subscribe(Scope, onEnvelope) |> Async.RunSynchronously

        let write (bytes: byte[]) =
            try
                response.OutputStream.Write(bytes, 0, bytes.Length)
                response.OutputStream.Flush()
                true
            with _ ->
                false

        try
            // The SDK's own comment frame, so an intermediary flushes
            // and the browser's `EventSource` opens immediately rather
            // than when the first co-editor happens to type.
            let mutable live = write SSE.readyBytes

            // …and then a hello the PAGE can see, because a comment
            // frame is invisible to `EventSource` listeners by design.
            //
            // This exists to make "my stream is open" a FACT the fixture
            // can wait on. Without it the page declares itself ready
            // while its subscription is still handshaking, and an edit
            // published in that window reaches nobody and is never
            // asked for again — a rare red that says nothing about the
            // code under test. Written straight to this one response
            // rather than published, so it announces the connection it
            // is about and no other.
            if live then
                let hello =
                    NotificationEnvelope.create
                        Scope
                        (SystemMessage(SystemMessageLevel.Info, "browser-smoke stream open"))

                live <-
                    write (
                        SSE.namedFrame
                            (NotificationKind.ofNotification hello.Notification)
                            (JsonSerializer.Serialize(hello, jsonOptions))
                    )

            while live && not cancellation.IsCancellationRequested do
                let mutable frame = Array.empty<byte>

                if frames.TryTake(&frame, 1000) then
                    live <- write frame
                else
                    live <- write (Encoding.UTF8.GetBytes ": keepalive\n\n")
        finally
            try
                channel.Unsubscribe subscriptionId |> Async.RunSynchronously
            with _ ->
                ()

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
                | "/api/notifications" -> handleNotifications context.Response
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
            // contexts that fetch concurrently, and a serial loop would
            // make the fan-out latency a property of the harness.
            //
            // Except the notification stream, which lives as long as its
            // page: a pooled thread parked for the whole run is how a
            // pool that also serves every diff and every static asset
            // starves.
            if context.Request.Url.AbsolutePath = "/api/notifications" then
                let stream = Thread(ThreadStart(fun () -> handle context))
                stream.IsBackground <- true
                stream.Start()
            else
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