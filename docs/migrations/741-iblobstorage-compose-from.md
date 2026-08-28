# Phase 741 — `IBlobStorage` gains `CanComposeFrom` + `ComposeFrom`

**Who this affects:** anyone with a **custom `IBlobStorage` implementation**. The interface gained
two members, so a custom store no longer compiles until it answers them. Nothing else changes:
existing members keep their signatures and their semantics, and every bundled store already
implements the new pair.

**If you only consume `IBlobStorage`** — you take it as a parameter, call `Upload` / `Download` /
`DownloadRange` — there is nothing to do.

## The two-line adoption (declining)

The member is a capability, and declining it is a first-class answer. If your store has no
bounded-memory multi-part commit primitive, say so:

```fsharp skip=fragment
open ToolUp.Platform.BlobStorage

type MyBlobStorage() =
    interface IBlobStorage with
        // …existing members unchanged…

        member _.CanComposeFrom = false
        member _.ComposeFrom(_, _, _) = composeNotSupported "MyBlobStorage"
```

`BlobStorage.composeNotSupported` returns the standard `ComposeRefusal.NotSupported` with a message
naming your implementation. Callers that can fall back do; callers that cannot report it. Nothing
else in your store changes, and no behaviour a deployment relies on changes either.

## What the member is for

Resumable media uploads accumulate their chunks as ordinary blobs. Before this phase, commit
downloaded every chunk, concatenated them into one `byte[]`, and uploaded that — so a 2 GiB commit
pinned ~2 GiB of heap. `ComposeFrom` lets the store concatenate parts it already holds, and the
commit peaks at one chunk instead.

```fsharp skip=fragment
abstract CanComposeFrom: bool

abstract ComposeFrom:
    container: string * targetBlobName: string * sourceBlobNames: string list ->
        Async<Result<int64, ComposeRefusal>>
```

`ComposeRefusal` has two cases and they are not interchangeable:

| Case | Meaning | What the caller does |
|---|---|---|
| `NotSupported of reason` | this store has no compose primitive | falls back (materialised assembly) |
| `ComposeFailed of message` | a compose was attempted and failed | surfaces the failure |

`CanComposeFrom` exists so a caller can choose its whole strategy **before** doing work. A caller
with an O(object) fallback would otherwise have to walk the parts, discover the refusal, and walk
them again. It must be cheap and side-effect-free.

## The contract, if you implement it

Held by `IBlobStorageContract` — bind your store to that pack and it is checked for you:

- `CanComposeFrom = false` ⇒ `ComposeFrom` returns `NotSupported`, always, writing nothing.
- An empty `sourceBlobNames` ⇒ `ComposeFailed`. A zero-part compose has no honest answer: writing an
  empty object and returning `Ok 0L` lets a caller whose part listing came back empty by accident
  commit an empty object over a real one.
- A missing source ⇒ `ComposeFailed`, with no completed target (abandon a multi-part commit rather
  than completing it short).
- Otherwise the target holds the parts concatenated in the given order, and `Ok n` is that total.
  `Download`ing the target byte-equals concatenating the parts' `Download`s.
- **Bounded memory is the whole point.** Hold one part, or one fixed coalescing buffer — never an
  amount that grows with the number of parts or the size of the result. A store that cannot honour
  that declares `CanComposeFrom = false`; it does not quietly buffer.

## The escape hatch, and why it is not the default

`BlobStorage.composeFromViaDownload store container target sources` is a correct
download-concatenate-upload implementation. It is **O(object)** — the cost the member exists to
avoid — so a store that delegates to it should still declare `CanComposeFrom = false`, and the media
library will keep taking the path it can reason about. Use it when you need the member to *work*
before you can make it *cheap*.

## How the bundled stores answer

| Store | `CanComposeFrom` | Primitive |
|---|---|---|
| `LocalFileStorage` | `true` | stream each source into a temp file, rename into place |
| `AzureBlobStorage` | `true` | `StageBlock` per part + `CommitBlockList` |
| `AwsS3Storage` | `true` | multipart upload, parts coalesced to S3's 5 MiB minimum |
| `GoogleCloudStorage` | `true` | `Objects.compose`, folded in batches of 32 — fully server-side |
| `EncryptedBlobStorage` | `false` | refuses: concatenated AES-GCM envelopes are not the envelope of the concatenated plaintext |
| `ResilientBlobStorage` | inner's | forwards |

## Rollback

Pin the previous package version. The member is additive to the interface and to the media library's
commit path; the materialised assembly it replaces is still present and is what a refusing store
gets, so downgrading loses the O(chunk) commit and nothing else. Committed items are
content-hash-identical across both paths, so nothing already stored needs migrating in either
direction.

## See also

- [`docs/companions/media-library.md`](../companions/media-library.md) — "Commit costs one chunk, not
  one object"
- [`docs/migrations/455-iblobstorage-ranged-read.md`](455-iblobstorage-ranged-read.md) — the previous
  member-widening on this interface, same shape
