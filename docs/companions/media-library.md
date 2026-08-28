# Media library — `ToolUp.MediaLibrary`

Time-based-media hosting (video / audio) over the SDK's configured
`IBlobStorage`. Adds the four things raw blob storage lacks for serving
media: **HTTP range requests** (`206 Partial Content`) for `<video>`
seeking, **scope-signed expiring URLs** so gated media is not
world-readable, **poster / thumbnail derivation**, and **HLS / transcode
hooks** delivered as opt-in sub-companions. The default implementation
range-serves over blob storage with **zero transcode dependency**; FFmpeg
and cloud transcode are opt-in.

Phase 88. Pairs with [`ToolUp.AssetStore`](../../src/ToolUp.AssetStore/)
(the still-image analogue) and the Narrative
[`Video` / `Audio` blocks](../platform/narrative-elements.md) (Phase 87)
that a media item serves into.

## Enabling

`ToolUp.MediaLibrary` is opt-in (GP 13). Set the mode and compose with
`MediaLibraryServerApp` instead of `ServerApp`:

```fsharp skip=fragment
open ToolUp.MediaLibrary
open ToolUp.MediaLibrary.MediaCompose

let config = { ServerConfig.defaults with MediaLibrary = EnabledMediaLibrary }

MediaLibraryServerApp.create ()
|> MediaLibraryServerApp.withConfig config
|> MediaLibraryServerApp.withStorage blobStorage
|> MediaLibraryServerApp.withAuth authProvider
|> MediaLibraryServerApp.withNotifications notifications   // optional — ingestion status
|> MediaLibraryServerApp.withOptions { MediaLibraryOptions.defaults with MaxBytes = 4L * 1024L * 1024L * 1024L }
|> MediaLibraryServerApp.run
```

When `MediaLibrary = NoMediaLibrary` (the default), `run` short-circuits
byte-for-byte to `ServerApp.run` — no handlers, no DI, no signing key, no
probe. A deployment that doesn't host media pays nothing.

## Endpoints

| Route | Purpose |
|---|---|
| `GET /api/media/stream/{mediaId}` | Scoped (authenticated) stream — honours `Range` → `206` / `416`. |
| `GET /media/signed/{mediaId}?token=…` | Scope-signed public stream — verifies HMAC signature + expiry + scope before serving. |
| `GET /api/media/hls/{mediaId}/{file}` | HLS master manifest / variant / segment (present when a transcoder ran). |
| `POST/GET /api/media/*` | Fable.Remoting `IMediaApi` — `GetMedia` / `ListMedia` / `DeleteMedia` / `GetSignedUrl`, plus the Phase 469 chunk endpoints `BeginUpload` / `AppendChunk` / `CommitUpload` / `AbortUpload`. |

### Range serving (`206` / `416`)

Every serving route honours the `Range` header:

- No `Range` → `200 OK` + `Accept-Ranges: bytes` (full body).
- `Range: bytes=START-END` (satisfiable) → `206 Partial Content` +
  `Content-Range: bytes START-END/TOTAL`.
- An out-of-bounds range → `416 Range Not Satisfiable` +
  `Content-Range: bytes */TOTAL`.
- `If-Range` is honoured against the content-hash `ETag`: a stale
  validator falls back to a full `200`.

A browser `<video src="/api/media/stream/{id}">` seeks correctly because
the server answers each scrub with a `206` for the requested byte window.
The pure decision logic is `ByteRange.parse` (unit-tested across the full
`206` / `416` matrix in
[`MediaLibraryTests`](../../src/ToolUp.Platform.Tests/InProcess/MediaLibraryTests.fs)).

### Scope-signed expiring URLs (GP 4)

A gated media item is never served from a world-readable blob URL.
`IMediaLibrary.SignedUrl id scope ttl` mints a URL whose token HMAC-signs
`(MediaId, ScopeId, Container, ExpiresAt)`:

```fsharp skip=fragment
let! url = mediaLibrary.SignedUrl(mediaId, viewerScope, TimeSpan.FromMinutes 15.0)
//  /media/signed/{mediaId}?token=… — valid for 15 minutes, this scope only
```

The signing key is a 32-byte secret in `ISecretStore` under the reserved
`_platform` scope (key `media_library_signing_key`), auto-generated on
first use — the same lifecycle as the share-token signing key. The
`/media/signed/…` route verifies the signature, the expiry, and that the
signed `MediaId` matches the route before serving a byte; a leaked URL
stops working at its TTL, and a URL signed for one scope cannot read
another scope's media.

## Uploading large media

The `MaxBytes` cap is only reachable if a single request survives end to
end. A dropped connection at 1.9 GiB of a 2 GiB upload otherwise restarts
from zero, so anything above a few hundred megabytes wants the
**resumable** path: open a session, append chunks addressed by absolute
byte offset, commit.

```
BeginUpload  → sessionId
AppendChunk  → cursor          (repeat; idempotent at an accepted offset)
CommitUpload → MediaRecord
AbortUpload  → discard
```

`AppendChunk` returns an `UploadProgress` whose `ReceivedBytes` is both
"how far we got" and "the offset the next chunk must carry" — one number,
so a resuming client has nothing to compute. A chunk at any other offset
is refused with `OffsetMismatch(expected, received)`, and `expected` is
that same cursor. **Re-sending a chunk already accepted at its offset,
with the same length, is a no-op** — which is what makes the client loop
below safe to retry after a request whose response it never saw.

```fsharp skip=fragment
// `api` is the Fable.Remoting `IMediaApi` proxy.
let uploadResumable (api: IMediaApi) (bytes: byte[]) (filename: string) (mimeType: string) = async {
    match! api.BeginUpload(filename, mimeType, int64 bytes.Length, None) with
    | Error e -> return Error e
    | Ok sessionId ->
        let chunkSize = 8 * 1024 * 1024      // ≤ MediaLibraryOptions.MaxChunkBytes
        let mutable offset = 0L
        let mutable failure = None

        while failure.IsNone && offset < int64 bytes.Length do
            let take = int (min (int64 chunkSize) (int64 bytes.Length - offset))
            let chunk = bytes[int offset .. int offset + take - 1]

            match! api.AppendChunk(sessionId, offset, chunk) with
            | Ok progress ->
                // The server's cursor, not a locally incremented one:
                // an idempotent re-append returns the cursor unmoved,
                // and this loop then simply does not advance.
                offset <- progress.ReceivedBytes
            | Error(OffsetMismatch(expected, _)) ->
                // Resume from where the server actually is.
                offset <- expected
            | Error e -> failure <- Some e

        match failure with
        | Some e -> return Error e
        | None -> return! api.CommitUpload sessionId
}
```

A real client wraps `AppendChunk` in its own transport retry: on a
timeout or a dropped socket it re-sends the same chunk at the same
offset, and the server either accepts it (the first attempt never
landed) or reports the unchanged cursor (it did). Either way the object
is correct.

### What is validated, and when

Validation is deliberately split across the two ends of a session:

| At `BeginUpload` (fail fast) | At `CommitUpload` (the only honest measurement) |
|---|---|
| Filename non-blank | Actual assembled byte count |
| MIME in `AcceptedMimeTypes` | Content hash (SHA-256, the record's `ContentHash`) |
| **Declared** size within `MaxBytes` | Declared-vs-actual agreement |

A commit that is **short** returns `IncompleteUpload` and **keeps** the
session — the client's remaining chunks are still worth sending. A commit
whose assembled bytes **exceed** the declaration or the deployment cap
fails **closed**: the session and its chunks are destroyed, so a client
cannot retry its way past `MaxBytes`.

### Commit is the ordinary upload path

`CommitUpload` assembles the chunks and hands them to
`IMediaLibrary.Upload`. The committed item is therefore
indistinguishable from a single-shot upload of the same bytes — same
`media/originals/{mediaId}` layout, same `ContentHash`, same
`Queued → … → Ready` ingestion over `INotificationChannel`, same
derivation and transcode hooks. That is a property by construction
rather than a claim to be maintained.

### Commit costs one chunk, not one object

Assembly used to materialise the object in memory, because
`MediaUploadRequest` carries `byte[]` — so a 2 GiB commit pinned ~2 GiB
of heap, the same ceiling single-shot upload has. Resumable upload
removed the **network** single point of failure; this removes the
server-side memory one.

The chunks are already blobs, so assembly is a **store-side compose over
parts the store already holds**: `IBlobStorage.ComposeFrom` concatenates
them into `media/originals/{mediaId}`, and commit hands the library the
two facts it measured while walking the parts — the size and the
content hash — instead of the bytes. Peak memory is one chunk.

Both paths survive, and which one a deployment takes is decided by one
cheap read at commit time. The streaming path is taken when **both**
hold:

- the composed `IBlobStorage` declares `CanComposeFrom = true` — the
  bundled local / Azure / S3 / GCS stores all do; the whole-blob
  encryption decorator does not (see
  [Encrypted originals](#encrypted-originals-are-correct-but-not-cheap-to-seek)); and
- no derivation or transcode provider is installed. Poster extraction
  and HLS transcoding both take the original **bytes**, so a deployment
  that has composed `ToolUp.Media.FFmpeg` (or any provider declaring
  `CanExtractPoster` / `CanTranscodeHls`) materialises regardless — the
  bytes are needed either way.

Otherwise commit assembles in memory exactly as before. The fallback is
not a degraded corner to be worked around: it is the shipped behaviour
for every encrypted deployment and every transcoding deployment, and it
produces a **content-hash-equal record** either way. The size, contiguity
and hash checks are one shared walk over the chunks, so the two paths
cannot diverge on what they accept.

A `media_library:composed-commit` config validator emits a startup
`Warning` — never an error — when the composed store cannot compose, so
a deployment that sized its heap for O(chunk) commits learns at preflight
rather than at its first multi-gigabyte upload.

Custom `IBlobStorage` implementations gain the member; declining it is a
two-line adoption. See
[`docs/migrations/741-iblobstorage-compose-from.md`](../migrations/741-iblobstorage-compose-from.md).

### Progress + audit

When an `INotificationChannel` is composed, every accepted chunk
publishes `CustomNotification("MediaLibrary.UploadProgress", …)` to the
uploader's scope. The payload is `MediaUploadProgressUpdate`
(`SessionId` / `MediaId` / `FileName` / `ReceivedBytes` /
`DeclaredSizeBytes` / `Phase`), where `Phase` is `"appending"`,
`"committed"`, or `"aborted"`. `CommitUpload` and `AbortUpload` are
audited (`Custom:MediaUploadCommitted` / `Custom:MediaUploadAborted`) by
the dispatcher's `[<Audit>]` classifier, the same mechanism
`DeleteMedia` uses.

### Sessions are scope-isolated, and expire

A session's blobs live under the opening scope's container, so another
scope cannot address it at all (GP 4); the container recorded in the
session's own manifest is re-checked on every call as a second line, for
a store that does not isolate by container.

An abandoned session is reclaimed **opportunistically** — `BeginUpload`
sweeps sessions whose last append is older than
`MediaLibraryOptions.UploadSessionTtl` (default 24 h). There is no
`BackgroundService` and no timer, so a deployment that never opens a
session runs no sweep at all (GP 13).

```fsharp skip=fragment
app
|> MediaLibraryServerApp.withOptions
    { MediaLibraryOptions.defaults with
        MaxChunkBytes = 4 * 1024 * 1024
        UploadSessionTtl = TimeSpan.FromHours 2.0 }
```

`MaxChunkBytes` (default 8 MiB) caps one `AppendChunk` body; a larger
chunk is refused with `ChunkTooLarge` before a byte is written. It bounds
the *transport* unit — `MaxBytes` still caps the assembled item. A
non-positive value for either falls back to the default at read time
rather than failing, exactly as `RangeChunkBytes` does, so a hand-built
options record that omits them cannot break uploads.

Note this is a cap on the **decoded chunk**, applied by the session
store. A deployment that also wants to reject an oversized request
before it is buffered sets ASP.NET Core's own request-body limit
alongside it.

## Ingestion status

When an `INotificationChannel` is composed, uploads publish
`CustomNotification("MediaLibrary.IngestionStatus", …)` to the uploader's
scope as the item moves `Queued → Transcoding → Ready` (or `Failed`), so
an admin UI can show progress. The payload is `MediaIngestionStatusUpdate`
(`MediaId` / `FileName` / `Status` / `Reason`).

## Derivation + transcode (GP 1)

Poster-frame extraction and HLS rendition production are heavy,
FFmpeg/cloud-backed concerns, so they sit behind `IMediaDerivation`
(poster + probe) and `IMediaTranscoder` (HLS) and ship as **opt-in
sub-companions**. The default providers (`NoopMediaDerivation` /
`NoopMediaTranscoder`) declare no capability, so the default library
stores the original and serves a single-file progressive download with
**no media-binary dependency**.

### `ToolUp.Media.FFmpeg`

Shells out to the system `ffmpeg` / `ffprobe` binaries (must be on
`PATH`; ships no bundled binary):

```fsharp skip=fragment
open ToolUp.Media.FFmpeg.FFmpegMediaProvider

app
|> MediaLibraryServerApp.withDerivation (FFmpegMediaProvider.create None None)        // poster + probe
|> MediaLibraryServerApp.withTranscoder (FFmpegMediaProvider.createTranscoder None)   // HLS package
```

`ExtractPoster` grabs frame 1 as a JPEG; `Probe` reads duration +
dimensions via `ffprobe`; `TranscodeToHls` produces a master `.m3u8` +
`.ts` segments (stream-copy single rendition — extend the ladder for
production). When installed, an item's poster appears in
`MediaRecord.PosterBlob` and its HLS package is served from
`/api/media/hls/{id}/…`.

### `ToolUp.Media.CloudTranscode`

The seam for cloud-managed transcode (AWS MediaConvert, Mux, Coconut, …).
The SDK ships **no cloud-vendor SDK**; the deployment supplies a `submit`
callback that runs the provider job and returns the produced HLS files:

```fsharp skip=fragment
open ToolUp.Media.CloudTranscode.CloudTranscodeProvider

let transcoder =
    CloudTranscodeProvider.create (fun originalBytes mimeType -> async {
        // deployment calls MediaConvert / Mux / … and returns the rendered
        // TranscodedFile list (master manifest + segments), or an error.
        return Ok producedFiles
    })

app |> MediaLibraryServerApp.withTranscoder transcoder
```

`CloudTranscodeProvider.notConfigured ()` is the placeholder until a
callback is wired (declares no capability — single-file progressive
download).

## Gated HLS — AES-128 segments + a scope-gated key

A scope-signed URL protects **one file**. HLS does not have one file: a
rendition is a manifest plus N segment blobs, and the moment those
segments are statically exported or cached at a CDN edge, the origin's
route auth is no longer on the path. The bytes are simply there.

So the segments are encrypted and the **key** is what stays gated:

```fsharp skip=fragment
app
|> MediaLibraryServerApp.withTranscoder (FFmpegMediaProvider.createTranscoder None)
|> MediaLibraryServerApp.withOptions
    { MediaLibraryOptions.defaults with EncryptHlsByDefault = true }
```

Per-upload instead of (or against) the deployment default:

```fsharp skip=fragment
MediaUploadRequest.createWithEncryption
    options bytes filename mimeType uploadedBy caption (Some true)
```

`MediaUploadRequest.create` leaves the preference **unstated** (`None`),
which resolves to `EncryptHlsByDefault` — so every pre-existing call site
keeps its exact behaviour, and the shipped default is `false` (GP 11).

### What happens at transcode time

1. A 16-byte AES-128 key is minted and stored in `ISecretStore` under the
   item's **owning scope container**, keyed `media_hls_key:{mediaId}` —
   never beside the segments (GP 4).
2. The transcoder encrypts the segments and writes
   `#EXT-X-KEY:METHOD=AES-128,URI="/api/media/hls-key/{mediaId}"` into the
   manifest. The FFmpeg sub-companion does this through
   `-hls_key_info_file`; the key file it needs lives in a temp directory
   of its own, deleted whether the pass succeeds or fails, and never in
   the directory whose contents are uploaded.
3. On serve, the manifest's key URI is rewritten to an **origin-absolute**
   URI, so an exported or CDN-served manifest still points back at the
   origin's gate rather than at whatever host handed it over.

**Encryption is fail-closed.** Encryption requires a transcoder declaring
the optional `IMediaHlsEncryptingTranscoder` capability. An upload that
asks to be encrypted and cannot be fails its ingestion
(`MediaIngestionStatus.Failed`) rather than quietly producing a bare
rendition — a silently-unencrypted gated video is the exact exposure this
is for, and nothing would say so.

### The key endpoint

`GET /api/media/hls-key/{mediaId}` returns the raw 16 bytes with
`Cache-Control: no-store`. It admits on **the same two credentials the
media bytes themselves are reachable by**, so the key is never easier to
obtain than the video it decrypts:

| Credential | Outcome |
|---|---|
| A resolved scope (the `/api/media/stream/…` gate) | Key for **that scope's** copy. |
| A valid `SignedUrl` token for **this** media id | Key for the signed payload's container, until the token's TTL expires. |
| A token that is expired, tampered, or minted for another media id | `403` — never a fall-through to the scope gate. |
| No credential at all | `401`. |
| An authenticated caller in a **different** scope | `404`. The refusal is structural: the key is filed under the owning container, so a foreign scope's lookup simply has no answer, and learns nothing beyond "not here". |

A `token` on the manifest request is **carried onto the rewritten key
URI**, which is what makes signed playback work end to end: the token
that admitted the manifest is bound to the same media id, so it admits
the key fetch too, on the same signing key and the same TTL. No second
token species.

Denials are recorded through `IAuthAuditHook`, landing in the same
queryable `AuthorizationDenied` trail as every other authorization denial
in the deployment; granted deliveries are logged when
`MediaLibraryOptions.EmitAudit` is on (GP 6).

### Rotation is a re-transcode

There is deliberately no rotate verb. A key is bound to the ciphertext of
the segments produced with it — handing out a new key without
re-encrypting makes the rendition unplayable, and keeping both makes
revocation meaningless. To rotate, re-upload or re-derive the item: the
new pass mints a fresh key and replaces the segments in the same act.
`IMediaLibrary.Delete` destroys the key with the item, so a deleted video
leaves no live secret.

## Health + config

The companion registers a `media_library` readiness `IHealthCheck` (blob
store reachability) and two `IConfigValidator`s:

| Validator | Grade | Fires when |
|---|---|---|
| `media_library:options` | Error — aborts startup | A zero size cap, an empty MIME allowlist, or a non-positive signed-URL TTL. |
| `media_library:ranged-reads` | Warning — advisory only | The composed `IBlobStorage` refuses bounded ranged reads, so range serving falls back to whole-object slicing (see [above](#encrypted-originals-are-correct-but-not-cheap-to-seek)). |

## Storage layout

Per scope container:

```
{container}/media/originals/{mediaId}            raw bytes
{container}/media/records/{mediaId}.json         MediaRecord
{container}/media/derived/{mediaId}/poster.jpg   poster frame
{container}/media/derived/{mediaId}/hls/…        HLS manifest + segments
                                                 (ciphertext when encrypted;
                                                  the key is in ISecretStore,
                                                  never in the container)
{container}/media/uploads/{sessionId}/session.json    in-flight upload session
{container}/media/uploads/{sessionId}/chunks/{offset}  accepted chunk
```

An upload chunk's file name is its **absolute start offset**, zero-padded
to 20 digits, so a lexical listing is also the numeric order: assembly
needs no chunk index, a duplicate append is one `GetMetadata`, and the
session manifest stays a bounded record however many chunks arrive.
Everything under `media/uploads/` is transient — it is deleted at commit,
at abort, and by the TTL sweep.

## Range serving reads O(range), not O(object)

`OpenRange` serves a byte window through `IBlobStorage.DownloadRange` in
bounded chunks: a scrub into a 2 GiB video costs the requested window
plus at most one chunk of look-ahead. The object's size comes from
`GetMetadata`, so no read is ever open-ended — the storage seam has no
"offset to EOF" form precisely so that no implementation can be tempted
to materialise a whole object to satisfy a range.

The chunk is `MediaLibraryOptions.RangeChunkBytes` (default 1 MiB).
Raising it trades peak memory per in-flight response for fewer round
trips; lowering it does the reverse:

```fsharp skip=fragment
app
|> MediaLibraryServerApp.withOptions
    { MediaLibraryOptions.defaults with RangeChunkBytes = 4 * 1024 * 1024 }
```

The same bounded path serves derived blobs — HLS manifests and segments,
posters — so a player range-requesting a segment reads only its window.
That affordance is the optional `IMediaRangeReader` capability interface
rather than a member on `IMediaLibrary`: an implementation that answers
with a CDN redirect has no window to open, so consumers probe for it and
fall back to the whole-blob `OpenDerived` read.

```fsharp skip=fragment
match box mediaLibrary with
| :? IMediaRangeReader as ranged -> // bounded window
| _ -> // whole-blob OpenDerived
```

### Encrypted originals are correct, but not cheap to seek

The whole-blob AES-GCM encryption-at-rest decorator (see
[storage providers](storage-providers.md)) refuses
ranged reads by design: a mid-blob ciphertext window is undecryptable
without the surrounding blob, so `EncryptedBlobStorage.DownloadRange`
returns an honest `Error` rather than plausible garbage.

Media over an encrypted store therefore falls back to the pre-existing
path — download the whole original, slice in memory. **Serving is
byte-for-byte identical**; only the cost differs, and it is O(object) per
range request. A 2 GiB encrypted video re-reads 2 GiB on every scrub.

This is a **startup advisory, not an error**. The
`media_library:ranged-reads` config validator probes the composed store
once at compose end (a sentinel write / ranged read / delete under
`_platform`) and emits a `Warning` naming the trade if ranged reads come
back refused. It is a live probe rather than a type test because a
decorator stack — resilience over encryption — type-tests as its
outermost layer while refusing ranges underneath. Every arm that cannot
answer stays silent rather than guessing.

Deployments that need both encryption and cheap seeking have two levers
today: keep large media in an unencrypted scope container, or supply a
custom `IMediaLibrary` via `MediaLibraryServerApp.withMediaLibrary` that
brokers a signed direct URL. A chunked encryption envelope — which would
make mid-blob ranges decryptable — is a separate, larger piece of work.

**The same envelope refuses `ComposeFrom`, for the same reason.** Each
stored part is its own AES-GCM envelope (nonce + ciphertext + tag), and
concatenating the envelopes does not produce the envelope of the
concatenated plaintext — a composed target would decrypt as nothing at
all. Composing the *plaintexts* would mean decrypting every part and
re-encrypting the whole, which materialises exactly what the member
exists to avoid, so the decorator refuses rather than fakes it. The
refusal is the decorator's **own**: it is not delegated to the inner
store, which can compose and would — over ciphertext.

So an encrypted deployment commits resumable uploads through the
materialised path, at O(object) memory, and the
`media_library:composed-commit` validator says so at startup. The
chunked envelope that would lift the range refusal lifts this one too.

## Playback + delivery telemetry

Hosting media without knowing plays, completion rates or egress bytes is
a gap, and a monetised deployment cannot close it after the fact — the
bytes have already left. Two emissions close it, and neither invents a
pipeline: both flow into the metrics sink (`IMetricsSink`) and the usage
ledger (`IUsageLog`) the SDK already ships.

### Egress accounting

Every media response counts the bytes it **actually wrote** and attributes
them to `(mediaId, scope)`. Not `Content-Length` — that is what the server
intended to send, and a `<video>` seek the viewer abandons mid-window is
exactly the case where the two differ. A `416` writes no body and meters
nothing.

Two surfaces receive it, at deliberately different resolutions:

| Surface | Name | Shape |
|---|---|---|
| `IMetricsSink` | `toolup.media.egress.bytes` | Histogram, `bytes`, tagged `scope` + `class` |
| `IUsageLog` | `media.egress.bytes` | One row per response; `Quantity` = bytes, `Metadata` carries `mediaId` + `class` |

`class` is one of `original` (the stored file, via `/api/media/stream/{id}`
or `/media/signed/{id}`), `manifest`, `segment`, or `poster` — the same
classes `MediaEdgeCacheOptions` declares cacheability for, keyed on the
same extension test.

**The media id is in the ledger row, not in the metric tag, and that is
deliberate.** `IMetricsSink` carries a per-metric distinct-tag-set ceiling
(default 1 000, with the overflow routed to one `_overflow=true` series),
and a media id is precisely the unbounded key that would blow it. The
ledger is row-shaped and partitioned by scope, so per-item attribution is
exact where it is queried and bounded where it is aggregated.

### This is ORIGIN egress, not delivered egress

**With a CDN in front (see the edge-cache seam), an edge hit never reaches
this process.** The bytes are served from a POP and nothing here observes
them, so `media.egress.bytes` is what left *this origin* — never what a
viewer received. The gap between the two grows with the `s-maxage` the
deployment declared per response class, and that declaration is the only
origin-side signal about its size.

Forge does not estimate delivered egress from it. A number derived from a
declared TTL would be a guess presented as a measurement; the CDN's own
logs are the authority for what an edge served. Read `media.egress.bytes`
as an origin cost line, and join it with the CDN's reporting when you need
the delivered figure.

### Playback beacons

`POST /api/media/beacon` takes a small JSON body from a player:

```json
{ "mediaId": "3f2b…", "event": "progress", "percent": 42, "session": "opaque-player-handle" }
```

`event` is `started`, `progress` (which requires `percent`, 0–100) or
`completed`. The beacon is admitted on exactly the two credentials the
media bytes themselves are reachable by — a resolved scope, or a valid
signed-URL token for **that** media id (`?token=…`), so a token minted for
one item cannot report plays against another.

Three properties are worth stating plainly:

- **It never returns anything but `204`.** Accepted, malformed, rate-limited,
  unauthenticated and forbidden are indistinguishable to the caller. A
  telemetry endpoint that can return an error to a player is a telemetry
  endpoint that gets reported as a broken video — and a differentiated
  response would be an oracle telling an unauthenticated prober which
  media ids exist in which scope. What the outcomes differ in is whether
  a ledger row appears.
- **The session id you send is never stored.** It is hashed together with
  the scope and the media id, and only that 16-character correlator
  reaches the ledger. The correlator is stable within one `(scope, media)`
  — which is all that counting unique sessions and completion rates needs
  — and is useless as a cross-item or cross-scope tracking key. A player
  that (wrongly) puts a user identifier in the field does not thereby put
  one in your usage ledger.
- **It is rate-limited per partition**, over the same partition key the
  platform limiter derives (`token:` / `team:` / `user:` / `ip:`), at
  300/minute. A beacon over the limit is *dropped*, never rejected: the
  losing branch costs telemetry fidelity, not playback.

Accepted beacons land as `media.playback.events` ledger rows (one per
event, `Metadata` carrying `mediaId`, `event`, `session`, and `percent` on
progress) and as `toolup.media.playback.events` counter increments tagged
`scope` + `event`.

### Reading the numbers back

There is **no new API, no new store and no dashboard**. The rows are
ordinary usage records, so a deployment reads them through the read path
its usage dashboard already uses — `IUsageQueryApi.Query` — and folds them
with a pure function the companion ships:

```fsharp skip=fragment
let! rows = usageQueryApi.Query(None, Some { From = from; To = until })

let rollups = PlaybackTelemetry.PlaybackRollup.ofUsageRecords rows
// each: MediaId, ScopeId, Day, Plays, UniqueSessions, Completions,
//       CompletionRate, OriginEgressBytes
```

`ofUsageRecords` ignores records of other kinds, so handing it a whole
scope's ledger is the expected call, and its output is ordered
`(Day, MediaId, ScopeId)` so two readers of the same rows render the same
table.

### Nothing is composed until you compose it

A deployment with neither `MetricsEndpoint` nor `UsageMetering` enabled —
the SDK defaults — pays two singleton service lookups per media response
and **no allocation at all** for telemetry: the resolved account is a
cached singleton and both the per-chunk count and the trailing flush are a
tag test (GP 13). The serve path is byte-for-byte what it was. Enabling
either one alone is enough to start receiving that half.

To receive both, compose them as usual:

```fsharp skip=fragment
{ ServerConfig.defaults with
    MediaLibrary = EnabledMediaLibrary
    UsageMetering = EnabledUsageMetering
    MetricsEndpoint = EnabledMetricsEndpoint }
```

The two metric series are declared into `ServerApp.MetricRegistrations` by
`MediaLibraryServerApp.run`, so a composed sink pre-allocates them and the
emissions flow rather than being dropped as unregistered.

## See also

- [`IMediaLibrary`](../../src/Media/MediaLibrary/Server/IMediaLibrary.fs) — the interface, plus the optional `IMediaRangeReader` capability and the `IUploadSessionStore` resumable-upload seam.
- [`473-playback-delivery-telemetry.md`](../migrations/473-playback-delivery-telemetry.md) — the beacon contract, the metric + ledger vocabulary, and the origin-vs-delivered caveat.
- [`469-resumable-chunked-uploads.md`](../migrations/469-resumable-chunked-uploads.md) — what the chunked upload surface means for a consumer.
- [`471-gated-hls.md`](../migrations/471-gated-hls.md) — AES-128 segments + the scope-gated key endpoint.
- [`455-iblobstorage-ranged-read.md`](../migrations/455-iblobstorage-ranged-read.md) — the storage-seam member range serving is built on.
- [`narrative-elements.md`](../platform/narrative-elements.md) — the `Video` / `Audio` blocks media serves into.
- [`IMediaLibraryContract`](../../src/ToolUp.Platform.Tests/Contracts/IMediaLibraryContract.fs) — the conformance pack any implementation can validate against.
