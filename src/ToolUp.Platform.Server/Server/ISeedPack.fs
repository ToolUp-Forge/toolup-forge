// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.BlobStorage

// ─── ISeedPack — declarative seed / fixture data (Phase 447) ─────────
//
// A module contributes an `ISeedPack`; the SDK applies registered packs
// once, idempotently, at end-of-compose, and only in compositions that
// asked (`ServerConfig.SeedData`). Packs are plain code, not a data DSL
// (GP 8): they receive scoped store handles and write through them.
//
// **Determinism (pack-author contract).** A pack MUST be deterministic
// by construction — fixed ids and timestamps — so a re-apply after a
// version bump is comparable and a seeded environment is reproducible.
// Nondeterministic content (`DateTime.UtcNow`, `Guid.NewGuid()`) defeats
// the "resettable, comparable demo state" the substrate exists to give.
//
// **Idempotency.** `SeedDataLoader` guards each pack with an applied-
// marker blob keyed by `Name@Version`, so re-boot is a no-op and a
// version bump re-applies. A pack should additionally tolerate a
// bypassed marker (probe-before-write where cheap) so it stays safe
// under an at-least-once apply.

/// Scoped store handles + the target scope handed to a seed pack's
/// `Apply`. `EntityStore` / `DataObjectStore` are `option` because the
/// deployment may not have composed those substrates — a pack that
/// seeds typed entities matches on `Some` and no-ops (or logs) on
/// `None`, rather than NRE-ing on an absent store.
type SeedContext = {
    /// The SDK's suggested default scope for platform-shared reference /
    /// demo data — the reserved `_platform` scope. A pack is free to
    /// target any other scope through the store handles below (e.g.
    /// enumerate known team scopes and seed each); this is only the
    /// default the loader passes.
    ScopeId: string
    /// Always present — an `IBlobStorage` is registered in every
    /// composition.
    BlobStorage: IBlobStorage
    /// `Some` only when `ServerConfig.EntityStore = EnabledEntityStore`.
    /// `None` means the deployment did not compose the entity substrate.
    EntityStore: IEntityStore.IEntityStore option
    /// `Some` when a `IDataObjectStore` is composed (the default data
    /// path). `None` only in the minimal shapes that register none.
    DataObjectStore: IDataObjectStore option
    /// Startup logger for pack progress / warnings.
    Logger: ILogger
}

/// What a pack reports back after `Apply`. Feeds the applied-marker
/// payload + the startup-log + audit summary (GP 6).
type SeedReport = {
    PackName: string
    Version: string
    /// Count of items the pack wrote (entities / objects / blobs). A
    /// pack that found its data already present returns 0.
    ItemsSeeded: int
    /// Free-form notes for the audit trail (e.g. which entity types
    /// were seeded).
    Notes: string list
}

/// A module-contributed, deterministic, idempotent seed-data pack.
/// Registered via `ServerApp.withSeedPack`; applied once per
/// `Name@Version` by `SeedDataLoader` at end-of-compose — in dev / demo
/// shapes only (see `ServerConfig.SeedData` + its Team-shape refusal).
type ISeedPack =
    /// Stable pack identity. Combined with `Version` into the applied-
    /// marker blob key, so it must be stable across reboots.
    abstract Name: string

    /// Monotonic version tag. Bumping it re-applies the pack (a new
    /// marker key); leaving it unchanged makes reboot a no-op.
    abstract Version: string

    /// Apply the pack's data through the scoped stores in `ctx`. MUST be
    /// deterministic (fixed ids / timestamps). Returns a `SeedReport`
    /// summarising what was written.
    abstract Apply: ctx: SeedContext -> Async<SeedReport>