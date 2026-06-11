// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Narrative

// ─── Phase 80a — Default INarrativePagePublisher implementation ────
//
// Bridges the AI tool `publish_narrative` (defined in
// `ToolUp.AI.Server/Server/NarrativeTools.fs`) to PublicRendering's
// `IEntityStore<PublicPageEntity>` backing store. Registered in DI by
// `PublicRenderingCompose.run` when public rendering is enabled; AI
// tools resolve it from the request services at publish time.
//
// The implementation is deliberately thin — every interesting concern
// (overrides, document mutation, scope resolution) is already handled
// by the caller. The publisher's job is just to construct the page
// envelope, encode the slug, and write it through `IEntityStore.Save`.

// ─── Phase 91 — AI authoring hardening (guardrails + draft gate) ───
//
// `publish_narrative` lets an AI tool write a page to the public surface.
// Phase 80a shipped that as an immediate `Published` / `Public` write.
// These guardrails let a deployment constrain the AI authoring path:
//   * **forced Draft landing** — land every AI publish as `Draft`
//     (Phase 89 `PublishStatus.Draft`), so it is NOT publicly served
//     until a human moves it through the review workflow. AI drafts
//     never go live unattended.
//   * **layout allow-list** — reject a `layoutHint` outside an approved
//     set (an AI can't publish under an arbitrary layout).
//   * **forced audience** — pin the published page's `PageAudience`
//     (Phase 86), so AI can never publish a wider-audience page than
//     policy allows.
//   * **template shape cap** — reject documents with more than
//     `MaxSections` sections (a crude but effective "constrain the
//     shape" guard against runaway AI output).
//
// GP 11 — the defaults (`NarrativePublishGuardrails.defaults`) reproduce
// the Phase 80a behaviour exactly (Published, Public, any registered
// layout, no section cap), so an existing deployment is unchanged until
// it opts into hardening. `aiHardened` is the recommended opt-in for
// multi-tenant / untrusted-AI deployments.

/// Compose-time guardrails applied to the AI publishing path. Defaults
/// reproduce the pre-91 behaviour (GP 11).
type NarrativePublishGuardrails = {
    /// When `true`, every AI publish lands as `Draft` regardless of the
    /// caller's collision policy, so it is not publicly served until a
    /// human reviews it (Phase 89 workflow). Default `false` (Phase 80a
    /// behaviour — immediate publish).
    ForceDraft: bool
    /// When `Some names`, an explicit `layoutHint` must be one of `names`
    /// or the publish is refused. `None` (default) → any registered layout
    /// is accepted (Phase 80a behaviour). A `None` hint always falls back
    /// to the first-registered layout and is not allow-list-checked.
    AllowedLayouts: Set<string> option
    /// The audience every AI-published page is pinned to. Default
    /// `PageAudience.Public` (Phase 80a behaviour).
    Audience: PageAudience
    /// When `Some n`, a document with more than `n` sections is refused
    /// (template shape constraint). `None` (default) → no cap.
    MaxSections: int option
}

module NarrativePublishGuardrails =
    /// Pre-91 behaviour: immediate `Published`, `Public`, any registered
    /// layout, no section cap (GP 11).
    let defaults: NarrativePublishGuardrails = {
        ForceDraft = false
        AllowedLayouts = None
        Audience = PageAudience.Public
        MaxSections = None
    }

    /// Recommended opt-in for deployments that let an untrusted / shared
    /// AI publish: force a `Draft` landing so nothing goes live without a
    /// human review pass. Audience stays `Public` (a reviewed draft is
    /// promoted deliberately); add `withAudience` / `withAllowedLayouts`
    /// to tighten further.
    let aiHardened: NarrativePublishGuardrails = { defaults with ForceDraft = true }

    let withForceDraft (v: bool) (g: NarrativePublishGuardrails) : NarrativePublishGuardrails = {
        g with
            ForceDraft = v
    }

    let withAllowedLayouts (names: string list) (g: NarrativePublishGuardrails) : NarrativePublishGuardrails = {
        g with
            AllowedLayouts = Some(Set.ofList names)
    }

    let withAudience (a: PageAudience) (g: NarrativePublishGuardrails) : NarrativePublishGuardrails = {
        g with
            Audience = a
    }

    let withMaxSections (n: int) (g: NarrativePublishGuardrails) : NarrativePublishGuardrails = {
        g with
            MaxSections = Some n
    }

/// Optional layouts hint passed by `PublicRenderingCompose.run` so the
/// publisher can fall back to the first-registered layout when the
/// caller's `layoutHint` is `None` or doesn't match. Empty when the
/// deployment has registered no layouts (an unusual but valid
/// configuration when the deployment's pages all use a single
/// implicit shape — Save still succeeds, the PublicPageHandler will
/// surface "no layout registered" at render time).
type internal LayoutHint = LayoutHint of LayoutName list

type PublicRenderingNarrativePagePublisher
    (
        entityStore: IEntityStore,
        registeredLayouts: LayoutName list,
        renderCacheInvalidator: IRenderCacheInvalidation option,
        guardrails: NarrativePublishGuardrails,
        // Phase 109 — optional IndexNow push: when composed, a successful
        // publish pings the just-written slug to IndexNow immediately (the
        // same publish hook the render-cache purge rides). `None` when no
        // IndexNow is composed (or `PingOnPublish = false`).
        indexNowService: IIndexNowService option
    ) =

    let resolveLayout (hint: string option) : LayoutName =
        match hint with
        | Some name when registeredLayouts |> List.contains (LayoutName name) -> LayoutName name
        | _ ->
            match registeredLayouts with
            | first :: _ -> first
            // No layouts registered — write a placeholder layout name.
            // PublicPageHandler will surface the missing-layout error
            // at render time (a more useful place to fail than at
            // publish time, since the operator can register a layout
            // and immediately make published pages work).
            | [] -> LayoutName ""

    let sanitiseSlug (raw: string) : string =
        // Strip a leading slash (caller may pass with or without) and
        // trim whitespace. We don't lowercase or rewrite — slugs are
        // case-sensitive (filesystem convention) and the caller owns
        // their canonical form.
        raw.TrimStart('/').Trim()

    let slugExists (slug: string) : Async<bool> = async {
        let! result =
            entityStore.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)

        match result with
        | Ok _ -> return true
        | Error _ -> return false
    }

    /// Walk `slug`, `slug-2`, `slug-3`, … until a free slug is found.
    /// Caps at 100 attempts to avoid a runaway when a misconfigured
    /// IEntityStore reports every slug as occupied.
    let rec findFreeSlug (baseSlug: string) (attempt: int) : Async<string option> = async {
        if attempt > 100 then
            return None
        else
            let candidate =
                if attempt = 1 then
                    baseSlug
                else
                    sprintf "%s-%d" baseSlug attempt

            let! exists = slugExists candidate

            if exists then
                return! findFreeSlug baseSlug (attempt + 1)
            else
                return Some candidate
    }

    interface INarrativePagePublisher with
        member _.PublishAsync(slug, titleOverride, descriptionOverride, layoutHint, collisionPolicy, document) = async {
            let requestedSlug = sanitiseSlug slug

            // Phase 91 — guardrail rejections first (cheapest, no store
            // access). An explicit layout hint outside the allow-list, or a
            // document exceeding the section cap, is refused before any
            // slug-collision work.
            let guardrailViolation =
                match guardrails.AllowedLayouts, layoutHint with
                | Some allowed, Some h when not (Set.contains h allowed) ->
                    Some(sprintf "layout '%s' is not permitted for AI publishing (guardrail allow-list)" h)
                | _ ->
                    match guardrails.MaxSections with
                    | Some n when List.length document.Sections > n ->
                        Some(
                            sprintf
                                "document has %d sections, exceeding the %d-section guardrail for AI publishing"
                                (List.length document.Sections)
                                n
                        )
                    | _ -> None

            if System.String.IsNullOrWhiteSpace requestedSlug then
                return PublishFailed "slug is required (received an empty / whitespace-only value)"
            elif guardrailViolation.IsSome then
                return PublishFailed guardrailViolation.Value
            else
                // Resolve the target slug per collision policy. Reject
                // and AutoSuffix both consult the entity store first;
                // OverwriteExisting skips the check (the existing Save
                // semantics already handle the overwrite).
                let! resolvedSlug = async {
                    match collisionPolicy with
                    | OverwriteExisting -> return Some requestedSlug
                    | RejectIfExists ->
                        let! exists = slugExists requestedSlug
                        if exists then return None else return Some requestedSlug
                    | AutoSuffix -> return! findFreeSlug requestedSlug 1
                }

                match resolvedSlug with
                | None ->
                    match collisionPolicy with
                    | RejectIfExists ->
                        return
                            PublishFailed(
                                sprintf
                                    "slug '%s' is already occupied (RejectIfExists policy); pass a different slug or use OverwriteExisting / AutoSuffix to proceed"
                                    requestedSlug
                            )
                    | _ ->
                        return
                            PublishFailed(
                                sprintf
                                    "could not find a free slug starting from '%s' within 100 attempts"
                                    requestedSlug
                            )
                | Some canonicalSlug ->
                    let title =
                        titleOverride
                        |> Option.defaultValue (
                            if System.String.IsNullOrWhiteSpace document.Title then
                                "(untitled)"
                            else
                                document.Title
                        )

                    let description =
                        descriptionOverride |> Option.orElse document.Subtitle |> Option.defaultValue ""

                    let page: PublicPage = {
                        Slug = Slug canonicalSlug
                        Title = title
                        Description = description
                        Body = Narrative document
                        Layout = resolveLayout layoutHint
                        Frontmatter = Map.empty
                        PublishedAt = Some DateTimeOffset.UtcNow
                        Collection = None
                        // Phase 91 — forced-draft guardrail: when enabled,
                        // the page lands as Draft and is not publicly served
                        // until a human moves it through the Phase 89 review
                        // workflow. Default (off) preserves the Phase 80a
                        // immediate-publish behaviour (GP 11).
                        Status = (if guardrails.ForceDraft then Draft else Published)
                        // Phase 91 — audience guardrail. Default Public
                        // (Phase 80a / 86 behaviour); a deployment can pin a
                        // narrower audience so AI never widens reach.
                        Audience = guardrails.Audience
                    }

                    let envelope = PublicPageEntity.fromPage page

                    let! result = entityStore.Save<PublicPageEntity>(PublicPageEntity.PublicScope, envelope)

                    match result with
                    | Ok _ ->
                        // Phase 84 — purge any cached render of this slug so
                        // the republished content is served immediately
                        // rather than waiting out the prior entry's TTL. A
                        // no-op when no render cache is composed.
                        match renderCacheInvalidator with
                        | Some inv -> do! inv.PurgeSlug canonicalSlug
                        | None -> ()

                        // Phase 109 — push the just-published URL to IndexNow
                        // so participating engines re-crawl it immediately
                        // rather than waiting out the passive-crawl window. A
                        // no-op when no IndexNow is composed (or its
                        // `PingOnPublish` toggle is off — the service itself
                        // gates that). Best-effort: PingSlug swallows its own
                        // transport failures, so a push outage never fails a
                        // publish.
                        match indexNowService with
                        | Some svc -> do! svc.PingSlug canonicalSlug
                        | None -> ()

                        return PublishSucceeded canonicalSlug
                    | Error err -> return PublishFailed(sprintf "entity store rejected the save: %A" err)
        }

module PublicRenderingNarrativePagePublisher =
    /// Factory invoked from `PublicRenderingCompose.run` once the
    /// registered layout map is known. Returns the interface-typed
    /// value so the DI registration carries the abstraction.
    /// `renderCacheInvalidator` (Phase 84) purges a slug's cached render
    /// on a successful publish; pass `None` when no render cache is
    /// composed. `guardrails` (Phase 91) constrain the AI authoring path
    /// (forced-draft landing, layout allow-list, forced audience, section
    /// cap); pass `NarrativePublishGuardrails.defaults` to preserve the
    /// pre-91 immediate-publish behaviour. `indexNowService` (Phase 109)
    /// pings the just-published slug to IndexNow on success; pass `None`
    /// when no IndexNow is composed.
    let create
        (entityStore: IEntityStore)
        (layoutNames: LayoutName list)
        (renderCacheInvalidator: IRenderCacheInvalidation option)
        (guardrails: NarrativePublishGuardrails)
        (indexNowService: IIndexNowService option)
        : INarrativePagePublisher =
        PublicRenderingNarrativePagePublisher(
            entityStore,
            layoutNames,
            renderCacheInvalidator,
            guardrails,
            indexNowService
        )
        :> INarrativePagePublisher

// ─── Phase 80b — Layout catalog ────────────────────────────────

/// `ILayoutCatalog` impl backed by the layouts registered on
/// `PublicRenderingServerApp` at compose time. Read-only; the
/// catalog snapshot is captured at construction and doesn't track
/// later additions (compose-time registration is final after
/// `PublicRenderingCompose.run` runs).
type PublicRenderingLayoutCatalog(layoutNames: string list) =
    interface ILayoutCatalog with
        member _.ListLayoutNames() = layoutNames

module PublicRenderingLayoutCatalog =
    let create (layoutNames: LayoutName list) : ILayoutCatalog =
        let strings = layoutNames |> List.map LayoutName.value
        PublicRenderingLayoutCatalog(strings) :> ILayoutCatalog