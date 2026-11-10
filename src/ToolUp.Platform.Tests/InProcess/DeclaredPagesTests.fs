// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeclaredPagesTests

open System
open System.IO
open Expecto
open Giraffe.ViewEngine
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.Compose
open ToolUp.Platform.Config
open ToolUp.Platform.Narrative
open ToolUp.Platform.Scope
open ToolUp.PublicRendering

let private bodyOf (text: string) (captures: Map<string, string>) =
    Html(text + (captures |> Map.toList |> List.map snd |> String.concat ","))

let private page (route: string) (audience: PageAudience) : DeclaredPage = {
    Route = route
    Title = route
    Layout = LayoutName "report"
    Audience = audience
    Captures = []
    Body = bodyOf route
    Narrative = None
}

let private inner (pages: PublicPage list) : IPublicContentApi =
    let found slug =
        pages |> List.tryFind (fun candidate -> Slug.value candidate.Slug = slug)

    { new IPublicContentApi with
        member _.GetPage slug = async.Return(found slug)
        member _.GetPageInContext(slug, _) = async.Return(found slug)

        member _.ListPages prefix =
            pages
            |> List.filter (fun candidate -> (Slug.value candidate.Slug).StartsWith prefix)
            |> async.Return

        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection _ = async.Return []
    }

let private filePage (slug: string) : PublicPage = {
    Slug = Slug slug
    Title = "file " + slug
    Description = ""
    Body = Html "from the file"
    Layout = LayoutName "report"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = PageAudience.Public
}

let private over pages =
    DeclaredPages.over DeclaredPages.noNarrative pages (inner [ filePage "about"; filePage "insights/2026" ])

let private publication: PageAudience =
    PageAudience.Publication {
        Readers = [ "readers" ]
        PublishingScope = "team-1"
    }

let private run (work: Async<'T>) = Async.RunSynchronously work

let private slugs (pages: PublicPage list) =
    pages |> List.map (fun listed -> Slug.value listed.Slug)

let private bodyText (found: PublicPage option) =
    match found with
    | Some { Body = Html text } -> text
    | other -> failtestf "%A" other

let private composed (configure: PublicRenderingOptions -> PublicRenderingOptions) =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-declared-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory root |> ignore

    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            PublicRendering = EnabledPublicRendering(ContentRoot root)
    }
    |> PublicRenderingCompose.withPublicRendering (configure (PublicRenderingOptions.create ()))
    |> ServerApp.resolve

let private anyLayout (_: PublicPage) : XmlNode = div [] []

[<Tests>]
let tests =
    testList "Declared pages" [
        testCase "a literal route answers before a capture, whatever the declaration order"
        <| fun _ ->
            let api =
                over [
                    page "insights/{date}" PageAudience.Public
                    page "insights/latest" PageAudience.Public
                ]

            Expect.equal (bodyText (run (api.GetPage "insights/latest"))) "insights/latest" "the literal"
            Expect.equal (bodyText (run (api.GetPage "insights/2026-11-06"))) "insights/{date}2026-11-06" "the capture"

        testCase "a typed capture that does not hold falls through to forge's API, and so to a 404"
        <| fun _ ->
            let typed = {
                page "insights/{date}" PageAudience.Public with
                    Captures = [ "date", DeclaredPages.Capture.date ]
            }

            let api = over [ typed ]

            Expect.equal (bodyText (run (api.GetPage "insights/2026-11-06"))) "insights/{date}2026-11-06" "it holds"

            let reader = AccessContexts.forSubject (AnonymousSession "session")

            Expect.equal
                (bodyText (run (api.GetPage "insights/2026")))
                "from the file"
                "does not hold, the inner API answers"

            Expect.isNone (run (api.GetPage "insights/2026-02-30")) "a date that does not exist is no page"
            Expect.isNone (run (api.GetPageInContext("insights/2026-02-30", reader))) "in context as well"

        testCase "the capture predicates"
        <| fun _ ->
            let date = DeclaredPages.Capture.date
            Expect.isTrue (date "2024-02-29") "a leap day"
            Expect.isFalse (date "2023-02-29") "no leap day"
            Expect.isFalse (date "2023-13-01") "no month 13"
            Expect.isFalse (date "20230101xx") "not the shape"

            let slug = DeclaredPages.Capture.slug
            Expect.isTrue (slug "a-1") "joined by a hyphen"

            for bad in [ ""; "-a"; "a-"; "a--b"; "A" ] do
                Expect.isFalse (slug bad) bad

            let integer = DeclaredPages.Capture.integer
            Expect.isTrue (integer "-12") "signed"

            for bad in [ ""; "-"; "+1"; " 1"; "99999999999" ] do
                Expect.isFalse (integer bad) bad

        testCase "enumeration lists literal routes and never a captured one"
        <| fun _ ->
            let api =
                over [
                    page "insights/{date}" PageAudience.Public
                    page "reports/latest" PageAudience.Public
                ]

            Expect.equal
                (slugs (run (api.ListPages "")))
                [ "about"; "insights/2026"; "reports/latest" ]
                "the file pages and the literal, sorted"

            Expect.equal (slugs (run (api.ListPages "reports"))) [ "reports/latest" ] "under a prefix"

        testCase "a declared route shadows the page it answers for, in the list as well"
        <| fun _ ->
            let api = over [ page "about" PageAudience.Public ]
            Expect.equal (slugs (run (api.ListPages "about"))) [ "about" ] "listed once"
            Expect.equal (bodyText (run (api.GetPage "about"))) "about" "the declared body"

        testCase "the gated enumeration omits a gated page that the ungated one lists"
        <| fun _ ->
            let api =
                over [ page "reports/open" PageAudience.Public; page "reports/closed" publication ]

            Expect.equal
                (slugs (run (api.ListPages "reports")))
                [ "reports/closed"; "reports/open" ]
                "the ungated enumeration has both"

            Expect.equal
                (slugs (run (api.ListPagesPublic(DateTimeOffset.UtcNow, "reports"))))
                [ "reports/open" ]
                "only the public one reaches a public surface"

        testCase "a page serves its narrative when one reads, and its own body when none does"
        <| fun _ ->
            let document = Narrative.create "Latest"

            let served = {
                page "insights/latest" PageAudience.Public with
                    Narrative =
                        Some {
                            Run = "daily"
                            Body = fun _ document -> Narrative document
                            PublishingPrincipal = None
                        }
            }

            let reader = AccessContexts.forSubject (AnonymousSession "session")

            let reading =
                DeclaredPages.over
                    (fun narrative _ _ _ ->
                        Expect.equal narrative.Run "daily" "the page's run"
                        async.Return(Some document))
                    [ served ]
                    (inner [])

            match run (reading.GetPageInContext("insights/latest", reader)) with
            | Some { Body = Narrative _ } -> ()
            | other -> failtestf "%A" other

            let quiet = DeclaredPages.over DeclaredPages.noNarrative [ served ] (inner [])

            Expect.equal
                (bodyText (run (quiet.GetPageInContext("insights/latest", reader))))
                "insights/latest"
                "nothing to serve: its own body"

        testCaseAsync "the composed content API answers a declared page, with its narrative, over the composed store"
        <| async {
            let store = InMemoryNarrativeStore() :> INarrativeStore
            let! _ = store.PublishTagged("team-1", "m", None, Narrative.create "Daily", [ "grounded-run:daily" ])

            let served = {
                page "insights/latest" PageAudience.Public with
                    Narrative =
                        Some {
                            Run = "daily"
                            Body = fun _ document -> Narrative document
                            PublishingPrincipal = None
                        }
            }

            let app =
                composed (
                    PublicRenderingOptions.withLayout (LayoutName "report") anyLayout
                    >> PublicRenderingCompose.withDeclaredPages [ served ]
                )

            let services = ServiceCollection()

            services.AddSingleton<ILogger>(ConsoleLogger.ConsoleLogger() :> ILogger)
            |> ignore

            (ServerApp.serviceConfig app).Value services |> ignore
            services.AddSingleton<INarrativeStore>(store) |> ignore
            use provider = services.BuildServiceProvider()
            let api = provider.GetRequiredService<IPublicContentApi>()

            let! listed = api.ListPagesPublic(DateTimeOffset.UtcNow, "")
            Expect.equal (slugs listed) [ "insights/latest" ] "the literal route is listed through the composed API"

            let member' = AccessContexts.forSubject (TeamMember("u1", "team-1"))

            match! api.GetPageInContext("insights/latest", member') with
            | Some { Body = Narrative document } -> Expect.equal document.Title "Daily" "the run's latest narrative"
            | other -> failtestf "%A" other

            let stranger = AccessContexts.forSubject (TeamMember("u2", "team-2"))

            match! api.GetPageInContext("insights/latest", stranger) with
            | Some { Body = Html _ } -> ()
            | other -> failtestf "another team's scope has no narrative: %A" other
        }

        testCaseAsync "a publication's narrative is read in the publishing scope, and only for its readers"
        <| async {
            let store = InMemoryNarrativeStore() :> INarrativeStore
            let! _ = store.PublishTagged("team-1", "m", None, Narrative.create "Board", [ "grounded-run:daily" ])
            let services = ServiceCollection()
            services.AddSingleton<INarrativeStore>(store) |> ignore
            use provider = services.BuildServiceProvider()

            let read =
                DeclaredPages.narrativeReadIn
                    ServerConfig.defaults
                    (ConsoleLogger.ConsoleLogger() :> ILogger)
                    None
                    (Some(store :> INarrativeStore))
                    provider

            let served = {
                Run = "daily"
                Body = fun _ document -> Narrative document
                PublishingPrincipal = None
            }

            let reader = Subject.PublicationReader "rhea"

            let admitted = {
                AccessContexts.forSubject reader with
                    TokenRoles = AccessContext.tokenRolesFor reader [ "readers" ]
                    Admission = AccessContext.admissionFor reader PrincipalAdmission.PublicationReader
            }

            let! document = read served publication "reports/latest" admitted
            Expect.equal (document |> Option.map _.Title) (Some "Board") "the publishing scope's narrative"

            let refused = AccessContexts.forSubject (TeamMember("u2", "team-2"))
            let! none = read served publication "reports/latest" refused
            Expect.isNone none "a principal the audience does not admit reads nothing"

            let gated =
                PageAudience.Publication {
                    Readers = [ "readers" ]
                    PublishingScope = "team-9"
                }

            let! elsewhere = read served gated "reports/latest" admitted
            Expect.isNone elsewhere "another publishing scope has published nothing"
        }

        testCaseAsync "a static export writes a declared public page and no gated or captured one"
        <| async {
            let root =
                Path.Combine(Path.GetTempPath(), "toolup-declared-" + Guid.NewGuid().ToString "N")

            let output =
                Path.Combine(Path.GetTempPath(), "toolup-declared-" + Guid.NewGuid().ToString "N")

            Directory.CreateDirectory root |> ignore

            try
                let app =
                    ServerApp.empty
                    |> ServerApp.withConfig {
                        ServerConfig.defaults with
                            PublicRendering = EnabledPublicRendering(ContentRoot root)
                    }

                let options =
                    PublicRenderingOptions.create ()
                    |> PublicRenderingOptions.withLayout (LayoutName "report") anyLayout
                    |> PublicRenderingCompose.withDeclaredPages [
                        page "open" PageAudience.Public
                        page "closed" publication
                        page "dated/{date}" PageAudience.Public
                    ]

                let! count = PublicRenderingCompose.PublicRenderingExport.exportStatic output (app, options)
                Expect.equal count 1 "the one public literal"
                Expect.isTrue (File.Exists(Path.Combine(output, "open", "index.html"))) "written"
                Expect.isFalse (Directory.Exists(Path.Combine(output, "closed"))) "a gated page is not"
            finally
                Directory.Delete(root, true)

                if Directory.Exists output then
                    Directory.Delete(output, true)
        }

        testCase "no declared pages leaves the API as it is"
        <| fun _ ->
            let api = inner []
            Expect.isTrue (obj.ReferenceEquals(api, DeclaredPages.over DeclaredPages.noNarrative [] api)) "the same API"

        testCase "compose refuses a page whose layout nobody registered, naming the route and the layout"
        <| fun _ ->
            let raised =
                Expect.throwsC
                    (fun () ->
                        composed (
                            PublicRenderingOptions.withLayout (LayoutName "other") anyLayout
                            >> PublicRenderingCompose.withDeclaredPages [ page "insights/latest" PageAudience.Public ]
                        )
                        |> ignore)
                    (fun ex ->
                        Expect.isTrue (ex :? LayoutRefusedException) "the layout refusal"
                        ex.Message)

            Expect.stringContains raised "insights/latest" "the route"
            Expect.stringContains raised "report" "the layout"

        testCase "a registered layout composes, and withDeclaredPages adds to what is declared"
        <| fun _ ->
            let options =
                PublicRenderingOptions.create ()
                |> PublicRenderingCompose.withDeclaredPages [ page "a" PageAudience.Public ]
                |> PublicRenderingCompose.withDeclaredPages [ page "b" PageAudience.Public ]

            Expect.equal (options.DeclaredPages |> List.map _.Route) [ "a"; "b" ] "both, in order"

            let app =
                composed (
                    PublicRenderingOptions.withLayout (LayoutName "report") anyLayout
                    >> PublicRenderingCompose.withDeclaredPages [ page "a" PageAudience.Public ]
                )

            Expect.contains app.ComposedCompanions "ToolUp.PublicRendering" "composed"

        testCase "a repeated route and a predicate on a capture the route lacks are refused"
        <| fun _ ->
            let typo = {
                page "insights/{date}" PageAudience.Public with
                    Captures = [ "dat", DeclaredPages.Capture.date ]
            }

            let refusals =
                DeclaredPages.shapeRefusals [ page "a" PageAudience.Public; page "a" PageAudience.Public; typo ]

            Expect.equal refusals.Length 2 "both"
            Expect.stringContains refusals[0] "'a'" "the repeat"
            Expect.stringContains refusals[1] "'dat'" "the capture"

            Expect.throwsT<InvalidOperationException>
                (fun () ->
                    composed (
                        PublicRenderingOptions.withLayout (LayoutName "report") anyLayout
                        >> PublicRenderingCompose.withDeclaredPages [ typo ]
                    )
                    |> ignore)
                "compose refuses it"
    ]