// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 276 — neutral hosted-route declaration (shared floor) ───────
//
// A hosted multi-page module (Phase 267 `withElementPages`) is deep-linkable
// and crawlable only when its pages have stable URLs registered into BOTH
// the client shell router (Phase 110 `Navigate`) and the SSR route table
// (Phase 111). `HostRoute` is the neutral route DECLARATION both sides read.
//
// It lives in `ToolUp.Platform.Core` (the shared floor) — not the Fable
// client tier — because BOTH the client deep-link mapping
// (`Platform.Client.HostRouteContract` → `NavigationRequest`) and the SSR
// registration (`ToolUp.PublicRendering.HostRouteRegistration` →
// `IContentSource`) consume the same type, and `ToolUp.PublicRendering`
// must not reference the client tier (GP 10). No tree-language type appears
// (GP 1) — a route is a path pattern + a sidebar id + a title.

/// Captured path-segment values from a matched route: matching the pattern
/// `"reports/{quarter}"` against `"reports/q3"` captures
/// `{ "quarter" = "q3" }`. Neutral `string → string`, the same shape the
/// Phase 111 `RouteShape` matcher produces, so a route round-trips through
/// either tier identically.
type HostRouteParams = Map<string, string>

/// A hosted module's declaration of one deep-linkable / crawlable route.
type HostRoute = {
    /// Path pattern, `/`-delimited, a `{name}` segment capturing exactly one
    /// path segment (e.g. `"reports/{quarter}"`). Literal segments match
    /// case-sensitively; `{rest}` does not greedily span segments. Matched
    /// by `HostRoute.tryMatch`.
    PathPattern: string
    /// The shell sidebar id this route deep-links to: a bare module id for a
    /// single-page module, or `"{moduleId}{/pageRoute}"` for a multi-page
    /// module — the `NavigationRequest.SidebarId` contract Phase 110's
    /// `Navigate` capability drives.
    SidebarId: string
    /// Human page title — the SSR `<title>` / crawl label for the route.
    Title: string
}

[<RequireQualifiedAccess>]
module HostRoute =

    let private isCapture (seg: string) =
        seg.Length >= 2 && seg.StartsWith "{" && seg.EndsWith "}"

    /// Try to match a concrete `path` against the route's `PathPattern`,
    /// returning the captured segment map on a full match (every segment
    /// accounted for), or `None` when the segment counts differ or a literal
    /// segment mismatches. An empty capture map is a valid `Some` for a
    /// fully-literal pattern that matches.
    ///
    /// The single-segment `{name}` capture rule is deliberately identical to
    /// the Phase 111 `RouteShape.tryMatch` the SSR tier uses; Core cannot
    /// reference `ToolUp.PublicRendering`, so the tiny matcher is duplicated
    /// here rather than shared — both tiers agree on the same rule so a
    /// route resolves the same client-side and server-side.
    let tryMatch (route: HostRoute) (path: string) : HostRouteParams option =
        let pSegs = route.PathPattern.Split('/')
        let sSegs = path.Split('/')

        if pSegs.Length <> sSegs.Length then
            None
        else
            let rec go i acc =
                if i >= pSegs.Length then
                    Some acc
                else
                    let p = pSegs[i]
                    let s = sSegs[i]

                    if isCapture p then
                        go (i + 1) (Map.add (p.Substring(1, p.Length - 2)) s acc)
                    elif p = s then
                        go (i + 1) acc
                    else
                        None

            go 0 Map.empty

    /// Project captured route params into a `HostBindingSources.State`
    /// namespace, so a deep link's param state restores through the Phase
    /// 264 read-side: the hosted tree resolves `State["quarter"]` exactly as
    /// it resolves any other binding. This is the param round-trip — a deep
    /// link carries the params, the projection hands them to the tree.
    let paramsToBindingSources (ps: HostRouteParams) : HostBindingSources =
        HostBindingSources.ofState (ps |> Map.map (fun _ v -> box v))