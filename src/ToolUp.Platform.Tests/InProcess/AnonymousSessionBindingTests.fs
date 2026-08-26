module ToolUp.Platform.Tests.InProcess.AnonymousSessionBindingTests

open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Caching.Memory
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.NotificationChannel

// ─── Phase 337 — signed anonymous-session binding ────────────────────
//
// Phase 135 bound the anonymous session id to the browser and gated the
// anonymous→authenticated MIGRATION on that binding. It did not close
// the scope-selection leg: `DefaultSubjectResolver` still built
// `Subject.AnonymousSession` from the self-asserted `X-User-Id` header,
// so a caller could address any anonymous session's storage scope simply
// by naming it — the migration gate protected the lift, not the data.
//
// Nor could trust-on-first-use have closed it. Minting a binding for
// whatever id an unbound browser asserts hands the attacker a valid
// binding for the victim's id on request one, which is precisely the
// shape Phase 135 shipped.
//
// Phase 337 inverts the direction of trust: the sealed cookie CARRIES
// the id, and `X-User-Id` can only echo it. These tests pin both halves
// — that a claimed id never selects a scope, and that a first-time
// visitor still gets a stable session across requests (GP 11), because
// a fix that broke anonymous continuity would be reverted rather than
// kept.

let private spWithDataProtection () =
    let services = ServiceCollection()
    services.AddDataProtection() |> ignore
    services.AddMemoryCache() |> ignore
    services.BuildServiceProvider()

let private ctxWith (sp: System.IServiceProvider) =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

/// Lenient anonymous auth provider — no credentials presented, so the
/// four-step algorithm falls to step 3 (the anonymous branch) which is
/// the branch under test.
let private anonymousAuth () : IAuthProvider =
    let user = AuthenticatedUser.anonymous

    { new IAuthProvider with
        member _.GetUser(_ctx) = async { return user }
        member _.ValidateRequest(_ctx) = async { return Result.Ok user }
        member _.IsCryptographicallyVerified = false
    }

let private anonymousResolver () : ISubjectResolver =
    let cache = new MemoryCache(MemoryCacheOptions()) :> IMemoryCache
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel

    new DefaultSubjectResolver.DefaultSubjectResolver(Surfaces.anonymous, None, cache, notifications)
    :> ISubjectResolver

/// Attach a binding cookie to the request, the way a browser would.
let private presentCookie (ctx: HttpContext) (token: string) =
    ctx.Request.Headers["Cookie"] <- StringValues(AnonymousSessionBinding.CookieName + "=" + token)

/// The binding token a response asked the browser to store, recovered
/// from `Set-Cookie` — so a test can carry it into the next request the
/// way a real client does, rather than minting one behind the server's
/// back and proving nothing about what the server actually issued.
let private issuedToken (ctx: HttpContext) : string option =
    ctx.Response.Headers["Set-Cookie"]
    |> Seq.cast<string>
    |> Seq.tryPick (fun header ->
        let prefix = AnonymousSessionBinding.CookieName + "="

        if header.StartsWith prefix then
            header.Substring(prefix.Length).Split(';')[0] |> Some
        else
            None)

/// Resolve the subject for one request, exactly as
/// `ScopeResolutionMiddleware` does: extract, then resolve.
let private resolveSubject (ctx: HttpContext) : Subject =
    let request =
        Middleware.SubjectRequestExtractor.fromHttpContext ctx (anonymousAuth ())
        |> Async.RunSynchronously

    match (anonymousResolver ()).Resolve request |> Async.RunSynchronously with
    | Ok subject -> subject
    | Error err -> failtestf "expected a resolved subject, got %A" err

let private sessionIdOf (subject: Subject) : string =
    match subject with
    | AnonymousSession sid -> sid
    | other -> failtestf "expected AnonymousSession, got %A" other

let private scopeSelectionTests =
    testList "scope selection" [
        test "a forged session id does NOT select that session's scope" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            // The attack: assert the victim's (non-secret) session id and
            // present no binding. Before Phase 337 this resolved to
            // `AnonymousSession "victim-session"` — the victim's scope.
            ctx.Request.Headers["X-User-Id"] <- StringValues "victim-session"

            let sid = resolveSubject ctx |> sessionIdOf

            Expect.notEqual sid "victim-session" "an unsigned claimed id must not select its scope"
            Expect.isNotEmpty sid "a fresh session is minted in its place"
        }

        test "a validly-signed session id round-trips" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "owner-session").Value
            presentCookie ctx token

            let sid = resolveSubject ctx |> sessionIdOf

            Expect.equal sid "owner-session" "the server-issued id selects its own scope"
        }

        test "the sealed cookie beats a mismatching X-User-Id claim" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "own-session").Value
            presentCookie ctx token
            // Holding a legitimate session of their own does not let a
            // caller reach for someone else's by asserting its id.
            ctx.Request.Headers["X-User-Id"] <- StringValues "victim-session"

            let sid = resolveSubject ctx |> sessionIdOf

            Expect.equal sid "own-session" "the server-issued binding is authoritative, the header an echo"
        }

        test "a tampered binding cookie falls back to a fresh session, not the claimed one" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            presentCookie ctx "not.a.valid.seal"
            ctx.Request.Headers["X-User-Id"] <- StringValues "victim-session"

            let sid = resolveSubject ctx |> sessionIdOf

            Expect.notEqual sid "victim-session" "a broken seal is not a licence to trust the header"
            Expect.isNotEmpty sid "fail closed onto a fresh session"
        }

        test "a session sealed for one browser does not verify for another id (replay)" {
            let sp = spWithDataProtection ()
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "session-A").Value
            let ctx = ctxWith sp
            presentCookie ctx token

            // `boundSessionId` returns what was sealed, never what was asked
            // for — so a replayed cookie addresses only its own session.
            Expect.equal
                (AnonymousSessionBinding.boundSessionId ctx)
                (Some "session-A")
                "the seal names its own session and no other"
        }
    ]

let private freshVisitorTests =
    testList "fresh visitor (GP 11)" [
        test "a first-time visitor is issued a session and keeps it on the next request" {
            let sp = spWithDataProtection ()

            // Request 1 — nothing presented at all.
            let first = ctxWith sp
            let firstSid = resolveSubject first |> sessionIdOf
            Expect.isNotEmpty firstSid "a first-time visitor gets a session"

            // `ScopeResolutionMiddleware` seals whatever the request
            // resolved to; do the same here.
            AnonymousSessionBinding.ensureBound first firstSid

            match issuedToken first with
            | None -> failtest "expected the response to issue a binding cookie"
            | Some token ->
                // Request 2 — the browser presents what it was given.
                let second = ctxWith sp
                presentCookie second token
                let secondSid = resolveSubject second |> sessionIdOf

                Expect.equal secondSid firstSid "the anonymous session is continuous across requests"
        }

        test "ensureBound is a no-op when the browser is already bound" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "steady-session").Value
            presentCookie ctx token

            AnonymousSessionBinding.ensureBound ctx "steady-session"

            Expect.isNone (issuedToken ctx) "a steady-state anonymous request emits no Set-Cookie"
        }

        test "issue mints a fresh id and seals it in one step" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp

            let sid = AnonymousSessionBinding.issue ctx
            Expect.isNotEmpty sid "an id is returned"

            match issuedToken ctx with
            | None -> failtest "expected a binding cookie"
            | Some token ->
                Expect.isTrue (AnonymousSessionBinding.verify ctx token sid) "the cookie seals the returned id"
        }
    ]

type private RecordingMigrator() =
    let calls = ResizeArray<string * Subject>()
    member _.Calls = calls

    interface IAnonymousSessionMigrator with
        member _.Migrate(sid, subject) = async {
            calls.Add((sid, subject))
            return Ok MigrationSummary.empty
        }

let private migrationTests =
    let spWithMigrator () =
        let services = ServiceCollection()
        services.AddDataProtection() |> ignore
        services.AddMemoryCache() |> ignore
        services.AddSingleton<IAnonymousSessionMigrator>(RecordingMigrator()) |> ignore
        services.BuildServiceProvider()

    /// Drive the migration middleware for an authenticated request.
    let invokeAuthenticated (sp: ServiceProvider) (uid: string) (header: string option) (cookie: string option) =
        let migrator =
            sp.GetService(typeof<IAnonymousSessionMigrator>) :?> RecordingMigrator

        let ctx = ctxWith sp
        ctx.Request.Path <- PathString "/api/data"
        ctx.Items["ToolUp.Subject"] <- box (AuthenticatedUser uid)

        match header with
        | Some h -> ctx.Request.Headers["X-User-Id"] <- StringValues h
        | None -> ()

        match cookie with
        | Some c -> presentCookie ctx c
        | None -> ()

        let next = RequestDelegate(fun _ -> Task.CompletedTask)
        let mw = AnonymousSessionMigration.AnonymousSessionMigrationMiddleware(next)
        mw.InvokeAsync ctx |> Async.AwaitTask |> Async.RunSynchronously
        migrator.Calls

    testList "migration consumes only a verified binding" [
        test "an unverified anon session id does not migrate" {
            let sp = spWithMigrator ()
            let calls = invokeAuthenticated sp "attacker" (Some "victim-anon-sid") None
            Expect.isEmpty calls "no seal → nothing to migrate"
        }

        test "the migrator receives the SEALED id, never the asserted header" {
            let sp = spWithMigrator ()
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "owner-sid").Value

            // The header asserts a different session from the one sealed.
            // Phase 135 read the header and re-checked the binding; Phase 337
            // reads the seal, so the header cannot steer the migrator at all.
            let calls = invokeAuthenticated sp "owner" (Some "attacker-sid") (Some token)

            Expect.equal calls.Count 1 "a sealed session migrates"
            let migratedSid, _ = calls[0]
            Expect.equal migratedSid "owner-sid" "the sealed id is what reaches the migrator"
        }

        test "a tampered binding cookie does not migrate" {
            let sp = spWithMigrator ()

            let calls =
                invokeAuthenticated sp "attacker" (Some "victim-anon-sid") (Some "not.a.valid.seal")

            Expect.isEmpty calls "a broken seal migrates nothing"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 337 — signed anonymous-session binding" [ scopeSelectionTests; freshVisitorTests; migrationTests ]