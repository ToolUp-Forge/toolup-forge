module ToolUp.Platform.Tests.InProcess.AnonymousSessionMigrationTests

open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Primitives
open ToolUp.Platform

// ─── Phase 135 — anonymous-session ownership binding ────────────────
//
// The migration must fire only when the browser cryptographically
// proves (via the server-issued HttpOnly binding cookie) that it owned
// the anonymous session — not merely that it knows the non-secret
// `X-User-Id` session id.

let private spWithDataProtection () =
    let services = ServiceCollection()
    services.AddDataProtection() |> ignore
    services.AddMemoryCache() |> ignore
    services.BuildServiceProvider()

let private ctxWith (sp: System.IServiceProvider) =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx

type private RecordingMigrator() =
    let calls = ResizeArray<string * Subject>()
    member _.Calls = calls

    interface IAnonymousSessionMigrator with
        member _.Migrate(sid, subject) = async {
            calls.Add((sid, subject))
            return Ok MigrationSummary.empty
        }

let private bindingTests =
    testList "AnonymousSessionBinding" [
        test "mint then verify round-trips for the same session id" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            let token = (AnonymousSessionBinding.mint ctx "sid-1").Value
            Expect.isTrue (AnonymousSessionBinding.verify ctx token "sid-1") "valid binding verifies"
        }

        test "a binding for session A does not verify session B (replay attack)" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            let token = (AnonymousSessionBinding.mint ctx "session-A").Value
            Expect.isFalse (AnonymousSessionBinding.verify ctx token "session-B") "binding is session-specific"
        }

        test "a tampered token fails verification" {
            let sp = spWithDataProtection ()
            let ctx = ctxWith sp
            Expect.isFalse (AnonymousSessionBinding.verify ctx "not.a.valid.seal" "sid-1") "tampered → false"
        }
    ]

let private migrationGateTests =
    let invokeAuthenticated
        (sp: System.IServiceProvider)
        (targetUid: string)
        (sessionId: string)
        (cookie: string option)
        =
        let migrator =
            sp.GetService(typeof<IAnonymousSessionMigrator>) :?> RecordingMigrator

        let ctx = ctxWith sp
        ctx.Request.Path <- PathString "/api/data"
        ctx.Items["ToolUp.Subject"] <- box (AuthenticatedUser targetUid)
        ctx.Request.Headers["X-User-Id"] <- StringValues sessionId

        match cookie with
        | Some c -> ctx.Request.Headers["Cookie"] <- StringValues(AnonymousSessionBinding.CookieName + "=" + c)
        | None -> ()

        let next = RequestDelegate(fun _ -> Task.CompletedTask)
        let mw = AnonymousSessionMigration.AnonymousSessionMigrationMiddleware(next)
        mw.InvokeAsync(ctx) |> Async.AwaitTask |> Async.RunSynchronously
        migrator.Calls

    testList "AnonymousSessionMigration gate" [
        test "attacker replaying a victim session id WITHOUT a binding cookie does not migrate" {
            let services = ServiceCollection()
            services.AddDataProtection() |> ignore
            services.AddMemoryCache() |> ignore
            services.AddSingleton<IAnonymousSessionMigrator>(RecordingMigrator()) |> ignore
            let sp = services.BuildServiceProvider()

            let calls = invokeAuthenticated sp "attacker" "victim-anon-sid" None
            Expect.isEmpty calls "no binding proof → no migration (horizontal-theft primitive closed)"
        }

        test "legitimate owner WITH a valid binding cookie migrates" {
            let services = ServiceCollection()
            services.AddDataProtection() |> ignore
            services.AddMemoryCache() |> ignore
            services.AddSingleton<IAnonymousSessionMigrator>(RecordingMigrator()) |> ignore
            let sp = services.BuildServiceProvider()

            // Mint a binding for the session id using the same key ring.
            let token = (AnonymousSessionBinding.mint (ctxWith sp) "victim-anon-sid").Value

            let calls = invokeAuthenticated sp "victim" "victim-anon-sid" (Some token)
            Expect.equal calls.Count 1 "valid binding → migration fires once"

            let migratedSid, _ = calls[0]
            Expect.equal migratedSid "victim-anon-sid" "migrates the bound session"
        }
    ]

[<Tests>]
let tests =
    testList "Phase 135 — anonymous-session ownership binding" [ bindingTests; migrationGateTests ]