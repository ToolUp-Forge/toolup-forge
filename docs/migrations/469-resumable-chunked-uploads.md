# Phase 469 — resumable chunked media uploads

**What changes.** `ToolUp.MediaLibrary` gains a resumable upload path beside the
existing single-shot one. Three additions, all additive:

1. A new seam, `IUploadSessionStore` — `BeginUpload` / `AppendChunk` /
   `CommitUpload` / `AbortUpload` — with a default `BlobUploadSessionStore` over
   the `IBlobStorage` the library already uses. Registered automatically when
   `ServerConfig.MediaLibrary = EnabledMediaLibrary`; nothing to compose.
2. Four methods on the Fable.Remoting `IMediaApi` contract, under the existing
   `/api/media/*` scoped route family.
3. Two fields on `MediaLibraryOptions` — `MaxChunkBytes` (the per-chunk body
   cap, default 8 MiB) and `UploadSessionTtl` (idle-session lifetime, default
   24 h).

**`IMediaLibrary` is unchanged.** A custom implementation keeps compiling
byte-for-byte; resumability is a composed service a deployment either has or has
not, not a member every implementation must answer. A CDN-direct library
brokering its provider's own multipart protocol has no session of ours to open.

**Committed items are indistinguishable from single-shot uploads.**
`CommitUpload` assembles the chunks and routes them through
`IMediaLibrary.Upload`, so the record has the same `ContentHash`, the same
`media/originals/{mediaId}` layout, the same `Queued → … → Ready` ingestion over
`INotificationChannel`, and the same derivation / transcode hooks.

## Diff to apply

**Nothing is required.** Existing code compiles and behaves identically — the
new endpoints are inert until a client calls them, and a deployment that never
opens a session runs no sweep, allocates nothing, and registers no hosted
service (GP 13).

Two cases need a one-line change:

**1. You construct a `MediaLibraryOptions` record literally** (rather than
`{ MediaLibraryOptions.defaults with … }`). The record gained two fields, so a
positional literal no longer compiles:

```diff
 let options: MediaLibraryOptions = {
     MaxBytes = 4L * 1024L * 1024L * 1024L
     AcceptedMimeTypes = Set.ofList [ "video/mp4" ]
     SignedUrlDefaultTtl = TimeSpan.FromHours 1.0
     EmitAudit = true
     RangeChunkBytes = 1024 * 1024
+    MaxChunkBytes = MediaLibraryOptions.DefaultMaxChunkBytes
+    UploadSessionTtl = MediaLibraryOptions.DefaultUploadSessionTtl
 }
```

The `{ defaults with … }` form — which is what the companion docs have always
shown — needs no change at all. A non-positive value for either new field falls
back to its default at read time rather than failing, so an options record
assembled from configuration cannot break uploads by omission.

**2. You implement `IMediaApi` yourself** (a test double, or a proxy). The
record gained four fields:

```diff
 {
     GetMedia = …
     ListMedia = …
     DeleteMedia = …
     GetSignedUrl = …
+    BeginUpload = fun (_, _, _, _) -> async { return Error SessionNotFound }
+    AppendChunk = fun (_, _, _) -> async { return Error SessionNotFound }
+    CommitUpload = fun _ -> async { return Error SessionNotFound }
+    AbortUpload = fun _ -> async { return Error SessionNotFound }
 }
```

## Using it

The client loop, the validation split, and the failure vocabulary are in
[`docs/companions/media-library.md`](../companions/media-library.md#uploading-large-media).
The one thing worth repeating here, because it is what makes the protocol safe
to retry: `AppendChunk` is **idempotent at an already-accepted offset**, and an
`OffsetMismatch` carries the expected cursor, so a client that lost a response
re-sends the same chunk and either lands it or is told where the server actually
is. Advance your local offset from `UploadProgress.ReceivedBytes` — the server's
number — never from a locally incremented counter.

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean. The two record widenings above are
  the entire compile-time surface; anything else that breaks is not this phase.
- `dotnet run --project Build.fsproj -- VerifyAll` — the `IMediaLibrary`
  contract pack gains `IUploadSessionStore contract`: resume-after-drop
  (asserted as content-hash EQUALITY against a single-shot upload of the same
  bytes), duplicate chunk, wrong offset, per-chunk cap, over-cap declaration,
  declared-size overrun, early commit, TTL expiry, cross-scope refusal, abort.
  Your own `IUploadSessionStore` implementation, if you write one, validates
  against the same pack.
- Behavioural check in a running deployment: upload a file in chunks, kill the
  client mid-way, restart it against the reported cursor, commit — the resulting
  `MediaRecord.ContentHash` must equal the SHA-256 of the whole file.

## Rollback

Revert the commit. Every addition is additive at every call site: the seam is a
new type, the four `IMediaApi` methods are new fields, and the two options
fields have defaults. No persisted data shape changed — `MediaRecord`,
`media/originals/`, `media/records/` and `media/derived/` are untouched.
In-flight sessions under `media/uploads/` are the only new on-disk state; after
a rollback they are unreferenced blobs and can be deleted at leisure.
