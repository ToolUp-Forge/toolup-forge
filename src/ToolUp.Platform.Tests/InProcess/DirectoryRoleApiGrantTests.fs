module ToolUp.Platform.Tests.InProcess.DirectoryRoleApiGrantTests

open System
open System.Net.Http
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.TestHost
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.Server
open ToolUp.PublicRendering
open ToolUp.Remoting.Server

// ─── Phase 993 — directory roles gate pages; API roles need an allow-list ─
//
// Phase 987 mapped the identity provider's role and group claims onto
// `AuthenticatedUser.Roles`, which BOTH page audiences (`ScopeGated`, via
// `AccessContext.TokenRoles`) and API gates (`[<RequiresRole>]`, via
// `IAuthContext.HasRole`) read. So whoever administers the directory could
// grant API privilege by naming a role or group. Phase 993 keeps the two
// grants apart: a mapped role lands in `AuthenticatedUser.DirectoryRoles`
// (pages only) and reaches `Roles` (APIs) only when
// `ClaimMapping.ApiRoleGrants` names it.
//
// This pack tries to defeat that, at three layers:
//
//   A — the shipped claim mapping, driven directly: where each mapped role
//       lands, with and without the allow-list, and through group aliases.
//   B — the composed request pipeline: a REAL RS256 token from the mock
//       issuer, the REAL OIDC provider, the REAL `ScopeResolutionMiddleware`
//       and `AccessContext` factory for the page, and the REAL default
//       `ForgeAuthContext` resolver plus the REAL attribute classifier and
//       evaluator for the API. Nothing is injected.
//   C — fail-closed configuration: a malformed or reserved allow-list
//       refuses to build the provider, and the environment route refuses
//       startup.
//
// Which assertions go red without the fix, and which do not, is stated
// case by case. In particular a directory `PlatformAdmin` was ALREADY
// refused at `[<RequiresRole "PlatformAdmin">]` by the default resolver,
// which answers that one role from the server-resolved platform-admin
// grant (Phase 132), never from a token. It is asserted anyway — it is the
// role an attacker would reach for first — but the red-without-the-fix
// case is a deployment-defined role (`Publisher`), which the default
// resolver DOES read from `AuthenticatedUser.Roles`.

// ── A — the mapping ─────────────────────────────────────────────────

/// An UNSIGNED token around `payload` — enough for the post-validation
/// projection, which by contract reads already-trusted bytes.
let private rawToken (payload: string) =
    let enc (s: string) =
        Convert.ToBase64String(Encoding.UTF8.GetBytes s).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    enc """{"alg":"RS256","kid":"k"}""" + "." + enc payload + ".sig"

let private mapWith (mapping: ClaimMapping) (payload: string) =
    let baseUser = {
        AuthenticatedUser.anonymous with
            UserId = "u"
            DisplayName = "u"
    }

    match ToolUp.AuthProviders.OidcAuthProvider.applyValidatedClaimMapping mapping (rawToken payload) baseUser with
    | Ok u -> u
    | Error(claim, reason) -> failtestf "mapping refused claim %s: %s" claim reason

let private publisherAliased = {
    ClaimMapping.directoryRoles with
        GroupAliases = Map["0b3e-pub", "Publisher"]
}

let private mappingTests =
    testList "A — where a mapped role lands" [
        testCase "no allow-list: every mapped role is a directory role and none is an API role"
        <| fun _ ->
            // RED without the fix: 987 put both roles in `Roles`.
            let u =
                mapWith ClaimMapping.directoryRoles """{"roles":["PlatformAdmin","Publisher"],"groups":["Admin"]}"""

            Expect.equal u.DirectoryRoles [ "PlatformAdmin"; "Publisher"; "Admin" ] "pages see every mapped role"
            Expect.isEmpty u.Roles "no mapped role is an API role"

        testCase "an allow-listed role is ALSO an API role; the rest stay page-only"
        <| fun _ ->
            let mapping = {
                ClaimMapping.directoryRoles with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            let u = mapWith mapping """{"roles":["Publisher","Admin"]}"""
            Expect.equal u.Roles [ "Publisher" ] "exactly the allow-listed role"
            Expect.equal u.DirectoryRoles [ "Publisher"; "Admin" ] "pages still see both"

        testCase "a group is allow-listed by its ALIAS, never by its raw id"
        <| fun _ ->
            let byAlias = {
                publisherAliased with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            let byRawId = {
                publisherAliased with
                    ApiRoleGrants = Set [ "0b3e-pub" ]
            }

            let payload = """{"groups":["0b3e-pub"]}"""
            Expect.equal (mapWith byAlias payload).Roles [ "Publisher" ] "the alias is granted"
            Expect.isEmpty (mapWith byRawId payload).Roles "the raw id of an aliased group grants nothing"

            Expect.equal
                (mapWith byRawId payload).DirectoryRoles
                [ "Publisher" ]
                "the page role is the alias either way"

        testCase "the allow-list matches exactly, as the IdP issued the role"
        <| fun _ ->
            let mapping = {
                ClaimMapping.directoryRoles with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            Expect.isEmpty (mapWith mapping """{"roles":["publisher"]}""").Roles "case differs: not granted"

        testCase "an allow-list cannot grant a role the token does not carry"
        <| fun _ ->
            let mapping = {
                ClaimMapping.directoryRoles with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            let u = mapWith mapping """{"roles":["Reader"]}"""
            Expect.isEmpty u.Roles "nothing to grant"

        testCase "page roles are the API roles plus the directory roles"
        <| fun _ ->
            let u = {
                AuthenticatedUser.anonymous with
                    UserId = "u"
                    Roles = [ "Publisher" ]
                    DirectoryRoles = [ "Publisher"; "Reader" ]
            }

            Expect.equal (AuthenticatedUser.pageRoles u) [ "Publisher"; "Reader" ] "union, de-duplicated"

            Expect.equal
                (AccessContext.tokenRolesFor (Subject.AuthenticatedUser "u") (AuthenticatedUser.pageRoles u))
                [ "Publisher"; "Reader" ]
                "what the AccessContext carries"
    ]

// ── B — the composed pipeline ───────────────────────────────────────

/// The API record a deployment would mount. Classified by the real
/// attribute classifier, evaluated by the real evaluator.
type private GuardedApi = {
    [<RequiresRole "PlatformAdmin">]
    AdminOnly: unit -> Async<int>
    [<RequiresRole "Publisher">]
    PublishReport: unit -> Async<int>
}

let private guarded = AuthClassifier.classify typeof<GuardedApi>

let private silentLogger: ILogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private issuer =
    lazy (MockOidcServer.start MockOidcServer.MockOidcConfig.defaults)

let private oidcConfig (mapping: ClaimMapping) : AuthConfig =
    let s = issuer.Force()

    {
        Issuer = Some s.IssuerUrl
        Audience = None
        KeySource = JwksDiscovery s.IssuerUrl
        TokenLocation = BearerOrCookie AuthSession.CookieName
        ClockSkewSeconds = None
        AcceptedAlgorithms = None
        PreferOidWhenPresent = None
        ClaimMapping = Some mapping
    }

/// `/page/<role>` answers what `AudienceGate` decides for a `ScopeGated
/// [role]` page over the request's scoped `AccessContext`; `/api/<method>`
/// answers what the classifier decides for that `GuardedApi` method over
/// the default `ForgeAuthContext` resolver. 200 admitted, 403 refused,
/// 401 when the page wants a sign-in.
let private probeRoutes: HttpHandler =
    choose [
        routef "/page/%s" (fun role next ctx ->
            let access = ctx.RequestServices.GetRequiredService<AccessContext>()

            match AudienceGate.evaluate access (PageAudience.ScopeGated [ role ]) with
            | AudienceDecision.Allow -> text "page" next ctx
            | AudienceDecision.RequireAuthentication -> setStatusCode 401 next ctx
            | AudienceDecision.Forbidden -> setStatusCode 403 next ctx)
        routef "/api/%s" (fun methodName next ctx -> task {
            let! forge = ApiSeams.defaultForgeAuthContextResolver ctx |> Async.StartAsTask

            let auth =
                { new IAuthContext with
                    member _.HasRole role = forge.HasRole role
                    member _.HasClaim(claim, value) = forge.HasClaim(claim, value)
                    member _.HasTenant() = forge.HasTenant()
                    member _.IsAnonymous() = forge.IsAnonymous()
                    member _.SubjectId = forge.SubjectId
                }

            match AuthClassifier.evaluate (Map.find methodName guarded) (Some auth) with
            | AuthDecision.Allow -> return! text "api" next ctx
            | AuthDecision.Deny _ -> return! setStatusCode 403 next ctx
        })
    ]

/// The SDK's request pipeline, in the SDK's order, on an individual
/// (team-less) deployment whose auth provider is the real OIDC provider.
let private startHost (mapping: ClaimMapping) : Async<IHost> = async {
    let config = {
        ServerConfig.defaults with
            Surfaces = [ SurfaceProfile.individual ]
    }

    let blob = ToolUp.Platform.Testing.Fakes.standardBlobStorage ()
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel

    let permissionStore =
        PermissionStore.PermissionStore(blob) :> PermissionStore.IPermissionStore

    let auth =
        ToolUp.AuthProviders.OidcAuthProvider.fromConfigWith (new HttpClient()) None (oidcConfig mapping)

    let host =
        Host
            .CreateDefaultBuilder()
            .ConfigureWebHostDefaults(fun webHost ->
                webHost
                    .UseTestServer()
                    .ConfigureServices(fun services ->
                        services.AddGiraffe() |> ignore
                        services.AddSingleton<ILogger>(silentLogger) |> ignore
                        services.AddSingleton<IAuthProvider>(auth) |> ignore

                        services.AddSingleton<PermissionStore.IPermissionStore>(permissionStore)
                        |> ignore

                        ComposeScopeResolver.registerScopeResolution
                            services
                            config
                            None
                            notifications
                            silentLogger
                            []
                            []
                            []
                            None)
                    .Configure(fun (app: IApplicationBuilder) ->
                        app.UseMiddleware<Middleware.ScopeResolutionMiddleware>(config) |> ignore
                        app.UseMiddleware<SurfaceEnforcement.SurfaceEnforcementMiddleware>() |> ignore
                        app.UseGiraffe probeRoutes)
                |> ignore)
            .Build()

    do! host.StartAsync() |> Async.AwaitTask
    return host
}

/// Status for each `(claims, path)` cell, one host for the lot.
let private statuses (mapping: ClaimMapping) (cells: ((string * obj) list * string) list) = async {
    let s = issuer.Force()
    use! host = startHost mapping
    use client = host.GetTestClient()

    let! results =
        cells
        |> List.mapi (fun i (claims, path) -> async {
            use request = new HttpRequestMessage(HttpMethod.Get, path)
            request.Headers.Add("Authorization", "Bearer " + s.MintTokenFor($"reader-{i}", claims))
            use! response = client.SendAsync request |> Async.AwaitTask
            return int response.StatusCode
        })
        |> Async.Sequential

    do! host.StopAsync() |> Async.AwaitTask
    return List.ofArray results
}

let private directoryAdmin: (string * obj) list = [
    "roles", box [| "PlatformAdmin"; "Publisher" |]
    "groups", box [| "PlatformAdmin" |]
]

let private pipelineTests =
    testList "B — the composed pipeline" [
        testCaseAsync "mapping on, no allow-list: the directory gates pages and grants no API role"
        <| async {
            let! got =
                statuses ClaimMapping.directoryRoles [
                    directoryAdmin, "/page/PlatformAdmin"
                    directoryAdmin, "/page/Publisher"
                    directoryAdmin, "/api/AdminOnly"
                    // RED without the fix: 987 admitted this one (200).
                    directoryAdmin, "/api/PublishReport"
                ]

            Expect.equal
                got
                [ 200; 200; 403; 403 ]
                "pages admit the directory roles; neither RequiresRole gate admits them"
        }

        testCaseAsync "the falsifier: the same probe admits a role the allow-list names, and only that one"
        <| async {
            let mapping = {
                ClaimMapping.directoryRoles with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            let! got =
                statuses mapping [
                    directoryAdmin, "/api/PublishReport"
                    directoryAdmin, "/api/AdminOnly"
                    directoryAdmin, "/page/PlatformAdmin"
                    [ "roles", box [| "Reader" |] ], "/api/PublishReport"
                ]

            Expect.equal
                got
                [ 200; 403; 200; 403 ]
                "Publisher admitted at the API; PlatformAdmin still refused; a non-holder still refused"
        }

        testCaseAsync "a group aliased to an allow-listed name is admitted only through the alias"
        <| async {
            let byAlias = {
                publisherAliased with
                    ApiRoleGrants = Set [ "Publisher" ]
            }

            let byRawId = {
                publisherAliased with
                    ApiRoleGrants = Set [ "0b3e-pub" ]
            }

            let groupMember: (string * obj) list = [ "groups", box [| "0b3e-pub" |] ]
            let! aliased = statuses byAlias [ groupMember, "/api/PublishReport"; groupMember, "/page/Publisher" ]
            let! rawId = statuses byRawId [ groupMember, "/api/PublishReport"; groupMember, "/page/Publisher" ]

            Expect.equal aliased [ 200; 200 ] "the alias is allow-listed: API and page"
            Expect.equal rawId [ 403; 200 ] "the raw id is allow-listed: page only"
        }
    ]

// ── C — fail-closed configuration ───────────────────────────────────

let private expectRefusedGrants (grants: string list) (needle: string) =
    match ClaimMapping.validateApiRoleGrants (Set grants) with
    | Ok() -> failtestf "%A must be refused" grants
    | Error reason -> Expect.stringContains reason needle $"{grants}: the reason names the problem"

let private withEnv (pairs: (string * string option) list) (body: unit -> unit) =
    let priors =
        pairs |> List.map (fun (n, _) -> n, Environment.GetEnvironmentVariable n)

    try
        for n, v in pairs do
            Environment.SetEnvironmentVariable(n, v |> Option.toObj)

        body ()
    finally
        for n, prior in priors do
            Environment.SetEnvironmentVariable(n, prior)

/// The `ClaimMapping` `AuthProvider.fromEnv` hands its OIDC builder, under
/// `env`. `Error` carries the startup refusal's message.
let private envMapping (env: (string * string option) list) =
    let mutable captured: AuthConfig option = None

    let capturing: AuthProvider.OidcAuthBuilder =
        fun _ config ->
            captured <- Some config

            { new IAuthProvider with
                member _.GetUser _ = async { return AuthenticatedUser.anonymous }
                member _.ValidateRequest _ = async { return Error "marker" }
                member _.IsCryptographicallyVerified = true
            }

    let mutable result = Error "not run"

    withEnv
        ([
            ConfigKeys.Names.authMode, Some "oidc"
            ConfigKeys.Names.oidcIssuer, Some "https://idp.example.com"
            ConfigKeys.Names.oidcUserIdClaim, None
            ConfigKeys.Names.oidcTenantIdClaim, None
            ConfigKeys.Names.oidcRolesClaim, None
            ConfigKeys.Names.oidcGroupsClaim, None
            ConfigKeys.Names.oidcApiRoleGrants, None
         ]
         @ env)
        (fun () ->
            result <-
                try
                    AuthProvider.fromEnv silentLogger capturing |> ignore
                    Ok(captured |> Option.bind _.ClaimMapping)
                with :? InvalidOperationException as ex ->
                    Error ex.Message)

    result

let private configTests =
    testSequenced (
        testList "C — fail-closed configuration" [
            testCase "a blank, padded or whitespace-bearing entry is refused"
            <| fun _ ->
                for grants in [ [ "" ]; [ " " ]; [ " Publisher" ]; [ "Report Publisher" ]; [ "Publisher\t" ] ] do
                    expectRefusedGrants grants "not a role name"

            testCase "PlatformAdmin is refused: it is resolved server-side, never from a token"
            <| fun _ -> expectRefusedGrants [ "Publisher"; "PlatformAdmin" ] "IPlatformAdminStore"

            testCase "an exact role name is accepted, and so is the empty default"
            <| fun _ ->
                Expect.equal (ClaimMapping.validateApiRoleGrants (Set [ "Publisher"; "report.reader" ])) (Ok()) "ok"
                Expect.equal (ClaimMapping.validateApiRoleGrants ClaimMapping.none.ApiRoleGrants) (Ok()) "default"

            testCase "the OIDC provider refuses to BUILD over a refused allow-list"
            <| fun _ ->
                for grants in [ [ "PlatformAdmin" ]; [ "Report Publisher" ] ] do
                    let mapping = {
                        ClaimMapping.directoryRoles with
                            ApiRoleGrants = Set grants
                    }

                    Expect.throwsT<ArgumentException>
                        (fun () ->
                            ToolUp.AuthProviders.OidcAuthProvider.fromConfigWith
                                (new HttpClient())
                                None
                                (oidcConfig mapping)
                            |> ignore)
                        $"{grants}: refused at construction, not at request time"

            testCase "env: unset grants no API role"
            <| fun _ ->
                match envMapping [ ConfigKeys.Names.oidcRolesClaim, Some "roles" ] with
                | Ok(Some m) -> Expect.isEmpty m.ApiRoleGrants "no allow-list"
                | other -> failtestf "expected a mapping, got %A" other

            testCase "env: a comma-separated list is trimmed into the allow-list"
            <| fun _ ->
                match
                    envMapping [
                        ConfigKeys.Names.oidcRolesClaim, Some "roles"
                        ConfigKeys.Names.oidcApiRoleGrants, Some " Publisher , report.reader"
                    ]
                with
                | Ok(Some m) -> Expect.equal m.ApiRoleGrants (Set [ "Publisher"; "report.reader" ]) "both, trimmed"
                | other -> failtestf "expected a mapping, got %A" other

            testCase "env: malformed values refuse startup, naming the variable"
            <| fun _ ->
                for raw in [ "Publisher,,Reader"; "Publisher,"; "Report Publisher"; "PlatformAdmin" ] do
                    match
                        envMapping [
                            ConfigKeys.Names.oidcRolesClaim, Some "roles"
                            ConfigKeys.Names.oidcApiRoleGrants, Some raw
                        ]
                    with
                    | Error message -> Expect.stringContains message ConfigKeys.Names.oidcApiRoleGrants raw
                    | Ok m -> failtestf "'%s' must refuse startup; got %A" raw m

            testCase "env: an allow-list with no role or group claim to draw from refuses startup"
            <| fun _ ->
                match envMapping [ ConfigKeys.Names.oidcApiRoleGrants, Some "Publisher" ] with
                | Error message -> Expect.stringContains message ConfigKeys.Names.oidcRolesClaim "names the fix"
                | Ok m -> failtestf "must refuse startup; got %A" m
        ]
    )

let tests =
    testList "Phase 993 — directory roles gate pages; API roles only by allow-list" [
        mappingTests
        pipelineTests
        configTests
    ]