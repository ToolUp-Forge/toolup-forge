# Phase 69c — server-sent streaming via `IAsyncEnumerable<'T>` (consumer migration)

> **Substrate status: shipped, both ends.** The server-side streaming substrate (`Server/Remoting/Streaming.fs`) is live and contract-pinned; since Phase 69c.C every chunk frame carries `id: <correlation-id>-<chunk-index>` and the per-call telemetry event carries a `ChunkCount`. The **Fable consumer** (Phase 69c.D, `Client/Remoting/Streaming.fs`) is live: the proxy recognises an `IAsyncEnumerable<'T>` field at build time and `RemoteStream.subscribe` consumes it. The typed AI chat endpoint (`AIStreamingApi.StreamChatV2`) and the legacy `SubmitMessage` + `/api/ai/events` SSE pair are now **one turn implementation with two sinks** (Phase 69c.F) — the legacy bytes are pinned by `AIStreamFramingPinTests` and unchanged. Remaining tails: the Elmish `Cmd.OfRemoting.callStreaming` helper (69c.tail C), the KB ingestion-status opt-in (69c.tail D), the .NET-side `HttpClient` consumer (69c.E — placement escalated, see the forge TIDY-UP decision bundle), and the legacy-SSE deprecation window (69c.tail E — the actual wire break; waits on a pinned consumer migrating).

## What changes

An API record field whose F# function shape returns `IAsyncEnumerable<'T>` is classified as a **streaming method** at startup. At request time the dispatcher bypasses the proxy and serves the result as Server-Sent Events:

- each element is framed as `event: chunk\ndata: <json>\n\n` (multiline payloads emit one `data:` line per source line, per the SSE spec);
- normal end of sequence emits `event: complete\ndata: {}\n\n`;
- an exception emits `event: error\ndata: {"message":"..."}\n\n` (message only — stack traces stay server-side);
- **(69c.C)** each `chunk` frame carries `id: <correlation-id>-<chunk-index>` (zero-based) — the correlation id is the one the dispatcher stamps on the `x-correlation-id` response header (the caller's header value if it sent one, else a generated GUID), so a chunk is attributable to its call from the wire alone (`EventSource.lastEventId`, a proxy log, an SSE trace).

Zero-cost when unused (GP 13): a record with no `IAsyncEnumerable` methods is absent from the streaming classification.

## Diff to apply

```fsharp
open System.Collections.Generic

type FeedApi = {
    // 'arg -> IAsyncEnumerable<'T> — auto-framed as SSE.
    [<AllowAnonymous>]
    Tail: TailRequest -> IAsyncEnumerable<FeedEvent>
}
```

Produce the `IAsyncEnumerable<'T>` however you like (an F# `taskSeq { }` from `FSharp.Control.TaskSeq`, a C#-style iterator, or a hand-rolled `IAsyncEnumerable`). The dispatcher iterates it and frames each element.

**Constraint — pre-flight attributes are refused on streaming methods.** The SSE short-circuit runs *before* the auth / rate-limit / audit / idempotency pre-flight, so those attributes can't be enforced on a streaming method. The adapter **refuses to start** if a streaming method carries `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<RateLimit>]` / `[<Audit>]` / `[<Idempotent>]` (recognised across both the server-tier family and the `ToolUp.Platform.*` mirrors). `[<AllowAnonymous>]` / `[<PublicEndpoint>]` are the only markers honoured — gate a streaming method inside its handler, or expose it anonymously and gate the non-streaming surface around it, until per-frame pre-flight composition lands.

## Producing the stream from a callback (`AsyncStream.fromCallback`)

Most real producers are push-based (an agent loop, a progress reporter) rather than a natural pull sequence. `AsyncStream.fromCallback` (in `ToolUp.Remoting.Server`) bridges a callback/sink producer to the `IAsyncEnumerable<'T>` a streaming method returns:

```fsharp
open ToolUp.Remoting.Server

type FeedApi = {
    [<AllowAnonymous>]
    Tail: TailRequest -> IAsyncEnumerable<FeedEvent>
}

let feedApi: FeedApi = {
    Tail =
        fun req ->
            AsyncStream.fromCallback isTerminalEvent (fun emit ->
                async {
                    // emit values from any push source; the stream ends after a
                    // value satisfying `isTerminalEvent` is emitted (or on fault).
                    do! runProducer emit
                })
}
```

`runProducer` is started detached at enumeration time, so a producer whose foreground returns once a background loop is kicked off — and whose `emit` keeps firing afterwards — works correctly: the stream stays open until a terminal value is emitted, not until `runProducer` returns. **Contract:** the producer MUST emit a value satisfying `isTerminal` (or fault) to end the stream. This is exactly the shape the typed AI chat endpoint uses (the agent loop is a detached-tail producer; `TaskStatusChanged(_, AITaskCompleted | AITaskFailed _)` is the terminal). Forge's typed AI chat endpoint (`AIStreamingApi.StreamChatV2`) is the worked example.

## Telemetry (69c.C)

A streaming call emits **one** `IRemotingTelemetry` event, after its terminal frame, exactly as a request/response call does — with `MethodTelemetry.ChunkCount = Some n`, the number of `chunk` frames written before `complete` or `error` (`None` on every request/response method). The default metrics bridge (`Api.make`) records it as `toolup.remoting.chunks` under the same `method` / `outcome` tags as `toolup.remoting.elapsed_ms`; a custom sink reads the field. A mid-stream fault reports `Failed` with the count of chunks that reached the wire before it.

## Consuming the stream (Fable client — 69c.D)

The proxy recognises the field shape at build time, so the same record you declare on the server is the record you build a proxy over on the client — `IAsyncEnumerable<'T>` compiles under Fable as a type reference (it has no runtime shape, which is why the proxy returns a cold stream behind it). Calling the method sends nothing; subscribing sends the same route / headers / JSON-array body a request/response call would, reads the body incrementally, and hands you each element:

```fsharp skip=fragment
open ToolUp.Remoting.Client

let api = Remoting.createApi () |> Remoting.buildProxy<FeedApi>

let subscription =
    RemoteStream.subscribe (api.Tail { Since = cursor }) {
        OnChunk = fun (event: FeedEvent) -> dispatch (FeedEventArrived event)
        OnComplete = fun () -> dispatch FeedEnded
        OnError = fun exn -> dispatch (FeedFailed exn.Message)
    }

// later — aborts the connection; nothing further is delivered
subscription.Dispose()
```

Exactly one of `OnComplete` / `OnError` fires, after the last `OnChunk`. `OnError` carries a `ProxyRequestException` for the server's `error` frame (its `message`), a non-200 response (status + body; a Phase 783 decode-refusal envelope is decoded into `DecodeError` as on the request/response path), a chunk that did not deserialise, or a body that ended with no terminal frame — a stream never completes silently. The request goes through `window.fetch`, so the SDK's request guard attaches the live identity headers exactly as it does for XHR. Wrap the subscription in an `EffectHandle` for Elmish lifetime disposal; the `Cmd.OfRemoting.callStreaming` helper (69c.tail C) will fold that in.

Streaming methods are **unary** on the client as on the server (one argument, or `unit`); a `byte[]` argument (multipart) is refused at proxy-build time. Every non-streaming field on the same record builds exactly as before.

## Consuming the stream (.NET)

`ToolUp.Remoting.Server.SseFrame.parse` is the framing's decoder (public, beside the encoder, in `ToolUp.Platform.Server`): hand it the text of a response body — or each buffered slice ending on a blank-line delimiter — and it returns the `chunk` / `complete` / `error` frames with their ids. Over `HttpClient`, request with `HttpCompletionOption.ResponseHeadersRead`, read the body as it arrives, split at the last `\n\n` and decode the complete part (the Fable consumer's `splitComplete` is the same rule). A packaged `IAsyncEnumerable<'T>` reader for non-Fable callers (69c.E) is not shipped: forge has no .NET-side Remoting caller to sit beside — `InterPlatform`'s `HttpPeerClient` is a JSON-RPC peer transport, not a Remoting proxy — so its placement is an operator decision recorded in the forge TIDY-UP.

## Verification

1. Declare an `'arg -> IAsyncEnumerable<'T>` method; `curl -N` the route with a JSON-array body and observe `event: chunk` frames followed by `event: complete`.
2. Throw inside the sequence and observe a single `event: error` frame with the message (no stack trace).
3. Add a `[<RequiresRole>]` to the streaming method and confirm the adapter refuses to start, naming the method + the unenforceable attribute.
4. Contract pack: `InProcess/StreamingTests.fs` (`ToolUp.Platform.Tests`) — classification, the unenforceable-attribute refusal (both families), SSE frame formatting incl. multiline framing, and first-arg parsing.

## Shipped — the AI chat turn, one implementation, two sinks (69c.tail A → 69c.F)

`AIStreamingApi.StreamChatV2: AIMessageRequest -> IAsyncEnumerable<AIStreamEvent>` (server-only record in `ToolUp.AI.Server`, mounted alongside the legacy surface) serves the chat turn as typed SSE via `AsyncStream.fromCallback`. Since Phase 69c.F the turn is built ONCE over an injected event sink (`makeAssistantApi emit`): the legacy `SubmitMessage` + `/api/ai/events` pair is that record over the SSE-manager broadcast, the typed endpoint is the same record over the stream's channel — and nothing else differs. The legacy channel's bytes are pinned as a literal by `AIStreamFramingPinTests` (recorded before the change, green after it), and the typed chunk payloads are the same serialised events, so a client moving to the typed endpoint parses each chunk with the parser it already has. The legacy endpoint stays mounted.

## What's still gated

- The Fable `Cmd.OfRemoting.callStreaming` Elmish helper (69c.tail C) — `RemoteStream.subscribe` above is the consumer it wraps.
- KB ingestion-status (69c.tail D) + Phase 53 conversation-replay (69c.tail B) typed-streaming opt-ins — the KB status cache has no change-notification seam today, so its stream waits on one rather than polling the cache under a streaming shape.
- The .NET `HttpClient` consumer (69c.E) — placement escalated; the decoder is in place.
- Legacy-SSE **deprecation window** (69c.tail E) — gated: removing `/api/ai/events` is the actual wire break, so it opens only when a pinned consumer has migrated to the typed endpoint.

## Rollback

Convert the method off the `IAsyncEnumerable<'T>` shape (e.g. return `Async<'T list>`); it reverts to ordinary proxy dispatch with no other change. The substrate is inert for any record that declares no streaming methods.
