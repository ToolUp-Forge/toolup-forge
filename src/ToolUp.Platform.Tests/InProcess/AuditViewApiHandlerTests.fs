// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AuditViewApiHandlerTests

open System
open System.Text
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 529 — audit-trail viewer handler ──────────────────────────
//
// What is pinned here is what the handler ADDS over the store it reads:
//
//   1. **The role gate.** Anonymous refused, Member refused, Owner and
//      Admin admitted, single-scope callers admitted. Asserted per
//      METHOD rather than once, because each of the three methods
//      carries its own `withGate` call and a gate dropped from one of
//      them is exactly the omission a single case would miss — the
//      export being the one that hurts most, since it leaves the trail
//      as a file.
//   2. **Scope isolation (GP 4).** No wire input names a scope, so the
//      assertion is that a team reading its own trail sees none of
//      another team's rows even when both are in one store.
//   3. **Filters and paging.** Window / type / actor narrowing, the
//      cursor's total order over rows sharing a timestamp, and the
//      `NextCursor = None` terminator. The tie case is deliberate: the
//      audit log writes bursts, `OccurredAt` alone is not unique, and a
//      cursor over a non-total order silently drops or repeats rows at
//      the page boundary.
//   4. **The `NoAuditLog` short-circuit.** The handler reads the event
//      store rather than `IAuditLog`, so the pairing with
//      `ServerConfig.AuditLog` is restored by hand — and a hand-restored
//      property is one a test has to hold, because nothing about the
//      read path enforces it.
//
// The payload projections (actor probe / summary) are asserted through
// the rows the handler returns rather than directly: they are private,
// and their contract is what a client sees.

// ─── Fixtures ─────────────────────────────────────────────────────────

let private baseTime = DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)

/// A persisted audit row, as `EventStoreAuditLog.Record` writes one.
let private auditEvent (scopeId: string) (minutesAgo: float) (eventType: string) (payload: string) : ModuleEvent = {
    Id = Guid.NewGuid()
    OccurredAt = baseTime.AddMinutes(-minutesAgo)
    ScopeId = scopeId
    SourceModule = AuditSourceModule.value
    EventType = eventType
    Payload = payload
}

let private freshTeamStore () =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let notifications = InMemoryNotificationChannel(None) :> INotificationChannel
    TeamStore(storage, notifications)

/// Build a context over a seeded event store. `events` are written in
/// the order given; the handler is responsible for the ordering it
/// returns, so the seed deliberately does not pre-sort them.
let private buildApi
    (accessContext: AccessContext)
    (teamStore: ITeamStore option)
    (auditLogMode: AuditLogMode)
    (events: ModuleEvent list)
    : IAuditViewApi =
    let services = ServiceCollection()
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    for evt in events do
        (eventStore.Write evt) |> Async.RunSynchronously

    services.AddSingleton<AccessContext>(accessContext) |> ignore
    services.AddSingleton<IEventStore>(eventStore) |> ignore

    match teamStore with
    | Some ts -> services.AddSingleton<ITeamStore>(ts) |> ignore
    | None -> ()

    let sp = services.BuildServiceProvider() :> IServiceProvider
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    AuditViewApiHandler.auditViewApi auditLogMode ctx

/// A team-scoped context whose caller holds `role` in `teamId`.
let private teamContextWith (teamId: string) (userId: string) (role: TeamRole) = async {
    let store = freshTeamStore ()
    let! _ = store.CreateTeam(teamId, "Test Team")
    let! _ = (store :> ITeamStore).AddMember(teamId, userId, role)
    return AccessContext.unrestricted (TeamMember(userId, teamId)), (store :> ITeamStore)
}

let private expectOk (label: string) (result: Result<'T, string>) : 'T =
    match result with
    | Ok value -> value
    | Error msg -> failtestf "%s: expected Ok, got Error '%s'" label msg

let private expectError (label: string) (result: Result<'T, string>) : string =
    match result with
    | Ok _ -> failtestf "%s: expected Error, got Ok" label
    | Error msg -> msg

// ─── 1. Role gating ───────────────────────────────────────────────────

let roleGateTests =
    testList "Phase 529 — audit-view role gate" [

        testCaseAsync "an Anonymous caller is refused on every method"
        <| async {
            // Every method, not one: the export is the method whose
            // omission would leave the trail as a downloadable file, and
            // it is the one added last.
            let ctx = AccessContext.unrestricted (AnonymousSession "sess-1")

            let api =
                buildApi ctx None EnabledAuditLog [ auditEvent "_platform" 1.0 "UserLoggedIn" """{"UserId":"alice"}""" ]

            let! queried = api.Query AuditViewApi.defaultQuery
            let! types = api.ListEventTypes()
            let! exported = api.ExportCsv AuditViewApi.defaultQuery

            for label, msg in
                [
                    "Query", expectError "Query" queried
                    "ListEventTypes", expectError "ListEventTypes" types
                    "ExportCsv", expectError "ExportCsv" exported
                ] do
                Expect.stringContains msg "not available in this mode" $"{label} refuses an anonymous caller"
        }

        testCaseAsync "a Member is refused on every method, and told why"
        <| async {
            let! accessContext, teamStore = teamContextWith "team-a" "bob" Member

            let api =
                buildApi accessContext (Some teamStore) EnabledAuditLog [
                    auditEvent "team-a" 1.0 "UserLoggedIn" """{"UserId":"bob"}"""
                ]

            let! queried = api.Query AuditViewApi.defaultQuery
            let! types = api.ListEventTypes()
            let! exported = api.ExportCsv AuditViewApi.defaultQuery

            for label, msg in
                [
                    "Query", expectError "Query" queried
                    "ListEventTypes", expectError "ListEventTypes" types
                    "ExportCsv", expectError "ExportCsv" exported
                ] do
                Expect.stringContains msg "owners and admins" $"{label} names the required role"
        }

        testCaseAsync "an Owner and an Admin are both admitted"
        <| async {
            for role in [ Owner; Admin ] do
                let! accessContext, teamStore = teamContextWith "team-a" "alice" role

                let api =
                    buildApi accessContext (Some teamStore) EnabledAuditLog [
                        auditEvent "team-a" 1.0 "UserLoggedIn" """{"UserId":"alice"}"""
                    ]

                let! queried = api.Query AuditViewApi.defaultQuery
                let page = expectOk $"Query as {role}" queried
                Expect.hasLength page.Events 1 $"{role} reads the team's trail"
        }

        testCaseAsync "an AuthenticatedUser owns their own scope and is admitted"
        <| async {
            // No team, therefore no role to hold: the single-scope modes
            // read their own scope, which is the same authority the
            // usage dashboard grants them.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "carol")

            let api =
                buildApi accessContext None EnabledAuditLog [
                    auditEvent "carol" 1.0 "UserLoggedIn" """{"UserId":"carol"}"""
                ]

            let! queried = api.Query AuditViewApi.defaultQuery
            let page = expectOk "Query as AuthenticatedUser" queried
            Expect.hasLength page.Events 1 "the user reads their own scope"
        }
    ]

// ─── 2. Scope isolation ───────────────────────────────────────────────

let scopeIsolationTests =
    testList "Phase 529 — audit-view scope isolation" [

        testCaseAsync "a team reads only its own rows, with both teams in one store"
        <| async {
            let! accessContext, teamStore = teamContextWith "team-a" "alice" Owner

            let api =
                buildApi accessContext (Some teamStore) EnabledAuditLog [
                    auditEvent "team-a" 1.0 "UserLoggedIn" """{"UserId":"alice"}"""
                    auditEvent "team-b" 2.0 "UserLoggedIn" """{"UserId":"mallory"}"""
                    auditEvent "team-b" 3.0 "TeamCreated" """{"ChangedBy":"mallory"}"""
                ]

            let! queried = api.Query AuditViewApi.defaultQuery
            let page = expectOk "Query" queried

            Expect.hasLength page.Events 1 "only the caller's own scope is read"
            Expect.equal page.MatchedCount 1 "the match count is scoped too, not just the page"

            Expect.isFalse
                (page.Events |> List.exists (fun e -> e.Payload.Contains "mallory"))
                "no other team's payload appears"

            // The type list is a second read path over the same store and
            // would leak the neighbouring team's event kinds if it were
            // not scoped — a shape, not a payload, but still a fact about
            // another tenant.
            let! types = api.ListEventTypes()
            Expect.equal (expectOk "ListEventTypes" types) [ "UserLoggedIn" ] "the filter-bar options are scoped"
        }

        testCaseAsync "the CSV export carries only the caller's own scope"
        <| async {
            let! accessContext, teamStore = teamContextWith "team-a" "alice" Owner

            let api =
                buildApi accessContext (Some teamStore) EnabledAuditLog [
                    auditEvent "team-a" 1.0 "UserLoggedIn" """{"UserId":"alice"}"""
                    auditEvent "team-b" 2.0 "UserLoggedIn" """{"UserId":"mallory"}"""
                ]

            let! exported = api.ExportCsv AuditViewApi.defaultQuery
            let csv = Encoding.UTF8.GetString(expectOk "ExportCsv" exported)

            Expect.stringContains csv "alice" "the caller's own row is exported"
            Expect.isFalse (csv.Contains "mallory") "the other team's row is not"
        }
    ]

// ─── 3. Filters, ordering and paging ──────────────────────────────────

let private trailOf (scopeId: string) = [
    auditEvent scopeId 0.0 "UserLoggedIn" """{"UserId":"alice"}"""
    auditEvent scopeId 10.0 "TeamCreated" """{"ChangedBy":"bob","Name":"Team B"}"""
    auditEvent scopeId 20.0 "UserLoggedIn" """{"UserId":"bob"}"""
    auditEvent scopeId 30.0 "MemberAdded" """{"ChangedBy":"alice","MemberId":"carol"}"""
]

let filterTests =
    testList "Phase 529 — audit-view filters" [

        testCaseAsync "the page is newest-first"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried = api.Query AuditViewApi.defaultQuery
            let page = expectOk "Query" queried

            let times = page.Events |> List.map _.OccurredAt
            Expect.equal times (List.sortDescending times) "rows are reverse-chronological"
        }

        testCaseAsync "the time window is inclusive at both ends"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        From = Some(baseTime.AddMinutes -20.0)
                        To = Some(baseTime.AddMinutes -10.0)
                }

            let page = expectOk "Query" queried
            Expect.equal page.MatchedCount 2 "both boundary rows are inside the window"
        }

        testCaseAsync "the event-type filter is exact, not a prefix"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        EventType = Some "UserLoggedIn"
                }

            let page = expectOk "Query" queried
            Expect.equal page.MatchedCount 2 "both UserLoggedIn rows match"

            Expect.all page.Events (fun e -> e.EventType = "UserLoggedIn") "nothing else does"
        }

        testCaseAsync "the actor filter matches a case-insensitive substring of the projected actor"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        Actor = Some "ALI"
                }

            let page = expectOk "Query" queried

            // `UserId` on the login row and `ChangedBy` on the
            // member-added row: the probe reads both field names, which
            // is the point of the list rather than one canonical field.
            Expect.equal page.MatchedCount 2 "both of alice's rows match, whichever field named her"
        }

        testCaseAsync "a row whose payload names no actor never matches an actor filter"
        <| async {
            // "Unattributed" must not behave as a wildcard: an operator
            // narrowing to one actor and being shown every anonymous
            // system row would read the noise as that actor's activity.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let api =
                buildApi accessContext None EnabledAuditLog [
                    auditEvent "alice" 1.0 "OrphanSweepCompleted" """{"Reclaimed":4}"""
                ]

            let! unfiltered = api.Query AuditViewApi.defaultQuery
            let all = expectOk "Query" unfiltered
            Expect.equal (all.Events |> List.head |> _.Actor) None "the row is unattributed"

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        Actor = Some "alice"
                }

            Expect.equal (expectOk "Query" queried).MatchedCount 0 "an unattributed row is not a match"
        }

        testCaseAsync "a tombstoned payload still appears, with its envelope intact"
        <| async {
            // GDPR erasure replaces the whole payload with a marker. The
            // row must stay in the trail — that a row was redacted is
            // itself audit-relevant — so the projections have to survive
            // a payload that is not a JSON object.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let api =
                buildApi accessContext None EnabledAuditLog [
                    auditEvent "alice" 1.0 "FileUploaded" Erasure.TombstoneMarker
                ]

            let! queried = api.Query AuditViewApi.defaultQuery
            let page = expectOk "Query" queried

            Expect.hasLength page.Events 1 "the tombstoned row is not filtered out"
            let row = List.head page.Events
            Expect.equal row.EventType "FileUploaded" "its envelope survives"
            Expect.equal row.Actor None "and it attributes nobody"
        }
    ]

let pagingTests =
    testList "Phase 529 — audit-view paging" [

        testCaseAsync "the page size is capped server-side"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let events =
                [ 1 .. (AuditViewApi.MaxPageSize + 10) ]
                |> List.map (fun i -> auditEvent "alice" (float i) "UserLoggedIn" """{"UserId":"alice"}""")

            let api = buildApi accessContext None EnabledAuditLog events

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        PageSize = 10_000
                }

            let page = expectOk "Query" queried

            Expect.hasLength page.Events AuditViewApi.MaxPageSize "the caller's page size is clamped"
            Expect.equal page.MatchedCount (AuditViewApi.MaxPageSize + 10) "the match count is not clamped with it"
            Expect.isSome page.NextCursor "there is another page"
        }

        testCaseAsync "paging walks the whole trail exactly once, with no repeats"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let events =
                [ 1..7 ]
                |> List.map (fun i -> auditEvent "alice" (float i) "UserLoggedIn" """{"UserId":"alice"}""")

            let api = buildApi accessContext None EnabledAuditLog events

            let rec walk (cursor: string option) (seen: Guid list) = async {
                let! queried =
                    api.Query {
                        AuditViewApi.defaultQuery with
                            PageSize = 3
                            Cursor = cursor
                    }

                let page = expectOk "Query" queried
                let seen = seen @ (page.Events |> List.map _.Id)

                match page.NextCursor with
                | Some next -> return! walk (Some next) seen
                | None -> return seen
            }

            let! seen = walk None []

            Expect.hasLength seen 7 "every row is returned"
            Expect.hasLength (List.distinct seen) 7 "and none of them twice"
        }

        testCaseAsync "rows sharing a timestamp page without loss — the cursor's order is total"
        <| async {
            // The audit log writes bursts, so `OccurredAt` is not unique.
            // A cursor expressed on time alone would either skip the rest
            // of a tied group or serve it again forever; the id is the
            // tiebreak that makes the boundary a single row.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let events =
                [ 1..5 ]
                |> List.map (fun _ -> auditEvent "alice" 5.0 "UserLoggedIn" """{"UserId":"alice"}""")

            let api = buildApi accessContext None EnabledAuditLog events

            let! first =
                api.Query {
                    AuditViewApi.defaultQuery with
                        PageSize = 2
                }

            let firstPage = expectOk "Query" first
            Expect.hasLength firstPage.Events 2 "the first page is full"

            let! second =
                api.Query {
                    AuditViewApi.defaultQuery with
                        PageSize = 2
                        Cursor = firstPage.NextCursor
                }

            let secondPage = expectOk "Query" second

            let overlap =
                Set.intersect
                    (firstPage.Events |> List.map _.Id |> Set.ofList)
                    (secondPage.Events |> List.map _.Id |> Set.ofList)

            Expect.isEmpty overlap "the pages do not overlap despite the identical timestamps"
        }

        testCaseAsync "the last page terminates rather than offering an empty next"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        PageSize = 4
                }

            let page = expectOk "Query" queried
            Expect.hasLength page.Events 4 "the whole trail fits in one page"
            Expect.isNone page.NextCursor "so there is no next cursor"
        }

        testCaseAsync "an unreadable cursor is refused, not silently restarted"
        <| async {
            // Restarting from the top on a bad token would render a page
            // that looks like the continuation and is not — a page an
            // operator would read as the older half of the trail.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! queried =
                api.Query {
                    AuditViewApi.defaultQuery with
                        Cursor = Some "not-a-cursor"
                }

            let msg = expectError "Query" queried
            Expect.stringContains msg "cursor" "the refusal names the cursor"
        }
    ]

// ─── 4. Export + the NoAuditLog pairing ───────────────────────────────

let exportTests =
    testList "Phase 529 — audit-view CSV export" [

        testCaseAsync "the export covers the filtered window, not the current page"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let events =
                [ 1..5 ]
                |> List.map (fun i -> auditEvent "alice" (float i) "UserLoggedIn" """{"UserId":"alice"}""")

            let api = buildApi accessContext None EnabledAuditLog events

            let! exported =
                api.ExportCsv {
                    AuditViewApi.defaultQuery with
                        PageSize = 1
                }

            let csv = Encoding.UTF8.GetString(expectOk "ExportCsv" exported)
            let lines = csv.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)

            Expect.hasLength lines 6 "a header plus every matched row, not one page of them"

            Expect.stringStarts
                lines[0]
                "Id,OccurredAt,EventType,Actor,Summary,Payload"
                "the header is the documented one"
        }

        testCaseAsync "the export honours the filters"
        <| async {
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None EnabledAuditLog (trailOf "alice")

            let! exported =
                api.ExportCsv {
                    AuditViewApi.defaultQuery with
                        EventType = Some "TeamCreated"
                }

            let csv = Encoding.UTF8.GetString(expectOk "ExportCsv" exported)
            let lines = csv.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries)

            Expect.hasLength lines 2 "the header plus the single matching row"
            Expect.stringContains lines[1] "TeamCreated" "and it is the row the filter named"
        }

        testCaseAsync "a payload containing commas and quotes survives the round trip"
        <| async {
            // The payload is JSON, so it always contains quotes and
            // usually commas: an unescaped export would shift every
            // column right of it, which a spreadsheet renders without
            // complaint.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")

            let api =
                buildApi accessContext None EnabledAuditLog [
                    auditEvent "alice" 1.0 "TeamCreated" """{"ChangedBy":"alice","Name":"A, B \"and\" C"}"""
                ]

            let! exported = api.ExportCsv AuditViewApi.defaultQuery
            let csv = Encoding.UTF8.GetString(expectOk "ExportCsv" exported)

            Expect.stringContains csv "\"\"" "embedded quotes are doubled per RFC 4180"

            Expect.hasLength
                (csv.Split([| "\r\n" |], StringSplitOptions.RemoveEmptyEntries))
                2
                "and the row stays on one line"
        }
    ]

let substratePairingTests =
    testList "Phase 529 — audit-view / ServerConfig.AuditLog pairing" [

        testCaseAsync "NoAuditLog yields nothing, even over a store that still holds rows"
        <| async {
            // The handler reads the event store, so residue from before
            // the trail was switched off is physically present. A
            // deployment that has turned the audit log off has said it
            // keeps no trail, and the viewer must agree with it.
            let accessContext = AccessContext.unrestricted (AuthenticatedUser "alice")
            let api = buildApi accessContext None NoAuditLog (trailOf "alice")

            let! queried = api.Query AuditViewApi.defaultQuery
            let page = expectOk "Query" queried
            Expect.isEmpty page.Events "no rows are served"
            Expect.equal page.MatchedCount 0 "and none are counted"

            let! types = api.ListEventTypes()
            Expect.isEmpty (expectOk "ListEventTypes" types) "the filter bar offers nothing"

            let! exported = api.ExportCsv AuditViewApi.defaultQuery
            let csv = Encoding.UTF8.GetString(expectOk "ExportCsv" exported)
            Expect.isFalse (csv.Contains "UserLoggedIn") "and the export is empty of them"
        }

        testCaseAsync "the role gate still runs under NoAuditLog"
        <| async {
            // An empty answer is still an answer about a deployment. The
            // short-circuit sits INSIDE the gate, and a refactor that
            // moved it outside would turn the disabled case into an
            // ungated endpoint.
            let ctx = AccessContext.unrestricted (AnonymousSession "sess-1")
            let api = buildApi ctx None NoAuditLog []

            let! queried = api.Query AuditViewApi.defaultQuery
            expectError "Query" queried |> ignore
        }
    ]