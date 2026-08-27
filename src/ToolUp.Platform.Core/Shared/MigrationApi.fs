// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── IDataMigrationApi (ToolUp.Remoting wire surface) ────────────
//
// Client-callable read + trigger surface over the Phase 10a module
// data-migration substrate. Lives in the shared layer so the Fable
// admin module (`MigrationStatusUI`) can call it via ToolUp.Remoting
// (GP 10).
//
// **Scope discipline.** `ListStatuses` and `TriggerMigration` read the
// caller's resolved `AccessContext` server-side and never accept a
// team id off the wire. `ListAllStatuses` is the platform-operator
// view across teams and is gated separately.
//
// **Permission gating.** Reads are available to any member of the
// caller's own scope. `TriggerMigration` requires Owner / Admin in
// `Team` / `MultiTeam` mode (via `TeamRoles.canWriteTeamConfig`);
// other modes are ungated because they have no role concept.

/// Declared schema version for one registered data type, plus whether
/// the deployment holds a usable migrator chain for it. Projected
/// server-side so the admin UI does not have to re-derive chain
/// validity from a migrator list.
type MigrationDataTypeInfo = {
    DataTypeId: string
    DisplayName: string
    CurrentVersion: SchemaVersion
    /// `None` when the registered migrator set resolves a chain from
    /// every version below `CurrentVersion`; `Some reason` when it
    /// does not, in which case the runner refuses to start a pass for
    /// this data type and records `MigrationChainBlocked`.
    ChainProblem: string option
}

type IDataMigrationApi = {
    /// Registered data types that declare a schema version, with the
    /// chain-validity projection above. Empty when the deployment has
    /// not opted into `DataMigrations`.
    [<AllowAnonymous>]
    ListDataTypes: unit -> Async<MigrationDataTypeInfo list>

    /// Migration status for every data type in the caller's own
    /// resolved scope. A data type no pass has visited is returned in
    /// its `MigrationIdle` shape rather than omitted, so the admin
    /// table has a row per data type from the first render.
    [<AllowAnonymous>]
    ListStatuses: unit -> Async<MigrationStatus list>

    /// Migration status across every team, for the platform-operator
    /// view. Returns an empty list for a caller who is not a platform
    /// admin, rather than throwing — the UI hides the section.
    [<RequiresClaim "scope">]
    ListAllStatuses: unit -> Async<MigrationStatus list>

    /// Run a migration pass for one data type over the caller's own
    /// resolved scope, now. Returns the resulting status, or an error
    /// string when the substrate is disabled, the caller lacks the
    /// role, or the data type is unknown. Idempotent — a pass over an
    /// already-current scope upgrades nothing.
    [<RequiresClaim "scope">]
    [<Audit "PolicyChanged">]
    TriggerMigration: string -> Async<Result<MigrationStatus, string>>
}