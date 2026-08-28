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
| `POST/GET /api/media/*` | Fable.Remoting `IMediaApi` — `GetMedia` / `ListMedia` / `DeleteMedia` / `GetSignedUrl`. |

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
```

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

## See also

- [`IMediaLibrary`](../../src/Media/MediaLibrary/Server/IMediaLibrary.fs) — the interface, plus the optional `IMediaRangeReader` capability.
- [`455-iblobstorage-ranged-read.md`](../migrations/455-iblobstorage-ranged-read.md) — the storage-seam member range serving is built on.
- [`narrative-elements.md`](../platform/narrative-elements.md) — the `Video` / `Audio` blocks media serves into.
- [`IMediaLibraryContract`](../../src/ToolUp.Platform.Tests/Contracts/IMediaLibraryContract.fs) — the conformance pack any implementation can validate against.
