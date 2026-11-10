module ToolUp.Platform.Tests.InProcess.ScopeResolutionFailClosedTests

// Phase 1059 — a permission-store fault while `ScopeResolutionMiddleware`
// stamps a team member's permissions REFUSES the request.
//
// `PermissionStore` answers a `PermissionStoreError` for a
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
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

let private teamId = "team-1059"
let private moduleName = "M"

let private memberUser: AuthenticatedUser = {
    AuthenticatedUser.anonymous with
        UserId = UserId "u-1059"
        DisplayName = "Member"
}

/// How a faulting store fails: it answers the fault as data (the contract),
/// or it raises (a custom store that does not honour it).
type private Fault =
    | Answers of PermissionStoreError
    | Raises of exn

/// A permission store whose reads either fail (the fault) or return the
/// team's restrictive document (the control): module M is granted nothing,
/// another module is granted `Read`.
let private permissionStore (fault: Fault option) =
    let restricted = Map.ofList [ moduleName, []; "Other", [ ModulePermission.Read ] ]

    let read (value: 'a) : Async<Result<'a, PermissionStoreError>> = async {
        match fault with
        | Some(Answers e) -> return Error e
        | Some(Raises ex) -> return raise ex
        | None -> return Ok value
    }

    { new IPermissionStore with
        member _.GetTeamPermissions _ = read TeamPermissions.empty
        member _.SetTeamPermissions(_, _) = async { return Ok() }
        member _.UpdateTeamPermissions(_, _) = async { return Ok() }

        member _.GetEffectivePermissions(_, _) =
            read (ModulePermissions.Restricted restricted)

        member _.SetMemberPermissions(_, _, _, _) = async { return Ok() }
        member _.SetTeamDefaults(_, _) = async { return Ok() }
        member _.GetModuleExposure _ = read Map.empty
        member _.SetModuleExposure(_, _, _) = async { return Ok() }
    }

/// Run the real middleware for a valid team member against `store`. Returns
/// the response status, whether M's handler was reached, and the audit rows.
let private runWithAdmin (store: IPermissionStore) (adminFault: exn option) (onReached: HttpContext -> unit) = async {
    let rows = ConcurrentQueue<string * AuditEvent>()
    let services = ServiceCollection()
    services.AddMemoryCache() |> ignore
    services.AddSingleton<IPermissionStore>(store) |> ignore

    match adminFault with
    | Some ex ->
        services.AddSingleton<IPlatformAdminStore>(
            { new IPlatformAdminStore with
                member _.IsPlatformAdmin _ = async { return raise ex }
                member _.AssignPlatformAdmin(_, _) = async { return Ok() }
                member _.RevokePlatformAdmin(_, _) = async { return Ok() }
                member _.ListPlatformAdmins() = async { return [] }
            }
        )
        |> ignore
    | None -> ()

    services.AddSingleton<IAuditLog>(
        { new IAuditLog with
            member _.Record(AuditScope.StreamKey scopeId, _actor, event) = async {
                rows.Enqueue(scopeId, event)
                return Ok()
            }

            member _.GetAuditTrail(AuditScope.StreamKey _, _, _) = async { return [] }
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
            member _.Resolve _ = async { return Ok(TeamMember(memberUser.UserId.Value, teamId)) }
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
            onReached c

            let access =
                ComposeScopeResolver.accessContextOf c
                |> Option.defaultWith (fun () -> AccessContext.unresolved "ScopeResolutionFailClosedTests")

            c.Response.StatusCode <-
                if AccessContext.canAccessModule moduleName access then
                    200
                else
                    403

            Task.CompletedTask)

    let middleware =
        Middleware.ScopeResolutionMiddleware(handler, ServerConfig.defaults)

    do! middleware.InvokeAsync ctx |> Async.AwaitTask
    return ctx.Response.StatusCode, reached.Value, rows
}

let private runWith (store: IPermissionStore) (onReached: HttpContext -> unit) = runWithAdmin store None onReached

let private run (store: IPermissionStore) = runWith store ignore

/// Poll `probe` until it finds something (or 3 s pass).
let private settleOn (attempts: int) (probe: unit -> 'a list) = async {
    let rec go attempt = async {
        match probe () with
        | [] when attempt < attempts ->
            do! Async.Sleep 50
            return! go (attempt + 1)
        | found -> return found
    }

    return! go 0
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

        testCaseAsync "an unreadable permission document refuses with 503 and never reaches M's handler"
        <| async {
            let fault =
                Answers(PermissionStoreError.Unreadable "permissions document unreadable")

            let! status, reached, rows = run (permissionStore (Some fault))

            Expect.isFalse reached "a request whose permissions could not be read must not reach a module handler"
            Expect.equal status 503 "the refusal is a 503 — the store is unavailable, the caller is not unauthorised"

            // The row is fire-and-forget (Phase 1024): wait for it to land.
            let! failures = settleOn 60 (fun () -> List.ofSeq rows |> List.filter isResolutionFailure)

            match failures with
            | [ _, AuthScopeResolutionFailed payload ] ->
                Expect.equal payload.ExceptionKind "PermissionReadRefused" "the row names the fault"
                Expect.stringContains payload.Message "IPermissionStore" "the row names the store"
            | other -> failtestf "expected exactly one ScopeResolutionFailed row, got %A" other
        }

        testCaseAsync "any IPermissionStore fault refuses, not only the typed one"
        <| async {
            let! status, reached, _ = run (permissionStore (Some(Raises(InvalidOperationException "store down"))))
            Expect.isFalse reached "an untyped store fault is still a fault"
            Expect.equal status 503 "refused"
        }

        testCaseAsync "Phase 882 — the middleware leaves ONE item: the principal, carrying the user, subject and scope"
        <| async {
            let seen = ref None

            let! _, reached, _ =
                runWith (permissionStore None) (fun c ->
                    seen.Value <- Some(AccessContext.ofItems c.Items, List.ofSeq c.Items.Keys))

            Expect.isTrue reached "the handler runs for a healthy store"

            match seen.Value with
            | Some(Some access, keys) ->
                Expect.equal access.User.UserId memberUser.UserId "the provider's user rides the principal"
                Expect.equal access.Subject (TeamMember(memberUser.UserId.Value, teamId)) "so does the subject"
                Expect.equal (AccessContext.callerId access) memberUser.UserId.Value "and the caller id"
                Expect.isSome access.StorageScope "and the scope"

                for retired in
                    [
                        "ToolUp.User"
                        "ToolUp.UserId"
                        "ToolUp.Subject"
                        "ToolUp.StorageScope"
                        "ToolUp.ScopeResult"
                        "ToolUp.ModulePermissions"
                        "ToolUp.ModuleExposure"
                        "ToolUp.PlatformRole"
                        "ToolUp.PublicationGrants"
                        "ToolUp.ServiceAccountPermissions"
                    ] do
                    Expect.isFalse (List.contains (box retired) keys) (sprintf "%s is not stamped" retired)
            | other -> failtestf "expected a stamped principal, got %A" other
        }

        testCaseAsync "Phase 882 — a fault after the subject is stamped refuses the request, whichever read faulted"
        <| async {
            let! status, reached, _ =
                runWithAdmin (permissionStore None) (Some(InvalidOperationException "admin store down")) ignore

            Expect.isFalse reached "a request whose principal was interrupted must not reach a handler"
            Expect.equal status 503 "refused"
        }
    ]