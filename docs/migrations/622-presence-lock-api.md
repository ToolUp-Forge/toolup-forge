# Phase 622 — Presence + lock platform API and shell auto-mount (consumer migration)

**What changes.** `ServerConfig.Presence = EnabledPresence` now also mounts a batteries-included Remoting surface, `IPresenceApi`, at `/api/IPresenceApi/*` over the Phase 442 substrate it already registered. A new `ClientConfig.Presence` flag makes the SDK shell auto-mount `PresenceContext.provider` — heartbeat, roster, and `_platform.*` fold included — so module views get `Presence.usePeers` / `Presence.useLock` / `useEntityLock` with no deployment wiring.

**Not breaking, and nothing to do to stay put.** Both flags default to `NoPresence`. A deployment that does not set them composes and renders byte-for-byte as before (GP 11 + GP 13): no route, no DI resolution, no timer, no SSE subscription.

**Version.** Minor bump under the SemVer-on-`0.x` policy. `ClientConfig` gained a field, which retypes its constructor — consumers using the supported `{ ClientConfig.defaults with … }` / `{ ClientConfig.create handlers with … }` record-update form need no source change.

## Which presence substrate this binds, and why it matters to you

The SDK carries **two** presence families, and a consumer reading the source will hit both:

| | Phase 241 | Phase 442 |
|---|---|---|
| Server interface | `IPresenceChannel` | `IPresenceTracker` + `IEntityLockStore` |
| Roster type | `PresenceEntry` (`PrincipalId` / `Status`) | `PresencePeer` (adds `Location`) |
| Soft locks | — | `LockLease` / `LockOutcome` |
| Registered by `compose`? | **no — never has been** | yes, under `EnabledPresence` |

**`IPresenceApi` binds Phase 442, exclusively.** `IPresenceChannel` has no compose site and no DI registration anywhere in the SDK, so an API over it would have nothing to resolve; `PresencePeer` carries everything `PresenceEntry` does and adds the location descriptor; and locks exist only in 442. `IPresenceChannel` is **not removed** — it remains public surface, and its client-tier hook (`PresenceClient`) is the pump the new auto-mount runs on. What it is not is a substrate the platform will build further server surface over.

If you implemented `IPresenceChannel` directly, nothing breaks — but the platform API will not see your implementation. Implement `IPresenceTracker` instead and register it in place of the in-memory default. The full reasoning lives in `ToolUp.Platform.Core/Shared/PresenceTypes.fs` under "THE TWO-SUBSTRATE DECISION".

## The API

No method takes a `scopeId` or a `userId`. Both are resolved server-side from the authenticated request, so a client has no way to name another tenant or impersonate another principal — scope isolation is carried by the wire shape rather than by a filter (GP 4). Every method is `[<TenantScoped>]`, as the Phase 69d startup classifier requires.

| Method | Notes |
|---|---|
| `Heartbeat: PresenceLocation -> Async<PresencePeer list>` | The only announce verb. Folds Join / Move / Heartbeat: joins when absent, moves when the location changed, heartbeats otherwise. Returns the fresh roster, so a beat and a roster read are one round trip. |
| `Leave: unit -> Async<unit>` | Best-effort; a peer that never calls it still expires on its heartbeat window. |
| `Roster: unit -> Async<PresencePeer list>` | The caller's own scope only. |
| `AcquireLock` / `RenewLock` / `ReleaseLock` / `LockHolder` | Advisory soft-locks. Never block; `AcquireLock` returns `HeldByOther` naming the live holder on contention. |

**The lease TTL is server-owned** (`PresenceApi.lockTtl`, 90s) and is deliberately not a wire parameter, so a buggy or hostile client cannot strand an entity behind an unbounded lease. Hold a lock longer by renewing it.

**Why `Heartbeat` folds three operations.** `IPresenceTracker.Heartbeat` is contracted as a no-op for a peer that is no longer present. A client that only ever heartbeats would therefore vanish after one missed window — a backgrounded tab, a laptop lid — and never return. Because the location rides every beat, the handler can re-`Join` exactly where the client is, and the rest of the tenant learns about it (a `Joined` event fires; a silent revival would not tell anyone).

## Opting in

**1. Server — mount the API.**

```fsharp
{ ServerConfig.defaults with
    Presence = EnabledPresence }        // registers the substrate AND mounts /api/IPresenceApi/*
```

**2. Client — auto-mount the context.**

```fsharp
{ ClientConfig.create handlers with
    Presence = EnabledPresence }        // shell provides PresenceContext, runs the heartbeat
```

**Two flags, deliberately.** The client flag is separate so that a deployment already on `ServerConfig.Presence = EnabledPresence` with its own hand-rolled client wiring does not silently acquire a second heartbeat in every browser tab merely by upgrading the SDK.

**3. Read it from a module view.** Nothing else to wire — the hooks shipped in Phase 442 and are unchanged:

```fsharp
[<ReactComponent>]
let MyEditor (ref: EntityLockRef) (me: string) =
    let here = Presence.usePeersAt "my-module"       // who else is on this module
    let readOnly = Presence.useIsReadOnly ref me     // is someone else editing this entity
    let lockHandle = useEntityLock transport ref PresenceApi.lockRenewIntervalMs
    ...
```

## Keeping the hand-mounted path

The pre-622 contract — *the SDK registers the substrate, the deployment owns the wire and the client mounting* — **is unchanged and still supported**. If you already expose a module-owned presence API over the resolved `IPresenceTracker` / `IEntityLockStore` and mount `PresenceContext.provider` yourself:

- **Leave `ClientConfig.Presence` at `NoPresence`.** Your existing wiring keeps working exactly as before and the shell mounts nothing.
- **Or set it and keep yours too.** React context resolves to the nearest provider, so your own provider nested inside the shell's wins for the views beneath it. The platform route is an additional endpoint you need never call.
- **The client hooks stay transport-parameterised** (`PresenceTransport`, `LockTransport`) for exactly this reason — they are not being narrowed to the platform API.

If you want the platform's semantics but your own route (your own auth gate, your own scope convention), `PresenceApiHandler.forScope` is public: hand it a tracker, a lock store, a scope and a principal and it returns the same `IPresenceApi` record the mounted route uses.

## Verification

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll
```

The behaviour is covered by `ToolUp.Platform.Tests` → `Presence + lock platform API (Phase 622)` (18 cases): scope isolation for the roster, the heartbeat's returned roster, same-ref locks in two tenants and the event fan-out scope; lock contention, renew, release, non-holder release and lapse; heartbeat expiry and the re-join announcement; and the hand-mounted composition path.

To confirm your own deployment is isolated after wiring a custom `IPresenceTracker`, the sharpest check is the one the pack uses: build two `forScope` records over **one shared substrate** with different scope ids and assert neither roster sees the other. A shared store is the condition under which a leak is actually possible, so testing against two separate stores proves nothing.

## Rollback

Set `Presence = NoPresence` on both configs (or remove the fields). The route unmounts, the shell stops mounting the provider, no DI service is registered, and no data migration is involved — presence and lock state are in-memory and derived.

## See also

- `docs/migrations/69d-authorization-metadata.md` — the auth classification every method here carries.
- `ToolUp.Platform.Core/Shared/PresenceTypes.fs` — the two-substrate decision record.
- Phase 535 (CRDT co-editing) remains deliberately unphased: this is awareness-level collaboration, not merge-free co-editing.
