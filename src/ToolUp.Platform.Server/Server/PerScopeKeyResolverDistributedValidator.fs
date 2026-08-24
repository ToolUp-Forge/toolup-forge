module ToolUp.Platform.PerScopeKeyResolverDistributedValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.ConfigValidation

// ─── Gap #1 / Phase 458 — PerScopeKeyResolver multi-instance cache coherence ──
//
// `PerScopeKeyResolver` caches per-scope encryption keys with a
// 5-minute sliding TTL. On `DestroyKey` (the Phase 22 crypto-shred
// path), the resolver evicts its LOCAL cache, deletes the persisted
// secret, and publishes a `KeyDestroyed` envelope on the
// platform-reserved scope so every OTHER replica evicts its own cached
// copy (Phase 22b) and records an acknowledgement.
//
// Two things have to hold for that fanout to reach a sibling replica,
// and this validator refuses startup with a security-class `Error` when
// a MULTI-INSTANCE deployment satisfies neither:
//
//   1. **The resolver is wired at all.** `channel` starts as `None`;
//      `WireToChannel` is what turns the publish on. Compose calls it
//      for every composed `PerScopeKeyResolver`, so an unwired resolver
//      means one built outside compose.
//   2. **The wired channel can leave the process.** Distributed
//      `INotificationChannel` companions (Redis pub/sub via
//      `src/NotificationChannels/Redis/`, NATS in future) bridge the
//      publish across silos. The in-process default
//      (`InMemoryNotificationChannel`) does not — its publish only
//      reaches subscribers inside the SAME process — and
//      `NoOpNotificationChannel` reaches nobody at all.
//
// Either failure has the same consequence: a `DestroyKey` on silo A
// evicts only A's cache, and silos B, C… keep decrypting the destroyed
// scope's blobs until their 5-minute sliding TTL elapses. That violates
// the Phase 22 crypto-shred contract for GDPR right-to-erasure /
// contract-termination workflows.
//
// ── Phase 458: read the replica count from CONFIG, not only the env ──
//
// This validator read `TOOLUP_REPLICA_COUNT` from the environment while
// its six sibling topology validators (`JobSchedulerInstanceValidator`,
// `IdempotencyStoreInstanceValidator`, `SessionFileStoreInstanceValidator`,
// `MultiInstanceAdminCoherenceValidator`,
// `AICancellationDispatchInstanceValidator`,
// `ShareTokenRateLimiterDistributionValidator`) all read
// `config.ReplicaCount`. The env var is only ONE of the two ways that
// field is populated — `ServerConfig.fromEnv` parses it, but a
// deployment that sets `{ config with ReplicaCount = 3 }` in code never
// touches the environment. Such a deployment tripped every other
// topology validator and silently skipped this one: the single
// HARD-ERROR guard in the set, protecting the crypto-shred contract,
// was the one that could be bypassed by configuring in the ordinary
// way. Now `max config.ReplicaCount <env>` — either declaration counts,
// and a stale env var can only ever raise the count, never lower a
// declared one.
//
// ── Why the channel probe reads DI, with an env fallback ──
//
// The composed channel can diverge from what config says: it is
// resolved through `NotificationChannel.fromEnv`, and a missing
// connection string falls back to in-process. Inspecting the live
// `IServiceCollection` (the shape `DeployPlaneDepsValidator` and
// `KeyDestroyAckCoverageValidator` use) measures the composed reality.
// When no channel instance is registered — a bespoke composition, a
// factory registration — there is nothing to inspect, and the validator
// falls back to the `TOOLUP_NOTIFICATION_CHANNEL` reading it used
// before, so no deployment loses a check it previously had.
//
// ── Relationship to `KeyDestroyAckCoverageValidator` (Phase 22b) ──
//
// Neither subsumes the other, and the split is by DECLARATION, not by
// mechanism: this validator fails closed when the operator has DECLARED
// more than one replica (config or env), the ack-coverage validator
// warns on the deployment SHAPE that usually implies several
// (`Team` / `MultiTeam`) but has not declared it, where a hard abort
// would false-positive a legitimate single-replica Team deployment.
// Both classify "in-process channel" through the same helper below so
// the Error arm and the Warning arm can never disagree about what the
// composed channel is.
//
// Escape hatch: none. There is no legitimate single-process equivalent
// of a cross-silo broadcast, and the thing being protected is a
// tenant's erasure guarantee.

[<Literal>]
let private ReplicaCountEnvVar = ConfigKeys.Names.replicaCount

[<Literal>]
let private NotificationChannelEnvVar = ConfigKeys.Names.notificationChannel

// Phase 698 — both keys resolve through the Phase-696 `ConfigResolution`
// seam. This validator gates a tenant's crypto-erasure guarantee on the
// declared topology, so reading a different value from the one the
// deployment actually runs on is the failure that matters here.
let private envReplicaCount () =
    match ConfigResolution.tryValue ReplicaCountEnvVar |> Option.map _.Trim() with
    | None -> 1
    | Some v ->
        match Int32.TryParse v with
        | true, n when n > 0 -> n
        | _ -> 1

/// The declared replica count: the greater of the `ServerConfig` field
/// and the environment variable. Config is the primary source (every
/// sibling topology validator reads it); the env var is retained because
/// it is what this validator gated on before Phase 458 and a deployment
/// may still be setting only it.
let internal declaredReplicaCount (config: ServerConfig) =
    max config.ReplicaCount (envReplicaCount ())

let private isDistributedChannelEnv () =
    match
        ConfigResolution.tryValue NotificationChannelEnvVar
        |> Option.map (fun v -> v.Trim().ToLowerInvariant())
    with
    | Some "inprocess"
    | None -> false
    | Some _ -> true

/// Registered implementation instance for `'T`, when compose registered
/// one as a singleton instance. `None` covers both "not registered" and
/// "registered as a factory / open generic", neither of which is
/// inspectable without building the container (which would create
/// different singletons than the runtime one).
let internal registeredInstance<'T> (services: IServiceCollection) : obj option =
    services
    |> Seq.tryPick (fun d ->
        if
            not (isNull d.ServiceType)
            && d.ServiceType = typeof<'T>
            && not (isNull d.ImplementationInstance)
        then
            Some d.ImplementationInstance
        else
            None)

/// `true` when the composed `INotificationChannel` cannot cross a process
/// boundary. Both shipped in-process implementations count: the in-memory
/// channel delivers to same-process subscribers only, and the no-op
/// channel delivers to nobody at all.
///
/// An UNRECOGNISED channel type is treated as distributed — a companion
/// this SDK has never heard of is far more likely to be a real pub/sub
/// backend than a third in-process variant, and a false abort aimed at a
/// correctly-configured deployment is worse than the check it replaces.
/// `None` means no channel instance was registered, so the composed
/// reality is not inspectable and the caller falls back to config/env.
///
/// Shared with `KeyDestroyAckCoverageValidator` (Phase 22b) deliberately:
/// the hard-Error arm and the Warning arm must classify a channel
/// identically, or one preflight line contradicts the other.
let internal composedChannelIsInProcess (services: IServiceCollection) : bool option =
    match registeredInstance<INotificationChannel> services with
    | Some instance ->
        match instance with
        | :? NotificationChannel.InMemoryNotificationChannel
        | :? NotificationChannel.NoOpNotificationChannel -> Some true
        | _ -> Some false
    | None -> None

/// The per-scope (crypto-shredding) resolver, when that is what was
/// composed. A `SingleKeyResolver` has no `DestroyKey` path, and a custom
/// resolver owns its own coherence story.
let internal perScopeResolver
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option)
    : PerScopeKeyResolver.PerScopeKeyResolver option =
    match encryptionKeyResolver with
    | Some(:? PerScopeKeyResolver.PerScopeKeyResolver as r) -> Some r
    | _ -> None

/// Gap #1 / Phase 458 — config validator that refuses startup when
/// `PerScopeKeyResolver` is active in a deployment declaring more than one
/// replica (`ServerConfig.ReplicaCount` or `TOOLUP_REPLICA_COUNT`) and the
/// destruction broadcast cannot reach a sibling — because the resolver was
/// never wired to a channel, or because the wired channel cannot leave the
/// process. Crypto-shredding is silently incoherent in either case.
type PerScopeKeyResolverDistributedValidator
    (
        config: ServerConfig,
        encryptionKeyResolver: IBlobEncryptionKeyResolver option,
        services: IServiceCollection,
        ?timeout: TimeSpan
    ) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // A crypto-shred key resolver under multi-instance is a
    // cross-instance key-state hole — security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "per-scope-key-resolver-distributed"
        member _.Timeout = timeout

        member _.Validate() = async {
            let replicas = declaredReplicaCount config

            match perScopeResolver encryptionKeyResolver with
            | None -> return Ok
            | Some _ when replicas <= 1 ->
                // Single-instance: `WireToChannel` is genuinely optional
                // — there is no sibling cache to evict, so the fanout is
                // a no-op and the shred is complete on return.
                return Ok
            | Some resolver ->
                // Wiring is the first gate: an unwired resolver publishes
                // nothing at all, whatever channel the deployment
                // configured.
                if not resolver.IsWiredToChannel then
                    return
                        Error(
                            sprintf
                                "PerScopeKeyResolver is active with %d declared replicas (ServerConfig.ReplicaCount / TOOLUP_REPLICA_COUNT), but WireToChannel was never invoked on it — the resolver holds no INotificationChannel, so DestroyKey publishes no destruction broadcast at all. A crypto-shred on one replica evicts only that replica's cache and deletes the shared secret; every OTHER replica keeps decrypting the destroyed scope's blobs from its own warm cache for up to the 5-minute sliding TTL, and no EncryptionKeyDestroyAcknowledged audit rows are recorded to reveal it. This breaks the Phase 22 crypto-shred contract for GDPR right-to-erasure / contract-termination workflows. Compose the resolver through ServerApp.withEncryptedBlobStorage — compose calls WireToChannel for every PerScopeKeyResolver it composes — or call WireToChannel yourself with the distributed channel before startup completes. Single-instance deployments (ReplicaCount = 1) do not need it and are not affected."
                                replicas
                        )
                else
                    // Wired — but a channel that cannot leave the process
                    // reaches no sibling either.
                    let inProcess =
                        composedChannelIsInProcess services
                        |> Option.defaultValue (not (isDistributedChannelEnv ()))

                    if inProcess then
                        return
                            Error(
                                sprintf
                                    "PerScopeKeyResolver is active with %d declared replicas (ServerConfig.ReplicaCount / TOOLUP_REPLICA_COUNT) but the composed INotificationChannel is in-process (InMemoryNotificationChannel / NoOpNotificationChannel, or TOOLUP_NOTIFICATION_CHANNEL unset / 'inprocess') — the cross-silo cache-invalidation path that DestroyKey relies on is non-functional. A DestroyKey call on one silo evicts only that silo's cache; other silos continue to decrypt the destroyed scope's blobs until their 5-minute sliding TTL elapses, violating the Phase 22 crypto-shred contract for GDPR right-to-erasure / contract-termination workflows. Switch to a distributed channel: ServerConfig.Notifications = RedisNotifications \"<connection-string>\" (or TOOLUP_NOTIFICATION_CHANNEL=redis + TOOLUP_REDIS_CONNECTION=<conn-string>) — the Redis companion at src/NotificationChannels/Redis/ ships today. This is the same env var Phase 6l.F's job-scheduler-instance validator names. No escape hatch — there's no legitimate single-process equivalent for the cross-silo broadcast. Confirm the wiring afterwards on the /dev/inspect \"Crypto-shred fanout\" panel."
                                    replicas
                            )
                    else
                        return Ok
        }