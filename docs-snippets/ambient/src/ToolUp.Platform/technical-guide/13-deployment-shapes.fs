// Ambient context for `src/ToolUp.Platform/technical-guide/13-deployment-shapes.md`.
//
// The chapter is four composition roots and four lease-usage patterns.
// The composition roots differ from each other only in `ProcessProfile`
// and the substrate they mount, so everything they share — the app's own
// module registration, the cloud storage config, the Redis channel — is
// the reader's deployment, not SDK surface, and stands in here. The
// lease patterns are excerpts from a critical section: the lock, the
// lease, the id and TTL being held, and (for the fencing pattern) the
// DOWNSTREAM store that records the highest token it has seen, which is
// necessarily the reader's own store since fencing is a property of the
// write path rather than of the lock.
open Microsoft.Extensions.DependencyInjection
open ToolUp.Storage
open ToolUp.Platform.NotificationChannels.RedisDistributedLock

[<AutoOpen>]
module PageAmbient =

    // ── The three pure-Kestrel composition roots ──────────────────────

    /// The deployment's own module, registered identically on every silo
    /// — the shapes run from the SAME publish output, so the module set
    /// never varies between them.
    module MyApp =

        module Module =

            let register () : ServerModule = failwith "ambient"

    /// Cloud substrate every multi-silo shape must share. Both are wired
    /// as substrate rather than as `ServerConfig` fields, which is the
    /// distinction the composition roots are drawing.
    let azureConfig: AzureBlobStorage.AzureBlobStorageConfig = failwith "ambient"

    let redisChannel: INotificationChannel = failwith "ambient"

    // ── `IDistributedLock` — the lease-usage excerpts ─────────────────

    /// The deployment's logger, passed to `DistributedLockSelection.fromEnv`.
    let logger: ILogger = failwith "ambient"

    /// The resolved lock, the lease a critical section is holding, and
    /// the id / TTL it was acquired under.
    let lck: IDistributedLock = failwith "ambient"

    let lease: Lease = failwith "ambient"

    let lockId: string = failwith "ambient"

    let ttl: TimeSpan = failwith "ambient"

    /// The heartbeat pattern's two continuations.
    let keepWorking (lease: Lease) : unit = failwith "ambient"

    let abandon () : unit = failwith "ambient"

    /// The fencing pattern's downstream store — the reader's own, since
    /// a fence token only means something to a write path that records
    /// the highest one it has seen and refuses anything lower.
    type IFencedStore =
        abstract WriteFenced: resourceId: string * fenceToken: int64 * payload: string -> Async<unit>

    let store: IFencedStore = failwith "ambient"

    let resourceId: string = failwith "ambient"

    let payload: string = failwith "ambient"

    /// What `releaseDetached` does with a failed best-effort release.
    let onError (ex: exn) : unit = failwith "ambient"