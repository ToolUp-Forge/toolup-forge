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

/// Name of the role gate a caller must hold to MINT a preview link
/// (Phase 198). Wire-stable, and deliberately the same string the
/// authoring lifecycle registers its approval predicate under — minting
/// a link that bypasses the publish-visibility filter is the same
/// authority as approving a publish, so it is the same gate rather than
/// a second one an operator could forget to configure.
///
/// The name is spelled here rather than referenced because the authoring
/// companion depends on this package, not the other way round; a test
/// pins the two spellings together so they cannot drift. A deployment
/// using a different guard name calls `mintPreviewLinkWithGuard`.
[<Literal>]
let mintGuard = "content:can-approve"

let private resolveLayout (layouts: Map<LayoutName, PublicPage -> XmlNode>) (page: PublicPage) =
    match Map.tryFind page.Layout layouts with
    | Some f -> Some f
    | None -> layouts |> Map.toSeq |> Seq.tryHead |> Option.map snd

/// The site-relative `/preview` path carrying `token`. The single place
/// the preview URL shape is spelled — every mint path and every doc
/// example goes through it, so there is one preview-link format to
/// match the one validation path below.
let previewPath (token: string) : string =
    sprintf "/preview?token=%s" (Uri.EscapeDataString token)

/// The one `IShareTokenStore.Issue` call behind every preview link.
/// Both `issuePreviewToken` (the Phase 89 shape) and the Phase 198 mint
/// surface route through it, so a minted token is — by construction, not
/// by convention — the same token the `/preview` route already
/// validates. There is no second preview-token format.
let private issuePreviewClaim
    (store: IShareTokenStore)
    (scopeId: string)
    (slug: string)
    (issuedBy: string)
    (attributedHandle: string option)
    (ttl: TimeSpan)
    : Async<Result<ShareToken, ShareTokenError>> =
    let request: ShareTokenIssueRequest = {
        ScopeId = scopeId
        ResourceKind = resourceKind
        ResourceId = slug
        AttributedHandle = attributedHandle
        IssuedBy = issuedBy
        ExpiresAt = Some(DateTimeOffset.UtcNow + ttl)
        // `Some None` = explicitly unlimited uses within the TTL
        // (a shared preview link is reloaded many times).
        UseLimit = Some None
        RateLimit = None
    }

    store.Issue request

/// Mint a preview URL for a page slug, valid for `ttl`. The returned
/// `/preview?token=...` path serves the page — including a Draft /
/// unpublished one — gated by the signed token. The token is multi-use
/// within its TTL (a preview link is shared and reloaded).
///
/// This is the unguarded primitive: the caller has already decided the
/// mint is authorised. An admin surface should prefer `mintPreviewLink`,
/// which applies the role gate and returns a typed decline.
let issuePreviewToken
    (store: IShareTokenStore)
    (scopeId: string)
    (slug: string)
    (issuedBy: string)
    (ttl: TimeSpan)
    : Async<Result<string, ShareTokenError>> =
    async {
        match! issuePreviewClaim store scopeId slug issuedBy None ttl with
        | Ok token -> return Ok(previewPath token.Token)
        | Error e -> return Error e
    }

// ─── Phase 198 — role-gated minting ───────────────────────────────────
//
// The admin half of the preview surface. Phase 89 shipped validation and
// the unguarded `issuePreviewToken` primitive; what a consumer admin UI
// needs is an authorised "Copy preview link" affordance that answers in
// typed values rather than exceptions.

/// Whether `access` may mint a preview link, gated on `guardName`.
///
/// Default-deny in the two directions that matter (GP 2):
///
/// - an **anonymous** caller never mints — a preview link bypasses the
///   publish-visibility filter, so minting is an authoring authority;
/// - a **share-token bearer** never mints, even though it is a resolved,
///   non-anonymous subject. A preview link that could mint further
///   preview links would let a leaked link extend its own reach
///   indefinitely and re-attribute itself; the claim's authority is to
///   READ the one resource it names (GP 4).
///
/// Beyond that the check is the SDK's ordinary module-RBAC axis, so it
/// behaves like every other gated surface: a platform admin passes, and
/// a deployment that has configured no permissions at all is
/// unrestricted for authenticated users exactly as it is everywhere else
/// (GP 11 — adopting this surface does not silently lock out an existing
/// deployment's editors).
let canMintPreviewLinkWithGuard (guardName: string) (access: AccessContext) : bool =
    if not (AccessContext.isAuthenticated access) then false
    elif AccessContext.isClaimBearer access then false
    elif AccessContext.canModifyPlatformConfig access then true
    else AccessContext.canAccessModule guardName access

/// `canMintPreviewLinkWithGuard` at the standard `mintGuard`.
let canMintPreviewLink (access: AccessContext) : bool =
    canMintPreviewLinkWithGuard mintGuard access

let private validateMintRequest (access: AccessContext) (request: MintPreviewLinkRequest) =
    if String.IsNullOrWhiteSpace request.Slug then
        Error(PreviewLinkDecline.InvalidRequest "slug is required")
    elif request.Ttl <= TimeSpan.Zero then
        Error(PreviewLinkDecline.InvalidRequest "ttl must be positive")
    elif request.Ttl > MintPreviewLinkRequest.MaxTtl then
        Error(
            PreviewLinkDecline.InvalidRequest(
                sprintf "ttl exceeds the %g-day maximum for a preview link" MintPreviewLinkRequest.MaxTtl.TotalDays
            )
        )
    else
        match AccessContext.configScope access with
        | Some scope -> Ok scope.ScopeId
        | None -> Error(PreviewLinkDecline.InvalidRequest "the caller has no resolvable storage scope")

/// Mint a scope-bound preview link for an unpublished page, gated on
/// `guardName`.
///
/// The scope, the issuer and the base URL are all server-derived — the
/// request names only the slug, the lifetime, and an optional attribution
/// handle — so a caller cannot widen scope or retarget the link (GP 4).
///
/// `store = None` (no `IShareTokenStore` registered) declines with
/// `PreviewsNotEnabled` and touches nothing: the same posture the
/// `/preview` route already takes, so a deployment that never enables
/// previews is unchanged by this surface existing (GP 11 / GP 13).
let mintPreviewLinkWithGuard
    (guardName: string)
    (store: IShareTokenStore option)
    (baseUrl: string)
    (access: AccessContext)
    (request: MintPreviewLinkRequest)
    : Async<Result<PreviewLink, PreviewLinkDecline>> =
    async {
        // Authorisation first, so an unauthorised caller cannot use the
        // decline to probe whether this deployment has previews enabled.
        if not (canMintPreviewLinkWithGuard guardName access) then
            return Error PreviewLinkDecline.Unauthorised
        else
            match store with
            | None -> return Error PreviewLinkDecline.PreviewsNotEnabled
            | Some store ->
                match validateMintRequest access request with
                | Error decline -> return Error decline
                | Ok scopeId ->
                    let slug = request.Slug.Trim()

                    match! issuePreviewClaim store scopeId slug access.UserId request.AttributedHandle request.Ttl with
                    | Error e -> return Error(PreviewLinkDecline.MintFailed e)
                    | Ok token ->
                        let path = previewPath token.Token

                        return
                            Ok {
                                Url = (baseUrl |> Option.ofObj |> Option.defaultValue "").TrimEnd '/' + path
                                Path = path
                                Token = token.Token
                                TokenId = token.Claim.TokenId
                                Slug = slug
                                IssuedBy = token.Claim.IssuedBy
                                ExpiresAt = token.Claim.ExpiresAt
                            }
    }

/// `mintPreviewLinkWithGuard` at the standard `mintGuard` — the entry
/// point a consumer admin surface calls.
let mintPreviewLink
    (store: IShareTokenStore option)
    (baseUrl: string)
    (access: AccessContext)
    (request: MintPreviewLinkRequest)
    : Async<Result<PreviewLink, PreviewLinkDecline>> =
    mintPreviewLinkWithGuard mintGuard store baseUrl access request

/// `mintPreviewLink` resolving both deployment-side inputs from the live
/// request: the `IShareTokenStore` from request-scoped DI (absent → a
/// `PreviewsNotEnabled` decline, never a null-reference) and the base URL
/// from the request's own scheme / host / path base, so a minted link is
/// absolute against the origin the editor is actually browsing.
let mintPreviewLinkForRequest
    (ctx: HttpContext)
    (access: AccessContext)
    (request: MintPreviewLinkRequest)
    : Async<Result<PreviewLink, PreviewLinkDecline>> =
    let store =
        match ctx.RequestServices.GetService(typeof<IShareTokenStore>) with
        | :? IShareTokenStore as s -> Some s
        | _ -> None

    let baseUrl =
        sprintf "%s://%s%s" ctx.Request.Scheme (ctx.Request.Host.ToString()) (ctx.Request.PathBase.ToString())

    mintPreviewLink store baseUrl access request

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

        // Resolve by pattern-match, not by `:?>`. F# interface types are
        // non-nullable, so casting the ABSENT service raised a
        // `NullReferenceException` before the `isNull` guard below could
        // ever see it — the documented "no store registered → decline"
        // path 500'd instead of 404ing, and did so after the handler had
        // been entered. Surfaced by the Phase 198 test pack; this is the
        // idiom the Forms compose root already uses for the same lookup.
        let store =
            match ctx.RequestServices.GetService(typeof<IShareTokenStore>) with
            | :? IShareTokenStore as s -> Some s
            | _ -> None

        match store with
        | None -> return None
        | Some _ when String.IsNullOrEmpty token -> return None
        | Some store ->
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