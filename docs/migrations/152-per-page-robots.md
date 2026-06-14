# Migration — Phase 152: per-page `robots` directive (`<meta name="robots">` + `X-Robots-Tag`)

**Status:** one net-new opt-in capability, per-page via frontmatter. A page that declares no `robots` key is byte-for-byte pre-152 (GP 11). No consumer action required.

## What changes

Before Phase 152, forge set `X-Robots-Tag: noindex` only on audience-denied (401/403) responses; an otherwise-public page had no way to declare `robots: noindex,nofollow` — needed for thin tag/index pages, faceted-browse duplicates, staging-only slugs, or "linkable but not indexable" utility pages.

Phase 152 reads a `robots` frontmatter key (e.g. `noindex`, `noindex,nofollow`, `noarchive`) and emits it as **two** signals:

1. **`<meta name="robots" content="…">`** in the rendered `<head>` — via `NarrativeLayout.headTags` (the `<head>` half). Applies to every body kind (Markdown / Html / Narrative). Shipped in this phase.
2. **`X-Robots-Tag: …` response header** — the crawler-honoured, non-HTML-covering half, set by the page handler and carried forward on a render-cache hit. See "Track-1 coordination" below.

Absent key → neither tag nor header → page is indexable, byte-for-byte pre-152 (GP 11). **Audience-denied responses keep their unconditional `noindex` regardless of frontmatter** — denial noindex is a security posture, not a content choice (GP 4).

## Head half (shipped)

`NarrativeLayout.robotsMetaTags page` returns the `<meta name="robots">` tag list (`[]` when the `robots` key is absent/blank); `NarrativeLayout.headTags` now prepends it for any body kind. A layout that already drives its head off `headTags` gets the meta tag for free once a page declares `robots:`.

## Track-1 coordination (response header + cache fidelity)

The `X-Robots-Tag` response header + cache-entry carry are in files owned by the conditional-GET / render-cache track (`PublicPageHandler.fs`, `IRenderCache.fs`) and are applied as a coordinated micro-patch there. The change:

1. **`IRenderCache.fs` — `RenderedPage` gains `Robots: string option`** (defaulted to `None` in `RenderedPage.forStore`, so pre-152 cached blobs deserialise safely). This carries the resolved directive so a cache *hit* re-emits the header without re-resolving the page.

2. **`PublicPageHandler.fs` — set `X-Robots-Tag` from the page's `robots` frontmatter** on every serve path that renders a page (`serveUncached`, `serveUncachedConditional`, and `serveCached`'s miss + hit branches), and persist it on the stored `RenderedPage`:

   ```fsharp
   // when a page renders (uncached + cached-miss):
   match page.Frontmatter.TryFind "robots" with
   | Some r when not (System.String.IsNullOrWhiteSpace r) ->
       ctx.Response.Headers["X-Robots-Tag"] <- StringValues(r.Trim())
   | _ -> ()
   // store it on the entry so a cache hit re-emits:
   let rendered = { RenderedPage.forStore html renderedAt with
                       Audience = page.Audience
                       LastModified = lastModified
                       Robots = page.Frontmatter.TryFind "robots" |> Option.map (fun r -> r.Trim()) }
   // on a cache hit, before writing the body:
   match entry.Robots with
   | Some r -> ctx.Response.Headers["X-Robots-Tag"] <- StringValues r
   | None -> ()
   ```

   The audience-denial path (`writeDenied`) is unchanged — it already sets `X-Robots-Tag: noindex` unconditionally (GP 4); content frontmatter cannot weaken it.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project Build.fsproj -- VerifyAll` — green. New Expecto coverage in `PublicRenderingTests.fs` (`PublicRendering — Phase 152 per-page robots meta`): `headTags` emits `<meta name="robots">` from the `robots` key for Markdown / Html / Narrative bodies; value trimmed; absent / blank key → no tag (byte-for-byte); robots meta coexists with Narrative head tags. (The header + cache-fidelity assertions land with the Track-1 micro-patch above.)

## Rollback

Revert the Phase 152 commit (head half) and the Track-1 micro-patch. The `robots` frontmatter key simply stops being read; `RenderedPage.Robots` defaults to `None` so old cache blobs are unaffected. No data migration.
