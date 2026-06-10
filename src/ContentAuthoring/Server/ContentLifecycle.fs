// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContentAuthoring.ContentLifecycle

open System
open ToolUp.Platform
open ToolUp.Platform.EntityTypes
open ToolUp.Platform.IEntityStore
open ToolUp.Forms.Workflow
open ToolUp.PublicRendering

// ─── Phase 89 — editorial lifecycle = WorkflowDefinition ──────────────
//
// The draft → review → publish lifecycle is a Forms `WorkflowDefinition`
// (consumed unforked). `PublicPage.Status` is the rendered projection of
// the editorial state; the transitions below move an entry between
// states with role-gated approval, and the scheduled-publish sweep
// promotes a `Scheduled` page to `Published` once its time arrives
// (wired by a deployment as a recurring `IJobScheduler` job).

/// Name of the registered approval guard. A deployment registers the
/// predicate via `FormsServerApp.withGuard ContentLifecycle.approveGuard
/// (fun ctx -> ...)`, gating who may approve / unpublish / archive
/// (GP 4 — role-gated transitions).
[<Literal>]
let approveGuard = "content:can-approve"

/// The canonical editorial workflow: `draft → in-review → published`,
/// with unpublish / archive / restore. Register with
/// `FormsServerApp.withWorkflow ContentLifecycle.editorialWorkflow`.
let editorialWorkflow: WorkflowDefinition = {
    Id = "content-editorial"
    InitialState = "draft"
    Transitions = [
        {
            From = "draft"
            Event = "submit-for-review"
            To = "in-review"
            Guard = None
            Action = None
        }
        {
            From = "in-review"
            Event = "approve"
            To = "published"
            Guard = Some approveGuard
            Action = None
        }
        {
            From = "in-review"
            Event = "reject"
            To = "draft"
            Guard = Some approveGuard
            Action = None
        }
        {
            From = "published"
            Event = "unpublish"
            To = "draft"
            Guard = Some approveGuard
            Action = None
        }
        {
            From = "published"
            Event = "archive"
            To = "archived"
            Guard = Some approveGuard
            Action = None
        }
        {
            From = "archived"
            Event = "restore"
            To = "draft"
            Guard = Some approveGuard
            Action = None
        }
    ]
}

/// Map a workflow state name to the page `PublishStatus`. `"published"`
/// → `Published`; `"archived"` → `Archived`; everything else
/// (`"draft"` / `"in-review"`) → `Draft`. (A `Scheduled` page carries
/// its publish time on the page, not the workflow state, so it is set
/// via `schedule` rather than a workflow state name.)
let statusForState (state: string) : PublishStatus =
    match state.Trim().ToLowerInvariant() with
    | "published" -> Published
    | "archived" -> Archived
    | _ -> Draft

// ─── Pure status transitions on a page ────────────────────────────────

/// Mark a page published as of `now` (stamps `PublishedAt`).
let publishAt (now: DateTimeOffset) (page: PublicPage) : PublicPage = {
    page with
        Status = Published
        PublishedAt = Some now
}

/// Schedule a page to go live at `at`.
let schedule (at: DateTimeOffset) (page: PublicPage) : PublicPage = { page with Status = Scheduled at }

/// Archive a page (removed from public serving, retained for history).
let archive (page: PublicPage) : PublicPage = { page with Status = Archived }

/// Return a page to draft (unpublish).
let toDraft (page: PublicPage) : PublicPage = { page with Status = Draft }

/// Whether a `Scheduled` page is now due to go live.
let isDueForPublish (now: DateTimeOffset) (page: PublicPage) : bool =
    match page.Status with
    | Scheduled at -> now >= at
    | _ -> false

/// Promote a `Scheduled`-and-due page to `Published` (stamping
/// `PublishedAt`); pass any other page through unchanged.
let promoteIfDue (now: DateTimeOffset) (page: PublicPage) : PublicPage =
    if isDueForPublish now page then
        publishAt now page
    else
        page

// ─── Scheduled-publish sweep (wired via IJobScheduler) ────────────────

/// Scan the page overlay and promote every `Scheduled`-and-due page to
/// `Published`. Returns the slugs promoted. A deployment registers this
/// as a recurring `IJobScheduler` job (e.g. once a minute) so scheduled
/// content goes live without a redeploy — "scheduled publish fires via
/// the job scheduler" with no per-page job bookkeeping.
let runScheduledPublishSweep (store: IEntityStore) (now: DateTimeOffset) : Async<string list> = async {
    let! refs = store.ListAll<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, 0, 5000)

    let mutable promoted = []

    for ref in refs do
        match! store.Get<PublicPageEntity>(PublicPageEntity.PublicScope, PublicPageEntity.EntityTypeName, ref.Id) with
        | Ok entity when isDueForPublish now entity.Page ->
            let updated = {
                entity with
                    Page = promoteIfDue now entity.Page
            }

            match! store.Save(PublicPageEntity.PublicScope, updated) with
            | Ok _ -> promoted <- Slug.value entity.Page.Slug :: promoted
            | Error _ -> ()
        | _ -> ()

    return List.rev promoted
}