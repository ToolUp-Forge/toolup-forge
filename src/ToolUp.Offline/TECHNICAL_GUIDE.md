# ToolUp.Offline — technical guide

Implementation reference for the offline-first companion (Phase 24). Read
[`README.md`](README.md) first for what it does; this file is how and why.

---

## 1. Service-worker lifecycle

### Registration

`ServiceWorkerRegistration.boot` takes the `OfflineMode`, not the `OfflineConfig`, and returns
immediately on `NoOffline`. That is the whole zero-cost guarantee — there is no code path under the
default that touches `navigator.serviceWorker`, `caches`, `indexedDB` or the document head.

Under `EnabledOffline config` it does two things, in this order:

1. **Links the PWA manifest** — from `config.ManifestUrl` when the deployment ships its own file,
   otherwise from a blob URL built out of `config.Manifest`. Idempotent: an existing
   `link[rel=manifest]` has its `href` replaced, never duplicated. It also sets
   `meta[name=theme-color]`, because a manifest alone does not tint the address bar on the first
   load, before the app is installed.
2. **Registers the worker** at `versionedWorkerUrl config` with `config.ServiceWorkerScope`.

The manifest is linked **before** the support check, so a browser without service workers still gets
an installable PWA. `boot` returns a four-case `BootResult` rather than a `bool`:
`OfflineDisabled` / `WorkerRegistered` / `WorkerUnsupported` / `WorkerFailed`. Collapsing those is
how an unsupported browser silently looks like a working one.

### Cache versioning — the URL is the mechanism

```
/offline-sw.js?v=<CacheVersion>&cache=<CachePrefix>
```

`versionedWorkerUrl` appends both from `OfflineConfig`. Two things follow, and both are the point:

- **A version bump changes the script URL**, so the browser fetches and installs a new worker
  instead of reusing the byte-identical old one. Without this, a deployment can ship new assets and
  have every returning user served the previous release's bundle from a worker that never updated.
- **The worker derives every cache name from its own URL** (`CACHE_PREFIX + '-' + CACHE_VERSION`), so
  the version lives in ONE config field rather than being hand-edited into a JS file each release.
  Its `activate` handler deletes every cache carrying the same prefix and a different version, so
  the previous generation is evicted rather than accumulating.

`install` calls `skipWaiting()` and `activate` calls `clients.claim()`. Safe here precisely because
cache names are version-scoped: a new worker never reads the old worker's entries, so taking over
early cannot serve a mixed generation.

### Fetch routing

| Match | Strategy |
|---|---|
| Cross-origin | **Not intercepted at all.** Intercepting breaks CDN fonts, analytics and auth redirects in ways that are hard to attribute back to the worker. |
| `/api/IOfflineSyncApi/*` | **Not intercepted.** These run when the network returns; a cached response to a replay would report a phantom success. |
| `GET /api/*` | Network-first, cache fallback. Only `response.ok` responses are cached — caching an error would serve that error offline for the life of the cache. |
| Other `/api/*` | Pass through; on failure return `503` with `x-toolup-offline: queued`. |
| Other `GET` | Cache-first, then network, then a `503` placeholder. |

**The worker does not queue anything itself.** A worker that replayed writes would be a second,
invisible writer racing the page's own `SyncCoordinator`, with its own copy of the queue and no way
to show the user a conflict. Queueing lives in the page. The worker's only job on a failed write is
to answer in a shape the page can recognise without having to distinguish a network error from a
server error.

---

## 2. IndexedDB schema

One database (name supplied by the caller — scope it per deployment, and per user where an origin is
shared between accounts), version `1`, one object store:

**Object store `mutations`**, `keyPath: 'id'`, with one index `revision` on the `revision` field
(non-unique) so reads come back in enqueue order without a client-side sort of the whole set.

| Field | Type | Notes |
|---|---|---|
| `id` | string | `MutationId`. Primary key — so `Enqueue` is idempotent by construction. |
| `enqueuedAt` | string | ISO-8601 round-trip (`ToString "o"`). Origination time. |
| `scopeId` | string | Echoed to the server for bookkeeping; **never** used to address storage. |
| `entityType` | string | The entity's registered `Type`. |
| `entityId` | string | The entity's `Id`. |
| `op` | string | `"save"` / `"delete"` — a stable wire token, not the F# case name. |
| `payload` | `Uint8Array` | Serialised entity for `save`; empty for `delete`. |
| `baseVersion` | number | The server version the offline edit was based on. `0` = created offline. |
| `revision` | number | Monotonic per-client. Replay order. Indexed. |
| `state` | string | `"pending"` / `"applied"` / `"conflicted"` / `"failed"`. |
| `reason` | string | Failure reason; `""` in every other state. |
| `attempts` | number | Failed replay count. Feeds `RetryPolicy.delayFor`. |
| `serverEntity` | `Uint8Array` \| null | The server document from the last conflict. |

### Two totality rules, both deliberate

`Timestamps.ofWire` returns `DateTimeOffset.MinValue` for an unparseable value, and `ofRecord`
returns `None` for a record with an unrecognised `op`. Neither throws. **A queue that cannot be read
is a queue whose contents are lost**, so no single bad record — one written by an older or newer SDK
build — may take the whole drain down with it.

### Why `[<Emit>]` and not a binding package

The surface needed is small and fixed: open a database, run one transaction against one object
store, read/put/delete by key. `Directory.Packages.props` carries no IndexedDB binding, and this file
is the only reason one would have been added — so it is bound directly rather than widening the
SDK's supply chain for six functions (GP 1's spirit applied to the client tier).

### Why promises and not `Async.FromContinuations`

IndexedDB is callback-shaped, so the natural F# wrapper is `Async.FromContinuations` — and that path
is recorded in this repo as **silently no-opping under Fable 5** when driven through `Cmd.OfAsync`
(see the note in `Platform.Client/Client/FileManagerUI.fs`, where it hung an upload with no error).
Every request is therefore wrapped into a JS promise inside its `[<Emit>]` body and awaited with
`Async.AwaitPromise` — the pattern `Platform.Client/Client/CsrfClient.fs` already proves.

---

## 3. Sync algorithm

### Triggers

Three, and the second and third exist because the first is not enough:

1. **The poll**, every `PollIntervalMs`. It reads the queue and issues **no request at all** when
   nothing is pending or failed — a quiet app costs one fold per interval.
2. **The `online` event.**
3. **`visibilitychange`.** A backgrounded tab has its timers throttled to roughly one per minute, so
   returning to the tab must not wait for the throttled tick.

`navigator.onLine` is read for the badge's status **and nothing else**. It reports whether the
machine has a link, not whether the server is reachable — captive portals and VPN drops both report
`true` — so the coordinator attempts a drain whenever anything is pending and treats a transport
failure as the real offline signal.

### One drain pass

```
due = queue.Drain(policy, now)          # Pending + Failed whose backoff elapsed, in revision order
for mutation in due:
    outcome = api.Apply mutation
    Applied  -> queue.MarkApplied      (entry removed)
    Conflict -> queue.MarkConflicted   (parked, server document stored beside it)
    Rejected -> queue.Discard          (removed, reason warned to the console)
    transport failure -> queue.MarkFailed; STOP the pass
```

Two decisions worth stating:

- **A transport failure stops the pass; a `Rejected` does not.** A dropped connection would
  otherwise burn every queued entry's retry budget at once, so the pass ends after the first one and
  reports `Disconnected = true`. A `Rejected` is permanent by contract — retrying it loops forever —
  so it is dropped, with a console warning, because a write the user believes they made silently
  disappearing is the worst failure this companion can produce.
- **Backoff is per-mutation, not per-drain.** One poisoned payload must not hold the rest of the
  queue behind it.

### Retry schedule

`RetryPolicy` is data (portability rule 3): `InitialDelayMs`, `Multiplier`, `MaxDelayMs`,
`MaxAttempts`. `RetryPolicy.delayFor policy attempt` is total and clamped — the exponent is capped at
30 before the power is computed, so a pathological attempt count yields the ceiling rather than
`int infinity`, which is `Int32.MinValue`, which reads as "retry immediately, forever". A
`Multiplier` below 1 is floored at 1. Defaults: 1 s doubling to a 5-minute ceiling over 8 attempts —
about twenty minutes of connectivity, long enough to ride out a flapping link and short enough that
a genuinely dead endpoint parks.

`DrainSelection.isRetryDue` measures the backoff from the **original enqueue time** plus the
accumulated delay, not from a stored last-attempt stamp. Deliberately conservative: the browser may
have been closed across the whole backoff window, in which case the entry is due immediately on the
next boot — which is what a field user reopening the app in signal wants.

`DrainSelection` lives in **Core**, not beside the IndexedDB implementation, so it is testable
off-browser. A rule that lives only inside an `[<Emit>]`-bearing client file is a rule nothing can
assert.

### Server-side replay — the three guards

1. **The scope is server-resolved.** `QueuedMutation.ScopeId` is echoed for the client's bookkeeping
   and is never used to address storage. A mutation naming a scope the caller does not hold is
   `Rejected` — a client that has been offline across a team switch must be told, not quietly
   written into whichever scope it last saw. An empty `ScopeId` is accepted, meaning "the client did
   not say", which is legitimate for a single-scope deployment.

2. **Conflict is detected in the handler, not by the store.** The phase design assumed
   `IEntityStore.Save` surfaces `EntityError.VersionConflict` for a stale write. **It does not.**
   `BlobEntityStore.Save` assigns `max(existing) + 1` unconditionally and never compares against the
   caller's version — `VersionConflict` exists in `EntityError` but that store never emits it. So the
   handler reads the head version first and compares it against `BaseVersion`. That comparison **is**
   the last-writer-wins guard; without it a replay silently clobbers every concurrent server-side
   edit, with no error anywhere.

3. **Replay is typed through a registration.** The wire carries `byte[]`; `Save<'T>` needs the real
   record so the store's index and full-text extractors run. Only the module that owns the entity
   knows `'T`, so it registers an `OfflineEntityReplay` — the same shape, and for the same reason, as
   `IDataMigrator.Migrate` (sanctioned erasure boundary 7 in the repo `CLAUDE.md`). An unregistered
   type is `Rejected`, never guessed at.

`OfflineEntityReplay.ofJson<'T>` takes the entity type **explicitly** rather than reflecting
`typeof<'T>.Name`, because the registered `Type` string is a wire value that may deliberately differ
from the CLR type name — inferring it would work until the first renamed record and then silently
stop matching.

`Apply` **re-reads after saving** and returns the stored bytes rather than the request bytes. The
store rewrites `Version` on the way in and may run extractors that normalise fields; handing the
client back a document that disagrees with the server on version is the precise state that
manufactures the next conflict.

`ApplyBatch` applies sequentially and **does not abort on a conflict** — the remaining mutations may
touch unrelated entities, and holding them hostage is how a queue never drains. Over-ceiling batches
are truncated rather than refused, because the client's drain loop is resumable; refusing would
strand a client whose backlog grew past the limit.

---

## 4. Audit — six-rule and provenance notes

### What is emitted

Opt in with `OfflineSyncOptions.withAuditEventStore`. An **applied** replay then writes one
`ModuleEvent` with:

- `OccurredAt` = the mutation's `EnqueuedAt` (origination, **not** application time)
- `SourceModule` = `AuditSourceModule.value`
- `EventType` = `EntityCreated` (new version 1) / `EntityUpdated` / `EntityDeleted`
- `Payload` = an `EntityLifecycleEventPayload` carrying the **resolved caller's** user id

A **conflicted** or **rejected** replay emits nothing — nothing was written, so auditing it would
record a write that did not happen.

### Why `IEventStore` and not `IAuditLog`

`IAuditLog.Record(scopeId, event)` accepts neither fact this phase requires. It builds the envelope
internally with `Events.create`, which stamps `DateTime.UtcNow`; and `BlobEntityStore` hard-codes
`UserId = "system"` in its own emission, because `IEntityStore.Save` does not carry caller identity.
Writing the `ModuleEvent` directly — with the same source module, event-type name and payload shape
the audit codec expects — is the only path that preserves both. `AuditReplicator` rebuilds the
`AuditEnvelope` from `modEvt.OccurredAt`, so downstream sinks see the origination time with **no
sink-side change**.

### The known consequence, stated rather than hidden

When the entity store is **also** composed with an `IAuditLog`, an applied replay produces **two**
lifecycle rows for the same entity version:

| Row | `UserId` | `OccurredAt` | Records |
|---|---|---|---|
| the store's | `"system"` | application time | that the write landed |
| this one | the real user | origination time | that the user made the edit |

They are distinguishable by `UserId` and they record genuinely different facts, but they are **not
deduplicated**. Collapsing them cleanly needs a dedicated `AuditEvent.OfflineMutationReplayed` case,
which is a breaking union-case addition to `ToolUp.Platform.Core` — it ripples into `AuditTypes.fs`,
the `AuditLog` codec registry, every exhaustive match over the union (FS0025 is an error tree-wide),
the CEF formatter, and three api-baselines. That is deliberately outside this phase's scope, and it
is why the emission is opt-in: a deployment that does not need origination-time audit pays neither
the second row nor the cost.

### Six-rule portability audit — `IOfflineQueue`

| Rule | How it is satisfied |
|---|---|
| 1. Identity by value | `MutationId` is a `string`. Nothing returns a live handle, cursor or transaction. |
| 2. Async at every boundary | Every member returns `Async<_>`. |
| 3. Retry + supervision as data | The queue stores `Attempts` and nothing else about retrying; the schedule is the caller's `RetryPolicy` **record**. No `OnFailure` callback parameter anywhere. |
| 4. Stateless between calls | Every member takes the ids it needs; `Drain` takes `policy` and `now` as parameters rather than reading ambient state. The only cross-call state is the opened database handle — a resource, not semantics. |
| 5. No cross-shard ordering promises | Ordering is guaranteed **only** within one client's queue, by `LocalRevision`. Nothing is promised relative to another device's queue; the server applies each mutation independently. |
| 6. Precision at the lower bound | `EnqueuedAt` carries the browser clock's precision (typically milliseconds, coarsened further by cross-origin-isolation mitigations). `RetryPolicy` delays are honoured to whole milliseconds and are a **lower** bound on the wait — background-tab timer throttling means a sub-second delay is not a wall-clock promise. |

The same rules are why `InMemoryOfflineQueue` is possible at all, and both implementations route
`Drain` through `DrainSelection.eligible` so they cannot disagree about what is due.

---

## 5. Testing

`src/ToolUp.Offline.Tests` (Expecto pack `Offline`, wired into `VerifyAll`) covers the pure half:
the retry schedule including its overflow clamps, the status derivation and its documented
precedence, the queue-stats fold, the wire-name round trips, the ISO timestamp round trip the
IndexedDB queue depends on, `DrainSelection`, and the handler's three guards against a fake
`IEntityStore` and a capturing `IEventStore`.

Each guard has a **paired go-red** in the suite: the scope test also asserts that the *matching*
scope applies, and the conflict test also asserts that the *matching* version applies — so a guard
that refused everything would fail too.

The IndexedDB and Feliz surfaces are browser-only; they ride the Fable compile gate
(`samples/MinimalClient`) rather than this pack. The worked end-to-end example in the phase's
acceptance — drop the network mid-mutation, reconnect, force a conflict — is an operator-run browser
exercise; there is no in-tree harness that drives a real service worker.

---

## 6. Composition checklist

- [ ] `examples/offline-sw.js` copied to the public-asset root, `PRECACHE_URLS` adjusted for the shell.
- [ ] `ClientConfig.Offline = EnabledOffline …`, with `CacheVersion` bumped per release.
- [ ] `ServiceWorkerRegistration.boot config.Offline` called at app start.
- [ ] `SyncCoordinator.start queue api offlineConfig` — and its `Stop` called on unmount.
- [ ] `OfflineConflictResolver` mounted in the shell (it renders nothing when there are no conflicts).
- [ ] `OfflineStatusBadge` mounted (it renders nothing when online and settled).
- [ ] One `OfflineEntityReplay` registered per offline-editable entity type, server-side.
- [ ] `offlineSyncApi` mounted with `OfflineSyncApi.routeBuilder`.
- [ ] Sign-out calls `IOfflineQueue.Clear` **and** `ServiceWorkerRegistration.unregister`.
