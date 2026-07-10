module ToolUp.Platform.IdempotencyStoreInstanceValidator

open System
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Remoting.Server

// ─── Multi-instance + in-memory idempotency store ───────────────────
//
// `InMemoryIdempotencyStore` (the in-process default `IIdempotencyStore`)
// memoises each `[<Idempotent>]` method's first response in a
// process-local `ConcurrentDictionary`, replaying it on retries within
// TTL. In a single-instance deployment that's the whole guarantee:
// a client that retries a create/charge/submit gets the SAME response
// and the handler runs once. Behind a load balancer fronting N replicas,
// the cache is per-instance and lost on restart — a retry that lands on
// a different replica than the first call misses the memo and
// **re-executes the handler** (double-charge / duplicate side effect —
// precisely what idempotency exists to prevent). Scaling from 1 → N with
// the same wiring silently drops the cross-instance idempotency
// guarantee, with no signal.
//
// The store is composed per-API via `Remoting.withIdempotencyStore`
// (a consumer `customOptions` concern), so the ServerApp compose path
// never holds the instance — unlike `IOAuthStateStore`, which forge
// composes itself. This validator therefore inspects the DI
// `IServiceCollection` for a registered `IIdempotencyStore` whose
// implementation is the in-memory default (the same shape
// `DeployPlaneDepsValidator` uses to probe composed substrate). A
// deployment that wants the preflight warning surfaces its store to DI
// with `services.AddSingleton<IIdempotencyStore>(store)` alongside the
// `withIdempotencyStore` wiring — as forge already does for
// `IOAuthStateStore`. When nothing is registered, or the registered
// store is a distributed impl (`BlobIdempotencyStore` / a Redis /
// DynamoDB companion), the validator is `Ok` — so single-instance and
// distributed deployments are both unaffected (GP 11).
//
// `Warning` (not `Error`) and no escape hatch — mirrors the soft-touch
// posture of `NotificationChannelInstanceValidator` /
// `SessionFileStoreInstanceValidator`: many `[<Idempotent>]` handlers
// tolerate a re-execution, and the operator can knowingly accept the
// per-instance window until a distributed store is wired. The remedy —
// the distributed `BlobIdempotencyStore` (69f.C) over `IBlobStorage`, or
// a compare-and-set backend (Redis SETNX / DynamoDB conditional put)
// against the same `IIdempotencyStore` contract — already ships.

/// Config validator that warns when a DI-registered `IIdempotencyStore`
/// is the in-memory default (`InMemoryIdempotencyStore`) under
/// `ReplicaCount > 1`. The per-instance cache does not cross replica
/// boundaries, so a retry routed to a different instance than the first
/// call re-executes the handler instead of replaying the memoised
/// response. Single-instance deployments and deployments that register a
/// distributed `IIdempotencyStore` validate to `Ok`.
type IdempotencyStoreInstanceValidator(config: ServerConfig, services: IServiceCollection, ?timeout: TimeSpan) =
    let timeout = defaultArg timeout IConfigValidator.defaultTimeout

    /// True when an `IIdempotencyStore` is registered in DI whose
    /// implementation is the in-memory default. Covers the instance-,
    /// type-, and (best-effort) factory-free registration shapes;
    /// a factory registration (`ImplementationFactory`) is not
    /// introspectable, so it is treated as not-in-memory (no
    /// false-positive warning for a deployment that hides its store
    /// behind a factory).
    let inMemoryStoreRegistered () =
        services
        |> Seq.exists (fun d ->
            not (isNull d.ServiceType)
            && d.ServiceType = typeof<IIdempotencyStore>
            // Reading ImplementationInstance / ImplementationType on a keyed
            // descriptor throws — skip keyed registrations (forge composes
            // none for this seam) so the probe stays exception-free.
            && not d.IsKeyedService
            && (match d.ImplementationInstance with
                | null -> d.ImplementationType = typeof<InMemoryIdempotencyStore>
                | instance -> instance.GetType() = typeof<InMemoryIdempotencyStore>))

    interface IConfigValidator with
        member _.Name = "idempotency-store-instance"
        member _.Timeout = timeout

        member _.Validate() = async {
            let multiInstance = config.ReplicaCount > 1

            if multiInstance && inMemoryStoreRegistered () then
                return
                    Warning(
                        sprintf
                            "A DI-registered IIdempotencyStore is the in-memory default (InMemoryIdempotencyStore) with ReplicaCount = %d. The idempotency cache is a process-local dictionary, per-instance and lost on restart — so a retry of an [<Idempotent>] method that lands on a different replica (or after a restart) than the first call misses the memo and re-executes the handler, dropping the cross-instance idempotency guarantee (double-charge / duplicate side effect) silently. Wire the distributed BlobIdempotencyStore over IBlobStorage (the Phase 69f.C reference), or a compare-and-set backend (Redis SETNX / DynamoDB conditional put) against the same IIdempotencyStore contract, or run a single instance / pin idempotent traffic to one replica. After fixing, verify in the HealthMonitorUI admin tab (production-safe) or /dev/inspect Validators panel (debug builds only)."
                            config.ReplicaCount
                    )
            else
                return Ok
        }