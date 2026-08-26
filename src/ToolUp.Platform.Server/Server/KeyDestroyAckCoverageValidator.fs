module ToolUp.Platform.KeyDestroyAckCoverageValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.ConfigValidation

// ─── Phase 22b — crypto-shred acknowledgement coverage ───────────────
//
// `PerScopeKeyResolver.DestroyKey` publishes a `KeyDestroyedEnvelope`
// through `INotificationChannel` so every OTHER replica evicts its cached
// copy of the destroyed key and records an
// `EncryptionKeyDestroyAcknowledged` audit event. The in-process default
// channel (`InMemoryNotificationChannel`) delivers only inside the
// publishing process, so with it composed the fanout reaches nobody: a
// sibling replica keeps decrypting the shredded scope's blobs for up to
// the 5-minute sliding TTL, and the audit trail shows zero
// acknowledgements — which reads identically to "there are no other
// replicas".
//
// That ambiguity is the reason this validator exists as a SEPARATE check
// from `PerScopeKeyResolverDistributedValidator`, and the difference is
// worth stating because the two look alike at a glance:
//
//   * `PerScopeKeyResolverDistributedValidator` gates on the operator
//     DECLARING multi-instance (`ServerConfig.ReplicaCount > 1` or
//     `TOOLUP_REPLICA_COUNT > 1` — either counts since Phase 458; it read
//     only the env var before, and so skipped every deployment that set
//     the config field in code) and refuses startup with `Error`. It is
//     precise and unambiguous — the operator said there are N replicas —
//     but it is silent whenever the count is left at its default, which
//     is exactly the deployment that scales out behind a load balancer
//     without anyone revisiting the declaration.
//   * This validator gates on the deployment SHAPE instead
//     (`Team` / `MultiTeam` surfaces — a multi-tenant posture that is
//     usually horizontally scaled) and emits `Warning`. A Team
//     deployment on one replica is perfectly legitimate, so a hard abort
//     would false-positive it; the warning names the gap and the fix
//     without blocking a single-replica boot.
//
// Together: the Error arm catches the declared case, this Warning arm
// catches the undeclared-but-likely one. Neither subsumes the other.
//
// **Why DI inspection rather than the config record.** Both facts this
// validator needs are decided by what compose actually REGISTERED, and
// both can diverge from `ServerConfig`. The notification channel is
// resolved through `NotificationChannel.fromEnv`, so
// `TOOLUP_NOTIFICATION_CHANNEL` and a fallback-to-in-process on a missing
// connection string can both leave `config.Notifications` saying one
// thing while the composed channel is another; the resolver arrives as an
// instance a consumer handed to `withEncryptedBlobStorage`, not as a
// config case at all. Reading the live `IServiceCollection` — the shape
// `DeployPlaneDepsValidator` uses — measures the composed reality.
// `Validate()` runs at end-of-compose via `ConfigValidatorAggregator`, so
// every registration is present by then regardless of this validator's
// own registration order.

/// The distributed companion named as the remedy. One place so the
/// message and any future rename stay in step.
[<Literal>]
let private RecommendedCompanion =
    "RedisNotifications (ServerConfig.Notifications = RedisNotifications \"<connection-string>\", the src/NotificationChannels/Redis companion)"

/// True when the composed `INotificationChannel` cannot cross a process
/// boundary. Both shipped in-process implementations count: the in-memory
/// channel delivers to same-process subscribers only, and the no-op
/// channel delivers to nobody at all.
///
/// Phase 458 — the classification now lives once, in
/// `PerScopeKeyResolverDistributedValidator`, and both validators call it.
/// They previously carried a copy each, and a copy that drifted would make
/// the Error arm and the Warning arm contradict each other about what the
/// composed channel is — the exact confusion the two-validator split was
/// documented to avoid. An UNRECOGNISED channel type is treated as
/// distributed there (a false Warning aimed at a correctly-configured
/// deployment teaches operators to ignore the preflight); `None` — no
/// classifiable channel registration — means there is no fanout path to
/// assess *here*, and this arm abstains rather than guessing.
///
/// `None` no longer conflates two cases (2026-08-26). "Nothing registered"
/// really is nothing to assess; "registered as a factory" is a fanout path
/// whose reachability is unknown, and `composedChannelIsOpaque` gives that
/// its own arm above rather than letting it read as clean.
let private inProcessChannel (services: IServiceCollection) : bool =
    PerScopeKeyResolverDistributedValidator.composedChannelIsInProcess services
    |> Option.defaultValue false

/// True when the composed encryption-key resolver is the per-scope
/// (crypto-shredding) one. A `SingleKeyResolver` has no `DestroyKey`
/// path, and a custom resolver owns its own coherence story.
let private perScopeResolverComposed (services: IServiceCollection) : bool =
    match PerScopeKeyResolverDistributedValidator.probeRegistration<IBlobEncryptionKeyResolver> services with
    | PerScopeKeyResolverDistributedValidator.KnownInstance instance ->
        instance :? PerScopeKeyResolver.PerScopeKeyResolver
    // A typed registration declares the concrete resolver without handing
    // over an instance, and classifies just as well.
    | PerScopeKeyResolverDistributedValidator.KnownType implType ->
        typeof<PerScopeKeyResolver.PerScopeKeyResolver>.IsAssignableFrom implType
    | PerScopeKeyResolverDistributedValidator.NotRegistered
    | PerScopeKeyResolverDistributedValidator.Opaque -> false

/// Phase 22b — warn when the crypto-shred acknowledgement fanout has no
/// transport that can reach another replica, in a deployment shape whose
/// replica count is likely greater than one.
///
/// Warning (never Error): a `Team` deployment on a single replica is a
/// legitimate and common shape, and its fanout is correctly a no-op. The
/// declared-multi-instance case is the one that fails closed, in
/// `PerScopeKeyResolverDistributedValidator`.
type KeyDestroyAckCoverageValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "key-destroy-ack-coverage"
        member _.Timeout = timeout

        member _.Validate() = async {
            let teamShaped = DeploymentConfig.hasTeamScope config

            if
                teamShaped
                && perScopeResolverComposed services
                && PerScopeKeyResolverDistributedValidator.composedChannelIsOpaque services
            then
                // The channel IS registered — as a factory, the ordinary
                // shape when the connection string is itself resolved from
                // DI — so nothing about it is knowable before the container
                // is built, and building it here would create different
                // singletons from the runtime's. This arm used to be
                // silence: `inProcessChannel` defaulted an unreadable probe
                // to `false`, so a factory-registered channel produced no
                // finding of any kind (origin: Phase 458 ship report). That
                // is the wrong default for a coverage check — it is
                // strongest precisely where a deployment did something
                // unusual. Say what could not be read, and where to look.
                return
                    Warning(
                        sprintf
                            "PerScopeKeyResolver is composed in a %s deployment and an INotificationChannel IS registered, but as a FACTORY (or open generic), so preflight cannot tell whether the composed channel can leave the process. PerScopeKeyResolver.DestroyKey (the GDPR-erasure / contract-termination crypto-shred path) publishes a KeyDestroyed envelope so every OTHER replica evicts its cached key and records an EncryptionKeyDestroyAcknowledged audit event; an in-process channel makes that publish reach nobody, silently. This is NOT a finding that the channel is wrong — only that it is unreadable here. Confirm the composed channel on the /dev/inspect \"Crypto-shred fanout\" panel at runtime, or register the channel as a singleton INSTANCE so preflight can classify it. If this deployment runs more than one replica and the factory yields an in-process channel, switch to %s."
                            (DeploymentConfig.surfacesLabel config)
                            RecommendedCompanion
                    )
            elif teamShaped && perScopeResolverComposed services && inProcessChannel services then
                return
                    Warning(
                        sprintf
                            "PerScopeKeyResolver is composed in a %s deployment, but the registered INotificationChannel is in-process. PerScopeKeyResolver.DestroyKey (the GDPR-erasure / contract-termination crypto-shred path) publishes a KeyDestroyed envelope so every OTHER replica evicts its cached key and records an EncryptionKeyDestroyAcknowledged audit event — with an in-process channel that publish reaches no other replica. On a single replica this is correct and costs nothing. On more than one, a destroyed key keeps decrypting that tenant's blobs on every sibling replica for up to the resolver's 5-minute sliding TTL, and the audit trail records no acknowledgements — indistinguishable from having no siblings at all, so the gap does not announce itself. If this deployment runs more than one replica, switch to %s. If it runs exactly one, this warning is expected and can be ignored; setting ServerConfig.ReplicaCount = 1 explicitly documents that."
                            (DeploymentConfig.surfacesLabel config)
                            RecommendedCompanion
                    )
            else
                return Ok
        }