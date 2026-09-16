module ToolUp.AI.SSEHandler

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.StorageScopeResolver
open ToolUp.AI

// ─── JSON serialization ──────────────────────────────────────────
// Uses FableConverters (from ToolUp.Remoting.Json.SystemTextJson) so
// that F# DUs are serialized in the format Fable.SimpleJson expects on
// the client:
//   {"CaseName": [field1, field2]}
// NOT the bare-STJ default contract format:
//   {"Case":"CaseName","Fields":[field1, field2]}

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

let private jsonOptions = FableConverters.create ()

let private serializeEvent (event: AIStreamEvent) =
    JsonSerializer.Serialize(event, jsonOptions)

// ─── AI dispatcher ───────────────────────────────────────────────

/// Writes an `AIStreamEvent` to every SSE connection registered for a
/// scope. Thin wrapper over the shared `SSEConnectionManager` —
/// serialises with `FableConverters`, frames as a default SSE
/// `message` event, and calls `Broadcast`. The connection-tracking,
/// zombie cleanup, and scope-gating all live in the core manager.
///
/// Call sites use this instead of reaching into the manager directly
/// so that a future change to AI serialisation (different converter,
/// different framing) is localised here.
let sendEvent (manager: SSEConnectionManager) (scopeId: string) (event: AIStreamEvent) =
    // Phase 6h follow-up — Workstream B. Pass the event's discriminated
    // case name as the trace `kind` so the SseTraceContributor on
    // `/dev/inspect` can render meaningful labels per entry instead of
    // just "data". The DU case-name string is debug-only — the wire
    // payload (the JSON below) is unchanged.
    let kind =
        match event with
        | MessageDelta _ -> "MessageDelta"
        | MessageComplete _ -> "MessageComplete"
        | TaskStatusChanged _ -> "TaskStatusChanged"
        | StreamError _ -> "StreamError"
        | StreamCancelled _ -> "StreamCancelled"
        | ToolCallStarted _ -> "ToolCallStarted"
        | ToolCallCompleted _ -> "ToolCallCompleted"
        | ClientToolInvoke _ -> "ClientToolInvoke"
        | AnswerVerified _ -> "AnswerVerified"
        | AIConsentRequired _ -> "AIConsentRequired"
        | ToolApprovalRequired _ -> "ToolApprovalRequired"

    let json = serializeEvent event
    manager.BroadcastWithKind(scopeId, SSE.dataFrame json, kind)

// ─── Giraffe handler ─────────────────────────────────────────────

/// Giraffe HttpHandler for the AI SSE endpoint: GET /api/ai/events.
/// Sets SSE headers, resolves the subscriber's scope from the
/// authenticated request context, registers the connection with the
/// shared `SSEConnectionManager`, and holds it open until the client
/// disconnects.
///
/// **Scope resolution** is the shared
/// `ToolUp.Platform.SseScopeResolution.resolve` — the same code path
/// `ToolUp.Platform.NotificationHandler` calls, so the two SSE
/// endpoints cannot drift (Phase 117). Middleware-resolved identity
/// always wins and a mismatching `?userId=` is audited; the
/// `?userId=` / `X-User-Id` fallback (EventSource cannot set custom
/// headers) is honoured ONLY under `QueryParamFallback` (the
/// documented dev / Anonymous path; preflight `SseAuthModeValidator`
/// already refuses authenticated mode + fallback unless explicitly
/// opted in). Under `CookieRequired` the fallback is disabled — a
/// missing / `"anonymous"` resolved identity is refused with 401
/// rather than trusting a client-supplied `userId`, which would let
/// any client subscribe as an arbitrary user and receive their stream.
// ─── Phase 69c.tail E — the legacy channel's deprecation window ──
//
// `/api/ai/events` + `AIAssistantApi.SubmitMessage` are the PRE-69c way
// to drive a chat turn: a POST that returns nothing, and a separate,
// long-lived, per-user SSE broadcast the client had already subscribed
// to. Since Phase 69c.F the typed `AIStreamingApi.StreamChatV2` is the
// same turn over the same implementation, with the turn's events on the
// caller's OWN connection — correlated per chunk, categorised on error,
// and closed when the turn ends.
//
// The window OPENED on 2026-09-16 and is announced on the wire rather
// than only in a doc: every accepted connection to the legacy channel
// carries an IETF `Deprecation` header (RFC 9745) naming the date, and a
// `Link rel="deprecation"` to the migration guide. A header is the right
// place for this because the operator most likely to be surprised by the
// eventual removal is the one running a client nobody has looked at in a
// year — and that client's traffic is the only thing that still knows it
// exists.
//
// **There is deliberately NO `Sunset` header.** A sunset date is a
// promise, and two things have to be true before one can honestly be
// made: the SDK's deprecation policy retires public surface only at a
// major boundary (`1.0.0`, undated), and the phase's own gate is that a
// PINNED consumer has migrated to the typed endpoint. Emitting a date
// the policy might not honour would be worse than emitting none. When
// both hold, a `Sunset` lands here in the same commit as the dated
// entry in the migration guide.
//
// Nothing about the event stream itself changes: these are response
// headers, written before the body is committed, and the frames stay
// byte-for-byte what `AIStreamFramingPinTests` pins.

/// The date the legacy AI SSE channel was announced as deprecated, in
/// the IMF-fixdate form RFC 9745 requires.
[<Literal>]
let LegacyChannelDeprecatedOn = "Wed, 16 Sep 2026 00:00:00 GMT"

/// Where a consumer of the legacy channel is told to go.
[<Literal>]
let LegacyChannelMigrationGuide =
    "https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/migrations/69c-streaming-asyncseq-adoption.md"

/// Announce the deprecation on an accepted legacy-channel connection.
/// Must be called BEFORE the response body is started — once the
/// `text/event-stream` headers are committed no header can be added.
let writeDeprecationHeaders (response: HttpResponse) =
    response.Headers["Deprecation"] <- LegacyChannelDeprecatedOn

    let link =
        sprintf "<%s>; rel=\"deprecation\"; type=\"text/html\"" LegacyChannelMigrationGuide

    response.Headers["Link"] <- Microsoft.Extensions.Primitives.StringValues link

let sseHandler (manager: SSEConnectionManager) (sseAuthMode: SseAuthMode) : HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> task {
        // Phase 6l.D — resolve scope + attempt registration BEFORE
        // writeReadyResponse so a refusal (scope-at-capacity) can
        // return HTTP 429 cleanly. Once writeReadyResponse runs the
        // headers are committed to text/event-stream and an explicit
        // status code becomes inaccessible.
        match SseScopeResolution.resolve sseAuthMode ctx with
        | SseScopeResolution.SseUnauthenticated ->
            // CookieRequired + no resolved identity. Refuse rather than
            // trust a client-supplied userId.
            do! SseScopeResolution.refuseUnauthenticated ctx
            return Some ctx
        | SseScopeResolution.SseScope scopeId ->

            let sink = SseConnectionSink.fromHttpResponse ctx.Response ctx.RequestAborted

            match manager.Add(scopeId, sink) with
            | Result.Error refusal ->
                // Scope at capacity. Refuse with 429 + Retry-After so the
                // client knows to back off rather than thrashing reconnects.
                // Body is plain text — JSON would mislead clients into
                // parsing it as an SSE event.
                ctx.Response.StatusCode <- 429
                ctx.Response.Headers["Retry-After"] <- "30"
                ctx.Response.ContentType <- "text/plain; charset=utf-8"

                do!
                    ctx.Response.WriteAsync(
                        sprintf
                            "Too many concurrent SSE connections for scope %s (cap=%d, current=%d). Retry after 30s."
                            refusal.ScopeId
                            refusal.Cap
                            refusal.CurrentCount
                    )

                return Some ctx
            | Result.Ok() ->
                // Phase 69c.tail E — announce the deprecation while headers
                // are still mutable; `writeReadyResponse` commits them.
                writeDeprecationHeaders ctx.Response
                do! SSE.writeReadyResponse ctx.Response

                // Keep connection alive until client disconnects
                try
                    while not ctx.RequestAborted.IsCancellationRequested do
                        do! Task.Delay(15000, ctx.RequestAborted)
                        do! ctx.Response.Body.WriteAsync(SSE.keepaliveBytes, 0, SSE.keepaliveBytes.Length)
                        do! ctx.Response.Body.FlushAsync()
                with
                | :? TaskCanceledException -> ()
                | :? OperationCanceledException -> ()

                manager.Remove(scopeId, sink)
                return Some ctx
    }