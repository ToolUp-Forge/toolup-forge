module ToolUp.Platform.HealthCheck

open System
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.HealthChecks

// ─── First-party health checks (Phase 9 + 9k) ────────────────────────
//
// The three probes the SDK ships unconditionally — storage, auth, event
// store — implement the Phase 9k `IHealthCheck` interface and are
// registered via `services.AddSingleton<IHealthCheck>(instance)` in
// `compose`. The aggregator picks them up alongside any companion
// probes when it walks the service collection at end-of-compose.
//
// Each check is **lightweight on purpose**: readiness probes run on
// every load-balancer poll, so anything expensive becomes a production
// cost. We exercise the minimum side effect that proves the dependency
// is reachable, and we never write platform state from a probe (an
// audit event from `/ready` would dwarf real activity at scale).

/// `_platform` container is reserved for SDK-level state — using it
/// for the health probe avoids polluting any tenant scope and ensures
/// the probe runs against a container the SDK already creates.
[<Literal>]
let private healthContainer = "_platform"

[<Literal>]
let private healthProbeBlobName = "health/probe"

/// Storage health: confirms the configured `IBlobStorage` is reachable
/// and able to answer an `Exists` call against the reserved `_platform`
/// container. `Exists` is the cheapest call on the interface — it
/// neither writes nor reads content, and treats "not found" the same
/// as "found", which is what we want: we are checking the round-trip,
/// not whether the probe blob has been seeded.
type BlobStorageHealthCheck(storage: IBlobStorage) =
    interface IHealthCheck with
        member _.Name = "blob_storage"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! _ = storage.Exists(healthContainer, healthProbeBlobName)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }

/// Auth-provider health: a registration-only check. We deliberately
/// avoid synthesising a request to call `ValidateRequest` — the
/// production `IAuthProvider` implementations (header, JWT, OIDC) all
/// expect real request state, and faking it produces noisy false
/// negatives. A registered service is the load-bearing signal: if DI
/// resolved an `IAuthProvider`, requests will be able to authenticate
/// against it, and downstream failures (token signature, OIDC
/// metadata) are auth-provider concerns surfaced through their own
/// telemetry.
type AuthProviderHealthCheck(authProvider: IAuthProvider) =
    interface IHealthCheck with
        member _.Name = "auth_provider"
        member _.Kind = Readiness
        member _.Timeout = TimeSpan.FromSeconds 2.0

        member _.Check() = async {
            if isNull (box authProvider) then
                return Unhealthy "No IAuthProvider registered"
            else
                return Healthy
        }

/// Event-store health: confirms the configured `IEventStore` is
/// reachable by issuing a `ReadAll` against the reserved `_platform`
/// scope. Reads are cheap on every shipped implementation
/// (`InMemoryEventStore`, `PersistentEventStore`) and never mutate
/// state, so this is safe to call on every readiness poll. We
/// deliberately do not write a probe event — the cost is double
/// (write + later cleanup) and the read already exercises the
/// transport.
type EventStoreHealthCheck(eventStore: IEventStore) =
    interface IHealthCheck with
        member _.Name = "event_store"
        member _.Kind = Readiness
        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            try
                let! _ = eventStore.ReadAll(healthContainer)
                return Healthy
            with ex ->
                return Unhealthy ex.Message
        }