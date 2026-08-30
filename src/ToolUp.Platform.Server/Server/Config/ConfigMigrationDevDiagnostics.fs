// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConfigMigrationDevDiagnostics

open ToolUp.Platform
open ToolUp.Platform.ConfigMigrationRegistry

/// Wire-shape rendered into the `Contributors["Pending Config
/// Migrations"]` panel of the `DevDiagnosticsReport`
/// (2026-05-06 ToolUp.Platform gap audit, Gap 7).
///
/// The panel answers one question for a module author: **which
/// migrator do I owe, and how badly?** Drift is silent by
/// construction — a renamed field reads back as its default and the
/// admin re-enters the value — so without a surface like this the only
/// evidence is a support ticket saying the setting did not stick.
type private PendingConfigMigrationsPanel = {
    /// Modules declaring a schema version above the implicit floor.
    VersionedModules: (string * SchemaVersion) list
    /// Modules with at least one registered `IConfigMigrator`.
    ModulesWithMigrators: string list
    /// Registration defects in the declared migrator set. Pure over the
    /// registration — present here whether or not any document has hit
    /// them, because a broken chain is worth seeing before it costs a
    /// team its config.
    ChainDefects: string list
    /// Total field-level drift observations since process start.
    TotalDriftObservations: int
    /// Per-module drift, ordered by observation count. A row with
    /// `HasMigrators = false` is the headline case: the schema has
    /// moved and nothing is upgrading the documents behind it.
    Drift: ConfigDriftSummary list
    /// Set when the substrate is inert — no module declares a version
    /// and no migrator is registered — so a reader is not left
    /// wondering whether an empty panel means "nothing to do" or
    /// "nothing wired".
    Note: string option
}

/// Default contributor. Registered as a DI singleton in `compose`
/// alongside the other platform panels.
///
/// Honours the `IDevDiagnosticsContributor` contract: deployment-scoped,
/// idempotent, side-effect-free, and well inside the 50 ms budget — it
/// reads two in-memory structures and allocates a summary.
type PendingConfigMigrationsContributor(support: ConfigMigrationSupport) =
    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let registry = support.Registry

            let versioned =
                registry.MigratableModules
                |> List.map (fun key -> key, registry.TargetVersion key)
                |> List.filter (fun (_, v) -> v > ConfigMigrationMetadata.InitialVersion)

            let drift = support.Drift.Summarise registry

            let panel: PendingConfigMigrationsPanel = {
                VersionedModules = versioned |> List.sortBy fst
                ModulesWithMigrators = registry.MigratableModules |> List.sort
                ChainDefects = registry.ValidateAll() |> List.map ConfigMigrationChainError.describe
                TotalDriftObservations = support.Drift.TotalObservations
                Drift = drift
                Note =
                    if registry.IsInert then
                        Some
                            "No module declares ModuleConfigSchema.SchemaVersion above 1 and no IConfigMigrator is registered — config schema evolution is inert in this deployment. Drift rows, if any, name schemas that moved without a version bump."
                    else
                        None
            }

            return "Pending Config Migrations", box panel
        }