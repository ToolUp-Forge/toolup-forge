// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 445 — the platform backup / restore coordinator seam.
///
/// Every persistent store the SDK ships — the event store, data objects,
/// entities and their indexes, and the ~45 `_platform/<feature>/`
/// prefixes each feature owns — rides `IBlobStorage`. A consistent,
/// verifiable snapshot of a deployment's state is therefore a copy of
/// its blob containers plus a manifest that lets the copy be checked:
/// per-blob SHA-256, counts per store, wall-clock bounds, a consistency
/// marker, and the encryption key ids the copy depends on.
///
/// **Enumerated, not hand-listed (GP 9) — and why there is no registry
/// to enumerate from.** The phase shard asked for the store prefixes to
/// be "enumerated from registrations". No such registration exists:
/// every store hardcodes its own prefix literal, and introducing a
/// registry would touch each of them for the benefit of one consumer.
/// The coordinator instead walks each container's WHOLE key space
/// (`List(container, "")`) and derives the per-store counts from the
/// first path segment of every key. Nothing is hand-listed, and nothing
/// can be missed: a store that does not exist yet is captured the day it
/// writes its first blob. That is a stronger completeness guarantee than
/// any registry — a registry can be forgotten; a container cannot.
///
/// **The consistency marker.** `IEventStore` exposes no head position,
/// so the marker is derived from `PersistentEventStore`'s layout: its
/// canonical blob names under `_platform/events/{scope}/` are
/// timestamp-prefixed and lexicographically sortable, so the (count,
/// greatest name) pair over that prefix IS the store's head. It is
/// captured before and after the walk; a moved head marks the snapshot
/// `Fuzzy` rather than pretending atomicity. So does a blob that was
/// listed and then gone by the time it was read.
///
/// **Encryption interplay (Phase 22).** The coordinator is composed
/// over the RAW blob storage beneath any `EncryptedBlobStorage`
/// decorator, so it copies ciphertext as-is. The envelope is
/// self-describing (`TOBL` magic + key id), so the manifest records
/// which key ids the snapshot depends on — never the key material — and
/// a restore after crypto-shred fails loudly at preflight, not silently
/// at read time.
///
/// **Restore never destroys the current state.** Every hash is checked
/// before the first write; a partial manifest is refused unless forced;
/// and the default placement is `SideBySide` — every blob lands under a
/// fresh `_restore/{restoreId}/` prefix in its own container, to be
/// `Promote`d into place (or `DiscardStaged`) as a separate act.
module ToolUp.Platform.Backup

open System
open ToolUp.Platform.BlobStorage

/// Which scope-derived containers a snapshot or restore covers (GP 4 —
/// per-team backup / restore is the offboarding / onboarding story).
type BackupScope =
    /// The reserved `_platform` container only — the event store, every
    /// `_platform/<feature>/` prefix, and nothing tenant-scoped.
    | Platform
    /// An explicit container set (`_platform`, `team-{id}`, `user-{id}`,
    /// `session-{id}`). Order is irrelevant; duplicates are collapsed.
    | Containers of containers: string list

/// Helpers over `BackupScope`.
module BackupScope =
    /// The reserved platform container's name.
    [<Literal>]
    let PlatformContainer = "_platform"

    /// The distinct container list a scope resolves to.
    let containers (scope: BackupScope) : string list =
        match scope with
        | Platform -> [ PlatformContainer ]
        | Containers cs -> cs |> List.distinct

    /// `_platform` plus the given extra containers — the shape
    /// `BackupSettings.Containers` composes.
    let platformPlus (extra: string list) : BackupScope = Containers(PlatformContainer :: extra)

/// One copied blob, as the manifest records it.
type SnapshotEntry = {
    /// The scope-derived container the blob lives in.
    Container: string
    /// The blob's name within its container (forward-slash delimited).
    BlobName: string
    /// Lowercase-hex SHA-256 of the copied bytes.
    Sha256: string
    /// Byte length of the copied bytes.
    Size: int64
    /// The Phase 22 key id read off the blob's envelope header when the
    /// copied bytes are `EncryptedBlobStorage` ciphertext; `None` for a
    /// plaintext blob. Never the key material.
    KeyId: string option
}

/// The event store's head, derived from `PersistentEventStore`'s
/// timestamp-sortable canonical blob names under `_platform/events/`.
type EventStoreHead = {
    /// Canonical event blobs under the prefix (index refs excluded).
    CanonicalEvents: int
    /// The lexicographically greatest canonical blob name — the newest
    /// event across every scope — or `None` when the store is empty.
    LatestEventBlob: string option
}

/// Whether the snapshot is a point-in-time or a point-in-interval.
type SnapshotConsistency =
    /// The event-store head did not move and every listed blob was read.
    | Consistent
    /// Something moved during the walk; each reason says what.
    | Fuzzy of reasons: string list

/// The manifest blob written beside the copied blobs.
type BackupManifest = {
    /// Manifest shape version, for a later reader that must tell a
    /// pre-change manifest from a post-change one.
    FormatVersion: int
    /// Sortable snapshot identifier — `{yyyyMMddTHHmmssfffZ}-{guid}`.
    SnapshotId: string
    /// The containers walked.
    Containers: string list
    /// When the walk started.
    StartedAt: DateTime
    /// When the manifest was written.
    CompletedAt: DateTime
    /// Every copied blob.
    Entries: SnapshotEntry list
    /// Entry count per store, keyed `{container}/{first path segment}`
    /// — e.g. `_platform/events`, `team-x/objects`, `team-x/entities`.
    /// Derived from the keys, never hand-listed (GP 9).
    StoreCounts: (string * int) list
    /// The event-store head before the walk; `None` when the scope
    /// excludes `_platform`.
    HeadBefore: EventStoreHead option
    /// The event-store head after the walk; `None` when the scope
    /// excludes `_platform`.
    HeadAfter: EventStoreHead option
    /// The consistency marker.
    Consistency: SnapshotConsistency
    /// Distinct Phase 22 key ids read off the envelope headers of the
    /// ciphertext copied. Key material is never captured.
    KeyIds: string list
}

/// Helpers over `BackupManifest`.
module BackupManifest =
    /// The manifest shape this build writes.
    [<Literal>]
    let CurrentFormatVersion = 1

    /// Bytes copied, summed over every entry.
    let totalBytes (manifest: BackupManifest) : int64 = manifest.Entries |> List.sumBy _.Size

    /// The consistency marker's wire spelling (`"Consistent"` / `"Fuzzy"`).
    let consistencyName (manifest: BackupManifest) : string =
        match manifest.Consistency with
        | Consistent -> "Consistent"
        | Fuzzy _ -> "Fuzzy"

/// Failures reading the backup target or its manifests.
type BackupError =
    /// No manifest exists for the given snapshot id.
    | SnapshotNotFound of snapshotId: string
    /// A manifest exists but could not be parsed.
    | ManifestUnreadable of snapshotId: string * reason: string
    /// The source or target storage failed; the message is diagnostic.
    | StorageFailure of reason: string

/// Helpers over `BackupError`.
module BackupError =
    /// Operator-readable rendering.
    let message (error: BackupError) : string =
        match error with
        | SnapshotNotFound id -> $"Snapshot not found: {id}"
        | ManifestUnreadable(id, reason) -> $"Manifest for snapshot {id} is unreadable: {reason}"
        | StorageFailure reason -> $"Storage failure: {reason}"

/// A reason restore preflight refused — nothing has been written when
/// any of these is reported.
type RestorePreflightFailure =
    /// The backup target's copy of a blob does not hash to the manifest's
    /// digest: tampered, truncated or bit-rotted.
    | HashMismatch of container: string * blobName: string * expected: string * actual: string
    /// A manifest entry has no blob in the backup target — a partial
    /// manifest. Refused unless `RestoreOptions.Force`.
    | MissingBlob of container: string * blobName: string
    /// The snapshot depends on an encryption key that has been
    /// crypto-shredded. The data is unrecoverable by construction — no
    /// force overrides this.
    | KeyDestroyed of keyId: string
    /// The snapshot depends on a key id the resolver does not know.
    | KeyNotFound of keyId: string
    /// Resolving a key failed for an infrastructure reason; retry.
    | KeyResolutionFailed of keyId: string * reason: string
    /// The snapshot carries enveloped ciphertext but this deployment has
    /// no `IBlobEncryptionKeyResolver` composed — every restored blob
    /// would be unreadable.
    | NoKeyResolver of keyIds: string list

/// Helpers over `RestorePreflightFailure`.
module RestorePreflightFailure =
    /// Operator-readable rendering.
    let message (failure: RestorePreflightFailure) : string =
        match failure with
        | HashMismatch(c, b, expected, actual) -> $"Hash mismatch on {c}/{b}: manifest {expected}, backup copy {actual}"
        | MissingBlob(c, b) -> $"Missing from backup target: {c}/{b}"
        | KeyDestroyed keyId ->
            $"Encryption key destroyed (crypto-shredded): {keyId} — the snapshot's ciphertext is unrecoverable"
        | KeyNotFound keyId -> $"Encryption key not found: {keyId}"
        | KeyResolutionFailed(keyId, reason) -> $"Encryption key {keyId} could not be resolved: {reason}"
        | NoKeyResolver keyIds ->
            let ids = String.Join(", ", keyIds)
            $"Snapshot carries ciphertext under key id(s) {ids} but no IBlobEncryptionKeyResolver is composed"

    /// Whether `RestoreOptions.Force` may override the failure. Only a
    /// partial manifest is forceable; a destroyed key never is.
    let isForceable (failure: RestorePreflightFailure) : bool =
        match failure with
        | MissingBlob _ -> true
        | HashMismatch _
        | KeyDestroyed _
        | KeyNotFound _
        | KeyResolutionFailed _
        | NoKeyResolver _ -> false

/// Failures of a restore, promote or discard.
type RestoreError =
    /// The manifest could not be read.
    | ManifestError of BackupError
    /// Preflight refused; every failure found is listed, so an operator
    /// sees the whole picture rather than the first problem.
    | PreflightRefused of failures: RestorePreflightFailure list
    /// A write to the live storage failed after preflight passed. With
    /// `SideBySide` placement nothing live was touched.
    | WriteFailed of container: string * blobName: string * reason: string
    /// Reading a staged blob back failed during promote.
    | StagedReadFailed of container: string * blobName: string * reason: string

/// Helpers over `RestoreError`.
module RestoreError =
    /// Operator-readable rendering.
    let message (error: RestoreError) : string =
        match error with
        | ManifestError e -> BackupError.message e
        | PreflightRefused failures ->
            let lines = failures |> List.map RestorePreflightFailure.message
            "Restore preflight refused: " + String.Join("; ", lines)
        | WriteFailed(c, b, reason) -> $"Write failed on {c}/{b}: {reason}"
        | StagedReadFailed(c, b, reason) -> $"Staged read failed on {c}/{b}: {reason}"

/// Where a restore writes.
type RestorePlacement =
    /// Default. Every blob lands under `_restore/{restoreId}/` in its
    /// own container; the live keys are untouched until `Promote`.
    | SideBySide
    /// Overwrite the live keys directly. Only after a rehearsal — the
    /// caller opts in.
    | InPlace

/// How a restore behaves.
type RestoreOptions = {
    /// Where the restored blobs land.
    Placement: RestorePlacement
    /// Proceed past a partial manifest (missing blobs are skipped and
    /// counted). Never overrides a hash mismatch or a destroyed key.
    Force: bool
}

/// Helpers over `RestoreOptions`.
module RestoreOptions =
    /// `SideBySide`, not forced.
    let defaults = {
        Placement = SideBySide
        Force = false
    }

/// What a restore did.
type RestoreReport = {
    /// The snapshot restored.
    SnapshotId: string
    /// This restore's identifier — the staging prefix's discriminator
    /// under `SideBySide`.
    RestoreId: string
    /// Where the blobs landed.
    Placement: RestorePlacement
    /// The containers restored (the manifest's, intersected with the
    /// requested scope).
    Containers: string list
    /// Blobs whose SHA-256 matched before the first write.
    HashesVerified: int
    /// Blobs written.
    BlobsWritten: int
    /// Manifest entries skipped because their blob was missing from the
    /// backup target — non-zero only under `Force`.
    SkippedMissing: int
    /// The staging prefix every restored blob sits under (`SideBySide`),
    /// or `None` (`InPlace`).
    StagingPrefix: string option
}

/// The destination `IBlobStorage` for snapshots — any companion (local
/// directory, S3, Azure, GCS — GP 3). Registered in DI by the deployment
/// under `BackupMode.BackupEnabled`; the compose validator refuses
/// startup when it is absent.
type IBackupTarget =
    /// The storage snapshots are written to and restores are read from.
    abstract Storage: IBlobStorage

/// Constructors for `IBackupTarget`.
module BackupTarget =
    /// Wrap a storage as a backup target.
    let ofStorage (storage: IBlobStorage) : IBackupTarget =
        { new IBackupTarget with
            member _.Storage = storage
        }

/// The coordinator. One instance per deployment, composed over the raw
/// live storage and the backup target. Portability audit (GP 12):
/// identity by value (snapshot / restore ids are strings), async at
/// every boundary, failure as data, no state between calls beyond what
/// the two storages hold.
type IBackupCoordinator =
    /// Walk the scope's containers, copy every blob to the backup target
    /// and write the manifest. Emits `BackupCompleted` / `BackupFailed`
    /// when an audit log is composed.
    abstract Snapshot: scope: BackupScope -> Async<Result<BackupManifest, BackupError>>

    /// Every snapshot id on the backup target, ascending — the last is
    /// the latest.
    abstract ListSnapshots: unit -> Async<string list>

    /// Read one manifest.
    abstract ReadManifest: snapshotId: string -> Async<Result<BackupManifest, BackupError>>

    /// The integrity check a restore runs before its first write, on its
    /// own: every hash over the backup copies, every key id through the
    /// resolver. Returns the manifest narrowed to the requested scope on
    /// success. Writes nothing.
    abstract Preflight: snapshotId: string * scope: BackupScope -> Async<Result<BackupManifest, RestoreError>>

    /// Preflight, then write. `SideBySide` (the default) stages under a
    /// fresh prefix and touches nothing live; `InPlace` overwrites.
    abstract Restore:
        snapshotId: string * scope: BackupScope * options: RestoreOptions -> Async<Result<RestoreReport, RestoreError>>

    /// Swap a `SideBySide` restore into place: copy every staged blob to
    /// its live name and delete the staged copy. Returns the count moved.
    abstract Promote: restoreId: string * scope: BackupScope -> Async<Result<int, RestoreError>>

    /// Erase a `SideBySide` restore's staging prefix without promoting
    /// it. Returns the count erased.
    abstract DiscardStaged: restoreId: string * scope: BackupScope -> Async<Result<int, RestoreError>>