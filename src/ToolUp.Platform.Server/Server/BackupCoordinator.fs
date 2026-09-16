// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 445 — the default `IBackupCoordinator` over two `IBlobStorage`s:
/// the deployment's RAW live storage (beneath any Phase 22 encryption
/// decorator) and the backup target. See `IBackupCoordinator.fs` for the
/// design and the premises it rests on.
///
/// Backup-target layout (container `_backups`):
///
///   {snapshotId}/manifest.json
///   {snapshotId}/blobs/{container}/{blobName}
///
/// Live-storage staging layout (`SideBySide` restores, per container):
///
///   _restore/{restoreId}/{blobName}
///
/// `_restore/` is scratch: the snapshot walk skips it, `Promote` swaps it
/// into place, `DiscardStaged` erases it.
module ToolUp.Platform.BackupCoordinator

open System
open System.Text
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.Backup
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.EncryptionTypes
open ToolUp.Remoting.Json.SystemTextJson

/// The backup-target container every snapshot lives in.
[<Literal>]
let BackupsContainer = "_backups"

/// The live-storage root every `SideBySide` restore stages under.
[<Literal>]
let StagingRoot = "_restore/"

/// The staging prefix of one restore, within each of its containers.
let stagingPrefix (restoreId: string) : string = $"{StagingRoot}{restoreId}/"

/// The backup-target name of a snapshot's manifest.
let manifestBlobName (snapshotId: string) : string = $"{snapshotId}/manifest.json"

/// The backup-target name of one copied blob.
let backupBlobName (snapshotId: string) (container: string) (blobName: string) : string =
    $"{snapshotId}/blobs/{container}/{blobName}"

/// A sortable snapshot id: UTC timestamp, then a guid so two snapshots
/// in one millisecond still differ.
let newSnapshotId (now: DateTime) : string =
    let stamp = now.ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'")
    let guid = Guid.NewGuid().ToString "N"
    $"{stamp}-{guid}"

/// The Phase 22 key id off an `EncryptedBlobStorage` envelope, or
/// `None` when the bytes are not enveloped. Reads the self-describing
/// header only — `[Magic:4][KeyIdLen:1][KeyId:N]…` — and never touches
/// the ciphertext.
let tryEnvelopeKeyId (bytes: byte[]) : string option =
    let magic = EncryptionEnvelope.Magic

    let headerLength =
        EncryptionEnvelope.MagicLength + EncryptionEnvelope.KeyIdLengthBytes

    if bytes.Length < headerLength then
        None
    else
        let mutable magicMatches = true

        for i in 0 .. EncryptionEnvelope.MagicLength - 1 do
            if bytes[i] <> magic[i] then
                magicMatches <- false

        if not magicMatches then
            None
        else
            let keyIdLength = int bytes[EncryptionEnvelope.MagicLength]

            if keyIdLength = 0 || bytes.Length < headerLength + keyIdLength then
                None
            else
                Some(Encoding.UTF8.GetString(bytes, headerLength, keyIdLength))

/// The store a blob name belongs to — its first path segment (`events`,
/// `objects`, `entities`, `admin`, …), or `_root` for an un-nested name.
let storeOf (blobName: string) : string =
    match blobName.IndexOf '/' with
    | -1 -> "_root"
    | i -> blobName.Substring(0, i)

// ─── Manifest codec ─────────────────────────────────────────────────

/// `FableConverters` — the canonical STJ converter set for record / DU
/// shapes outside Remoting — so the manifest an operator reads off the
/// backup target has the same PascalCase, DU-friendly shape as the audit
/// rows beside it.
let private manifestJsonOptions = FableConverters.create ()

/// Serialise a manifest to its blob bytes.
let encodeManifest (manifest: BackupManifest) : byte[] =
    JsonSerializer.Serialize(manifest, manifestJsonOptions)
    |> Encoding.UTF8.GetBytes

/// Parse a manifest blob.
let decodeManifest (bytes: byte[]) : Result<BackupManifest, string> =
    try
        Ok(JsonSerializer.Deserialize<BackupManifest>(Encoding.UTF8.GetString bytes, manifestJsonOptions))
    with ex ->
        Error ex.Message

// ─── Prefixed view ──────────────────────────────────────────────────

/// An `IBlobStorage` view of another under a fixed name prefix: every
/// name is prefixed on the way in and stripped on the way out. A
/// `SideBySide` restore writes through it, and the restore drill
/// constructs the real stores (`PersistentEventStore`, …) over it, so a
/// staged copy is verified by the code that will read it once promoted
/// rather than by a re-implementation of that code's layout.
///
/// Erase is prefix-scoped by contract; the view narrows it further.
/// `ComposeFrom` and `DownloadRange` pass through with mapped names.
type PrefixedBlobStorage(inner: IBlobStorage, prefix: string) =
    let map (name: string) = prefix + name

    let unmap (name: string) =
        if name.StartsWith prefix then
            name.Substring prefix.Length
        else
            name

    /// The prefix every name is mapped under.
    member _.Prefix = prefix

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            inner.Upload(container, map blobName, content)

        member _.Download(container, blobName) = inner.Download(container, map blobName)
        member _.Delete(container, blobName) = inner.Delete(container, map blobName)

        member _.List(container, listPrefix) = async {
            let! names = inner.List(container, map listPrefix)
            return names |> List.filter (fun n -> n.StartsWith prefix) |> List.map unmap
        }

        member _.Exists(container, blobName) = inner.Exists(container, map blobName)

        member _.GetMetadata(container, blobName) =
            inner.GetMetadata(container, map blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, map blobName, offset, length)

        member _.CanComposeFrom = inner.CanComposeFrom

        member _.ComposeFrom(container, targetBlobName, sourceBlobNames) =
            inner.ComposeFrom(container, map targetBlobName, sourceBlobNames |> List.map map)

        member _.Erase(container, erasePrefix, policy, dryRun) =
            inner.Erase(container, map erasePrefix, policy, dryRun)

// ─── Coordinator ────────────────────────────────────────────────────

/// One blob read off the source during a walk.
type private WalkOutcome =
    | Copied of SnapshotEntry
    | Vanished of container: string * blobName: string
    | Failed of reason: string

/// The default coordinator.
///
///  * `source` — the RAW live storage. Compose passes the storage
///    beneath the encryption decorator so snapshots copy ciphertext
///    as-is; a caller that passes the decorated storage gets plaintext
///    copies, which is a composition error, not a coordinator one.
///  * `target` — the backup target.
///  * `keyResolver` — the deployment's Phase 22 resolver, when one is
///    composed; restore preflight resolves every key id through it.
///  * `auditLog` — `BackupCompleted` / `BackupFailed` land here on the
///    reserved `_platform` scope. Drill outcomes are the verifier's.
///  * `clock` — injectable for tests; defaults to `DateTime.UtcNow`.
type BackupCoordinator
    (
        source: IBlobStorage,
        target: IBlobStorage,
        ?keyResolver: IBlobEncryptionKeyResolver,
        ?auditLog: IAuditLog,
        ?logger: ILogger,
        ?clock: unit -> DateTime
    ) =

    let now = defaultArg clock (fun () -> DateTime.UtcNow)

    let audit (event: AuditEvent) = async {
        match auditLog with
        | Some log ->
            try
                do! log.Record(BackupScope.PlatformContainer, event)
            with ex ->
                logger
                |> Option.iter (fun l -> l.Warn(sprintf "[Phase 445] audit emission failed: %s" ex.Message))
        | None -> ()
    }

    /// The event-store head — `PersistentEventStore`'s canonical blobs
    /// under `_platform/events/`, count + lexicographic max. Index refs
    /// (`_by-type/`, `_by-source/`) are excluded exactly as that store
    /// excludes them.
    let captureHead () = async {
        let! names = source.List(BackupScope.PlatformContainer, "events/")

        let canonical =
            names
            |> List.filter (fun n ->
                n.EndsWith ".json"
                && not (n.Contains "/_by-type/")
                && not (n.Contains "/_by-source/"))

        return {
            CanonicalEvents = canonical.Length
            LatestEventBlob = (if canonical.IsEmpty then None else Some(List.max canonical))
        }
    }

    let captureHeadIf (includesPlatform: bool) = async {
        if includesPlatform then
            let! head = captureHead ()
            return Some head
        else
            return None
    }

    let copyOne (snapshotId: string) (container: string) (blobName: string) = async {
        match! source.Download(container, blobName) with
        | Error reason ->
            let! stillThere = source.Exists(container, blobName)

            if stillThere then
                return Failed $"reading {container}/{blobName}: {reason}"
            else
                return Vanished(container, blobName)
        | Ok bytes ->
            match! target.Upload(BackupsContainer, backupBlobName snapshotId container blobName, bytes) with
            | Error reason -> return Failed $"writing backup copy of {container}/{blobName}: {reason}"
            | Ok _ ->
                return
                    Copied {
                        Container = container
                        BlobName = blobName
                        Sha256 = DeployRecords.digestBytes bytes
                        Size = int64 bytes.Length
                        KeyId = tryEnvelopeKeyId bytes
                    }
    }

    /// Walk one container: list, then copy each blob in listed order.
    let walkContainer (snapshotId: string) (container: string) = async {
        let! names = source.List(container, "")

        let names =
            names |> List.filter (fun n -> not (n.StartsWith StagingRoot)) |> List.sort

        let entries = ResizeArray<SnapshotEntry>()
        let fuzzy = ResizeArray<string>()
        let mutable failure: string option = None

        for name in names do
            if failure.IsNone then
                match! copyOne snapshotId container name with
                | Copied entry -> entries.Add entry
                | Vanished(c, b) -> fuzzy.Add $"{c}/{b} was listed but gone by the time it was read"
                | Failed reason -> failure <- Some reason

        return
            match failure with
            | Some reason -> Error reason
            | None -> Ok(List.ofSeq entries, List.ofSeq fuzzy)
    }

    let readManifest (snapshotId: string) = async {
        let name = manifestBlobName snapshotId
        let! exists = target.Exists(BackupsContainer, name)

        if not exists then
            return Error(SnapshotNotFound snapshotId)
        else
            match! target.Download(BackupsContainer, name) with
            | Error reason -> return Error(BackupError.StorageFailure $"reading manifest for {snapshotId}: {reason}")
            | Ok bytes ->
                return
                    decodeManifest bytes
                    |> Result.mapError (fun reason -> ManifestUnreadable(snapshotId, reason))
    }

    /// The manifest narrowed to the requested scope.
    let narrow (scope: BackupScope) (manifest: BackupManifest) : BackupManifest =
        let wanted = BackupScope.containers scope |> Set.ofList
        let containers = manifest.Containers |> List.filter wanted.Contains
        let entries = manifest.Entries |> List.filter (fun e -> wanted.Contains e.Container)

        {
            manifest with
                Containers = containers
                Entries = entries
                StoreCounts =
                    manifest.StoreCounts
                    |> List.filter (fun (k, _) -> containers |> List.exists (fun c -> k.StartsWith(c + "/")))
                KeyIds = entries |> List.choose _.KeyId |> List.distinct
        }

    /// Hash every backup copy against the manifest; resolve every key.
    let preflightChecks (snapshotId: string) (manifest: BackupManifest) = async {
        let failures = ResizeArray<RestorePreflightFailure>()

        for entry in manifest.Entries do
            match! target.Download(BackupsContainer, backupBlobName snapshotId entry.Container entry.BlobName) with
            | Error _ -> failures.Add(MissingBlob(entry.Container, entry.BlobName))
            | Ok bytes ->
                let actual = DeployRecords.digestBytes bytes

                if actual <> entry.Sha256 then
                    failures.Add(HashMismatch(entry.Container, entry.BlobName, entry.Sha256, actual))

        match manifest.KeyIds, keyResolver with
        | [], _ -> ()
        | keyIds, None -> failures.Add(NoKeyResolver keyIds)
        | keyIds, Some resolver ->
            for keyId in keyIds do
                match! resolver.ResolveKeyById keyId with
                | Ok _ -> ()
                | Error(KeyResolutionError.KeyDestroyed id) -> failures.Add(RestorePreflightFailure.KeyDestroyed id)
                | Error(KeyResolutionError.KeyNotFound id) -> failures.Add(RestorePreflightFailure.KeyNotFound id)
                | Error(KeyResolutionError.StorageFailure reason) -> failures.Add(KeyResolutionFailed(keyId, reason))

        return List.ofSeq failures
    }

    let preflight (snapshotId: string) (scope: BackupScope) = async {
        match! readManifest snapshotId with
        | Error e -> return Error(ManifestError e)
        | Ok manifest ->
            let narrowed = narrow scope manifest

            match! preflightChecks snapshotId narrowed with
            | [] -> return Ok narrowed
            | failures -> return Error(PreflightRefused failures)
    }

    /// Copy every staged blob under `restoreId` to its live name, then
    /// delete the staged copy.
    let promote (restoreId: string) (scope: BackupScope) = async {
        let prefix = stagingPrefix restoreId
        let mutable moved = 0
        let mutable failure: RestoreError option = None

        for container in BackupScope.containers scope do
            if failure.IsNone then
                let! staged = source.List(container, prefix)

                for stagedName in staged |> List.filter (fun n -> n.StartsWith prefix) do
                    if failure.IsNone then
                        let liveName = stagedName.Substring prefix.Length

                        match! source.Download(container, stagedName) with
                        | Error reason -> failure <- Some(StagedReadFailed(container, stagedName, reason))
                        | Ok bytes ->
                            match! source.Upload(container, liveName, bytes) with
                            | Error reason -> failure <- Some(WriteFailed(container, liveName, reason))
                            | Ok _ ->
                                match! source.Delete(container, stagedName) with
                                | Error reason -> failure <- Some(WriteFailed(container, stagedName, reason))
                                | Ok() -> moved <- moved + 1

        return
            match failure with
            | Some e -> Error e
            | None -> Ok moved
    }

    let discard (restoreId: string) (scope: BackupScope) = async {
        let prefix = stagingPrefix restoreId
        let mutable erased = 0
        let mutable failure: RestoreError option = None

        for container in BackupScope.containers scope do
            if failure.IsNone then
                let! staged = source.List(container, prefix)

                for stagedName in staged |> List.filter (fun n -> n.StartsWith prefix) do
                    if failure.IsNone then
                        match! source.Delete(container, stagedName) with
                        | Error reason -> failure <- Some(WriteFailed(container, stagedName, reason))
                        | Ok() -> erased <- erased + 1

        return
            match failure with
            | Some e -> Error e
            | None -> Ok erased
    }

    interface IBackupCoordinator with

        member _.Snapshot(scope) = async {
            let startedAt = now ()
            let containers = BackupScope.containers scope
            let snapshotId = newSnapshotId startedAt
            let includesPlatform = containers |> List.contains BackupScope.PlatformContainer

            let fail (reason: string) = async {
                do!
                    audit (
                        BackupFailed {
                            Containers = containers
                            Reason = reason
                            StartedAt = startedAt
                            FailedAt = now ()
                        }
                    )

                return Error(BackupError.StorageFailure reason)
            }

            try
                let! headBefore = captureHeadIf includesPlatform

                let allEntries = ResizeArray<SnapshotEntry>()
                let fuzzyReasons = ResizeArray<string>()
                let mutable failure: string option = None

                for container in containers do
                    if failure.IsNone then
                        match! walkContainer snapshotId container with
                        | Error reason -> failure <- Some reason
                        | Ok(entries, fuzzy) ->
                            allEntries.AddRange entries
                            fuzzyReasons.AddRange fuzzy

                match failure with
                | Some reason -> return! fail reason
                | None ->
                    let! headAfter = captureHeadIf includesPlatform

                    match headBefore, headAfter with
                    | Some before, Some after when before <> after ->
                        let describe (h: EventStoreHead) =
                            let latest = defaultArg h.LatestEventBlob "<none>"
                            $"{h.CanonicalEvents} event(s), latest {latest}"

                        fuzzyReasons.Add
                            $"event-store head moved during the walk: before {describe before}; after {describe after}"
                    | _ -> ()

                    let entries = List.ofSeq allEntries

                    let manifest = {
                        FormatVersion = BackupManifest.CurrentFormatVersion
                        SnapshotId = snapshotId
                        Containers = containers
                        StartedAt = startedAt
                        CompletedAt = now ()
                        Entries = entries
                        StoreCounts =
                            entries
                            |> List.countBy (fun e -> $"{e.Container}/{storeOf e.BlobName}")
                            |> List.sortBy fst
                        HeadBefore = headBefore
                        HeadAfter = headAfter
                        Consistency =
                            (if fuzzyReasons.Count = 0 then
                                 Consistent
                             else
                                 Fuzzy(List.ofSeq fuzzyReasons))
                        KeyIds = entries |> List.choose _.KeyId |> List.distinct |> List.sort
                    }

                    match! target.Upload(BackupsContainer, manifestBlobName snapshotId, encodeManifest manifest) with
                    | Error reason -> return! fail $"writing manifest: {reason}"
                    | Ok _ ->
                        do!
                            audit (
                                BackupCompleted {
                                    SnapshotId = snapshotId
                                    Containers = containers
                                    BlobCount = entries.Length
                                    TotalBytes = BackupManifest.totalBytes manifest
                                    Consistency = BackupManifest.consistencyName manifest
                                    KeyIds = manifest.KeyIds
                                    StartedAt = startedAt
                                    CompletedAt = manifest.CompletedAt
                                }
                            )

                        return Ok manifest
            with ex ->
                return! fail $"unexpected failure: {ex.Message}"
        }

        member _.ListSnapshots() = async {
            let! names = target.List(BackupsContainer, "")

            return
                names
                |> List.choose (fun n ->
                    if n.EndsWith "/manifest.json" then
                        Some(n.Substring(0, n.Length - "/manifest.json".Length))
                    else
                        None)
                |> List.filter (fun id -> not (id.Contains '/'))
                |> List.distinct
                |> List.sort
        }

        member _.ReadManifest(snapshotId) = readManifest snapshotId

        member _.Preflight(snapshotId, scope) = preflight snapshotId scope

        member _.Restore(snapshotId, scope, options) = async {
            match! readManifest snapshotId with
            | Error e -> return Error(ManifestError e)
            | Ok manifest ->
                let narrowed = narrow scope manifest
                let! failures = preflightChecks snapshotId narrowed

                let forceable, blocking =
                    failures |> List.partition RestorePreflightFailure.isForceable

                if not blocking.IsEmpty || (not forceable.IsEmpty && not options.Force) then
                    return Error(PreflightRefused failures)
                else
                    let missing =
                        forceable
                        |> List.choose (function
                            | MissingBlob(c, b) -> Some(c, b)
                            | _ -> None)
                        |> Set.ofList

                    let restoreId = newSnapshotId (now ())

                    let destination: IBlobStorage =
                        match options.Placement with
                        | SideBySide -> PrefixedBlobStorage(source, stagingPrefix restoreId) :> IBlobStorage
                        | InPlace -> source

                    let mutable written = 0
                    let mutable failure: RestoreError option = None

                    for entry in narrowed.Entries do
                        if failure.IsNone && not (missing.Contains(entry.Container, entry.BlobName)) then
                            match!
                                target.Download(
                                    BackupsContainer,
                                    backupBlobName snapshotId entry.Container entry.BlobName
                                )
                            with
                            | Error reason -> failure <- Some(StagedReadFailed(entry.Container, entry.BlobName, reason))
                            | Ok bytes ->
                                // Re-verify at write: preflight hashed a moment ago,
                                // and a backup target that changed in between must
                                // not get its new bytes written on the old verdict.
                                let actual = DeployRecords.digestBytes bytes

                                if actual <> entry.Sha256 then
                                    failure <-
                                        Some(
                                            PreflightRefused [
                                                HashMismatch(entry.Container, entry.BlobName, entry.Sha256, actual)
                                            ]
                                        )
                                else
                                    match! destination.Upload(entry.Container, entry.BlobName, bytes) with
                                    | Error reason ->
                                        failure <- Some(WriteFailed(entry.Container, entry.BlobName, reason))
                                    | Ok _ -> written <- written + 1

                    match failure with
                    | Some e -> return Error e
                    | None ->
                        return
                            Ok {
                                SnapshotId = snapshotId
                                RestoreId = restoreId
                                Placement = options.Placement
                                Containers = narrowed.Containers
                                HashesVerified = narrowed.Entries.Length - missing.Count
                                BlobsWritten = written
                                SkippedMissing = missing.Count
                                StagingPrefix =
                                    (match options.Placement with
                                     | SideBySide -> Some(stagingPrefix restoreId)
                                     | InPlace -> None)
                            }
        }

        member _.Promote(restoreId, scope) = promote restoreId scope

        member _.DiscardStaged(restoreId, scope) = discard restoreId scope

// ─── Compose-time validator ─────────────────────────────────────────

/// Phase 445 — the preflight beside `ComposeStores.registerBackupCoordinator`.
///
///  * **Error** — `Backup = BackupEnabled` with no `IBackupTarget`
///    registered. Without it the coordinator fails by name at first
///    resolution, which is a request-path (or first-cron) surprise
///    rather than a preflight one.
///  * **Warning** — a `SnapshotCron` / `DrillCron` declared under
///    `JobScheduler = NoJobScheduler`. The job can never fire; the
///    deployment believes it is drilling and is not.
///  * **Ok** — `NoBackup`, or enabled with a target and a scheduler (or
///    no cron at all — on-demand only is a legitimate shape).
///
/// Probes the service collection the way `DataObjectOrphanSweepConfiguredValidator`
/// does: the instance shape `BackupTarget.ofStorage` registers is
/// recognised; a factory registration is not introspectable and is
/// treated as present, since a consumer that went to the trouble of a
/// factory has registered SOMETHING under the type.
type BackupTargetConfiguredValidator
    (config: ServerConfig, services: Microsoft.Extensions.DependencyInjection.IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout ConfigValidation.IConfigValidator.defaultTimeout

    let targetRegistered () =
        services
        |> Seq.exists (fun d ->
            not (isNull d.ServiceType)
            && d.ServiceType = typeof<IBackupTarget>
            && not d.IsKeyedService)

    interface ConfigValidation.IConfigValidator with
        member _.Name = "backup-target"
        member _.Timeout = timeout

        member _.Validate() = async {
            match config.Backup with
            | NoBackup -> return ConfigValidation.Ok
            | BackupEnabled settings ->
                if not (targetRegistered ()) then
                    return
                        ConfigValidation.Error(
                            "ServerConfig.Backup = BackupEnabled but no IBackupTarget is registered, so the coordinator has nowhere to write a snapshot and will fail by name the first time it is resolved. Register the destination storage — services.AddSingleton<IBackupTarget>(BackupTarget.ofStorage <any IBlobStorage: local directory, S3, Azure, GCS>) through ComposeExtensions.ServiceConfig — or set Backup = NoBackup. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                        )
                else
                    let cronDeclared = settings.SnapshotCron.IsSome || settings.DrillCron.IsSome

                    if cronDeclared && config.JobScheduler = NoJobScheduler then
                        return
                            ConfigValidation.Warning(
                                "ServerConfig.Backup declares a SnapshotCron and/or DrillCron but JobScheduler = NoJobScheduler — the scheduled snapshot / restore drill can never fire, so the deployment believes it is rehearsing recovery and is not. Set ServerConfig.JobScheduler = InProcessJobScheduler (or a distributed scheduler companion), or clear the crons and drive IBackupCoordinator.Snapshot / RestoreDrillVerifier.RunDrill on demand. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            )
                    else
                        return ConfigValidation.Ok
        }