# Phase 69c — server-sent streaming via `IAsyncEnumerable<'T>` (consumer migration)

> **Substrate status: shipped. Typed AI chat endpoint (Task A) + producer bridge shipped 2026-06-15.** The server-side streaming substrate (`Server/Remoting/Streaming.fs`) is live and contract-pinned. The typed AI chat endpoint (`AIStreamingApi.StreamChatV2`) ships **additively alongside** the legacy `SubmitMessage` + `/api/ai/events` SSE pair (which stays mounted unchanged), plus the reusable `AsyncStream.fromCallback` producer bridge. Remaining tails ride forward: the Fable `Cmd.OfRemoting.callStreaming` client helper (Task C), Phase 53 replay (B) + KB ingestion (D) typed-streaming opt-ins, and the legacy-SSE **deprecation window** (E — gated: removing the legacy endpoint is the actual wire break, so it waits on a pinned consumer migrating).

## What changes

An API record field whose F# function shape returns `IAsyncEnumerable<'T>` is classified as a **streaming method** at startup. At request time the dispatcher bypasses the proxy and serves the result as Server-Sent Events:

- each element is framed as `event: chunk\ndata: <json>\n\n` (multiline payloads emit one `data:` line per source line, per the SSE spec);
- normal end of sequence emits `event: complete\ndata: {}\n\n`;
- an exception emits `event: error\ndata: {"message":"..."}\n\n` (message only — stack traces stay server-side).

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

## Consuming the stream (client)

Until the typed `Cmd.OfRemoting.callStreaming` helper ships (Task C), a Fable client consumes the endpoint as a plain SSE source — `fetch` the route with the JSON-array body, read the `ReadableStream`, and parse the `chunk` / `complete` / `error` events (the same shape the SDK's `NotificationClient` SSE consumer already handles). A `.NET` client uses `HttpClient` with `HttpCompletionOption.ResponseHeadersRead` and reads the event stream line-by-line.

## Verification

1. Declare an `'arg -> IAsyncEnumerable<'T>` method; `curl -N` the route with a JSON-array body and observe `event: chunk` frames followed by `event: complete`.
2. Throw inside the sequence and observe a single `event: error` frame with the message (no stack trace).
3. Add a `[<RequiresRole>]` to the streaming method and confirm the adapter refuses to start, naming the method + the unenforceable attribute.
4. Contract pack: `InProcess/StreamingTests.fs` (`ToolUp.Platform.Tests`) — classification, the unenforceable-attribute refusal (both families), SSE frame formatting incl. multiline framing, and first-arg parsing.

## Shipped (Phase 69c.tail A) — typed AI chat endpoint

`AIStreamingApi.StreamChatV2: AIMessageRequest -> IAsyncEnumerable<AIStreamEvent>` (server-only record in `ToolUp.AI.Server`, mounted alongside the legacy surface) serves the chat turn as typed SSE via `AsyncStream.fromCallback`. It reuses the legacy `SubmitMessage` turn machinery through a per-request event-sink indirection: when the typed endpoint drives the turn the events route to its channel; when `SubmitMessage` drives it (the legacy `/api/ai/events` path) the events broadcast to the SSE manager **exactly as before — byte-for-byte unchanged**. The legacy endpoint is untouched and stays mounted.

## What's still gated (Phase 69c.tail B/C/D/E)

- The Fable `Cmd.OfRemoting.callStreaming` Elmish helper + its Node test harness (Task C) — client-side; the interim "consume the SSE source directly" path above works today.
- KB ingestion-status (D) + Phase 53 conversation-replay (B) typed-streaming opt-ins — separate subsystems; each adopts `AsyncStream.fromCallback` when wanted.
- Legacy-SSE **deprecation window** (E) — gated: removing `/api/ai/events` is the actual wire break, so it opens only when a pinned consumer has migrated to the typed endpoint.

## Rollback

Convert the method off the `IAsyncEnumerable<'T>` shape (e.g. return `Async<'T list>`); it reverts to ordinary proxy dispatch with no other change. The substrate is inert for any record that declares no streaming methods.
