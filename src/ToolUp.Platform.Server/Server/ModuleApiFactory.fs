namespace ToolUp.Platform

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative

// ─── Phase 495 — Module API-factory helper ───────────────────────────
//
// Captures the per-module API-factory shape every consumer composition
// root repeats: scope-aware file-contents resolution
// (`FileManagement.getFileContents`), the canonical
// `key=value|key=value` settings-key convention, and
// narrative-provenance publishing
// (`NarrativePublisher.publishWithProvenance`) bound once to the
// module's id. A consumer factory binds a `ModuleApiContext` at the
// top (`let m = ModuleApiFactory.create "MyModule" ctx`) and each
// endpoint collapses to a one-to-few-line helper call; the domain
// routines and request/response mapping stay with the module.
//
// Everything here delegates to the existing request-scoped helpers —
// behaviour (scope resolution, DI fallbacks,
// `FileNotFoundInSessionException` on a missing file, replace-latest
// publish semantics, no-op when no `INarrativeStore` is registered)
// is identical to hand-rolled wiring by construction (GP 11).

/// The analysis settings attached to a published narrative as
/// provenance: the canonical machine `Key` (deduplication — see
/// `NarrativeProvenance.SettingsKey`) plus the human-readable
/// `Display` label/value pairs (UI rendering — source order
/// preserved).
type NarrativeSettings = {
    /// Canonical, deterministic settings key — a stable ordering of
    /// the analysis inputs collapsed into one string
    /// (`"country=UK|brand=X|period=Last52Weeks"`). Two runs with the
    /// same inputs must produce the same key.
    Key: string
    /// Human-readable rendering of the same inputs for the UI:
    /// label / value pairs preserving source order.
    Display: (string * string) list
}

module NarrativeSettings =
    /// The canonical settings-key convention: ordered `(key, value)`
    /// pairs collapsed to `"key=value|key=value"`. Pair order is
    /// preserved — callers keep a stable ordering of analysis inputs
    /// so re-runs dedupe (see `NarrativeProvenance.SettingsKey`).
    let key (pairs: (string * string) list) : string =
        pairs |> List.map (fun (k, v) -> k + "=" + v) |> String.concat "|"

    /// Build settings from distinct machine pairs (collapsed into the
    /// canonical key) and display pairs (shown verbatim in the UI).
    /// Use when display labels/groupings differ from the machine key
    /// parts.
    let create (keyPairs: (string * string) list) (display: (string * string) list) : NarrativeSettings = {
        Key = key keyPairs
        Display = display
    }

    /// Build settings from a single pair list serving as both the
    /// machine key parts and the display pairs.
    let ofPairs (pairs: (string * string) list) : NarrativeSettings = create pairs pairs

/// Per-request module API context — `HttpContext` + the module id,
/// bound once at the top of a consumer's API factory. Every member
/// delegates to the request-scoped SDK helpers, so runtime behaviour
/// is identical to calling `FileManagement.getFileContents` /
/// `NarrativePublisher.publishWithProvenance` by hand.
type ModuleApiContext = {
    /// The request the factory body is executing in.
    HttpContext: HttpContext
    /// Module id attributed on every narrative publish (and its
    /// provenance stamp).
    ModuleId: string
} with

    /// Scope-aware file-contents resolution for the current request —
    /// `FileManagement.getFileContents` with the context pre-applied.
    /// Raises `FileNotFoundInSessionException` when the file is not
    /// present in the request's scope (classified as a 4xx user-action
    /// error by the remoting error handler).
    member this.GetFileContents(fileName: string) : string =
        FileManagement.getFileContents this.HttpContext fileName

    /// Wrap a pure file-backed routine as an async endpoint: resolve
    /// the request's file via `fileName`, hand its contents + the
    /// request to `routine`. The endpoint shape every non-narrative
    /// factory line repeats
    /// (`fun request -> async { let contents = getFileContents ctx …
    /// ; return routine contents … }`) collapses to
    /// `m.FromFile(_.FileName, fun contents r -> routine contents …)`.
    member this.FromFile(fileName: 'Req -> string, routine: string -> 'Req -> 'Res) : 'Req -> Async<'Res> =
        fun request -> async { return routine (this.GetFileContents(fileName request)) request }

    /// Publish `document` with provenance under this module's id —
    /// `NarrativePublisher.publishWithProvenance` with the module id
    /// and settings pre-shaped. Returns the stamped document so the
    /// caller can wire it onto the response. Replace-latest semantics
    /// per `(ModuleId, pageRoute, subtitleKey)`; no-op publish (still
    /// returning the stamped document) when no `INarrativeStore` is
    /// registered.
    member this.PublishNarrative
        (pageRoute: string option, settings: NarrativeSettings, subtitleKey: string option, document: NarrativeDocument)
        : Async<NarrativeDocument> =
        NarrativePublisher.publishWithProvenance
            this.HttpContext
            this.ModuleId
            pageRoute
            settings.Key
            settings.Display
            subtitleKey
            document

    /// `PublishNarrative` with the subtitle key defaulted to the
    /// document's own `Subtitle` (the common case).
    member this.PublishNarrative
        (pageRoute: string option, settings: NarrativeSettings, document: NarrativeDocument)
        : Async<NarrativeDocument> =
        this.PublishNarrative(pageRoute, settings, document.Subtitle, document)

    /// Best-effort variant of `PublishNarrative`: a publish failure
    /// (e.g. a caller outside a store-capable scope) degrades to the
    /// original unstamped document instead of failing the endpoint —
    /// the caller still gets its narrative, just unstamped and
    /// unstored.
    member this.TryPublishNarrative
        (pageRoute: string option, settings: NarrativeSettings, subtitleKey: string option, document: NarrativeDocument)
        : Async<NarrativeDocument> =
        async {
            try
                return! this.PublishNarrative(pageRoute, settings, subtitleKey, document)
            with _ ->
                return document
        }

    /// `TryPublishNarrative` with the subtitle key defaulted to the
    /// document's own `Subtitle`.
    member this.TryPublishNarrative
        (pageRoute: string option, settings: NarrativeSettings, document: NarrativeDocument)
        : Async<NarrativeDocument> =
        this.TryPublishNarrative(pageRoute, settings, document.Subtitle, document)

/// Entry point for the Phase 495 module API-factory helper.
module ModuleApiFactory =
    /// Bind a per-request module API context: the one-line opener of a
    /// consumer's API factory
    /// (`let m = ModuleApiFactory.create "MyModule" ctx`).
    let create (moduleId: string) (ctx: HttpContext) : ModuleApiContext = {
        HttpContext = ctx
        ModuleId = moduleId
    }