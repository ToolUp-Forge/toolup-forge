module ToolUp.Platform.WebhookSecretRotationFanoutValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 464 tail — webhook signing-secret rotation across instances ──
//
// `BlobWebhookRegistry.RotateSecret` publishes a
// `_platform.webhooks.secret-rotated` envelope so every OTHER instance
// drops its cached signing material for the named scope. Two things have
// to hold for that to reach a sibling, and they are the same two the
// crypto-shred fanout needs:
//
//   1. **The registry is wired at all.** `WireToChannel` turns the
//      subscription on; compose calls it
//      (`ComposeJobs.wireWebhookRegistryToNotificationChannel`).
//   2. **The wired channel can leave the process.** The in-process default
//      delivers only to same-process subscribers; `NoOpNotificationChannel`
//      delivers to nobody.
//
// Until 2026-08-26 neither was gated: the compose step logged an `Error`
// and registered a degraded-capability entry when the SUBSCRIBE THREW, and
// said nothing at all about the two configurations where the subscription
// succeeds and still reaches no sibling. That is warn-and-count where the
// crypto-shred path fails closed (`PerScopeKeyResolverDistributedValidator`,
// Phase 458), and the asymmetry was not a decision — it was a gap the 464
// ship report recorded and left outside its lease.
//
// **Why an Error rather than another warning.** A rotation is normally the
// response to a secret believed compromised. If siblings keep signing with
// the superseded value, the operator has performed the remediation, seen it
// succeed, and is still exposed — and the failure is silent in the worst
// direction, because the ROTATING instance's own deliveries verify
// correctly. The receiver's view is the mirror image: deliveries signed
// with the old secret are rejected as INAUTHENTIC, not as stale, so the
// symptom presents as an attack rather than as a config gap. A caching
// `ISecretStore` has no TTL, so unlike the crypto-shred window this never
// self-heals; it persists until the sibling process restarts.
//
// **Scope, deliberately narrow.** It fires only when the operator has
// DECLARED more than one replica (`ServerConfig.ReplicaCount` or
// `TOOLUP_REPLICA_COUNT`, via the same `declaredReplicaCount` helper 458
// uses — so the two gates can never disagree about the topology) AND the
// webhook subsystem is composed. A single-replica deployment has no
// sibling to invalidate, so its fanout is correctly a no-op and this
// validator returns `Ok` without looking at anything else (GP 13).
//
// **Not security-class.** 458's crypto-shred gate runs even under
// `SkipPreflight` because it protects a tenant's erasure guarantee, which
// no operator flag should be able to waive. This one protects a signing
// secret's retirement — serious, but it is the deployment's own secret
// rather than a third party's data, and `SkipPreflight` is an explicit
// operator act on their own deployment. Erring toward the narrower claim.

/// Registered `BlobWebhookRegistry`, when that is what compose registered.
/// A consumer's own `IWebhookRegistry` implementation owns its coherence
/// story; there is nothing here for this validator to assess.
let private blobRegistry (services: IServiceCollection) : WebhookRegistry.BlobWebhookRegistry option =
    match PerScopeKeyResolverDistributedValidator.probeRegistration<IWebhookRegistry> services with
    | PerScopeKeyResolverDistributedValidator.KnownInstance instance ->
        match instance with
        | :? WebhookRegistry.BlobWebhookRegistry as r -> Some r
        | _ -> None
    | PerScopeKeyResolverDistributedValidator.NotRegistered
    | PerScopeKeyResolverDistributedValidator.KnownType _
    | PerScopeKeyResolverDistributedValidator.Opaque -> None

/// Phase 464 tail — refuse startup when webhook signing-secret rotation
/// cannot reach a sibling instance in a deployment that declares more than
/// one replica.
type WebhookSecretRotationFanoutValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "webhook-secret-rotation-fanout"
        member _.Timeout = timeout

        member _.Validate() = async {
            let replicas = PerScopeKeyResolverDistributedValidator.declaredReplicaCount config

            match blobRegistry services with
            | None -> return Ok
            | Some _ when replicas <= 1 -> return Ok
            | Some registry ->
                if not registry.IsWiredToChannel then
                    return
                        Error(
                            sprintf
                                "The webhook registry is composed with %d declared replicas (ServerConfig.ReplicaCount / TOOLUP_REPLICA_COUNT), but WireToChannel was never invoked on it — it holds no INotificationChannel, so RotateSecret publishes no invalidation broadcast at all. Every OTHER instance keeps signing deliveries with the SUPERSEDED secret until its process restarts (a caching ISecretStore has no TTL, so the stale read never expires on its own), and a receiver updated to the new secret rejects those deliveries as INAUTHENTIC rather than as stale. Since a rotation is usually the response to a compromised secret, the remediation appears to succeed while the exposure continues. Compose the webhook subsystem through ServerApp — compose calls WireToChannel for every BlobWebhookRegistry it builds — or call WireToChannel yourself before startup completes. Single-instance deployments (ReplicaCount = 1) do not need it and are not affected."
                                replicas
                        )
                else
                    // An unclassifiable channel registration abstains rather
                    // than falling back to the env var the way 458 does. 458
                    // kept that fallback because it was the check it had
                    // BEFORE the DI probe existed, so keeping it lost nobody
                    // a check. This gate is new, and an unset
                    // TOOLUP_NOTIFICATION_CHANNEL alongside a factory that
                    // yields Redis would be a false abort on arrival — the
                    // one thing that reliably teaches operators to disable a
                    // gate. Compose registers the channel as an INSTANCE, so
                    // the abstain is reachable only from a bespoke
                    // composition root, where the sibling coverage validator
                    // now warns.
                    let inProcess =
                        PerScopeKeyResolverDistributedValidator.composedChannelIsInProcess services
                        |> Option.defaultValue false

                    if inProcess then
                        return
                            Error(
                                sprintf
                                    "The webhook registry is composed with %d declared replicas (ServerConfig.ReplicaCount / TOOLUP_REPLICA_COUNT) but the composed INotificationChannel is in-process (InMemoryNotificationChannel / NoOpNotificationChannel) — the cross-instance invalidation RotateSecret relies on reaches no sibling. Every OTHER instance keeps signing deliveries with the SUPERSEDED secret until its process restarts (a caching ISecretStore has no TTL), and a receiver updated to the new secret rejects those deliveries as INAUTHENTIC rather than as stale. Since a rotation is usually the response to a compromised secret, the remediation appears to succeed while the exposure continues. Switch to a distributed channel: ServerConfig.Notifications = RedisNotifications \"<connection-string>\" (or TOOLUP_NOTIFICATION_CHANNEL=redis + TOOLUP_REDIS_CONNECTION=<conn-string>) — the Redis companion at src/NotificationChannels/Redis/ ships today. This is the same channel the Phase 458 crypto-shred gate requires."
                                    replicas
                            )
                    else
                        return Ok
        }