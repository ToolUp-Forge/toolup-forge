module ToolUp.Platform.Tests.InProcess.DataSubjectRequestTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.DataSubjectRequestApi
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose

[<Tests>]
let tests = IDataSubjectRequestContract.tests

// ─── Phase 229 — DSR Platform-Admin authorization gate ───────────────
//
// Every IDataSubjectRequestApi method gates on Platform-Admin: the
// `[<RequiresRole "PlatformAdmin">]` classifier attribute on the contract
// PLUS an in-handler `canModifyPlatformConfig` check. These cases prove
// the in-handler gate (a non-admin caller is denied export/preview/
// confirm even if the deployment auth middleware admitted them).

let private nonAdmin: AccessContext =
    AccessContexts.forSubject (AuthenticatedUser "regular-user")

let private admin: AccessContext = {
    AccessContexts.forSubject (AuthenticatedUser "admin-user") with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private mkApi (accessContext: AccessContext) : IDataSubjectRequestApi =
    DataSubjectRequestApiHandler.create
        []
        []
        ErasurePolicy.Tombstone
        "team-x"
        accessContext.UserId
        accessContext
        DataSubjectRequestApiHandler.noOpAuditCallback
        None

let private exportInput: ExportRequestInput = {
    SubjectUserId = "victim"
    TeamId = None
    Reason = "test"
}

let private eraseInput: ErasureRequestInput = {
    SubjectUserId = "victim"
    TeamId = None
    Reason = "test"
    OverridePolicy = None
}

[<Tests>]
let authorizationTests =
    testList "Phase 229 — DSR Platform-Admin authorization" [

        testCaseAsync "non-admin RequestExport is denied"
        <| async {
            let api = mkApi nonAdmin

            match! api.RequestExport exportInput with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin export is denied"
            | Ok _ -> failtest "expected a non-admin export to be denied"
        }

        testCaseAsync "non-admin PreviewErasure is denied"
        <| async {
            let api = mkApi nonAdmin

            match! api.PreviewErasure eraseInput with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin preview is denied"
            | Ok _ -> failtest "expected a non-admin preview to be denied"
        }

        testCaseAsync "non-admin ConfirmErasure is denied (before the preview-cache lookup)"
        <| async {
            let api = mkApi nonAdmin

            match! api.ConfirmErasure "any-request-id" with
            | Error msg -> Expect.equal msg "platform admin role required" "non-admin confirm is denied"
            | Ok _ -> failtest "expected a non-admin confirm to be denied"
        }

        testCaseAsync "admin RequestExport passes the gate"
        <| async {
            let api = mkApi admin

            // Empty exporters → an Ok empty-segment envelope, NOT the gate error.
            match! api.RequestExport exportInput with
            | Ok _ -> ()
            | Error msg -> Expect.notEqual msg "platform admin role required" "an admin must pass the gate"
        }
    ]
// ─── Erasure preview → confirm through the mounted route ─────────────
//
// The route builds a fresh `IDataSubjectRequestApi` per HTTP request, so a
// preview made by one request must still be there when a later request
// confirms it. These cases drive the REAL composed router
// (`BuildRouteHandlers.buildRouteHandlers`) over a test host: each POST is
// its own request and its own handler instance, exactly as a deployment
// serves them. A test that called both members on one handler value would
// pass whether or not the previews outlive the request.

module private RouteHost =
    open System.Net.Http
    open System.Text
    open System.Text.Json
    open Microsoft.AspNetCore.Builder
    open Microsoft.AspNetCore.Hosting
    open Microsoft.AspNetCore.Http
    open Microsoft.AspNetCore.TestHost
    open Microsoft.Extensions.DependencyInjection
    open Microsoft.Extensions.Hosting
    open Giraffe
    open ToolUp.Platform.Config

    /// The header a test request names its team scope by; the scoped
    /// `AccessContext` factory turns it into a Platform-Admin team member.
    let teamHeader = "x-test-team"

    let private silentLogger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    let private routes () : HttpHandler list =
        let config = {
            ServerConfig.defaults with
                DataSubjectRequests =
                    DataSubjectRequestMode.Enabled(DataSubjectRequestConfig.sync ErasurePolicy.Tombstone)
        }

        let handlers =
            BuildRouteHandlers.buildRouteHandlers config [] [] ComposeExtensions.empty [] None NoNotifications None None

        [ handlers.Router [] ]

    /// A signed-in Platform Admin working in `team`: the principal the
    /// attribute gate and the in-handler gate both admit.
    let private adminInTeam (team: string) : AccessContext = {
        AccessContexts.forSubject (TeamMember("admin-user", team)) with
            PlatformRole = Some PlatformRole.PlatformAdmin
            User = {
                UserId = UserId "admin-user"
                DisplayName = "Platform Admin"
                Email = Some "admin@example.test"
                TenantId = None
                Roles = []
                DirectoryRoles = []
                Admission = ToolUp.Platform.Auth.PrincipalAdmission.Member
            }
    }

    /// One host over the composed router; `post team path body` sends one
    /// request under `team` and answers the status and the response body.
    let run (body: (string -> string -> string -> Async<int * string>) -> Async<unit>) = async {
        use host =
            Host
                .CreateDefaultBuilder()
                .ConfigureWebHostDefaults(fun webHost ->
                    webHost
                        .UseTestServer()
                        .ConfigureServices(fun services ->
                            services.AddGiraffe() |> ignore
                            services.AddHttpContextAccessor() |> ignore
                            services.AddSingleton<ILogger>(silentLogger) |> ignore
                            services.AddSingleton<IAuditLog>(NoOpAuditLog()) |> ignore

                            // As the real pipeline does: the request's principal is
                            // stamped on its items, and the scoped factory reads it.
                            services.AddScoped<AccessContext>(fun sp ->
                                let ctx = sp.GetRequiredService<IHttpContextAccessor>().HttpContext
                                AccessContext.ofItems ctx.Items |> Option.toObj)
                            |> ignore)
                        .Configure(fun (app: IApplicationBuilder) ->
                            app
                                .Use(fun (ctx: HttpContext) (next: RequestDelegate) ->
                                    AccessContext.stamp
                                        ctx.Items
                                        (adminInTeam (string ctx.Request.Headers[teamHeader]))

                                    next.Invoke ctx)
                                .UseGiraffe(choose (routes ())))
                    |> ignore)
                .Build()

        do! host.StartAsync() |> Async.AwaitTask
        use client = host.GetTestClient()

        let post (team: string) (path: string) (json: string) = async {
            use content = new StringContent(json, Encoding.UTF8, "application/json")
            use request = new HttpRequestMessage(HttpMethod.Post, path, Content = content)
            request.Headers.Add(teamHeader, team)
            use! resp = client.SendAsync request |> Async.AwaitTask
            let! text = resp.Content.ReadAsStringAsync() |> Async.AwaitTask
            return int resp.StatusCode, text
        }

        try
            do! body post
        finally
            host.StopAsync() |> Async.AwaitTask |> Async.RunSynchronously
    }

    let previewPath = "/api/IDataSubjectRequestApi/PreviewErasure"
    let confirmPath = "/api/IDataSubjectRequestApi/ConfirmErasure"

    let previewBody =
        """[{"SubjectUserId":"victim","TeamId":null,"Reason":"route test","OverridePolicy":null}]"""

    /// The request id of a `PreviewErasure` response (`{"Ok":{"Request":{"Id":…}}}`).
    let previewId (json: string) : string =
        use doc = JsonDocument.Parse json
        doc.RootElement.GetProperty("Ok").GetProperty("Request").GetProperty("Id").GetString()

    /// The `ErasureRunResult` case a `ConfirmErasure` response carries
    /// (`{"Ok":{"Completed":…}}` / `{"Ok":{"Refused":"…"}}`), with its body.
    let confirmCase (json: string) : string * string =
        use doc = JsonDocument.Parse json
        let ok = doc.RootElement.GetProperty "Ok"

        match ok.ValueKind with
        | JsonValueKind.Object ->
            let p = ok.EnumerateObject() |> Seq.head
            p.Name, p.Value.GetRawText()
        | _ -> ok.GetString(), ""

[<Tests>]
let routeTests =
    testList "DSR erasure preview and confirm through the mounted route" [

        testCaseAsync "a preview made by one request is confirmed by a later one"
        <| RouteHost.run (fun post -> async {
            let! previewStatus, previewJson = post "team-a" RouteHost.previewPath RouteHost.previewBody
            Expect.equal previewStatus 200 $"preview answers 200: {previewJson}"
            let id = RouteHost.previewId previewJson

            let! confirmStatus, confirmJson = post "team-a" RouteHost.confirmPath $"[\"{id}\"]"
            Expect.equal confirmStatus 200 $"confirm answers 200: {confirmJson}"
            let case, detail = RouteHost.confirmCase confirmJson
            Expect.equal case "Completed" $"the confirm runs the previewed erasure, got {case} {detail}"

            // One-shot: the confirmed preview is gone, so a second confirm is refused.
            let! _, againJson = post "team-a" RouteHost.confirmPath $"[\"{id}\"]"
            let againCase, _ = RouteHost.confirmCase againJson
            Expect.equal againCase "Refused" "a confirmed preview cannot be confirmed twice"
        })

        testCaseAsync "a preview made under one scope is refused when confirmed under another"
        <| RouteHost.run (fun post -> async {
            let! _, previewJson = post "team-a" RouteHost.previewPath RouteHost.previewBody
            let id = RouteHost.previewId previewJson

            let! _, crossJson = post "team-b" RouteHost.confirmPath $"[\"{id}\"]"
            let crossCase, _ = RouteHost.confirmCase crossJson
            Expect.equal crossCase "Refused" "scope B cannot confirm scope A's preview"

            // The refusal under B leaves A's preview in place.
            let! _, ownJson = post "team-a" RouteHost.confirmPath $"[\"{id}\"]"
            let ownCase, detail = RouteHost.confirmCase ownJson
            Expect.equal ownCase "Completed" $"scope A still confirms its own preview, got {ownCase} {detail}"
        })
    ]

// ─── The pending-preview holder: scope, expiry, removal ──────────────

let private adminInTeam (team: string) : AccessContext = {
    AccessContexts.teamMember "admin-user" team with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

/// A handler over `previews` in `scope` with `erasers` registered, built the
/// way the route builds one.
let private apiWith
    (erasers: IDataExporter.IErasureHandler list)
    (previews: DataSubjectRequestApiHandler.PendingErasurePreviews)
    (scope: string)
    =
    let access = adminInTeam scope

    DataSubjectRequestApiHandler.createWithPreviews
        previews
        []
        erasers
        ErasurePolicy.Tombstone
        scope
        access.UserId
        access
        DataSubjectRequestApiHandler.noOpAuditCallback
        None

/// A handler over `previews` in `scope`, with no erasure handler registered.
let private apiOver previews scope = apiWith [] previews scope

/// An erasure handler that counts its runs, takes `hold` over each, and
/// throws on its first `failFirst` runs.
type private CountingEraser(hold: System.TimeSpan, failFirst: int) =
    let mutable runs = 0

    member _.Runs = runs

    interface IDataExporter.IErasureHandler with
        member _.Name = "counting"

        member _.Erase(_, _, _) = async {
            let run = System.Threading.Interlocked.Increment &runs
            do! Async.Sleep(int hold.TotalMilliseconds)

            if run <= failFirst then
                failwith "the store went away"

            return
                Ok {
                    HandlerName = "counting"
                    RecordsAffected = 1
                    Note = None
                }
        }

        member _.Preview(_, _, _) = async {
            return {
                HandlerName = "counting"
                RecordsAffected = 1
                Note = None
            }
        }

let private previewOn (api: IDataSubjectRequestApi) = async {
    match! api.PreviewErasure eraseInput with
    | Ok preview -> return preview.Request.Id
    | Error e -> return failtestf "preview must succeed; got %s" e
}

let private confirmCaseOn (api: IDataSubjectRequestApi) (id: string) = async {
    match! api.ConfirmErasure id with
    | Ok(Completed _) -> return "Completed"
    | Ok(Refused _) -> return "Refused"
    | Ok(NotImplemented _) -> return "NotImplemented"
    | Error e -> return failtestf "confirm must answer Ok; got %s" e
}

[<Tests>]
let previewHolderTests =
    testList "DSR pending erasure previews" [

        testCaseAsync "a preview expires after its lifetime, read on an injected clock"
        <| async {
            let mutable now = System.DateTimeOffset(2026, 11, 28, 9, 0, 0, System.TimeSpan.Zero)
            let ttl = System.TimeSpan.FromMinutes 30.0

            let previews =
                DataSubjectRequestApiHandler.PendingErasurePreviews(ttl, (fun () -> now))

            let! kept = previewOn (apiOver previews "team-a")
            let! lapsed = previewOn (apiOver previews "team-a")

            now <- now + ttl - System.TimeSpan.FromSeconds 1.0
            let! keptCase = confirmCaseOn (apiOver previews "team-a") kept
            Expect.equal keptCase "Completed" "a preview inside its lifetime is confirmed"

            now <- now + System.TimeSpan.FromSeconds 1.0
            let! lapsedCase = confirmCaseOn (apiOver previews "team-a") lapsed
            Expect.equal lapsedCase "Refused" "a preview at the end of its lifetime is refused"
            Expect.equal previews.Count 0 "the expired preview is removed when read"
        }

        test "every preview sweeps the expired ones, so the holder does not grow without bound" {
            let mutable now = System.DateTimeOffset(2026, 11, 28, 9, 0, 0, System.TimeSpan.Zero)
            let ttl = System.TimeSpan.FromMinutes 30.0

            let previews =
                DataSubjectRequestApiHandler.PendingErasurePreviews(ttl, (fun () -> now))

            let api = apiOver previews "team-a"

            for _ in 1..5 do
                previewOn api |> Async.RunSynchronously |> ignore

            Expect.equal previews.Count 5 "five previews wait"
            now <- now + ttl
            previewOn api |> Async.RunSynchronously |> ignore
            Expect.equal previews.Count 1 "the five lapsed previews were swept by the sixth"
        }

        testCaseAsync "a preview made under scope A is refused under scope B, and stays for A"
        <| async {
            let previews = DataSubjectRequestApiHandler.PendingErasurePreviews()
            let! id = previewOn (apiOver previews "team-a")

            let! crossCase = confirmCaseOn (apiOver previews "team-b") id
            Expect.equal crossCase "Refused" "scope B cannot confirm scope A's preview"

            let! ownCase = confirmCaseOn (apiOver previews "team-a") id
            Expect.equal ownCase "Completed" "scope A still confirms it"
            Expect.equal previews.Count 0 "a confirmed preview is removed"
        }

        testCaseAsync "a non-admin cannot confirm a preview an admin made in the same scope"
        <| async {
            let previews = DataSubjectRequestApiHandler.PendingErasurePreviews()
            let! id = previewOn (apiOver previews "team-a")
            let member' = AccessContexts.teamMember "member-user" "team-a"

            let api =
                DataSubjectRequestApiHandler.createWithPreviews
                    previews
                    []
                    []
                    ErasurePolicy.Tombstone
                    "team-a"
                    member'.UserId
                    member'
                    DataSubjectRequestApiHandler.noOpAuditCallback
                    None

            match! api.ConfirmErasure id with
            | Error msg -> Expect.equal msg "platform admin role required" "a non-admin confirm is denied"
            | Ok outcome -> failtestf "a non-admin confirm must be denied; got %A" outcome

            Expect.equal previews.Count 1 "the denied confirm leaves the preview in place"
        }

        testCaseAsync "two confirms of one preview arriving together run the erasure once"
        <| async {
            let previews = DataSubjectRequestApiHandler.PendingErasurePreviews()
            let eraser = CountingEraser(System.TimeSpan.FromMilliseconds 300.0, 0)
            let api = apiWith [ eraser ] previews "team-a"
            let! id = previewOn api

            let! cases = Async.Parallel [ confirmCaseOn api id; confirmCaseOn api id ]

            Expect.equal eraser.Runs 1 "the erasure ran once"

            Expect.equal
                (List.sort (List.ofArray cases))
                [ "Completed"; "Refused" ]
                "one confirm runs it, the other is refused as an absent preview is"

            Expect.equal previews.Count 0 "the confirmed preview is removed"
        }

        testCaseAsync "a confirm whose erasure throws leaves the preview confirmable"
        <| async {
            let previews = DataSubjectRequestApiHandler.PendingErasurePreviews()
            let eraser = CountingEraser(System.TimeSpan.Zero, 1)
            let api = apiWith [ eraser ] previews "team-a"
            let! id = previewOn api

            match! api.ConfirmErasure id with
            | Error msg -> Expect.equal msg "the store went away" "the thrown erasure answers its error"
            | Ok outcome -> failtestf "the first confirm's erasure throws; got %A" outcome

            Expect.equal previews.Count 1 "the preview waits for a retry"

            let! retried = confirmCaseOn api id
            Expect.equal retried "Completed" "the admin's retry confirms the same preview"
            Expect.equal eraser.Runs 2 "the retry ran the erasure again"
            Expect.equal previews.Count 0 "the confirmed preview is removed"
        }
    ]