# ToolUp.Offline

Offline-first / Progressive Web App support for ToolUp Platform applications.

A field worker loses signal. They keep working. Their edits are saved on the device, and the moment
connectivity returns they are sent to the server — with the time the user actually made them, not
the time the network came back. Where someone else changed the same record in the meantime, the user
is shown both versions and picks.

Three packages, composed independently:

| Package | Tier | What it carries |
|---|---|---|
| `ToolUp.Offline.Core` | shared | `QueuedMutation` / `SyncOutcome` / `RetryPolicy` / `SyncStatus`, and the `IOfflineSyncApi` wire contract. Fable-safe. |
| `ToolUp.Offline.Server` | server | The replay handler — routes queued mutations through `IEntityStore.Save` with last-writer-wins conflict detection, plus opt-in audit emission. |
| `ToolUp.Offline.Client` | browser | Service-worker + PWA-manifest boot, the IndexedDB queue, the drain-on-reconnect coordinator, and the conflict-resolver / status-badge components. |

## Off by default

`ClientConfig.Offline` defaults to `NoOffline`. Under that value nothing happens: no service worker
is registered, no IndexedDB database is opened, no manifest is linked, no install prompt appears, no
badge is mounted. A deployment that upgrades and does not opt in is byte-for-byte unchanged
(GP 11 + GP 13).

## What is cached, what is queued

| Request | Behaviour offline |
|---|---|
| App shell (HTML, JS, CSS, images) | Served from cache. The app boots with no network at all. |
| `GET /api/*` | Network first; the last successful response is served when the network fails. Stale data, clearly better than an error page. |
| `POST` / `PUT` / `DELETE` `/api/*` | The request fails and the page **queues the mutation** in IndexedDB. The user is told it is saved on the device. |
| `/api/IOfflineSyncApi/*` | Never cached, never queued — these are the endpoints that run when the network returns. |

Queued mutations live **only on the client** until they are applied or discarded. The server holds
no per-client pending state.

## Conflict resolution UX

v1 is **last-writer-wins with an explicit user choice.** There is no automatic merge — CRDT-based
merge is a deliberate follow-up, not a gap this release papers over.

When a queued mutation replays, the server compares the version the offline edit was based on
against the version it now holds:

- **They match** — the edit applies. The user sees nothing; it simply saved.
- **They differ** — someone else changed the record. The mutation is parked, and
  `OfflineConflictResolver` shows both documents side by side with three choices:
  - **Keep mine** — the offline edit is rebased onto the server's current version and replayed.
  - **Keep theirs** — the offline edit is discarded.
  - **Decide later** — the conflict stays parked and the status badge keeps reporting it.

The resolver renders both documents as text by default, because the SDK has no schema knowledge of
your entity. Pass `RenderDocument` to render your own record shape instead.

`OfflineStatusBadge` is a fixed-position pill reporting `Offline` / `Syncing` / `N conflicts`. It
renders **nothing** when everything is online and settled — a permanent "Online" indicator teaches
users to ignore the element that matters when it changes.

## Quick start

**1. Copy the reference service worker.** `examples/offline-sw.js` ships in the client package under
`contentFiles/`. Put it at the root of your public assets so it is served from `/offline-sw.js` and
can claim the whole origin. It is a template — read it and adjust `PRECACHE_URLS` for your shell.

**2. Turn offline on in your client config.**

```fsharp
{ ClientConfig.create handlers with
    Offline =
        EnabledOffline {
            OfflineConfig.defaults with
                CacheVersion = "2026-08-31"  // bump per release to evict old caches
                Manifest =
                    Some {
                        PwaManifest.defaults with
                            Name = "Site Inspections"
                            ShortName = "Inspect"
                            ThemeColor = "#1e40af"
                    }
        } }
```

**3. Boot it, and start the coordinator.**

```fsharp
open ToolUp.Offline.Client

let queue = OfflineQueue.create "inspections-offline"
let api = SyncCoordinator.defaultProxy ()

async {
    let! _ = ServiceWorkerRegistration.boot config.Offline

    match config.Offline with
    | EnabledOffline offlineConfig ->
        SyncCoordinator.start queue api offlineConfig |> ignore
    | NoOffline -> ()
}
|> Async.StartImmediate
```

**4. Register a replay adapter per entity type, server-side.** The wire carries bytes; only the
module that owns the record knows its shape, so it supplies the typed adapter:

```fsharp
open ToolUp.Offline.OfflineSyncHandler

let options =
    OfflineSyncOptions.defaults
    |> OfflineSyncOptions.withReplays [ OfflineEntityReplay.ofJson<Inspection> "Inspection" ]

// Mount alongside your other APIs:
makeApi (fun ctx -> offlineSyncApi entityStore options ctx)
```

An entity type with no registered adapter is **rejected**, never guessed at.

## Audit

Opt in with `OfflineSyncOptions.withAuditEventStore`. An applied replay then emits an
`EntityCreated` / `EntityUpdated` / `EntityDeleted` audit record stamped with the mutation's
**original enqueue time** and the **applying user's id** — so an inspection edited at 09:14 in a
tunnel and synced at 11:02 is audited as having happened at 09:14, by the person who made it.

Read [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md) before enabling it: when your entity store is also
composed with an `IAuditLog`, two lifecycle rows appear for the same version, and the guide explains
why and how to tell them apart.

## Requirements and limits

- **Secure origin.** Service workers require HTTPS (or `localhost`). On an insecure origin
  `ServiceWorkerRegistration.boot` returns `WorkerUnsupported` and offline caching does not engage;
  the queue still works.
- **IndexedDB.** Unavailable in some private-browsing modes and locked-down webviews. The queue then
  falls back to an in-memory implementation and **warns to the console** — offline edits made in
  that session are lost if the tab closes before reconnecting.
- **Sign-out must clear both.** Call `IOfflineQueue.Clear` and
  `ServiceWorkerRegistration.unregister` — a queue left populated replays one user's writes under
  the next user's credentials, and a worker left registered serves the previous user's cached
  responses.

## Out of scope (deliberately)

- Real-time multi-device sync. Edits sync when **that** client reconnects; no peer-to-peer, no live
  cursors.
- CRDTs. v1 is last-writer-wins with the explicit conflict UI described above.
- The `BackgroundSync` API. v1 polls, plus `online` and `visibilitychange` triggers.
- A mobile-native client. PWA is as close to mobile as the SDK goes.

## Licence

Apache-2.0. See [LICENSE](../../LICENSE).
