# Phase 69c — server-sent streaming via `IAsyncEnumerable<'T>` (consumer migration)

> **Substrate status: shipped; live-path migration is consumer-trigger-gated.** The server-side streaming substrate (`Server/Remoting/Streaming.fs`) is live and contract-pinned. Migrating forge's existing AI chat SSE path to the typed shape, and the Fable `Cmd.OfRemoting.callStreaming` client helper, are **wire-divergent** and wait on a consumer committing to the typed-streaming wire (Phase 69c.tail A/B/C/D/E) — nothing forces adoption today.

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

## Consuming the stream (client)

Until the typed `Cmd.OfRemoting.callStreaming` helper ships, a Fable client consumes the endpoint as a plain SSE source — `fetch` the route with the JSON-array body, read the `ReadableStream`, and parse the `chunk` / `complete` / `error` events (the same shape the SDK's `NotificationClient` SSE consumer already handles). A `.NET` client uses `HttpClient` with `HttpCompletionOption.ResponseHeadersRead` and reads the event stream line-by-line.

## Verification

1. Declare an `'arg -> IAsyncEnumerable<'T>` method; `curl -N` the route with a JSON-array body and observe `event: chunk` frames followed by `event: complete`.
2. Throw inside the sequence and observe a single `event: error` frame with the message (no stack trace).
3. Add a `[<RequiresRole>]` to the streaming method and confirm the adapter refuses to start, naming the method + the unenforceable attribute.
4. Contract pack: `InProcess/StreamingTests.fs` (`ToolUp.Platform.Tests`) — classification, the unenforceable-attribute refusal (both families), SSE frame formatting incl. multiline framing, and first-arg parsing.

## What's still gated (Phase 69c.tail A/B/C/D/E)

- Migrating forge's `AIAssistantHandler` chat SSE path from its hand-rolled writer to a typed `IAsyncEnumerable<AIStreamEvent>` at a versioned endpoint, with the legacy SSE path kept during a deprecation window — **wire-divergent**, starts when a pinned consumer commits to migrating.
- The Fable `Cmd.OfRemoting.callStreaming` Elmish helper + its Node test harness.
- KB ingestion-status + Phase 53 conversation-replay typed-streaming opt-ins.

## Rollback

Convert the method off the `IAsyncEnumerable<'T>` shape (e.g. return `Async<'T list>`); it reverts to ordinary proxy dispatch with no other change. The substrate is inert for any record that declares no streaming methods.
