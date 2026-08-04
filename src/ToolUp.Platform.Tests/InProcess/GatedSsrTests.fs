module ToolUp.Platform.Tests.InProcess.GatedSsrTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.PublicRendering

// ─── Phase 86 — gated / tenant-scoped / audience-targeted SSR ────────
//
// Four layers:
//   1. PageAudience.parse (frontmatter → audience).
//   2. AudienceGate.evaluate matrix (the security-critical pure decision)
//      — anon / authenticated / role-gated / client-gated × allowed /
//      denied, plus the platform-admin bypass.
//   3. PublicPageHandler authorization pre-check over a DefaultHttpContext
//      (401 anon / 403 wrong-role / 200 right-role / 200 public).
//   4. Gated exclusion from the sitemap + cross-tenant isolation through
//      PublicContentApiImpl's scope-keyed overlay (GP 4).

// ─── AccessContext fixtures ─────────────────────────────────────────

let private anon = AccessContext.unrestricted (AnonymousSession "s1")
let private user = AccessContext.unrestricted (AuthenticatedUser "u1")
let private teamA = AccessContext.unrestricted (TeamMember("u1", "team-a"))

/// An authenticated user whose RBAC is configured (non-empty) but lacks
/// the gating role — the "viewer without the role → 403" case.
let private userNoEditor = {
    user with
        ModulePermissions = Map["viewer", [ ModulePermission.Read ]]
}

/// An authenticated user who holds the gating role.
let private userEditor = {
    user with
        ModulePermissions = Map["editor", [ ModulePermission.Write ]]
}

let private platformAdmin = {
    user with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

// ─── 1. PageAudience.parse ──────────────────────────────────────────

let private parseTests =
    testList "PageAudience.parse" [
        testCase "absent / empty / public → Public"
        <| fun _ ->
            Expect.equal (PageAudience.parse None) PageAudience.Public "absent"
            Expect.equal (PageAudience.parse (Some "")) PageAudience.Public "empty"
            Expect.equal (PageAudience.parse (Some "public")) PageAudience.Public "public"

        testCase "authenticated → Authenticated"
        <| fun _ -> Expect.equal (PageAudience.parse (Some "authenticated")) PageAudience.Authenticated "authenticated"

        testCase "scope:editor,admin → ScopeGated [editor; admin]"
        <| fun _ ->
            Expect.equal
                (PageAudience.parse (Some "scope:editor, admin"))
                (PageAudience.ScopeGated [ "editor"; "admin" ])
                "comma list trimmed"

        testCase "client:acme → ClientGated acme"
        <| fun _ ->
            Expect.equal
                (PageAudience.parse (Some "client:acme"))
                (PageAudience.ClientGated "acme")
                "client relationship"

        testCase "unrecognised value → Public (fail-open, GP 11)"
        <| fun _ -> Expect.equal (PageAudience.parse (Some "garblexyz")) PageAudience.Public "typo degrades to Public"
    ]

// ─── 2. AudienceGate.evaluate matrix ────────────────────────────────

let private gateTests =
    testList "AudienceGate.evaluate" [
        testCase "Public → Allow for anyone (incl. anonymous)"
        <| fun _ ->
            Expect.equal (AudienceGate.evaluate anon PageAudience.Public) AudienceDecision.Allow "anon public"
            Expect.equal (AudienceGate.evaluate user PageAudience.Public) AudienceDecision.Allow "user public"

        testCase "Authenticated → 401 for anon, Allow for a principal"
        <| fun _ ->
            Expect.equal
                (AudienceGate.evaluate anon PageAudience.Authenticated)
                AudienceDecision.RequireAuthentication
                "anon → require auth"

            Expect.equal (AudienceGate.evaluate user PageAudience.Authenticated) AudienceDecision.Allow "user → allow"

        testCase "ScopeGated → 401 anon, 403 wrong-role, Allow right-role"
        <| fun _ ->
            let gated = PageAudience.ScopeGated [ "editor" ]
            Expect.equal (AudienceGate.evaluate anon gated) AudienceDecision.RequireAuthentication "anon → 401"

            Expect.equal
                (AudienceGate.evaluate userNoEditor gated)
                AudienceDecision.Forbidden
                "configured-but-no-role → 403"

            Expect.equal (AudienceGate.evaluate userEditor gated) AudienceDecision.Allow "holds role → allow"

        testCase "ScopeGated — unrestricted (empty permissions) principal passes (GP 11)"
        <| fun _ ->
            // A principal with NO configured RBAC is unrestricted, matching
            // the pre-RBAC module-route gating surface.
            Expect.equal
                (AudienceGate.evaluate user (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Allow
                "empty ModulePermissions = unrestricted"

        testCase "ScopeGated [] gates to any-authenticated"
        <| fun _ ->
            Expect.equal
                (AudienceGate.evaluate anon (PageAudience.ScopeGated []))
                AudienceDecision.RequireAuthentication
                "anon → 401"

            Expect.equal
                (AudienceGate.evaluate userNoEditor (PageAudience.ScopeGated []))
                AudienceDecision.Allow
                "any authenticated principal allowed when no roles named"

        testCase "platform admin bypasses ScopeGated and ClientGated"
        <| fun _ ->
            Expect.equal
                (AudienceGate.evaluate platformAdmin (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Allow
                "admin bypass on ScopeGated"

            Expect.equal
                (AudienceGate.evaluate platformAdmin (PageAudience.ClientGated "acme"))
                AudienceDecision.Allow
                "admin bypass on ClientGated"

        testCase "ClientGated → Allow only when relationship matches a principal scope (GP 4)"
        <| fun _ ->
            // teamA's scopes include "team-a" (team id). A page for client
            // "team-a" is visible to it; "other-client" is not.
            Expect.equal
                (AudienceGate.evaluate teamA (PageAudience.ClientGated "team-a"))
                AudienceDecision.Allow
                "own scope → allow"

            Expect.equal
                (AudienceGate.evaluate teamA (PageAudience.ClientGated "other-client"))
                AudienceDecision.Forbidden
                "another client's page → 403"

            Expect.equal
                (AudienceGate.evaluate anon (PageAudience.ClientGated "team-a"))
                AudienceDecision.RequireAuthentication
                "anon → 401"
    ]

// ─── 3. Handler authorization pre-check ─────────────────────────────

let private layouts: Map<LayoutName, PublicPage -> XmlNode> =
    Map[(LayoutName "page", (fun (p: PublicPage) -> html [] [ body [] [ str p.Title ] ]))]

let private mkPage (slug: string) (audience: PageAudience) : PublicPage = {
    Slug = Slug slug
    Title = $"Title-{slug}"
    Description = ""
    Body = Html $"body-{slug}"
    Layout = LayoutName "page"
    Frontmatter = Map.empty
    PublishedAt = None
    Collection = None
    Status = Published
    Audience = audience
}

let private mkApi (pages: Map<string, PublicPage>) : IPublicContentApi =
    { new IPublicContentApi with
        member _.GetPage slug = async { return Map.tryFind slug pages }
        member _.ListPages _ = async { return pages |> Map.toList |> List.map snd }

        member this.ListPagesPublic(now, prefix) =
            PublicContentApi.defaultListPagesPublic this now prefix

        member _.GetCollection _ = async { return [] }
        member _.GetPageInContext(slug, _ctx) = async { return Map.tryFind slug pages }
    }

/// Run the page handler over a DefaultHttpContext with `ctx` injected as
/// the resolved AccessContext, returning the status code. No render cache
/// is composed (the audience gate is independent of Phase 84).
let private runStatus (accessContext: AccessContext) (page: PublicPage) : int =
    let api = mkApi (Map[Slug.value page.Slug, page])
    let services = ServiceCollection()
    services.AddSingleton<AccessContext>(accessContext) |> ignore
    let provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- provider
    ctx.Request.Path <- PathString("/" + Slug.value page.Slug)
    ctx.Response.Body <- new MemoryStream()
    let finalFunc: HttpFunc = fun c -> Task.FromResult(Some c)

    (PublicPageHandler.handler api layouts finalFunc ctx).GetAwaiter().GetResult()
    |> ignore

    ctx.Response.StatusCode

let private handlerTests =
    testList "PublicPageHandler — Phase 86 authorization pre-check" [
        testCase "Public page → 200 anonymously"
        <| fun _ -> Expect.equal (runStatus anon (mkPage "p" PageAudience.Public)) 200 "public served to anon"

        testCase "Authenticated page → 401 anon, 200 signed-in"
        <| fun _ ->
            Expect.equal (runStatus anon (mkPage "a" PageAudience.Authenticated)) 401 "anon → 401"
            Expect.equal (runStatus user (mkPage "a" PageAudience.Authenticated)) 200 "user → 200"

        testCase "ScopeGated [editor] → 403 without role, 200 with role"
        <| fun _ ->
            let page = mkPage "g" (PageAudience.ScopeGated [ "editor" ])
            Expect.equal (runStatus userNoEditor page) 403 "no role → 403"
            Expect.equal (runStatus userEditor page) 200 "with role → 200"

        testCase "ClientGated → 403 for a non-matching principal, 200 for the owner"
        <| fun _ ->
            Expect.equal (runStatus teamA (mkPage "c" (PageAudience.ClientGated "team-a"))) 200 "owner → 200"
            Expect.equal (runStatus teamA (mkPage "c" (PageAudience.ClientGated "rival"))) 403 "non-owner → 403"
    ]

// ─── 4. Gated exclusion + cross-tenant isolation ────────────────────

let private exclusionTests =
    testList "Phase 86 — gated exclusion + tenant isolation" [
        testCase "SitemapGenerator excludes non-Public pages"
        <| fun _ ->
            let pub = mkPage "open" PageAudience.Public
            let gated = mkPage "secret" PageAudience.Authenticated
            let xml = SitemapGenerator.generate "https://example.com" [ pub; gated ]
            Expect.stringContains xml "https://example.com/open" "public slug present"
            Expect.isFalse (xml.Contains "secret") "gated slug never leaks to the sitemap"

        testCaseAsync "cross-tenant isolation — team A's scoped page is invisible to team B (GP 4)"
        <| async {
            // A real blob-backed entity store; save a PublicPageEntity under
            // team A's scope and confirm only a team-A principal resolves it.
            let tempDir =
                Path.Combine(Path.GetTempPath(), "toolup-gated-ssr-" + Guid.NewGuid().ToString("N"))

            Directory.CreateDirectory tempDir |> ignore
            let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
            let dos = DataObjectStore(blob) :> IDataObjectStore
            let registry = EntityRegistry()
            registry.Register<PublicPageEntity>(PublicPageEntity.registration)
            let store = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

            let teamScopedPage = {
                mkPage "dashboard" (PageAudience.ScopeGated []) with
                    Title = "Team A Dashboard"
            }

            let! _ = store.Save<PublicPageEntity>("team-a", PublicPageEntity.fromPage teamScopedPage)

            // Empty markdown root → no file pages; the only resolution path
            // is the scope-keyed overlay tier.
            let logger = ConsoleLogger.ConsoleLogger() :> ILogger

            let loader =
                new MarkdownContentLoader(ContentRoot tempDir, logger, hotReload = false)

            let api = PublicContentApiImpl.create loader (Some store) []

            let ctxA = AccessContext.unrestricted (TeamMember("u1", "team-a"))
            let ctxB = AccessContext.unrestricted (TeamMember("u2", "team-b"))

            let! seenByA = api.GetPageInContext("dashboard", ctxA)
            let! seenByB = api.GetPageInContext("dashboard", ctxB)
            let! seenByAnon = api.GetPageInContext("dashboard", AccessContext.unrestricted (AnonymousSession "x"))

            Expect.isSome seenByA "team A resolves its own scoped page"
            Expect.equal (seenByA |> Option.map _.Title) (Some "Team A Dashboard") "correct page body"
            Expect.isNone seenByB "team B cannot read team A's scoped page"
            Expect.isNone seenByAnon "anonymous never reaches the tenant-scoped tier"
        }
    ]

let tests =
    testList "GatedSsr (Phase 86)" [ parseTests; gateTests; handlerTests; exclusionTests ]