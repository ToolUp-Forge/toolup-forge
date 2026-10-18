module ToolUp.Platform.Tests.InProcess.AuditFireAndForgetTests

// Phase 1024 — a fire-and-forget write started by `ScopeResolutionMiddleware`
// never faults unobserved: it is caught, the failure is logged, and the
// response is the one a healthy sink produces.
//
// Each write is driven through the REAL middleware, once against a healthy
// sink (the control) and once against a sink that fails in one of three
// ways:
//
//   * `Plain` — the write fails inside plain async code. Started with a bare
//     `Async.Start`, the exception is rethrown on a thread-pool thread and
//     TERMINATES THE HOST, so this arm can only go red in a child process.
//   * `TaskShaped` — the write fails behind a task boundary
//     (`Async.AwaitTask`). Started bare, it surfaces as an unobserved task
//     exception after a collection; that is what this arm asserts.
//   * `Synchronous` — `Record` throws before an `Async` exists. Unguarded on
//     the login path it fell through to the anonymous fallback and changed
//     the subject the request resolved to.
//
// For every arm: the status and the resolved subject equal the control's,
// no unobserved task exception carries the arm's marker, and exactly one
// Warning names the write and the exception type.

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.Support
open ToolUp.Platform.Tests.Support.FireAndForgetHarness

type AuditSinkDownException(marker: string) =
    inherit Exception(marker)

let private failWith mode marker =
    FireAndForgetHarness.failWith (fun m -> AuditSinkDownException m :> exn) mode marker

let private healthyAuditLog () =
    let rows = ConcurrentQueue<AuditEvent>()

    rows,
    { new IAuditLog with
        member _.Record(_, event) = async { rows.Enqueue event }
        member _.GetAuditTrail(_, _, _) = async { return [] }
    }

let private failingAuditLog (mode: FailureMode) (marker: string) =
    { new IAuditLog with
        member _.Record(_, _) = failWith mode marker
        member _.GetAuditTrail(_, _, _) = async { return [] }
    }

let private pendingStore (consume: unit -> Async<Result<PendingInviteByEmail option, PendingInviteStoreError>>) =
    { new IPendingInviteStore with
        member _.Upsert(_, _) = async { return Ok() }
        member _.Remove _ = async { return Ok() }
        member _.TryConsumeForEmail _ = consume ()
        member _.ListAll() = async { return Ok [] }
        member _.SweepExpired() = async { return Ok 0 }
    }

let private warningLogger () =
    let warnings = ConcurrentQueue<string>()

    warnings,
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn message = warnings.Enqueue message
        member _.Error(_, _) = ()
    }

let private memberUser: AuthenticatedUser = {
    AuthenticatedUser.anonymous with
        UserId = "u-1024"
        DisplayName = "Member"
        Email = Some "member@example.test"
}

/// One of the request-bound writes, with the request that reaches it.
type private Site = {
    /// The write's name, as the Warning names it.
    Write: string
    Path: string
    Resolve: unit -> Async<Result<Subject, SubjectResolutionError>>
    /// Whether the failure is injected through the pending-invite store
    /// (the invite consumption) rather than the audit sink.
    ThroughInvites: bool
}

let private sites: Site list = [
    {
        Write = "scope-downgrade audit"
        // A page route: the downgrade is reported there too, and no anonymous
        // binding cookie is issued.
        Path = "/p/report"
        Resolve = fun () -> async { return Error(SubjectResolutionError.NotTeamMember "team-gone") }
        ThroughInvites = false
    }
    {
        Write = "reader sign-in audit"
        Path = "/api/x"
        Resolve = fun () -> async { return Ok(PublicationReader "reader-1024") }
        ThroughInvites = false
    }
    {
        Write = "login audit"
        Path = "/api/x"
        Resolve = fun () -> async { return Ok(Subject.AuthenticatedUser memberUser.UserId) }
        ThroughInvites = false
    }
    {
        Write = "invite consumption"
        Path = "/api/x"
        Resolve = fun () -> async { return Ok(Subject.AuthenticatedUser memberUser.UserId) }
        ThroughInvites = true
    }
    {
        Write = "scope-resolution-failure audit"
        Path = "/api/x"
        // A resolver that throws reaches the A1 catch and its audit row.
        Resolve = fun () -> raise (InvalidOperationException "resolver down")
        ThroughInvites = false
    }
]

/// Run the real middleware once. `auditLog` is the sink; `invites`, when
/// given, is composed with a team store so the invite consumption runs.
let private run (site: Site) (auditLog: IAuditLog) (invites: IPendingInviteStore option) = async {
    let warnings, logger = warningLogger ()
    let services = ServiceCollection()
    services.AddMemoryCache() |> ignore
    services.AddSingleton<ILogger>(logger) |> ignore
    services.AddSingleton<IAuditLog>(auditLog) |> ignore

    services.AddSingleton<IAuthProvider>(
        { new IAuthProvider with
            member _.GetUser _ = async { return memberUser }
            member _.ValidateRequest _ = async { return Ok memberUser }
            member _.IsCryptographicallyVerified = true
        }
    )
    |> ignore

    services.AddSingleton<ISubjectResolver>(
        { new ISubjectResolver with
            member _.Resolve _ = site.Resolve()
        }
    )
    |> ignore

    invites
    |> Option.iter (fun store ->
        services.AddSingleton<IPendingInviteStore>(store) |> ignore

        services.AddSingleton<ITeamStore>(TeamStore(InMemoryBlobStorage(), NoOpNotificationChannel()))
        |> ignore)

    // Disposed when the request ends, exactly as the request's own services
    // are: a write that fails afterwards finds them gone.
    use provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext(RequestServices = provider)
    ctx.Request.Method <- "GET"
    ctx.Request.Path <- PathString site.Path

    let next =
        RequestDelegate(fun c ->
            c.Response.StatusCode <- 204
            Task.CompletedTask)

    let middleware = Middleware.ScopeResolutionMiddleware(next, ServerConfig.defaults)
    do! middleware.InvokeAsync ctx |> Async.AwaitTask

    let subject =
        match ctx.Items.TryGetValue "ToolUp.Subject" with
        | true, (:? Subject as s) ->
            match s with
            // A fresh anonymous session id per request; its shape is the claim.
            | AnonymousSession _ -> "AnonymousSession"
            | other -> sprintf "%A" other
        | _ -> "(none)"

    return ctx.Response.StatusCode, subject, warnings
}

let private failureWarnings (site: Site) (warnings: ConcurrentQueue<string>) =
    warnings.ToArray()
    |> List.ofArray
    |> List.filter (fun w -> w.Contains(sprintf "the %s write failed" site.Write))

let private arm (site: Site) (mode: FailureMode) =
    testCaseAsync (sprintf "%s: a %A failure is caught, logged once, and changes nothing" site.Write mode)
    <| async {
        watchUnobserved ()
        let marker = sprintf "sink-down-%s" (Guid.NewGuid().ToString "N")

        let _, healthy = healthyAuditLog ()
        let healthyInvites = pendingStore (fun () -> async { return Ok None })

        let! controlStatus, controlSubject, _ =
            run site healthy (if site.ThroughInvites then Some healthyInvites else None)

        let auditLog, invites =
            if site.ThroughInvites then
                healthy, Some(pendingStore (fun () -> failWith mode marker))
            else
                failingAuditLog mode marker, None

        let! status, subject, warnings = run site auditLog invites

        Expect.equal status controlStatus "the response status is the healthy sink's"
        Expect.equal subject controlSubject "the request resolves to the healthy sink's subject"

        let! logged = settleOn 60 (fun () -> failureWarnings site warnings)
        let! leaked = leakedFor marker
        Expect.isEmpty leaked "no unobserved task exception carries this write's failure"

        match logged with
        | [ warning ] ->
            Expect.stringContains warning "AuditSinkDownException" "the Warning names the exception type"
            Expect.stringContains warning "ScopeResolutionMiddleware" "the Warning names its source"
        | other -> failtestf "expected exactly one Warning naming the %s write, got %A" site.Write other
    }

let tests =
    testList "Phase 1024 — a fire-and-forget audit write never faults unobserved" [
        for site in sites do
            for mode in [ Plain; TaskShaped; Synchronous ] do
                arm site mode
    ]