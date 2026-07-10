module ToolUp.Platform.Tests.InProcess.PrincipalRegistryTests

open System
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 543 — derived principal enumeration tests ─────────────────
//
// Contract-shaped pack over `PrincipalRegistry.listPrincipals` and the
// `IPlatformTenantApi.ListPrincipals` handler surface: a
// membership-only principal, a storage-only principal (team-less
// flagged), an audit-only principal (signed in, never stored, never
// joined), the merged-all-three case, the look-back bound, and the
// admin / substrate fail-closed gates.

let private auditJson = FableConverters.create ()

/// A stored membership blob in `TeamManagement`'s wire shape.
let private membershipBlob (rows: (string * string) list) : byte[] =
    rows
    |> List.map (fun (teamId, role) ->
        sprintf """{"teamId":"%s","role":"%s","joinedAt":"2026-01-01T00:00:00Z"}""" teamId role)
    |> String.concat ","
    |> sprintf "[%s]"
    |> Encoding.UTF8.GetBytes

/// A `UserLoggedIn` audit row as `EventStoreAuditLog` persists it.
let private loginEvent (scopeId: string) (userId: string) (occurredAt: DateTime) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = occurredAt
    ScopeId = scopeId
    SourceModule = AuditSourceModule.value
    EventType = "UserLoggedIn"
    Payload =
        JsonSerializer.Serialize(
            ({
                UserId = userId
                AuthProvider = "Test"
            }
            : UserLoggedInPayload),
            auditJson
        )
}

/// An ordinary (non-audit) module event — user-scope data evidence.
let private scopeEvent (scopeId: string) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = DateTime.UtcNow
    ScopeId = scopeId
    SourceModule = "test.module"
    EventType = "SomethingHappened"
    Payload = "{}"
}

let private newStores () =
    InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage, InMemoryEventStore.InMemoryEventStore() :> IEventStore

let private seed (events: IEventStore) (rows: ModuleEvent list) =
    rows |> List.map events.Write |> Async.Sequential |> Async.Ignore

let private principalFor (userId: string) (principals: PrincipalSummary list) =
    match principals |> List.tryFind (fun p -> p.UserId = userId) with
    | Some p -> p
    | None -> failtestf "expected principal '%s' in %A" userId (principals |> List.map _.UserId)

// ─── Handler builder (mirrors OffboardConfirmationTests) ─────────────

let private adminCtx (userId: string) : AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser userId) with
        PlatformRole = Some PlatformRole.PlatformAdmin
}

let private handlerFor
    (accessCtx: AccessContext option)
    (storage: IBlobStorage option)
    (events: IEventStore option)
    : IPlatformTenantApi =
    let services = ServiceCollection()

    match accessCtx with
    | Some ac -> services.AddSingleton<AccessContext>(ac) |> ignore
    | None -> ()

    services.AddSingleton<ServerConfig>(
        {
            ServerConfig.defaults with
                TenantLifecycle = EnabledTenantLifecycle
        }
    )
    |> ignore

    match storage with
    | Some s -> services.AddSingleton<IBlobStorage>(s) |> ignore
    | None -> ()

    match events with
    | Some e -> services.AddSingleton<IEventStore>(e) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    PlatformTenantApiHandler.platformTenantApi ctx

let tests =
    testList "Phase 543 — derived principal enumeration" [

        testCaseAsync "membership-only principal — rows listed, not team-less, no other evidence"
        <| async {
            let storage, events = newStores ()
            let! _ = storage.Upload("_platform", "memberships/alice.json", membershipBlob [ "team-x", "Owner" ])
            let! principals = PrincipalRegistry.listPrincipals storage events

            let alice = principalFor "alice" principals
            Expect.equal alice.Memberships [ "team-x", Owner ] "membership row projected from the blob"
            Expect.isFalse alice.TeamLess "a membership row means not team-less"
            Expect.isFalse alice.HasUserScopeData "no user-scope data seeded"
            Expect.isNone alice.LastSeenAt "no login seeded"
        }

        testCaseAsync "storage-only principal — discovered from the user scope, team-less flagged"
        <| async {
            let storage, events = newStores ()
            do! seed events [ scopeEvent "user-bob" ]
            let! principals = PrincipalRegistry.listPrincipals storage events

            let bob = principalFor "bob" principals
            Expect.isTrue bob.TeamLess "no membership row — team-less"
            Expect.isTrue bob.HasUserScopeData "user-scope events are scope-data evidence"
            Expect.isNone bob.LastSeenAt "never signed in inside the window"
        }

        testCaseAsync "audit-only principal — signed in (under a team scope), never stored, never joined"
        <| async {
            let storage, events = newStores ()
            let seenAt = DateTime.UtcNow.AddDays -1.0
            do! seed events [ loginEvent "team-x" "carol" seenAt ]
            let! principals = PrincipalRegistry.listPrincipals storage events

            let carol = principalFor "carol" principals
            Expect.isTrue carol.TeamLess "no membership row — team-less"
            Expect.isFalse carol.HasUserScopeData "no user-scope data"
            Expect.equal carol.LastSeenAt (Some seenAt) "LastSeenAt is the login envelope's OccurredAt"
        }

        testCaseAsync "merged-all-three — one row per UserId with every evidence source folded in"
        <| async {
            let storage, events = newStores ()
            let! _ = storage.Upload("_platform", "memberships/dave.json", membershipBlob [ "team-y", "Member" ])
            let earlier = DateTime.UtcNow.AddDays -3.0
            let later = DateTime.UtcNow.AddDays -1.0

            do!
                seed events [
                    scopeEvent "user-dave"
                    loginEvent "team-y" "dave" earlier
                    loginEvent "user-dave" "dave" later
                ]

            let! principals = PrincipalRegistry.listPrincipals storage events

            Expect.hasLength (principals |> List.filter (fun p -> p.UserId = "dave")) 1 "one merged row per UserId"
            let dave = principalFor "dave" principals
            Expect.equal dave.Memberships [ "team-y", Member ] "membership evidence merged"
            Expect.isFalse dave.TeamLess "not team-less"
            Expect.isTrue dave.HasUserScopeData "user-scope evidence merged"
            Expect.equal dave.LastSeenAt (Some later) "LastSeenAt is the most recent login across scopes"
        }

        testCaseAsync "look-back bound — a login older than the window contributes no evidence"
        <| async {
            let storage, events = newStores ()
            do! seed events [ loginEvent "team-x" "erin" (DateTime.UtcNow.AddDays -30.0) ]
            let! principals = PrincipalRegistry.listPrincipalsWith storage events (TimeSpan.FromDays 7.0)

            Expect.isEmpty
                (principals |> List.filter (fun p -> p.UserId = "erin"))
                "a stale login neither discovers the principal nor sets LastSeenAt"
        }

        testCaseAsync "blob probe — user-container blobs flag HasUserScopeData for a known principal"
        <| async {
            let storage, events = newStores ()
            let! _ = storage.Upload("_platform", "memberships/fred.json", membershipBlob [ "team-z", "Admin" ])
            let! _ = storage.Upload("user-fred", "files/notes.txt", Encoding.UTF8.GetBytes "hi")
            let! principals = PrincipalRegistry.listPrincipals storage events

            let fred = principalFor "fred" principals
            Expect.isTrue fred.HasUserScopeData "blob probe finds the user-container data"
        }

        testCaseAsync "empty membership blob — the principal is listed and team-less"
        <| async {
            let storage, events = newStores ()
            let! _ = storage.Upload("_platform", "memberships/gina.json", membershipBlob [])
            let! principals = PrincipalRegistry.listPrincipals storage events

            let gina = principalFor "gina" principals
            Expect.isTrue gina.TeamLess "TeamLess = true exactly when no membership row exists"
        }

        // ─── Handler surface (IPlatformTenantApi.ListPrincipals) ─────

        testCaseAsync "handler — admin caller enumerates principals"
        <| async {
            let storage, events = newStores ()
            let! _ = storage.Upload("_platform", "memberships/alice.json", membershipBlob [ "team-x", "Owner" ])
            let api = handlerFor (Some(adminCtx "admin-1")) (Some storage) (Some events)
            let! result = api.ListPrincipals()

            match result with
            | Ok principals -> Expect.equal (principals |> List.map _.UserId) [ "alice" ] "admin sees the enumeration"
            | Error e -> failtestf "expected Ok, got Error %s" e
        }

        testCaseAsync "handler — non-admin caller is refused (fail-closed)"
        <| async {
            let storage, events = newStores ()

            let api =
                handlerFor (Some(AccessContext.unrestricted (AuthenticatedUser "user-1"))) (Some storage) (Some events)

            let! result = api.ListPrincipals()
            Expect.equal result (Error PlatformTenantApiHandler.adminError) "non-admin refused"
        }

        testCaseAsync "handler — no resolved AccessContext is treated as non-admin"
        <| async {
            let storage, events = newStores ()
            let api = handlerFor None (Some storage) (Some events)
            let! result = api.ListPrincipals()
            Expect.equal result (Error PlatformTenantApiHandler.adminError) "missing AccessContext fails closed"
        }

        testCaseAsync "handler — missing substrate yields the clear remedy, never a partial list"
        <| async {
            let _, events = newStores ()
            let api = handlerFor (Some(adminCtx "admin-1")) None (Some events)
            let! result = api.ListPrincipals()

            Expect.equal
                result
                (Error PlatformTenantApiHandler.principalEnumerationNoSubstrate)
                "no IBlobStorage — fail closed with the remedy"
        }
    ]