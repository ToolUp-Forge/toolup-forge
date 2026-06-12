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
type IContentAdminApi = {
    // `ContentAdminApiImpl.create` applies no role/claim gate of its
    // own — `withContentAdmin` mounts the handler bare and relies on
    // the deployment's auth middleware to fence `/api/content-admin/*`.
    // `AllowAnonymous` documents that existing behaviour exactly
    // (honest classification, not a policy choice). NOTE: this
    // surface writes the shared `_public` page overlay — a follow-up
    // tightening to `RequiresRole` is a candidate once the handler
    // grows an explicit admin gate.
    [<AllowAnonymous>]
    ListPages: unit -> Async<ContentPageSummary list>
    [<AllowAnonymous>]
    GetPage: string -> Async<PublicPage option>
    [<AllowAnonymous>]
    SavePage: PublicPage -> Async<Result<unit, ContentAdminError>>
    /// Publish / unpublish / schedule lever — the policy-changing
    /// method on this surface, hence the dispatcher audit opt-in.
    [<AllowAnonymous>]
    [<Audit "PolicyChanged">]
    SetStatus: string * PublishStatus -> Async<Result<unit, ContentAdminError>>
    [<AllowAnonymous>]
    ListRevisions: string -> Async<PublicPageRevisions.PageRevision list>
    [<AllowAnonymous>]
    RestoreRevision: string * int -> Async<Result<unit, ContentAdminError>>
}

module ContentAdminApi =
    [<Literal>]
    let routeBuilderPrefix = "/api/content-admin"

    let routeBuilder (_typeName: string) (methodName: string) =
        sprintf "%s/%s" routeBuilderPrefix methodName

/// Default `IContentAdminApi` over the `IEntityStore` page overlay
/// (scope `_public`, type `PublicPage`).
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
                    |> List.sortBy (fun s -> s.Slug)
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