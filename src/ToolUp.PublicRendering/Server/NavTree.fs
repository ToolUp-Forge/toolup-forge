// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 90 — site-structure navigation model.
///
/// An ordered, nestable navigation tree that layouts render into menus
/// and breadcrumbs. Nodes are audience-aware: a node whose `Audience`
/// the requesting principal doesn't satisfy is filtered out (along with
/// its subtree) before render, so a gated item never leaks to an
/// unauthorised viewer (GP 4). The audience model is self-contained
/// (evaluated against the shipped `AccessContext`), so site structure
/// ships independently of the page-level audience-targeting work; a
/// deployment that composes page-level audiences can map them onto these
/// node rules.
///
/// The whole model is pure data (no live handles, no functions on the
/// node — `NavAudience` is a data DU), so a tree round-trips through
/// `nav.yaml` (`parseYaml`) or an entity overlay unchanged, and two
/// renders of the same tree are byte-identical (GP 5).
namespace ToolUp.PublicRendering

open ToolUp.Platform

/// Where a nav node points. `NavSlug` is an internal page (rendered as
/// `/slug`); `NavUrl` is an absolute / external URL passed through
/// verbatim; `NavSection` is a collection / section id (rendered as
/// `/section`) used for grouping headings and section landings.
type NavTarget =
    | NavSlug of Slug
    | NavUrl of string
    | NavSection of string

/// Visibility rule for a nav node, evaluated against the requesting
/// `AccessContext`. Pure data (no predicate function) so a tree stays
/// serialisable / YAML-loadable. `NavPublic` (default) shows to everyone;
/// `NavAuthenticated` to any non-anonymous subject; `NavTeamOnly` to
/// `TeamMember` subjects only.
type NavAudience =
    | NavPublic
    | NavAuthenticated
    | NavTeamOnly

/// One node in the navigation tree. `Children` nest arbitrarily deep
/// (the renderer emits multi-level dropdown markup). Immutable (GP 5).
type NavNode = {
    Label: string
    Target: NavTarget
    Children: NavNode list
    Audience: NavAudience
}

module NavTree =

    // ─── Constructors ────────────────────────────────────────────────

    /// A leaf node pointing at an internal slug, public audience.
    let leaf (label: string) (slug: string) : NavNode = {
        Label = label
        Target = NavSlug(Slug slug)
        Children = []
        Audience = NavPublic
    }

    /// A node pointing at an external / absolute URL, public audience.
    let external (label: string) (url: string) : NavNode = {
        Label = label
        Target = NavUrl url
        Children = []
        Audience = NavPublic
    }

    /// A parent node with children, public audience.
    let node (label: string) (target: NavTarget) (children: NavNode list) : NavNode = {
        Label = label
        Target = target
        Children = children
        Audience = NavPublic
    }

    /// Restrict a node's audience (chainable).
    let withAudience (audience: NavAudience) (n: NavNode) : NavNode = { n with Audience = audience }

    // ─── Targets ─────────────────────────────────────────────────────

    /// The href a target renders to. Internal slugs / sections get a
    /// leading `/`; external URLs pass through verbatim.
    let targetHref (target: NavTarget) : string =
        match target with
        | NavSlug(Slug s) -> "/" + s
        | NavUrl u -> u
        | NavSection sec -> "/" + sec

    // ─── Audience filtering (GP 4) ───────────────────────────────────

    /// Whether a single node is visible to `ctx` (ignores children).
    let isVisible (ctx: AccessContext) (node: NavNode) : bool =
        match node.Audience with
        | NavPublic -> true
        | NavAuthenticated -> AccessContext.isAuthenticated ctx
        | NavTeamOnly -> AccessContext.inTeamScope ctx

    /// Filter a tree to the nodes visible to `ctx`, recursively. A node
    /// the viewer can't see drops together with its whole subtree; a
    /// visible node keeps only its visible children. Order is preserved.
    let rec filter (ctx: AccessContext) (nodes: NavNode list) : NavNode list =
        nodes
        |> List.filter (isVisible ctx)
        |> List.map (fun n -> {
            n with
                Children = filter ctx n.Children
        })

    // ─── Breadcrumbs ─────────────────────────────────────────────────

    /// Derive a breadcrumb trail from a nested slug: each `/`-segment
    /// becomes a `(label, href)` crumb whose href is the accumulated
    /// path. `"services/consulting"` →
    /// `[ ("services", "/services"); ("consulting", "/services/consulting") ]`.
    /// Empty segments are dropped; a flat slug yields a single crumb.
    let breadcrumbFromSlug (Slug slug) : (string * string) list =
        let segs = slug.Split('/') |> Array.filter (fun s -> s <> "")

        segs
        |> Array.mapi (fun i seg ->
            let href = "/" + (segs[0..i] |> String.concat "/")
            seg, href)
        |> Array.toList

    /// Find the root-first trail of nodes from the tree to the node
    /// targeting `slug` (an internal `NavSlug`). Returns the ancestor
    /// chain including the matched node, or `[]` when no node targets the
    /// slug. Used to highlight the active branch and derive a
    /// nav-anchored breadcrumb.
    let findTrail (Slug target) (nodes: NavNode list) : NavNode list =
        let rec go path nodes =
            nodes
            |> List.tryPick (fun n ->
                let path' = path @ [ n ]

                match n.Target with
                | NavSlug(Slug s) when s = target -> Some path'
                | _ -> go path' n.Children)

        go [] nodes |> Option.defaultValue []

    // ─── Auto-nav from a collection ──────────────────────────────────

    /// Generate a flat section menu from a collection's pages — the
    /// docs-site "chapter list from the `docs` collection" shape. Each
    /// page becomes a public `NavSlug` leaf labelled by its `Title`,
    /// preserving the input order (the caller sorts the collection).
    let ofCollection (pages: PublicPage list) : NavNode list =
        pages
        |> List.map (fun p -> {
            Label = p.Title
            Target = NavSlug p.Slug
            Children = []
            Audience = NavPublic
        })

    /// Phase 98 — `ofCollection` paginated: page `page` of size
    /// `pageSize` of the collection's nodes, plus the `PageSlice`
    /// pagination contract (so a layout can render a pager). `pageSize
    /// <= 0` = the whole collection as page 1 of 1 (unchanged).
    let ofCollectionPaged (pageSize: int) (page: int) (pages: PublicPage list) : PageSlice<NavNode> =
        Pagination.paginate pageSize page (ofCollection pages)

    // ─── nav.yaml loader ─────────────────────────────────────────────
    //
    // A deliberately small indentation-based parser for the nav.yaml
    // shape — list items keyed by `label`, a target (`slug` / `url` /
    // `section`), an optional `audience`, and nested `children`. Mirrors
    // `FrontmatterParser`'s "hand-rolled, no YamlDotNet dependency"
    // posture (GP 1). The supported subset:
    //
    //   - label: Home
    //     slug: home
    //   - label: Services
    //     slug: services
    //     children:
    //       - label: Consulting
    //         slug: services/consulting
    //       - label: Partners
    //         url: https://example.com
    //         audience: authenticated
    //
    // 2-space indentation; one `key: value` per line; `#` comments and
    // blank lines ignored. Determinism: no culture-sensitive ops.

    let private indentOf (l: string) = l.Length - (l.TrimStart(' ')).Length

    let private splitKV (s: string) : string * string =
        let i = s.IndexOf(':')

        if i < 0 then
            s.Trim(), ""
        else
            s.Substring(0, i).Trim(), s.Substring(i + 1).Trim()

    let private parseAudience (s: string) : NavAudience =
        match s.Trim().ToLowerInvariant() with
        | "authenticated" -> NavAuthenticated
        | "team"
        | "teamonly" -> NavTeamOnly
        | _ -> NavPublic

    let rec private parseItems (dashIndent: int) (lines: string list) : NavNode list =
        match lines with
        | [] -> []
        | l :: rest when indentOf l = dashIndent && (l.TrimStart(' ')).StartsWith "- " ->
            let bodyIndent = dashIndent + 2
            // The dash line carries the first scalar prop inline
            // (`- label: Home`); re-indent it to sit with the body.
            let firstProp = System.String(' ', bodyIndent) + (l.TrimStart(' ')).Substring(2)
            let bodyLines = rest |> List.takeWhile (fun l2 -> indentOf l2 >= bodyIndent)
            let afterBody = rest |> List.skipWhile (fun l2 -> indentOf l2 >= bodyIndent)
            let node = buildNode bodyIndent (firstProp :: bodyLines)
            node :: parseItems dashIndent afterBody
        | l :: rest ->
            // Dedent ends this block; an unexpected line is skipped.
            if indentOf l < dashIndent then
                []
            else
                parseItems dashIndent rest

    and private buildNode (bodyIndent: int) (lines: string list) : NavNode =
        let scalarLines = lines |> List.filter (fun l -> indentOf l = bodyIndent)
        let childLines = lines |> List.filter (fun l -> indentOf l > bodyIndent)

        let baseNode = {
            Label = ""
            Target = NavSlug(Slug "")
            Children = []
            Audience = NavPublic
        }

        scalarLines
        |> List.fold
            (fun acc line ->
                let key, value = splitKV (line.Trim())

                match key with
                | "label" -> { acc with Label = value }
                | "slug" -> {
                    acc with
                        Target = NavSlug(Slug value)
                  }
                | "url" -> { acc with Target = NavUrl value }
                | "section" -> { acc with Target = NavSection value }
                | "audience" -> {
                    acc with
                        Audience = parseAudience value
                  }
                | "children" -> {
                    acc with
                        Children = parseItems (bodyIndent + 2) childLines
                  }
                | _ -> acc)
            baseNode

    /// Parse a `nav.yaml` document into a nav tree. Comment / blank lines
    /// are ignored; the supported subset is documented above. A malformed
    /// document degrades gracefully (unknown keys ignored, missing target
    /// → an empty internal slug) rather than throwing.
    let parseYaml (text: string) : NavNode list =
        let lines =
            text.Replace("\r\n", "\n").Split('\n')
            |> Array.toList
            |> List.filter (fun l ->
                let t = l.Trim()
                t <> "" && not (t.StartsWith "#"))

        match lines with
        | [] -> []
        | first :: _ -> parseItems (indentOf first) lines