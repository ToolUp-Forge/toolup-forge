namespace ToolUp.Platform

open System
open ToolUp.Platform.Narrative

/// Identity of a narrative entry — a stable pointer a module writes
/// when it publishes a `NarrativeDocument` and reads back when it
/// wants to surface earlier output. Values, not runtime handles
/// (GP 12 rule 1).
type NarrativeId = Guid

/// A narrative entry the store returns on lookup. Entries are written
/// by modules whenever they generate a fresh `NarrativeDocument` on
/// the server (successful analysis, cross-channel report, elasticity
/// fit, etc.). One entry per publication — the store is append-only
/// from the module's perspective; replacing "the latest narrative for
/// page X" is the caller's responsibility via `ReplaceLatest`.
type NarrativeEntry = {
    Id: NarrativeId
    /// Sidebar id the narrative corresponds to, e.g. `"SalesAnalysis"`
    /// or `"ChannelAnalysis/cross-channel"`. Used for list filtering
    /// and for human-readable attribution in AI tool responses.
    ModuleId: string
    /// Page route within the module, if the module is multi-page.
    /// `None` for single-page modules. Leading slash preserved.
    PageRoute: string option
    /// Short human-readable title surfaced by `list_narratives`. Taken
    /// from `NarrativeDocument.Title` at publish time.
    Title: string
    /// Optional subtitle (e.g. SKU name, channel name, fitted-row id)
    /// so the AI can disambiguate multiple narratives for the same
    /// module/page.
    Subtitle: string option
    /// UTC timestamp the narrative was published. Used for sorting and
    /// for the snapshot caveat when the AI surfaces it.
    PublishedAt: DateTime
    /// The full document. Held by the in-memory implementation;
    /// persistent implementations may rehydrate from blob storage.
    Document: NarrativeDocument
}

/// Summary projection used by AI tool calls and UI listing. Strips the
/// full `Document` to keep prompt/JSON payloads compact — the caller
/// fetches the body separately via `Get` when it actually needs it.
type NarrativeEntryInfo = {
    Id: NarrativeId
    ModuleId: string
    PageRoute: string option
    Title: string
    Subtitle: string option
    PublishedAt: DateTime
}

/// Scope-isolated narrative store. Every operation is scoped to a
/// `scopeId` (resolved by `ScopeResolutionMiddleware` from the
/// authenticated request); entries written under one scope are
/// invisible to every other. Scope separation is structural, not a
/// defensive filter (GP 4, GP 12 rule 5: no cross-shard promises).
///
/// ## What belongs here
///
/// The store exists so that AI tools (`list_narratives`,
/// `get_narrative`) can surface narrative output the user generated on
/// *other* pages or earlier in the same session. It is intentionally
/// lightweight — not a replacement for blob-backed persistence, not a
/// replacement for `IEventStore` (which records state-change events),
/// not a replacement for `IVectorStore` (which indexes chunks for
/// retrieval). Modules write after a successful server-side narrative
/// generation; the default in-memory implementation holds the tail of
/// recent entries per scope and evicts on process restart.
///
/// ## Distribution
///
/// GP 12 audit: returns are plain records; every method is
/// `Async<T>`; no callbacks; state is held per-scope; no ordering
/// guarantee beyond "most recent first" within a scope; precision is
/// millisecond (`DateTime.UtcNow` at publish time). A Redis-backed or
/// Postgres-backed implementation can satisfy the same contract
/// without touching any consumer.
type INarrativeStore =
    /// Publish a fresh narrative for `scopeId`. Returns the assigned
    /// `NarrativeId`. Implementations should not block the caller on
    /// storage writes beyond what is strictly necessary for durability
    /// — in-memory implementations should be O(1).
    abstract member Publish:
        scopeId: string * moduleId: string * pageRoute: string option * document: NarrativeDocument ->
            Async<NarrativeId>

    /// Publish a narrative, replacing any existing entries for the
    /// same `(scopeId, moduleId, pageRoute, subtitleKey)` tuple. Used
    /// by modules that regenerate narrative from parameters the user
    /// edits repeatedly — e.g. re-running price elasticity for the
    /// same SKU should not accumulate stale entries. When
    /// `subtitleKey` is `None`, replaces the latest entry for the
    /// module/page pair regardless of subtitle.
    abstract member ReplaceLatest:
        scopeId: string *
        moduleId: string *
        pageRoute: string option *
        subtitleKey: string option *
        document: NarrativeDocument ->
            Async<NarrativeId>

    /// List the most-recently-published narrative entries visible to
    /// `scopeId`, newest first. `limit` caps the returned list —
    /// implementations should honour it strictly so AI tool responses
    /// remain bounded. Returns summaries, not full documents.
    abstract member List: scopeId: string * limit: int -> Async<NarrativeEntryInfo list>

    /// Fetch a single entry by id. Returns `None` when the entry does
    /// not exist in `scopeId` — either because it was never published,
    /// belongs to a different scope, or has been evicted. Callers
    /// should treat `None` as a normal outcome, not an error.
    abstract member Get: scopeId: string * id: NarrativeId -> Async<NarrativeEntry option>

    /// Fetch a single section from a narrative by section id. Useful when
    /// the assistant only needs one part of a long analysis — saves the
    /// round-trip cost of returning a multi-kilobyte document body when
    /// a single section answers the question. Returns `None` when the
    /// narrative does not exist in `scopeId` or has no section with that
    /// id.
    ///
    /// The standard implementations fall back to `Get` plus a list
    /// lookup; stores backed by a structured persistence layer can
    /// override with a targeted fetch that avoids deserialising the full
    /// document.
    abstract member GetSection:
        scopeId: string * id: NarrativeId * sectionId: string -> Async<NarrativeSection option>

    /// Delete every entry visible to `scopeId`. Returns the count of
    /// deleted entries (0 if the scope had no entries). Idempotent —
    /// calling on an empty scope returns 0 without error. Used by
    /// `KnowledgeApi.ResetIndex` to keep narrative-store and KB views
    /// coherent after a per-scope wipe; without it, `list_narratives`
    /// would still return persisted entries even after the user has
    /// reset their KB. Other scopes are unaffected (per-scope wipe
    /// is structural — a Team mode reset of one team does not touch
    /// any other team's narratives).
    abstract member DeleteScope: scopeId: string -> Async<int>

/// Composition-root helpers for writing narratives from request-scoped
/// Fable.Remoting handlers. Handlers resolve `INarrativeStore` and the
/// current `StorageScope` from DI / `HttpContext.Items`; the helper
/// hides that plumbing so module handlers remain one-liners.
///
/// Falls back to a user-based scope when the scope middleware did not
/// run (tests, harness) — mirrors `FileManagement.getFileContents`.
module NarrativePublisher =
    open Microsoft.AspNetCore.Http
    open ToolUp.Platform

    let private resolveScopeId (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as scope) -> scope.ScopeId
        | _ -> FileManagement.getUserId ctx

    let private resolveStore (ctx: HttpContext) : INarrativeStore option =
        match ctx.RequestServices.GetService(typeof<INarrativeStore>) with
        | :? INarrativeStore as s -> Some s
        | _ -> None

    /// Publish a narrative for the current request's scope. No-op when
    /// no store is registered (keeps handlers working if a deployment
    /// opts out of the default in-memory store).
    let publish
        (ctx: HttpContext)
        (moduleId: string)
        (pageRoute: string option)
        (document: NarrativeDocument)
        : Async<NarrativeId option> =
        async {
            match resolveStore ctx with
            | Some store ->
                let scopeId = resolveScopeId ctx
                let! id = store.Publish(scopeId, moduleId, pageRoute, document)
                return Some id
            | None -> return None
        }

    /// Publish a narrative, replacing any existing entry for the same
    /// `(moduleId, pageRoute, subtitleKey)` tuple within the scope.
    /// Used by modules that regenerate narratives from parameters the
    /// user edits repeatedly.
    let replaceLatest
        (ctx: HttpContext)
        (moduleId: string)
        (pageRoute: string option)
        (subtitleKey: string option)
        (document: NarrativeDocument)
        : Async<NarrativeId option> =
        async {
            match resolveStore ctx with
            | Some store ->
                let scopeId = resolveScopeId ctx
                let! id = store.ReplaceLatest(scopeId, moduleId, pageRoute, subtitleKey, document)
                return Some id
            | None -> return None
        }

    /// List the most-recently-published narratives for the current
    /// request's scope. Returns `[]` when no store is registered.
    let list (ctx: HttpContext) (limit: int) : Async<NarrativeEntryInfo list> = async {
        match resolveStore ctx with
        | Some store ->
            let scopeId = resolveScopeId ctx
            return! store.List(scopeId, limit)
        | None -> return []
    }

    /// Fetch a single entry by id within the current request's scope.
    /// Returns `None` when no store is registered or the entry does not
    /// exist in the scope.
    let get (ctx: HttpContext) (id: NarrativeId) : Async<NarrativeEntry option> = async {
        match resolveStore ctx with
        | Some store ->
            let scopeId = resolveScopeId ctx
            return! store.Get(scopeId, id)
        | None -> return None
    }

    /// Fetch a single section from a narrative within the current
    /// request's scope. Returns `None` when no store is registered, the
    /// entry does not exist, or the section is not present.
    let getSection (ctx: HttpContext) (id: NarrativeId) (sectionId: string) : Async<NarrativeSection option> = async {
        match resolveStore ctx with
        | Some store ->
            let scopeId = resolveScopeId ctx
            return! store.GetSection(scopeId, id, sectionId)
        | None -> return None
    }

    /// Delete every narrative entry for the current request's scope.
    /// Returns 0 when no store is registered or the scope has no
    /// entries. Idempotent. Used by `KnowledgeApi.ResetIndex` to keep
    /// narrative-store and KB views coherent after a per-scope wipe.
    let deleteScope (ctx: HttpContext) : Async<int> = async {
        match resolveStore ctx with
        | Some store ->
            let scopeId = resolveScopeId ctx
            return! store.DeleteScope scopeId
        | None -> return 0
    }

    /// Convenience wrapper around `replaceLatest` that stamps
    /// `Provenance` onto the document before publishing. Collapses the
    /// 30-line `let docWithProv = { doc with Provenance = Some {...} } /
    /// NarrativePublisher.replaceLatest ... / return { result with
    /// Document = Some docWithProv }` boilerplate that every
    /// narrative-producing API factory repeats.
    ///
    /// Returns the stamped document (the same value the caller would
    /// have wired onto the API result) so callers can plug the result
    /// directly into the response shape:
    ///
    /// ```fsharp
    /// let! stamped =
    ///     NarrativePublisher.publishWithProvenance ctx "MyModule" (Some "/page")
    ///         settingsKey settingsDisplay subtitleKey doc
    /// return { result with Document = Some stamped }
    /// ```
    let publishWithProvenance
        (ctx: HttpContext)
        (moduleId: string)
        (pageRoute: string option)
        (settingsKey: string)
        (settingsDisplay: (string * string) list)
        (subtitleKey: string option)
        (document: NarrativeDocument)
        : Async<NarrativeDocument> =
        async {
            let stamped = {
                document with
                    Provenance =
                        Some {
                            ModuleId = moduleId
                            PageRoute = pageRoute
                            GeneratedAt = System.DateTimeOffset.UtcNow
                            SettingsKey = settingsKey
                            SettingsDisplay = settingsDisplay
                        }
            }

            let! _ = replaceLatest ctx moduleId pageRoute subtitleKey stamped
            return stamped
        }