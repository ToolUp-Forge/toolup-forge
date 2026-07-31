# Scope-isolated server-driven live sessions — `ILiveSessionHost`

A **server-driven UI** holds the authoritative UI tree on the server, computes diffs there, and pushes patch frames to a thin generic client shim — no per-app Fable compile, no client-side state to drift. The protocol (diff format, patch semantics) belongs to the UI layer; what forge supplies is the **hosting substrate** such a protocol needs to live inside a `ServerApp` composition:

- **structural tenant/scope isolation (GP 4)** — sessions are partitioned by `StorageScope.ScopeId`; every operation resolves only inside the caller's partition, so a session id leaked across tenants resolves to nothing, never to frames;
- **per-scope rate limiting** via the Phase 56 `IRateLimitStore`;
- **security-headers coverage** via the global Phase 9j middleware (nothing extra to wire);
- the SDK's SSE plumbing (named events, ready handshake, keepalives).

## Compose

```fsharp
ServerApp.create config modules
|> LiveSessionsCompose.withLiveSessions (fun o ->
    { o with
        SubscribesPerMinutePerScope = Some 30
        MaxSessionsPerScope = Some 8 })
|> ServerApp.run
```

Not composed → no endpoint, no DI registration, no allocation (GP 11 / GP 13). Composed, it registers the `ILiveSessionHost` singleton and mounts `GET {Route}/{sessionId}` (default `/api/live-sessions/…`) as an SSE subscribe endpoint; frames arrive as `live-frame` events.

## Driving a session (server-side protocol code)

```fsharp skip=fragment
let host = ctx.RequestServices.GetService(typeof<ILiveSessionHost>) :?> ILiveSessionHost

// open under the caller's resolved scope (the structural partition key)
match! host.OpenSession callerScope with
| Error refusal -> // scope at its session cap → surface a 429
| Ok session ->
    // hand session.SessionId to the client; it opens
    //   new EventSource($"/api/live-sessions/{sessionId}")
    // then push patches as your protocol computes them:
    match! host.TryGetChannel(callerScope.ScopeId, session.SessionId) with
    | Some channel -> do! channel.PushFrame patchJson
    | None -> ()   // session closed
```

Frames are **opaque strings** — the host doesn't interpret them. A WebSocket transport, or a distributed multi-node host, is an additive companion behind the same interfaces; validate either against the `ILiveSessionHostContract` pack (identity-by-value addressing, structural cross-scope denial, in-order delivery within one session, idempotent unsubscribe/close).

## Guarantees and non-guarantees

- **Isolation is structural.** `TryGetChannel` / `Subscribe` under scope B with scope A's session id return `None` — indistinguishable from "no such session". There is no filter to forget.
- **Ordering holds within one session only.** Frames pushed to one session's channel arrive in push order; nothing is promised across sessions (six-rule #5).
- **Sessions are connection-lived.** The in-process default keeps no snapshot across reconnects — a reconnect re-bootstraps from the server's source of truth. (An `ILiveSessionStore` for resumable sessions would slot in additively; the omission is recorded in the `LiveSessionHost.fs` header.)
- **Dispatch gating composes with the [action authorizer](action-authorizer.md)** — a protocol that lets the client dispatch actions back should authorize each one default-deny before executing it.

## See also

- [`docs/platform/sse-deployment.md`](sse-deployment.md) — proxy/buffering concerns for SSE endpoints generally.
- [`docs/platform/client-host-bridge.md`](client-host-bridge.md) — the client-compiled sibling (tree rendered in the browser, actions routed through `ClientHostCapabilities`).
- [`docs/migrations/112-live-session-host.md`](../migrations/112-live-session-host.md).
