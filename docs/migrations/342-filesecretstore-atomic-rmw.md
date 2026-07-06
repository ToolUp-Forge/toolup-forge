# `FileSecretStore` — atomic read-modify-write (behaviour-preserving)

## What changes

`FileSecretStore.SetSecret` and `DeleteSecret` previously ran their
`loadFile` → `Map.add`/`Map.remove` → `writeFile` sequence **outside** any
lock — only the in-memory cache eviction was serialised. Two concurrent writes
to the *same scope file* (e.g. two OAuth callbacks for different data sources in
one team container, or the token refresher persisting an access token + expiry
while a callback persists a refresh token) each read the old file and each wrote
back their own superset. Last-writer-wins silently dropped the other key — and a
dropped just-rotated refresh token strands the connection.

Two changes close this:

1. **The whole read-modify-write is now serialised under the store's existing
   `cacheLock`**, together with the cache eviction. Concurrent writes to one
   scope file are ordered; none can lose another's key. Reads (`loadForScope`)
   take the same lock, so an in-flight read sees either the pre-write file or
   blocks until the write completes.

2. **Writes are crash-atomic** — `writeFile` writes to a same-directory temp
   file, hardens its permissions, then atomically renames it over the target
   (`MoveFileEx`/`MOVEFILE_REPLACE_EXISTING` on Windows, `rename(2)` on Unix). A
   crash mid-write can no longer truncate the live secrets file, and a
   concurrent reader never observes a torn file.

## Impact

**None for consumers.** This is wholly internal to the SDK's default
`ISecretStore` implementation. The public `ISecretStore` surface is unchanged;
no member signatures move. A single-writer deployment is byte-for-byte
unchanged (GP 11) — the same JSON is written, at the same path, with the same
permissions; the only observable difference is that concurrent writers no longer
lose updates.

No action is required. Custom `ISecretStore` implementations are unaffected (the
change is inside `FileSecretStore`, not the interface).

## Verification

`src/ToolUp.Platform.Tests/InProcess/FileSecretStoreAtomicityTests.fs` drives
genuine concurrency at one scope file:

- N concurrent `SetSecret` calls with distinct keys all survive (no lost update).
- A `SetSecret` racing a `DeleteSecret` on different keys leaves both results
  consistent.
- Concurrent same-key writes converge to one written value (never a dropped key).
- Single-writer sequential behaviour is unchanged and no temp file is left behind.

## Rollback

Revert the `FileSecretStore.fs` change. There is no persisted-format or
API change to unwind — the on-disk secrets file format is identical.
