// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ContentAuthoring.ContentAdminCompose

open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe
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

/// Append the content-authoring admin API to a `ServerApp`. The admin
/// surface drives content list / edit / status-transition / revision
/// operations against the page overlay; pair it with a "Content" client
/// module that binds the `IContentAdminApi` contract.
let withContentAdmin (app: ServerApp) : ServerApp =
    let adminApi (ctx: HttpContext) : IContentAdminApi =
        let store = ctx.RequestServices.GetService(typeof<IEntityStore>) :?> IEntityStore
        ContentAdminApiImpl.create store

    let handler =
        Remoting.createApi ()
        |> Remoting.withRouteBuilder ContentAdminApi.routeBuilder
        |> Remoting.fromContext adminApi
        |> Remoting.buildHttpHandler

    let baseExt = app.Extensions

    let mergedExt: ComposeExtensions = {
        baseExt with
            Handlers = baseExt.Handlers @ [ handler ]
    }

    { app with Extensions = mergedExt }