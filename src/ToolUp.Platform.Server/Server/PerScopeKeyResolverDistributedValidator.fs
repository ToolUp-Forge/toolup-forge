module ToolUp.Platform.PerScopeKeyResolverDistributedValidator

open System
open ToolUp.Platform
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.ConfigValidation

// ─── Gap #1 — PerScopeKeyResolver multi-instance cache coherence ────
//
// `PerScopeKeyResolver` caches per-scope encryption keys with a
// 5-minute sliding TTL. On `DestroyKey` (the Phase 22 crypto-shred
// path), the resolver evicts its LOCAL cache and publishes a
// `CustomNotification(KeyDestroyedNotificationKey, scopeId)` on the
// platform-reserved scope so other silos can evict their own caches
// synchronously.
//
// Distributed `INotificationChannel` (Redis pub/sub via
// `src/NotificationChannels/Redis/`, NATS in future) bridges the
// publish across silos. The in-process default
// (`InMemoryNotificationChannel`) does NOT — its publish only reaches
// subscribers inside the SAME process. So when:
//
//   * `IBlobEncryptionKeyResolver = PerScopeKeyResolver`, AND
//   * `TOOLUP_REPLICA_COUNT > 1` (operator-declared multi-instance),
//     AND
//   * `TOOLUP_NOTIFICATION_CHANNEL` is unset or `inprocess` (default),
//
// the cross-silo eviction path is non-functional. A `DestroyKey` on
// silo A only evicts A's cache; silos B, C… continue to decrypt the
// destroyed scope's blobs until their 5-minute sliding TTL elapses.
// This violates the Phase 22 crypto-shred contract for GDPR
// right-to-erasure / contract-termination workflows.
//
// This validator refuses startup with `Error` for that combination.
// Escape hatch: switch `TOOLUP_NOTIFICATION_CHANNEL=redis` (or
// equivalent distributed companion) — same env var Phase 6l.F's
// JobSchedulerInstanceValidator points at.

[<Literal>]
let private ReplicaCountEnvVar = "TOOLUP_REPLICA_COUNT"

[<Literal>]
let private NotificationChannelEnvVar = "TOOLUP_NOTIFICATION_CHANNEL"

let private envValue (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some(v.Trim())

let private replicaCount () =
    match envValue ReplicaCountEnvVar with
    | None -> 1
    | Some v ->
        match Int32.TryParse v with
        | true, n when n > 0 -> n
        | _ -> 1

let private isDistributedChannel () =
    match envValue NotificationChannelEnvVar |> Option.map _.ToLowerInvariant() with
    | Some "inprocess"
    | None -> false
    | Some _ -> true

/// Gap #1 — config validator that refuses startup when
/// `PerScopeKeyResolver` is registered, the operator declares
/// multi-instance via `TOOLUP_REPLICA_COUNT > 1`, AND the
/// notification channel is the in-process default (which can't bridge
/// the destruction-broadcast across silos). Crypto-shredding is
/// silently incoherent in that combination.
type PerScopeKeyResolverDistributedValidator
    (encryptionKeyResolver: IBlobEncryptionKeyResolver option, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    // A crypto-shred key resolver under multi-instance is a
    // cross-instance key-state hole — security-class, so it runs even under SkipPreflight.
    interface ISecurityClassValidator

    interface IConfigValidator with
        member _.Name = "per-scope-key-resolver-distributed"
        member _.Timeout = timeout

        member _.Validate() = async {
            let isPerScope =
                match encryptionKeyResolver with
                | Some r -> r.GetType() = typeof<PerScopeKeyResolver.PerScopeKeyResolver>
                | None -> false

            let multiInstance = replicaCount () > 1
            let inProcessChannel = not (isDistributedChannel ())

            if isPerScope && multiInstance && inProcessChannel then
                return
                    Error(
                        sprintf
                            "PerScopeKeyResolver is registered with TOOLUP_REPLICA_COUNT=%d (multi-instance) but TOOLUP_NOTIFICATION_CHANNEL is unset or 'inprocess' — the cross-silo cache-invalidation path that DestroyKey relies on is non-functional. A DestroyKey call on one silo evicts only that silo's cache; other silos continue to decrypt the destroyed scope's blobs until their 5-minute sliding TTL elapses, violating the Phase 22 crypto-shred contract for GDPR right-to-erasure / contract-termination workflows. Switch to a distributed channel: TOOLUP_NOTIFICATION_CHANNEL=redis + TOOLUP_REDIS_CONNECTION=<conn-string> (the Redis companion at src/NotificationChannels/Redis/ ships today). This is the same env var Phase 6l.F's job-scheduler-instance validator names. No escape hatch — there's no legitimate single-process equivalent for the cross-silo broadcast."
                            (replicaCount ())
                    )
            else
                return Ok
        }