module ToolUp.AI.NarrativeTools

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIToolRegistry

/// Built-in AI tools for surfacing narrative output produced on other
/// pages or earlier in the session. The assistant can list the user's
/// recent narratives (`list_narratives`) and fetch the full markdown
/// body of any one of them by id (`get_narrative`). Both are scoped by
/// the request's `StorageScope` — a user in one team only sees their
/// own scope's narratives (GP 4).
///
/// Wired automatically by `composeWithAI`; apps do not need to register
/// these explicitly.

// ─── JSON helpers ────────────────────────────────────────────────

let private fableJsonOptions = FableConverters.create ()

let private fableSerialize (value: obj) : string =
    JsonSerializer.Serialize(value, fableJsonOptions)

// ─── Disclosure egress (Phase 525.C) ─────────────────────────────
//
// These narrative tools are the SDK's fact-reading tool executors: a
// narrative's `Metric` spans carry fact-referenced values (Phase 521),
// so a `get_narrative` / `get_narrative_section` result is a fact
// egress surface. When the fact companion's disclosure gate is
// composed, every fact ref in a returned document is checked at the
// `FactToolResult` surface; a denied fact's *value* is redacted to a
// policy-naming marker and the payload carries a typed `withheldFacts`
// list — the model can explain that a value exists but is restricted,
// without ever seeing it. No gate in DI ⇒ pass-through (GP 13);
// `publish_narrative` additionally refuses at the
// `FactNarrativePublication` surface (a public page is an even wider
// door than a tool result).

/// One withheld fact in a tool-result payload — the typed
/// not-disclosable marker (never the value).
type private WithheldFact = {
    FactId: string
    PolicyRef: string
    /// The canonical refusal wording ("computed, but not disclosable
    /// under policy P").
    Status: string
}

let private disclosureGateOf (ctx: HttpContext) : IFactDisclosureGate option =
    match ctx.RequestServices.GetService(typeof<IFactDisclosureGate>) with
    | :? IFactDisclosureGate as gate -> Some gate
    | _ -> None

/// The caller's resolved storage-scope id — the same tenant boundary
/// `NarrativePublisher` scopes reads by, and the shard the fact store
/// resolves fact ids within (GP 4).
let private scopeIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as scope) -> scope.ScopeId
    | _ ->
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

let private userIdOf (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ -> "anonymous"

/// Check every fact ref in `document` at `surface`; returns the denied
/// (factId, policyRef) pairs. Empty when no gate is composed, the
/// document cites no facts, or everything is disclosable.
let private deniedFactsIn
    (ctx: HttpContext)
    (surface: FactEgressSurface)
    (document: NarrativeDocument)
    : Async<(string * string) list> =
    async {
        match disclosureGateOf ctx with
        | None -> return []
        | Some gate ->
            match NarrativeFacts.factRefs document |> Set.toList with
            | [] -> return []
            | refs ->
                let! verdicts = gate.Check(scopeIdOf ctx, userIdOf ctx, surface, refs)

                return
                    verdicts
                    |> Map.toList
                    |> List.choose (fun (factId, verdict) ->
                        match verdict with
                        | FactNotDisclosable policyRef -> Some(factId, policyRef)
                        | FactDisclosable -> None)
    }

/// Redact denied fact values out of a document and project the typed
/// withheld markers for the tool payload.
let private applyToolResultDisclosure
    (ctx: HttpContext)
    (document: NarrativeDocument)
    : Async<NarrativeDocument * WithheldFact list> =
    async {
        let! denied = deniedFactsIn ctx FactToolResult document

        match denied with
        | [] -> return document, []
        | denied ->
            let redacted = NarrativeFacts.redactDeniedFacts (Map.ofList denied) document

            let withheld =
                denied
                |> List.map (fun (factId, policyRef) -> {
                    FactId = factId
                    PolicyRef = policyRef
                    Status = FactDisclosureVerdict.refusalText policyRef
                })

            return redacted, withheld
    }

// ─── list_narratives ─────────────────────────────────────────────

let private listDefinition: AIToolDefinition = {
    Name = "list_narratives"
    Description =
        "List narrative outputs the user has generated on other pages in this session (or earlier sessions for persistent deployments). Returns an id, module, page, title, optional subtitle, publication timestamp and tags for each entry. Use this before `get_narrative` to discover what is available. Pass `tag` to surface only entries carrying that classification label (case-sensitive whole-string match). Results are scoped to the current user/team — the assistant cannot see other users' narratives."
    Parameters = [
        {
            Name = "limit"
            Type = "number"
            Description =
                "Maximum number of entries to return (newest first). Omit for a default of 20; the store caps its own upper bound."
            Required = false
            Default = Some "20"
        }
        {
            Name = "tag"
            Type = "string"
            Description =
                "Optional classification tag to filter by. Returns only entries whose `tags` list contains this exact string."
            Required = false
            Default = None
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private executeList (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let limit, tagFilter =
        try
            let doc = JsonDocument.Parse(argsJson)

            let l =
                match doc.RootElement.TryGetProperty("limit") with
                | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
                | _ -> 20

            let t =
                match doc.RootElement.TryGetProperty("tag") with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    let s = v.GetString()

                    if System.String.IsNullOrWhiteSpace s then None else Some s
                | _ -> None

            l, t
        with _ ->
            20, None

    let! entries = NarrativePublisher.listByTag ctx limit tagFilter
    return fableSerialize entries
}

// ─── get_narrative ───────────────────────────────────────────────

let private getDefinition: AIToolDefinition = {
    Name = "get_narrative"
    Description =
        "Fetch the full body of a single narrative entry by id (discovered via `list_narratives`). Returns the narrative rendered as Markdown — section headings, prose, lists, callouts and metrics — ready to quote or summarise. Returns an error object if the id is unknown or not visible to the current scope."
    Parameters = [
        {
            Name = "id"
            Type = "string"
            Description = "NarrativeId (GUID string) returned by `list_narratives`."
            Required = true
            Default = None
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private executeGet (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let idText =
        let doc = JsonDocument.Parse(argsJson)

        match doc.RootElement.TryGetProperty("id") with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

    match idText with
    | None ->
        return
            fableSerialize {|
                error = "Required argument 'id' is missing. Call list_narratives first to get valid ids."
            |}
    | Some idText ->

        match Guid.TryParse idText with
        | false, _ ->
            return
                fableSerialize {|
                    error = "Argument 'id' is not a valid GUID."
                    id = idText
                |}
        | true, id ->
            let! entry = NarrativePublisher.get ctx id

            match entry with
            | None ->
                return
                    fableSerialize {|
                        error =
                            "No narrative with that id is visible to this scope. It may belong to a different user or team, or have been evicted."
                        id = idText
                    |}
            | Some e ->
                // Phase 525.C — tool-result egress door: denied fact values
                // are redacted before rendering; the typed markers ride the
                // payload so the model can name the restriction.
                let! docForRender, withheld = applyToolResultDisclosure ctx e.Document
                let markdown = NarrativeMarkdown.render docForRender

                match withheld with
                | [] ->
                    let payload = {|
                        id = e.Id
                        moduleId = e.ModuleId
                        pageRoute = e.PageRoute
                        title = e.Title
                        subtitle = e.Subtitle
                        publishedAt = e.PublishedAt
                        markdown = markdown
                    |}

                    return fableSerialize payload
                | withheld ->
                    let payload = {|
                        id = e.Id
                        moduleId = e.ModuleId
                        pageRoute = e.PageRoute
                        title = e.Title
                        subtitle = e.Subtitle
                        publishedAt = e.PublishedAt
                        markdown = markdown
                        withheldFacts = withheld
                    |}

                    return fableSerialize payload
}

// ─── get_narrative_section ───────────────────────────────────────

let private getSectionDefinition: AIToolDefinition = {
    Name = "get_narrative_section"
    Description =
        "Fetch a single section from a narrative by section id, returning its markdown body. Use this when `list_narratives` and the section index from a prior `get_narrative` call show that one section answers the question — avoids returning a multi-kilobyte full body when one section is enough. Section ids are stable anchors visible in the `id` field of each section."
    Parameters = [
        {
            Name = "id"
            Type = "string"
            Description = "NarrativeId (GUID string) returned by `list_narratives`."
            Required = true
            Default = None
        }
        {
            Name = "sectionId"
            Type = "string"
            Description = "Stable section anchor (the `id` field of the section, not its heading)."
            Required = true
            Default = None
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private executeGetSection (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let parsedArgs =
        try
            let doc = JsonDocument.Parse(argsJson)

            let idText =
                match doc.RootElement.TryGetProperty("id") with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            let sectionId =
                match doc.RootElement.TryGetProperty("sectionId") with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            Some(idText, sectionId)
        with _ ->
            None

    match parsedArgs with
    | None
    | Some(None, _) ->
        return
            fableSerialize {|
                error = "Required argument 'id' is missing. Call list_narratives first to get valid ids."
            |}
    | Some(_, None) ->
        return
            fableSerialize {|
                error =
                    "Required argument 'sectionId' is missing. Section ids are stable anchors visible in the `id` field of each section returned by `get_narrative`."
            |}
    | Some(Some idText, Some sectionId) ->

        match Guid.TryParse idText with
        | false, _ ->
            return
                fableSerialize {|
                    error = "Argument 'id' is not a valid GUID."
                    id = idText
                |}
        | true, id ->
            let! entry = NarrativePublisher.get ctx id

            match entry with
            | None ->
                return
                    fableSerialize {|
                        error =
                            "No narrative with that id is visible to this scope. It may belong to a different user or team, or have been evicted."
                        id = idText
                    |}
            | Some e ->
                match e.Document.Sections |> List.tryFind (fun s -> s.Id = sectionId) with
                | None ->
                    let availableSections = e.Document.Sections |> List.map _.Id

                    return
                        fableSerialize {|
                            error = "No section with that id exists in this narrative."
                            id = idText
                            sectionId = sectionId
                            availableSectionIds = availableSections
                        |}
                | Some section ->
                    let sectionDoc: NarrativeDocument = {
                        Title = e.Document.Title
                        Subtitle = e.Document.Subtitle
                        Sections = [ section ]
                        Provenance = e.Document.Provenance
                        Lang = e.Document.Lang
                        CanonicalUrl = e.Document.CanonicalUrl
                    }

                    // Phase 525.C — tool-result egress door (see executeGet).
                    let! docForRender, withheld = applyToolResultDisclosure ctx sectionDoc
                    let markdown = NarrativeMarkdown.render docForRender

                    match withheld with
                    | [] ->
                        let payload = {|
                            id = e.Id
                            moduleId = e.ModuleId
                            pageRoute = e.PageRoute
                            title = e.Title
                            subtitle = e.Subtitle
                            publishedAt = e.PublishedAt
                            sectionId = section.Id
                            sectionHeading = section.Heading
                            markdown = markdown
                        |}

                        return fableSerialize payload
                    | withheld ->
                        let payload = {|
                            id = e.Id
                            moduleId = e.ModuleId
                            pageRoute = e.PageRoute
                            title = e.Title
                            subtitle = e.Subtitle
                            publishedAt = e.PublishedAt
                            sectionId = section.Id
                            sectionHeading = section.Heading
                            markdown = markdown
                            withheldFacts = withheld
                        |}

                        return fableSerialize payload
}

// ─── publish_narrative ───────────────────────────────────────────

let private publishDefinition: AIToolDefinition = {
    Name = "publish_narrative"
    Description =
        "Publish a stored narrative (one the user has generated and saved) as a public-facing page at the given slug. Looks up the narrative by id within the current scope, wraps it in a `PublicPage` envelope (Body = Narrative), and writes it through the registered `INarrativePagePublisher`. Returns the canonical slug the page lives at. Use this after `get_narrative` confirms the source content; layouts + canonical-URL + lang flow through the document's existing fields, with optional overrides for title / description / layout. Returns an error if no publisher is registered (deployment without PublicRendering) or if the narrative id is unknown to this scope."
    Parameters = [
        {
            Name = "id"
            Type = "string"
            Description = "NarrativeId (GUID string) returned by `list_narratives`."
            Required = true
            Default = None
        }
        {
            Name = "slug"
            Type = "string"
            Description =
                "URL slug for the published page (no leading slash; nested slugs like `blog/q3-release` encode a directory shape)."
            Required = true
            Default = None
        }
        {
            Name = "title"
            Type = "string"
            Description =
                "Optional override for the published page's Title (used in `<title>` and crawler results). Defaults to the document's own Title."
            Required = false
            Default = None
        }
        {
            Name = "description"
            Type = "string"
            Description =
                "Optional override for the published page's Description (used in `<meta name=description>` and Open Graph). Defaults to the document's Subtitle when set, empty string otherwise."
            Required = false
            Default = None
        }
        {
            Name = "layout"
            Type = "string"
            Description =
                "Optional layout name to render under. Must match a layout registered via `PublicRenderingServerApp.withLayout`. Unknown / omitted layouts fall back to the first-registered layout."
            Required = false
            Default = None
        }
        {
            Name = "canonicalUrl"
            Type = "string"
            Description =
                "Optional canonical absolute URL the page should be indexed under. Overrides the document's own `CanonicalUrl` field. Recommended whenever the same content is published at multiple paths or syndicated externally."
            Required = false
            Default = None
        }
        {
            Name = "lang"
            Type = "string"
            Description =
                "Optional BCP-47 language tag (e.g. `en-GB`, `fr`). Overrides the document's own `Lang` field. Drives `<html lang>` and Open Graph `og:locale`."
            Required = false
            Default = None
        }
        {
            Name = "collisionPolicy"
            Type = "string"
            Description =
                "What to do when the slug already has a published page. `overwrite` (default) writes regardless — previous version stays in the entity store's version history. `reject` returns an error and refuses to publish. `suffix` finds the next free slug (`slug-2`, `slug-3`, …) and publishes there; the returned `slug` reflects the actual slug used."
            Required = false
            Default = Some "overwrite"
        }
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private parseCollisionPolicy (raw: string option) : SlugCollisionPolicy =
    match raw with
    | Some s ->
        match s.ToLowerInvariant() with
        | "reject"
        | "rejectifexists" -> RejectIfExists
        | "suffix"
        | "autosuffix" -> AutoSuffix
        | _ -> OverwriteExisting
    | None -> OverwriteExisting

let private executePublish (ctx: HttpContext) (argsJson: string) : Async<string> = async {
    let parsed =
        try
            let doc = JsonDocument.Parse(argsJson)

            let getStr (name: string) =
                match doc.RootElement.TryGetProperty(name) with
                | true, v when v.ValueKind = JsonValueKind.String ->
                    let s = v.GetString()

                    if System.String.IsNullOrWhiteSpace s then None else Some s
                | _ -> None

            Some(
                getStr "id",
                getStr "slug",
                getStr "title",
                getStr "description",
                getStr "layout",
                getStr "canonicalUrl",
                getStr "lang",
                getStr "collisionPolicy"
            )
        with _ ->
            None

    match parsed with
    | None
    | Some(None, _, _, _, _, _, _, _) ->
        return
            fableSerialize {|
                error = "Required argument 'id' is missing. Call list_narratives first to get valid ids."
            |}
    | Some(_, None, _, _, _, _, _, _) ->
        return
            fableSerialize {|
                error =
                    "Required argument 'slug' is missing. Pass a URL slug like 'blog/q3-release' (no leading slash)."
            |}
    | Some(Some idText, Some slug, titleOpt, descOpt, layoutOpt, canonicalOpt, langOpt, collisionOpt) ->
        match Guid.TryParse idText with
        | false, _ ->
            return
                fableSerialize {|
                    error = "Argument 'id' is not a valid GUID."
                    id = idText
                |}
        | true, id ->
            // Phase 80b — per-request authoriser gate. If an
            // AIPublishAuthoriser is registered, call it before
            // doing any further work; a `false` return short-
            // circuits to an unauthorised error response.
            let! authorised =
                match ctx.RequestServices.GetService(typeof<AIPublishAuthoriser>) with
                | :? AIPublishAuthoriser as (AIPublishAuthoriser f) -> f ctx
                | _ -> async { return true }

            if not authorised then
                return
                    fableSerialize {|
                        error =
                            "This session is not authorised to publish. The deployment's AIPublishAuthoriser refused the request."
                        id = idText
                        slug = slug
                    |}
            else
                let! entry = NarrativePublisher.get ctx id

                match entry with
                | None ->
                    return
                        fableSerialize {|
                            error =
                                "No narrative with that id is visible to this scope. It may belong to a different user or team, or have been evicted."
                            id = idText
                        |}
                | Some entry ->
                    // Phase 525.D — narrative-publication egress door. A
                    // public page is the widest surface a narrative can
                    // reach; a document whose Metric spans reference facts
                    // the gate denies at `FactNarrativePublication` is
                    // refused outright (not redacted — publication is a
                    // deliberate act; the caller should resolve the refs,
                    // not silently ship a redacted page). The diagnostic
                    // names the offending refs + policies, never the values.
                    let! deniedForPublication = deniedFactsIn ctx FactNarrativePublication entry.Document

                    match deniedForPublication with
                    | _ :: _ ->
                        let offending =
                            deniedForPublication
                            |> List.map (fun (factId, policyRef) -> sprintf "%s (policy %s)" factId policyRef)
                            |> String.concat "; "

                        return
                            fableSerialize {|
                                error =
                                    sprintf
                                        "Publication refused: the narrative references %d fact(s) that are not disclosable — %s. Remove or replace the offending metric spans, or have the facts reclassified, then publish again."
                                        deniedForPublication.Length
                                        offending
                                id = idText
                                slug = slug
                                withheldFacts =
                                    deniedForPublication
                                    |> List.map (fun (factId, policyRef) -> {
                                        FactId = factId
                                        PolicyRef = policyRef
                                        Status = FactDisclosureVerdict.refusalText policyRef
                                    })
                            |}
                    | [] ->

                        // Apply caller-supplied overrides onto the document
                        // before handing to the publisher. Overrides win over
                        // document fields when present.
                        let docWithOverrides = {
                            entry.Document with
                                Lang = langOpt |> Option.orElse entry.Document.Lang
                                CanonicalUrl = canonicalOpt |> Option.orElse entry.Document.CanonicalUrl
                        }

                        let collisionPolicy = parseCollisionPolicy collisionOpt

                        let publisher =
                            match ctx.RequestServices.GetService(typeof<INarrativePagePublisher>) with
                            | :? INarrativePagePublisher as p -> Some p
                            | _ -> None

                        match publisher with
                        | None ->
                            return
                                fableSerialize {|
                                    error =
                                        "No INarrativePagePublisher is registered. This deployment does not have PublicRendering wired in, or has not enabled AI publishing via withAIPublishEnabled true."
                                |}
                        | Some publisher ->
                            let! outcome =
                                publisher.PublishAsync(
                                    slug,
                                    titleOpt,
                                    descOpt,
                                    layoutOpt,
                                    collisionPolicy,
                                    docWithOverrides
                                )

                            match outcome with
                            | PublishSucceeded canonicalSlug ->
                                return
                                    fableSerialize {|
                                        id = idText
                                        slug = canonicalSlug
                                        published = true
                                    |}
                            | PublishFailed reason ->
                                return
                                    fableSerialize {|
                                        error = reason
                                        id = idText
                                        slug = slug
                                    |}
}

// ─── list_layouts ────────────────────────────────────────────────

let private listLayoutsDefinition: AIToolDefinition = {
    Name = "list_layouts"
    Description =
        "List the public-rendering layout names registered for this deployment. Use this before `publish_narrative` to pick an appropriate `layout` argument (otherwise the publisher silently falls back to the first-registered layout). Returns an empty array when no `ILayoutCatalog` is registered (PublicRendering not wired in)."
    Parameters = []
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
}

let private executeListLayouts (ctx: HttpContext) (_argsJson: string) : Async<string> = async {
    let catalog =
        match ctx.RequestServices.GetService(typeof<ILayoutCatalog>) with
        | :? ILayoutCatalog as c -> Some c
        | _ -> None

    match catalog with
    | None ->
        return
            fableSerialize {|
                layouts = ([]: string list)
                note = "no layout catalog registered"
            |}
    | Some c ->
        let names = c.ListLayoutNames()
        return fableSerialize {| layouts = names |}
}

// ─── Registration ────────────────────────────────────────────────

/// Built-in narrative tools — auto-registered by `composeWithAI`.
let builtInTools: RegisteredTool list = [
    createTool listDefinition executeList
    createTool getDefinition executeGet
    createTool getSectionDefinition executeGetSection
    createTool publishDefinition executePublish
    createTool listLayoutsDefinition executeListLayouts
]