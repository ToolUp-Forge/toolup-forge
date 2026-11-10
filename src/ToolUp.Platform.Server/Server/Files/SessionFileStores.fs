module ToolUp.Platform.Files.SessionFileStores

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading.Tasks
open ToolUp.Remoting.Json.SystemTextJson
open ProcessedDataTypes
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose
open ToolUp.Platform.Files.FileManagement

// ─── The process's session file stores ───────────────────────────────
//
// One `SessionFileStore` per storage container, held by a registered
// service rather than by module-level state: the stores, the epochs of the
// stores eviction dropped, the eviction TTL and the eviction timer are this
// instance's, so a test builds its own and a host owns exactly one.
//
// A store is hydrated once, when its scope is first resolved: the
// hydration starts as a task and every request for the scope awaits that
// task, so two requests arriving during hydration both wait and neither
// holds a thread while it does.

/// One container's store, the hydration every request for it awaits, and
/// when it was last resolved.
[<NoEquality; NoComparison>]
type private StoreEntry = {
    Store: SessionFileStore
    Hydration: Lazy<Task>
    LastAccessed: DateTime ref
    Persist: bool
}

/// Serialiser for the `SessionStoreResetKey` payload — the options every
/// other `CustomNotification` publisher uses, so the JSON the client's
/// `Json.parseAs` reads has the shape it expects.
let private sessionStoreResetJsonOptions = PlatformJson.options

/// Announce an eviction-then-recreate transition on the scope whose store
/// was just rebuilt. Fire-and-forget on both arms: a slow or failing channel
/// must never delay the request that happened to re-create the store, and a
/// publish failure is not a reason to fail an upload.
///
/// The notification is suppressible (GP 13 — a deployment that prefers the
/// silent-failure behaviour composes no channel, or sets
/// `ServerConfig.NotifyOnSessionStoreReset = false`, which clears
/// `NotificationChannel`). The audit emission is NOT: an audit trail that
/// records data loss only when the UX opted in would be answering a
/// compliance question with a product preference.
let private announceStoreReset
    (runtime: FileManagementRuntime)
    (scope: StorageScope)
    (previousEpoch: Guid)
    (currentEpoch: Guid)
    =
    // No configured logger falls back to the console, as the hooks do.
    let logger =
        runtime.PostSaveHooksLogger
        |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

    let warn =
        Background.writeFailureLogger "FileManagement" (Some logger) "" "the request"

    try
        runtime.AuditLog.Record(
            StorageScope.auditScope scope,
            AuditSubject.system "FileManagement",
            SessionStoreReset {
                Container = scope.Container
                Reason = SessionStoreResetReasonEvicted
            }
        )
        |> Background.startAuditRow warn "SessionStoreReset audit"
    with ex ->
        warn "SessionStoreReset audit" ex

    match runtime.NotificationChannel with
    | Some channel ->
        async {
            try
                let payload: SessionStoreResetNotification = {
                    Container = scope.Container
                    PreviousEpoch = Some previousEpoch
                    CurrentEpoch = currentEpoch
                    Reason = SessionStoreResetReasonEvicted
                }

                let json = JsonSerializer.Serialize(payload, sessionStoreResetJsonOptions)
                do! channel.Publish(scope.ScopeId, CustomNotification(SessionStoreResetKey, json))
            with _ ->
                // A channel that cannot accept the publish leaves the client
                // on its slower reconciliation paths (the on-focus epoch poll
                // and the pre-flight check), which is exactly the no-channel
                // deployment's behaviour — degraded, not broken.
                // The audit row above already recorded the fact.
                ()
        }
        |> Async.Start
    | None -> ()

/// The process's session file stores. `evictionMinutes` is the TTL of an
/// ephemeral (non-persistent) store not resolved within it
/// (`ServerConfig.EphemeralStoreEvictionMinutes`; a non-positive value keeps
/// the 60-minute default). The sweep runs every ten minutes on a timer this
/// instance owns and disposes.
type SessionFileStores(evictionMinutes: float) =
    let ttl =
        TimeSpan.FromMinutes(if evictionMinutes > 0.0 then evictionMinutes else 60.0)

    let stores = ConcurrentDictionary<string, StoreEntry>()

    // The epoch of the store that most recently occupied a container and was
    // EVICTED from it: the one fact that tells the two ways a container can be
    // found empty apart — a fresh first access (no entry: nothing was lost)
    // from an eviction-then-recreate (an entry: the caller's uploads are gone
    // while their client may still be listing them). Written by the sweep,
    // consumed by the resolve that re-creates the container, so it is a
    // one-shot marker. A process restart leaves none: the instance dies with
    // the process, and the client's epoch comparison reconciles that path.
    let evictedEpochs = ConcurrentDictionary<string, Guid>()

    let evictIdleSince (cutoff: DateTime) =
        for kvp in stores do
            if not kvp.Value.Persist && kvp.Value.LastAccessed.Value < cutoff then
                match stores.TryRemove(kvp.Key) with
                | true, removed -> evictedEpochs[kvp.Key] <- removed.Store.Epoch
                | false, _ -> ()

    let timer =
        new System.Threading.Timer(
            (fun _ -> evictIdleSince (DateTime.UtcNow - ttl)),
            null,
            TimeSpan.FromMinutes 10.0,
            TimeSpan.FromMinutes 10.0
        )

    /// Evict every ephemeral store last resolved before `cutoff`, recording
    /// its epoch — the timer's sweep, callable by a test at a cutoff of its
    /// choosing.
    member internal _.EvictIdleSince(cutoff: DateTime) = evictIdleSince cutoff

    interface ISessionFileStores with
        member _.Resolve(dataTypes, dataObjectStore, runtime, scope) = async {
            // `GetOrAdd`'s factory can run and still LOSE the race, so "did
            // the factory fire" is not "is my store the one in the
            // dictionary". Only the entry that won is ever hydrated (its
            // `Lazy` is forced below), and only the request whose store won
            // announces, so a burst against a just-evicted container
            // produces exactly one notification.
            let built = SessionFileStore(dataTypes, dataObjectStore, scope, runtime)

            let entry =
                stores.GetOrAdd(
                    scope.Container,
                    fun _ -> {
                        Store = built
                        Hydration = lazy (Async.StartAsTask(built.Hydrate()) :> Task)
                        LastAccessed = ref DateTime.UtcNow
                        Persist = scope.Persist
                    }
                )

            if obj.ReferenceEquals(built, entry.Store) then
                // `TryRemove` makes consuming the marker atomic, so a second
                // request arriving behind this one cannot re-announce.
                match evictedEpochs.TryRemove scope.Container with
                | true, previousEpoch -> announceStoreReset runtime scope previousEpoch built.Epoch
                | false, _ -> ()

            entry.LastAccessed.Value <- DateTime.UtcNow
            do! Async.AwaitTask entry.Hydration.Value
            return entry.Store
        }

    interface IDisposable with
        member _.Dispose() = timer.Dispose()