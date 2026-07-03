// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Declarative composition descriptor (CompositionDescriptor) ───────
//
// The serializable *inverse* of the read-only composition manifest
// ([Phase 280] `CompositionManifest`). Where the manifest projects a
// live `ServerApp` down to a machine-readable enumeration of what it
// composed, a `CompositionDescriptor` lifts the *whole* composition — the
// module / companion selection, plus the `ServerConfig` that shaped it —
// back up to data, and `CompositionDescriptor.ofManifest` builds it into
// an equivalent `ServerApp`. Composition becomes config-as-data:
// reproducible deploys, GitOps, diffable composition, lightweight test
// fixtures.
//
// **Handler bodies stay code (GP 1).** The descriptor references every
// composed unit by its stable Phase 279 `ComponentId` — it carries *no
// application logic*. The code side (a module's `ServerModule` value, a
// companion's registration) lives in a `RegistrationCatalogue`; the
// descriptor names *which* ids to compose (as data) and the catalogue
// supplies the *how* (as code). This is the seam that keeps the
// serializable descriptor free of handler / view / executor bodies, so it
// is not a domain artefact and stays vendor / tree-language-free.
//
// **Round-trip law.** For a descriptor `d` whose every selected id resolves
// in a catalogue `cat`, projecting the built app back to a manifest
// reproduces `d`'s selected component ids:
//   `manifest (ofManifest cat d)` ⊇ `d`'s module + companion selections
// (datatypes / tools are module-derived, so they fall out of the module
// registrations transitively — see [Phase 295] for the full completeness
// round-trip). The descriptor ↔ manifest pair are inverse over the
// composed surface.
//
// **Zero cost when unused (GP 11 / GP 13).** The fluent builders
// (`ServerApp.addModule`, `with*`) stay the primary composition path; a
// deployment that never authors a descriptor builds none and is
// byte-for-byte unchanged. `ofManifest` allocates only when a caller
// drives composition from data.

/// A serializable selection of one composed unit: its stable Phase 279
/// `ComponentId` (the catalogue key that resolves the code side) plus
/// optional registration inputs carried as data. `Inputs` is empty for a
/// component whose registration takes no data; a component that needs
/// richer input parses it from these string values (config-as-data).
type ComponentSelection = {
    /// The stable id of the composed unit to include. Resolved against a
    /// `RegistrationCatalogue` at build time.
    Id: ComponentId
    /// Serializable registration inputs, keyed by name. Empty for the
    /// common case (the catalogue entry closes over everything it needs);
    /// non-empty when a descriptor parameterises a registration by data.
    Inputs: Map<string, string>
}

/// A declarative, serializable description of an application's whole
/// composition: which components to include (each by stable
/// `ComponentId`), plus the `ServerConfig` that shaped composition
/// (already a record). The serializable inverse of `CompositionManifest`;
/// built into a `ServerApp` by `CompositionDescriptor.ofManifest` against
/// a `RegistrationCatalogue`. Carries no application logic — handler /
/// view / executor bodies are referenced by id, never embedded (GP 1).
type CompositionDescriptor = {
    /// Phase 292 — the descriptor's schema version. A persisted descriptor
    /// authored against an older forge carries an older version; the
    /// versioned build path (`CompositionDescriptorVersion.migrate`,
    /// invoked by `ServerApp.ofManifest`) upgrades it to
    /// `CompositionDescriptor.CurrentSchemaVersion` before composing.
    /// `CompositionDescriptor.create` stamps the current version.
    Version: int
    /// The modules / companions selected, each by stable id. Datatypes and
    /// tools are contributed *by* their owning module's registration, so
    /// they are not listed separately — they appear in the projected
    /// manifest transitively.
    Components: ComponentSelection list
    /// The `ServerConfig` whose switches change *what* gets composed
    /// (drift detection, usage metering, rate limiting, …).
    Config: ServerConfig
}

/// Why a descriptor could not be built into a `ServerApp`. Every case
/// renders to a readable, actionable message via
/// `CompositionDescriptor.renderError` — a descriptor never silently
/// mis-loads.
type DescriptorError =
    /// A selected `ComponentId` resolves to no entry in the registration
    /// catalogue (a typo'd id, or a component the catalogue never
    /// registered). Carries every unresolved id, so one failure reports
    /// them all.
    | UnknownComponents of ComponentId list

/// The code side of config-as-data composition: a lookup from a stable
/// `ComponentId` to the registration that folds that component onto a
/// `ServerApp`. The descriptor names ids as data; the catalogue supplies
/// the code, so handler / view bodies never live in the serializable
/// descriptor (GP 1). A registration is `Map<string,string> -> ServerApp
/// -> ServerApp` — it receives the selection's `Inputs` (config-as-data)
/// and the app-in-progress, and returns the app with the component folded
/// in (typically `ServerApp.addModule` for a module, a `with*` builder
/// for a companion).
type ComponentRegistration = Map<string, string> -> ServerApp -> ServerApp

/// A registration catalogue: `ComponentId` → `ComponentRegistration`.
/// Private map — build one with `RegistrationCatalogue.empty` +
/// `add` / `addModule`, resolve with `tryResolve`. The catalogue is the
/// deployment's known composition vocabulary; a descriptor can only
/// compose ids the catalogue registers.
type RegistrationCatalogue = private {
    Entries: Map<ComponentId, ComponentRegistration>
}

/// Construct + query a `RegistrationCatalogue`.
module RegistrationCatalogue =

    /// The empty catalogue — resolves nothing. Every descriptor built
    /// against it whose `Components` is non-empty fails with
    /// `UnknownComponents`.
    let empty: RegistrationCatalogue = { Entries = Map.empty }

    /// Register a raw `ComponentRegistration` under an explicit id. The
    /// low-level primitive; prefer `addModule` for the common module case
    /// (it derives the id for you). A later `add` on the same id replaces
    /// the earlier registration (last write wins).
    let add
        (id: ComponentId)
        (registration: ComponentRegistration)
        (cat: RegistrationCatalogue)
        : RegistrationCatalogue =
        {
            cat with
                Entries = Map.add id registration cat.Entries
        }

    /// Register a `ServerModule` under its resolved stable id — the
    /// explicit `ComponentId` when the module declares one (via
    /// `ServerModule.withComponentId`), else the name-derived default
    /// (`ComponentId.ofModule Name`). Exactly the id `addModule`
    /// accumulates onto `ServerApp.ModuleComponentIds`, so a descriptor
    /// selecting `ComponentId.ofModule name` resolves this module and the
    /// projected manifest carries the same id (round-trip law). The
    /// registration ignores its `Inputs` — a module closes over its own
    /// contribution.
    let addModule (m: ServerModule) (cat: RegistrationCatalogue) : RegistrationCatalogue =
        let id = m.ComponentId |> Option.defaultValue (ComponentId.ofModule m.Name)
        add id (fun _ app -> ServerApp.addModule m app) cat

    /// The registration for `id`, or `None` when the catalogue does not
    /// register it.
    let tryResolve (id: ComponentId) (cat: RegistrationCatalogue) : ComponentRegistration option =
        Map.tryFind id cat.Entries

    /// Every id the catalogue can resolve. Handy for validating a
    /// descriptor's selections against the known vocabulary up front.
    let ids (cat: RegistrationCatalogue) : ComponentId list =
        cat.Entries |> Map.toList |> List.map fst

/// Author + build a `CompositionDescriptor`.
module CompositionDescriptor =

    /// Phase 292 — the current descriptor schema version. `create` stamps
    /// it; `CompositionDescriptorVersion.migrate` upgrades any older
    /// descriptor up to it before `ofManifest` composes. Bump this (and
    /// add a migration step) whenever the descriptor's serialized shape
    /// changes in a way an older stored descriptor would not satisfy.
    [<Literal>]
    let CurrentSchemaVersion = 1

    /// A component selection by id, with no registration inputs — the
    /// common case (the catalogue entry closes over everything it needs).
    let select (id: ComponentId) : ComponentSelection = { Id = id; Inputs = Map.empty }

    /// A component selection by id carrying serializable registration
    /// inputs (config-as-data), for a catalogue entry that parameterises
    /// its registration.
    let selectWith (id: ComponentId) (inputs: (string * string) list) : ComponentSelection = {
        Id = id
        Inputs = Map.ofList inputs
    }

    /// A descriptor over the given component selections and `ServerConfig`,
    /// stamped at the current schema version. The construction path —
    /// author descriptors through this, never a bare record literal, so
    /// additive fields (schema version, …) default here without churning
    /// call sites.
    let create (components: ComponentSelection list) (config: ServerConfig) : CompositionDescriptor = {
        Version = CurrentSchemaVersion
        Components = components
        Config = config
    }

    /// A descriptor stamped at an explicit schema `version` — for
    /// round-tripping a persisted descriptor authored against an older (or
    /// newer) forge, or a test fixture that exercises the Phase 292
    /// migration path. Prefer `create` (stamps the current version) for
    /// freshly-authored descriptors.
    let createVersioned
        (version: int)
        (components: ComponentSelection list)
        (config: ServerConfig)
        : CompositionDescriptor =
        {
            Version = version
            Components = components
            Config = config
        }

    /// The stable ids the descriptor selects, in declaration order.
    let componentIds (descriptor: CompositionDescriptor) : ComponentId list = descriptor.Components |> List.map _.Id

    /// Render a `DescriptorError` to a readable, actionable single-line
    /// message — the text `ofManifest`'s raising variant surfaces.
    let renderError (error: DescriptorError) : string =
        match error with
        | UnknownComponents ids ->
            let listing = ids |> List.map ComponentId.value |> String.concat ", "

            sprintf
                "CompositionDescriptor references component id(s) that the registration catalogue does not register: %s. Every selected ComponentId must resolve to a catalogue entry (register it via RegistrationCatalogue.addModule / add) — the descriptor names ids as data, the catalogue supplies the code."
                listing

    /// Build a `CompositionDescriptor` into a `ServerApp` by resolving
    /// every selected `ComponentId` against `catalogue` and folding its
    /// registration onto a fresh app seeded with the descriptor's
    /// `ServerConfig`. Total: returns `Error (UnknownComponents …)`
    /// naming *every* id the catalogue cannot resolve, rather than
    /// composing a partial app. On success the projected manifest
    /// reproduces the descriptor's module + companion selections (the
    /// round-trip law).
    let ofManifest
        (catalogue: RegistrationCatalogue)
        (descriptor: CompositionDescriptor)
        : Result<ServerApp, DescriptorError> =
        let unresolved =
            descriptor.Components
            |> List.filter (fun sel -> (RegistrationCatalogue.tryResolve sel.Id catalogue) |> Option.isNone)
            |> List.map _.Id

        match unresolved with
        | _ :: _ -> Error(UnknownComponents unresolved)
        | [] ->
            let seed = ServerApp.empty |> ServerApp.withConfig descriptor.Config

            let built =
                descriptor.Components
                |> List.fold
                    (fun app sel ->
                        // Resolution is total here — the unresolved sweep
                        // above already rejected any missing id, so this
                        // `Option.get` cannot fire on the non-error path.
                        let registration = (RegistrationCatalogue.tryResolve sel.Id catalogue).Value
                        registration sel.Inputs app)
                    seed

            Ok built

// `ServerApp.ofManifest` — the ergonomic raising entry point — lives in
// `CompositionDescriptorVersion.fs` (Phase 292), where it runs the schema
// migration before composing. The total, version-agnostic builder is
// `CompositionDescriptor.ofManifest` above.