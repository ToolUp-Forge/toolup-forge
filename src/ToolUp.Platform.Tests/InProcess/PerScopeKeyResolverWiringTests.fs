module ToolUp.Platform.Tests.InProcess.PerScopeKeyResolverWiringTests

open System
open System.Collections.Concurrent
open System.IO
open Expecto
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Secrets

// ─── Phase 458 — crypto-shred fanout wiring: optional on one replica, ──
//     required on more, and enforced rather than assumed
//
// Phase 22 made `WireToChannel` optional and Phase 22b made the broadcast
// typed and audited, but the requirement itself was still a convention:
// `channel` starts as `None`, a `DestroyKey` with no channel published
// nothing and said nothing, and the one hard-Error guard over the
// crypto-shred contract read the replica count from
// `TOOLUP_REPLICA_COUNT` — the environment — while its six sibling
// topology validators read `config.ReplicaCount`. A deployment that
// declared `{ config with ReplicaCount = 3 }` in code therefore tripped
// every other topology validator and silently skipped the security one.
//
// Three properties are pinned here, and each has a control case beside it
// so a green result is evidence of the mechanism rather than of a probe
// that cannot fail:
//
//   1. An unwired `DestroyKey` emits a security-class warning naming the
//      staleness window (control: a WIRED destroy emits none, so the
//      assertion is about the unwired path, not about destroys).
//   2. Declared multi-instance + an unwired-or-in-process channel refuses
//      startup — from the CONFIG field with the environment untouched,
//      which is the fix (control: the same composition at
//      `ReplicaCount = 1` boots, so the Error is about the declaration).
//   3. The wired/unwired state is readable at `/dev/inspect` without
//      reading compose source.

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-458-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private newSecretStore () : ISecretStore =
    FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> ISecretStore

let private newCache () : IMemoryCache =
    new MemoryCache(MemoryCacheOptions()) :> IMemoryCache

let private scopeId = "offboarded-tenant"

/// Recording `ILogger`. Only `Warn` is asserted on; the others are
/// captured too so a message routed to the wrong level fails loudly
/// instead of reading as "nothing was logged".
type private RecordingLogger() =
    let warnings = ConcurrentQueue<string>()
    let others = ConcurrentQueue<string>()

    member _.Warnings = warnings |> List.ofSeq
    member _.Others = others |> List.ofSeq

    interface ILogger with
        member _.Debug m = others.Enqueue m
        member _.Info m = others.Enqueue m
        member _.Warn m = warnings.Enqueue m
        member _.Error(m, _) = others.Enqueue m

/// A channel this SDK has never heard of — the stand-in for a real
/// distributed companion (Redis, NATS). The validator treats an
/// unrecognised channel type as distributed, which is the arm that must
/// stay quiet.
type private FakeDistributedChannel() =
    interface INotificationChannel with
        member _.Publish(_, _) = async { return () }
        member _.Subscribe(_, _) = async { return Guid.NewGuid() }
        member _.Unsubscribe _ = async { return () }

/// A `ServerConfig` declaring `n` replicas and nothing else unusual.
let private configWithReplicas (n: int) = {
    ServerConfig.defaults with
        ReplicaCount = n
}

/// `IServiceCollection` carrying the composed channel instance, in the
/// shape `compose` registers it (a singleton INSTANCE — the validator
/// reads `ImplementationInstance`, and a factory registration is
/// deliberately not inspectable).
let private servicesWithChannel (channel: INotificationChannel option) : IServiceCollection =
    let services = ServiceCollection() :> IServiceCollection

    match channel with
    | Some c -> services.AddSingleton<INotificationChannel>(c) |> ignore
    | None -> ()

    services

let private validate (v: IConfigValidator) = v.Validate()

let private validator
    (config: ServerConfig)
    (resolver: IBlobEncryptionKeyResolver option)
    (services: IServiceCollection)
    : IConfigValidator =
    PerScopeKeyResolverDistributedValidator.PerScopeKeyResolverDistributedValidator(config, resolver, services)
    :> IConfigValidator

/// `TOOLUP_REPLICA_COUNT` is read as a secondary declaration source, so
/// the config-driven cases must run with it demonstrably unset — else a
/// stray value on the machine would be doing the work the fix is supposed
/// to be doing. Restores whatever was there.
let private withoutReplicaCountEnv (body: unit -> Async<'T>) : Async<'T> = async {
    let saved = Environment.GetEnvironmentVariable "TOOLUP_REPLICA_COUNT"
    Environment.SetEnvironmentVariable("TOOLUP_REPLICA_COUNT", null)

    try
        return! body ()
    finally
        Environment.SetEnvironmentVariable("TOOLUP_REPLICA_COUNT", saved)
}

// ── Task A — the unwired destroy is no longer silent ──

let unwiredDestroyTests =
    testList "Phase 458 unwired DestroyKey" [

        testCaseAsync "an unwired destroy logs a security-class warning naming the staleness window"
        <| async {
            let logger = RecordingLogger()
            use cache = newCache ()

            let resolver =
                PerScopeKeyResolver.createWithLogger (newSecretStore ()) cache None (logger :> ILogger)

            // Create the key first, so the destroy is a real shred rather
            // than a delete of nothing.
            let! _ =
                (resolver :> IBlobEncryptionKeyResolver).ResolveKey {
                    ScopeId = scopeId
                    Container = "team-" + scopeId
                    Persist = true
                }

            let! result = resolver.DestroyKey(scopeId, "admin@example.com")

            Expect.isOk result "the shred itself still succeeds — the warning is additive, not a failure"

            Expect.hasLength logger.Warnings 1 "exactly one warning for the first unwired destroy"

            let warning = logger.Warnings |> List.head

            Expect.stringContains
                warning
                "crypto_shred_unwired_destroy"
                "the warning carries a greppable event id for log-based alerting"

            Expect.stringContains warning "class=security" "classified security, not an ordinary operational warning"

            Expect.stringContains
                warning
                "5-minute sliding TTL"
                "names the window a sibling replica keeps decrypting for — the whole point of the warning"

            Expect.stringContains warning scopeId "names the affected scope"

            Expect.stringContains warning "WireToChannel" "names the remedy, so the operator does not have to find it"
        }

        testCaseAsync "the warning fires once per process; every unwired destroy is still counted"
        <| async {
            let logger = RecordingLogger()
            use cache = newCache ()

            let resolver =
                PerScopeKeyResolver.createWithLogger (newSecretStore ()) cache None (logger :> ILogger)

            let! _ = resolver.DestroyKey("tenant-a", "admin@example.com")
            let! _ = resolver.DestroyKey("tenant-b", "admin@example.com")
            let! _ = resolver.DestroyKey("tenant-c", "admin@example.com")

            Expect.hasLength
                logger.Warnings
                1
                "logged once — a shred loop over 500 offboarded tenants must not emit 500 identical warnings"

            Expect.equal
                resolver.UnwiredDestroyKeyCount
                3
                "the COUNT is the per-destroy record; it is what /dev/inspect surfaces, and it needs no logger to be true"
        }

        testCaseAsync "CONTROL — a wired destroy logs no warning and counts nothing"
        <| async {
            // Without this case, the assertions above could be satisfied by
            // a warning emitted on every destroy regardless of wiring.
            let logger = RecordingLogger()
            use cache = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel

            let resolver =
                PerScopeKeyResolver.createWithLogger (newSecretStore ()) cache None (logger :> ILogger)

            do! resolver.WireToChannel channel
            let! _ = resolver.DestroyKey(scopeId, "admin@example.com")

            Expect.isEmpty logger.Warnings "a wired resolver publishes the broadcast, so there is nothing to warn about"
            Expect.equal resolver.UnwiredDestroyKeyCount 0 "nothing unwired happened"
        }

        testCaseAsync "a logger-less resolver still counts unwired destroys (GP 11 — the 3-arg ctor is unchanged)"
        <| async {
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let! _ = resolver.DestroyKey(scopeId, "admin@example.com")

            Expect.isFalse resolver.IsWiredToChannel "no channel was wired"

            Expect.equal
                resolver.UnwiredDestroyKeyCount
                1
                "counted without a logger — the panel is the fallback surface"
        }
    ]

// ── Task B — the preflight guard, read from CONFIG ──

let wiringValidatorTests =
    testList "Phase 458 per-scope-key-resolver-distributed validator" [

        testCaseAsync "ReplicaCount > 1 from CONFIG + active resolver + no WireToChannel → Error naming the remedy"
        <| withoutReplicaCountEnv (fun () -> async {
            // The defect this closes: the replica count was read from the
            // ENVIRONMENT only, so this exact deployment — declared
            // multi-instance in code — skipped the one hard-Error guard
            // over the crypto-shred contract.
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let v =
                validator
                    (configWithReplicas 3)
                    (Some(resolver :> IBlobEncryptionKeyResolver))
                    (servicesWithChannel None)

            match! validate v with
            | Error msg ->
                Expect.stringContains
                    msg
                    "WireToChannel was never invoked"
                    "names what is missing, not just that something is"

                Expect.stringContains msg "3" "quotes the declared replica count back"
                Expect.stringContains msg "ServerConfig.ReplicaCount" "names the field that declared it"
                Expect.stringContains msg "withEncryptedBlobStorage" "names the compose path that wires it for you"
            | other ->
                failtestf "expected a hard Error for declared multi-instance with an unwired resolver, got %A" other
        })

        testCaseAsync "CONTROL — the same composition at ReplicaCount = 1 boots (single-instance wiring stays optional)"
        <| withoutReplicaCountEnv (fun () -> async {
            // Same resolver, same absent channel, same absent env var —
            // only the declaration differs. If this failed, the case above
            // would be evidence of nothing more than "PerScopeKeyResolver
            // present".
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let v =
                validator
                    (configWithReplicas 1)
                    (Some(resolver :> IBlobEncryptionKeyResolver))
                    (servicesWithChannel None)

            let! result = validate v
            Expect.equal result Ok "there is no sibling cache to evict on one replica, so the fanout is a no-op"
        })

        testCaseAsync "ReplicaCount > 1 + wired to the IN-PROCESS channel → Error naming the channel"
        <| withoutReplicaCountEnv (fun () -> async {
            use cache = newCache ()
            let channel = InMemoryNotificationChannel(None) :> INotificationChannel
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None
            do! resolver.WireToChannel channel

            let v =
                validator
                    (configWithReplicas 3)
                    (Some(resolver :> IBlobEncryptionKeyResolver))
                    (servicesWithChannel (Some channel))

            match! validate v with
            | Error msg ->
                Expect.stringContains msg "in-process" "names the reason the wiring does not help"
                Expect.stringContains msg "RedisNotifications" "names a companion that ships today"
            | other -> failtestf "expected a hard Error for a wired-but-in-process channel, got %A" other
        })

        testCaseAsync "the NoOp channel counts as in-process too (it delivers to nobody at all)"
        <| withoutReplicaCountEnv (fun () -> async {
            use cache = newCache ()
            let channel = NoOpNotificationChannel() :> INotificationChannel
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None
            do! resolver.WireToChannel channel

            let v =
                validator
                    (configWithReplicas 2)
                    (Some(resolver :> IBlobEncryptionKeyResolver))
                    (servicesWithChannel (Some channel))

            match! validate v with
            | Error _ -> ()
            | other -> failtestf "expected a hard Error — NoOp reaches no replica, including this one, got %A" other
        })

        testCaseAsync "ReplicaCount > 1 + wired to a DISTRIBUTED channel → Ok (the correct deployment boots)"
        <| withoutReplicaCountEnv (fun () -> async {
            use cache = newCache ()
            let channel = FakeDistributedChannel() :> INotificationChannel
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None
            do! resolver.WireToChannel channel

            let v =
                validator
                    (configWithReplicas 5)
                    (Some(resolver :> IBlobEncryptionKeyResolver))
                    (servicesWithChannel (Some channel))

            let! result = validate v
            Expect.equal result Ok "a properly-configured scale-out deployment must not be refused"
        })

        testCaseAsync "TOOLUP_REPLICA_COUNT alone still declares multi-instance (the pre-458 route is not lost)"
        <| async {
            let saved = Environment.GetEnvironmentVariable "TOOLUP_REPLICA_COUNT"
            Environment.SetEnvironmentVariable("TOOLUP_REPLICA_COUNT", "4")

            try
                use cache = newCache ()
                let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

                // Config says one replica; only the env var says otherwise.
                let v =
                    validator
                        ServerConfig.defaults
                        (Some(resolver :> IBlobEncryptionKeyResolver))
                        (servicesWithChannel None)

                match! validate v with
                | Error msg -> Expect.stringContains msg "4" "the env-declared count is the one reported"
                | other -> failtestf "expected the env declaration to still gate, got %A" other
            finally
                Environment.SetEnvironmentVariable("TOOLUP_REPLICA_COUNT", saved)
        }

        testCaseAsync "no encryption resolver at all → Ok whatever the replica count"
        <| withoutReplicaCountEnv (fun () -> async {
            let v = validator (configWithReplicas 9) None (servicesWithChannel None)
            let! result = validate v
            Expect.equal result Ok "a deployment with no envelope encryption has no crypto-shred contract to break"
        })

        testCaseAsync "a non-per-scope resolver → Ok (SingleKeyResolver has no DestroyKey path)"
        <| withoutReplicaCountEnv (fun () -> async {
            let resolver = SingleKeyResolver.create (newSecretStore ())
            let v = validator (configWithReplicas 3) (Some resolver) (servicesWithChannel None)

            let! result = validate v
            Expect.equal result Ok "no per-scope key, no per-scope shred, nothing to fan out"
        })

        testCase "the validator is security-class, so SkipPreflight cannot bypass it"
        <| fun _ ->
            let v =
                PerScopeKeyResolverDistributedValidator.PerScopeKeyResolverDistributedValidator(
                    ServerConfig.defaults,
                    None,
                    ServiceCollection()
                )

            Expect.isTrue
                (box v :? ISecurityClassValidator)
                "a cross-replica key-state hole must not be bypassable by one boolean"
    ]

// ── Task C — the wiring state is operator-discoverable ──

let wiringDiagnosticsTests =
    testList "Phase 458 crypto-shred fanout dev panel" [

        testCaseAsync "the panel reports unwired before WireToChannel and wired after"
        <| async {
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let contributor =
                PerScopeKeyResolver.CryptoShredFanoutContributor(resolver) :> IDevDiagnosticsContributor

            let! (panelBefore, payloadBefore) = contributor.Contribute()

            Expect.equal panelBefore "Crypto-shred fanout" "stable panel name — operators grep for it"

            let before = payloadBefore :?> PerScopeKeyResolver.CryptoShredFanoutStatus

            Expect.isFalse before.WiredToChannel "unwired before compose calls WireToChannel"

            Expect.equal
                before.UnwiredStalenessWindowMinutes
                5.0
                "the panel states the window so the operator need not know the resolver's TTL"

            Expect.equal before.UnwiredDestroyKeyCalls 0 "nothing shredded yet"

            do! resolver.WireToChannel(InMemoryNotificationChannel(None) :> INotificationChannel)

            let! (_, payloadAfter) = contributor.Contribute()
            let after = payloadAfter :?> PerScopeKeyResolver.CryptoShredFanoutStatus

            Expect.isTrue after.WiredToChannel "the panel is read live, not snapshotted at construction"
        }

        testCaseAsync "the panel surfaces unwired destroys that already happened"
        <| async {
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let contributor =
                PerScopeKeyResolver.CryptoShredFanoutContributor(resolver) :> IDevDiagnosticsContributor

            let! _ = resolver.DestroyKey("tenant-a", "admin@example.com")
            let! _ = resolver.DestroyKey("tenant-b", "admin@example.com")

            let! (_, payload) = contributor.Contribute()
            let status = payload :?> PerScopeKeyResolver.CryptoShredFanoutStatus

            Expect.equal
                status.UnwiredDestroyKeyCalls
                2
                "two tenants were shredded with no broadcast — a gap that already occurred, not a warning about one that might"

            Expect.equal status.Resolver "PerScopeKeyResolver" "names the resolver the panel describes"
        }

        testCaseAsync "compose registers the panel for a per-scope resolver and not for a single-key one"
        <| async {
            use cache = newCache ()
            let perScope = PerScopeKeyResolver.create (newSecretStore ()) cache None

            let withPerScope = ServiceCollection() :> IServiceCollection

            ComposeEncryption.registerEncryptionResolver withPerScope (Some(perScope :> IBlobEncryptionKeyResolver))

            let contributorCount (services: IServiceCollection) =
                services
                |> Seq.filter (fun d -> d.ServiceType = typeof<IDevDiagnosticsContributor>)
                |> Seq.length

            Expect.equal (contributorCount withPerScope) 1 "the crypto-shred panel is registered by compose"

            let withSingleKey = ServiceCollection() :> IServiceCollection
            let single = SingleKeyResolver.create (newSecretStore ())
            ComposeEncryption.registerEncryptionResolver withSingleKey (Some single)

            Expect.equal
                (contributorCount withSingleKey)
                0
                "no DestroyKey, no fanout, no panel — a deployment pays nothing for what it cannot use (GP 13)"

            let withNothing = ServiceCollection() :> IServiceCollection
            ComposeEncryption.registerEncryptionResolver withNothing None
            Expect.equal (contributorCount withNothing) 0 "no encryption at all, no panel"
        }
    ]