// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Microsoft.Extensions.DependencyInjection

// ─── The substrate and its stages ────────────────────────────────────
//
// `compose` builds the deployment's substrate in dependency order: storage,
// secrets and the logger; then the event-store chain; then the notification
// stack, the audit log and the sinks; then the outbound rate limiter; then
// the job scheduler. Two consumers genuinely need a value from a LATER stage
// than their own — the webhook dispatcher waits on the rate limiter, and the
// job-notify event-store decorator forwards to the scheduler, which itself
// writes through that event store. Each holds a `LateBound` slot: a value
// with a stated dormant behaviour until its stage binds it, bound exactly
// once, so the window is part of the type rather than a comment.
//
// When the stages are built, `compose` registers the `Substrate` in the
// service collection, and a staged contribution
// (`Contribution.staged owner (fun substrate services -> …)`) receives it at
// the fixed point where contributions run. A companion therefore reads the
// event store, audit log or notification channel the root built, as values,
// and never builds a second container to find them.

/// A substrate value a consumer holds before the stage that builds
/// it. Until `Bind`, `Current` is the stated dormant value; `Bind` runs once.
[<Sealed>]
type LateBound<'T>(name: string, dormant: 'T) =
    [<VolatileField>]
    let mutable bound: 'T option = None

    /// What the slot stands for, for the refusal on a second bind.
    member _.Name = name

    /// The bound value, or the dormant one before its stage.
    member _.Current: 'T =
        match bound with
        | Some value -> value
        | None -> dormant

    /// Whether the slot's stage has bound it.
    member _.IsBound = bound.IsSome

    /// Bind the slot to the value its stage built. A second bind refuses.
    member _.Bind(value: 'T) =
        match bound with
        | Some _ -> invalidOp (sprintf "the substrate slot '%s' is bound once, by its stage; it was bound again" name)
        | None -> bound <- Some value

/// The values `compose` built, handed to a staged contribution.
[<NoEquality; NoComparison>]
type Substrate = {
    /// The deployment's final configuration (every override folded).
    Config: ServerConfig
    Storage: BlobStorage.IBlobStorage
    SecretStore: Secrets.ISecretStore
    Logger: ILogger
    /// The outermost link of the composed event-store chain.
    EventStore: IEventStore
    AuditLog: IAuditLog
    Notifications: INotificationChannel
    MetricsSink: Metrics.IMetricsSink
    RateLimiter: IRateLimiter
    JobScheduler: IJobScheduler option
    ActivitySink: Tracing.IActivitySink
}

[<RequireQualifiedAccess>]
module Substrate =

    /// Register `substrate` where the staged contributions read it.
    /// `compose` does this once its stages are built; a test that applies
    /// staged contributions without `compose` registers its own first.
    let register (substrate: Substrate) (services: IServiceCollection) : IServiceCollection =
        services.AddSingleton<Substrate>(substrate)

    /// The substrate registered in `services`, read off its descriptor (no
    /// provider is built). Refuses when none is registered.
    let find (services: IServiceCollection) : Substrate =
        services
        |> Seq.tryPick (fun d ->
            if d.ServiceType = typeof<Substrate> && not d.IsKeyedService then
                match d.ImplementationInstance with
                | :? Substrate as substrate -> Some substrate
                | _ -> None
            else
                None)
        |> Option.defaultWith (fun () ->
            invalidOp
                "a staged contribution runs where compose has registered the substrate; apply it through ServerApp.run, or register one with Substrate.register first")