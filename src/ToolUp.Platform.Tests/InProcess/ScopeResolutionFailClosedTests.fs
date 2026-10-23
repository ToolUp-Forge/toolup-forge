module ToolUp.Platform.Tests.InProcess.ScopeResolutionFailClosedTests

// Phase 1059 — a permission-store fault while `ScopeResolutionMiddleware`
// stamps a team member's permissions REFUSES the request.
//
// `PermissionStore` raises `PermissionStoreUnavailableException` for a
// present-but-unreadable document so that the request fails closed. Before
// this phase the middleware read the permissions inside its catch-all, AFTER
// the subject and the storage scope were stamped: the catch swallowed the
// raise, `ToolUp.ModulePermissions` was never written, the access-context
// factory read an empty map, and an empty map means "every module". The
// member reached a module its team's document restricts.
//
// Driven through the REAL middleware and the REAL access-context reading
// (`ComposeScopeResolver.accessContextOf`); the `next` delegate stands in for
// module M's handler and applies the per-route rule (`canAccessModule`).

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.PermissionStore

let private teamId = "team-1059"
let private moduleName = "M"

let private memberUser: AuthenticatedUser = {
    AuthenticatedUser.anonymous with
        UserId = "u-1059"
        DisplayName = "Member"
}

/// A permission store whose reads either raise (the fault) or return the
/// team's restrictive document (the control): module M is granted nothing,
/// another module is granted `Read`.
let private permissionStore (fault: exn option) =
    let restricted = Map.ofList [ moduleName, []; "Other", [ ModulePermission.Read ] ]

    let read (value: 'a) : Async<'a> = async {
        match fault with
        | Some ex -> return raise ex
        | None -> return value
    }

    { new IPermissionStore with
        member _.GetTeamPermissions _ = read TeamPermissions.empty
        member _.SetTeamPermissions(_, _) = async { return Ok() }
        member _.GetEffectivePermissions(_, _) = read restricted
        member _.SetMemberPermissions(_, _, _, _) = async { return Ok() }
        member _.SetTeamDefaults(_, _) = async { return Ok() }
        member _.GetModuleExposure _ = read Map.empty
        member _.SetModuleExposure(_, _, _) = async { return Ok() }
    }

/// Run the real middleware for a valid team member against `store`. Returns
/// the response status, whether M's handler was reached, and the audit rows.
let private run (store: IPermissionStore) = async {
    let rows = ConcurrentQueue<string * AuditEvent>()
    let services = ServiceCollection()
    services.AddMemoryCache() |> ignore
    services.AddSingleton<IPermissionStore>(store) |> ignore

    services.AddSingleton<IAuditLog>(
        { new IAuditLog with
            member _.Record(scopeId, event) = async { rows.Enqueue(scopeId, event) }
            member _.GetAuditTrail(_, _, _) = async { return [] }
        }
    )
    |> ignore

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
            member _.Resolve _ = async { return Ok(TeamMember(memberUser.UserId, teamId)) }
        }
    )
    |> ignore

    use provider = services.BuildServiceProvider()
    let ctx = DefaultHttpContext(RequestServices = provider)
    ctx.Request.Method <- "POST"
    ctx.Request.Path <- PathString "/api/M/DoThing"

    let reached = ref false

    let handler =
        RequestDelegate(fun c ->
            reached.Value <- true
            let access = ComposeScopeResolver.accessContextOf c

            c.Response.StatusCode <-
                if AccessContext.canAccessModule moduleName access then
                    200
                else
                    403

            Task.CompletedTask)

    let middleware =
        Middleware.ScopeResolutionMiddleware(handler, ServerConfig.defaults)

    do! middleware.InvokeAsync ctx |> Async.AwaitTask
    return ctx.Response.StatusCode, reached.Value, List.ofSeq rows
}

let private isResolutionFailure (_, event) =
    match event with
    | AuthScopeResolutionFailed _ -> true
    | _ -> false

let tests =
    testList "Phase 1059 — a permission-store fault refuses the request" [

        testCaseAsync "control: a readable restrictive document keeps the member out of M (403)"
        <| async {
            let! status, reached, _ = run (permissionStore None)
            Expect.isTrue reached "the handler runs for a healthy store"
            Expect.equal status 403 "M is restricted by the team's document"
        }

        testCaseAsync "a PermissionStoreUnavailableException refuses with 503 and never reaches M's handler"
        <| async {
            let fault = PermissionStoreUnavailableException "permissions document unreadable"
            let! status, reached, rows = run (permissionStore (Some fault))

            Expect.isFalse reached "a request whose permissions could not be read must not reach a module handler"
            Expect.equal status 503 "the refusal is a 503 — the store is unavailable, the caller is not unauthorised"

            match rows |> List.filter isResolutionFailure with
            | [ _, AuthScopeResolutionFailed payload ] ->
                Expect.equal payload.ExceptionKind "PermissionStoreUnavailableException" "the row names the fault"
                Expect.stringContains payload.Message "IPermissionStore" "the row names the store"
            | other -> failtestf "expected exactly one ScopeResolutionFailed row, got %A" other
        }

        testCaseAsync "any IPermissionStore fault refuses, not only the typed one"
        <| async {
            let! status, reached, _ = run (permissionStore (Some(InvalidOperationException "store down")))
            Expect.isFalse reached "an untyped store fault is still a fault"
            Expect.equal status 503 "refused"
        }
    ]