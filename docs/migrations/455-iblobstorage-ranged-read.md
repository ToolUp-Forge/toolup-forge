# Phase 455 — `IBlobStorage` ranged read (`DownloadRange`)

**What changes.** `IBlobStorage` gains one method — a bounded ranged read — so
paged dataset reads and range-serving handlers can fetch slices of multi-GB
blobs instead of materialising whole objects:

```fsharp
abstract DownloadRange:
    container: string * blobName: string * offset: int64 * length: int ->
        Async<Result<byte[], string>>
```

**Semantics** (enforced by the `IBlobStorageContract` pack against every
implementation):

- `offset < 0` or `length <= 0` → `Error` (invalid arguments).
- Missing blob → `Error` (parity with `Download`).
- `offset >= size` → `Ok [||]` (past-EOF clamp; the cloud providers map their
  HTTP 416 responses here, distinguished from 404 not-found).
- Otherwise the bytes `[offset, min(offset + length, size))` — the result may
  be **shorter than `length`** when the range runs off the end, and
  concatenating consecutive ranges byte-equals the full `Download`.

There is deliberately **no open-ended "offset to EOF" form** — combine
`GetMetadata` (`Size`) with a capped-chunk loop, so no implementation is ever
forced to materialise a whole object to satisfy a range.

**Encryption caveat.** The `EncryptedBlobStorage` decorator **refuses** ranged
reads with a documented `Error` — its envelope is whole-blob AES-GCM, so a
mid-blob ciphertext range is undecryptable. Encrypted content is read via
`Download`; a chunked envelope is a possible future phase.

## Diff to apply

**Breaking for implementors only.** Callers of `IBlobStorage` need no change.
The five in-tree implementations (local, Azure, S3, GCS, the decorators) and
every in-tree test double already implement the method. The only consumers
that must add code ship their **own** `IBlobStorage` implementation.

If your backing store has a native range primitive (HTTP `Range`, file seek),
implement it natively so bounded reads stay bounded. Otherwise, this
copy-paste fallback delegates to the shared download-then-slice default —
correct against the contract, but it materialises the whole blob, so treat it
as a stopgap:

```fsharp
member this.DownloadRange(container, blobName, offset, length) =
    ToolUp.Platform.BlobStorage.downloadRangeViaDownload
        (this :> ToolUp.Platform.BlobStorage.IBlobStorage)
        container
        blobName
        offset
        length
```

## Verification steps

- `dotnet build ToolUp.Forge.sln` — clean (any custom implementation missing
  the method fails to compile; that is the whole migration surface).
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj`
  — the `IBlobStorageContract` pack now includes a `DownloadRange` section
  (start / mid / overshoot-clamp / past-EOF / concatenation-equivalence /
  not-found parity / argument validation) bound against `LocalFileStorage`,
  the in-memory doubles, the resilience decorator, and (env-gated) Azure /
  S3 / GCS. Custom implementations validate against the same pack.

## Rollback

Revert the commit. The method is additive at every call site (no SDK code
depends on it yet — the first consumers are the Phase 448 dataset `ReadPage`
refinement and Phase 468 media-library range-serving, both unshipped), so
removal restores the prior surface; no persisted data shape changed.
