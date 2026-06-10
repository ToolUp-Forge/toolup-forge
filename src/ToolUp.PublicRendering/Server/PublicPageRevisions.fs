// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.PublicRendering.PublicPageRevisions

open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore

// ─── Phase 89 — page version history ──────────────────────────────────
//
// The `IEntityStore` overlay already keeps an append-only version per
// `Save` (every edit to a `PublicPageEntity` writes a new version and
// preserves the prior ones). This module is the thin convenience layer
// the CMS authoring surface uses: list a page's revisions, read a prior
// revision, and restore one. `restore` is itself append-only — it writes
// the old content as a *new* current version, so the restore is a normal
// edit in the history rather than a destructive rewind.

/// A revision summary for a page.
type PageRevision = { Slug: string; Version: int }

/// List every stored revision of a page (newest version first). Empty
/// when the slug has never been saved to the overlay.
let list (store: IEntityStore) (slug: string) : Async<PageRevision list> = async {
    let! refs =
        store.ListVersions<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)

    return
        refs
        |> List.map (fun r -> { Slug = slug; Version = r.Version })
        |> List.sortByDescending (fun r -> r.Version)
}

/// Read a specific revision's `PublicPage`. `NotFound` when the slug or
/// version is absent.
let get (store: IEntityStore) (slug: string) (version: int) : Async<Result<PublicPage, EntityError>> = async {
    let! result =
        store.GetVersion<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug, version)

    return result |> Result.map (fun e -> e.Page)
}

/// Read the current (latest) revision's `PublicPage`.
let current (store: IEntityStore) (slug: string) : Async<Result<PublicPage, EntityError>> = async {
    let! result = store.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug)
    return result |> Result.map (fun e -> e.Page)
}

/// Restore a prior revision: write its content back as a new current
/// version (append-only — history is preserved, including the restore
/// itself). Returns the restored page on success.
let restore (store: IEntityStore) (slug: string) (version: int) : Async<Result<PublicPage, EntityError>> = async {
    match!
        store.GetVersion<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, slug, version)
    with
    | Error e -> return Error e
    | Ok old ->
        // A fresh envelope (Version = 0) — the store assigns the next
        // version on Save, so the restore appends rather than rewinds.
        let restored = PublicPageEntity.fromPage old.Page

        match! store.Save(PublicPageEntity.PublicScope, restored) with
        | Ok _ -> return Ok old.Page
        | Error e -> return Error e
}