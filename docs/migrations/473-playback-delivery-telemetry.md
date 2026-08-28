# Media playback + delivery telemetry (consumer migration)

**What changes.** `ToolUp.MediaLibrary` gains two telemetry emissions and one
new endpoint. Every media response now counts the bytes it actually wrote and
attributes them to `(mediaId, scope)`; `POST /api/media/beacon` accepts typed
playback events from a player. Both flow into the shipped `IMetricsSink`
(Phase 9e) and `IUsageLog` (Phase 9d) substrates — there is no new store, no
new API, and no dashboard.

**Scope.** Additive. A deployment that composes neither a metrics sink nor
usage metering is byte-for-byte unchanged on the serve path and allocates
nothing for telemetry (GP 13). The beacon endpoint is mounted only under
`ServerConfig.MediaLibrary = EnabledMediaLibrary`.

## Diff to apply

**None is required.** Upgrading the package changes nothing observable until
you compose a sink. To start receiving the numbers:

```fsharp
{ ServerConfig.defaults with
    MediaLibrary = EnabledMediaLibrary
    // Either one alone is enough to receive that half.
    UsageMetering = EnabledUsageMetering
    MetricsEndpoint = EnabledMetricsEndpoint }
```

The two metric series are declared into `ServerApp.MetricRegistrations` by
`MediaLibraryServerApp.run` — you do not register them yourself.

### If your player should report playback

Post a beacon at start, periodically, and at end. Every response is `204`;
there is nothing to branch on.

```json
{ "mediaId": "3f2b…", "event": "started",   "session": "opaque-player-handle" }
{ "mediaId": "3f2b…", "event": "progress",  "percent": 42, "session": "opaque-player-handle" }
{ "mediaId": "3f2b…", "event": "completed", "session": "opaque-player-handle" }
```

The beacon rides the same credential the media itself did: an authenticated
scope, or `?token=…` carrying the signed-URL token that admitted the stream
(the token must be for the same `mediaId`). `session` is any opaque
client-minted handle — it is hashed with the scope and media id before
storage and never persisted raw, so a stable per-tab value is exactly right
and a user identifier is neither needed nor wanted.

### Reading the numbers back

```fsharp
let! rows = usageQueryApi.Query(None, Some { From = from; To = until })
let rollups = PlaybackTelemetry.PlaybackRollup.ofUsageRecords rows
```

`ofUsageRecords` is pure and ignores unrelated records, so passing a whole
scope's ledger is the expected call.

## Vocabulary this adds

| Where | Name | Notes |
|---|---|---|
| `IUsageLog` | `media.egress.bytes` | `Quantity` = bytes written; `Metadata`: `mediaId`, `class` |
| `IUsageLog` | `media.playback.events` | `Quantity` = 1; `Metadata`: `mediaId`, `event`, `session`, `percent` (progress only) |
| `IMetricsSink` | `toolup.media.egress.bytes` | Histogram, `bytes`, tags `scope` + `class` |
| `IMetricsSink` | `toolup.media.playback.events` | Counter, tags `scope` + `event` |

`class` is `original` / `manifest` / `segment` / `poster`. The media id is a
ledger `Metadata` key and deliberately **not** a metric tag — a metric tagged
by media id would exhaust the sink's per-metric series ceiling.

## The one thing to read carefully

**`media.egress.bytes` is ORIGIN egress, not delivered egress.** With a CDN in
front of these routes, an edge hit never reaches the origin: the bytes are
served from a POP and nothing in this process observes them. The gap grows
with the `s-maxage` the deployment declared per response class.

Forge does not estimate the delivered figure from that declaration — a number
derived from a TTL is a guess wearing a measurement's clothes. Treat this as
an origin cost line and join it with the CDN's own reporting when you need
what viewers actually received. A deployment with no CDN composed has no gap:
origin egress *is* delivered egress.

## Verification

- `dotnet build ToolUp.Forge.sln`
- `dotnet run --project Build.fsproj -- VerifyAll` — the `MediaLibrary`
  suite covers the beacon validation matrix, the session correlator's
  privacy properties, the rollup fold, and byte reconciliation of every
  metered response against its served `Content-Range` window.

## Rollback

Revert the package. The endpoint and both emissions disappear together; no
persisted state is left behind beyond the usage rows already written, which
are ordinary `UsageRecord`s and can be filtered out by `ResourceKind`.

## See also

- [`media-library.md`](../companions/media-library.md) — the companion's telemetry + monetisation section.
- [`472-edge-cache-seam.md`](472-edge-cache-seam.md) — the edge declaration that determines how large the origin-vs-delivered gap is.
- [`69l-telemetry-zero-cost-gate.md`](69l-telemetry-zero-cost-gate.md) — the GP 13 gate pattern the egress accounting follows.
