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
let private startHost (surfaces: SurfaceProfile list) : Async<IHost> = async {
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
                        services.AddSingleton<IAuthProvider>(authProvider ()) |> ignore

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
                            choose [ route "/api/x" >=> text "api"; PublicPageHandler.handler api layouts ]
                        ))
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask
    return host
}

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
            Expect.isFalse (body.Contains "body-") $"%A{credential} GET {path}: a refused page carries no page body"
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

let tests =
    testList "GatedSsr (Phase 86)" [ parseTests; gateTests; handlerTests; exclusionTests; pipelineTests ]