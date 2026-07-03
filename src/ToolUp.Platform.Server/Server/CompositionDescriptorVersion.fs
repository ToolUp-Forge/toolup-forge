// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Composition-descriptor schema version + migration ────────────────
//
// Config-as-data is only durable if a stored `CompositionDescriptor`
// ([Phase 284]) survives forge upgrades. This adds the version tag's
// forward-migration step: an older descriptor is upgraded to the current
// shape before [`ServerApp.ofManifest`] composes it, and a *too-new*
// descriptor (authored against a later forge) fails with a readable
// version-gap error rather than silently mis-loading.
//
// The version tag itself is the `CompositionDescriptor.Version` field +
// `CompositionDescriptor.CurrentSchemaVersion` (declared with the
// descriptor in [Phase 284]'s file). This file carries the *behaviour*:
// `migrate`, the versioned build path, and the ergonomic raising
// `ServerApp.ofManifest` (which runs `migrate` first).
//
// **Additive + opt-in (GP 11 / GP 13).** A deployment that never persists
// descriptors never migrates one; the versioned build path is a strict
// superset of the pure builder — a current-version descriptor migrates as
// a byte-identical no-op, so nothing changes for it. **Total (GP 4):** the
// version gap is data (`MigrationError`), rendered to a readable message,
// never an unchecked mis-load.

/// Why a `CompositionDescriptor` could not be migrated to the current
/// schema version. Rendered to a readable, version-gap-naming message via
/// `CompositionDescriptorVersion.renderMigrationError`. (Named
/// `DescriptorMigrationError` to stay distinct from the unrelated
/// `ToolUp.Platform.MigrationError` in `Core` — anonymous-session migration.)
type DescriptorMigrationError =
    /// The descriptor's version is newer than this forge understands —
    /// authored against a later forge. Carries (found, current) so the
    /// message names the gap. Never silently down-migrated.
    | DescriptorTooNew of found: int * current: int
    /// The descriptor's version is not a known schema version (e.g.
    /// negative / corrupt). Carries the offending value.
    | UnknownDescriptorVersion of found: int

/// A failure of the versioned build path (`ServerApp.ofManifest` /
/// `CompositionDescriptorVersion.ofManifest`): either the schema migration
/// failed, or — post-migration — a selected component id did not resolve.
type VersionedBuildError =
    | MigrationFailed of DescriptorMigrationError
    | BuildFailed of DescriptorError

/// Schema migration + the versioned build path for `CompositionDescriptor`.
module CompositionDescriptorVersion =

    /// Upgrade a descriptor to `CompositionDescriptor.CurrentSchemaVersion`.
    ///
    /// - Current version → `Ok` unchanged (byte-identical no-op, GP 11).
    /// - Older version (in `[0, current)`) → apply the forward-migration
    ///   steps and stamp the current version. Today the only prior version
    ///   is `0` (an unversioned legacy descriptor whose shape is a subset
    ///   of the current one), so the upgrade is a re-stamp; each future
    ///   schema bump adds its transform here, keyed on the source version.
    /// - Newer version → `Error (DescriptorTooNew …)` naming the gap
    ///   (authored against a later forge — never down-migrated).
    /// - Negative / unknown → `Error (UnknownDescriptorVersion …)`.
    let migrate (descriptor: CompositionDescriptor) : Result<CompositionDescriptor, DescriptorMigrationError> =
        let current = CompositionDescriptor.CurrentSchemaVersion

        if descriptor.Version = current then
            Ok descriptor
        elif descriptor.Version > current then
            Error(DescriptorTooNew(descriptor.Version, current))
        elif descriptor.Version < 0 then
            Error(UnknownDescriptorVersion descriptor.Version)
        else
            // Older-but-valid: fold the forward-migration steps from the
            // descriptor's version up to current, then stamp. The step set
            // is empty today (v0 → v1 is a pure re-stamp — v0's shape is a
            // subset of v1's); a future bump inserts its transform here.
            Ok { descriptor with Version = current }

    /// Render a `MigrationError` to a readable, actionable single-line
    /// message naming the version gap.
    let renderMigrationError (error: DescriptorMigrationError) : string =
        match error with
        | DescriptorTooNew(found, current) ->
            sprintf
                "CompositionDescriptor schema version %d is newer than this forge understands (current: %d). The descriptor was authored against a later forge — upgrade forge, or re-author the descriptor against this version. Descriptors are never silently down-migrated."
                found
                current
        | UnknownDescriptorVersion found ->
            sprintf
                "CompositionDescriptor schema version %d is not a known version (expected 0..%d). The descriptor is corrupt or was written by an incompatible tool."
                found
                CompositionDescriptor.CurrentSchemaVersion

    /// Render a `VersionedBuildError` to a readable single-line message —
    /// the text `ServerApp.ofManifest` raises.
    let renderError (error: VersionedBuildError) : string =
        match error with
        | MigrationFailed e -> renderMigrationError e
        | BuildFailed e -> CompositionDescriptor.renderError e

    /// Migrate a descriptor to the current schema version, then build it
    /// into a `ServerApp` against `catalogue`. The total form: an
    /// unknown / too-new version fails with `MigrationFailed`; an
    /// unresolved component id fails with `BuildFailed`. A current-version,
    /// fully-resolvable descriptor is exactly `CompositionDescriptor.ofManifest`.
    let ofManifest
        (catalogue: RegistrationCatalogue)
        (descriptor: CompositionDescriptor)
        : Result<ServerApp, VersionedBuildError> =
        match migrate descriptor with
        | Error e -> Error(MigrationFailed e)
        | Ok migrated ->
            CompositionDescriptor.ofManifest catalogue migrated
            |> Result.mapError BuildFailed

/// `ServerApp.ofManifest` — the ergonomic composition-root entry point for
/// building an app from a descriptor. Runs the Phase 292 schema migration
/// (`CompositionDescriptorVersion.migrate`) before composing, then surfaces
/// any `VersionedBuildError` (a version gap, or an unresolved id) as a
/// readable exception, so a composition root reads `ServerApp.ofManifest
/// (cat, d) |> ServerApp.run` without threading a `Result`. Prefer the
/// total `CompositionDescriptorVersion.ofManifest` (version-aware) or
/// `CompositionDescriptor.ofManifest` (version-agnostic) where a caller
/// wants to handle the error as data.
[<AutoOpen>]
module ServerAppDescriptorExtensions =

    type ServerApp with

        /// Build a `ServerApp` from a `CompositionDescriptor` against a
        /// `RegistrationCatalogue`, migrating the descriptor to the current
        /// schema version first and raising a readable error on a version
        /// gap or an unresolved component id.
        static member ofManifest(catalogue: RegistrationCatalogue, descriptor: CompositionDescriptor) : ServerApp =
            match CompositionDescriptorVersion.ofManifest catalogue descriptor with
            | Ok app -> app
            | Error e -> failwith (CompositionDescriptorVersion.renderError e)