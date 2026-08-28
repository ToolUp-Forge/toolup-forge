# Phase 471 — gated HLS: AES-128 segments + scope-gated key delivery

**What changes.** `ToolUp.MediaLibrary` can encrypt an HLS rendition with AES-128
and deliver the key through a gated endpoint, so a gated video stays gated
wherever its segments physically land. Four additions, all opt-in:

1. `MediaLibraryOptions.EncryptHlsByDefault` (default `false`), plus a per-upload
   preference on `MediaUploadRequest` set via the new
   `MediaUploadRequest.createWithEncryption`.
2. A probe-style capability seam, `IMediaHlsEncryptingTranscoder`, beside
   `IMediaTranscoder`. `ToolUp.Media.FFmpeg`'s transcoder declares it.
3. `HlsKeyDelivery.MediaHlsKeyStore` — per-media 16-byte keys in `ISecretStore`
   under the owning scope container, keyed `media_hls_key:{mediaId}` — and the
   route `GET /api/media/hls-key/{mediaId}`. Both are registered automatically
   when `ServerConfig.MediaLibrary = EnabledMediaLibrary`; nothing to compose.
4. A manifest rewrite on the serve path: `#EXT-X-KEY` URIs become
   origin-absolute, carrying through any `token` on the request.

**`IMediaLibrary` and `IMediaTranscoder` are unchanged.** A custom implementation
of either keeps compiling byte-for-byte. Encryption is a capability a transcoder
either declares or does not, discovered by a type test.

**Nothing changes for an unencrypted deployment.** With `EncryptHlsByDefault`
left `false` and no per-upload preference stated, no key is minted, the FFmpeg
argument list is the pre-471 list token-for-token, the segments are stored in the
clear as before, and a manifest with no `#EXT-X-KEY` line is served back
byte-for-byte — the rewrite short-circuits and the ORIGINAL bytes are served
rather than a re-encoding of them. All four claims are pinned by tests.

## Diff to apply

**Usually nothing.** One case needs a one-line change:

**You construct a `MediaLibraryOptions` record literally** (rather than
`{ MediaLibraryOptions.defaults with … }`). The record gained a field, so a
positional literal no longer compiles:

```diff
 let options: MediaLibraryOptions = {
     MaxBytes = 4L * 1024L * 1024L * 1024L
     AcceptedMimeTypes = Set.ofList [ "video/mp4" ]
     SignedUrlDefaultTtl = TimeSpan.FromHours 1.0
     EmitAudit = true
     RangeChunkBytes = 1024 * 1024
     MaxChunkBytes = MediaLibraryOptions.DefaultMaxChunkBytes
     UploadSessionTtl = MediaLibraryOptions.DefaultUploadSessionTtl
+    EncryptHlsByDefault = false
 }
```

The `{ defaults with … }` form — which is what the companion docs have always
shown — needs no change.

`DefaultMediaLibrary`'s pre-471 seven-argument constructor is preserved as an
explicit secondary constructor, so a consumer that builds one by hand compiles
and behaves identically: with no key store, the encryption path is structurally
unreachable and every upload takes the plain transcode.

## Turning it on

```fsharp skip=fragment
app
|> MediaLibraryServerApp.withTranscoder (FFmpegMediaProvider.createTranscoder None)
|> MediaLibraryServerApp.withOptions
    { MediaLibraryOptions.defaults with EncryptHlsByDefault = true }
```

Two things to know before you do.

**It is fail-closed, and that is the point.** An upload that asks to be
encrypted and cannot be — no transcoder declaring
`IMediaHlsEncryptingTranscoder`, or no `ISecretStore`-backed key store composed —
fails its ingestion with `MediaIngestionStatus.Failed` rather than producing a
bare rendition. A silently-unencrypted gated video is the exact exposure the
encryption exists to prevent, and nothing anywhere would say so. The key is
minted only after both preconditions hold, so a refused upload leaves no orphan
secret.

**It applies to NEW renditions only.** Existing renditions are already on disk in
the clear; turning the flag on does not reach back and re-encrypt them. Re-upload
or re-derive the items that need it — which is also how key rotation works (see
below).

## Rotation and deletion

There is no rotate verb, deliberately. A key is bound to the ciphertext of the
segments produced with it: handing out a new key without re-encrypting makes the
rendition unplayable, and keeping both makes revocation meaningless. **To rotate,
re-transcode** — the new pass mints a fresh key and replaces the segments in the
same act. `IMediaLibrary.Delete` destroys the key alongside the item, so a
deleted video leaves no live secret behind it.

## The key endpoint's gate

`GET /api/media/hls-key/{mediaId}` admits on the same two credentials the media
bytes themselves are reachable by — a resolved scope, or a valid `SignedUrl`
token for **this** media id — so the key is never easier to obtain than the video
it decrypts. Responses are `Cache-Control: no-store`.

A present-but-bad token is `403` and never falls through to the scope gate. A
caller authenticated in a **different** scope gets `404`: the refusal is
structural rather than a check, because the key is filed under the owning
container and a foreign scope's lookup has no answer to give.

For signed playback, the `token` on the manifest request is carried onto the
rewritten key URI — the same token, same signing key, same TTL. There is no
second token species to mint or manage.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean. The options-record widening above is
  the entire compile-time surface; anything else that breaks is not this phase.
- `dotnet run --project Build.fsproj -- VerifyAll` — the media pack gains the
  gate matrix (pure), the manifest-rewrite cases (pure), the FFmpeg key-info and
  argument-list cases (pure — nothing shells `ffmpeg`), the encrypted-rendition
  cases (a real AES-128-CBC round trip, asserting the stored segment is not the
  plaintext and that the stored key recovers it exactly), and the endpoint driven
  through a real `HttpContext`.
- Behavioural check in a running deployment: upload a video with encryption on,
  fetch `/api/media/hls/{id}/index.m3u8` and confirm the `#EXT-X-KEY` URI is
  absolute and points at your origin; download a segment blob directly from
  storage and confirm it does not play; fetch the key endpoint from another
  team's session and confirm `404`.

## Rollback

Revert the commit. Every addition is additive at every call site except the
`MediaLibraryOptions` field, which is the one line above. No persisted data shape
changed — `MediaRecord`, `media/originals/`, `media/records/` and
`media/derived/` are untouched.

**One caveat if you had encryption ON.** Renditions produced while it was on are
ciphertext on disk, and after a rollback there is no key endpoint to open them:
those items need re-deriving. The keys themselves remain in `ISecretStore` under
`media_hls_key:{mediaId}` and are inert — delete them at leisure, or leave them
in place if you intend to roll forward again.
