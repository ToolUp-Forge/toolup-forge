// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 445 — the backup / restore coordinator and the restore drill.
///
/// Every fixture is hermetic (`InMemoryBlobStorage` for the live store
/// and the backup target). The stores whose layouts the snapshot must
/// carry — `PersistentEventStore`, `DataObjectStore`, `BlobEntityStore`
/// — are written through THEIR OWN code, never by hand, so the drill's
/// invariants are exercised over the real on-disk shapes and a layout
/// change in any of them reddens here rather than in production.
module ToolUp.Platform.Tests.InProcess.BackupCoordinatorTests

open System
open System.Text
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Backup
open ToolUp.Platform.BackupCoordinator
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.EntityStore
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.RestoreDrill
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private run (work: Async<'T>) = Async.RunSynchronously work

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

// ─── Doubles ─────────────────────────────────────────────────────────

/// Records every `(scopeId, event)` handed to it.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Recorded = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// One key, destroyable at will — the crypto-shred half of Phase 22 in
/// the smallest shape that exercises `ResolveKeyById`.
type private FakeKeyResolver(key: EncryptionKey) =
    let mutable destroyed = false
    member _.Destroy() = destroyed <- true

    interface IBlobEncryptionKeyResolver with
        member _.ResolveKey(_scope) = async { return key }

        member _.ResolveKeyById(keyId) = async {
            if keyId <> key.KeyId then
                return Error(KeyResolutionError.KeyNotFound keyId)
            elif destroyed then
                return Error(KeyResolutionError.KeyDestroyed keyId)
            else
                return Ok key
        }

/// A live store that gains an event mid-walk: the first `Download` the
/// walk makes writes a fresh canonical event blob under
/// `_platform/events/`, so the head captured after the walk differs from
/// the one captured before it.
type private WriteDuringWalkStorage(inner: IBlobStorage) =
    let mutable fired = false

    interface IBlobStorage with
        member _.Upload(c, b, content) = inner.Upload(c, b, content)

        member _.Download(c, b) = async {
            if not fired then
                fired <- true

                let! _ =
                    inner.Upload(
                        "_platform",
                        "events/team-a/9999-12-31T00-00-00-0000000Z-ffffffffffffffffffffffffffffffff.json",
                        utf8 "{}"
                    )

                ()

            return! inner.Download(c, b)
        }

        member _.Delete(c, b) = inner.Delete(c, b)
        member _.List(c, p) = inner.List(c, p)
        member _.Exists(c, b) = inner.Exists(c, b)
        member _.GetMetadata(c, b) = inner.GetMetadata(c, b)
        member _.DownloadRange(c, b, o, l) = inner.DownloadRange(c, b, o, l)
        member _.CanComposeFrom = inner.CanComposeFrom
        member _.ComposeFrom(c, t, s) = inner.ComposeFrom(c, t, s)
        member _.Erase(c, p, policy, dry) = inner.Erase(c, p, policy, dry)

// ─── Fixture ─────────────────────────────────────────────────────────

[<Literal>]
let private Team = "team-a"

let private containers = [ "_platform"; Team ]

/// Populate a live store through the real stores: two events for the
/// team scope (canonical blob + both index refs each), one data object,
/// one entity (versioned object + two index refs), and two plain blobs.
let private populate (live: IBlobStorage) =
    run (
        async {
            let events =
                PersistentEventStore.PersistentEventStore(live, EventRetentionPolicy.unlimited) :> IEventStore

            for i in 1..2 do
                do!
                    events.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime(2026, 9, 16, 10, 0, i, DateTimeKind.Utc)
                        ScopeId = Team
                        SourceModule = "widgets"
                        EventType = "WidgetMade"
                        Payload = sprintf "{\"n\":%d}" i
                    }

            let objects = DataObjectStore(live) :> IDataObjectStore

            let! saved =
                objects.Save(
                    Team,
                    "report-1",
                    utf8 "report bytes",
                    "report",
                    "alice",
                    Map.empty,
                    VersioningPolicy.Versioned
                )

            Expect.isOk saved "data object saved"

            let registry = EntityRegistry()
            registry.Register<IEntityStoreContract.TestEntity>(IEntityStoreContract.testEntityRegistration)
            let entities = BlobEntityStore(objects, live, registry, None) :> IEntityStore

            let! savedEntity =
                entities.Save<IEntityStoreContract.TestEntity>(
                    Team,
                    EntityTypes.EntityPrincipal.ofPrincipal "tester",
                    {
                        Id = "e-1"
                        Type = "TestEntity"
                        Version = 0
                        Owner = "alice"
                        Status = "open"
                    }
                )

            Expect.isOk savedEntity "entity saved"

            let! _ = live.Upload("_platform", "admin/settings.json", utf8 "{\"theme\":\"dark\"}")
            let! _ = live.Upload(Team, "notes/hello.txt", utf8 "hello")
            ()
        }
    )

/// Every `(container, name, bytes)` across the fixture's containers.
let private dump (storage: IBlobStorage) =
    run (
        async {
            let acc = ResizeArray<string * string * byte[]>()

            for c in containers do
                let! names = storage.List(c, "")

                for n in names do
                    match! storage.Download(c, n) with
                    | Ok bytes -> acc.Add((c, n, bytes))
                    | Error e -> failwithf "dump: %s/%s unreadable: %s" c n e

            return acc |> Seq.sortBy (fun (c, n, _) -> c, n) |> List.ofSeq
        }
    )

let private wipe (storage: IBlobStorage) =
    run (
        async {
            for c in containers do
                let! names = storage.List(c, "")

                for n in names do
                    let! _ = storage.Delete(c, n)
                    ()
        }
    )

let private newFixture () =
    let live = InMemoryBlobStorage() :> IBlobStorage
    let target = InMemoryBlobStorage() :> IBlobStorage
    let audit = RecordingAuditLog()
    populate live

    let coordinator =
        BackupCoordinator(live, target, auditLog = audit) :> IBackupCoordinator

    live, target, audit, coordinator

let private snapshotOk (coordinator: IBackupCoordinator) =
    match run (coordinator.Snapshot(Containers containers)) with
    | Ok m -> m
    | Error e -> failwithf "snapshot failed: %s" (BackupError.message e)

let private backupNameOf (manifest: BackupManifest) (entry: SnapshotEntry) =
    backupBlobName manifest.SnapshotId entry.Container entry.BlobName

let private stagingKeys (storage: IBlobStorage) =
    dump storage |> List.filter (fun (_, n, _) -> n.StartsWith StagingRoot)

let private tests_snapshot =
    testList "snapshot" [
        test "walks every container whole, hashes every blob, and derives store counts from the keys (GP 9)" {
            let live, target, audit, coordinator = newFixture ()
            let before = dump live
            let manifest = snapshotOk coordinator

            Expect.equal manifest.Entries.Length before.Length "one manifest entry per live blob"
            Expect.equal manifest.Consistency Consistent "nothing moved during the walk"
            Expect.equal manifest.HeadBefore manifest.HeadAfter "event-store head captured twice, unchanged"

            Expect.equal
                (manifest.HeadBefore |> Option.map _.CanonicalEvents)
                (Some 2)
                "two canonical events under _platform/events/"

            Expect.isEmpty manifest.KeyIds "plaintext fixture depends on no key"

            for c, n, bytes in before do
                let entry =
                    manifest.Entries |> List.find (fun e -> e.Container = c && e.BlobName = n)

                Expect.equal entry.Sha256 (DeployRecords.digestBytes bytes) (sprintf "%s/%s digest" c n)
                Expect.equal entry.Size (int64 bytes.Length) (sprintf "%s/%s size" c n)

                match run (target.Download("_backups", backupNameOf manifest entry)) with
                | Ok copy -> Expect.equal copy bytes (sprintf "%s/%s copied byte-for-byte" c n)
                | Error e -> failtestf "backup copy of %s/%s missing: %s" c n e

            let storeKeys = manifest.StoreCounts |> List.map fst |> Set.ofList

            Expect.isTrue (storeKeys.Contains "_platform/events") "events store counted from its first path segment"
            Expect.isTrue (storeKeys.Contains "team-a/objects") "objects store counted"
            Expect.isTrue (storeKeys.Contains "team-a/entities") "entity index root counted"

            Expect.isTrue
                (storeKeys.Contains "_platform/admin")
                "an arbitrary _platform/<feature>/ prefix is counted without being listed anywhere"

            Expect.equal
                (manifest.StoreCounts |> List.sumBy snd)
                manifest.Entries.Length
                "store counts partition the entries"

            match run (coordinator.ListSnapshots()) with
            | [ id ] -> Expect.equal id manifest.SnapshotId "the manifest is listed"
            | other -> failtestf "expected one snapshot, got %A" other

            match audit.Recorded with
            | [ "_platform", BackupCompleted p ] ->
                Expect.equal p.SnapshotId manifest.SnapshotId "audit names the snapshot"
                Expect.equal p.BlobCount manifest.Entries.Length "audit carries the count"
                Expect.equal p.Consistency "Consistent" "audit carries the marker"
            | other -> failtestf "expected one BackupCompleted row, got %A" other
        }

        test "the manifest round-trips through its codec" {
            let _, _, _, coordinator = newFixture ()
            let manifest = snapshotOk coordinator

            let fuzzy = {
                manifest with
                    Consistency = Fuzzy [ "head moved"; "a blob vanished" ]
                    KeyIds = [ "k1"; "k2" ]
            }

            match decodeManifest (encodeManifest fuzzy) with
            | Ok decoded ->
                Expect.equal decoded.SnapshotId fuzzy.SnapshotId "id"
                Expect.equal decoded.Entries fuzzy.Entries "entries"
                Expect.equal decoded.StoreCounts fuzzy.StoreCounts "store counts (tuple list)"
                Expect.equal decoded.Consistency fuzzy.Consistency "the Fuzzy case and its reasons"
                Expect.equal decoded.KeyIds fuzzy.KeyIds "key ids"
                Expect.equal decoded.HeadBefore fuzzy.HeadBefore "head option"
                Expect.equal decoded.FormatVersion BackupManifest.CurrentFormatVersion "format version"
            | Error e -> failtestf "manifest did not round-trip: %s" e

            match run (coordinator.ReadManifest manifest.SnapshotId) with
            | Ok read -> Expect.equal read.Entries manifest.Entries "read back from the target"
            | Error e -> failtestf "ReadManifest: %s" (BackupError.message e)

            match run (coordinator.ReadManifest "nope") with
            | Error(SnapshotNotFound "nope") -> ()
            | other -> failtestf "expected SnapshotNotFound, got %A" other
        }

        test "an event written during the walk marks the snapshot Fuzzy, with the head moves named" {
            let live = InMemoryBlobStorage() :> IBlobStorage
            populate live
            let racy = WriteDuringWalkStorage(live) :> IBlobStorage

            let coordinator =
                BackupCoordinator(racy, InMemoryBlobStorage()) :> IBackupCoordinator

            let manifest = snapshotOk coordinator

            match manifest.Consistency with
            | Fuzzy reasons ->
                Expect.exists reasons (fun r -> r.Contains "event-store head moved") "reason names the head"
            | Consistent -> failtest "a write during the walk must not read as Consistent"

            Expect.equal (manifest.HeadBefore |> Option.map _.CanonicalEvents) (Some 2) "before: two events"
            Expect.equal (manifest.HeadAfter |> Option.map _.CanonicalEvents) (Some 3) "after: three events"
        }

        test "the walk skips _restore/ scratch, so a staged restore is never snapshotted back" {
            let live, _, _, coordinator = newFixture ()
            let first = snapshotOk coordinator

            match run (coordinator.Restore(first.SnapshotId, Containers containers, RestoreOptions.defaults)) with
            | Ok _ -> ()
            | Error e -> failtestf "restore: %s" (RestoreError.message e)

            Expect.isNonEmpty (stagingKeys live) "the staged copy exists in the live store"
            let second = snapshotOk coordinator
            Expect.equal second.Entries.Length first.Entries.Length "the second snapshot has the same entry count"

            Expect.isEmpty
                (second.Entries |> List.filter (fun e -> e.BlobName.StartsWith StagingRoot))
                "no staged key was captured"
        }

        test "a source read failure is BackupFailed, audited, and writes no manifest" {
            let live, target, audit, _ = newFixture ()

            let failing =
                { new IBlobStorage with
                    member _.Upload(c, b, x) = live.Upload(c, b, x)
                    member _.Download(_, _) = async { return Error "disk on fire" }
                    member _.Delete(c, b) = live.Delete(c, b)
                    member _.List(c, p) = live.List(c, p)
                    member _.Exists(c, b) = live.Exists(c, b)
                    member _.GetMetadata(c, b) = live.GetMetadata(c, b)
                    member _.DownloadRange(c, b, o, l) = live.DownloadRange(c, b, o, l)
                    member _.CanComposeFrom = false
                    member _.ComposeFrom(c, t, s) = live.ComposeFrom(c, t, s)
                    member _.Erase(c, p, policy, dry) = live.Erase(c, p, policy, dry)
                }

            let coordinator =
                BackupCoordinator(failing, target, auditLog = audit) :> IBackupCoordinator

            match run (coordinator.Snapshot Platform) with
            | Error(BackupError.StorageFailure reason) -> Expect.stringContains reason "disk on fire" "reason surfaces"
            | other -> failtestf "expected StorageFailure, got %A" other

            Expect.isEmpty (run (coordinator.ListSnapshots())) "no manifest written"

            match audit.Recorded with
            | [ "_platform", BackupFailed p ] ->
                Expect.stringContains p.Reason "disk on fire" "audit carries the reason"
            | other -> failtestf "expected one BackupFailed row, got %A" other
        }
    ]

let private tests_restore =
    testList "restore" [
        test "snapshot → wipe → restore in place reproduces every store bit-for-bit" {
            let live, _, _, coordinator = newFixture ()
            let before = dump live
            let manifest = snapshotOk coordinator
            wipe live
            Expect.isEmpty (dump live) "wiped"

            let report =
                match
                    run (
                        coordinator.Restore(
                            manifest.SnapshotId,
                            Containers containers,
                            {
                                RestoreOptions.defaults with
                                    Placement = InPlace
                            }
                        )
                    )
                with
                | Ok r -> r
                | Error e -> failtestf "restore: %s" (RestoreError.message e)

            Expect.equal report.BlobsWritten before.Length "every blob written"
            Expect.equal report.HashesVerified before.Length "every hash verified before the first write"
            Expect.equal report.StagingPrefix None "in place"
            Expect.equal (dump live) before "live store is byte-identical to the pre-wipe dump"
        }

        test "side-by-side restore touches no live key; Promote swaps it in; DiscardStaged erases it" {
            let live, _, _, coordinator = newFixture ()
            let original = dump live
            let manifest = snapshotOk coordinator

            // Drift the live store after the snapshot.
            run (live.Upload(Team, "notes/hello.txt", utf8 "changed since the snapshot"))
            |> ignore

            run (live.Delete("_platform", "admin/settings.json")) |> ignore
            let drifted = dump live
            Expect.notEqual drifted original "the live store has drifted"

            let report =
                match
                    run (coordinator.Restore(manifest.SnapshotId, Containers containers, RestoreOptions.defaults))
                with
                | Ok r -> r
                | Error e -> failtestf "restore: %s" (RestoreError.message e)

            Expect.equal report.Placement SideBySide "default placement"
            Expect.equal report.StagingPrefix (Some(stagingPrefix report.RestoreId)) "staging prefix reported"

            let liveKeys =
                dump live |> List.filter (fun (_, n, _) -> not (n.StartsWith StagingRoot))

            Expect.equal liveKeys drifted "every live key is exactly as it was before the restore"
            Expect.equal (stagingKeys live).Length original.Length "every blob is staged"

            // Discard a second restore; the first is untouched.
            let second =
                match run (coordinator.Restore(manifest.SnapshotId, Platform, RestoreOptions.defaults)) with
                | Ok r -> r
                | Error e -> failtestf "second restore: %s" (RestoreError.message e)

            match run (coordinator.DiscardStaged(second.RestoreId, Platform)) with
            | Ok n -> Expect.equal n second.BlobsWritten "discard erased exactly the second staging set"
            | Error e -> failtestf "discard: %s" (RestoreError.message e)

            Expect.equal (stagingKeys live).Length original.Length "the first staging set is intact"

            match run (coordinator.Promote(report.RestoreId, Containers containers)) with
            | Ok n -> Expect.equal n original.Length "promote moved every staged blob"
            | Error e -> failtestf "promote: %s" (RestoreError.message e)

            Expect.isEmpty (stagingKeys live) "staging prefix gone after promote"
            Expect.equal (dump live) original "live store equals the snapshot"
        }

        test "a tampered backup copy fails preflight before any write — and Force does not override it" {
            let live, target, _, coordinator = newFixture ()
            let manifest = snapshotOk coordinator
            let victim = manifest.Entries |> List.find (fun e -> e.BlobName = "notes/hello.txt")

            run (target.Upload("_backups", backupNameOf manifest victim, utf8 "HELLO"))
            |> ignore

            wipe live

            for force in [ false; true ] do
                match
                    run (
                        coordinator.Restore(
                            manifest.SnapshotId,
                            Containers containers,
                            { Placement = InPlace; Force = force }
                        )
                    )
                with
                | Error(PreflightRefused [ HashMismatch(c, n, expected, actual) ]) ->
                    Expect.equal (c, n) (victim.Container, victim.BlobName) "names the tampered blob"
                    Expect.equal expected victim.Sha256 "expected digest"
                    Expect.equal actual (DeployRecords.digestBytes (utf8 "HELLO")) "actual digest"
                | other -> failtestf "force=%b: expected one HashMismatch, got %A" force other

            Expect.isEmpty (dump live) "nothing was written on either attempt"
        }

        test "a partial manifest is refused unless forced; forced, the rest restores and the gap is counted" {
            let live, target, _, coordinator = newFixture ()
            let manifest = snapshotOk coordinator

            let missing =
                manifest.Entries |> List.find (fun e -> e.BlobName = "admin/settings.json")

            run (target.Delete("_backups", backupNameOf manifest missing)) |> ignore
            wipe live

            match
                run (
                    coordinator.Restore(
                        manifest.SnapshotId,
                        Containers containers,
                        { Placement = InPlace; Force = false }
                    )
                )
            with
            | Error(PreflightRefused [ MissingBlob(c, n) ]) ->
                Expect.equal (c, n) (missing.Container, missing.BlobName) "names the gap"
            | other -> failtestf "expected MissingBlob, got %A" other

            Expect.isEmpty (dump live) "nothing written"

            match
                run (
                    coordinator.Restore(
                        manifest.SnapshotId,
                        Containers containers,
                        { Placement = InPlace; Force = true }
                    )
                )
            with
            | Ok r ->
                Expect.equal r.SkippedMissing 1 "one gap counted"
                Expect.equal r.BlobsWritten (manifest.Entries.Length - 1) "the rest written"
            | Error e -> failtestf "forced restore: %s" (RestoreError.message e)

            Expect.isFalse (run (live.Exists("_platform", "admin/settings.json"))) "the missing blob stays missing"
            Expect.isTrue (run (live.Exists(Team, "notes/hello.txt"))) "the others came back"
        }

        test "the scope selector narrows a restore to the containers named (GP 4)" {
            let live, _, _, coordinator = newFixture ()
            let manifest = snapshotOk coordinator
            wipe live

            match
                run (
                    coordinator.Restore(
                        manifest.SnapshotId,
                        Containers [ Team ],
                        { Placement = InPlace; Force = false }
                    )
                )
            with
            | Ok r ->
                Expect.equal r.Containers [ Team ] "only the team container"

                Expect.equal
                    r.BlobsWritten
                    (manifest.Entries |> List.filter (fun e -> e.Container = Team)).Length
                    "only its blobs"
            | Error e -> failtestf "restore: %s" (RestoreError.message e)

            Expect.isEmpty (run (live.List("_platform", ""))) "the platform container was not restored"
        }
    ]

let private tests_encryption =
    testList "encryption interplay" [
        test
            "snapshots copy ciphertext as-is, record the key id and never the material; a shredded key refuses restore at preflight" {
            let raw = InMemoryBlobStorage() :> IBlobStorage
            let target = InMemoryBlobStorage() :> IBlobStorage

            let key = {
                KeyId = "_platform/scopes/team-a/v1"
                Material = Array.init 32 byte
            }

            let resolver = FakeKeyResolver(key)

            let encrypted =
                EncryptedBlobStorage.EncryptedBlobStorage(raw, resolver) :> IBlobStorage

            run (encrypted.Upload(Team, "secrets/plan.txt", utf8 "the plan")) |> ignore
            run (raw.Upload(Team, "public/readme.txt", utf8 "plain")) |> ignore

            let cipherBytes =
                match run (raw.Download(Team, "secrets/plan.txt")) with
                | Ok b -> b
                | Error e -> failtestf "raw read: %s" e

            Expect.equal (tryEnvelopeKeyId cipherBytes) (Some key.KeyId) "the envelope header names the key"
            Expect.equal (tryEnvelopeKeyId (utf8 "plain")) None "a plaintext blob has no key id"

            let coordinator =
                BackupCoordinator(raw, target, keyResolver = resolver) :> IBackupCoordinator

            let manifest =
                match run (coordinator.Snapshot(Containers [ Team ])) with
                | Ok m -> m
                | Error e -> failtestf "snapshot: %s" (BackupError.message e)

            Expect.equal manifest.KeyIds [ key.KeyId ] "manifest records the key id"

            let secret =
                manifest.Entries |> List.find (fun e -> e.BlobName = "secrets/plan.txt")

            Expect.equal secret.KeyId (Some key.KeyId) "entry records the key id"

            Expect.equal
                (manifest.Entries |> List.find (fun e -> e.BlobName = "public/readme.txt")).KeyId
                None
                "plain entry has none"

            match run (target.Download("_backups", backupNameOf manifest secret)) with
            | Ok copy ->
                Expect.equal copy cipherBytes "the backup copy IS the ciphertext, byte for byte"
                let copyText = Encoding.Latin1.GetString copy
                Expect.isFalse (copyText.Contains "the plan") "no plaintext in the copy"
            | Error e -> failtestf "copy: %s" e

            let manifestText =
                match run (target.Download("_backups", manifestBlobName manifest.SnapshotId)) with
                | Ok b -> Encoding.UTF8.GetString b
                | Error e -> failtestf "manifest: %s" e

            Expect.isFalse
                (manifestText.Contains(Convert.ToBase64String key.Material))
                "no key material in the manifest"

            // Before the shred: preflight passes.
            match run (coordinator.Preflight(manifest.SnapshotId, Containers [ Team ])) with
            | Ok _ -> ()
            | Error e -> failtestf "preflight before shred: %s" (RestoreError.message e)

            resolver.Destroy()

            for force in [ false; true ] do
                match
                    run (
                        coordinator.Restore(
                            manifest.SnapshotId,
                            Containers [ Team ],
                            { Placement = InPlace; Force = force }
                        )
                    )
                with
                | Error(PreflightRefused [ RestorePreflightFailure.KeyDestroyed id ]) ->
                    Expect.equal id key.KeyId "names the shredded key"
                | other -> failtestf "force=%b: expected KeyDestroyed, got %A" force other

            Expect.isEmpty (stagingKeys raw) "nothing staged"

            // A deployment with no resolver at all cannot read the ciphertext either.
            let noResolver = BackupCoordinator(raw, target) :> IBackupCoordinator

            match run (noResolver.Preflight(manifest.SnapshotId, Containers [ Team ])) with
            | Error(PreflightRefused [ NoKeyResolver ids ]) -> Expect.equal ids [ key.KeyId ] "names the key ids"
            | other -> failtestf "expected NoKeyResolver, got %A" other
        }
    ]

let private drillFixture () =
    let live, target, audit, coordinator = newFixture ()
    let verifier = RestoreDrillVerifier(coordinator, live, auditLog = audit)
    let health = RestoreDrillHealthCheck(verifier) :> IHealthCheck
    live, target, audit, coordinator, verifier, health

let private tests_drill =
    testList "restore drill" [
        test
            "a healthy snapshot drills green: hashes, event replay, entity round-trip; scratch erased; audited; probe Healthy" {
            let live, _, audit, coordinator, verifier, health = drillFixture ()
            let manifest = snapshotOk coordinator
            let before = dump live
            Expect.equal (run (health.Check())) Healthy "no drill yet: Healthy"

            let outcome = run (verifier.RunDrill())

            Expect.isTrue outcome.Passed (sprintf "drill passed: %A" outcome)
            Expect.equal outcome.Stage "complete" "stage"
            Expect.equal outcome.SnapshotId (Some manifest.SnapshotId) "rehearsed the latest snapshot"
            Expect.equal outcome.HashesVerified manifest.Entries.Length "every hash verified"

            Expect.equal
                (outcome.Invariants |> List.map (fun i -> i.Name, i.Passed))
                [
                    "restored-hashes", true
                    "event-replay", true
                    "entity-index-round-trip", true
                ]
                "all three invariants held"

            let replay = outcome.Invariants |> List.find (fun i -> i.Name = "event-replay")
            Expect.stringContains replay.Detail "2 event(s) replayed" "the real store replayed both events"

            let entities =
                outcome.Invariants |> List.find (fun i -> i.Name = "entity-index-round-trip")

            Expect.stringContains entities.Detail "2 index ref(s)" "both entity index refs resolved"

            Expect.isEmpty (stagingKeys live) "scratch prefix erased"
            Expect.equal (dump live) before "live store untouched by the drill"
            Expect.equal verifier.LastOutcome (Some outcome) "outcome retained"
            Expect.equal (run (health.Check())) Healthy "probe Healthy after a pass"

            match audit.Recorded |> List.filter (fun (_, e) -> e.IsRestoreDrillPassed) with
            | [ "_platform", RestoreDrillPassed p ] ->
                Expect.equal p.SnapshotId manifest.SnapshotId "audit names the snapshot"

                Expect.equal
                    p.Invariants
                    [ "restored-hashes"; "event-replay"; "entity-index-round-trip" ]
                    "audit lists the invariants"
            | other -> failtestf "expected one RestoreDrillPassed row, got %A" other
        }

        test "a tampered backup fails the drill at preflight; probe Degraded; nothing staged" {
            let live, target, audit, coordinator, verifier, health = drillFixture ()
            let manifest = snapshotOk coordinator
            let victim = manifest.Entries |> List.head

            run (target.Upload("_backups", backupNameOf manifest victim, utf8 "tampered"))
            |> ignore

            let outcome = run (verifier.RunDrill())
            Expect.isFalse outcome.Passed "failed"
            Expect.equal outcome.Stage "preflight" "stage"
            Expect.stringContains (defaultArg outcome.Failure "") "Hash mismatch" "reason"
            Expect.isEmpty (stagingKeys live) "nothing staged"

            match run (health.Check()) with
            | Degraded msg -> Expect.stringContains msg "preflight" "probe carries the stage"
            | other -> failtestf "expected Degraded, got %A" other

            match audit.Recorded |> List.filter (fun (_, e) -> e.IsRestoreDrillFailed) with
            | [ _, RestoreDrillFailed p ] ->
                Expect.equal p.Stage "preflight" "audit stage"
                Expect.equal p.SnapshotId (Some manifest.SnapshotId) "audit names the snapshot"
            | other -> failtestf "expected one RestoreDrillFailed row, got %A" other
        }

        test
            "a dangling entity index ref in the snapshot fails the round-trip invariant, and the scratch is still erased" {
            let live, _, _, coordinator, verifier, _ = drillFixture ()

            run (live.Upload(Team, "entities/_indexes/TestEntity/Owner/ghost/e-ghost.ref", [||]))
            |> ignore

            let _ = snapshotOk coordinator
            let outcome = run (verifier.RunDrill())

            Expect.isFalse outcome.Passed "failed"
            Expect.equal outcome.Stage "invariant" "stage"

            Expect.equal
                (DrillOutcome.failedInvariants outcome)
                [ "entity-index-round-trip" ]
                "the one broken invariant"

            Expect.stringContains (defaultArg outcome.Failure "") "e-ghost" "names the dangling ref"
            Expect.isEmpty (stagingKeys live) "scratch erased on the failure path too"
        }

        test "no snapshot on the target is a failed drill at the no-snapshot stage" {
            let _, _, _, _, verifier, health = drillFixture ()
            let outcome = run (verifier.RunDrill())
            Expect.isFalse outcome.Passed "failed"
            Expect.equal outcome.Stage "no-snapshot" "stage"
            Expect.equal outcome.SnapshotId None "no snapshot"

            match run (health.Check()) with
            | Degraded _ -> ()
            | other -> failtestf "expected Degraded, got %A" other
        }

        test "the job handlers map outcomes to JobResult" {
            let _, _, _, coordinator, verifier, _ = drillFixture ()
            let ctx: JobContext = Unchecked.defaultof<JobContext>

            let drill = RestoreDrillJobHandler(verifier) :> IJobHandler

            match run (drill.Execute ctx) with
            | JobResult.PermanentFailure reason ->
                Expect.stringContains reason "no-snapshot" "a failed drill dead-letters the run"
            | other -> failtestf "expected PermanentFailure, got %A" other

            let snapshot = SnapshotJobHandler(coordinator, Containers containers) :> IJobHandler
            Expect.equal (run (snapshot.Execute ctx)) JobResult.Success "snapshot job succeeds"
            Expect.equal (run (drill.Execute ctx)) JobResult.Success "drill job succeeds once a snapshot exists"
        }
    ]

let private tests_compose =
    testList "compose" [
        test
            "BackupTargetConfiguredValidator: NoBackup Ok; enabled without a target Error; cron under NoJobScheduler Warning; otherwise Ok" {
            let validate (config: ServerConfig) (services: IServiceCollection) =
                run (
                    (BackupTargetConfiguredValidator(config, services) :> ConfigValidation.IConfigValidator).Validate()
                )

            let enabled crons = {
                ServerConfig.defaults with
                    Backup =
                        BackupEnabled {
                            BackupSettings.defaults with
                                DrillCron = crons
                        }
            }

            Expect.equal
                (validate ServerConfig.defaults (ServiceCollection()))
                ConfigValidation.Ok
                "default is NoBackup"

            match validate (enabled None) (ServiceCollection()) with
            | ConfigValidation.Error msg -> Expect.stringContains msg "IBackupTarget" "names the missing registration"
            | other -> failtestf "expected Error, got %A" other

            let withTarget () =
                let s = ServiceCollection()

                s.AddSingleton<IBackupTarget>(BackupTarget.ofStorage (InMemoryBlobStorage()))
                |> ignore

                s :> IServiceCollection

            Expect.equal (validate (enabled None) (withTarget ())) ConfigValidation.Ok "target, on-demand only"

            match validate (enabled (Some "0 3 * * *")) (withTarget ()) with
            | ConfigValidation.Warning msg -> Expect.stringContains msg "NoJobScheduler" "names the pairing"
            | other -> failtestf "expected Warning, got %A" other

            let scheduled = {
                enabled (Some "0 3 * * *") with
                    JobScheduler = InProcessJobScheduler
            }

            Expect.equal (validate scheduled (withTarget ())) ConfigValidation.Ok "target + scheduler"
        }

        test "ServerConfig.defaults carries NoBackup (GP 11 / GP 13)" {
            Expect.equal ServerConfig.defaults.Backup NoBackup "default"
            Expect.isFalse (BackupMode.isComposed NoBackup) "not composed"
            Expect.isTrue (BackupMode.isComposed (BackupEnabled BackupSettings.defaults)) "composed"
        }

        test "the four audit events round-trip through the registry codec" {
            let at = DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)

            let events = [
                BackupCompleted {
                    SnapshotId = "s1"
                    Containers = [ "_platform" ]
                    BlobCount = 3
                    TotalBytes = 42L
                    Consistency = "Fuzzy"
                    KeyIds = [ "k" ]
                    StartedAt = at
                    CompletedAt = at
                }
                BackupFailed {
                    Containers = [ "_platform" ]
                    Reason = "boom"
                    StartedAt = at
                    FailedAt = at
                }
                RestoreDrillPassed {
                    SnapshotId = "s1"
                    RestoreId = "r1"
                    HashesVerified = 3
                    Invariants = [ "restored-hashes" ]
                    StartedAt = at
                    CompletedAt = at
                }
                RestoreDrillFailed {
                    SnapshotId = None
                    RestoreId = None
                    Stage = "no-snapshot"
                    Reason = "none"
                    FailedInvariants = []
                    StartedAt = at
                    FailedAt = at
                }
            ]

            for event in events do
                let json = AuditLog.serialiseAuditEvent event

                match AuditLog.tryDecodeAuditEvent (AuditEvent.eventTypeName event) json with
                | Ok decoded -> Expect.equal decoded event (AuditEvent.eventTypeName event)
                | Error e -> failtestf "%s: %s" (AuditEvent.eventTypeName event) e
        }
    ]

let tests =
    testList "Phase 445 — backup / restore coordinator" [
        tests_snapshot
        tests_restore
        tests_encryption
        tests_drill
        tests_compose
    ]