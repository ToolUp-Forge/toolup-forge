// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Generic

// ─── IContainerScheduler (Phase 26 substrate) ────────────────────────
//
// Server-only abstraction over container schedulers (Docker, K8s,
// Fly Machines, CloudRun, ECS, App Service). The substrate ships
// one reference companion (`DockerLocalContainerScheduler` at
// `src/ContainerSchedulers/DockerLocal/`) proving the abstraction
// against a dev-grade local Docker socket; cloud-specific
// companions ship downstream — Diametrical-side for ToolUp Cloud,
// third-party for self-hosted operators. Forge intentionally
// privileges no cloud here (GP 1).
//
// **Server-only by construction.** `StreamLogs` returns
// `IAsyncEnumerable<LogEntry>` which Fable cannot transpile; the
// interface therefore lives in `ToolUp.Platform.Server` rather than
// `Shared/Types/` (mirrors `IEntityStore` and `IJobScheduler`
// placement). The supporting types — `ContainerSpec`,
// `ContainerStatus`, `LogEntry` — live in
// `Shared/Types/DeployPlaneTypes.fs` so a Fable admin UI can render
// them without round-tripping through a DTO layer.
//
// **Six-rule portability audit (Phase 9c, Guiding Principle 12):**
//
//   1. Identity by value      — every method takes/returns
//                                `ContainerId` and `TenantId`
//                                (string aliases). `ContainerSpec`,
//                                `ContainerStatus`, `ContainerInfo`,
//                                `LogEntry` are records / DUs with
//                                no live handles. No Docker
//                                `IContainer`-shaped references
//                                escape the boundary.
//   2. Async at every boundary — every method returns `Async<_>` or
//                                `IAsyncEnumerable<_>` (a more
//                                expressive cancellable async surface
//                                for streaming). No synchronous
//                                methods, no `Tell`-shaped
//                                signatures.
//   3. Retry/supervision as data — failure flows through
//                                  `ContainerSchedulerError`. The
//                                  scheduler itself does not retry —
//                                  the deploy pipeline (`IDeployPipeline`)
//                                  owns retry policy and re-invokes
//                                  the scheduler.
//   4. Stateless between calls — every method derives its result
//                                from the scheduler backend on every
//                                call. No in-memory state caches
//                                survive across calls; an Orleans
//                                grain deactivation between
//                                `LaunchContainer` and
//                                `GetContainerStatus` is invisible.
//   5. No cross-shard ordering — containers for different tenants
//                                have no relative ordering.
//                                Operations on the same container
//                                are sequential (per-`ContainerId`
//                                single-actor invariant inside the
//                                scheduler companion).
//   6. Precision at lower bound — `ContainerStatus` timestamps
//                                  inherit the backend's precision
//                                  (Docker reports second-resolution;
//                                  K8s reports millisecond). Log
//                                  entry timestamps inherit the
//                                  container's stream precision.

/// Server-side abstraction over a container scheduler. Companions
/// implement this against their target (Docker socket, K8s API
/// server, Fly Machines, etc.) and self-register via
/// `services.AddSingleton<IContainerScheduler>`. The substrate ships
/// `DockerLocalContainerScheduler` as the dev-grade reference impl.
type IContainerScheduler =
    /// Launch a fresh container for the given tenant. Returns the
    /// assigned `ContainerId` on success.
    ///
    /// **Validation chain**:
    ///   1. `spec.Image` is non-empty.
    ///   2. `spec.ExposedPort` (when `Some`) is in `1..65535`.
    /// First failure short-circuits with
    /// `ContainerSchedulerError.InvalidContainerSpec`.
    ///
    /// Implementations attach `tenantId` as a label on the
    /// container so `ListContainers` filtering works without an
    /// external lookup.
    abstract LaunchContainer:
        tenantId: TenantId * spec: ContainerSpec -> Async<Result<ContainerId, ContainerSchedulerError>>

    /// Stop a container. Sends SIGTERM (or scheduler-native
    /// equivalent), waits the scheduler's grace period, then
    /// SIGKILL. Idempotent: stopping an already-stopped container
    /// returns `Ok ()`.
    abstract StopContainer: containerId: ContainerId -> Async<Result<unit, ContainerSchedulerError>>

    /// Restart a container. Stops then re-launches with the same
    /// spec. The `ContainerId` is preserved across the restart
    /// (Docker / K8s semantics — the same container handle resumes
    /// after restart).
    abstract RestartContainer: containerId: ContainerId -> Async<Result<unit, ContainerSchedulerError>>

    /// Read the live status of a container. Returns
    /// `ContainerNotFound` when the scheduler has no record of the
    /// id (vs. `UnknownContainer` error which surfaces transport
    /// failures); the `Async<Result<_, _>>` envelope distinguishes
    /// "scheduler unreachable" from "scheduler responded, container
    /// is gone".
    abstract GetContainerStatus: containerId: ContainerId -> Async<Result<ContainerStatus, ContainerSchedulerError>>

    /// Enumerate containers. `tenantId = Some tid` filters to a
    /// single tenant's containers via the `tenantId` label;
    /// `None` returns every container the scheduler is managing.
    /// Ordering: scheduler-defined (typically launch-time
    /// ascending). Implementations re-query the backend on every
    /// call — no caching.
    abstract ListContainers: tenantId: TenantId option -> Async<ContainerInfo list>

    /// Stream log entries from a container. `fromTime = Some t`
    /// scopes to logs at or after `t`; `None` streams from the
    /// container's earliest available logs (scheduler-defined
    /// retention).
    ///
    /// The returned `IAsyncEnumerable<LogEntry>` is cancellable via
    /// the standard `IAsyncEnumerator` `CancellationToken` —
    /// callers iterate with `await foreach` (C#) or `for entry in
    /// asEnumerable do` (F# via `TaskSeq`). Implementations
    /// terminate the enumeration when the container exits;
    /// reconnecting after exit returns an empty stream.
    abstract StreamLogs: containerId: ContainerId * fromTime: System.DateTime option -> IAsyncEnumerable<LogEntry>