// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── Phase 276 — hosted-tree route registration (SSR) ──────────────────
//
// The SSR half of the Phase 276 route contract. A hosted multi-page
// module's neutral `HostRoute` declarations (Core) register into the Phase
// 111 PublicRendering route table as `IContentSource` entries, so each
// hosted page has a stable crawlable URL reachable by direct navigation —
// and, because each source is `IEnumerableContentSource`, its concrete
// slugs reach `sitemap.xml` / static export / prerender (the crawlability
// half of the acceptance).
//
// The client half (`Platform.Client.HostRouteContract`) maps the SAME
// `HostRoute` list to forge `NavigationRequest` deep-links + Phase 264
// param restore. Both halves read one neutral route type (Core `HostRoute`);
// no tree-language type appears (GP 1). Registration is append-only (Phase
// 83 `withContentSource`), so a pipeline that registers no host routes is
// byte-for-byte unchanged (GP 11) and pays nothing (GP 13).

/// SSR resolver for a hosted route: given the route's captured params and
/// the caller's `AccessContext`, produce the page's `ResolvedContent` (body
/// + per-request `<head>` metadata) or `None` to fall through. The consumer
/// supplies this — typically the SSR lowering of the hosted tree for the
/// page (e.g. the Phase 111 server-rendered fragment for the route). The
/// `AccessContext` lets the resolver scope its render to the caller (GP 4).
type HostRouteResolver = HostRouteParams -> AccessContext -> Async<ResolvedContent option>

/// One hosted route's full SSR registration: the neutral route, its SSR
/// resolver, and an enumerator of the concrete slugs it currently produces
/// (so sitemap / export / prerender discover the page). `enumerate` returns
/// `[]` for a purely dynamic route with no crawlable instances.
type HostRouteRegistration = {
    Route: HostRoute
    Resolve: HostRouteResolver
    Enumerate: unit -> Async<Slug list>
}

[<RequireQualifiedAccess>]
module HostRouteRegistration =

    /// Build a registration from its parts; `enumerate` defaults to the
    /// caller when omitted via `create`.
    let create
        (route: HostRoute)
        (resolve: HostRouteResolver)
        (enumerate: unit -> Async<Slug list>)
        : HostRouteRegistration =
        {
            Route = route
            Resolve = resolve
            Enumerate = enumerate
        }

    /// A registration whose route produces no crawlable slugs (request-only
    /// / purely dynamic): the page is reachable by direct navigation + deep
    /// link, but contributes nothing to the sitemap.
    let requestOnly (route: HostRoute) (resolve: HostRouteResolver) : HostRouteRegistration =
        create route resolve (fun () -> async { return [] })

    /// Turn one registration into an `IContentSource` that claims the route's
    /// `PathPattern` (matched by the Phase 111 `RouteShape` matcher — the
    /// SAME single-segment `{name}` rule Core's `HostRoute.tryMatch` uses on
    /// the client) AND enumerates its concrete slugs (`IEnumerableContentSource`).
    /// The captured segments the SSR matcher produces are the same
    /// `HostRouteParams` the client contract restores, so a route resolves
    /// identically on both tiers.
    let toContentSource (reg: HostRouteRegistration) : IContentSource =
        ContentSource.ofRouteResolvedEnumerable
            reg.Route.PathPattern
            (fun captures ctx -> reg.Resolve captures ctx)
            reg.Enumerate

    /// Register a hosted module's routes into a `PublicRenderingServerApp`:
    /// one `IContentSource` per registration, appended via the Phase 83
    /// `withContentSource` (append-only — GP 11). The order is preserved, so
    /// a more specific literal route registered first is consulted before a
    /// capturing one.
    let register
        (registrations: HostRouteRegistration list)
        (app: PublicRenderingServerApp)
        : PublicRenderingServerApp =
        registrations
        |> List.fold (fun acc reg -> PublicRenderingServerApp.withContentSource (toContentSource reg) acc) app