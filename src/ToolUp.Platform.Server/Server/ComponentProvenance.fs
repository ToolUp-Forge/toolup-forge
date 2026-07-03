// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Component provenance (ComponentProvenance) ──────────────────────
//
// Records, per Phase 279 `ComponentId`, which package / assembly +
// version a composed component came from. `ConfigDriftDetector` (Phase
// 9q) already hashes the active companion-assembly set; this attaches
// that provenance to each composed component so the Phase 280
// `CompositionManifest` can answer "which nupkg provides this companion,
// at what version" — supply-chain visibility, SBOM-adjacent, upgrade
// auditing.
//
// **Read-only enrichment, opt-in (GP 11 / GP 13).** The provenance map is
// built only when a caller asks (`ComponentProvenance.forApp`), keyed by
// the SAME `ComponentId` the manifest's companion slots carry, so it
// *attaches to* the manifest by id-join without widening the
// `CompositionManifest` / `ComponentEntry` shape — a manifest read
// without provenance is byte-for-byte unchanged.
//
// **Total (GP 4).** Resolution never throws: a type whose assembly
// metadata is missing resolves to `ComponentProvenance.unknown` rather
// than raising, so a manifest can always report provenance for every
// entry even in an odd `AssemblyLoadContext` environment.

/// Where a composed component's implementation came from: the package /
/// assembly simple name (the nupkg id proxy), its version, and the full
/// assembly name.
type ComponentProvenance = {
    /// The assembly simple name — the closest proxy for the nupkg id
    /// (`ToolUp.Platform.Server`, `ToolUp.Storage.AwsS3`, …).
    Package: string
    /// The assembly version string (`0.9.4.0`), or `unknown`.
    Version: string
    /// The full assembly name.
    Assembly: string
}

module ComponentProvenance =

    /// The total-resolution fallback — reported for a component whose
    /// implementation type carries no resolvable assembly metadata,
    /// rather than raising.
    let unknown: ComponentProvenance = {
        Package = "unknown"
        Version = "unknown"
        Assembly = "unknown"
    }

    /// Resolve provenance from a runtime implementation type. Total —
    /// null / metadata-less inputs resolve to `unknown`, never throw.
    let forType (implType: Type) : ComponentProvenance =
        if isNull implType then
            unknown
        else
            let asm = implType.Assembly
            let name = asm.GetName()

            let simple = name.Name |> Option.ofObj |> Option.defaultValue unknown.Package

            let version =
                name.Version
                |> Option.ofObj
                |> Option.map string
                |> Option.defaultValue unknown.Version

            {
                Package = simple
                Version = version
                Assembly = asm.FullName |> Option.ofObj |> Option.defaultValue simple
            }

    /// Resolve provenance from a live implementation instance (its
    /// runtime type). Total.
    let forInstance (impl: obj) : ComponentProvenance =
        if isNull impl then unknown else forType (impl.GetType())

    // The companion-instance walk mirrors `ServerApp.compositionManifest`
    // exactly — same slots, same `ComponentId` keys — so a provenance
    // entry lines up one-for-one with a manifest companion-slot entry.
    let private companionProvenance (app: ServerApp) : (ComponentId * ComponentProvenance) list = [
        match app.Auth with
        | Some impl -> ComponentId.forCompanionSlot "IAuthProvider", forInstance impl
        | None -> ()
        match app.Storage with
        | Some impl -> ComponentId.forCompanionSlot "IBlobStorage", forInstance impl
        | None -> ()
        match app.Notifications with
        | Some impl -> ComponentId.forCompanionSlot "INotificationChannel", forInstance impl
        | None -> ()
        match app.ProviderProfile with
        | Some impl -> ComponentId.forCompanionSlot "IProviderProfile", forInstance impl
        | None -> ()
        match app.UserDirectory with
        | Some impl -> ComponentId.forCompanionSlot "IUserDirectory", forInstance impl
        | None -> ()

        for sink in app.AuditSinks do
            ComponentId.forCompanionImpl "IAuditSink" sink.Name, forInstance sink
        for sink in app.TransactionalSinks do
            ComponentId.forCompanionImpl "INotificationSink" (NotificationKind.SinkKind.toWireString sink.Kind),
            forInstance sink
        for check in app.HealthChecks do
            ComponentId.forCompanionImpl "IHealthCheck" check.Name, forInstance check
        for validator in app.ConfigValidators do
            ComponentId.forCompanionImpl "IConfigValidator" validator.Name, forInstance validator
        for smoke in app.SmokeTests do
            ComponentId.forCompanionImpl "ISmokeTest" smoke.Name, forInstance smoke
    ]

    /// The provenance map for a composed app's companion slots, keyed by
    /// the same Phase 279 `ComponentId` the manifest's companion entries
    /// carry. Opt-in + on demand: a deployment that never calls this
    /// builds no map and pays nothing (GP 13); reading the manifest
    /// without it is byte-for-byte unchanged (GP 11).
    let forApp (app: ServerApp) : Map<ComponentId, ComponentProvenance> = companionProvenance app |> Map.ofList

    /// Provenance for one composed component id, if present in the app's
    /// companion set. `None` for an id the app did not compose.
    let tryForComponent (componentId: ComponentId) (app: ServerApp) : ComponentProvenance option =
        forApp app |> Map.tryFind componentId