# Phase 855 — a fetch transport, and same-tick calls as one request

**What changes for you: the transport, invisibly; batching only when you compose it.** Every proxy
request now goes out through `fetch` instead of `XMLHttpRequest`. It sends the same URL, method,
headers, body and cookies, and a caller sees the same results and the same failures (a network
failure is still a `ProxyRequestException` with status 0). Batching is off until you compose it on
BOTH sides. The public surface grows additively: `Http.Transport` / `Http.useTransport` /
`Http.currentTransport` and `RemoteBatching` in `ToolUp.Platform.Client`; `RemotingBatch`,
`RemotingBatchOptions` and `ServerApp.withRemotingBatching` in `ToolUp.Platform.Server`.
`Http.send` / `Http.sendAndReadBinary` keep their signatures.

## If something depended on XMLHttpRequest

A test harness or browser extension that scripted or observed `XMLHttpRequest` to see proxy calls now
sees `fetch` calls instead. Either script `fetch`, or opt the page back into XHR, once, at
composition:

```fsharp skip=fragment
Http.useTransport Http.Transport.Xhr
```

The forge's own XHR-scripted Fable cases (`JsonClientDecodeTests`, `ReadPolicyTests`) do exactly
that. The request guard wraps both transports, so identity and `X-CSRF-Token` attach either way.

The `keepalive` request flag is not set, deliberately: it does not mean HTTP keep-alive (every
`fetch` reuses connections), it lets a request outlive the page, and the browser caps the bodies of
all in-flight `keepalive` requests at 64 KiB together — it would turn a large upload into a network
failure.

## Adopting batching

1. Server: add `ServerApp.withRemotingBatching` to the composition. It serves `POST /api/_batch` at
   the pre-middleware seam and re-applies the CSRF middleware to every element. A deployment
   composing its own pipeline uses `RemotingBatch.useBatching` directly, ahead of the APIs it
   batches, with `ElementPipeline` set to whatever sits in front of its batch point that a lone call
   would have passed through.
2. Client: call `RemoteBatching.enable RemoteBatching.DefaultRoute` once at composition.

Nothing else changes: each call's promise resolves from its own element, through the same decode and
error categorisation as before. Streaming, long-running, multipart and binary methods are never
batched; a server that refuses an envelope (`400 remoting_batch_refused`) gets the calls re-sent one
request each. Semantics in full:
[`docs/platform/client-remoting-proxies.md`](../platform/client-remoting-proxies.md#the-transport-and-same-tick-batching-phase-855).

**Enable both halves together.** A client that batches against a server without the route sees every
multi-call tick fail with the envelope's 404 — loudly, by design, rather than silently halving its
own traffic.

## Verification

- `dotnet run --project Build.fsproj -- VerifyFable` — the `Phase 855` list in
  `ToolUp.AI.Client.Tests` drives the real proxy against a scripted `fetch`.
- The `Phase 855 — the remoting batch route` list in `ToolUp.Platform.Tests` runs the real dispatcher
  behind the real middleware on a TestServer: three audited calls audit three entries; three calls of
  a method budgeted at two per minute answer 200, 200, 429; a role-gated element is refused with the
  status a lone call gets while its neighbours are served; an envelope naming a streaming route runs
  nothing.

## Measured — requests, before and after

The shard asked for Phase 849's requests per boot before and after. **849's harness counts no
requests**: it counts proxies BUILT at boot (the `Remoting.buildProxy` counter), and its boot leg is
`samples/MinimalClient`, which loads no remoting module and sends no remoting request at all — 0
before and 0 after, the same finding Phase 854 recorded. The before/after is therefore taken where
the mechanism acts, on the transpiled client, counting the requests the scripted `fetch` receives:

| Scenario (Fable-tier pack, same JavaScript task) | Batching off (before) | Batching on (after) |
|---|---|---|
| Five calls | 5 requests | **1 request** (one envelope, five elements) |
| One call | 1 request | 1 request, to its own route — unchanged |
| Two identical declared reads + one other | 2 requests (854 shares the pair) | **1 request**, two elements |
| Two calls on each of two proxies with different headers | 4 requests | **2 requests**, one envelope per header set |
| Three calls, envelope refused by the server | 3 requests | 4 requests — the refusal, then one each |

**Where it applies today.** Calls started in one JavaScript task are coalesced. Elmish commands
(`Cmd.OfAsync`, `Cmd.OfRemoting`) currently start behind a `setTimeout` hop each, so the commands of
one `init` land in separate tasks and are sent separately until that hop is removed — Phase 851's
"no timer hop", after which a screen's `init` issuing five reads is one request.

## Rollback

Client: `RemoteBatching.disable ()` (or remove the `enable` call) and, if needed,
`Http.useTransport Http.Transport.Xhr`. Server: remove `ServerApp.withRemotingBatching`; nothing else
is mounted.
