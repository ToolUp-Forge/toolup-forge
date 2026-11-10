module ToolUp.AI.NarrativeTools

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.ToolHelpers
open ToolUp.Platform.Infra
open ToolUp.Platform.Scope
open ToolUp.Platform.Jobs.SmokeTests
open ToolUp.Platform.Compose

/// Built-in AI tools for surfacing narrative output produced on other
/// pages or earlier in the session. The assistant can list the user's
/// recent narratives (`list_narratives`) and fetch the full markdown
/// body of any one of them by id (`get_narrative`). Both are scoped by
/// the request's `StorageScope` — a user in one team only sees their
/// own scope's narratives (GP 4).
///
/// Wired automatically by `composeWithAI`; apps do not need to register
/// these explicitly.

// ─── Disclosure egress ───────────────────────────────────────────
//
// These narrative tools are the SDK's fact-reading tool executors: a
// narrative's `Metric` spans carry fact-referenced values, so a `get_narrative` / `get_narrative_section` result is a fact
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

/// Check every fact ref in `document` at `surface`; returns the denied
/// (factId, policyRef) pairs. Empty when no gate is composed, the
/// document cites no facts, or everything is disclosable.
let private deniedFactsIn
    (context: ToolContext)
    (surface: FactEgressSurface)
    (document: NarrativeDocument)
    : Async<(string * string) list> =
    async {
        match disclosureGateOf context.Request with
        | None -> return []
        | Some gate ->
            match NarrativeFacts.factRefs document |> Set.toList with
            | [] -> return []
            | refs ->
                let! verdicts =
                    gate.Check(
                        StorageScopeResolver.ScopeResolution.forRequest context.Request,
                        context.Access.UserId,
                        surface,
                        refs
                    )

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
    (context: ToolContext)
    (document: NarrativeDocument)
    : Async<NarrativeDocument * WithheldFact list> =
    async {
        let! denied = deniedFactsIn context FactToolResult document

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

// ─── Arguments ───────────────────────────────────────────────────

/// A malformed argument, as the fault the model is shown.
let private invalid (toolName: string) (refusal: ToolArgumentRefusal) : ToolOutcome =
    ToolOutcome.Faulted(ToolFault.InvalidArguments(toolName, ToolArgumentRefusal.toMessage refusal))

/// The narrative id every tool but `list_narratives` names.
let private narrativeId (toolName: string) (args: ToolArgs) : Result<Guid * string, ToolOutcome> =
    match ToolHelpers.decodeArg<string> args.Root "id" ToolSchema.text with
    | Error refusal -> Error(invalid toolName refusal)
    | Ok text ->
        match Guid.TryParse text with
        | true, id -> Ok(id, text)
        | _ ->
            Error(
                ToolOutcome.Faulted(
                    ToolFault.InvalidArguments(
                        toolName,
                        $"argument 'id' is not a valid GUID (received {text}); call list_narratives to get valid ids."
                    )
                )
            )

/// An optional text argument; blank reads as absent.
let private optionalText (args: ToolArgs) (name: string) : Result<string option, ToolArgumentRefusal> =
    ToolHelpers.decodeOptionalArg<string> args.Root name ToolSchema.text
    |> Result.map (Option.filter (String.IsNullOrWhiteSpace >> not))

let private notVisible (idText: string) =
    ToolOutcome.Produced(
        fableSerialize {|
            error =
                "No narrative with that id is visible to this scope. It may belong to a different user or team, or have been evicted."
            id = idText
        |}
    )

// ─── List_narratives ─────────────────────────────────────────────

let private listDefinition: AIToolDefinition = {
    Name = "list_narratives"
    Description =
        "List narrative outputs the user has generated on other pages in this session (or earlier sessions for persistent deployments). Returns an id, module, page, title, optional subtitle, publication timestamp and tags for each entry. Use this before `get_narrative` to discover what is available. Pass `tag` to surface only entries carrying that classification label (case-sensitive whole-string match). Results are scoped to the current user/team — the assistant cannot see other users' narratives."
    Parameters = [
        ToolSchema.parameter
            "limit"
            "Maximum number of entries to return (newest first). Omit for a default of 20; the store caps its own upper bound."
            false
            ToolSchema.integer
        ToolSchema.parameter
            "tag"
            "Optional classification tag to filter by. Returns only entries whose `tags` list contains this exact string."
            false
            ToolSchema.text
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeList (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match ToolHelpers.decodeOptionalArg<int> args.Root "limit" ToolSchema.integer, optionalText args "tag" with
    | Error refusal, _
    | _, Error refusal -> return invalid listDefinition.Name refusal
    | Ok limit, Ok tagFilter ->
        let! entries = NarrativePublisher.listByTag context.Request (Option.defaultValue 20 limit) tagFilter
        return ToolOutcome.Produced(fableSerialize entries)
}

// ─── Get_narrative ───────────────────────────────────────────────

let private getDefinition: AIToolDefinition = {
    Name = "get_narrative"
    Description =
        "Fetch the full body of a single narrative entry by id (discovered via `list_narratives`). Returns the narrative rendered as Markdown — section headings, prose, lists, callouts and metrics — ready to quote or summarise. Returns an error object if the id is unknown or not visible to the current scope."
    Parameters = [
        ToolSchema.parameter "id" "NarrativeId (GUID string) returned by `list_narratives`." true ToolSchema.text
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeGet (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match narrativeId getDefinition.Name args with
    | Error fault -> return fault
    | Ok(id, idText) ->
        match! NarrativePublisher.get context.Request id with
        | None -> return notVisible idText
        | Some e ->
            // Tool-result egress door: denied fact values are redacted before
            // rendering; the typed markers ride the payload so the model can
            // name the restriction.
            let! docForRender, withheld = applyToolResultDisclosure context e.Document
            let markdown = NarrativeMarkdown.render docForRender

            match withheld with
            | [] ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            id = e.Id
                            moduleId = e.ModuleId
                            pageRoute = e.PageRoute
                            title = e.Title
                            subtitle = e.Subtitle
                            publishedAt = e.PublishedAt
                            markdown = markdown
                        |}
                    )
            | withheld ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            id = e.Id
                            moduleId = e.ModuleId
                            pageRoute = e.PageRoute
                            title = e.Title
                            subtitle = e.Subtitle
                            publishedAt = e.PublishedAt
                            markdown = markdown
                            withheldFacts = withheld
                        |}
                    )
}

// ─── Get_narrative_section ───────────────────────────────────────

let private getSectionDefinition: AIToolDefinition = {
    Name = "get_narrative_section"
    Description =
        "Fetch a single section from a narrative by section id, returning its markdown body. Use this when `list_narratives` and the section index from a prior `get_narrative` call show that one section answers the question — avoids returning a multi-kilobyte full body when one section is enough. Section ids are stable anchors visible in the `id` field of each section."
    Parameters = [
        ToolSchema.parameter "id" "NarrativeId (GUID string) returned by `list_narratives`." true ToolSchema.text
        ToolSchema.parameter
            "sectionId"
            "Stable section anchor (the `id` field of the section, not its heading)."
            true
            ToolSchema.text
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeGetSection (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    match
        narrativeId getSectionDefinition.Name args, ToolHelpers.decodeArg<string> args.Root "sectionId" ToolSchema.text
    with
    | Error fault, _ -> return fault
    | _, Error refusal -> return invalid getSectionDefinition.Name refusal
    | Ok(id, idText), Ok sectionId ->
        match! NarrativePublisher.get context.Request id with
        | None -> return notVisible idText
        | Some e ->
            match e.Document.Sections |> List.tryFind (fun s -> s.Id = sectionId) with
            | None ->
                return
                    ToolOutcome.Produced(
                        fableSerialize {|
                            error = "No section with that id exists in this narrative."
                            id = idText
                            sectionId = sectionId
                            availableSectionIds = e.Document.Sections |> List.map _.Id
                        |}
                    )
            | Some section ->
                let sectionDoc: NarrativeDocument = {
                    Title = e.Document.Title
                    Subtitle = e.Document.Subtitle
                    Sections = [ section ]
                    Provenance = e.Document.Provenance
                    Lang = e.Document.Lang
                    CanonicalUrl = e.Document.CanonicalUrl
                }

                // Tool-result egress door (see executeGet).
                let! docForRender, withheld = applyToolResultDisclosure context sectionDoc
                let markdown = NarrativeMarkdown.render docForRender

                match withheld with
                | [] ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
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
                        )
                | withheld ->
                    return
                        ToolOutcome.Produced(
                            fableSerialize {|
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
                        )
}

// ─── Publish_narrative ───────────────────────────────────────────

/// The collision policy's tokens, declared.
let private collisionPolicySchema =
    ToolSchema.enumOf [ "overwrite"; "reject"; "suffix" ]

let private publishDefinition: AIToolDefinition = {
    Name = "publish_narrative"
    Description =
        "Publish a stored narrative (one the user has generated and saved) as a public-facing page at the given slug. Looks up the narrative by id within the current scope, wraps it in a `PublicPage` envelope (Body = Narrative), and writes it through the registered `INarrativePagePublisher`. Returns the canonical slug the page lives at. Use this after `get_narrative` confirms the source content; layouts + canonical-URL + lang flow through the document's existing fields, with optional overrides for title / description / layout. Returns an error if no publisher is registered (deployment without PublicRendering) or if the narrative id is unknown to this scope."
    Parameters = [
        ToolSchema.parameter "id" "NarrativeId (GUID string) returned by `list_narratives`." true ToolSchema.text
        ToolSchema.parameter
            "slug"
            "URL slug for the published page (no leading slash; nested slugs like `blog/q3-release` encode a directory shape)."
            true
            ToolSchema.text
        ToolSchema.parameter
            "title"
            "Optional override for the published page's Title (used in `<title>` and crawler results). Defaults to the document's own Title."
            false
            ToolSchema.text
        ToolSchema.parameter
            "description"
            "Optional override for the published page's Description (used in `<meta name=description>` and Open Graph). Defaults to the document's Subtitle when set, empty string otherwise."
            false
            ToolSchema.text
        ToolSchema.parameter
            "layout"
            "Optional layout name to render under. Must match a layout registered via `PublicRenderingOptions.withLayout`. Omitted → the deployment's default layout; a name that is not registered is refused."
            false
            ToolSchema.text
        ToolSchema.parameter
            "canonicalUrl"
            "Optional canonical absolute URL the page should be indexed under. Overrides the document's own `CanonicalUrl` field. Recommended whenever the same content is published at multiple paths or syndicated externally."
            false
            ToolSchema.text
        ToolSchema.parameter
            "lang"
            "Optional BCP-47 language tag (e.g. `en-GB`, `fr`). Overrides the document's own `Lang` field. Drives `<html lang>` and Open Graph `og:locale`."
            false
            ToolSchema.text
        ToolSchema.parameter
            "collisionPolicy"
            "What to do when the slug already has a published page. `overwrite` (default) writes regardless — previous version stays in the entity store's version history. `reject` returns an error and refuses to publish. `suffix` finds the next free slug (`slug-2`, `slug-3`, …) and publishes there; the returned `slug` reflects the actual slug used."
            false
            collisionPolicySchema
    ]
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.declare [ ReadContent; WriteState "narratives" ]
}

/// What to do when the slug already has a page, by its declared token.
let private collisionPolicyOf (token: string option) : SlugCollisionPolicy =
    match token with
    | Some "reject" -> RejectIfExists
    | Some "suffix" -> AutoSuffix
    | _ -> OverwriteExisting

/// The publish arguments, decoded against the declaration.
type private PublishArgs = {
    Id: Guid
    IdText: string
    Slug: string
    Title: string option
    Description: string option
    Layout: string option
    CanonicalUrl: string option
    Lang: string option
    Collision: SlugCollisionPolicy
}

let private publishArgs (args: ToolArgs) : Result<PublishArgs, ToolOutcome> =
    let fail = invalid publishDefinition.Name

    match narrativeId publishDefinition.Name args with
    | Error fault -> Error fault
    | Ok(id, idText) ->
        match
            ToolHelpers.decodeArg<string> args.Root "slug" ToolSchema.text,
            optionalText args "title",
            optionalText args "description",
            optionalText args "layout",
            optionalText args "canonicalUrl",
            optionalText args "lang",
            ToolHelpers.decodeOptionalArg<string> args.Root "collisionPolicy" collisionPolicySchema
        with
        | Ok slug, Ok title, Ok description, Ok layout, Ok canonicalUrl, Ok lang, Ok collision ->
            Ok {
                Id = id
                IdText = idText
                Slug = slug
                Title = title
                Description = description
                Layout = layout
                CanonicalUrl = canonicalUrl
                Lang = lang
                Collision = collisionPolicyOf collision
            }
        | Error r, _, _, _, _, _, _
        | _, Error r, _, _, _, _, _
        | _, _, Error r, _, _, _, _
        | _, _, _, Error r, _, _, _
        | _, _, _, _, Error r, _, _
        | _, _, _, _, _, Error r, _
        | _, _, _, _, _, _, Error r -> Error(fail r)

let private executePublish (context: ToolContext) (args: ToolArgs) : Async<ToolOutcome> = async {
    let ctx = context.Request

    match publishArgs args with
    | Error fault -> return fault
    | Ok a ->
        let refused (body: obj) =
            ToolOutcome.Produced(fableSerialize body)

        // The deployment's per-request publish authoriser, when one is
        // registered, decides before any further work.
        let! authorised =
            match ctx.RequestServices.GetService(typeof<AIPublishAuthoriser>) with
            | :? AIPublishAuthoriser as (AIPublishAuthoriser f) -> f ctx
            | _ -> async { return true }

        if not authorised then
            return
                refused {|
                    error =
                        "This session is not authorised to publish. The deployment's AIPublishAuthoriser refused the request."
                    id = a.IdText
                    slug = a.Slug
                |}
        else
            match! NarrativePublisher.get ctx a.Id with
            | None -> return notVisible a.IdText
            | Some entry ->
                // Narrative-publication egress door: a document referencing
                // facts the gate denies at publication is refused outright,
                // not redacted. The diagnostic names refs and policies, never
                // the values.
                match! deniedFactsIn context FactNarrativePublication entry.Document with
                | _ :: _ as denied ->
                    let offending =
                        denied
                        |> List.map (fun (factId, policyRef) -> sprintf "%s (policy %s)" factId policyRef)
                        |> String.concat "; "

                    return
                        refused {|
                            error =
                                sprintf
                                    "Publication refused: the narrative references %d fact(s) that are not disclosable — %s. Remove or replace the offending metric spans, or have the facts reclassified, then publish again."
                                    denied.Length
                                    offending
                            id = a.IdText
                            slug = a.Slug
                            withheldFacts =
                                denied
                                |> List.map (fun (factId, policyRef) -> {
                                    FactId = factId
                                    PolicyRef = policyRef
                                    Status = FactDisclosureVerdict.refusalText policyRef
                                })
                        |}
                | [] ->
                    // Overrides win over the document's own fields.
                    let document = {
                        entry.Document with
                            Lang = a.Lang |> Option.orElse entry.Document.Lang
                            CanonicalUrl = a.CanonicalUrl |> Option.orElse entry.Document.CanonicalUrl
                    }

                    match ctx.RequestServices.GetService(typeof<INarrativePagePublisher>) with
                    | :? INarrativePagePublisher as publisher ->
                        // The caller is the principal the page's lifecycle row records.
                        match!
                            publisher.PublishAsync(
                                EntityPrincipal.ofPrincipal context.Access.UserId,
                                a.Slug,
                                a.Title,
                                a.Description,
                                a.Layout,
                                a.Collision,
                                document
                            )
                        with
                        | PublishSucceeded canonicalSlug ->
                            return
                                refused {|
                                    id = a.IdText
                                    slug = canonicalSlug
                                    published = true
                                |}
                        | PublishFailed reason ->
                            return
                                refused {|
                                    error = reason
                                    id = a.IdText
                                    slug = a.Slug
                                |}
                    | _ ->
                        return
                            refused {|
                                error =
                                    "No INarrativePagePublisher is registered. This deployment does not have PublicRendering wired in, or has not enabled AI publishing via withAIPublishEnabled true."
                            |}
}

// ─── List_layouts ────────────────────────────────────────────────

let private listLayoutsDefinition: AIToolDefinition = {
    Name = "list_layouts"
    Description =
        "List the public-rendering layout names registered for this deployment. Use this before `publish_narrative` to pick an appropriate `layout` argument (omitting it publishes under the deployment's default layout; a name that is not registered is refused). Returns an empty array when no `ILayoutCatalog` is registered (PublicRendering not wired in)."
    Parameters = []
    SourceModule = "ToolUp.Platform"
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readContent
}

let private executeListLayouts (context: ToolContext) (_: ToolArgs) : Async<ToolOutcome> = async {
    match context.Request.RequestServices.GetService(typeof<ILayoutCatalog>) with
    | :? ILayoutCatalog as catalog ->
        return
            ToolOutcome.Produced(
                fableSerialize {|
                    layouts = catalog.ListLayoutNames()
                |}
            )
    | _ ->
        return
            ToolOutcome.Produced(
                fableSerialize {|
                    layouts = ([]: string list)
                    note = "no layout catalog registered"
                |}
            )
}

// ─── Registration ────────────────────────────────────────────────

/// Built-in narrative tools — auto-registered by `composeWithAI`.
let builtInTools: RegisteredTool list = [
    createTypedTool listDefinition executeList
    createTypedTool getDefinition executeGet
    createTypedTool getSectionDefinition executeGetSection
    createTypedTool publishDefinition executePublish
    createTypedTool listLayoutsDefinition executeListLayouts
]