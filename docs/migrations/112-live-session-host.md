# Migration — Phase 112: scope-isolated server-driven live-session host

**Status:** additive, opt-in. A pipeline that never calls `LiveSessionsCompose.withLiveSessions` mounts no endpoint, registers no service, and allocates nothing (GP 11 / GP 13).

## What changes

A forge substrate for hosting **server-driven UI sessions** — the server holds the authoritative UI tree, computes diffs, and pushes opaque patch frames to a thin generic client shim over SSE (no per-app Fable compile) — with tenant/scope isolation carried **structurally** (GP 4), per-scope rate limiting via the Phase 56 `IRateLimitStore`, and Phase 9j security-headers coverage. Transport- and protocol-neutral: any server-driven UI protocol binds to it.

New public surface (all in `ToolUp.Platform.Server`):

| Symbol | Where | Purpose |
|---|---|---|
| `ILiveChannel` / `ILiveSessionHost` | `Server/Notifications/LiveSessionHost.fs` | Open / resolve / subscribe / close sessions, partitioned by `StorageScope.ScopeId`; push opaque string frames |
| `LiveSessionDescriptor` / `LiveSessionRefusal` | same file | Value descriptor; cap refusal as data |
| `InMemoryLiveSessionHost` / `LiveSessionHost.createInMemory` | same file | Single-instance default (distributed impls validate against the contract pack) |
| `LiveSessionOptions` / `LiveSessionHandler` | `Server/Compose/LiveSessionsCompose.fs` | Options + the SSE subscribe endpoint (`GET {Route}/{sessionId}`) |
| `LiveSessionsCompose.withLiveSessions` | same file | The compose hook (appends via `ComposeExtensions` — handler + `ILiveSessionHost` DI singleton) |
| `ILiveSessionHostContract` | `ToolUp.Platform.Tests` | Six-rule conformance pack |

**`ILiveSessionStore` deliberately omitted** — the motivating diff hosts are connection-lived (a reconnect re-bootstraps from the server's source of truth). Resumable-session persistence slots in additively if a protocol ever needs it; the decision is recorded in the `LiveSessionHost.fs` header.

## Adopting it

```fsharp
ServerApp.create config modules
|> LiveSessionsCompose.withLiveSessions (fun o ->
    { o with
        SubscribesPerMinutePerScope = Some 30      // via the registered IRateLimitStore
        MaxSessionsPerScope = Some 8 })
|> ServerApp.run
```

Server-side protocol code resolves `ILiveSessionHost` from DI, opens a session for the caller's `StorageScope`, hands the client the `SessionId`, and pushes frames through `TryGetChannel`. The client shim opens `EventSource("{Route}/{sessionId}")` and applies each `live-frame` event. A session opened in scope A **cannot** deliver to (or be resolved by) scope B — the partition is structural, not a filter. Dispatched actions inside a session compose with the [Phase 113](113-action-authorizer.md) authorizer.

## Breaking change

None. New files only; the existing SSE notification path is untouched.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `LiveSessionHost (Phase 112)` suite covers the contract pack (cross-scope structural denial, single-session ordering, idempotent unsubscribe/close), the per-scope cap refusal, and endpoint integration (path-gate fall-through, cross-scope 404, rate-limit 429 + `Retry-After`, end-to-end SSE frame delivery).

## Rollback

Remove the `withLiveSessions` call — no endpoint, no service, nothing else changes.
