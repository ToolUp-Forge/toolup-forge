// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

// ─── Hosting — a unit of background work declares where it runs ──
//
// "Which process profile runs this?" has one answer, `Hosting.admits`, read
// off a `Placement` the work's AUTHOR declares. Every hosted service in the
// SDK and in its companions is registered through `Hosting.register`, which
// registers it when the deployment's shape admits its placement and
// registers nothing otherwise (GP 13: zero background services unless the
// deployment asks). The composition root places a unit; the unit says what
// kind of work it is.
//
// The three placements, and the shapes that admit them:
//
//   | Placement      | AllInOne | WorkerOnly | DispatcherOnly  | WebOnly | ServerlessHost |
//   |----------------|----------|------------|-----------------|---------|----------------|
//   | `SharedWork`   | yes      | yes        | only `Outbound` | no      | no             |
//   | `ProcessLocal` | yes      | yes        | yes             | yes     | no             |
//   | `OnceAtStart`  | yes      | yes        | yes             | yes     | yes            |
//
//   - `SharedWork` drains SHARED durable state, so one silo may do it for
//     every silo: a `WebOnly` replica leaves it to a sibling worker, and N
//     replicas of a worker do it N times only if the work itself says it is
//     safe to (the scheduler's tick election, the replicator's sink dedup).
//     Tagged `Outbound`, it is one of the two outbound dispatchers a
//     `DispatcherOnly` silo exists to isolate.
//   - `ProcessLocal` is bound to THIS process — its own memory, its own
//     files, the requests its own HTTP tier accepted — so no sibling could
//     do it, and every Kestrel profile that holds the state runs it. A
//     serverless host keeps no loop alive, so it admits none.
//   - `OnceAtStart` runs no loop: a one-shot at start, a handler handed to
//     the scheduler, a hook at stop. It is admitted everywhere.
//
// The answer used to be an 11-case subsystem union
// matched by a 7-arm matrix that only the platform's own 14 sites
// consulted; the companions — which the matrix could never name — each
// registered on every profile with a written reason. The reasons were
// placements in prose; they are now values.
//
// Lives first in the Compose chain so every Compose helper and the
// `/dev/inspect` panel (`DevDiagnosticsHandler.fs`) see it.

/// Where a unit of background work runs. Declared by the
/// work's author; `Hosting.admits` reads it against the deployment's shape.
type Placement =
    /// Drains shared durable state — any one silo may do it for all.
    /// Admitted on `AllInOne` and `WorkerOnly`; on `DispatcherOnly` only
    /// when the unit is `Outbound`; never on `WebOnly` or a serverless host.
    | SharedWork
    /// Bound to this process's own state, so it runs wherever that state
    /// lives: every Kestrel profile, never a serverless host.
    | ProcessLocal
    /// Runs no loop (a one-shot at start, a scheduler registration, a stop
    /// hook). Admitted on every profile, a serverless host included.
    | OnceAtStart

/// How a unit's `IHostedService` is obtained.
[<NoEquality; NoComparison>]
type HostedServiceSource =
    /// Built from the container (a factory registration: the container
    /// owns, and disposes, what it builds).
    | Built of (IServiceProvider -> IHostedService)
    /// An instance the composition already holds (an instance
    /// registration: the container never disposes an instance it was
    /// handed, so whoever constructed it keeps owning it).
    | Existing of IHostedService

/// One unit of background work: its identity, where it runs,
/// and the hosted service that runs it.
[<NoEquality; NoComparison>]
type BackgroundUnit = {
    /// Stable identity (`hosted:<name>`), the key of the placement table
    /// and of `/dev/inspect`'s hosting panel.
    Id: ComponentId
    Placement: Placement
    /// One of the outbound dispatchers a `DispatcherOnly` silo runs.
    /// Meaningful for `SharedWork` only.
    Outbound: bool
    Service: HostedServiceSource
}

/// What `Hosting.register` records for EVERY unit, admitted
/// or not: the data `Hosting.describe` and `/dev/inspect` read. Registered
/// as a DI singleton beside the hosted service (or in its place when the
/// profile does not admit the unit).
type BackgroundUnitRecord = {
    Id: ComponentId
    Placement: Placement
    Outbound: bool
}

/// One row of `Hosting.describe`: a unit and whether this
/// deployment's shape admits it.
type HostingRow = {
    Unit: BackgroundUnitRecord
    Admitted: bool
}

[<RequireQualifiedAccess>]
module BackgroundUnit =

    /// The identity of a unit named `name` — `hosted:<name>`.
    let id (name: string) : ComponentId = ComponentId.create ("hosted:" + name)

    /// A unit whose hosted service the container builds.
    let create (name: string) (placement: Placement) (factory: IServiceProvider -> IHostedService) : BackgroundUnit = {
        Id = id name
        Placement = placement
        Outbound = false
        Service = Built factory
    }

    /// A unit over a hosted-service instance the composition already holds.
    let ofInstance (name: string) (placement: Placement) (service: IHostedService) : BackgroundUnit = {
        Id = id name
        Placement = placement
        Outbound = false
        Service = Existing service
    }

    /// Tag `unit` as an outbound dispatcher (admitted on `DispatcherOnly`).
    let outbound (unit: BackgroundUnit) : BackgroundUnit = { unit with Outbound = true }

    /// The record `register` keeps for `unit`.
    let record (unit: BackgroundUnit) : BackgroundUnitRecord = {
        Id = unit.Id
        Placement = unit.Placement
        Outbound = unit.Outbound
    }

[<RequireQualifiedAccess>]
module Hosting =

    /// Whether `config`'s deployment shape runs a unit placed `placement`
    /// (tagged `outbound` or not). The one placement rule — see the table
    /// at the head of this file.
    let admitsPlacement (config: ServerConfig) (placement: Placement) (outbound: bool) : bool =
        match config.ServerlessHost, placement with
        | _, OnceAtStart -> true
        | ServerlessHost, _ -> false
        | KestrelHost, ProcessLocal -> true
        | KestrelHost, SharedWork ->
            match config.ProcessProfile with
            | AllInOne
            | WorkerOnly -> true
            | DispatcherOnly -> outbound
            | WebOnly -> false

    /// Whether `config`'s deployment shape runs `unit`.
    let admits (config: ServerConfig) (unit: BackgroundUnit) : bool =
        admitsPlacement config unit.Placement unit.Outbound

    /// Register `unit`: its record always, its hosted service only when
    /// `config` admits it. The one entry point for an `IHostedService`.
    let register (services: IServiceCollection) (config: ServerConfig) (unit: BackgroundUnit) : IServiceCollection =
        services.AddSingleton<BackgroundUnitRecord>(BackgroundUnit.record unit)
        |> ignore

        if admits config unit then
            match unit.Service with
            | Built factory -> services.AddSingleton<IHostedService>(Func<IServiceProvider, IHostedService>(factory))
            | Existing service -> services.AddSingleton<IHostedService>(service)
        else
            services

    /// The registered units, each with whether `config` admits it, in
    /// registration order. What `/dev/inspect`'s hosting panel renders.
    let describe (config: ServerConfig) (records: BackgroundUnitRecord seq) : HostingRow list =
        records
        |> Seq.map (fun record -> {
            Unit = record
            Admitted = admitsPlacement config record.Placement record.Outbound
        })
        |> List.ofSeq

    /// The records `register` wrote into `services`, before the provider
    /// is built (a test or a compose-time inspector reads them here).
    let records (services: IServiceCollection) : BackgroundUnitRecord list =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<BackgroundUnitRecord> && not d.IsKeyedService)
        |> Seq.choose (fun d ->
            match d.ImplementationInstance with
            | :? BackgroundUnitRecord as record -> Some record
            | _ -> None)
        |> List.ofSeq

    /// The startup-registration shape: a hosted service whose `StartAsync`
    /// runs `start` once (typically `IJobScheduler.RegisterHandler`) and
    /// whose stop does nothing. Construct it inside a unit's factory so
    /// whatever `start` resolves from the provider is resolved there.
    let atStart (start: unit -> unit) : IHostedService =
        { new IHostedService with
            member _.StartAsync(_ct) =
                start ()
                Threading.Tasks.Task.CompletedTask

            member _.StopAsync(_ct) = Threading.Tasks.Task.CompletedTask
        }

    /// The unit that hands `declarations` to the scheduler at start
    /// (`DeferredScheduledJobDeclaration.hostedService`). `OnceAtStart`: it
    /// runs no work of its own — the scheduler's own unit decides where the
    /// jobs run, and every process that can enqueue them needs the handler.
    /// `label` names the composing feature in the unit id and in the
    /// no-scheduler diagnostic alike.
    let deferredJobs (label: string) (declarations: DeferredScheduledJobDeclaration list) : BackgroundUnit =
        BackgroundUnit.create label OnceAtStart (DeferredScheduledJobDeclaration.hostedService label declarations)

/// Whether a profile mounts the HTTP pipeline — a different question from
/// placement (which concerns background work), kept under its historical
/// name.
[<RequireQualifiedAccess>]
module ProcessProfileGate =

    /// Returns `true` when `compose` should register HTTP middleware
    /// + the Giraffe router. `WorkerOnly` silos serve no HTTP, so
    /// `configurePipeline` is bypassed (the silo runs only its
    /// registered `IHostedService` set). Every other profile mounts
    /// the HTTP pipeline.
    let shouldRegisterHttpPipeline (config: ServerConfig) : bool =
        match config.ProcessProfile with
        | WorkerOnly -> false
        | AllInOne
        | WebOnly
        | DispatcherOnly -> true