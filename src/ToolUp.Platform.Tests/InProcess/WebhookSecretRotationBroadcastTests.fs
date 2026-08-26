module ToolUp.Platform.Tests.InProcess.WebhookSecretRotationBroadcastTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open Expecto
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Secrets
open ToolUp.Platform.WebhookDispatcher
open ToolUp.Platform.Tests.Contracts

// ─── Phase 464 — cross-instance signing-secret rotation fanout ───────
//
// Two `BlobWebhookRegistry` instances stand in for two deployment
// instances. They share the durable infrastructure a real fleet shares —
// one `IBlobStorage` for the subscription blobs, one secrets DIRECTORY —
// and each gets its OWN `FileSecretStore` over that directory, which is
// the whole point: `FileSecretStore` memoises a scope's secret map on
// first read and evicts only on its own write, with no TTL. That private
// per-instance cache is what a cross-instance rotation has to reach.
//
// One `InMemoryNotificationChannel` stands in for the Redis companion's
// bus. Each registry is wired with an explicit instance identity via the
// three-argument `WireToChannel`; without that they would share the
// derived `{machine}/{pid}` identity, each would classify the other's
// broadcast as its own echo, and the fanout would test as working while
// doing nothing.
//
// **The control cases are load-bearing.** "B serves the rotated secret"
// is only evidence of fanout if B would otherwise serve the STALE one —
// a test whose B never cached anything passes without any fanout at all.
// So the unwired-B cases are asserted too, and they assert the DEFECT:
// B keeps serving the superseded secret, and a receiver updated to the
// new one rejects B's deliveries as inauthentic. If a future change makes
// both arms agree for some unrelated reason, the control fails and says
// which.

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-464-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

/// A distinct `FileSecretStore` over a SHARED directory — two processes'
/// worth of independent caches over one durable store.
let private storeOver (dir: string) : ISecretStore =
    FileSecretStore.FileSecretStore(baseDir = dir) :> ISecretStore

let private oldSecret = "old-webhook-signing-secret-0123456789abcdef"
let private newSecret = "new-webhook-signing-secret-fedcba9876543210"

let private scopeId = "_platform"

let private body = Encoding.UTF8.GetBytes """{"event":"FlagChanged"}"""

let private envelopeJson = FableConverters.create ()

/// Recording `ILogger` — the unwired-rotation warning is emitted once per
/// process at security class, and "was it logged?" is a distinct question
/// from "was it counted?" (the one-argument registry constructor has no
/// logger, which is why the count exists as its own signal).
type private RecordingLogger() =
    let warns = ConcurrentQueue<string>()
    let errors = ConcurrentQueue<string>()

    member _.Warnings = warns |> List.ofSeq
    member _.Errors = errors |> List.ofSeq

    interface ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(message) = warns.Enqueue message
        member _.Error(message, _) = errors.Enqueue message

let private sampleSubscription (scope: string) : WebhookSubscription =
    let id = Guid.NewGuid()

    {
        SubscriptionId = id
        ScopeId = scope
        TargetUrl = "https://hooks.example.com/endpoint"
        SecretRef = WebhookSecretRef.current id
        Secret = None
        EventTypes = [ "FlagChanged" ]
        Status = WebhookStatus.Active
        CreatedBy = "user-1"
        CreatedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        ConsecutiveFailures = 0
        PreviousSecretRef = None
        PreviousSecret = None
        PreviousSecretExpiresAt = None
    }

/// Resolve a subscription's current signing secret VALUE the way the
/// dispatcher does — through whichever `ISecretStore` this instance holds.
let private resolveSecret (store: ISecretStore) (sub: WebhookSubscription) = async {
    let! value = store.GetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf sub.SecretRef)
    return value |> Option.defaultValue ""
}

/// Shared fixture: durable blob + secrets directory seeded with
/// `oldSecret`, a subscription persisted, and one store per instance.
let private seedFleet () = async {
    let secretsDir = newTempDir ()
    let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
    let storeA = storeOver secretsDir
    let storeB = storeOver secretsDir

    let sub = sampleSubscription scopeId

    do!
        storeA.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf sub.SecretRef, oldSecret)
        |> Async.Ignore

    return secretsDir, blobs, storeA, storeB, sub
}

/// Perform the rotation the way the API handler does: move the VALUES in
/// the shared store first, then record the ref bookkeeping through the
/// registry (which is what publishes the broadcast).
let private rotate (registry: IWebhookRegistry) (store: ISecretStore) (sub: WebhookSubscription) = async {
    let currentRef = WebhookSecretRef.current sub.SubscriptionId
    let previousRef = WebhookSecretRef.previous sub.SubscriptionId

    do!
        store.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf previousRef, oldSecret)
        |> Async.Ignore

    do!
        store.SetSecret(WebhookSecretRef.Scope, WebhookSecretRef.keyOf currentRef, newSecret)
        |> Async.Ignore

    return!
        registry.RotateSecret(sub.ScopeId, sub.SubscriptionId, currentRef, previousRef, DateTime.UtcNow.AddHours 24.0)
}

// ── Fanout: does a rotation on A reach B? ──

let private fanoutTests =
    testList "cross-instance rotation fanout" [

        testCaseAsync "CONTROL — an unwired sibling instance keeps resolving the STALE secret (the defect)"
        <| async {
            let! secretsDir, blobs, storeA, storeB, sub = seedFleet ()
            ignore secretsDir

            // No channel on either side: pre-Phase-464 behaviour. It must
            // still be reproducible, or the wired case below proves
            // nothing about the broadcast.
            let registryA = WebhookRegistry.createRegistry blobs
            let! created = registryA.CreateSubscription sub
            Expect.isOk created "subscription created"

            // Warm B's cache — it now holds the whole scope's secret map.
            let! warm = resolveSecret storeB sub
            Expect.equal warm oldSecret "B resolves the original secret before the rotation (its cache is warm)"

            match! rotate registryA storeA sub with
            | Error e -> failtestf "rotation failed on A: %s" e
            | Ok _ -> ()

            // A sees the new value (it did the write). B never heard.
            let! onA = resolveSecret storeA sub
            Expect.equal onA newSecret "A resolves the rotated secret — it performed the write"

            let! onB = resolveSecret storeB sub

            if onB = newSecret then
                failtest
                    "control case is no longer a control — unwired B already resolved the ROTATED secret, so the fanout test below would pass without any fanout. Did FileSecretStore stop caching, or gain a TTL?"

            Expect.equal
                onB
                oldSecret
                "the control reproduces the defect: unwired B still resolves the superseded secret"
        }

        testCaseAsync "RotateSecret on A invalidates B's cached secret — B resolves the ROTATED secret"
        <| async {
            let! secretsDir, blobs, storeA, storeB, sub = seedFleet ()
            ignore secretsDir

            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let registryA = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())
            let registryB = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())

            do! registryA.WireToChannel(channel, "instance-a", WebhookRegistry.secretCacheInvalidator storeA)
            do! registryB.WireToChannel(channel, "instance-b", WebhookRegistry.secretCacheInvalidator storeB)

            let! _ = (registryA :> IWebhookRegistry).CreateSubscription sub

            // Same warm-cache starting state as the control.
            let! warm = resolveSecret storeB sub
            Expect.equal warm oldSecret "B resolves the original secret before the rotation (cache warm)"

            match! rotate (registryA :> IWebhookRegistry) storeA sub with
            | Error e -> failtestf "rotation failed on A: %s" e
            | Ok _ -> ()

            let! onB = resolveSecret storeB sub

            Expect.equal
                onB
                newSecret
                "B resolves the rotated secret within the notification round-trip — no restart, no TTL wait"

            Expect.equal registryB.ObservedRotationCount 1 "B acted on exactly one sibling rotation"
            Expect.equal registryA.UnwiredRotateSecretCount 0 "a wired rotation is never counted as unwired"
        }

        testCaseAsync
            "no silent inauthentic drop — B's signature verifies against the rotated secret (control: unwired B's does not)"
        <| async {
            // The acceptance criterion stated end to end. A receiver that
            // has been updated to the new secret is the judge: it accepts
            // or rejects what instance B actually signs. This is the
            // difference the operator experiences, and it is reported as a
            // forged payload rather than as a stale key — which is why the
            // unwired arm is the more important half of this case.
            let assertReceiverVerdict (wired: bool) = async {
                let! secretsDir, blobs, storeA, storeB, sub = seedFleet ()
                ignore secretsDir

                let registryA = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())
                let registryB = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())

                if wired then
                    let channel = InMemoryNotificationChannel(None) :> INotificationChannel

                    do! registryA.WireToChannel(channel, "instance-a", WebhookRegistry.secretCacheInvalidator storeA)
                    do! registryB.WireToChannel(channel, "instance-b", WebhookRegistry.secretCacheInvalidator storeB)

                let! _ = (registryA :> IWebhookRegistry).CreateSubscription sub
                let! warm = resolveSecret storeB sub
                Expect.equal warm oldSecret "B's cache is warm with the pre-rotation secret in both arms"

                match! rotate (registryA :> IWebhookRegistry) storeA sub with
                | Error e -> failtestf "rotation failed on A: %s" e
                | Ok _ -> ()

                // B signs an outbound delivery with whatever it resolves.
                let! bSecret = resolveSecret storeB sub
                let header = WebhookSignature.headerFor [ bSecret ] body

                // The receiver rotated in step with the platform and now
                // holds only the new secret.
                return WebhookSignature.verifies newSecret body header
            }

            let! unwiredAccepted = assertReceiverVerdict false

            Expect.isFalse
                unwiredAccepted
                "CONTROL — an unwired sibling's delivery is REJECTED by a receiver holding the rotated secret (the silent-inauthentic-drop defect)"

            let! wiredAccepted = assertReceiverVerdict true

            Expect.isTrue
                wiredAccepted
                "a wired sibling's delivery verifies against the rotated secret — no inauthentic drop during the rotation window"
        }

        testCaseAsync "a rotating instance does not act on its own echo (single-instance deployments stay quiet)"
        <| async {
            // The in-process channel delivers a publish back to its
            // publisher. Without echo suppression a single-instance
            // deployment would count its own rotation as a sibling signal,
            // and ObservedRotationCount would stop being fanout evidence.
            let! secretsDir, blobs, storeA, _, sub = seedFleet ()
            ignore secretsDir

            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let registryA = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())

            do! registryA.WireToChannel(channel, "instance-a", WebhookRegistry.secretCacheInvalidator storeA)

            let! _ = (registryA :> IWebhookRegistry).CreateSubscription sub

            match! rotate (registryA :> IWebhookRegistry) storeA sub with
            | Error e -> failtestf "rotation failed: %s" e
            | Ok _ -> ()

            Expect.equal registryA.ObservedRotationCount 0 "the publisher does not act on its own broadcast"

            // And the rotation still worked locally — echo suppression must
            // not be mistaken for the rotation being a no-op.
            let! onA = resolveSecret storeA sub
            Expect.equal onA newSecret "the rotating instance still resolves the new secret"
        }

        testCaseAsync "a failed rotation publishes nothing — a sibling's cache is left alone"
        <| async {
            // Publishing before the durable write would invalidate every
            // sibling's cache and then leave them re-reading the OLD value
            // they just dropped: churn with no rotation. A rotation that
            // cannot persist must be silent on the bus.
            let! secretsDir, blobs, storeA, storeB, _ = seedFleet ()
            ignore secretsDir

            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let registryA = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())
            let registryB = WebhookRegistry.createRegistryInstance blobs (RecordingLogger())

            do! registryA.WireToChannel(channel, "instance-a", WebhookRegistry.secretCacheInvalidator storeA)
            do! registryB.WireToChannel(channel, "instance-b", WebhookRegistry.secretCacheInvalidator storeB)

            ignore storeB
            let missingId = Guid.NewGuid()

            match!
                (registryA :> IWebhookRegistry)
                    .RotateSecret(
                        scopeId,
                        missingId,
                        WebhookSecretRef.current missingId,
                        WebhookSecretRef.previous missingId,
                        DateTime.UtcNow.AddHours 24.0
                    )
            with
            | Ok _ -> failtest "expected Error — the subscription does not exist"
            | Error _ -> ()

            Expect.equal registryB.ObservedRotationCount 0 "a rotation that never persisted published no invalidation"

            Expect.equal
                registryA.UnwiredRotateSecretCount
                0
                "a failed rotation is not counted as an unwired one either"
        }
    ]

// ── Unwired accounting: is the gap visible? ──

let private unwiredTests =
    testList "unwired rotation accounting" [

        testCaseAsync "an unwired rotation is counted and warned once per process at security class"
        <| async {
            let! secretsDir, blobs, storeA, _, sub = seedFleet ()
            ignore secretsDir

            let logger = RecordingLogger()
            let registry = WebhookRegistry.createRegistryInstance blobs logger

            Expect.isFalse registry.IsWiredToChannel "not wired before WireToChannel"

            let! _ = (registry :> IWebhookRegistry).CreateSubscription sub

            match! rotate (registry :> IWebhookRegistry) storeA sub with
            | Error e -> failtestf "rotation failed: %s" e
            | Ok _ -> ()

            Expect.equal registry.UnwiredRotateSecretCount 1 "the unwired rotation is counted"

            let warning =
                logger.Warnings |> List.tryFind (fun w -> w.Contains "secret_rotation_unwired")

            match warning with
            | None -> failtest "no unwired-rotation warning emitted"
            | Some w ->
                Expect.stringContains w "class=security" "the warning is classified security"
                Expect.stringContains w "WireToChannel" "the warning names the missing wiring"

                Expect.stringContains
                    w
                    "inauthentic"
                    "the warning names the symptom an operator will actually see (a rejected-as-forged delivery)"

                Expect.isFalse (w.Contains oldSecret) "the warning never carries a secret value"
                Expect.isFalse (w.Contains newSecret) "the warning never carries a secret value"

            // A second rotation counts but does not re-log (once per process).
            let sub2 = sampleSubscription scopeId
            let! _ = (registry :> IWebhookRegistry).CreateSubscription sub2

            match! rotate (registry :> IWebhookRegistry) storeA sub2 with
            | Error e -> failtestf "second rotation failed: %s" e
            | Ok _ -> ()

            Expect.equal registry.UnwiredRotateSecretCount 2 "every unwired rotation is counted"

            let warningCount =
                logger.Warnings
                |> List.filter (fun w -> w.Contains "secret_rotation_unwired")
                |> List.length

            Expect.equal warningCount 1 "the warning is logged once per process, not once per rotation"
        }

        testCaseAsync "CONTROL — a WIRED rotation warns nothing and counts nothing"
        <| async {
            // Pairs with the case above so a green run is evidence about
            // the UNWIRED path specifically, rather than about rotations
            // in general.
            let! secretsDir, blobs, storeA, _, sub = seedFleet ()
            ignore secretsDir

            let logger = RecordingLogger()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let registry = WebhookRegistry.createRegistryInstance blobs logger

            do! registry.WireToChannel(channel, "instance-a", WebhookRegistry.secretCacheInvalidator storeA)
            Expect.isTrue registry.IsWiredToChannel "wired after WireToChannel"

            let! _ = (registry :> IWebhookRegistry).CreateSubscription sub

            match! rotate (registry :> IWebhookRegistry) storeA sub with
            | Error e -> failtestf "rotation failed: %s" e
            | Ok _ -> ()

            Expect.equal registry.UnwiredRotateSecretCount 0 "a wired rotation is not counted as unwired"

            Expect.isEmpty
                (logger.Warnings |> List.filter (fun w -> w.Contains "secret_rotation_unwired"))
                "a wired rotation emits no unwired warning"

            Expect.isEmpty logger.Errors "a successful wired rotation logs no error"
        }
    ]

// ── The envelope + the invalidation seam ──

let private envelopeTests =
    testList "WebhookSecretRotated envelope" [

        test "the notification key is the documented stable wire constant" {
            // A distributed channel companion matches on this string. It is
            // a wire contract, so pin it rather than re-deriving it.
            Expect.equal
                WebhookSecretRotatedNotification.NotificationKey
                "_platform.webhooks.secret-rotated"
                "the CustomNotification key is stable"
        }

        test "the envelope round-trips through the F# converter set with every field intact" {
            let envelope: WebhookSecretRotatedEnvelope = {
                ScopeId = "team-acme"
                SubscriptionId = Guid("11111111-2222-3333-4444-555555555555")
                CurrentSecretRef = "_platform/webhooks/abc.secret"
                PreviousSecretRef = "_platform/webhooks/abc.secret.previous"
                GraceExpiresAt = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                RotatedAt = DateTimeOffset(2026, 5, 31, 9, 30, 0, TimeSpan.Zero)
                OriginReplicaId = "pod-7/4242"
            }

            let json = JsonSerializer.Serialize(envelope, envelopeJson)

            match WebhookRegistry.decodeSecretRotatedPayload json with
            | None -> failtest "the envelope did not decode"
            | Some decoded -> Expect.equal decoded envelope "every field survives the round-trip"
        }

        test "the envelope carries no secret VALUE — only references" {
            let envelope: WebhookSecretRotatedEnvelope = {
                ScopeId = "team-acme"
                SubscriptionId = Guid.NewGuid()
                CurrentSecretRef = "_platform/webhooks/abc.secret"
                PreviousSecretRef = "_platform/webhooks/abc.secret.previous"
                GraceExpiresAt = DateTime.UtcNow
                RotatedAt = DateTimeOffset.UtcNow
                OriginReplicaId = "pod-7/4242"
            }

            let json = JsonSerializer.Serialize(envelope, envelopeJson)
            Expect.isFalse (json.Contains oldSecret) "no old secret value on the wire"
            Expect.isFalse (json.Contains newSecret) "no new secret value on the wire"
        }

        test "a malformed payload decodes to None rather than throwing inside the handler" {
            Expect.isNone (WebhookRegistry.decodeSecretRotatedPayload "") "empty payload"
            Expect.isNone (WebhookRegistry.decodeSecretRotatedPayload "   ") "whitespace payload"
            Expect.isNone (WebhookRegistry.decodeSecretRotatedPayload "not json at all") "non-JSON payload"
            Expect.isNone (WebhookRegistry.decodeSecretRotatedPayload "{}") "JSON with no scope"

            Expect.isNone
                (WebhookRegistry.decodeSecretRotatedPayload """{"ScopeId":""}""")
                "JSON with a blank scope carries nothing to invalidate"
        }

        test "an envelope with EXTRA fields still decodes — a newer publisher stays readable by an older subscriber" {
            let forwardJson =
                """{"ScopeId":"team-acme","SubscriptionId":"11111111-2222-3333-4444-555555555555","CurrentSecretRef":"cur","PreviousSecretRef":"prev","GraceExpiresAt":"2026-06-01T00:00:00.0000000Z","RotatedAt":"2026-05-31T09:30:00.0000000+00:00","OriginReplicaId":"pod-7/4242","SomeFutureField":"ignored"}"""

            match WebhookRegistry.decodeSecretRotatedPayload forwardJson with
            | None -> failtest "an additive field broke the decode — rolling upgrades would drop invalidations"
            | Some decoded -> Expect.equal decoded.ScopeId "team-acme" "the known fields still decode"
        }

        testCaseAsync "FileSecretStore implements the invalidation seam, and InvalidateScope forces a re-read"
        <| async {
            let dir = newTempDir ()
            let writer = storeOver dir
            let reader = storeOver dir

            do! writer.SetSecret(scopeId, "some-key", oldSecret) |> Async.Ignore

            let! warm = reader.GetSecret(scopeId, "some-key")
            Expect.equal warm (Some oldSecret) "reader caches the original value"

            // A write from the OTHER instance. The reader's cache has no
            // TTL, so without invalidation it would never see this.
            do! writer.SetSecret(scopeId, "some-key", newSecret) |> Async.Ignore

            let! stillStale = reader.GetSecret(scopeId, "some-key")
            Expect.equal stillStale (Some oldSecret) "CONTROL — the reader is genuinely stale before invalidation"

            match box reader with
            | :? ISecretCacheInvalidation as invalidation -> invalidation.InvalidateScope scopeId
            | _ ->
                failtest
                    "FileSecretStore no longer implements ISecretCacheInvalidation — the fanout has no eviction target"

            let! fresh = reader.GetSecret(scopeId, "some-key")
            Expect.equal fresh (Some newSecret) "after invalidation the reader re-reads the durable value"
        }

        test "secretCacheInvalidator over a NON-caching store is a harmless no-op" {
            // A cloud store that round-trips the vault per call has nothing
            // to invalidate. The seam must degrade to `ignore`, not throw.
            let nonCaching =
                { new ISecretStore with
                    member _.GetSecret(_, _) = async { return None }
                    member _.SetSecret(_, _, _) = async { return Ok() }
                    member _.DeleteSecret(_, _) = async { return Ok() }
                    member _.ListKeys(_) = async { return [] }
                }

            let invalidator = WebhookRegistry.secretCacheInvalidator nonCaching

            let envelope: WebhookSecretRotatedEnvelope = {
                ScopeId = "team-acme"
                SubscriptionId = Guid.NewGuid()
                CurrentSecretRef = "cur"
                PreviousSecretRef = "prev"
                GraceExpiresAt = DateTime.UtcNow
                RotatedAt = DateTimeOffset.UtcNow
                OriginReplicaId = "pod-1/1"
            }

            invalidator envelope
        }
    ]

// ── The preflight gate over the two configurations above ──
//
// The 464 ship report recorded that compose reported a THROWN subscribe
// (an `Error` log plus a degraded-capability entry) and said nothing
// about the two configurations where the subscription succeeds and still
// reaches no sibling — unwired, and wired to an in-process channel.
// `WebhookSecretRotationFanoutValidator` (tidy-drain 2026-08-26) fails
// those closed under a declared multi-replica topology, the shape the
// Phase 458 crypto-shred gate already had.
//
// Every arm here is paired with the configuration that must NOT fire, so
// a validator that returned `Error` unconditionally would fail this list
// rather than pass it.

let private preflightGateTests =
    let services (channel: INotificationChannel option) (registry: IWebhookRegistry option) =
        let sc = Microsoft.Extensions.DependencyInjection.ServiceCollection()

        match channel with
        | Some c ->
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<
                INotificationChannel
             >(
                sc,
                c
            )
            |> ignore
        | None -> ()

        match registry with
        | Some r ->
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IWebhookRegistry>(
                sc,
                r
            )
            |> ignore
        | None -> ()

        sc :> Microsoft.Extensions.DependencyInjection.IServiceCollection

    let validate config sc = async {
        let v =
            WebhookSecretRotationFanoutValidator.WebhookSecretRotationFanoutValidator(config, sc)
            :> ConfigValidation.IConfigValidator

        return! v.Validate()
    }

    let multiReplica = {
        ServerConfig.defaults with
            ReplicaCount = 3
    }

    let wiredRegistry (channel: INotificationChannel) = async {
        let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
        let registry = WebhookRegistry.BlobWebhookRegistry(blobs)
        let secrets = storeOver (newTempDir ())
        do! registry.WireToChannel(channel, secrets)
        return registry
    }

    testList "preflight — rotation fanout gate" [

        testCaseAsync "an UNWIRED registry under declared multi-instance is refused"
        <| async {
            let blobs = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
            let registry = WebhookRegistry.BlobWebhookRegistry(blobs)
            Expect.isFalse registry.IsWiredToChannel "precondition: the registry is unwired"

            let sc =
                services
                    (Some(InMemoryNotificationChannel(None) :> INotificationChannel))
                    (Some(registry :> IWebhookRegistry))

            match! validate multiReplica sc with
            | ConfigValidation.Error msg ->
                Expect.stringContains msg "WireToChannel" "the refusal names the missing wiring call"
            | other -> failtestf "expected Error for an unwired registry under 3 replicas, got %A" other
        }

        testCaseAsync "a registry wired to an IN-PROCESS channel under declared multi-instance is refused"
        <| async {
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let! registry = wiredRegistry channel
            let sc = services (Some channel) (Some(registry :> IWebhookRegistry))

            match! validate multiReplica sc with
            | ConfigValidation.Error msg ->
                Expect.stringContains msg "in-process" "the refusal names the channel as the cause"
            | other -> failtestf "expected Error for an in-process channel under 3 replicas, got %A" other
        }

        // The two arms that must stay silent. Without them an
        // unconditional `Error` would pass the two above.
        testCaseAsync "the SAME in-process configuration on ONE replica is Ok — there is no sibling to reach"
        <| async {
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let! registry = wiredRegistry channel
            let sc = services (Some channel) (Some(registry :> IWebhookRegistry))

            match! validate ServerConfig.defaults sc with
            | ConfigValidation.Ok -> ()
            | other -> failtestf "expected Ok on a single replica, got %A" other
        }

        testCaseAsync "no webhook registry composed is Ok whatever the replica count"
        <| async {
            let sc =
                services (Some(InMemoryNotificationChannel(None) :> INotificationChannel)) None

            match! validate multiReplica sc with
            | ConfigValidation.Ok -> ()
            | other -> failtestf "expected Ok with no webhook subsystem composed, got %A" other
        }
    ]

[<Tests>]
let tests =
    testList "Phase 464 — webhook signing-secret rotation broadcast" [
        fanoutTests
        unwiredTests
        envelopeTests
        preflightGateTests
    ]