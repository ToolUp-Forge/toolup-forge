// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContentAuthoring.ContentAdminCompose

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.Server

// ─── Phase 89 — content admin compose seam ────────────────────────────
//
// Mounts the `IContentAdminApi` Fable.Remoting handler
// (`/api/content-admin/*`) onto a `ServerApp`. Composes additively after
// the PublicRendering companion (which registers the `IEntityStore`
// overlay the admin API writes to). The scheduled-publish sweep is wired
// separately by the deployment as a recurring `IJobScheduler` job calling
// `ContentLifecycle.runScheduledPublishSweep`.
//
// ─── Phase 627.A — the classifier is armed ────────────────────────────
//
// This mount used to build its handler from raw `Remoting.createApi ()`
// … `Remoting.buildHttpHandler`, which composes **no auth-context
// resolver**. The Phase 69d startup classifier — the thing that reads the
// `[<RequiresRole>]` / `[<AllowAnonymous>]` family and refuses to start on
// an unclassified method — is armed by `Remoting.withAuthContext`, and
// nothing here called it. The consequence was not that the record's
// attributes were wrong; it is that they were **not read at all**. Every
// method dispatched regardless of what it declared, so the 627.B
// classification on the contract would have been decoration without this
// change. This is the load-bearing half of the pair.
//
// `Api.make` is the seam that arms it, and it is the same call every
// other forge remoting mount already makes. For an F# record API type it
// composes, default-on:
//
//   * the default `ForgeAuthContext` resolver over Phase 66's `Subject` /
//     `AuthenticatedUser` — which arms the classifier, so an unclassified
//     method now REFUSES STARTUP rather than dispatching silently;
//   * the `IAuditEmitter` bridge, because the record carries
//     `[<Audit "PolicyChanged">]` on `SetStatus`. Pre-627 that annotation
//     emitted nothing, for the same reason — the bare mount composed no
//     emitter. A publish/unpublish now lands an audit row against the
//     DI-registered `IAuditLog`;
//   * the Phase 132 dead-gate startup warning, which is what would shout
//     if someone later swapped `"PlatformAdmin"` for a role the default
//     resolver cannot emit.
//
// The route builder is passed through unchanged, so the mounted paths are
// byte-identical to the pre-627 ones (`/api/content-admin/<Method>`). The
// behaviour change is exactly the intended one and nothing else.

/// Append the content-authoring admin API to a `ServerApp`. The admin
/// surface drives content list / edit / status-transition / revision
/// operations against the page overlay; pair it with a "Content" client
/// module that binds the `IContentAdminApi` contract.
///
/// **Phase 627 — BREAKING, deliberately.** Every method now requires a
/// `PlatformAdmin` caller and the gate is actually enforced. A deployment
/// that reached `/api/content-admin/*` without one — which, before 627,
/// was every deployment, because nothing was enforcing anything — starts
/// receiving `ErrorCategory.Auth` denials. See
/// `docs/migrations/627-content-admin-api-authorization.md`.
let withContentAdmin (app: ServerApp) : ServerApp =
    let adminApi (ctx: HttpContext) : IContentAdminApi =
        let store = ctx.RequestServices.GetService(typeof<IEntityStore>) :?> IEntityStore
        ContentAdminApiImpl.create store

    let handler =
        Api.make<IContentAdminApi> (adminApi, routeBuilder = ContentAdminApi.routeBuilder)

    let baseExt = app.Extensions

    let mergedExt: ComposeExtensions = {
        baseExt with
            Handlers = baseExt.Handlers @ [ handler ]
    }

    { app with Extensions = mergedExt }