// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SeedDataLoader

open System
open System.Text
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── SeedDataLoader — idempotent, mode-gated pack application (447) ──
//
// Called once, after the DI container is built, from
// `ComposeBootstrap.buildAndRunHost` (web branch). Applies every
// registered `ISeedPack` when `ServerConfig.SeedData` enables it,
// honouring the production-shape refusal, and idempotently: each pack
// is guarded by an applied-marker blob under `_platform/seed/` keyed by
// `Name@Version`, so re-boot is a no-op and a version bump re-applies.
//
// **Audit as an operational event, not an `AuditEvent` DU case.** A
// successful apply is recorded to `IEventStore` under the reserved
// `_platform.seed` source module — the same "separate stream from the
// AuditEvent DU" pattern the codebase uses for `_platform.jobs`'s
// `JobSchedulerTickMissed` (an infra-level signal that deliberately
// does not grow the audit union). Correlatable by `ScopeId +
// OccurredAt` if a sink needs both.

[<Literal>]
let private PlatformContainer = "_platform"

/// Reserved source module for the seed-application operational event
/// stream (see the file header).
[<Literal>]
let SeedSourceModule = "_platform.seed"

/// Wire-format `EventType` for the seed-application event.
[<Literal>]
let SeedAppliedEventType = "SeedPackApplied"

/// Applied-marker blob name under the `_platform` container. Keyed by
/// `Name@Version` so a version bump is a distinct marker (re-apply) and
/// an unchanged version is a hit (no-op).
let private markerBlobName (packName: string) (version: string) = sprintf "seed/%s@%s" packName version

/// Marker payload + audit body — the pack's report, for operator
/// inspection. A named record (not an anonymous one) so the F#
/// `string list` serialises through the converter set.
type private SeedMarker = {
    Pack: string
    Version: string
    ItemsSeeded: int
    Notes: string list
}

let private jsonOptions = FableConverters.create ()

let private tryResolve<'T> (services: IServiceProvider) : 'T option =
    match services.GetService(typeof<'T>) with
    | null -> None
    | resolved -> Some(resolved :?> 'T)

/// Apply the registered seed packs when `ServerConfig.SeedData` enables
/// it. A refusal on a Team / multi-team production shape (secure default
/// — demo data must not be written into a real tenant) raises to abort
/// startup. `NoSeedData` (the default) short-circuits to nothing — a
/// composition that never opted in pays zero cost (GP 13).
let runIfEnabled
    (services: IServiceProvider)
    (config: ServerConfig)
    (blobStorage: IBlobStorage)
    (logger: ILogger)
    : unit =
    match config.SeedData with
    | NoSeedData -> ()
    | EnabledSeedData when DeploymentConfig.hasTeamScope config ->
        // Production-shape refusal (GP 13 + secure-default posture): a
        // Team / multi-team deployment is a real tenant. Refuse rather
        // than silently write demo data into it; `ForcedSeedData` is the
        // deliberate override.
        failwithf
            "SeedData = EnabledSeedData on a Team/multi-team production shape (%s). Demo/fixture data must not be written into a real tenant. Set SeedData = ForcedSeedData to override deliberately, or remove the seed packs from this composition."
            (DeploymentConfig.surfacesLabel config)
    | EnabledSeedData
    | ForcedSeedData ->
        let packs = services.GetServices<ISeedPack>() |> List.ofSeq

        if not packs.IsEmpty then
            let entityStore = tryResolve<IEntityStore.IEntityStore> services
            let dataObjectStore = tryResolve<IDataObjectStore> services
            let eventStore = tryResolve<IEventStore> services

            let applyPack (pack: ISeedPack) = async {
                let marker = markerBlobName pack.Name pack.Version
                let! exists = blobStorage.Exists(PlatformContainer, marker)

                if exists then
                    logger.Debug(sprintf "seed: %s@%s already applied — skipping" pack.Name pack.Version)
                else
                    let ctx = {
                        ScopeId = PlatformContainer
                        BlobStorage = blobStorage
                        EntityStore = entityStore
                        DataObjectStore = dataObjectStore
                        Logger = logger
                    }

                    let! report = pack.Apply ctx

                    let payload =
                        JsonSerializer.Serialize(
                            {
                                Pack = report.PackName
                                Version = report.Version
                                ItemsSeeded = report.ItemsSeeded
                                Notes = report.Notes
                            },
                            jsonOptions
                        )

                    // Write the applied-marker AFTER a successful apply so a
                    // mid-apply crash re-runs next boot (at-least-once;
                    // idempotency is the pack's + the marker's job). A marker-
                    // write failure is a warning, not fatal — the pack applied;
                    // the next boot simply re-applies.
                    match! blobStorage.Upload(PlatformContainer, marker, Encoding.UTF8.GetBytes payload) with
                    | Error err ->
                        logger.Warn(
                            sprintf
                                "seed: %s@%s applied (%d item(s)) but marker write failed (%s) — will re-apply next boot"
                                pack.Name
                                pack.Version
                                report.ItemsSeeded
                                err
                        )
                    | Ok _ -> ()

                    // Durable, queryable audit trail (GP 6) — operational
                    // event, not an `AuditEvent` DU case. `IEventStore` is
                    // always composed (default `InMemoryOnly`); the `None`
                    // arm is defensive.
                    match eventStore with
                    | Some es ->
                        do! es.Write(Events.create PlatformContainer SeedSourceModule SeedAppliedEventType payload)
                    | None -> ()

                    logger.Info(sprintf "seed: applied %s@%s (%d item(s))" pack.Name pack.Version report.ItemsSeeded)
            }

            async {
                for pack in packs do
                    do! applyPack pack
            }
            |> Async.RunSynchronously