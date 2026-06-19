module ToolUp.Platform.SessionFileStoreInstanceValidator

open System
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Multi-instance + in-memory session file store ──────────────────
//
// `SessionFileStore` (FileManagement.fs) keeps three per-instance
// in-memory dictionaries — `files`, `processedData`, `processedEntries`
// — hydrated ONCE from the backing `IDataObjectStore` at store
// construction (the first request that resolves a scope) and, for a
// persistent scope, never evicted. Persistence writes THROUGH to the
// shared store, but reads are served from that in-memory snapshot.
//
// In a single-instance deployment the snapshot is authoritative. Under
// N replicas behind a load balancer, a file uploaded to replica A
// updates A's dictionaries + the shared store, but replica B hydrated
// its dictionaries at its own first-request time and never re-reads — so
// B serves a stale file list (A's upload is invisible to B) for the
// process lifetime. Sticky sessions hide it; a round-robin hop or a
// replica restart surfaces it as "my uploaded file vanished".
//
// `Warning` (not `Error`) and no escape hatch — many deployments run
// single-instance or with sticky sessions and legitimately accept the
// read-staleness until a shared read-through cache / store-change
// notification ships. Mirrors `RateLimiterInstanceValidator` /
// `NotificationChannelInstanceValidator`: operator-declared
// multi-instance via `ServerConfig.ReplicaCount`; the warning surfaces
// in the startup log + HealthMonitorUI Preflight tab + `/dev/inspect`.

/// Config validator that warns when the in-memory `SessionFileStore`
/// runs under `ReplicaCount > 1` with a persistent surface configured —
/// each replica's hydrated-once dictionaries drift from the shared
/// backing store, so a file uploaded to one replica is invisible to the
/// others until their stores are reconstructed.
type SessionFileStoreInstanceValidator(config: ServerConfig, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    interface IConfigValidator with
        member _.Name = "session-file-store-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1
            let persistentSurface = DeploymentConfig.hasPersistentAuthenticatedStorage config

            if multiInstance && persistentSurface then
                return
                    Warning(
                        sprintf
                            "ReplicaCount = %d with a persistent surface (%s). SessionFileStore keeps its file list and processed-data summaries in per-instance in-memory dictionaries, hydrated once from the backing store at first access and never re-read. Persistence writes through to the shared store, but each replica reads from its own snapshot — a file uploaded to one replica stays invisible to the others (a stale file list / 'my upload vanished' after a round-robin hop or a replica restart) for the process lifetime. Run a single instance, pin sticky sessions at the load balancer, or accept the read-staleness knowingly until a shared read-through cache / store-change notification ships."
                            config.ReplicaCount
                            (DeploymentConfig.surfacesLabel config)
                    )
            else
                return Ok
        }