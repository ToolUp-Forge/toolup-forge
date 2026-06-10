namespace ToolUp.PublicRendering

open System
open System.IO
open System.Collections.Concurrent
open System.Threading
open Markdig
open ToolUp.Platform

/// Walks a `ContentRoot` for `**/*.md`, parses YAML frontmatter +
/// markdown body, and exposes the resulting `PublicPage` set. In dev
/// (`hotReload = true`), a `FileSystemWatcher` re-loads the tree on
/// change — debounced to one reload per ~150 ms burst so a save that
/// flushes in multiple chunks doesn't re-parse mid-flight.
///
/// Slug derivation:
///   - `content/pages/about.md`            → slug `"about"`,            collection `None`
///   - `content/services/consulting.md`    → slug `"services/consulting"`, collection `Some "services"`
///   - `content/news/2026-05-22-launch.md` → slug `"news/2026-05-22-launch"`, collection `Some "news"`
///
/// Top-level `pages/` collapses to root; every other top-level
/// subdirectory becomes the page's collection.
///
/// An explicit frontmatter `slug:` value overrides the path-derived
/// slug — useful for folder-index landings (`docs/forms/README.md`
/// can declare `slug: docs/forms` so it lives at the natural folder
/// URL rather than `/docs/forms/README`). The override is normalised:
/// any leading `/` is stripped so `slug: /docs/forms` and
/// `slug: docs/forms` are equivalent. The `Collection` field stays
/// path-derived — the override only affects the URL key, not the
/// page's collection membership.
///
/// Frontmatter (well-known keys):
///   - `title`        → `PublicPage.Title`         (defaults to the slug)
///   - `description`  → `PublicPage.Description`   (defaults to `""`)
///   - `layout`       → `PublicPage.Layout`        (defaults to `"page"`)
///   - `slug`         → overrides the path-derived slug (see above)
///   - `date`         → `PublicPage.PublishedAt`   (ISO 8601 / `DateTimeOffset.Parse`)
///   - `sitemap`      → controls inclusion (`"exclude"` drops the page from `/sitemap.xml`)
///   - arbitrary keys → preserved in `PublicPage.Frontmatter`
type MarkdownContentLoader(root: ContentRoot, logger: ILogger, hotReload: bool) =
    let path = ContentRoot.value root
    let pages = ConcurrentDictionary<string, PublicPage>()
    let mutable redirects: Redirect list = []
    let mutable reloadTimer: Timer = null
    let timerLock = obj ()

    let pipeline =
        MarkdownPipelineBuilder().UseYamlFrontMatter().UseAdvancedExtensions().Build()

    let deriveSlugAndCollection (relativePath: string) : Slug * string option =
        let normalised = relativePath.Replace('\\', '/').TrimStart('/')

        let withoutExt =
            if normalised.EndsWith(".md", StringComparison.OrdinalIgnoreCase) then
                normalised.Substring(0, normalised.Length - 3)
            else
                normalised

        match withoutExt.Split('/') |> Array.toList with
        | "pages" :: rest -> Slug(String.concat "/" rest), None
        | first :: _ when first.StartsWith "_" -> Slug withoutExt, None
        | first :: _ -> Slug withoutExt, Some first
        | [] -> Slug withoutExt, None

    /// Split a markdown file's text into (frontmatter-yaml-text, body-text).
    /// Frontmatter is fenced by a leading `---` line and a closing `---`
    /// line. No frontmatter ⇒ empty yaml block + body = entire input.
    let splitFrontmatter (text: string) : string * string =
        let lines = text.Split([| '\n' |])

        if lines.Length > 0 && lines[0].TrimEnd().StartsWith "---" then
            let mutable closingIdx = -1

            for i in 1 .. lines.Length - 1 do
                if closingIdx = -1 && lines[i].TrimEnd() = "---" then
                    closingIdx <- i

            if closingIdx > 0 then
                let yamlSlice = lines[1 .. closingIdx - 1]
                let bodySlice = lines[closingIdx + 1 ..]
                let yaml = String.concat "\n" yamlSlice
                let body = String.concat "\n" bodySlice
                yaml, body
            else
                "", text
        else
            "", text

    let parsePage (file: string) (relativePath: string) : PublicPage =
        let text = File.ReadAllText(file)
        let pathDerivedSlug, collection = deriveSlugAndCollection relativePath
        let yamlText, body = splitFrontmatter text
        let frontmatter = FrontmatterParser.parse yamlText

        // A `slug:` override in frontmatter wins over the path-derived
        // slug; `Collection` stays path-derived so the override only
        // changes the URL key, not collection membership. Empty values
        // and whitespace-only values are ignored (treat as absent).
        // Leading `/` stripped so `slug: /docs/forms` and
        // `slug: docs/forms` are equivalent.
        let slug =
            frontmatter
            |> Map.tryFind "slug"
            |> Option.bind (fun s ->
                let trimmed = s.Trim().TrimStart('/')

                if String.IsNullOrEmpty trimmed then
                    None
                else
                    Some(Slug trimmed))
            |> Option.defaultValue pathDerivedSlug

        let title =
            frontmatter
            |> Map.tryFind "title"
            |> Option.defaultWith (fun () -> Slug.value slug)

        let description = frontmatter |> Map.tryFind "description" |> Option.defaultValue ""

        let layout =
            frontmatter |> Map.tryFind "layout" |> Option.defaultValue "page" |> LayoutName

        let publishedAt =
            frontmatter
            |> Map.tryFind "date"
            |> Option.bind (fun s ->
                match DateTimeOffset.TryParse s with
                | true, dt -> Some dt
                | _ -> None)

        let html = Markdown.ToHtml(body, pipeline)

        // Phase 89 — optional `status` frontmatter key gates file-backed
        // pages through the publish lifecycle. Absent / unrecognised →
        // `Published`, preserving the pre-89 always-published behaviour
        // (GP 11).
        let status =
            match
                frontmatter
                |> Map.tryFind "status"
                |> Option.map (fun s -> s.Trim().ToLowerInvariant())
            with
            | Some "draft" -> Draft
            | Some "archived" -> Archived
            | _ -> Published

        {
            Slug = slug
            Title = title
            Description = description
            Body = Html html
            Layout = layout
            Frontmatter = frontmatter
            PublishedAt = publishedAt
            Collection = collection
            Status = status
        }

    let loadRedirects () =
        let csv = Path.Combine(path, "redirects.csv")

        if File.Exists csv then
            try
                redirects <-
                    File.ReadAllLines(csv)
                    |> Array.filter (fun l ->
                        let t = l.TrimStart()
                        not (t.StartsWith "#") && t <> "")
                    |> Array.choose (fun line ->
                        let parts = line.Split(',')

                        match parts with
                        | [| from; toUrl |] ->
                            Some {
                                From = from.Trim()
                                To = toUrl.Trim()
                                StatusCode = 301
                            }
                        | [| from; toUrl; status |] ->
                            let code =
                                match Int32.TryParse(status.Trim()) with
                                | true, c -> c
                                | _ -> 301

                            Some {
                                From = from.Trim()
                                To = toUrl.Trim()
                                StatusCode = code
                            }
                        | _ -> None)
                    |> List.ofArray
            with ex ->
                logger.Warn(sprintf "[PublicRendering] Failed to load redirects.csv at %s: %s" csv ex.Message)
        else
            redirects <- []

    let loadAll () =
        pages.Clear()

        if Directory.Exists path then
            for file in Directory.EnumerateFiles(path, "*.md", SearchOption.AllDirectories) do
                try
                    let relativePath = Path.GetRelativePath(path, file)
                    let page = parsePage file relativePath
                    pages[Slug.value page.Slug] <- page
                with ex ->
                    logger.Warn(sprintf "[PublicRendering] Failed to load %s: %s" file ex.Message)

            loadRedirects ()
        else
            logger.Warn(sprintf "[PublicRendering] ContentRoot '%s' does not exist" path)

    let scheduleReload () =
        lock timerLock (fun () ->
            if not (isNull reloadTimer) then
                reloadTimer.Dispose()

            reloadTimer <-
                new Timer(
                    (fun _ ->
                        try
                            loadAll ()
                            logger.Info("[PublicRendering] Hot-reload: content reloaded")
                        with ex ->
                            logger.Warn(sprintf "[PublicRendering] Hot-reload failed: %s" ex.Message)),
                    null,
                    150,
                    Timeout.Infinite
                ))

    do loadAll ()

    let watcher: FileSystemWatcher option =
        if hotReload && Directory.Exists path then
            let w = new FileSystemWatcher(path)
            w.Filter <- "*.md"
            w.IncludeSubdirectories <- true

            w.NotifyFilter <-
                NotifyFilters.LastWrite
                ||| NotifyFilters.FileName
                ||| NotifyFilters.DirectoryName

            w.EnableRaisingEvents <- true
            w.Changed.Add(fun _ -> scheduleReload ())
            w.Created.Add(fun _ -> scheduleReload ())
            w.Deleted.Add(fun _ -> scheduleReload ())
            w.Renamed.Add(fun _ -> scheduleReload ())
            Some w
        else
            None

    member _.GetPage(slug: string) : PublicPage option =
        match pages.TryGetValue slug with
        | true, p -> Some p
        | _ -> None

    member _.ListPages(prefix: string) : PublicPage list =
        pages.Values
        |> Seq.filter (fun p -> (Slug.value p.Slug).StartsWith prefix)
        |> Seq.sortBy (fun p -> Slug.value p.Slug)
        |> List.ofSeq

    member _.GetCollection(collectionId: string) : PublicPage list =
        pages.Values
        |> Seq.filter (fun p -> p.Collection = Some collectionId)
        |> Seq.sortByDescending (fun p -> p.PublishedAt |> Option.defaultValue DateTimeOffset.MinValue)
        |> List.ofSeq

    member _.Redirects: Redirect list = redirects

    /// Force a re-load (test hook + manual-reload escape hatch).
    member _.Reload() : unit = loadAll ()

    interface IDisposable with
        member _.Dispose() =
            match watcher with
            | Some w -> w.Dispose()
            | None -> ()

            lock timerLock (fun () ->
                if not (isNull reloadTimer) then
                    reloadTimer.Dispose()
                    reloadTimer <- null)