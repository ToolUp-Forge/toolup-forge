// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz

// ─── Per-Kind credential UI registry (Phase 10b → Phase 13a) ────────
//
// Each connector companion (`src/DataSources/<Provider>/`) exports a
// `let handler : string * DataSourceCredentialHandler` value keyed by
// its `Kind` discriminator. Consumers add the handler to
// `ClientConfig.Handlers.DataSourceCredentialHandlers`. The built-in
// `DataIngestionUI` admin module looks the form up at render time and
// inlines it inside the row for the matching data source.
//
// Phase 13a — replaces the legacy `register`-at-module-load side
// effect. The render context type (`DataSourceCredentialUIContext`)
// and the handler type (`DataSourceCredentialHandler`) live in
// `SDK.ClientTypes.fs` so `ClientConfig` can reference them.

/// Registry of per-Kind credential UI forms. Populated once at boot
/// by `SDK.Client.program` from
/// `config.Handlers.DataSourceCredentialHandlers`; the admin module
/// calls `tryGet` per row to find the matching renderer.
module DataSourceCredentialUIRegistry =
    /// Local alias preserved for source-level back-compat; the
    /// canonical name is `DataSourceCredentialHandler` in
    /// `SDK.ClientTypes.fs`.
    type Handler = DataSourceCredentialHandler

    // Per-tab cache, populated once by `SDK.Client.program` from
    // `config.Handlers.DataSourceCredentialHandlers`. Sole writer is
    // the SDK boot path; companions never touch it.
    let mutable private handlers: Map<string, DataSourceCredentialHandler> = Map.empty

    /// Called once by `SDK.Client.program` with the value from
    /// `ClientConfig.Handlers.DataSourceCredentialHandlers`.
    /// Duplicate Kinds within the supplied list collapse to last-wins
    /// (the SDK's `Client.run` validator surfaces duplicates
    /// separately).
    let setHandlers (entries: (string * DataSourceCredentialHandler) list) : unit = handlers <- entries |> Map.ofList

    /// Snapshot of the cached map — used by `Client.run` validators
    /// to surface what is wired.
    let snapshot () : (string * DataSourceCredentialHandler) list = handlers |> Map.toList

    /// Look up the form for `kind`. `None` when no companion is
    /// wired for that Kind — the admin module surfaces a "credential
    /// UI not registered" hint with the import command that would
    /// activate one.
    let tryGet (kind: string) : DataSourceCredentialHandler option = Map.tryFind kind handlers

    /// Snapshot of registered Kinds. Used by the admin module's "Add
    /// data source" form to populate the Kind dropdown — only Kinds
    /// with a registered credential UI can be created through the
    /// SDK admin UI (unsupported Kinds need bespoke admin work).
    let registeredKinds () : string list =
        handlers |> Map.toList |> List.map fst |> List.sort