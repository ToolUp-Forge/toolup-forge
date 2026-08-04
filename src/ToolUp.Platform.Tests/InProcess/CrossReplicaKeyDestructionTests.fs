module ToolUp.Platform.Tests.InProcess.CrossReplicaKeyDestructionTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.EncryptedBlobStorage
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Secrets

// ─── Phase 22b — cross-replica encryption-key destruction ────────────
//
// Two `PerScopeKeyResolver` instances stand in for two replicas: they
// share one `ISecretStore` (the persisted key is shared infrastructure)
// and one `InMemoryNotificationChannel` (standing in for the Redis
// companion's bus), and each holds its OWN `IMemoryCache` — which is the
// whole point, because the private per-replica cache is what a
// cross-replica shred has to reach.
//
// Each is wired with an explicit replica identity via the two-argument
// `WireToChannel`. Without that they would share the derived
// `{machine}/{pid}` identity, each would classify the other's broadcast
// as its own echo, and the fanout would test as working while doing
// nothing.
//
// **The control case is load-bearing.** "B cannot decrypt after A's
// destroy" is only evidence of fanout if B COULD decrypt without it —
// otherwise the test passes on a cold cache and proves nothing. So the
// unwired-B case is asserted too, and it asserts the defect: B keeps
// serving plaintext. If a future change makes both cases fail for some
// unrelated reason, the control fails and says so.

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-22b-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private newSecretStore () : ISecretStore =
    FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> ISecretStore

let private newInnerStorage () : IBlobStorage =
    LocalFileStorage.LocalFileStorage(newTempDir ()) :> IBlobStorage

let private newCache () : IMemoryCache =
    new MemoryCache(MemoryCacheOptions()) :> IMemoryCache

let private samplePayload =
    Encoding.UTF8.GetBytes "tenant payload that must stop decrypting fleet-wide"

/// `EncryptedBlobStorage` derives the `StorageScope` from the container
/// name, so `team-<id>` yields `ScopeId = <id>` — the value `DestroyKey`
/// takes.
let private container = "team-offboarded-tenant"
let private scopeId = "offboarded-tenant"

/// Recording `IAuditLog`. The acknowledgement is written off the
/// channel's delivery thread (`Async.Start`), so reads go through
/// `waitForEvent` rather than assuming it has landed by the time
/// `DestroyKey` returns.
type private RecordingAuditLog() =
    let recorded = ConcurrentQueue<string * AuditEvent>()

    member _.Events = recorded |> Seq.map snd |> List.ofSeq

    member this.OfType(eventType: string) =
        this.Events |> List.filter (fun e -> AuditEvent.eventTypeName e = eventType)

    interface IAuditLog with
        member _.Record(scope, audit) = async { recorded.Enqueue(scope, audit) }
        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// Poll until at least `count` events of `eventType` are recorded, or the
/// budget expires. Returns what it found either way so the caller asserts
/// on the count rather than on a timeout boolean.
let private waitForEvent (log: RecordingAuditLog) (eventType: string) (count: int) = async {
    let deadline = DateTime.UtcNow.AddSeconds 5.0

    let mutable found = log.OfType eventType

    while found.Length < count && DateTime.UtcNow < deadline do
        do! Async.Sleep 10
        found <- log.OfType eventType

    return found
}

/// Grace period before asserting an event was NOT recorded. Absence
/// cannot be polled for, so the negative assertions wait out the window
/// in which a stray `Async.Start` could still land.
let private settleForAbsence () = Async.Sleep 300

let private jsonOptions = FableConverters.create ()

// ── Fanout + eviction + audit, with two in-process "replicas" ──

let private fanoutTests =
    testList "cross-replica fanout" [

        testCaseAsync "CONTROL — an unwired sibling replica keeps decrypting after a destroy (the defect)"
        <| async {
            // No WireToChannel on B: this is pre-Phase-22b behaviour, and
            // it must still be reproducible, or the test below proves
            // nothing about the fanout.
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cacheA = newCache ()
            use cacheB = newCache ()

            let replicaA = PerScopeKeyResolver.create secrets cacheA None
            let replicaB = PerScopeKeyResolver.create secrets cacheB None
            let storageA = EncryptedBlobStorage(inner, replicaA) :> IBlobStorage
            let storageB = EncryptedBlobStorage(inner, replicaB) :> IBlobStorage

            let! upload = storageA.Upload(container, "doc.bin", samplePayload)
            Expect.isOk upload "upload succeeds"

            // Warm B's cache — it now holds the key in memory.
            let! warm = storageB.Download(container, "doc.bin")
            Expect.isOk warm "B decrypts before the destroy (its cache is warm)"

            let! destroyed = replicaA.DestroyKey(scopeId, "admin-1")
            Expect.isOk destroyed "destroy succeeds on A"

            // The secret is gone and A's cache is evicted, but B never
            // heard about it: its warm cache keeps serving plaintext.
            let! after = storageB.Download(container, "doc.bin")

            match after with
            | Result.Ok bytes ->
                Expect.equal bytes samplePayload "the control reproduces the defect: unwired B still returns plaintext"
            | Result.Error e ->
                failtestf
                    "control case is no longer a control — unwired B failed to decrypt (%s), so the fanout test below would pass without any fanout"
                    e
        }

        testCaseAsync
            "DestroyKey on A evicts B's cache — B's decrypt fails with the key-destroyed error, never plaintext"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cacheA = newCache ()
            use cacheB = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let replicaA = PerScopeKeyResolver.create secrets cacheA None
            let replicaB = PerScopeKeyResolver.create secrets cacheB None
            do! replicaA.WireToChannel(channel, "replica-a")
            do! replicaB.WireToChannel(channel, "replica-b")

            let storageA = EncryptedBlobStorage(inner, replicaA) :> IBlobStorage
            let storageB = EncryptedBlobStorage(inner, replicaB) :> IBlobStorage

            let! _ = storageA.Upload(container, "doc.bin", samplePayload)

            let! warm = storageB.Download(container, "doc.bin")
            Expect.isOk warm "B decrypts before the destroy (its cache is warm — same starting state as the control)"

            let! destroyed = replicaA.DestroyKey(scopeId, "admin-1")
            Expect.isOk destroyed "destroy succeeds on A"

            let! after = storageB.Download(container, "doc.bin")

            match after with
            | Result.Ok _ ->
                failtest "B still decrypted after the cross-replica destroy — plaintext survived a crypto-shred"
            | Result.Error msg -> Expect.stringContains msg "destroyed" "B surfaces the documented key-destroyed error"
        }

        testCaseAsync
            "A records EncryptionKeyDestroyed and no self-acknowledgement; B records exactly one acknowledgement"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cacheA = newCache ()
            use cacheB = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let auditA = RecordingAuditLog()
            let auditB = RecordingAuditLog()

            let replicaA = PerScopeKeyResolver.create secrets cacheA (Some(auditA :> IAuditLog))
            let replicaB = PerScopeKeyResolver.create secrets cacheB (Some(auditB :> IAuditLog))
            do! replicaA.WireToChannel(channel, "replica-a")
            do! replicaB.WireToChannel(channel, "replica-b")

            let storageA = EncryptedBlobStorage(inner, replicaA) :> IBlobStorage
            let! _ = storageA.Upload(container, "doc.bin", samplePayload)

            let requestedAfter = DateTimeOffset.UtcNow.AddSeconds -1.0
            let! destroyed = replicaA.DestroyKey(scopeId, "admin-1")
            Expect.isOk destroyed "destroy succeeds on A"

            let! acks = waitForEvent auditB "EncryptionKeyDestroyAcknowledged" 1
            do! settleForAbsence ()

            // A: the destroy, and nothing acknowledging its own broadcast.
            Expect.equal
                (auditA.OfType "EncryptionKeyDestroyed" |> List.length)
                1
                "A records exactly one EncryptionKeyDestroyed"

            Expect.isEmpty
                (auditA.OfType "EncryptionKeyDestroyAcknowledged")
                "A must not acknowledge its own destroy — the in-process channel echoes the publish back, and a self-ack would make a fleet of one look like a confirmed fanout"

            // B: exactly one acknowledgement, naming itself and A.
            Expect.equal acks.Length 1 "B records exactly one EncryptionKeyDestroyAcknowledged"

            match acks with
            | [ EncryptionKeyDestroyAcknowledged p ] ->
                Expect.equal p.ScopeId scopeId "acknowledgement names the destroyed scope"
                Expect.stringContains p.KeyId scopeId "acknowledgement carries the destroyed KeyId"
                Expect.equal p.UserId "admin-1" "UserId is the original requester, carried across from the envelope"
                Expect.equal p.AcknowledgedBy "replica-b" "acknowledgement names the replica that evicted"
                Expect.equal p.OriginReplicaId "replica-a" "acknowledgement names the replica the destroy originated on"
                Expect.equal p.Resolver "PerScopeKeyResolver" "resolver is named"

                Expect.isGreaterThan
                    p.RequestedAt
                    requestedAfter
                    "RequestedAt is the originating replica's instant, not a default"

                Expect.isGreaterThanOrEqual
                    p.AcknowledgedAt
                    p.RequestedAt
                    "AcknowledgedAt is at or after RequestedAt, so the fanout delay is computable"
            | other -> failtestf "expected one EncryptionKeyDestroyAcknowledged payload, got %A" other

            Expect.isEmpty
                (auditB.OfType "EncryptionKeyDestroyed")
                "B did not perform the destroy, so it must not claim to have"
        }

        testCaseAsync "Single replica — no acknowledgement is recorded and the destroy behaves as before"
        <| async {
            // GP 11: a single-replica deployment must be unchanged. Its
            // fanout is a publish it receives itself, and that must
            // produce no forensic event at all.
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cache = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let audit = RecordingAuditLog()

            let resolver = PerScopeKeyResolver.create secrets cache (Some(audit :> IAuditLog))
            do! resolver.WireToChannel channel

            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage
            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            let! destroyed = resolver.DestroyKey(scopeId, "admin-1")
            Expect.isOk destroyed "destroy succeeds"

            do! settleForAbsence ()

            Expect.equal
                (audit.OfType "EncryptionKeyDestroyed" |> List.length)
                1
                "the destroy is recorded exactly as before Phase 22b"

            Expect.isEmpty
                (audit.OfType "EncryptionKeyDestroyAcknowledged")
                "a lone replica has nothing to acknowledge to itself"

            let! after = storage.Download(container, "doc.bin")

            match after with
            | Result.Ok _ -> failtest "expected the key-destroyed error after a single-replica destroy"
            | Result.Error msg -> Expect.stringContains msg "destroyed" "unchanged single-replica behaviour"
        }
    ]

// ── The published envelope shape ──

let private envelopeTests =
    testList "KeyDestroyed envelope" [

        testCaseAsync "DestroyKey publishes (ScopeId, KeyId, RequestedBy, RequestedAt) plus the origin replica"
        <| async {
            // Subscribe a bare handler alongside the resolvers to capture
            // the wire payload — the contract is the envelope, so assert
            // on the bytes rather than only on the effect.
            let secrets = newSecretStore ()
            use cache = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let captured = ConcurrentQueue<string>()

            let! _ =
                channel.Subscribe(
                    NotificationKind.PlatformReservedScope,
                    fun env ->
                        match env.Notification with
                        | CustomNotification(key, payload) when key = KeyDestroyedNotification.NotificationKey ->
                            captured.Enqueue payload
                        | _ -> ()
                )

            let resolver = PerScopeKeyResolver.create secrets cache None
            do! resolver.WireToChannel(channel, "replica-a")

            let resolverIface = resolver :> IBlobEncryptionKeyResolver

            let! _ =
                resolverIface.ResolveKey {
                    Container = container
                    ScopeId = scopeId
                    Persist = true
                }

            let before = DateTimeOffset.UtcNow.AddSeconds -1.0
            let! destroyed = resolver.DestroyKey(scopeId, "admin-7")
            Expect.isOk destroyed "destroy succeeds"

            match captured |> List.ofSeq with
            | [ payload ] ->
                let env = JsonSerializer.Deserialize<KeyDestroyedEnvelope>(payload, jsonOptions)
                Expect.equal env.ScopeId scopeId "ScopeId"
                Expect.equal env.KeyId (sprintf "_platform/scopes/%s/v1" scopeId) "KeyId matches the resolver's format"
                Expect.equal env.RequestedBy "admin-7" "RequestedBy is the invoking actor"
                Expect.isGreaterThan env.RequestedAt before "RequestedAt is stamped at destroy time"
                Expect.equal env.OriginReplicaId "replica-a" "OriginReplicaId identifies the publisher"
            | other -> failtestf "expected exactly one published envelope, got %d" other.Length
        }

        testCaseAsync "A pre-Phase-22b bare-scopeId payload still evicts, and records no acknowledgement"
        <| async {
            // Rolling upgrade: an old replica publishes the bare scopeId.
            // The security-critical half (evict) must still run; the
            // acknowledgement is skipped because the envelope's fields
            // were never sent, and fabricating an actor would be worse
            // than omitting the row.
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cache = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let audit = RecordingAuditLog()

            let replica = PerScopeKeyResolver.create secrets cache (Some(audit :> IAuditLog))
            do! replica.WireToChannel(channel, "replica-b")

            let storage = EncryptedBlobStorage(inner, replica) :> IBlobStorage
            let! _ = storage.Upload(container, "doc.bin", samplePayload)
            let! warm = storage.Download(container, "doc.bin")
            Expect.isOk warm "cache is warm before the legacy broadcast"

            // Delete the persisted secret out-of-band, as the old
            // replica's own DestroyKey would have, then broadcast the
            // legacy payload.
            let! _ = secrets.DeleteSecret("_platform", "encryption/scopes/" + scopeId + ".key")

            do!
                channel.Publish(
                    NotificationKind.PlatformReservedScope,
                    CustomNotification(KeyDestroyedNotification.NotificationKey, scopeId)
                )

            do! settleForAbsence ()

            let! after = storage.Download(container, "doc.bin")

            match after with
            | Result.Ok _ ->
                failtest
                    "a legacy-format broadcast failed to evict — a rolling upgrade would keep serving a shredded key"
            | Result.Error msg -> Expect.stringContains msg "destroyed" "legacy payload still evicts the cache"

            Expect.isEmpty
                (audit.OfType "EncryptionKeyDestroyAcknowledged")
                "a legacy payload carries none of the acknowledgement's fields, so no row is fabricated"
        }
    ]

// ── KeyDestroyAckCoverageValidator ──

/// Stand-in for a distributed channel companion — an `INotificationChannel`
/// the SDK does not recognise as in-process.
type private FakeDistributedChannel() =
    interface INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

let private cfg (surfaces: SurfaceProfile list) : ServerConfig = {
    ServerConfig.defaults with
        Surfaces = surfaces
}

/// Compose a service collection the way the real compose does: the
/// resolver and the channel arrive as registered singleton instances.
let private servicesWith (resolver: IBlobEncryptionKeyResolver option) (channel: INotificationChannel option) =
    let services = ServiceCollection() :> IServiceCollection

    resolver
    |> Option.iter (fun r -> services.AddSingleton<IBlobEncryptionKeyResolver>(r) |> ignore)

    channel
    |> Option.iter (fun c -> services.AddSingleton<INotificationChannel>(c) |> ignore)

    services

let private perScopeResolver () : IBlobEncryptionKeyResolver =
    PerScopeKeyResolver.create (newSecretStore ()) (newCache ()) None :> IBlobEncryptionKeyResolver

let private validate (config: ServerConfig) (services: IServiceCollection) : ValidationResult =
    let v =
        KeyDestroyAckCoverageValidator.KeyDestroyAckCoverageValidator(config, services) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private validatorTests =
    testList "KeyDestroyAckCoverageValidator" [

        test "Team + PerScopeKeyResolver + in-memory channel → Warning" {
            let services =
                servicesWith
                    (Some(perScopeResolver ()))
                    (Some(InMemoryNotificationChannel(None) :> INotificationChannel))

            match validate (cfg Surfaces.team) services with
            | Warning msg ->
                Expect.stringContains msg "Team" "names the deployment shape"
                Expect.stringContains msg "PerScopeKeyResolver" "names the resolver in use"
                Expect.stringContains msg "RedisNotifications" "names the recommended distributed companion"

                Expect.stringContains
                    msg
                    "EncryptionKeyDestroyAcknowledged"
                    "explains the missing forensic evidence, not just the cache staleness"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "MultiTeam + PerScopeKeyResolver + no-op channel → Warning (no-op reaches nobody at all)" {
            let services =
                servicesWith (Some(perScopeResolver ())) (Some(NoOpNotificationChannel() :> INotificationChannel))

            match validate (cfg Surfaces.multiTeam) services with
            | Warning msg -> Expect.stringContains msg "MultiTeam" "names the deployment shape"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Team + PerScopeKeyResolver + distributed channel → Ok" {
            let services =
                servicesWith (Some(perScopeResolver ())) (Some(FakeDistributedChannel() :> INotificationChannel))

            Expect.equal
                (validate (cfg Surfaces.team) services)
                Ok
                "an unrecognised channel is assumed distributed — a false warning at a correctly-configured deployment teaches operators to ignore preflight"
        }

        test "Team + SingleKeyResolver + in-memory channel → Ok (no DestroyKey path to fan out)" {
            let single = SingleKeyResolver.create (newSecretStore ())

            let services =
                servicesWith (Some single) (Some(InMemoryNotificationChannel(None) :> INotificationChannel))

            Expect.equal (validate (cfg Surfaces.team) services) Ok "SingleKeyResolver has no crypto-shred broadcast"
        }

        test "Individual + PerScopeKeyResolver + in-memory channel → Ok (not a Team/MultiTeam shape)" {
            let services =
                servicesWith
                    (Some(perScopeResolver ()))
                    (Some(InMemoryNotificationChannel(None) :> INotificationChannel))

            Expect.equal
                (validate (cfg Surfaces.individual) services)
                Ok
                "the warning is scoped to the multi-tenant shapes the phase names"
        }

        test "No encryption resolver composed → Ok" {
            let services =
                servicesWith None (Some(InMemoryNotificationChannel(None) :> INotificationChannel))

            Expect.equal (validate (cfg Surfaces.team) services) Ok "nothing to warn about without envelope encryption"
        }

        test "No channel registered → Ok (nothing to assess)" {
            let services = servicesWith (Some(perScopeResolver ())) None

            Expect.equal
                (validate (cfg Surfaces.team) services)
                Ok
                "with no channel instance registered there is no fanout path to judge"
        }

        test "Validator metadata is well-formed" {
            let v =
                KeyDestroyAckCoverageValidator.KeyDestroyAckCoverageValidator(cfg Surfaces.team, servicesWith None None)
                :> IConfigValidator

            Expect.equal v.Name "key-destroy-ack-coverage" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 22b — cross-replica encryption-key destruction" [ fanoutTests; envelopeTests; validatorTests ]