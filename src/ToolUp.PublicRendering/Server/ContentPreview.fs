// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.PublicRendering.ContentPreview

open System
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform

// ─── Phase 89 — shareable preview links ───────────────────────────────
//
// Reuses the `IShareTokenStore` substrate (the same HMAC-signed,
// scope-bound, TTL'd tokens that gate publishable Forms surveys, Phase
// 21b) to share an unpublished page — a Draft awaiting review, or a
// client-facing page kept off the public index — via a token-gated URL
// without granting full auth. The token's claim carries the page slug
// (`ResourceKind = "PublicPage"`, `ResourceId = slug`); the `/preview`
// route validates it and renders the referenced page bypassing the
// publish-visibility filter so an editor sees the draft.
//
// No token, an invalid / expired token, or no `IShareTokenStore`
// registered → the route declines and the request 404s like any unknown
// path. The page is never reachable without a valid signature (GP 4).

[<Literal>]
let resourceKind = "PublicPage"

let private resolveLayout (layouts: Map<LayoutName, PublicPage -> XmlNode>) (page: PublicPage) =
    match Map.tryFind page.Layout layouts with
    | Some f -> Some f
    | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

/// Mint a preview URL for a page slug, valid for `ttl`. The returned
/// `/preview?token=...` path serves the page — including a Draft /
/// unpublished one — gated by the signed token. The token is multi-use
/// within its TTL (a preview link is shared and reloaded).
let issuePreviewToken
    (store: IShareTokenStore)
    (scopeId: string)
    (slug: string)
    (issuedBy: string)
    (ttl: TimeSpan)
    : Async<Result<string, ShareTokenError>> =
    async {
        let request: ShareTokenIssueRequest = {
            ScopeId = scopeId
            ResourceKind = resourceKind
            ResourceId = slug
            AttributedHandle = None
            IssuedBy = issuedBy
            ExpiresAt = Some(DateTimeOffset.UtcNow + ttl)
            // `Some None` = explicitly unlimited uses within the TTL
            // (a shared preview link is reloaded many times).
            UseLimit = Some None
            RateLimit = None
        }

        match! store.Issue request with
        | Ok token -> return Ok(sprintf "/preview?token=%s" (Uri.EscapeDataString token.Token))
        | Error e -> return Error e
    }

/// The `/preview?token=...` route. Validates the share token and, when it
/// grants `PublicPage` access, renders the referenced page bypassing the
/// publish-visibility filter (so a Draft previews). Declines (→ 404) on a
/// missing token or absent store; `403` on a token that doesn't grant
/// page access.
let previewHandler (api: IPublicContentApi) (layouts: Map<LayoutName, PublicPage -> XmlNode>) : HttpHandler =
    route "/preview"
    >=> fun _next (ctx: HttpContext) -> task {
        let token =
            match ctx.Request.Query.TryGetValue "token" with
            | true, v -> v.ToString()
            | _ -> ""

        let store =
            ctx.RequestServices.GetService(typeof<IShareTokenStore>) :?> IShareTokenStore

        if isNull (box store) || String.IsNullOrEmpty token then
            return None
        else
            match! store.Validate token |> Async.StartAsTask with
            | Ok claim when claim.ResourceKind = resourceKind ->
                match! api.GetPage claim.ResourceId |> Async.StartAsTask with
                | Some page ->
                    match resolveLayout layouts page with
                    | Some layout ->
                        let html = layout page |> RenderView.AsString.htmlDocument
                        ctx.Response.ContentType <- "text/html; charset=utf-8"
                        // Preview pages must never be indexed or cached.
                        ctx.Response.Headers["X-Robots-Tag"] <- StringValues "noindex, nofollow"
                        ctx.Response.Headers["Cache-Control"] <- StringValues "no-store"
                        return! ctx.WriteStringAsync html
                    | None ->
                        ctx.SetStatusCode 500
                        return! ctx.WriteStringAsync "PublicRendering: no layout registered"
                | None -> return None
            | Ok _ ->
                ctx.SetStatusCode 403
                return Some ctx
            | Error _ ->
                ctx.SetStatusCode 403
                return Some ctx
    }