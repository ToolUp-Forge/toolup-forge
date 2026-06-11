// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform

// ─── Phase 83 — request-time / data-bound SSR content resolution ─────
//
// `IContentSource` is the seam that turns `ToolUp.PublicRendering` from
// a brochure / docs engine into a data-bound application surface. A
// source computes a page body PER REQUEST from backend data (analytics,
// entity queries, retrieval) rather than loading it from a markdown file
// or the runtime-edited entity overlay.
//
// `PublicContentApiImpl` consults registered sources AFTER its file +
// entity-overlay tiers (registration order, first `Some` wins). A
// deployment that registers no sources is byte-for-byte identical to the
// pre-83 file+overlay chain (GP 11) and pays nothing (GP 13).

/// Request-time content resolver. Produces a `ContentBody` for a slug,
/// scoped to the requesting principal, or `None` to fall through.
///
/// **Six portability rules** (per `CLAUDE.md` — every substrate
/// interface that could plausibly be implemented by a distributed
/// framework must satisfy all six):
///
///   1. **Identity by value.** Inputs are `Slug` and `AccessContext`
///      (records of `string` / `Guid` / DU primitives); the output is
///      `ContentBody` (a string / HTML fragment / `NarrativeDocument`).
///      No `IActorRef` / `IGrainReference` / live handles. ✓
///   2. **Async at every boundary.** `Resolve` returns `Async<_>`. ✓
///   3. **Retry + supervision as data.** N/A — a resolver is a
///      read-only query surface with no retry policy to express. A
///      distributed impl that needs retry on a transient backing-store
///      failure wraps its own record-shaped policy; not part of this
///      interface. ✓
///   4. **Stateless between invocations.** A resolver receives all
///      per-request state via parameters (`slug`, `ctx`). It must not
///      depend on state held between calls — an Orleans grain can
///      deactivate, an Akka actor can restart between two `Resolve`
///      calls. (Impls may cache process-lifetime read-only data —
///      e.g. a compiled template — the same way `MarkdownContentLoader`
///      caches its file map.) ✓
///   5. **No cross-shard ordering promises.** A single `Resolve` is a
///      point lookup; there is no cross-call ordering contract. ✓
///   6. **Precision at the lower bound.** N/A — no scheduling / timing
///      primitives on this interface. ✓
type IContentSource =
    /// Resolve a slug to a content body, scoped to the requesting
    /// principal's `AccessContext`. Return `None` to fall through to the
    /// next registered source (or, when every source declines, to the
    /// file/overlay-miss 404 path). The `AccessContext` lets the
    /// resolver scope its backing query to the caller (GP 4) — e.g. a
    /// `TeamMember`'s team-scoped dashboard, an `AnonymousSession`'s
    /// public-only view.
    abstract Resolve: slug: Slug -> ctx: AccessContext -> Async<ContentBody option>


/// Slug-pattern matching for route-shape content sources. A pattern is a
/// `/`-delimited template where a `{name}` segment captures one path
/// segment: `"services/{client}"` matches `"services/acme"`, capturing
/// `client = "acme"`. Literal segments must match exactly (case-sensitive,
/// matching the filesystem-slug convention used elsewhere in this
/// companion). Segment counts must be equal — `{rest}` does not greedily
/// span multiple segments.
module RouteShape =

    let private isCapture (seg: string) =
        seg.Length >= 2 && seg.StartsWith "{" && seg.EndsWith "}"

    /// Try to match `slug` against `pattern`. Returns the captured
    /// segment map on a full match (every segment accounted for), or
    /// `None` when the segment counts differ or a literal segment
    /// mismatches. An empty capture map is a valid `Some` result for a
    /// fully-literal pattern that matches.
    let tryMatch (pattern: string) (slug: string) : Map<string, string> option =
        let pSegs = pattern.Split('/')
        let sSegs = slug.Split('/')

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


/// Phase 95 — optional capability a content source MAY also implement:
/// enumerate the concrete slugs it can produce, so `sitemap.xml`, the
/// static-export build, and prerender discover its dynamic pages. A
/// source implements this IN ADDITION to `IContentSource`; sources that
/// don't are unaffected — their dynamic pages stay request-only, exactly
/// as pre-95 (GP 11). `EnumerateRoutes` should return only PUBLICLY
/// discoverable slugs (a source that gates some of its pages omits them),
/// returns by value (`Slug list`), and is async + stateless — the six
/// portability rules hold.
type IEnumerableContentSource =
    abstract EnumerateRoutes: unit -> Async<Slug list>


/// Phase 111 — optional capability a content source MAY also implement:
/// resolve to a `ResolvedContent` (body + per-request `<head>` metadata
/// + provenance) instead of a bare `ContentBody`, so a data-bound page
/// is SEO-complete (canonical / `og:image` / extra meta / JSON-LD)
/// without a frontmatter file. `PublicContentApiImpl` prefers this
/// interface when present; plain `IContentSource` impls are consulted
/// exactly as pre-111 (GP 11). The same six portability rules hold —
/// `ResolvedContent` is a value (records / strings / DUs), the method
/// is async and stateless.
type IResolvedContentSource =
    /// Resolve a slug to a `ResolvedContent`, scoped to the requesting
    /// principal's `AccessContext`. Return `None` to fall through —
    /// identical fall-through semantics to `IContentSource.Resolve`.
    abstract ResolveContent: slug: Slug -> ctx: AccessContext -> Async<ResolvedContent option>


/// Constructors for `IContentSource`. Use `create` for a resolver that
/// claims slugs by its own logic; use `ofRoute` for the common case of a
/// single source claiming a family of dynamic paths by pattern.
module ContentSource =

    /// Build a content source from a plain resolver. The resolver sees
    /// the full slug and the caller's `AccessContext` and decides whether
    /// to claim it (`Some body`) or fall through (`None`).
    let create (resolve: Slug -> AccessContext -> Async<ContentBody option>) : IContentSource =
        { new IContentSource with
            member _.Resolve slug ctx = resolve slug ctx
        }

    /// Build a route-shape content source that claims the family of slugs
    /// matching `pattern` (e.g. `"services/{client}"`, `"dashboard/{quarter}"`).
    /// On a match the captured segments are handed to `resolve` along with
    /// the `AccessContext`; a non-matching slug falls through (`None`)
    /// without invoking `resolve`. The resolver may itself still return
    /// `None` for a matched-but-unknown segment value (e.g. an unknown
    /// `{client}`), falling through to the next source.
    let ofRoute
        (pattern: string)
        (resolve: Map<string, string> -> AccessContext -> Async<ContentBody option>)
        : IContentSource =
        { new IContentSource with
            member _.Resolve (Slug s) ctx =
                match RouteShape.tryMatch pattern s with
                | Some captures -> resolve captures ctx
                | None -> async { return None }
        }

    /// Phase 95 — like `ofRoute`, but the returned source also implements
    /// `IEnumerableContentSource`: `enumerate` returns the concrete slugs
    /// this route-family currently produces, so sitemap / static export /
    /// prerender include them. The resolver and the enumerator share the
    /// same backing data (e.g. the page set), so they agree on what
    /// exists.
    let ofRouteEnumerable
        (pattern: string)
        (resolve: Map<string, string> -> AccessContext -> Async<ContentBody option>)
        (enumerate: unit -> Async<Slug list>)
        : IContentSource =
        { new IContentSource with
            member _.Resolve (Slug s) ctx =
                match RouteShape.tryMatch pattern s with
                | Some captures -> resolve captures ctx
                | None -> async { return None }

          interface IEnumerableContentSource with
              member _.EnumerateRoutes() = enumerate ()
        }

    // ─── Phase 111 — resolved-content constructors ───────────────────

    /// Build a head-metadata-aware content source from a plain resolver.
    /// The returned source implements BOTH `IContentSource` (the bare
    /// body, so any pre-111 consumer of the source still works) and
    /// `IResolvedContentSource` (the full `ResolvedContent`, which
    /// `PublicContentApiImpl` prefers).
    let ofResolved (resolve: Slug -> AccessContext -> Async<ResolvedContent option>) : IContentSource =
        { new IContentSource with
            member _.Resolve slug ctx = async {
                let! rc = resolve slug ctx
                return rc |> Option.map _.Body
            }

          interface IResolvedContentSource with
              member _.ResolveContent slug ctx = resolve slug ctx
        }

    /// Phase 111 — like `ofRoute`, but the resolver returns a
    /// `ResolvedContent` (body + per-request head metadata + provenance).
    let ofRouteResolved
        (pattern: string)
        (resolve: Map<string, string> -> AccessContext -> Async<ResolvedContent option>)
        : IContentSource =
        let resolveContent (Slug s) ctx =
            match RouteShape.tryMatch pattern s with
            | Some captures -> resolve captures ctx
            | None -> async { return None }

        { new IContentSource with
            member _.Resolve slug ctx = async {
                let! rc = resolveContent slug ctx
                return rc |> Option.map _.Body
            }

          interface IResolvedContentSource with
              member _.ResolveContent slug ctx = resolveContent slug ctx
        }

    /// Phase 111 — like `ofRouteResolved`, additionally implementing
    /// `IEnumerableContentSource` so the route-family's dynamic slugs
    /// reach `sitemap.xml`, static export, prerender, and IndexNow
    /// (Phase 95 / Phase 109 reach for resolved-content pages).
    let ofRouteResolvedEnumerable
        (pattern: string)
        (resolve: Map<string, string> -> AccessContext -> Async<ResolvedContent option>)
        (enumerate: unit -> Async<Slug list>)
        : IContentSource =
        let resolveContent (Slug s) ctx =
            match RouteShape.tryMatch pattern s with
            | Some captures -> resolve captures ctx
            | None -> async { return None }

        { new IContentSource with
            member _.Resolve slug ctx = async {
                let! rc = resolveContent slug ctx
                return rc |> Option.map _.Body
            }

          interface IResolvedContentSource with
              member _.ResolveContent slug ctx = resolveContent slug ctx

          interface IEnumerableContentSource with
              member _.EnumerateRoutes() = enumerate ()
        }

    /// Collect the public routes every `IEnumerableContentSource` in the
    /// list advertises, deduped, preserving registration order. Sources
    /// that don't implement `IEnumerableContentSource` contribute nothing.
    /// Used by the sitemap generator + static export to discover dynamic
    /// pages.
    let enumerateAll (sources: IContentSource list) : Async<Slug list> = async {
        let acc = System.Collections.Generic.List<Slug>()

        for s in sources do
            match s with
            | :? IEnumerableContentSource as e ->
                let! routes = e.EnumerateRoutes()
                acc.AddRange routes
            | _ -> ()

        return acc |> List.ofSeq |> List.distinct
    }