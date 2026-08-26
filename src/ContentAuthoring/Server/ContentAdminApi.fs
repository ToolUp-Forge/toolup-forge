// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ContentAuthoring

open ToolUp.Platform // 0.5.0 — forge-native auth + audit attributes
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.PublicRendering

// ─── Phase 89 — content authoring admin API ───────────────────────────
//
// The server surface the "Content" / "Pages" admin module drives: list /
// read / save content entries on the `IEntityStore` overlay, drive
// status transitions, and browse / restore revisions. Reuses the
// lifecycle (`ContentLifecycle`) + versioning (`PublicPageRevisions`)
// substrate; the existing overlay render path serves whatever this
// writes. A Fable.Remoting contract so a client module binds to it
// type-safely.

/// A page row for the admin list view.
type ContentPageSummary = {
    Slug: string
    Title: string
    /// Publish-status token (`"draft"` / `"scheduled"` / `"published"` /
    /// `"archived"`).
    Status: string
    Collection: string option
}

type ContentAdminError =
    | NotFound
    | StorageError of message: string

/// Fable.Remoting contract for the authoring admin surface. `SetStatus`
/// takes a full `PublishStatus` so a `Scheduled at` carries its publish
/// time; `RestoreRevision` appends the chosen revision as the new
/// current version (history preserved).
///
/// **Phase 627 — secure by default. No method is anonymous-reachable.**
/// Every method carries `[<RequiresRole "PlatformAdmin">]`. Before 627
/// all six were `[<AllowAnonymous>]`, and — the compounding fact that
/// made it materially worse than the [Phase 619] instance —
/// `withContentAdmin` mounted the handler through raw
/// `Remoting.buildHttpHandler`, so no auth-context resolver was ever
/// composed and the Phase 69d classifier never ran. The attributes were
/// **inert metadata**: tightening them alone would have changed nothing,
/// which is why 627.A (arming the classifier via `Api.make`) is the
/// load-bearing half of this pair and this file is the other.
///
/// **Why `"PlatformAdmin"` here, when [Phase 619] rejected every role
/// gate for `IReportApi`.** 619's rejection was about *scope ownership*:
/// a report template is scope-owned, so gating it on the platform role
/// would break per-team management. This surface is the opposite. It
/// writes the deployment-wide `_public` page overlay
/// (`PublicPageEntity.PublicScope`, a fixed literal — see the
/// `ContentAdminApiImpl` note below), which is platform-owned by
/// construction: there is exactly one public site per deployment, and
/// every method here mutates or reads it. Platform-owned surface,
/// platform-level gate.
///
/// **And it is a live gate, not a Phase 132 dead one.** `"PlatformAdmin"`
/// is the *only* role string the default `ForgeAuthContext` resolver can
/// emit — it bridges to the server-resolved `ToolUp.PlatformRole` from
/// `IPlatformAdminStore`, rather than to the `AuthenticatedUser.Roles`
/// list the first-party providers leave empty. `[<RequiresRole "Owner">]`
/// / `"Admin"` — the strings the pre-627 comment gestured at — would have
/// denied every caller including real admins. That trap is exactly what
/// 619 warned this phase about; the discriminator is that the role we
/// need happens to be the one that is emittable.
///
/// **Why not `[<RequiresClaim "scope">]`** (619's answer). Against the
/// default resolver that gate resolves to exactly `not isAnonymous` — any
/// authenticated subject, *including a share-token `ClaimBearer`*. For a
/// scope-owned surface that is enough, because the `StorageScope`
/// resolver then isolates the caller to their own scope (GP 4). Here
/// there is no per-caller scope to isolate to: the binding is fixed. The
/// structural defence 619 could lean on does not exist on this surface,
/// so the attribute gate has to carry the whole weight — and "any
/// authenticated caller may rewrite the public site" is not the policy.
///
/// **`SetStatus` is why this was urgent.** It carries
/// `[<Audit "PolicyChanged">]`, and the bare mount meant the dispatcher's
/// audit emitter was never composed either — so an unauthenticated
/// publish/unpublish of any public page emitted no audit row at all. An
/// audited policy change anyone can invoke, whose audit does not fire, is
/// not a classification gap; it is an open door that also keeps no record
/// of who walked through it. `Api.make` composes both the classifier and
/// the audit emitter, so 627.A closes both halves in one move.
///
/// Migration: `docs/migrations/627-content-admin-api-authorization.md`.
type IContentAdminApi = {
    /// List every page in the public overlay.
    /// **Gate: `PlatformAdmin`.** A read, but a read of the whole
    /// authoring surface — including `draft` and `scheduled` pages the
    /// public renderer deliberately does not serve. Leaving it open
    /// would publish unpublished content to anyone who asked for the
    /// list, which is the thing the lifecycle exists to prevent.
    [<RequiresRole "PlatformAdmin">]
    ListPages: unit -> Async<ContentPageSummary list>
    /// Read one page's full authoring record.
    /// **Gate: `PlatformAdmin`** — same reasoning as `ListPages`, and
    /// this one returns the body, not just the summary row. The
    /// *published* read path for anonymous visitors is the
    /// `ToolUp.PublicRendering` overlay renderer, which serves only
    /// `published` pages; this is the authoring door, not that one.
    [<RequiresRole "PlatformAdmin">]
    GetPage: string -> Async<PublicPage option>
    /// Create or overwrite a page in the public overlay.
    /// **Gate: `PlatformAdmin`.** An unauthenticated write to the
    /// publicly-served page overlay is the headline defect this phase
    /// closes — it is content injection into the deployment's own
    /// public site.
    [<RequiresRole "PlatformAdmin">]
    SavePage: PublicPage -> Async<Result<unit, ContentAdminError>>
    /// Publish / unpublish / schedule lever — the policy-changing
    /// method on this surface, hence the dispatcher audit opt-in.
    /// **Gate: `PlatformAdmin`**, and the audit annotation now actually
    /// emits (see the record note above).
    [<RequiresRole "PlatformAdmin">]
    [<Audit "PolicyChanged">]
    SetStatus: string * PublishStatus -> Async<Result<unit, ContentAdminError>>
    /// Browse a page's revision history.
    /// **Gate: `PlatformAdmin`** — the history carries every prior body,
    /// including drafts that were never published.
    [<RequiresRole "PlatformAdmin">]
    ListRevisions: string -> Async<PublicPageRevisions.PageRevision list>
    /// Restore a revision as the new current version.
    /// **Gate: `PlatformAdmin`** — a write, and one that can silently
    /// republish previously-withdrawn content.
    [<RequiresRole "PlatformAdmin">]
    RestoreRevision: string * int -> Async<Result<unit, ContentAdminError>>
}

module ContentAdminApi =
    [<Literal>]
    let routeBuilderPrefix = "/api/content-admin"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName

/// Default `IContentAdminApi` over the `IEntityStore` page overlay
/// (scope `_public`, type `PublicPage`).
///
/// ─── Phase 627.C — the fixed-`PublicScope` binding, decided ───────────
///
/// `create` binds `PublicPageEntity.PublicScope` — the literal `"_public"`
/// — rather than resolving a scope per caller, and **that is deliberate
/// and stays**. Phase 627 filed it as an open question because it is what
/// removes the scope-isolation defence every other blanket-anonymous
/// record in the tree leans on. The answer is that the defence was never
/// applicable here, not that it went missing:
///
/// * **A per-caller scope would be incorrect, not merely stricter.** The
///   public renderer, the sitemap, the narrative feed and the scheduled-
///   publish sweep all read `PublicPageEntity.PublicScope` unconditionally
///   (`NarrativePagePublisher`, `NarrativeFeedHandler`,
///   `ContentLifecycle.runScheduledPublishSweep`). An admin whose writes
///   landed in `team-a` would be editing an overlay nothing serves — the
///   page would simply never appear, with no error to explain it. There
///   is one public site per deployment; its overlay is a single
///   platform-owned store, and a per-caller binding would fragment it.
/// * **So the isolation GP 4 provides elsewhere has to be replaced, not
///   restored.** With no per-caller scope there is nothing for
///   `StorageScope` to isolate, which is precisely why the contract's
///   attribute gate is `[<RequiresRole "PlatformAdmin">]` and not the
///   `[<RequiresClaim "scope">]` that sufficed for [Phase 619]'s
///   scope-owned `IReportApi`. The gate carries the whole weight here
///   because it is the only thing that can.
///
/// A deployment wanting per-team content authoring wants a *different*
/// surface — team-scoped entities projected into the public overlay at
/// publish time — not a rescoped `IContentAdminApi`. That is a feature,
/// not a tightening of this one.
module ContentAdminApiImpl =

    let private scope = PublicPageEntity.PublicScope
    let private etype = PublicPageEntity.EntityTypeName

    let create (store: IEntityStore) : IContentAdminApi = {
        ListPages =
            fun () -> async {
                let! refs = store.ListAll<PublicPageEntity>(scope, etype, 0, 5000)

                let! pages =
                    refs
                    |> List.map (fun r -> async {
                        match! store.Get<PublicPageEntity>(scope, etype, r.Id) with
                        | Ok e -> return Some e.Page
                        | Error _ -> return None
                    })
                    |> Async.Sequential

                return
                    pages
                    |> Array.toList
                    |> List.choose id
                    |> List.map (fun p -> {
                        Slug = Slug.value p.Slug
                        Title = p.Title
                        Status = PublishStatus.token p.Status
                        Collection = p.Collection
                    })
                    |> List.sortBy _.Slug
            }
        GetPage =
            fun slug -> async {
                match! store.Get<PublicPageEntity>(scope, etype, slug) with
                | Ok e -> return Some e.Page
                | Error _ -> return None
            }
        SavePage =
            fun page -> async {
                match! store.Save(scope, PublicPageEntity.fromPage page) with
                | Ok _ -> return Ok()
                | Error e -> return Error(StorageError(sprintf "%A" e))
            }
        SetStatus =
            fun (slug, status) -> async {
                match! store.Get<PublicPageEntity>(scope, etype, slug) with
                | Error _ -> return Error NotFound
                | Ok e ->
                    let updated = {
                        e with
                            Page = { e.Page with Status = status }
                    }

                    match! store.Save(scope, updated) with
                    | Ok _ -> return Ok()
                    | Error err -> return Error(StorageError(sprintf "%A" err))
            }
        ListRevisions = fun slug -> PublicPageRevisions.list store slug
        RestoreRevision =
            fun (slug, version) -> async {
                match! PublicPageRevisions.restore store slug version with
                | Ok _ -> return Ok()
                | Error(EntityError.NotFound _) -> return Error NotFound
                | Error other -> return Error(StorageError(sprintf "%A" other))
            }
    }