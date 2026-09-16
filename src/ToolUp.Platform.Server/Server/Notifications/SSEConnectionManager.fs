namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// Narrow contract a connection registered with `SSEConnectionManager`
/// must satisfy. Two members — exactly the operations the manager
/// performs against a live connection — over BCL-only types so a
/// non-HTTP test fake or a future runtime-neutral companion can
/// implement it without fabricating ASP.NET Core shapes.
///
/// Phase 1c retrofit: replaces a prior public `SSEConnection` record
/// (`HttpResponse * CancellationToken`) which coupled the connection
/// model to ASP.NET Core. The SSE-specific implementation is private
/// to this file under `SseConnectionSink.fromHttpResponse`.
type IConnectionSink =
    /// Token signalled when the underlying connection is closed
    /// (client disconnect, server abort, request aborted). Used by
    /// the manager for upfront aliveness checks before write attempts
    /// and as one input to the per-write linked-cancellation token.
    abstract DisconnectToken: CancellationToken
    /// Write a frame and flush. Honours the supplied cancellation
    /// token (the manager passes a linked token combining
    /// `DisconnectToken` and a per-write timeout).
    abstract WriteFrame: bytes: byte[] * cancellationToken: CancellationToken -> Async<unit>

/// Helpers for building SSE-framed byte payloads. Defined ahead of
/// `SSEConnectionManager` so the manager's keepalive timer can
/// reference `SSE.keepaliveBytes` without a forward declaration.
/// Every caller that writes to `SSEConnectionManager.Broadcast`
/// should use these so the wire format stays consistent — browser
/// `EventSource` parsers are strict about the `data:`/`event:`
/// framing and the terminating double newline.
module SSE =
    /// Frame a JSON payload as a default `message`-type SSE event.
    /// Consumer-side: `eventSource.onmessage` receives it.
    let dataFrame (json: string) : byte[] =
        Encoding.UTF8.GetBytes $"data: {json}\n\n"

    /// Frame a JSON payload with a named event type. Consumer-side:
    /// `eventSource.addEventListener(name, handler)` receives it.
    /// Used by the notification channel so the client router can
    /// dispatch by kind without parsing the payload first.
    let namedFrame (eventName: string) (json: string) : byte[] =
        Encoding.UTF8.GetBytes $"event: {eventName}\ndata: {json}\n\n"

    /// Server-side keepalive comment. SSE spec: any line starting
    /// with `:` is a comment that clients ignore. Sending one every
    /// 15–30 s keeps proxies from dropping idle connections, and —
    /// equally important — gives the server a reason to attempt a
    /// write so closed connections detect themselves rather than
    /// lingering until the next inbound publish.
    let keepaliveBytes: byte[] = Encoding.UTF8.GetBytes ": keepalive\n\n"

    /// Initial comment frame written immediately after the SSE
    /// headers, before the handler awaits anything async. SSE spec
    /// ignores lines starting with `:` at the client, so this is
    /// invisible to consumers — its job is to give intermediate
    /// proxies a newline-terminated body chunk to flush, which some
    /// reverse proxies require before unbuffering even when
    /// `X-Accel-Buffering: no` is set on the response.
    let readyBytes: byte[] = Encoding.UTF8.GetBytes ": ready\n\n"

    /// Set the SSE response headers + write the ready comment frame
    /// + flush. Both `NotificationHandler` and `SSEHandler` must call
    /// this before any await on the per-connection subscriber so the
    /// response stream opens immediately. Encapsulated so the two
    /// handlers share identical behaviour and a regression test can
    /// exercise the setup against a `DefaultHttpContext` without
    /// spinning Giraffe or `TestServer`.
    ///
    /// `X-Accel-Buffering: no` is honoured by nginx and Cloudflare's
    /// analogous mechanism; proxies that don't recognise it ignore
    /// silently. No HTTP semantic change.
    let writeReadyResponse (response: HttpResponse) : Task = task {
        response.Headers.ContentType <- "text/event-stream"
        response.Headers.CacheControl <- "no-cache"
        response.Headers.Connection <- "keep-alive"
        response.Headers["X-Accel-Buffering"] <- "no"
        do! response.Body.WriteAsync(readyBytes, 0, readyBytes.Length)
        do! response.Body.FlushAsync()
    }

/// SSE-specific implementation of `IConnectionSink`. Holds the live
/// `HttpResponse` privately — `HttpResponse` is referenced nowhere
/// else outside this file, keeping the runtime coupling localised.
/// Handlers (`NotificationHandler`, `SSEHandler`) call
/// `fromHttpResponse` to build a sink before passing it to
/// `SSEConnectionManager.Add`; nothing outside the SDK constructs
/// one directly.
module SseConnectionSink =
    type private SseSink(response: HttpResponse, disconnectToken: CancellationToken) =
        interface IConnectionSink with
            member _.DisconnectToken = disconnectToken

            member _.WriteFrame(bytes, ct) = async {
                do! response.Body.WriteAsync(bytes, 0, bytes.Length, ct) |> Async.AwaitTask
                do! response.Body.FlushAsync(ct) |> Async.AwaitTask
            }

    /// Build a sink from the request's `HttpResponse` and the
    /// request-abort token. The handler keeps `ctx.Response` for
    /// direct writes on its own per-connection feed; the manager
    /// only sees the narrow `IConnectionSink`.
    let fromHttpResponse (response: HttpResponse) (disconnectToken: CancellationToken) : IConnectionSink =
        SseSink(response, disconnectToken) :> IConnectionSink

/// Scope-keyed registry of live SSE connections, plus a zombie-aware
/// `Broadcast` that sends raw bytes to every connection for a scope.
///
/// This is the transport primitive shared by the core SDK's
/// `NotificationHandler` (notifications at `/api/notifications`) and
/// the AI companion's `SSEHandler` (AI stream events at
/// `/api/ai/events`). Both register connections keyed by the same
/// scope id and rely on the same connection cleanup semantics; giving
/// them a shared manager ensures a single source of truth for idle
/// timeout, keepalive, and write-failure cleanup.
///
/// ## Scope isolation
///
/// `Broadcast(scopeId, bytes)` only writes to connections whose
/// registered scope exactly equals `scopeId`. Cross-scope writes are
/// structurally impossible — a publisher that passes the wrong scope
/// id simply hits no subscribers, never leaks to the wrong team.
/// Callers must still resolve `scopeId` from authenticated context
/// before calling; the manager does not validate the scope string.
///
/// ## Zombie cleanup
///
/// Connections whose write fails (client disconnected, response
/// body closed, proxy hung up) or whose cancellation token has fired
/// are removed eagerly during the next `Broadcast` — not lazily on
/// next send. Without this, a client that closed mid-stream would
/// leave an unbounded dead entry consuming a per-send write attempt
/// forever. A 30-second keepalive write keeps the eviction path
/// active even when no inbound publish is happening.
///
/// ## Thread safety
///
/// Backed by `ConcurrentDictionary` with atomic `AddOrUpdate` on
/// mutation. Enumeration during `Broadcast` is lock-free; a
/// subscriber added mid-broadcast may or may not see the current
/// write, which is expected "best effort" behaviour for SSE.
/// Phase 6h follow-up — Workstream B. Bounded ring-buffer entry for the
/// last N broadcasts on this `SSEConnectionManager`. Surfaced via
/// `Snapshot()` and rendered by `SseTraceContributor` on `/dev/inspect`,
/// so a developer can see at a glance which scopeIds events landed on
/// and whether the broadcast hit any connections.
type SseBroadcastTraceEntry = {
    Timestamp: DateTime
    ScopeId: string
    /// Best-effort "what kind of event was this" tag. Populated when
    /// the caller has more information than `Broadcast(scopeId, bytes)`
    /// alone provides; defaults to `"data"` for the bytes-only entry
    /// point. AI's `sendEvent` and the notification channel both pass
    /// the rich kind via `BroadcastWithKind` (added below).
    EventKind: string
    /// Length of the payload in bytes. Useful for spotting unusually
    /// large events without dumping content into the trace.
    PayloadBytes: int
    /// Number of connections the broadcast targeted at the moment of
    /// dispatch. `0` = no subscribers; the event was dropped.
    ConnectionCount: int
    /// Convenience flag derived from `ConnectionCount = 0`.
    Dropped: bool
}

/// Snapshot returned by `SSEConnectionManager.Snapshot()`. `Broadcasts`
/// is most-recent-first (newest at head) so renderers can take a head
/// slice for "last N events" without re-sorting. `RegisteredScopes`
/// pairs each scopeId with its current connection count.
///
/// Phase 6l.D — `RefusalCounts` carries running totals of
/// `Add`-time scope-at-capacity refusals per scope. Operators reading
/// `/dev/sse-trace` see which scopes are hitting the
/// `MaxSseConnectionsPerScope` cap.
type SseManagerSnapshot = {
    Broadcasts: SseBroadcastTraceEntry list
    RegisteredScopes: Map<string, int>
    RefusalCounts: Map<string, int>
}

/// Phase 6l.D — outcome of `SSEConnectionManager.Add` when the new
/// connection would push the scope past `MaxSseConnectionsPerScope`.
/// SSE handlers translate this to HTTP 429 + `Retry-After: 30`.
type ScopeAtCapacity = {
    ScopeId: string
    Cap: int
    CurrentCount: int
}

/// Phase 6k — one connection's outbound queue and the single loop that
/// drains it.
///
/// Before this, `Broadcast` fanned out with `Task.WhenAll` over every
/// connection in the scope and awaited them all. That coupled every
/// subscriber's latency to every other's: a wedged tab (renderer killed,
/// laptop suspended, proxy holding the socket open without reading) made
/// the whole scope wait out the 5 s per-write cap on EVERY event. Two
/// tabs, one wedged, and the healthy tab's stream ran 5 s behind per
/// frame — which the client's 60 s watchdog eventually reported as a
/// silent hang on the connection that was working fine.
///
/// Now each connection owns a bounded `Channel<byte[]>` and one reader
/// loop. `Broadcast` is `TryWrite` per connection: non-blocking, and the
/// slow subscriber's backpressure is its own. A connection whose queue
/// is full is not merely slow — it has failed to drain `queueCapacity`
/// frames within the writes the others completed — so it is evicted as
/// already-dead, which is the same outcome the 5 s cap produced before,
/// reached without making anyone else wait for it.
///
/// Per-connection ORDER is preserved (one reader, FIFO channel), which
/// is what SSE delta streams require. Cross-connection order never was
/// guaranteed and still is not.
type private ConnectionWriter(sink: IConnectionSink, queueCapacity: int, perWriteTimeoutMs: int, onDead: unit -> unit) =

    let channel =
        Channel.CreateBounded<byte[]>(
            BoundedChannelOptions(
                queueCapacity,
                // `TryWrite` returns false rather than dropping or
                // blocking when the queue is full — the manager needs
                // that false to decide the connection is dead.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            )
        )

    /// Frames accepted but not yet handed to the sink. Drives
    /// `SSEConnectionManager.WaitForDelivery`, which is how a test (or a
    /// shutdown path) gets a deterministic "everything queued has been
    /// written" point out of an otherwise fire-and-forget send.
    let pending = ref 0

    let stopped = ref 0
    let cts = new CancellationTokenSource()

    /// Idempotent. Called by the manager on `Remove`/`Dispose` and by
    /// the loop itself when a write fails — whichever happens first.
    let stop () =
        if Interlocked.Exchange(&stopped.contents, 1) = 0 then
            channel.Writer.TryComplete() |> ignore

            try
                cts.Cancel()
            with _ ->
                ()

    /// One frame, capped at `perWriteTimeoutMs` and linked to the
    /// connection's own disconnect token. Returns false when the write
    /// failed or timed out — identical semantics to the pre-6k
    /// `writeOne`, minus the shared `dead` list.
    let writeOnce (frame: byte[]) : Task<bool> = task {
        use timeoutCts = new CancellationTokenSource(perWriteTimeoutMs)

        use linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(sink.DisconnectToken, timeoutCts.Token, cts.Token)

        try
            do! sink.WriteFrame(frame, linkedCts.Token) |> Async.StartAsTask
            return true
        with _ ->
            return false
    }

    /// The dedicated reader loop. Exits on: the channel completing
    /// (`Remove`/`Dispose`), the writer's token cancelling, or a failed
    /// write. Every exit runs `onDead`, so eviction has exactly one
    /// path regardless of which end noticed first.
    let loop = task {
        let mutable running = true

        try
            while running do
                let! frame = channel.Reader.ReadAsync(cts.Token).AsTask()
                let! ok = writeOnce frame
                Interlocked.Decrement(&pending.contents) |> ignore

                if not ok then
                    running <- false
        with _ ->
            // `ChannelClosedException` on a completed-and-drained
            // queue, or `OperationCanceledException` on stop. Both
            // are ordinary shutdown, not failures to report.
            ()

        stop ()
        onDead ()
    }

    member _.Sink = sink

    /// Frames queued but not yet written. `> 0` means this connection
    /// is behind, not that it is dead.
    member _.Pending = Volatile.Read(&pending.contents)

    /// Queue a frame. False means the connection's queue is full — the
    /// manager treats that as death, for the reason in the type's
    /// summary.
    member _.TryEnqueue(frame: byte[]) : bool =
        if Volatile.Read(&stopped.contents) <> 0 then
            false
        else
            Interlocked.Increment(&pending.contents) |> ignore

            if channel.Writer.TryWrite frame then
                true
            else
                Interlocked.Decrement(&pending.contents) |> ignore
                false

    member _.Stop() = stop ()

    /// Awaitable completion of the reader loop. Used by `Dispose` so a
    /// disposed manager leaves no loop writing to a response body the
    /// host is tearing down.
    member _.Completion: Task = loop

type SSEConnectionManager(?maxConnectionsPerScope: int) =
    let connections = ConcurrentDictionary<string, ConnectionWriter list>()

    /// Phase 6l.D — per-scope cap; `None` = unbounded (legacy
    /// behaviour). When set, `Add` returns `Result.Error
    /// ScopeAtCapacity` once the scope's connection count reaches
    /// the cap.
    let cap: int option = maxConnectionsPerScope

    /// Phase 6h follow-up: per-connection write/flush timeout.
    /// Without this cap, a stalled subscriber (slow client, wedged
    /// proxy) blocks `Task.WhenAll` in `broadcastAsync` and freezes
    /// every other subscriber in the same scope. On timeout, the
    /// connection's linked cancellation fires and the existing
    /// dead-connection eviction path removes it. Phase 6k will
    /// replace the WhenAll fan-out with a per-connection writer
    /// queue; this is the bounded-by-default fix in the meantime.
    let perWriteTimeoutMs = 5_000

    /// Phase 6k — how many frames one connection may fall behind
    /// before it is treated as dead. Deliberately an internal constant
    /// rather than a config knob: it is not a tuning dial, it is the
    /// point past which "slow" is indistinguishable from "gone", and a
    /// deployment that wants a different answer wants a different
    /// notification channel. 256 frames is several seconds of the
    /// densest stream the SDK produces (AI message deltas) and a few
    /// KB per idle connection.
    let queueCapacity = 256

    /// Phase 6h follow-up — Workstream B. Bounded ring-buffer of recent
    /// broadcasts. Capacity 100 (we keep the most-recent 100 entries
    /// across all scopes — small enough to fit comfortably in memory,
    /// large enough to span a typical reproduction window). Pushes are
    /// O(1) on the front; reads (via `Snapshot`) are O(N) and lock-free
    /// w.r.t. concurrent broadcasts (the lock here only protects the
    /// `LinkedList` mutation).
    let traceCapacity = 100
    let traceBuffer = Collections.Generic.LinkedList<SseBroadcastTraceEntry>()
    let traceLock = obj ()

    let recordTrace (entry: SseBroadcastTraceEntry) =
        lock traceLock (fun () ->
            traceBuffer.AddFirst(entry) |> ignore

            while traceBuffer.Count > traceCapacity do
                traceBuffer.RemoveLast())

    /// Phase 6l.D — running count of scope-at-capacity refusals,
    /// keyed by scopeId. Surfaced via `Snapshot()` so
    /// `SseTraceContributor` can render an operator-visible refusal
    /// summary on `/dev/sse-trace`.
    let refusalCounts = ConcurrentDictionary<string, int>()

    /// Drop a connection from its scope, matched by sink reference
    /// (two physical connections never share an instance). Idempotent:
    /// the filter is a no-op once the writer is gone, so the eviction
    /// the reader loop performs on failure and the one the enqueue path
    /// performs on a full queue can both run without double-counting.
    let evict (scopeId: string) (sink: IConnectionSink) =
        connections.AddOrUpdate(
            scopeId,
            [],
            fun _ (existing: ConnectionWriter list) ->
                existing |> List.filter (fun w -> not (obj.ReferenceEquals(w.Sink, sink)))
        )
        |> ignore

    /// Hand one frame to every connection in the scope. Non-blocking by
    /// construction — this is the whole point of the phase. A connection
    /// whose token has already fired, or whose queue is full, is stopped
    /// and evicted here; every other connection has its frame queued and
    /// is written to by its own loop, at its own pace.
    let enqueueAll (scopeId: string) (writers: ConnectionWriter list) (bytes: byte[]) =
        for w in writers do
            let accepted =
                not w.Sink.DisconnectToken.IsCancellationRequested && w.TryEnqueue bytes

            if not accepted then
                w.Stop()
                evict scopeId w.Sink

    let broadcastWithKindInternal (scopeId: string) (bytes: byte[]) (kind: string) =
        match connections.TryGetValue scopeId with
        | true, writers ->
            // Trace: record before dispatch so the entry's
            // `ConnectionCount` reflects what we actually attempted.
            recordTrace {
                Timestamp = DateTime.UtcNow
                ScopeId = scopeId
                EventKind = kind
                PayloadBytes = bytes.Length
                ConnectionCount = writers.Length
                Dropped = false
            }

            enqueueAll scopeId writers bytes
        | _ ->
            // Trace: scope had no subscribers. This is the smoking gun
            // for the bug we chased — a `Dropped = true` entry tells
            // the operator instantly that an event was published to a
            // scope nobody is listening on.
            recordTrace {
                Timestamp = DateTime.UtcNow
                ScopeId = scopeId
                EventKind = kind
                PayloadBytes = bytes.Length
                ConnectionCount = 0
                Dropped = true
            }

    /// Periodic keepalive. Every 30 s, send `: keepalive\n\n` to
    /// every connection across every scope. Two purposes:
    ///   1. Stops TLS / proxy idle timeouts from dropping the
    ///      connection (typical 60 s default).
    ///   2. Gives the server a write attempt so closed clients
    ///      surface as failed writes and get evicted. Without
    ///      this, a closed tab lingers in `connections` until the
    ///      next inbound publish — possibly hours.
    let keepaliveTimer =
        // Phase 6h follow-up — keepalive bypasses the trace ring buffer.
        // It fires every 30 s for every registered scope, which would
        // otherwise dominate the last-100 trace and obscure the actual
        // application events. Direct manager-internal write path: same
        // dead-connection eviction, no trace entry.
        let writeKeepalive (scopeId: string) =
            match connections.TryGetValue scopeId with
            | true, writers -> enqueueAll scopeId writers SSE.keepaliveBytes
            | _ -> ()

        new Timer(
            (fun _ ->
                let scopeIds = connections.Keys |> Seq.toArray

                for scopeId in scopeIds do
                    writeKeepalive scopeId),
            null,
            TimeSpan.FromSeconds 30.0,
            TimeSpan.FromSeconds 30.0
        )

    /// Register a live connection for a scope. Multiple connections
    /// per scope are supported (a user with two browser tabs open
    /// sees notifications on both).
    ///
    /// Phase 6l.D — when `MaxSseConnectionsPerScope` is set, returns
    /// `Result.Error ScopeAtCapacity` if the current scope count is
    /// already at or above the cap. SSE handlers translate the error
    /// to HTTP 429. Returns `Result.Ok ()` on successful registration.
    member _.Add(scopeId: string, conn: IConnectionSink) : Result<unit, ScopeAtCapacity> =
        let currentCount =
            match connections.TryGetValue scopeId with
            | true, conns -> conns.Length
            | _ -> 0

        match cap with
        | Some maxConns when currentCount >= maxConns ->
            // Increment the refusal counter for `/dev/sse-trace`
            // visibility. Concurrency-safe via ConcurrentDictionary.
            refusalCounts.AddOrUpdate(scopeId, 1, fun _ existing -> existing + 1) |> ignore

            Result.Error {
                ScopeId = scopeId
                Cap = maxConns
                CurrentCount = currentCount
            }
        | _ ->
            // Phase 6k — the connection gets its own queue and its own
            // reader loop at registration. `onDead` closes over this
            // scope and sink so the loop can evict itself the moment a
            // write fails, without the manager having to notice on the
            // next broadcast.
            let writer =
                ConnectionWriter(conn, queueCapacity, perWriteTimeoutMs, (fun () -> evict scopeId conn))

            connections.AddOrUpdate(scopeId, [ writer ], fun _ existing -> writer :: existing)
            |> ignore

            Result.Ok()

    /// Remove a specific connection from a scope (matched by
    /// reference identity, not value equality — two different
    /// physical connections can share the same sink type but never
    /// the same instance).
    member _.Remove(scopeId: string, conn: IConnectionSink) =
        // Stop the connection's writer loop before dropping it, so no
        // frame is written to a response body the handler has finished
        // with. `Stop` is idempotent and `evict` is a no-op when the
        // loop already removed itself.
        match connections.TryGetValue scopeId with
        | true, writers ->
            writers
            |> List.iter (fun w ->
                if obj.ReferenceEquals(w.Sink, conn) then
                    w.Stop())
        | _ -> ()

        evict scopeId conn

    /// Write raw SSE-framed bytes to every live connection for a
    /// scope. Callers pre-format with the SSE `data:`/`event:`
    /// framing and a trailing `\n\n` — this method does no
    /// formatting, which lets different callers use different
    /// serialisation (AI companion uses `FableConverters` on
    /// `AIStreamEvent`; notification channel uses the same converter
    /// on `NotificationEnvelope`).
    ///
    /// Dead connections (cancellation fired, write threw, flush
    /// threw) are collected and removed before returning.
    member _.Broadcast(scopeId: string, bytes: byte[]) =
        // Phase 6k — this used to block the caller on `Task.WhenAll`
        // over every connection in the scope. It now queues one frame
        // per connection and returns; each connection's own loop does
        // the writing. The call is still ordered per connection, and a
        // caller that needs the frames to have LANDED (a test, a
        // shutdown drain) asks for that explicitly via
        // `WaitForDelivery`.
        broadcastWithKindInternal scopeId bytes "data"

    /// Phase 6h follow-up — Workstream B. Variant of `Broadcast` that
    /// takes a richer `kind` tag for the trace ring-buffer. Functional
    /// behaviour is identical to `Broadcast` — the tag flows only into
    /// `Snapshot()` for `/dev/sse-trace` rendering, never on the wire.
    /// Callers that have a more specific event-kind label than the
    /// default `"data"` (e.g. AI's `MessageDelta`/`MessageComplete`,
    /// notifications' `SystemMessage`/`JobProgress`) call this so the
    /// dev panel can colour-tag entries and operators can scan the
    /// ring at a glance.
    member _.BroadcastWithKind(scopeId: string, bytes: byte[], kind: string) =
        broadcastWithKindInternal scopeId bytes kind

    /// Phase 6h follow-up — Workstream B. Snapshot the recent-broadcast
    /// trace ring + the currently-registered scope set. Read by
    /// `SseTraceContributor` for `/dev/inspect`. Lock-free w.r.t.
    /// concurrent broadcasts (the trace push lock is held only for
    /// LinkedList mutation; `connections` is `ConcurrentDictionary`).
    member _.Snapshot() =
        let broadcasts =
            lock traceLock (fun () -> traceBuffer |> Seq.toList // most-recent-first by construction
            )

        let registeredScopes =
            connections |> Seq.map (fun kv -> kv.Key, kv.Value.Length) |> Map.ofSeq

        let refusalCountsSnapshot =
            refusalCounts |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

        {
            Broadcasts = broadcasts
            RegisteredScopes = registeredScopes
            RefusalCounts = refusalCountsSnapshot
        }

    /// Phase 6k — block until every frame queued so far has been handed
    /// to its connection, or `timeoutMs` (default 5 s) elapses. Returns
    /// true when the queues drained.
    ///
    /// Delivery is asynchronous now, so "I broadcast, therefore the
    /// subscriber has the bytes" stopped being true. Rather than leave
    /// every caller to invent its own sleep, the manager exposes the one
    /// sync point it can compute exactly: a per-connection count of
    /// frames accepted but not yet written. Used by the SSE framing pin
    /// tests (which assert bytes, not bookkeeping) and available to a
    /// host draining before shutdown. NOT a substitute for the write
    /// timeout — a connection that never drains still evicts itself.
    member _.WaitForDelivery(?timeoutMs: int) : bool =
        let budget = defaultArg timeoutMs 5_000
        let deadline = DateTime.UtcNow.AddMilliseconds(float budget)
        let mutable drained = false

        while not drained && DateTime.UtcNow < deadline do
            let outstanding =
                connections |> Seq.sumBy (fun kv -> kv.Value |> List.sumBy _.Pending)

            if outstanding = 0 then drained <- true else Thread.Sleep 1

        drained

    interface IDisposable with
        member _.Dispose() =
            keepaliveTimer.Dispose()

            // Phase 6k — stop every reader loop before the manager goes
            // away. Without this a disposed manager leaves loops parked
            // on `ReadAsync` holding a response body the host is tearing
            // down.
            for kv in connections do
                for w in kv.Value do
                    w.Stop()

            connections.Clear()