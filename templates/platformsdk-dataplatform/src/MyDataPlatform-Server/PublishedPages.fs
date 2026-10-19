module MyDataPlatform.Server.PublishedPages

open System
open System.Threading.Tasks
open Giraffe.ViewEngine
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering
open ToolUp.PublicRendering.PublicRenderingCompose

// ─── The published report pages ───────────────────────────────────
//
// `insights/latest` and `insights/{date}` serve the daily report the
// grounded narrative run published. The AUDIENCE RIDES ON THE PAGE: forge's
// page handler gates every read against it (401 to an anonymous caller, 403
// to a principal holding none of the roles), and the sitemap, feeds, search
// index and static export never list a page that is not Public. A page
// served through an `IContentSource` would be synthesised Public, so the
// pages are declared here, in front of forge's own content API, instead.
//
// The report is read from the READER's own storage scope, so a team reads
// only the reports its own deliveries produced. Platform admins bypass the
// role check; anyone else needs the reader role, held through team
// permissions or asserted by the identity provider's role or group claims.

/// The role a reader needs. Map a directory group to it with the identity
/// provider's claim mapping (docs/platform/gated-ssr.md).
[<Literal>]
let ReaderRole = "report-reader"

let private audience = PageAudience.ScopeGated [ ReaderRole ]

[<Literal>]
let private Layout = "report"

/// The services the running host composed, captured once it starts: a
/// published page reads the narrative store at request time.
type private HostServices() =
    static member val Current: IServiceProvider option = None with get, set

type private CaptureServices(services: IServiceProvider) =
    interface IHostedService with
        member _.StartAsync _ =
            HostServices.Current <- Some services
            Task.CompletedTask

        member _.StopAsync _ = Task.CompletedTask

/// Register the capture on the host's services.
let captureServices (app: ServerApp) : ServerApp =
    let register (s: IServiceCollection) =
        s.AddHostedService<CaptureServices>() |> ignore
        s

    let ext = app.Extensions

    {
        app with
            Extensions = {
                ext with
                    ServiceConfig =
                        match ext.ServiceConfig with
                        | None -> Some register
                        | Some first -> Some(first >> register)
            }
    }

/// The newest report the run published in the reader's scope on `date`
/// (any date when `None`), when the page's audience admits the reader.
let private report (config: ServerConfig) (date: DateOnly option) (access: AccessContext) = async {
    match HostServices.Current, AudienceGate.evaluate access audience with
    | Some services, AudienceDecision.Allow ->
        match
            services.GetService typeof<INarrativeStore>,
            Middleware.StorageScopeDerivation.tryFromSubject None config access.Subject
        with
        | (:? INarrativeStore as store), Some scope ->
            let! published = store.ListByTag(scope.ScopeId, 400, Some Narratives.DailyReportTag)

            let chosen =
                published
                |> List.filter (fun info -> date |> Option.forall (fun d -> DateOnly.FromDateTime info.PublishedAt = d))
                |> List.sortByDescending _.PublishedAt
                |> List.tryHead

            match chosen with
            | Some info ->
                let! entry = store.Get(scope.ScopeId, info.Id)
                return entry |> Option.map _.Document
            | None -> return None
        | _ -> return None
    | _ -> return None
}

let private emptyState (title: string) =
    Html(sprintf "<section class=\"report\"><h1>%s</h1><p>No report has been published yet.</p></section>" title)

let private page (slug: string) (title: string) (body: ContentBody) : PublicPage = {
    Slug = Slug slug
    Title = title
    Description = ""
    Body = body
    Layout = LayoutName Layout
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = audience
}

/// The declared page serving `slug`, with the report the reader may read.
let private resolve (config: ServerConfig) (slug: string) (access: AccessContext option) = async {
    let read date title = async {
        match access with
        | None -> return Some(page slug title (emptyState title))
        | Some access ->
            match! report config date access with
            | Some document -> return Some(page slug title (Narrative document))
            | None -> return Some(page slug title (emptyState title))
    }

    match RouteShape.tryMatch "insights/latest" slug with
    | Some _ -> return! read None "Latest trading report"
    | None ->
        match RouteShape.tryMatch "insights/{date}" slug with
        | Some captures ->
            match
                DateOnly.TryParseExact(
                    captures["date"],
                    "yyyy-MM-dd",
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.None
                )
            with
            | true, date -> return! read (Some date) (sprintf "Trading report, %s" captures["date"])
            | _ -> return None
        | None -> return None
}

/// The report pages in front of `inner`, forge's own content API, which
/// answers every slug not declared here.
let over (config: ServerConfig) (inner: IPublicContentApi) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage slug = async {
            match! resolve config slug None with
            | Some page -> return Some page
            | None -> return! inner.GetPage slug
        }

        member _.GetPageInContext(slug, ctx) = async {
            match! resolve config slug (Some ctx) with
            | Some page -> return Some page
            | None -> return! inner.GetPageInContext(slug, ctx)
        }

        // The report pages are a family of captured routes nobody listed and
        // are gated, so they are never enumerated.
        member _.ListPages prefix = inner.ListPages prefix

        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection collectionId = inner.GetCollection collectionId
    }

/// The report layout: forge-native HTML around the narrative forge renders.
let layout (page: PublicPage) : XmlNode =
    html [] [
        head [] [
            meta [ _charset "utf-8" ]
            title [] [ str page.Title ]
            style [] [
                rawText "body{font-family:system-ui,sans-serif;max-width:46rem;margin:2rem auto;padding:0 1rem}"
            ]
        ]
        body [] [ main [] [ NarrativeLayout.renderBody page ] ]
    ]

/// Install the pages on the rendering app: the layout, and forge's default
/// content API (the markdown loader over the content root) behind them.
let install (app: PublicRenderingServerApp) : PublicRenderingServerApp =
    match app.Base.Config.PublicRendering with
    | EnabledPublicRendering root ->
        let logger =
            app.Base.Logger
            |> Option.defaultWith (fun () -> ConsoleLogger.ConsoleLogger() :> ILogger)

        let inner =
            PublicContentApiImpl.create (new MarkdownContentLoader(root, logger, app.HotReload)) None []

        app
        |> PublicRenderingServerApp.withLayout (LayoutName Layout) layout
        |> PublicRenderingServerApp.withContentApi (over app.Base.Config inner)
    | NoPublicRendering -> app