namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
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

type SSEConnectionManager(?maxConnectionsPerScope: int) =
    let connections = ConcurrentDictionary<string, IConnectionSink list>()

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

    /// Awaitable per-connection write. Failed writes / cancelled
    /// tokens add the connection to `dead`; successful writes are
    /// silent. Returns a `Task` so the caller can await all writes
    /// in parallel and then prune the dead set in one pass.
    let writeOne (scopeId: string) (bytes: byte[]) (dead: ResizeArray<IConnectionSink>) (conn: IConnectionSink) =
        task {
            if conn.DisconnectToken.IsCancellationRequested then
                lock dead (fun () -> dead.Add conn)
            else
                // Cap each write at PerWriteTimeoutMs so one stalled
                // connection can't hold up the whole scope. Linked
                // with the per-connection cancellation token so a
                // client disconnect still fires immediately.
                use timeoutCts = new CancellationTokenSource(perWriteTimeoutMs)

                use linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(conn.DisconnectToken, timeoutCts.Token)

                try
                    do! conn.WriteFrame(bytes, linkedCts.Token) |> Async.StartAsTask
                with _ ->
                    // Either the underlying connection failed or the 5 s
                    // write cap fired. Both outcomes mean the connection
                    // is no longer usable for this scope.
                    lock dead (fun () -> dead.Add conn)
        }
        :> Task

    let broadcastAsyncWithKind (scopeId: string) (bytes: byte[]) (kind: string) : Task = task {
        match connections.TryGetValue scopeId with
        | true, conns ->
            // Trace: record before dispatch so the entry's
            // `ConnectionCount` reflects what we actually attempted.
            recordTrace {
                Timestamp = DateTime.UtcNow
                ScopeId = scopeId
                EventKind = kind
                PayloadBytes = bytes.Length
                ConnectionCount = conns.Length
                Dropped = false
            }

            let dead = ResizeArray<IConnectionSink>()

            let writes = conns |> List.map (writeOne scopeId bytes dead) |> List.toArray
            do! Task.WhenAll writes

            if dead.Count > 0 then
                let toRemove = dead |> Seq.toList

                connections.AddOrUpdate(
                    scopeId,
                    [],
                    fun _ existing ->
                        existing
                        |> List.filter (fun c -> not (toRemove |> List.exists (fun d -> obj.ReferenceEquals(c, d))))
                )
                |> ignore
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
    }

    let broadcastAsync (scopeId: string) (bytes: byte[]) : Task =
        broadcastAsyncWithKind scopeId bytes "data"

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
        let writeKeepalive (scopeId: string) : Task = task {
            match connections.TryGetValue scopeId with
            | true, conns ->
                let dead = ResizeArray<IConnectionSink>()

                let writes =
                    conns |> List.map (writeOne scopeId SSE.keepaliveBytes dead) |> List.toArray

                do! Task.WhenAll writes

                if dead.Count > 0 then
                    let toRemove = dead |> Seq.toList

                    connections.AddOrUpdate(
                        scopeId,
                        [],
                        fun _ existing ->
                            existing
                            |> List.filter (fun c -> not (toRemove |> List.exists (fun d -> obj.ReferenceEquals(c, d))))
                    )
                    |> ignore
            | _ -> ()
        }

        new Timer(
            (fun _ ->
                let scopeIds = connections.Keys |> Seq.toArray

                for scopeId in scopeIds do
                    writeKeepalive scopeId |> ignore),
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
            connections.AddOrUpdate(scopeId, [ conn ], fun _ existing -> conn :: existing)
            |> ignore

            Result.Ok()

    /// Remove a specific connection from a scope (matched by
    /// reference identity, not value equality — two different
    /// physical connections can share the same sink type but never
    /// the same instance).
    member _.Remove(scopeId: string, conn: IConnectionSink) =
        connections.AddOrUpdate(
            scopeId,
            [],
            fun _ existing -> existing |> List.filter (fun c -> not (obj.ReferenceEquals(c, conn)))
        )
        |> ignore

    /// Write raw SSE-framed bytes to every live connection for a
    /// scope. Callers pre-format with the SSE `data:`/`event:`
    /// framing and a trailing `\n\n` — this method does no
    /// formatting, which lets different callers use different
    /// serialisation (AI companion uses `FableJsonConverter` on
    /// `AIStreamEvent`; notification channel uses the same converter
    /// on `NotificationEnvelope`).
    ///
    /// Dead connections (cancellation fired, write threw, flush
    /// threw) are collected and removed before returning.
    member _.Broadcast(scopeId: string, bytes: byte[]) =
        // Synchronous shim — historic callers (NotificationHandler,
        // AI SSEHandler) treat broadcast as fire-and-forget. Awaiting
        // is the correct semantics inside `broadcastAsync`; we wait
        // here so eviction completes before the caller continues. The
        // method is invoked on background notification publish paths,
        // not request-thread hot paths.
        (broadcastAsync scopeId bytes).GetAwaiter().GetResult()

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
        (broadcastAsyncWithKind scopeId bytes kind).GetAwaiter().GetResult()

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

    interface IDisposable with
        member _.Dispose() = keepaliveTimer.Dispose()