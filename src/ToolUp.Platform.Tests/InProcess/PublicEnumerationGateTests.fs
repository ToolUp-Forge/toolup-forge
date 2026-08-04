module ToolUp.Platform.Tests.InProcess.PublicEnumerationGateTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── Phase 632 — the public-content enumeration gate, made structural ─
//
// Phase 38 found that the publish-status gate was applied on the page
// READ path only, while every ENUMERATION surface filtered on audience
// alone — an orthogonal axis — so a `Draft` / `Archived` /
// future-`Scheduled` page carrying the default `Audience = Public`
// leaked. 38 fixed the five call sites it found with
// `PublicPage.isPubliclyDiscoverable`.
//
// That fix was correct and advisory: the guarantee rested on five
// callers each remembering to conjoin both axes. A sixth surface added
// later got it wrong by default, and one had — `TaxonomyHandler`'s
// `/tag/{slug}` index, compose-wired by Phase 100 to the raw
// `ListPages`. This pack pins the structural replacement:
//
//   Part 1 — the RAG-ingestion audit, pinned as a test rather than left
//            as prose. (The finding is NEGATIVE; see the header there.)
//   Part 2 — a source sweep asserting no public enumeration path in
//            `ToolUp.PublicRendering` reaches raw `ListPages`, so a
//            future SEVENTH surface fails this gate instead of leaking.
//   Part 3 — behavioural cases with teeth: each red when the gate is
//            removed, each paired with a published control so an
//            over-correction into "nothing is served" fails too.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private mkPage (slug: string) (title: string) (tagCsv: string) (status: PublishStatus) : PublicPage = {
    Slug = Slug slug
    Title = title
    Description = ""
    Body = Markdown ""
    Layout = LayoutName "page"
    Frontmatter =
        (if tagCsv = "" then
             Map.empty
         else
             Map.ofList [ "tags", tagCsv ])
    PublishedAt = None
    Collection = None
    Status = status
    Audience = PageAudience.Public
}

/// An `IPublicContentApi` over a fixed page list whose `ListPages` is
/// deliberately UNGATED (it returns drafts) and whose `ListPagesPublic`
/// is the shipped default body. A fake that pre-filtered its own store
/// would make every case below vacuous.
let private fakeApi (pages: PublicPage list) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage slug = async { return pages |> List.tryFind (fun p -> Slug.value p.Slug = slug) }

        member _.ListPages(prefix: string) = async {
            return pages |> List.filter (fun p -> (Slug.value p.Slug).StartsWith prefix)
        }

        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection collectionId = async {
            return pages |> List.filter (fun p -> p.Collection = Some collectionId)
        }

        member _.GetPageInContext(slug, _ctx) = async {
            return pages |> List.tryFind (fun p -> Slug.value p.Slug = slug)
        }
    }

let private anonCtx = AccessContext.unrestricted (AnonymousSession "anon")

// ─── Part 1 — the RAG-ingestion audit ─────────────────────────────────
//
// **The question Phase 38 declined to answer, answered.**
// `SemanticSearchHandler` serves `/search?q=` from `IRetrievalPipeline`
// — the RAG vector index — not from `ListPages`, so the Phase 38 gate
// never applied to it. 38 listed the open question rather than quietly
// closing it, explicitly claiming no leak either way. This phase audited
// it to a conclusion.
//
// **The finding is NEGATIVE: no `PublicPage` — published or not — can
// reach the index `SemanticSearchHandler` answers from.** The evidence
// is structural rather than a survey of call sites, which is why it is
// expressible as a test at all:
//
//   1. `ToolUp.PublicRendering` references only `ToolUp.Platform.Core` +
//      `ToolUp.Platform.Server`. It has no reference to `ToolUp.RAG.*`
//      at all — it reaches retrieval through the `IRetrievalPipeline`
//      interface in `Platform.Server` (GP 1). Retrieval is a READ port;
//      there is no ingestion port on it. So PublicRendering cannot
//      ingest anything, of any type.
//   2. The only in-tree code that enqueues RAG ingestion is
//      `ToolUp.KnowledgeBase.Server` (document upload, notes, narrative
//      commit), and the service that drains the queue is
//      `ToolUp.RAG.Server`. **Neither references
//      `ToolUp.PublicRendering`**, so neither can even name the
//      `PublicPage` type, let alone chunk one.
//
// So the index holds knowledge-base documents, notes and committed
// narratives — a different content domain, scope-gated at retrieval
// against the caller's `AccessContext` (GP 4) — and never public pages.
// Nothing to gate; nothing to re-index on unpublish. The corollary is
// that `SemanticSearchHandler` needed no change in this phase, and the
// migration doc says so.
//
// **The one adjacent shape, stated so it is not mistaken for this one.**
// A deployment can commit a narrative to its KB (ingested) and *also*
// publish that narrative as a page via `INarrativePagePublisher`. Those
// are two independent artefacts. Unpublishing the page does not evict
// the KB copy, and should not: the KB copy is content the deployment
// authorised into its own scope, and which scopes `/search` queries is
// the deployment's `SemanticSearchConfig.Scopes` decision, validated per
// caller by the pipeline. That is a composition choice with its own
// gate, not a bypass of this one.
//
// These cases pin the finding. If a future phase wires page ingestion —
// a perfectly reasonable feature — they go red, and the gate decision
// (ingest gated, or ingest all and filter at query time) is forced then
// rather than discovered later by a crawler.

let private assemblyRefs (asmName: string) : string list =
    let asm =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Array.tryFind (fun a -> a.GetName().Name = asmName)
        |> Option.defaultWith (fun () -> Assembly.Load asmName)

    asm.GetReferencedAssemblies()
    |> Array.choose (fun an -> Option.ofObj an.Name)
    |> List.ofArray

let private ragIngestionAuditTests =
    testList "Phase 632 — RAG-ingestion audit (the Phase 38 open question, settled)" [

        test "ToolUp.PublicRendering references no RAG assembly, so it cannot ingest anything" {
            let refs = assemblyRefs "ToolUp.PublicRendering"

            let ragRefs =
                refs
                |> List.filter (fun r -> r.StartsWith("ToolUp.RAG", StringComparison.Ordinal))

            Expect.isEmpty
                ragRefs
                "PublicRendering reaches retrieval through IRetrievalPipeline (a read port in Platform.Server), never the RAG companion — so no public page can be ingested from here"

            // CONTROL: the probe can see references at all, so an empty
            // result means "no RAG reference", not "no references read".
            Expect.contains refs "ToolUp.Platform.Server" "CONTROL: the reference sweep sees the references it should"
        }

        test "the assemblies that DO ingest cannot name PublicPage" {
            // KnowledgeBase.Server is the only in-tree enqueuer of RAG
            // ingestion jobs; RAG.Server drains the queue and writes the
            // vector index. Neither can reference the page type.
            for ingester in [ "ToolUp.KnowledgeBase.Server"; "ToolUp.RAG.Server" ] do
                let refs = assemblyRefs ingester

                Expect.isFalse
                    (List.contains "ToolUp.PublicRendering" refs)
                    $"{ingester} must not reference ToolUp.PublicRendering — if it ever does, unpublished-page ingestion becomes expressible and this gate must extend to the index"

                Expect.contains
                    refs
                    "ToolUp.Platform.Server"
                    "CONTROL: the reference sweep sees the references it should"
        }
    ]

// ─── Part 2 — the structural probe ────────────────────────────────────
//
// A source sweep over `src/ToolUp.PublicRendering/`, in the spirit of the
// Phase 174 architecture-fitness file walk: reflection cannot answer
// "which method called `ListPages`" without IL analysis, and an
// enumerate-every-surface behavioural test only ever covers the surfaces
// the test already knows about — which is exactly the class that failed
// here. A text scan catches the surface that does not exist yet.
//
// The detector is pure over `(filename, source)`, so the non-vacuity
// case below feeds it a planted violation and proves it fails closed.

/// One `<receiver>.ListPages` occurrence in source.
type ListPagesUse = {
    File: string
    Line: int
    Receiver: string
    /// `true` when the occurrence is an interface-member *definition*
    /// (`member _.ListPages …`) rather than a call.
    IsDefinition: bool
    /// `true` when the occurrence sits inside a `//` comment.
    IsComment: bool
}

/// `\b` after `ListPages` deliberately excludes `.ListPagesPublic` —
/// `s` and `P` are both word characters, so no boundary exists there.
/// The optional `member ` prefix distinguishes a definition from a call
/// even when both appear on one line (`member _.ListPages p = loader.ListPages p`).
let private listPagesPattern =
    Regex(@"(member\s+)?([A-Za-z_][A-Za-z0-9_.']*)\.ListPages\b", RegexOptions.Compiled)

let private lineNumberOf (source: string) (offset: int) : int =
    let mutable line = 1

    for i in 0 .. min (offset - 1) (source.Length - 1) do
        if source[i] = '\n' then
            line <- line + 1

    line

/// Text of the line containing `offset`, up to `offset`.
let private lineHeadBefore (source: string) (offset: int) : string =
    let start =
        match source.LastIndexOf('\n', max 0 (offset - 1)) with
        | -1 -> 0
        | i -> i + 1

    source.Substring(start, offset - start)

/// Every `<receiver>.ListPages` occurrence in one source file, classified.
let scanListPagesUses (filename: string) (source: string) : ListPagesUse list = [
    for m in listPagesPattern.Matches source do
        yield {
            File = filename
            Line = lineNumberOf source m.Index
            Receiver = m.Groups[2].Value
            IsDefinition = m.Groups[1].Success
            IsComment = (lineHeadBefore source m.Index).TrimStart().StartsWith "//"
        }
]

/// The deliberate, unusual `ListPages` readers — each an explicit
/// decision with a stated reason, not a backlog. Anything else reaching
/// the ungated enumeration inside `ToolUp.PublicRendering` is a finding.
///
/// Every entry is asserted to MATCH something, so a stale exemption
/// cannot sit here silently widening the gate.
let allowedRawReaders = [
    "IPublicContentApi.fs",
    "api",
    "`PublicContentApi.defaultListPagesPublic` — this IS the gate: it enumerates ungated and then filters."

    "PublicContentApiImpl.fs",
    "loader",
    "`MarkdownContentLoader` is the backing store, not the seam; the impl gates it on the way out."

    "NarrativeLayout.fs",
    "inner",
    "`SelfCanonical.wrap` is a decorator pass-through — it preserves whatever the inner impl does and enumerates nothing itself."
]

/// Findings = call sites (not definitions, not comments) that are not
/// allow-listed.
let ungatedEnumerationFindings (uses: ListPagesUse list) : ListPagesUse list =
    let allowed = allowedRawReaders |> List.map (fun (f, r, _) -> (f, r)) |> Set.ofList

    uses
    |> List.filter (fun u ->
        not u.IsDefinition
        && not u.IsComment
        && not (allowed.Contains(Path.GetFileName u.File, u.Receiver)))

let private publicRenderingSources () : string list =
    let asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let repoRoot = Path.GetFullPath(Path.Combine(asmDir, "..", "..", "..", "..", ".."))

    let root = Path.Combine(repoRoot, "src", "ToolUp.PublicRendering")

    if not (Directory.Exists root) then
        []
    else
        Directory.EnumerateFiles(root, "*.fs", SearchOption.AllDirectories)
        |> Seq.filter (fun p ->
            let n = p.Replace('\\', '/')
            not (n.Contains "/bin/" || n.Contains "/obj/"))
        |> List.ofSeq

let private structuralProbeTests =
    testList "Phase 632 — structural probe: no public enumeration path reaches raw ListPages" [

        test "no un-allow-listed ListPages call site exists in ToolUp.PublicRendering" {
            let files = publicRenderingSources ()

            // NON-VACUITY, part 1: the sweep must actually be walking the
            // module. `ToolUp.PublicRendering` ships 40+ server files;
            // a floor well under that catches "the walk found nothing"
            // without breaking when a file is added or removed.
            Expect.isGreaterThan
                (List.length files)
                30
                "the sweep must walk the real PublicRendering source tree, not an empty directory"

            let uses = files |> List.collect (fun f -> scanListPagesUses f (File.ReadAllText f))

            // NON-VACUITY, part 2: the detector must be matching real
            // text. If the member were renamed and this pattern stopped
            // matching, findings would be empty for the wrong reason.
            Expect.isGreaterThan
                (List.length uses)
                4
                "the detector must find the known ListPages occurrences (declaration, impls, decorator)"

            let findings = ungatedEnumerationFindings uses

            let rendered =
                findings
                |> List.map (fun f -> sprintf "  %s:%d — %s.ListPages" (Path.GetFileName f.File) f.Line f.Receiver)
                |> String.concat "\n"

            Expect.isEmpty
                findings
                $"a public-rendering surface reads the UNGATED enumeration. Call `ListPagesPublic(now, prefix)` instead — or, if the raw set is genuinely wanted, add the site to `allowedRawReaders` with a reason.\n{rendered}"
        }

        test "every allow-listed raw reader still exists (no stale exemption)" {
            let uses =
                publicRenderingSources ()
                |> List.collect (fun f -> scanListPagesUses f (File.ReadAllText f))
                |> List.filter (fun u -> not u.IsComment && not u.IsDefinition)

            for (file, receiver, reason) in allowedRawReaders do
                let matched =
                    uses
                    |> List.exists (fun u -> Path.GetFileName u.File = file && u.Receiver = receiver)

                Expect.isTrue
                    matched
                    $"the exemption for `{receiver}.ListPages` in {file} matches nothing — remove it rather than leave a widened gate behind. Stated reason: {reason}"
        }

        testCase "the detector fails closed on a planted violation and ignores the gated call"
        <| fun _ ->
            // The gate is a measurement, not a claim: feed the pure
            // detector a synthetic seventh surface and prove it is caught,
            // then prove the gated form and the exempt forms are not.
            let planted =
                """
module SeventhSurface
    let handler (api: IPublicContentApi) = async {
        let! pages = api.ListPages ""
        return pages
    }
"""

            let findings =
                scanListPagesUses "SeventhSurface.fs" planted |> ungatedEnumerationFindings

            Expect.equal (List.length findings) 1 "a new surface reading raw ListPages is caught"
            Expect.equal findings.Head.Receiver "api" "the offending receiver is named"

            let gated =
                """
    let handler (api: IPublicContentApi) = async {
        let! pages = api.ListPagesPublic(now, "")
        return pages
    }
"""

            Expect.isEmpty
                (scanListPagesUses "Gated.fs" gated |> ungatedEnumerationFindings)
                "the gated call is not flagged (`\\b` excludes ListPagesPublic)"

            let commented =
                """
    /// Pass `fun () -> api.ListPages ""` for the default content API.
    let x = 1
"""

            Expect.isEmpty
                (scanListPagesUses "Doc.fs" commented |> ungatedEnumerationFindings)
                "prose in a doc comment is not a call site"

            let definition =
                "        member _.ListPages(prefix: string) = async { return loader.ListPages prefix }"

            Expect.isEmpty
                (scanListPagesUses "PublicContentApiImpl.fs" definition
                 |> ungatedEnumerationFindings)
                "a member definition plus its allow-listed backing-store read on one line is not a finding"
    ]

// ─── Part 3 — the gated enumeration, behaviourally ────────────────────

/// The wall clock, deliberately — `TaxonomyHandler.tagIndexSource` is an
/// `IContentSource` whose `Resolve` / `EnumerateRoutes` carry no clock
/// parameter, so it can only gate at `DateTimeOffset.UtcNow`. Pinning the
/// fixture to a fixed literal instead would make the Part-3b cases pass
/// or fail depending on the date the suite is run, which is the
/// characteristic way a publish-lifecycle test goes vacuously green. The
/// ±7-day windows below are far wider than any plausible drift between
/// this binding and the source's own clock read.
let private now = DateTimeOffset.UtcNow

let private mixedPages = [
    mkPage "live" "Live Page" "launch" Published
    mkPage "secret-launch" "Project Nightingale" "launch,nightingale" PublishStatus.Draft
    mkPage "retired" "Retired Page" "launch" Archived
    mkPage "embargoed" "Embargoed Announcement" "launch,embargo" (Scheduled(now.AddDays 7.0))
    mkPage "released" "Released Page" "launch" (Scheduled(now.AddDays -7.0))
]

let private seamTests =
    testList "Phase 632 — the gated enumeration on IPublicContentApi" [

        // The negative control, stated as a measurement: raw `ListPages`
        // DOES return the draft. That is what a caller reaching past the
        // gate gets, asserted directly — so a reverter can see exactly
        // what they restore.
        test "raw ListPages admits everything; ListPagesPublic admits only the discoverable" {
            let api = fakeApi mixedPages

            let raw =
                api.ListPages ""
                |> Async.RunSynchronously
                |> List.map (fun p -> Slug.value p.Slug)

            Expect.contains
                raw
                "secret-launch"
                "UNGATED BY DESIGN: raw ListPages returns the draft — this is what a surface that skips the gate is handed"

            let gated =
                api.ListPagesPublic(now, "")
                |> Async.RunSynchronously
                |> List.map (fun p -> Slug.value p.Slug)
                |> List.sort

            Expect.equal gated [ "live"; "released" ] "the gated enumeration is exactly the discoverable set"
            Expect.isFalse (List.contains "secret-launch" gated) "no draft"
            Expect.isFalse (List.contains "retired" gated) "no archived"
            Expect.isFalse (List.contains "embargoed" gated) "no future-scheduled"
        }

        test "ListPagesPublic honours the prefix and the gate together" {
            let api =
                fakeApi [
                    mkPage "news/live" "Live" "" Published
                    mkPage "news/draft" "Draft" "" PublishStatus.Draft
                    mkPage "about" "About" "" Published
                ]

            let slugs =
                api.ListPagesPublic(now, "news/")
                |> Async.RunSynchronously
                |> List.map (fun p -> Slug.value p.Slug)

            Expect.equal slugs [ "news/live" ] "prefix AND gate, not either"
        }

        test "a scheduled page crosses the gate exactly at its publish instant" {
            let at = DateTimeOffset.Parse "2026-06-01T12:00:00Z"
            let api = fakeApi [ mkPage "embargoed" "Embargoed" "" (Scheduled at) ]

            let visibleAt (t: DateTimeOffset) =
                api.ListPagesPublic(t, "") |> Async.RunSynchronously |> List.isEmpty |> not

            Expect.isFalse (visibleAt (at.AddSeconds -1.0)) "absent one second before"
            Expect.isTrue (visibleAt at) "present exactly at the publish instant"
            Expect.isTrue (visibleAt (at.AddSeconds 1.0)) "present after"
        }

        test "a non-Public audience is gated even when Published (both axes still conjoined)" {
            let api =
                fakeApi [
                    {
                        mkPage "internal" "Internal" "" Published with
                            Audience = PageAudience.Authenticated
                    }
                    mkPage "live" "Live" "" Published
                ]

            let slugs =
                api.ListPagesPublic(now, "")
                |> Async.RunSynchronously
                |> List.map (fun p -> Slug.value p.Slug)

            Expect.equal slugs [ "live" ] "CONTROL + gate: the audience axis is not lost by the new member"
        }

        test "the self-canonical decorator preserves the gate rather than re-deriving it" {
            let wrapped =
                NarrativeLayout.SelfCanonical.wrap "https://example.com" (fakeApi mixedPages)

            let slugs =
                wrapped.ListPagesPublic(now, "")
                |> Async.RunSynchronously
                |> List.map (fun p -> Slug.value p.Slug)
                |> List.sort

            Expect.equal slugs [ "live"; "released" ] "a decorated API is gated exactly as its inner"
        }

        test "GP 11 — an all-published deployment sees the gated and ungated sets agree" {
            let api =
                fakeApi [
                    mkPage "a" "A" "" Published
                    mkPage "b" "B" "" Published
                    mkPage "c" "C" "" Published
                ]

            let slugsOf (ps: PublicPage list) =
                ps |> List.map (fun p -> Slug.value p.Slug) |> List.sort

            Expect.equal
                (slugsOf (api.ListPagesPublic(now, "") |> Async.RunSynchronously))
                (slugsOf (api.ListPages "" |> Async.RunSynchronously))
                "a deployment that never adopted the publish lifecycle is byte-for-byte unchanged"
        }
    ]

// ─── Part 3b — the sixth surface this phase found ─────────────────────
//
// `TaxonomyHandler.tagIndexSource` was compose-wired by Phase 100
// (`withTaxonomy`) against the raw `ListPages` and shipped before Phase
// 38's audit. It leaked on BOTH arms, and the enumerate arm is the worse
// of the two: `SitemapGenerator.entriesAt` appends content-source
// `dynamicSlugs` to the universe WITHOUT re-gating them (they are routes,
// not pages, so there is nothing to gate them against), so a tag that
// existed only on an unpublished page became a live `/tag/{x}` URL in
// `sitemap.xml`, the static export, and the IndexNow push.

let private sixthSurfaceTests =
    testList "Phase 632 — the sixth surface: /tag/{slug} taxonomy index" [

        test "the tag-index body lists no unpublished page (title or slug)" {
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return mixedPages })

            match source.Resolve (Slug "tag/launch") anonCtx |> Async.RunSynchronously with
            | Some(Narrative doc) ->
                let html = NarrativeHtml.render doc

                Expect.stringContains html "Live Page" "CONTROL: the published page is listed"
                Expect.stringContains html "/live" "CONTROL: and links to its slug"
                Expect.stringContains html "Released Page" "CONTROL: a due-scheduled page is listed"

                Expect.isFalse (html.Contains "Project Nightingale") "the draft's TITLE never reaches the tag index"
                Expect.isFalse (html.Contains "secret-launch") "nor its slug"
                Expect.isFalse (html.Contains "Retired Page") "an archived page is not listed"
                Expect.isFalse (html.Contains "Embargoed Announcement") "a future-scheduled page is not listed"
            | other -> failtestf "expected a Narrative tag-index body, got %A" other
        }

        test "the tag-index page COUNT does not leak the size of the unpublished set" {
            // The subtitle is the second-order oracle: even with every
            // title suppressed, "4 pages" on a tag with one published
            // member would tell a crawler three unpublished pages exist.
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return mixedPages })

            match source.Resolve (Slug "tag/launch") anonCtx |> Async.RunSynchronously with
            | Some(Narrative doc) ->
                Expect.equal doc.Subtitle (Some "2 pages") "the count is over the discoverable set only"
            | other -> failtestf "expected a Narrative tag-index body, got %A" other
        }

        test "route enumeration emits no tag that exists only on unpublished pages" {
            // This is the leak that reached crawlers: an enumerated route
            // is appended to the sitemap universe un-regated.
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return mixedPages })

            match source with
            | :? IEnumerableContentSource as e ->
                let slugs =
                    e.EnumerateRoutes()
                    |> Async.RunSynchronously
                    |> List.map Slug.value
                    |> List.sort

                Expect.equal slugs [ "tag/launch" ] "only tags carried by a discoverable page are enumerated"

                Expect.isFalse
                    (List.contains "tag/nightingale" slugs)
                    "a tag drawn ONLY from a draft is never enumerated as a live route"

                Expect.isFalse (List.contains "tag/embargo" slugs) "nor one drawn only from a future-scheduled page"
            | _ -> failtest "tagIndexSource must still implement IEnumerableContentSource"
        }

        test "an enumerated tag route does not survive into the sitemap universe" {
            // End to end through the chokepoint, because the enumerate arm
            // and `SitemapGenerator` are where the two halves meet.
            let source = TaxonomyHandler.tagIndexSource (fun () -> async { return mixedPages })

            let dynamicSlugs = ContentSource.enumerateAll [ source ] |> Async.RunSynchronously

            let universe =
                SitemapGenerator.entriesAt now mixedPages dynamicSlugs
                |> List.map (fun (Slug s, _) -> s)
                |> List.sort

            Expect.equal
                universe
                [ "live"; "released"; "tag/launch" ]
                "the whole crawl universe is the discoverable set"

            Expect.isFalse
                (List.contains "tag/nightingale" universe)
                "no draft-only tag route is pushed to search engines"
        }

        test "tagIndexSourceFromApi draws from the gated seam" {
            let source = TaxonomyHandler.tagIndexSourceFromApi (fakeApi mixedPages)

            match source with
            | :? IEnumerableContentSource as e ->
                let slugs = e.EnumerateRoutes() |> Async.RunSynchronously |> List.map Slug.value

                Expect.equal slugs [ "tag/launch" ] "the compose-wired form is gated too"
            | _ -> failtest "tagIndexSourceFromApi must implement IEnumerableContentSource"
        }
    ]

let tests =
    testList "PublicRendering — Phase 632 enumeration gate" [
        ragIngestionAuditTests
        structuralProbeTests
        seamTests
        sixthSurfaceTests
    ]