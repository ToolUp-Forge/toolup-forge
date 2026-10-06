module ToolUp.Platform.Tests.InProcess.GatedSsrTests

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.DataProtection
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.NotificationChannel
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
//   5. Phase 989 — the audience through the composed request pipeline:
//      real credentials, the real `ScopeResolutionMiddleware`, the real
//      `AccessContext` factory and the real page handler, on an
//      authenticated, an anonymous and a mixed deployment. Layers 2 and 3
//      inject a resolved `AccessContext`, so they cannot see whether the
//      pipeline resolves one for a page route; this layer can.

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

        testCase "ScopeGated — a principal with no configured permissions holds no role (Phase 989)"
        <| fun _ ->
            // Empty ModulePermissions means "unrestricted" for MODULE access
            // (GP 11), and still does: canAccessModule is unchanged. A page
            // that names its roles is restricted by its author, and a
            // principal granted no permissions holds none of them.
            Expect.equal
                (AudienceGate.evaluate user (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Forbidden
                "empty ModulePermissions holds no role"

            Expect.isTrue (AccessContext.canAccessModule "editor" user) "module access keeps the GP 11 reading"

        testCase "ScopeGated — a role entry with no permissions is not held"
        <| fun _ ->
            let revoked = {
                user with
                    ModulePermissions = Map["editor", []]
            }

            Expect.equal
                (AudienceGate.evaluate revoked (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Forbidden
                "a revoked role entry grants nothing"

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

        testCase "ClientGated — an individual user matches only its own scope"
        <| fun _ ->
            Expect.equal (AudienceGate.evaluate user (PageAudience.ClientGated "u1")) AudienceDecision.Allow "own"

            Expect.equal
                (AudienceGate.evaluate user (PageAudience.ClientGated "u2"))
                AudienceDecision.Forbidden
                "another user's relationship → 403"
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

            let! _ =
                store.Save<PublicPageEntity>(
                    "team-a",
                    EntityTypes.EntityPrincipal.ofPrincipal "tester",
                    PublicPageEntity.fromPage teamScopedPage
                )

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


// ─── 5. Phase 989 — the audience through the composed pipeline ──────

let private jwtSecret = "gated-ssr-pipeline-test-secret-0123456789abcdef"

let private base64Url (bytes: byte[]) =
    Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

/// An HS256 token for `sub`, valid for an hour — what a real issuer hands
/// the client. Verified by the real `StaticJwtAuthProvider`.
let private mintToken (sub: string) =
    let header = base64Url (Encoding.UTF8.GetBytes """{"alg":"HS256","typ":"JWT"}""")

    let payload =
        dict [
            "sub", box sub
            "exp", box (DateTimeOffset.UtcNow.AddHours(1.0).ToUnixTimeSeconds())
        ]
        |> JsonSerializer.Serialize
        |> Encoding.UTF8.GetBytes
        |> base64Url

    use hmac = new HMACSHA256(Encoding.UTF8.GetBytes jwtSecret)
    let signature = hmac.ComputeHash(Encoding.UTF8.GetBytes $"{header}.{payload}")
    $"{header}.{payload}.{base64Url signature}"

/// The session cookie the cookie arm of the test provider reads.
let private sessionCookie = "gated-ssr-session"

/// Server-side session table for the cookie arm: an opaque id maps to a
/// user, as a session-cookie provider's store does.
let private sessions = Map["sess-alice", "alice"]

/// Bearer credentials go to the real `StaticJwtAuthProvider`; a session
/// cookie is looked up in `sessions`. So the pipeline is exercised with
/// both kinds of credential a provider reads.
let private authProvider () : IAuthProvider =
    let jwt =
        StaticJwtAuthProvider.StaticJwtAuthProvider(
            {
                Secret = jwtSecret
                Issuer = None
                Audience = None
            }
        )
        :> IAuthProvider

    let fromCookie (rc: RequestContext) =
        let http = RequestContext.value rc :?> HttpContext

        match http.Request.Cookies.TryGetValue sessionCookie with
        | true, id -> sessions |> Map.tryFind id
        | _ -> None

    let userOf userId = {
        AuthenticatedUser.anonymous with
            UserId = userId
            DisplayName = userId
    }

    { new IAuthProvider with
        member _.GetUser rc =
            match fromCookie rc with
            | Some userId -> async { return userOf userId }
            | None -> jwt.GetUser rc

        member _.ValidateRequest rc =
            match fromCookie rc with
            | Some userId -> async { return Ok(userOf userId) }
            | None -> jwt.ValidateRequest rc

        member _.IsCryptographicallyVerified = true
    }

let private silentLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The pages of the probe: one per audience.
let private pipelinePages =
    [
        mkPage "open" PageAudience.Public
        mkPage "members" PageAudience.Authenticated
        mkPage "editors" (PageAudience.ScopeGated [ "editor" ])
        mkPage "client-alice" (PageAudience.ClientGated "alice")
        mkPage "client-team-a" (PageAudience.ClientGated "team-a")
    ]
    |> List.map (fun p -> Slug.value p.Slug, p)
    |> Map.ofList

/// Build a host whose request pipeline is the SDK's, in the SDK's order:
/// `ScopeResolutionMiddleware` → `SurfaceEnforcementMiddleware` → routes,
/// with the SDK's own scope-resolution registrations (the `ISubjectResolver`
/// and the scoped `AccessContext` factory). The routes are an `/api/x`
/// control and the public-page catch-all.
///
/// Team data: `team-a` has members `carol` (holds `editor`), `dave` (holds
/// only `viewer`) and `erin` (no permissions configured at all). `alice`
/// and `bob` belong to no team.
let private startHostWith
    (surfaces: SurfaceProfile list)
    (auth: IAuthProvider)
    (configure: IServiceCollection -> unit)
    (extraRoutes: HttpHandler list)
    : Async<IHost> =
    async {
        let config = {
            ServerConfig.defaults with
                Surfaces = surfaces
        }

        let hasTeams = DeploymentConfig.hasTeamScope config
        let blob = ToolUp.Platform.Testing.Fakes.standardBlobStorage ()
        let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
        let teamStore = TeamManagement.TeamStore(blob, notifications, silentLogger)

        let permissionStore =
            PermissionStore.PermissionStore(blob) :> PermissionStore.IPermissionStore

        if hasTeams then
            let teams = teamStore :> TeamManagement.ITeamStore
            let! _ = teams.CreateTeam("team-a", "Team A")

            for userId in [ "carol"; "dave"; "erin" ] do
                let! _ = teams.AddMember("team-a", userId, TeamRole.Member)
                let! _ = teams.SetActiveTeam(userId, "team-a")
                ()

            let! _ = permissionStore.SetMemberPermissions("team-a", "carol", "editor", [ ModulePermission.Read ])
            let! _ = permissionStore.SetMemberPermissions("team-a", "dave", "viewer", [ ModulePermission.Read ])
            ()

        let api = mkApi pipelinePages

        let host =
            Host
                .CreateDefaultBuilder()
                .ConfigureWebHostDefaults(fun webHost ->
                    webHost
                        .UseTestServer()
                        .ConfigureServices(fun services ->
                            services.AddGiraffe() |> ignore
                            services.AddDataProtection() |> ignore
                            services.AddSingleton<ILogger>(silentLogger) |> ignore
                            services.AddSingleton<IAuthProvider>(auth) |> ignore
                            configure services

                            services.AddSingleton<PermissionStore.IPermissionStore>(permissionStore)
                            |> ignore

                            ComposeScopeResolver.registerScopeResolution
                                services
                                config
                                (if hasTeams then Some teamStore else None)
                                notifications
                                silentLogger
                                []
                                []
                                []
                                None)
                        .Configure(fun (app: IApplicationBuilder) ->
                            app.UseMiddleware<Middleware.ScopeResolutionMiddleware>(config) |> ignore
                            app.UseMiddleware<SurfaceEnforcement.SurfaceEnforcementMiddleware>() |> ignore

                            app.UseGiraffe(
                                choose [
                                    yield! extraRoutes
                                    route "/api/x" >=> text "api"
                                    PublicPageHandler.handler api layouts
                                ]
                            ))
                    |> ignore)
                .Build()

        do! host.StartAsync() |> Async.AwaitTask
        return host
    }

let private startHost (surfaces: SurfaceProfile list) : Async<IHost> =
    startHostWith surfaces (authProvider ()) ignore []

/// The credential a request carries.
type private Credential =
    | NoCredential
    | GarbageBearer
    | Bearer of userId: string
    | SessionCookie of sessionId: string

let private send (client: HttpClient) (credential: Credential) (path: string) = async {
    use request = new HttpRequestMessage(HttpMethod.Get, path)

    match credential with
    | NoCredential -> ()
    | GarbageBearer -> request.Headers.Add("Authorization", "Bearer not.a.token")
    | Bearer userId -> request.Headers.Add("Authorization", "Bearer " + mintToken userId)
    | SessionCookie id -> request.Headers.Add("Cookie", $"{sessionCookie}={id}")

    use! response = client.SendAsync request |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
    return int response.StatusCode, body
}

/// Run every `(credential, path, expected status)` cell against one host
/// and fail with the whole list of mismatches, so a red run shows the
/// table rather than its first wrong cell. A refused page must not carry
/// the page body.
let private expectCells (surfaces: SurfaceProfile list) (cells: (Credential * string * int) list) = async {
    use! host = startHost surfaces
    use client = host.GetTestClient()

    let! results =
        cells
        |> List.map (fun (credential, path, expected) -> async {
            let! status, body = send client credential path
            return credential, path, expected, status, body
        })
        |> Async.Sequential

    do! host.StopAsync() |> Async.AwaitTask

    let mismatches =
        results
        |> Array.choose (fun (credential, path, expected, status, _) ->
            if status <> expected then
                Some $"%A{credential} GET {path}: expected {expected}, got {status}"
            else
                None)

    Expect.isEmpty
        mismatches
        ("every cell returns the status its audience implies — mismatched: "
         + String.concat "; " mismatches)

    for credential, path, _, status, body in results do
        if status = 401 || status = 403 then
            // Phase 987 — the layout renders the page's TITLE (`Title-<slug>`),
            // not its body, so the marker a refused response must not carry is
            // the title: a `body-` check could never fail.
            Expect.isFalse (body.Contains "Title-") $"%A{credential} GET {path}: a refused page carries no page content"
}

/// The authenticated deployment of the probe, plus a team surface so a
/// principal can actually hold a role.
let private authenticatedSurface = [ SurfaceProfile.individual; SurfaceProfile.team ]

let private pipelineTests =
    testList "Phase 989 — gated pages through the composed pipeline" [
        testCaseAsync "authenticated deployment — no credentials and a garbage token are refused"
        <| expectCells authenticatedSurface [
            for credential in [ NoCredential; GarbageBearer ] do
                credential, "/open", 200
                credential, "/members", 401
                credential, "/editors", 401
                credential, "/client-alice", 401
                credential, "/client-team-a", 401
                credential, "/api/x", 401
        ]

        testCaseAsync "authenticated deployment — a valid token is read on page routes"
        <| expectCells authenticatedSurface [
            // alice: signed in, no team, no role.
            Bearer "alice", "/open", 200
            Bearer "alice", "/members", 200
            Bearer "alice", "/editors", 403
            Bearer "alice", "/client-alice", 200
            Bearer "alice", "/client-team-a", 403
            // bob: another individual — alice's client page is not his.
            Bearer "bob", "/client-alice", 403
            // carol: team-a member holding `editor`.
            Bearer "carol", "/members", 200
            Bearer "carol", "/editors", 200
            Bearer "carol", "/client-team-a", 200
            Bearer "carol", "/client-alice", 403
            // dave: configured permissions, but not `editor`.
            Bearer "dave", "/editors", 403
            // erin: no permissions configured at all — holds no role.
            Bearer "erin", "/members", 200
            Bearer "erin", "/editors", 403
        ]

        testCaseAsync "authenticated deployment — a session cookie is read on page routes"
        <| expectCells authenticatedSurface [
            SessionCookie "sess-alice", "/members", 200
            SessionCookie "sess-alice", "/client-alice", 200
            SessionCookie "sess-alice", "/editors", 403
            SessionCookie "sess-unknown", "/members", 401
        ]

        testCaseAsync "anonymous deployment — gated pages are refused, public pages served"
        <| expectCells Surfaces.anonymous [
            // The deployment admits no authenticated subject, so even a
            // valid credential resolves to an anonymous session: every
            // gated page is 401, for every credential.
            for credential in [ NoCredential; GarbageBearer; Bearer "alice"; SessionCookie "sess-alice" ] do
                credential, "/open", 200
                credential, "/members", 401
                credential, "/editors", 401
                credential, "/client-alice", 401
        ]

        testCaseAsync "mixed anonymous + individual deployment — the principal decides"
        <| expectCells [ SurfaceProfile.anonymous; SurfaceProfile.individual ] [
            for credential in [ NoCredential; GarbageBearer ] do
                credential, "/open", 200
                credential, "/members", 401
                credential, "/editors", 401
                credential, "/client-alice", 401

            Bearer "alice", "/members", 200
            Bearer "alice", "/editors", 403
            Bearer "alice", "/client-alice", 200
            Bearer "bob", "/client-alice", 403
        ]

        testCaseAsync "a public page served to an anonymous visitor sets no cookie"
        <| async {
            use! host = startHost [ SurfaceProfile.anonymous; SurfaceProfile.individual ]
            use client = host.GetTestClient()
            use! page = client.GetAsync "/open" |> Async.AwaitTask
            Expect.equal (int page.StatusCode) 200 "served"

            Expect.isFalse
                (page.Headers.Contains "Set-Cookie")
                "a page route issues no anonymous session cookie, so the page stays cacheable"

            // The falsifier: the same host DOES bind an anonymous API caller,
            // so the assertion above is about the page route, not about a
            // host that can never issue the cookie.
            use! api = client.GetAsync "/api/x" |> Async.AwaitTask
            Expect.isTrue (api.Headers.Contains "Set-Cookie") "an anonymous API caller is bound to its session"

            do! host.StopAsync() |> Async.AwaitTask
        }
    ]

// ─── 6. Phase 987 — readers governed by the identity provider ───────
//
// The directory half of gated SSR: the OIDC provider maps the token's
// role and group claims onto `AuthenticatedUser.DirectoryRoles` (`ClaimMapping`;
// Phase 993 — page audiences only, never API roles without an allow-list),
// the `AccessContext` factory carries them as `TokenRoles`, and
// `ScopeGated` reads them beside module permissions. Layer 6a drives the
// shipped claim mapping and admission gate directly; 6b drives real RS256
// tokens from the mock issuer through the real middleware, the real
// factory and the real page handler — no injected `AccessContext`; 6c
// drives the interactive sign-in end to end.

/// An UNSIGNED token around `payload` — enough for the post-validation
/// projection, which by contract reads already-trusted bytes and performs
/// no validation of its own.
let private rawToken (payload: string) =
    let enc (s: string) = base64Url (Encoding.UTF8.GetBytes s)
    let header = enc """{"alg":"RS256","kid":"k"}"""
    header + "." + enc payload + ".sig"

let private mapWith (mapping: ClaimMapping) (payload: string) =
    let baseUser = {
        AuthenticatedUser.anonymous with
            UserId = "u"
            DisplayName = "u"
    }

    ToolUp.AuthProviders.OidcAuthProvider.applyValidatedClaimMapping mapping (rawToken payload) baseUser

let private rolesOf mapping payload =
    match mapWith mapping payload with
    | Ok u -> Ok u.DirectoryRoles
    | Error(claim, _) -> Error claim

let private claimMappingTests =
    let groupsAliased = {
        ClaimMapping.directoryRoles with
            GroupAliases = Map["0b3e-finance", "finance"]
    }

    testList "Phase 987 — role and group claims (ClaimMapping)" [
        testCase "roles and groups become directory roles; a group alias renames, an unaliased id is kept"
        <| fun _ ->
            Expect.equal
                (rolesOf groupsAliased """{"roles":["editor","reader"],"groups":["0b3e-finance","77aa"]}""")
                (Ok [ "editor"; "reader"; "finance"; "77aa" ])
                "both claims, aliases applied"

        testCase "a single-string claim is one role; an absent claim is none"
        <| fun _ ->
            Expect.equal (rolesOf ClaimMapping.directoryRoles """{"roles":"editor"}""") (Ok [ "editor" ]) "string"
            Expect.equal (rolesOf ClaimMapping.directoryRoles """{"sub":"u"}""") (Ok []) "absent"

        testCase "a mapping that names no role claim maps none, whatever the token carries"
        <| fun _ ->
            let idOnly = {
                ClaimMapping.none with
                    TenantIdClaim = Some "tid"
            }

            Expect.equal (rolesOf idOnly """{"tid":"t1","roles":["editor"]}""") (Ok []) "roles ignored"

        testCase "malformed role claims fail closed, naming the claim"
        <| fun _ ->
            for payload, claim in
                [
                    """{"roles":42}""", "roles"
                    """{"roles":{"x":1}}""", "roles"
                    """{"roles":["editor",7]}""", "roles"
                    """{"roles":["editor",""]}""", "roles"
                    """{"roles":"   "}""", "roles"
                    """{"roles":null}""", "roles"
                    """{"groups":[true]}""", "groups"
                ] do
                Expect.equal (rolesOf ClaimMapping.directoryRoles payload) (Error claim) payload

        testCase "a group overage is refused, never read as 'no groups'"
        <| fun _ ->
            let payload =
                """{"roles":["editor"],"_claim_names":{"groups":"src1"},"_claim_sources":{"src1":{"endpoint":"https://graph.example/x"}}}"""

            match mapWith ClaimMapping.directoryRoles payload with
            | Error(claim, reason) ->
                Expect.equal claim "groups" "the overage claim is named"
                Expect.stringContains reason "overage" "the reason says why"
            | Ok u -> failtestf "an overage must not map; got roles %A" u.DirectoryRoles

        testCase "the overage check applies to the claim that is named, not to every claim"
        <| fun _ ->
            let rolesOnly = {
                ClaimMapping.none with
                    RolesClaim = Some "roles"
            }

            Expect.equal
                (rolesOf rolesOnly """{"roles":["editor"],"_claim_names":{"groups":"src1"}}""")
                (Ok [ "editor" ])
                "groups is not mapped here, so its overage is not this mapping's concern"

        testCase "admission — AllowedTenants admits a listed tenant and refuses others"
        <| fun _ ->
            let gate = {
                ClaimMapping.none with
                    TenantIdClaim = Some "tid"
                    AllowedTenants = [ "t-ours" ]
            }

            let admit payload =
                mapWith gate payload
                |> Result.mapError fst
                |> Result.bind (ToolUp.AuthProviders.OidcAuthProvider.applyAdmission gate >> Result.mapError id)
                |> Result.isOk

            Expect.isTrue (admit """{"tid":"t-ours"}""") "listed tenant admitted"
            Expect.isFalse (admit """{"tid":"t-theirs"}""") "other tenant refused"

        testCase "admission — AllowedTenants without TenantIdClaim refuses every principal"
        <| fun _ ->
            let gate = {
                ClaimMapping.none with
                    AllowedTenants = [ "t-ours" ]
            }

            let user = {
                AuthenticatedUser.anonymous with
                    UserId = "u"
                    TenantId = None
            }

            match ToolUp.AuthProviders.OidcAuthProvider.applyAdmission gate user with
            | Error reason -> Expect.stringContains reason "TenantIdClaim" "the misconfiguration is named"
            | Ok _ -> failtest "no tenant can be read, so nobody is admitted"

        testCase "admission — RequiredRoles admits a holder of any listed role, after aliasing"
        <| fun _ ->
            let gate = {
                groupsAliased with
                    RequiredRoles = [ "finance"; "auditor" ]
            }

            let admit payload =
                match mapWith gate payload with
                | Ok u -> ToolUp.AuthProviders.OidcAuthProvider.applyAdmission gate u |> Result.isOk
                | Error _ -> false

            Expect.isTrue (admit """{"groups":["0b3e-finance"]}""") "aliased group satisfies the gate"
            Expect.isFalse (admit """{"roles":["editor"]}""") "a holder of no listed role is refused"
            Expect.isFalse (admit """{"sub":"u"}""") "a principal with no roles is refused"

        testCase "TokenRoles ride only on a signed-in human principal"
        <| fun _ ->
            let roles = [ "editor"; "editor"; "reader" ]

            Expect.equal
                (AccessContext.tokenRolesFor (AuthenticatedUser "u") roles)
                [ "editor"; "reader" ]
                "authenticated user, de-duplicated"

            Expect.equal (AccessContext.tokenRolesFor (TeamMember("u", "t")) roles) [ "editor"; "reader" ] "team member"
            Expect.equal (AccessContext.tokenRolesFor (AnonymousSession "s") roles) [] "anonymous session"

        testCase "a token role admits a ScopeGated page and grants no module permission"
        <| fun _ ->
            let directoryEditor = { user with TokenRoles = [ "editor" ] }

            Expect.equal
                (AudienceGate.evaluate directoryEditor (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Allow
                "the directory role satisfies the page"

            Expect.equal
                (AudienceGate.evaluate directoryEditor (PageAudience.ScopeGated [ "finance" ]))
                AudienceDecision.Forbidden
                "a role the principal does not hold is refused"

            Expect.equal
                (AudienceGate.evaluate { user with TokenRoles = [ "Editor" ] } (PageAudience.ScopeGated [ "editor" ]))
                AudienceDecision.Forbidden
                "roles compare exactly, as the IdP issued them"

            // The module-RBAC reading is untouched: an empty permission map is
            // still unrestricted for modules (GP 11), and a token role adds no
            // permission entry.
            Expect.isTrue directoryEditor.ModulePermissions.IsEmpty "no module permission was granted"
    ]

// ─── 6b. the composed pipeline, with the mock OIDC issuer ────────────

/// One mock issuer for the whole section — Kestrel on a loopback port.
let private issuer =
    lazy (MockOidcServer.start MockOidcServer.MockOidcConfig.defaults)

let private oidcProviderWith (mapping: ClaimMapping) : IAuthProvider =
    let s = issuer.Force()

    ToolUp.AuthProviders.OidcAuthProvider.fromConfigWith (new HttpClient()) None {
        Issuer = Some s.IssuerUrl
        Audience = None
        KeySource = JwksDiscovery s.IssuerUrl
        TokenLocation = BearerOrCookie AuthSession.CookieName
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
        ClaimMapping = Some mapping
    }

let private directoryMapping = {
    ClaimMapping.directoryRoles with
        GroupAliases = Map["0b3e-finance", "finance"]
}

/// A request to `path` carrying `token` (if any) as a bearer, optionally
/// as a browser page navigation (`Accept: text/html`).
let private sendToken (client: HttpClient) (token: string option) (navigation: bool) (path: string) = async {
    use request = new HttpRequestMessage(HttpMethod.Get, path)

    token
    |> Option.iter (fun t -> request.Headers.Add("Authorization", "Bearer " + t))

    if navigation then
        request.Headers.Add("Accept", "text/html")

    use! response = client.SendAsync request |> Async.AwaitTask
    let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask

    let location =
        match response.Headers.Location with
        | null -> None
        | l -> Some(string l)

    return int response.StatusCode, body, location
}

let private directoryPipelineTests =
    testList "Phase 987 — directory roles through the composed pipeline" [
        testCaseAsync "no token → 401; a non-member with the matching role → 200; wrong role → 403; no role → 403"
        <| async {
            let s = issuer.Force()
            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) ignore []
            use client = host.GetTestClient()

            let token sub (claims: (string * obj) list) = Some(s.MintTokenFor(sub, claims))

            let cells = [
                // no token at all
                "no token", None, "/editors", 401
                "no token", None, "/open", 200
                // zoe: in no team, holds `editor` in the directory only
                "zoe editor", token "zoe" [ "roles", box [| "editor" |] ], "/editors", 200
                "zoe editor", token "zoe" [ "roles", box [| "editor" |] ], "/members", 200
                // yuri: signed in, wrong role
                "yuri viewer", token "yuri" [ "roles", box [| "viewer" |] ], "/editors", 403
                // erin: team member with no permissions configured, no roles
                "erin none", token "erin" [], "/editors", 403
                // a group, aliased to the role the page names
                "xavi group", token "xavi" [ "groups", box [| "0b3e-finance"; "editor" |] ], "/editors", 200
                // a malformed role claim rejects the token: 401, not a silent 403
                "wanda malformed", token "wanda" [ "roles", box 42 ], "/editors", 401
                // carol's module permission still admits her without a token role
                "carol rbac", token "carol" [], "/editors", 200
            ]

            let! results =
                cells
                |> List.map (fun (label, tok, path, expected) -> async {
                    let! status, body, _ = sendToken client tok false path
                    return label, path, expected, status, body
                })
                |> Async.Sequential

            do! host.StopAsync() |> Async.AwaitTask

            let mismatches =
                results
                |> Array.choose (fun (label, path, expected, status, _) ->
                    if status <> expected then
                        Some $"{label} GET {path}: expected {expected}, got {status}"
                    else
                        None)

            Expect.isEmpty mismatches ("mismatched: " + String.concat "; " mismatches)

            for label, path, _, status, body in results do
                if status = 401 || status = 403 then
                    Expect.isFalse
                        (body.Contains "Title-")
                        $"{label} GET {path}: a refused page carries no page content"
                elif status = 200 then
                    // The falsifier for the line above: a served page does
                    // carry the marker.
                    Expect.stringContains body "Title-" $"{label} GET {path}: a served page carries its content"
        }

        testCaseAsync "a directory role change changes access on the next token, with no change in the app"
        <| async {
            let s = issuer.Force()
            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) ignore []
            use client = host.GetTestClient()

            let! before, _, _ = sendToken client (Some(s.MintTokenFor("vic", []))) false "/editors"

            let! granted, _, _ =
                sendToken client (Some(s.MintTokenFor("vic", [ "roles", box [| "editor" |] ]))) false "/editors"

            let! removed, _, _ = sendToken client (Some(s.MintTokenFor("vic", [ "roles", box [||] ]))) false "/editors"
            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal (before, granted, removed) (403, 200, 403) "granted, then removed, in the directory"
        }

        testCaseAsync "with an InteractiveSignIn registered, a credential-less navigation is redirected — and only that"
        <| async {
            let s = issuer.Force()

            let register (services: IServiceCollection) =
                services.AddSingleton<InteractiveSignIn>({ SignInPath = "/auth/sign-in" })
                |> ignore

            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) register []
            use client = host.GetTestClient()

            let! navStatus, _, navLocation = sendToken client None true "/editors?tab=q3"
            let! fetchStatus, _, _ = sendToken client None false "/editors"

            let! wrongRole, _, wrongLocation =
                sendToken client (Some(s.MintTokenFor("yuri", [ "roles", box [| "viewer" |] ]))) true "/editors"

            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal navStatus 302 "a browser navigation is sent to sign in"

            Expect.equal
                navLocation
                (Some "/auth/sign-in?returnUrl=%2Feditors%3Ftab%3Dq3")
                "to the sign-in route, carrying the page and its query"

            Expect.equal fetchStatus 401 "a non-navigation request keeps the bare 401"
            Expect.equal wrongRole 403 "a signed-in principal who is refused is never redirected"
            Expect.isNone wrongLocation "no redirect on 403"
        }
    ]

// ─── 6c. interactive sign-in, end to end ─────────────────────────────

/// The value of the `name` cookie a response sets, with its attributes.
let private setCookie (response: HttpResponseMessage) (name: string) : string option =
    match response.Headers.TryGetValues "Set-Cookie" with
    | true, values -> values |> Seq.tryFind (fun v -> v.StartsWith(name + "="))
    | _ -> None

let private cookieValue (header: string) =
    let pair = header.Split(';')[0]
    pair.Substring(pair.IndexOf '=' + 1)

let private signInTests =
    testList "Phase 987 — interactive SSR sign-in (authorization code + PKCE)" [
        testCaseAsync "a link opened cold signs the reader in and returns them to the page"
        <| async {
            let s = issuer.Force()

            let signInConfig =
                ToolUp.AuthProviders.OidcSsrSignIn.defaults
                    $"{s.IssuerUrl}/authorize"
                    $"{s.IssuerUrl}/token"
                    "gated-ssr-client"
                    "http://localhost/auth/callback"

            let register (services: IServiceCollection) =
                ToolUp.AuthProviders.OidcSsrSignIn.register services signInConfig |> ignore

            let routes = [ ToolUp.AuthProviders.OidcSsrSignIn.routes (new HttpClient()) signInConfig ]

            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) register routes
            use client = host.GetTestClient()

            use idp = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))

            let get (path: string) (cookies: (string * string) list) = async {
                use request = new HttpRequestMessage(HttpMethod.Get, path)
                request.Headers.Add("Accept", "text/html")

                if not cookies.IsEmpty then
                    request.Headers.Add("Cookie", cookies |> List.map (fun (k, v) -> $"{k}={v}") |> String.concat "; ")

                let! response = client.SendAsync request |> Async.AwaitTask
                return response
            }

            // The directory says zoe holds `editor`.
            s.SetSignInIdentity("zoe", [ "roles", box [| "editor" |] ])

            // 1. the page sends a cold browser to sign in
            use! page = get "/editors" []
            Expect.equal (int page.StatusCode) 302 "redirected to sign in"
            let signInPath = string page.Headers.Location

            // 2. the sign-in route seals its state and goes to the IdP
            use! start = get signInPath []
            Expect.equal (int start.StatusCode) 302 "redirected to the IdP"
            let authorize = string start.Headers.Location
            Expect.stringStarts authorize $"{s.IssuerUrl}/authorize?" "to the authorization endpoint"
            Expect.stringContains authorize "code_challenge_method=S256" "with a PKCE challenge"

            let stateCookie =
                setCookie start ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName
                |> Option.defaultWith (fun () -> failtest "the sign-in state cookie is set")

            Expect.stringContains (stateCookie.ToLowerInvariant()) "httponly" "the state cookie is HttpOnly"

            Expect.stringContains
                (stateCookie.ToLowerInvariant())
                "samesite=lax"
                "and Lax, so the IdP's return carries it"

            // 3. the IdP authenticates and redirects back with a code
            use! fromIdp = idp.GetAsync authorize |> Async.AwaitTask
            let callback = Uri(string fromIdp.Headers.Location)
            Expect.equal callback.AbsolutePath "/auth/callback" "back to the callback"

            // 4. the callback exchanges the code and sets the session
            use! landed =
                get callback.PathAndQuery [
                    ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName, cookieValue stateCookie
                ]

            Expect.equal (int landed.StatusCode) 302 "signed in"
            Expect.equal (string landed.Headers.Location) "/editors" "returned to the page that was asked for"

            let session =
                setCookie landed AuthSession.CookieName
                |> Option.defaultWith (fun () -> failtest "the session cookie is set")

            let lower = session.ToLowerInvariant()
            Expect.stringContains lower "httponly" "the session cookie is HttpOnly"
            Expect.stringContains lower "samesite=lax" "and SameSite=Lax"

            // 5. the page is now served
            use! reread = get "/editors" [ AuthSession.CookieName, cookieValue session ]
            let! body = reread.Content.ReadAsStringAsync() |> Async.AwaitTask
            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal (int reread.StatusCode) 200 "the signed-in reader holds the role"
            Expect.stringContains body "Title-editors" "and receives the page"
        }

        testCaseAsync "a signed-in reader without the role lands on 403, not back in the sign-in"
        <| async {
            let s = issuer.Force()

            let signInConfig =
                ToolUp.AuthProviders.OidcSsrSignIn.defaults
                    $"{s.IssuerUrl}/authorize"
                    $"{s.IssuerUrl}/token"
                    "gated-ssr-client"
                    "http://localhost/auth/callback"

            let register (services: IServiceCollection) =
                ToolUp.AuthProviders.OidcSsrSignIn.register services signInConfig |> ignore

            let routes = [ ToolUp.AuthProviders.OidcSsrSignIn.routes (new HttpClient()) signInConfig ]
            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) register routes
            use client = host.GetTestClient()
            use idp = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))
            s.SetSignInIdentity("yuri", [ "roles", box [| "viewer" |] ])

            use! start = client.GetAsync "/auth/sign-in?returnUrl=%2Feditors" |> Async.AwaitTask

            let stateCookie =
                setCookie start ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName |> Option.get

            use! fromIdp = idp.GetAsync(string start.Headers.Location) |> Async.AwaitTask
            let callback = Uri(string fromIdp.Headers.Location)

            use callbackRequest = new HttpRequestMessage(HttpMethod.Get, callback.PathAndQuery)

            callbackRequest.Headers.Add(
                "Cookie",
                $"{ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName}={cookieValue stateCookie}"
            )

            use! landed = client.SendAsync callbackRequest |> Async.AwaitTask
            let session = setCookie landed AuthSession.CookieName |> Option.get

            use pageRequest = new HttpRequestMessage(HttpMethod.Get, "/editors")
            pageRequest.Headers.Add("Accept", "text/html")
            pageRequest.Headers.Add("Cookie", $"{AuthSession.CookieName}={cookieValue session}")
            use! page = client.SendAsync pageRequest |> Async.AwaitTask
            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal (int page.StatusCode) 403 "refused, and not redirected"
            Expect.isNull page.Headers.Location "no redirect"
        }

        testCaseAsync "a forged state, a missing state cookie and a foreign return URL are refused"
        <| async {
            let s = issuer.Force()

            let signInConfig =
                ToolUp.AuthProviders.OidcSsrSignIn.defaults
                    $"{s.IssuerUrl}/authorize"
                    $"{s.IssuerUrl}/token"
                    "gated-ssr-client"
                    "http://localhost/auth/callback"

            let routes = [ ToolUp.AuthProviders.OidcSsrSignIn.routes (new HttpClient()) signInConfig ]
            use! host = startHostWith authenticatedSurface (oidcProviderWith directoryMapping) ignore routes
            use client = host.GetTestClient()
            use idp = new HttpClient(new HttpClientHandler(AllowAutoRedirect = false))
            s.SetSignInIdentity("zoe", [ "roles", box [| "editor" |] ])

            // An open-redirect attempt is replaced by `/`.
            use! start =
                client.GetAsync "/auth/sign-in?returnUrl=%2F%2Fevil.example%2Fx"
                |> Async.AwaitTask

            let stateCookie =
                setCookie start ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName |> Option.get

            use! fromIdp = idp.GetAsync(string start.Headers.Location) |> Async.AwaitTask
            let callback = Uri(string fromIdp.Headers.Location)

            let callbackWith (pathAndQuery: string) (cookie: string option) = async {
                use request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery)

                cookie
                |> Option.iter (fun c ->
                    request.Headers.Add("Cookie", $"{ToolUp.AuthProviders.OidcSsrSignIn.StateCookieName}={c}"))

                let! response = client.SendAsync request |> Async.AwaitTask
                return response
            }

            let forged = callback.PathAndQuery.Replace("state=", "state=forged")

            use! forgedResponse = callbackWith forged (Some(cookieValue stateCookie))
            use! noCookie = callbackWith callback.PathAndQuery None
            use! good = callbackWith callback.PathAndQuery (Some(cookieValue stateCookie))
            do! host.StopAsync() |> Async.AwaitTask

            Expect.equal (int forgedResponse.StatusCode) 400 "a state that does not match is refused"
            Expect.isNone (setCookie forgedResponse AuthSession.CookieName) "and sets no session"
            Expect.equal (int noCookie.StatusCode) 400 "a callback with no sealed state is refused"
            Expect.isNone (setCookie noCookie AuthSession.CookieName) "and sets no session"
            Expect.equal (int good.StatusCode) 302 "the genuine callback signs in"
            Expect.equal (string good.Headers.Location) "/" "to `/`, not to the foreign return URL"
        }
    ]

let tests =
    testList "GatedSsr (Phase 86)" [
        parseTests
        gateTests
        handlerTests
        exclusionTests
        pipelineTests
        claimMappingTests
        directoryPipelineTests
        signInTests
    ]