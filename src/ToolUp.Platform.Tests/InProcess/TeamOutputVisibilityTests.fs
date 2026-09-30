// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TeamOutputVisibilityTests

open System
open System.Text
open System.Threading.Tasks
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.TeamPolicyStore
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.AI
open ToolUp.AI.TeamConversationPolicyStore
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 896 — team output visibility ──────────────────────────────
//
// Module permission governs USE; the team's policy governs who sees the
// restricted OUTPUT a module published. Covered here: the pure rule and the
// policy-change check; the viewer-aware resolver beside the viewer-blind
// one; the gate deciding a `Restricted` fact for the viewer at EVERY egress
// surface (one case each), with the viewer taken from the platform-resolved
// request and the least-privileged viewer when there is none; the tool and
// browse doors end to end; byte-identical behaviour when nothing is
// declared; and the output level stored in the same per-team record as the
// conversation level, under the same change rules.

let private team = "alpha"

/// The platform-resolved viewers, as scope resolution stamps them.
let private viewer (userId: string) (isPlatformAdmin: bool) : RequestViewer = {
    UserId = userId
    ActiveTeamId = Some team
    IsPlatformAdmin = isPlatformAdmin
}

let private member' = viewer "mia" false
let private admin = viewer "ada" false
let private owner = viewer "olga" false
let private platformAdmin = viewer "pat" true

let private roles: Map<string, TeamRole> =
    Map.ofList [
        "mia", TeamRole.Member
        "ada", TeamRole.Admin
        "olga", TeamRole.Owner
        "pat", TeamRole.Member
    ]

let private allSurfaces = [
    FactRetrieval
    FactToolResult
    FactNarrativePublication
    FactExport
    FactWebhook
    FactPeerEgress
    FactBrowse
]

/// A `Restricted` policy the deployment permits at every surface, so the
/// only thing left to limit it is the team's output level.
let private salesPolicy: DisclosurePolicy = {
    PolicyRef = "sales"
    Mode = Plain
    PermitSurfaces = allSurfaces
    ContributorScope = None
}

let private q3: TemporalExtent = {
    From = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q3-2026"
}

let private draftWith (metric: string) (disclosure: Disclosure) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ "uk" ]
    }
    Metric = MetricRef metric
    Value = Scalar value
    Period = q3
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ "hashA" ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = disclosure
}

let private assertFact (store: IFactStore) (scope: string) (draft: FactDraft) =
    match store.Assert(scope, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// Stub team facts, counting role lookups so a test can prove the default
/// path never makes one.
type private Sources(level: Result<TeamVisibilityLevel, string>) =
    let mutable roleLookups = 0

    member _.RoleLookups = roleLookups

    member _.Value: DisclosureViewerSources = {
        OutputVisibility = fun _ -> async.Return level
        TeamRole =
            fun _ userId ->
                roleLookups <- roleLookups + 1
                async.Return(roles.TryFind userId)
        IsTeam = fun scopeId -> async.Return(String.Equals(scopeId, team))
    }

type private Harness = {
    Store: IFactStore
    Events: IEventStore
    Plain: IFactDisclosureGate
    Armed: IFactDisclosureGate
    Sources: Sources
}

let private harness (level: Result<TeamVisibilityLevel, string>) =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    let taint = DisclosureTaintConfig.ofLists [ salesPolicy ] []
    let sources = Sources level

    {
        Store = store
        Events = events
        Plain = FactDisclosureGate(store, events, taint = taint) :> IFactDisclosureGate
        Armed =
            FactDisclosureGate(store, events, taint = taint)
                .WithViewerAwareness(
                    {
                        Resolver = None
                        Sources = sources.Value
                    }
                )
        Sources = sources
    }

/// Run `body` as the platform's request plumbing would: with `viewer`
/// established as the ambient request viewer.
let private asViewer (viewer: RequestViewer option) (body: Async<'T>) : Async<'T> = async {
    use _ = RequestViewerContext.establish (fun () -> viewer)
    return! body
}

let private verdictOf (gate: IFactDisclosureGate) (viewer: RequestViewer option) surface (factId: string) =
    asViewer viewer (gate.Check(team, "principal-ignored", surface, [ factId ]))
    |> Async.RunSynchronously
    |> Map.tryFind factId

let private teamScope (teamId: string) : ResolvedScope =
    ScopeResolution.ofStorageScope {
        ScopeId = teamId
        Container = $"team-{teamId}"
        Persist = true
    }

// ─── The rule ────────────────────────────────────────────────────────

let private ruleTests =
    testList "the rule and the policy-change check" [

        test "canSee: whole team admits everyone the scope admits; team admins and platform admins admit only their own" {
            let cases = [
                TeamVisible, None, false, true
                TeamVisible, Some TeamRole.Member, false, true
                TeamAdmins, Some TeamRole.Member, false, false
                TeamAdmins, Some TeamRole.Admin, false, true
                TeamAdmins, Some TeamRole.Owner, false, true
                TeamAdmins, Some TeamRole.Member, true, false
                PlatformAdmins, Some TeamRole.Owner, false, false
                PlatformAdmins, Some TeamRole.Member, true, true
                PlatformAdmins, None, true, true
            ]

            for level, role, isPlatformAdmin, expected in cases do
                Expect.equal
                    (TeamOutputVisibility.canSee level role isPlatformAdmin)
                    expected
                    $"{TeamVisibilityLevel.name level} / {role} / platformAdmin={isPlatformAdmin}"
        }

        test "the conversation axis shares the level type rather than restating it" {
            let conversationLevel: TeamConversationVisibility = TeamAdmins
            let outputLevel: TeamVisibilityLevel = conversationLevel
            Expect.equal outputLevel TeamAdmins "one type, two axes"
            Expect.equal TeamConversationVisibility.all TeamVisibilityLevel.all "one list of levels"
        }

        test "the levels are not a chain: only TeamVisible contains the others" {
            Expect.isTrue (TeamVisibilityLevel.isWithin TeamAdmins TeamVisible) "team admins within whole team"
            Expect.isTrue (TeamVisibilityLevel.isWithin PlatformAdmins TeamVisible) "platform admins within whole team"
            Expect.isFalse (TeamVisibilityLevel.isWithin TeamAdmins PlatformAdmins) "incomparable"
            Expect.isFalse (TeamVisibilityLevel.isWithin PlatformAdmins TeamAdmins) "incomparable"
            Expect.isFalse (TeamVisibilityLevel.isWithin TeamVisible TeamAdmins) "whole team is wider"

            for level in TeamVisibilityLevel.all do
                Expect.isTrue (TeamVisibilityLevel.isWithin level level) "reflexive"
        }

        test "the policy-change check refuses conversation visibility wider than output, naming both" {
            match TeamOutputVisibility.checkCombination TeamVisible [ TeamAdmins ] with
            | Error message ->
                Expect.stringContains message "Conversation visibility TeamVisible" "names the conversation level"
                Expect.stringContains message "output visibility TeamAdmins" "names the output level"
            | Ok() -> failtest "whole-team conversations over admin-only output must be refused"

            match TeamOutputVisibility.checkCombination TeamAdmins [ PlatformAdmins ] with
            | Error message -> Expect.stringContains message "PlatformAdmins" "names the incomparable output level"
            | Ok() -> failtest "incomparable levels are refused"

            Expect.equal (TeamOutputVisibility.checkCombination TeamAdmins [ TeamAdmins ]) (Ok()) "equal levels"

            Expect.equal
                (TeamOutputVisibility.checkCombination TeamAdmins [ TeamVisible ])
                (Ok())
                "narrower conversations"

            Expect.equal (TeamOutputVisibility.checkCombination TeamVisible [ TeamVisible ]) (Ok()) "the default pair"
            Expect.equal (TeamOutputVisibility.checkCombination TeamVisible []) (Ok()) "no output level in force"
        }

        test "the change rules are conversation visibility's: owner only, platform admins only by a platform admin" {
            let settings = TeamOutputVisibilitySettings.unrestricted

            let change role isPlatformAdmin current requested =
                TeamOutputVisibility.checkChange settings current role isPlatformAdmin requested

            Expect.isOk (change (Some TeamRole.Owner) false TeamVisible TeamAdmins) "the owner narrows"
            Expect.isError (change (Some TeamRole.Admin) false TeamVisible TeamAdmins) "a team admin may not"
            Expect.isError (change (Some TeamRole.Member) false TeamVisible TeamAdmins) "a member may not"
            Expect.isError (change None true TeamVisible TeamAdmins) "a non-member platform admin may not"

            Expect.isError
                (change (Some TeamRole.Owner) false TeamVisible PlatformAdmins)
                "platform admins needs a platform admin"

            Expect.isOk
                (change (Some TeamRole.Owner) true TeamVisible PlatformAdmins)
                "an owner who is a platform admin may"

            Expect.isError (change (Some TeamRole.Owner) false PlatformAdmins TeamVisible) "leaving it needs one too"

            let narrow =
                TeamOutputVisibilitySettings.create TeamVisible [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            match TeamOutputVisibility.checkChange narrow TeamVisible (Some TeamRole.Owner) true PlatformAdmins with
            | Error message -> Expect.stringContains message "Allowed: TeamVisible, TeamAdmins" "names the allowed set"
            | Ok() -> failtest "a level outside the allowed set is refused"

            Expect.equal
                (TeamOutputVisibility.selectable settings TeamVisible (Some TeamRole.Owner) false)
                [ TeamVisible; TeamAdmins ]
                "an owner who is not a platform admin selects within reach"

            Expect.isEmpty
                (TeamOutputVisibility.selectable settings TeamVisible (Some TeamRole.Admin) false)
                "not an admin"
        }

        test "the deployment declaration is validated" {
            Expect.isError (TeamOutputVisibilitySettings.create TeamVisible []) "an empty allowed set"
            Expect.isError (TeamOutputVisibilitySettings.create TeamAdmins [ TeamVisible ]) "a default outside the set"

            let settings =
                TeamOutputVisibilitySettings.create TeamAdmins [ PlatformAdmins; TeamAdmins; TeamAdmins ]
                |> Result.defaultWith failwith

            Expect.equal settings.Allowed [ TeamAdmins; PlatformAdmins ] "deduplicated, widest first"
            Expect.equal TeamOutputVisibilitySettings.unrestricted.Default TeamVisible "the default is whole team"
        }
    ]

// ─── The resolver ────────────────────────────────────────────────────

let private resolverTests =
    testList "the viewer-aware resolver sits beside the viewer-blind one" [

        test "ofResolver ignores the viewer; the existing resolver type is unchanged" {
            let old: DisclosurePolicyResolver =
                fun policyRef _ -> if policyRef = "sales" then Some true else None

            let lifted = ViewerAwareDisclosurePolicyResolver.ofResolver old
            let nobody = DisclosureViewer.leastPrivileged PlatformAdmins
            Expect.equal (lifted "sales" FactRetrieval nobody) (Some true) "same answer for any viewer"
            Expect.equal (lifted "other" FactRetrieval nobody) None "unknown stays unknown"
        }

        test "withTeamOutputVisibility is a conjunction, and an unknown policy still denies" {
            let old: DisclosurePolicyResolver =
                fun policyRef surface ->
                    match policyRef with
                    | "sales" -> Some(surface <> FactWebhook)
                    | _ -> None

            let resolve = ViewerAwareDisclosurePolicyResolver.withTeamOutputVisibility old

            let adminViewer = {
                TeamRole = Some TeamRole.Admin
                IsPlatformAdmin = true
                OutputVisibility = TeamAdmins
            }

            let memberViewer = {
                adminViewer with
                    TeamRole = Some TeamRole.Member
                    IsPlatformAdmin = false
            }

            Expect.equal (resolve "sales" FactRetrieval adminViewer) (Some true) "policy and level both admit"
            Expect.equal (resolve "sales" FactRetrieval memberViewer) (Some false) "the level refuses a member"
            Expect.equal (resolve "sales" FactWebhook adminViewer) (Some false) "the policy still forbids a surface"
            Expect.equal (resolve "unregistered" FactRetrieval adminViewer) None "an unknown policy is never widened"

            Expect.equal
                (DisclosureEgress.evaluateForViewer resolve FactRetrieval memberViewer (Restricted "unregistered"))
                (FactNotDisclosable "unregistered")
                "an unknown policy denies, naming it"
        }

        test "Surfaceable and Internal are decided exactly as before, whatever the viewer" {
            let resolve =
                ViewerAwareDisclosurePolicyResolver.ofResolver DisclosurePolicyResolver.denyUnknown

            let nobody = DisclosureViewer.leastPrivileged PlatformAdmins

            for surface in allSurfaces do
                Expect.equal
                    (DisclosureEgress.evaluateForViewer resolve surface nobody Surfaceable)
                    (DisclosureEgress.evaluate DisclosurePolicyResolver.denyUnknown surface Surfaceable)
                    "surfaceable"

                Expect.equal
                    (DisclosureEgress.evaluateForViewer resolve surface nobody Disclosure.Internal)
                    (FactNotDisclosable "Internal")
                    "internal"
        }
    ]

// ─── The gate, one case per egress surface ──────────────────────────

let private surfaceCase (surface: FactEgressSurface) =
    testCase $"{FactEgressSurface.toString surface}: a fact limited to team admins is decided for the viewer" (fun () ->
        let h = harness (Ok TeamAdmins)

        let restricted =
            assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

        let memberVerdict = verdictOf h.Armed (Some member') surface restricted.FactId
        let adminVerdict = verdictOf h.Armed (Some admin) surface restricted.FactId

        Expect.equal memberVerdict (Some(FactNotDisclosable "sales")) "absent for a member"

        if ViewerAwareDisclosure.audienceIsRequester surface then
            Expect.equal adminVerdict (Some FactDisclosable) "present for an admin"
        else
            // This door's audience is wider than the requester, so it is
            // decided for the least-privileged viewer: publishing is never
            // a way to widen who sees restricted output.
            Expect.equal adminVerdict (Some(FactNotDisclosable "sales")) "withheld even when an admin publishes")

let private surfaceTests =
    testList "every egress surface" [ for surface in allSurfaces -> surfaceCase surface ]

let private gateTests =
    testList "the gate decides a Restricted fact for the viewer" [

        testCase "a team that declares nothing is decided exactly as before, with no role lookup" (fun () ->
            let h = harness (Ok TeamVisible)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            let surfaceable = assertFact h.Store team (draftWith "revenue" Surfaceable 1m)

            let unknown =
                assertFact h.Store team (draftWith "cost" (Restricted "unregistered") 2m)

            let ids = [ restricted.FactId; surfaceable.FactId; unknown.FactId ]

            for surface in allSurfaces do
                for viewer in [ None; Some member'; Some admin ] do
                    let armed =
                        asViewer viewer (h.Armed.Check(team, "p", surface, ids))
                        |> Async.RunSynchronously

                    let plain = h.Plain.Check(team, "p", surface, ids) |> Async.RunSynchronously
                    Expect.equal armed plain $"identical at {FactEgressSurface.toString surface}"

            Expect.equal h.Sources.RoleLookups 0 "whole team needs no role")

        testCase "platform admins: a platform admin sees it, a team owner does not" (fun () ->
            let h = harness (Ok PlatformAdmins)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            Expect.equal
                (verdictOf h.Armed (Some platformAdmin) FactRetrieval restricted.FactId)
                (Some FactDisclosable)
                "platform admin"

            Expect.equal
                (verdictOf h.Armed (Some owner) FactRetrieval restricted.FactId)
                (Some(FactNotDisclosable "sales"))
                "owner")

        testCase "no resolved viewer (a job, a webhook) evaluates as the least-privileged viewer" (fun () ->
            let narrow = harness (Ok TeamAdmins)

            let restricted =
                assertFact narrow.Store team (draftWith "margin" (Restricted "sales") 4242m)

            Expect.equal
                (verdictOf narrow.Armed None FactRetrieval restricted.FactId)
                (Some(FactNotDisclosable "sales"))
                "denied"

            Expect.equal narrow.Sources.RoleLookups 0 "no identity, no lookup"

            let wide = harness (Ok TeamVisible)

            let restricted' =
                assertFact wide.Store team (draftWith "margin" (Restricted "sales") 4242m)

            Expect.equal
                (verdictOf wide.Armed None FactRetrieval restricted'.FactId)
                (Some FactDisclosable)
                "whole team admits it")

        testCase "the viewer comes from the request, never from the principal the door passes" (fun () ->
            let h = harness (Ok TeamAdmins)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            let verdicts =
                asViewer (Some member') (h.Armed.Check(team, "ada", FactRetrieval, [ restricted.FactId ]))
                |> Async.RunSynchronously

            Expect.equal
                verdicts[restricted.FactId]
                (FactNotDisclosable "sales")
                "naming an admin as principal widens nothing")

        testCase "an unknown policy still denies, even for an admin" (fun () ->
            let h = harness (Ok TeamAdmins)

            let unknown =
                assertFact h.Store team (draftWith "cost" (Restricted "unregistered") 2m)

            Expect.equal
                (verdictOf h.Armed (Some admin) FactRetrieval unknown.FactId)
                (Some(FactNotDisclosable "unregistered"))
                "denied")

        testCase "an unreadable output level fails closed, naming why" (fun () ->
            let h = harness (Error "the record is unreadable")

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            let surfaceable = assertFact h.Store team (draftWith "revenue" Surfaceable 1m)

            let verdicts =
                asViewer
                    (Some admin)
                    (h.Armed.Check(team, "p", FactRetrieval, [ restricted.FactId; surfaceable.FactId ]))
                |> Async.RunSynchronously

            Expect.equal
                verdicts[restricted.FactId]
                (FactNotDisclosable ViewerAwareDisclosure.UnreadableRef)
                "restricted denied"

            Expect.equal verdicts[surfaceable.FactId] FactDisclosable "surfaceable unaffected")

        testCase "the ResolvedScope form reads the team from the scope; a personal scope has no team level" (fun () ->
            let h = harness (Ok TeamAdmins)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            let check (scope: ResolvedScope) viewer =
                asViewer viewer (h.Armed.Check(scope, "p", FactToolResult, [ restricted.FactId ]))
                |> Async.RunSynchronously
                |> Map.tryFind restricted.FactId

            Expect.equal
                (check (teamScope team) (Some member'))
                (Some(FactNotDisclosable "sales"))
                "member, team scope"

            Expect.equal (check (teamScope team) (Some admin)) (Some FactDisclosable) "admin, team scope"

            let personal =
                ScopeResolution.ofStorageScope {
                    ScopeId = team
                    Container = "user-alpha"
                    Persist = true
                }

            Expect.equal (check personal (Some member')) (Some FactDisclosable) "no team, no team level")

        testCase "a denial is audited like any other, naming the policy and never the value" (fun () ->
            let h = harness (Ok TeamAdmins)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            verdictOf h.Armed (Some member') FactBrowse restricted.FactId |> ignore

            let rows =
                h.Events.ReadBySource(team, FactEvents.SourceModule)
                |> Async.RunSynchronously
                |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            match rows with
            | [ row ] ->
                Expect.stringContains row.Payload "sales" "the policy"
                Expect.stringContains row.Payload "Browse" "the surface"
                Expect.isFalse (row.Payload.Contains "4242") "never the value"
            | _ -> failtestf "expected one deny row, got %d" rows.Length)
    ]

// ─── The request viewer ──────────────────────────────────────────────

let private contextWith (userId: string) (subject: Subject) (isPlatformAdmin: bool) =
    let ctx = DefaultHttpContext()
    ctx.Items["ToolUp.Subject"] <- box subject
    ctx.Items["ToolUp.UserId"] <- box userId

    if isPlatformAdmin then
        ctx.Items["ToolUp.PlatformRole"] <- box PlatformRole.PlatformAdmin

    // Module permission that does NOT include the module whose output is
    // read: permission governs use, not sight.
    ctx.Items["ToolUp.ModulePermissions"] <- box (Map.ofList [ "Sales", ([]: ModulePermission list) ])
    ctx

let private viewerTests =
    testList "the request viewer" [

        test "read off the items scope resolution stamps; nothing without a subject" {
            let ctx = contextWith "ada" (TeamMember("ada", team)) true

            Expect.equal
                (RequestViewer.ofItems ctx.Items)
                (Some {
                    UserId = "ada"
                    ActiveTeamId = Some team
                    IsPlatformAdmin = true
                })
                "the resolved viewer"

            Expect.isNone (RequestViewer.ofItems (DefaultHttpContext().Items)) "no subject, no viewer"
            Expect.isNone (RequestViewerContext.current ()) "nothing established outside a request"
        }

        testCase
            "the middleware establishes it for the request, and work the request started still reads it after"
            (fun () ->
                let ctx = contextWith "ada" (TeamMember("ada", team)) false
                let mutable during = None
                let mutable later: Task<RequestViewer option> = null
                let release = new TaskCompletionSource<unit>()

                let next =
                    Func<Task>(fun () ->
                        during <- RequestViewerContext.current ()

                        later <-
                            Task.Run<RequestViewer option>(
                                Func<Task<RequestViewer option>>(fun () -> task {
                                    let! () = release.Task
                                    return RequestViewerContext.current ()
                                })
                            )

                        Task.CompletedTask)

                (FactViewerContext.middleware ctx next).Wait()
                // The request has ended; recycle its context the way a server may.
                ctx.Items.Clear()
                release.SetResult()

                Expect.equal (during |> Option.map _.UserId) (Some "ada") "during the request"
                Expect.equal (later.Result |> Option.map _.UserId) (Some "ada") "after it, from the snapshot"
                Expect.isNone (RequestViewerContext.current ()) "nothing leaks past the request")

        testCase
            "a user with no permission to USE a module still receives its published output under the whole-team level"
            (fun () ->
                let h = harness (Ok TeamVisible)

                let restricted =
                    assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

                let ctx = contextWith "mia" (TeamMember("mia", team)) false

                let mutable verdicts = Map.empty

                let next =
                    Func<Task>(fun () -> task {
                        let! v =
                            h.Armed.Check(team, "mia", FactRetrieval, [ restricted.FactId ])
                            |> Async.StartAsTask

                        verdicts <- v
                    })

                (FactViewerContext.middleware ctx next).Wait()
                Expect.equal verdicts[restricted.FactId] FactDisclosable "output is not gated on module permission")
    ]

// ─── Two doors end to end ────────────────────────────────────────────

let private doorTests =
    testList "the doors" [

        testCase
            "tool result: the fact query tool withholds an admin-limited fact from a member, and returns it to an admin"
            (fun () ->
                let h = harness (Ok TeamAdmins)

                let restricted =
                    assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

                let args =
                    """{"subject_hierarchy":"geography","subject_path":"uk","metric":"margin"}"""

                let run viewer =
                    asViewer
                        viewer
                        (FactQueryTool.executeWith
                            h.Store
                            h.Armed
                            None
                            (fun () -> DateTime.UtcNow)
                            (teamScope team)
                            "p"
                            args)
                    |> Async.RunSynchronously

                let forMember = run (Some member')
                let forAdmin = run (Some admin)
                Expect.isFalse (forMember.Contains "4242") "no value for a member"
                Expect.stringContains forMember restricted.FactId "a withheld marker, never a silent omission"
                Expect.stringContains forAdmin "4242" "the value for an admin")

        testCase "browse: one fact's view is refused to a member and served to an admin" (fun () ->
            let h = harness (Ok TeamAdmins)

            let restricted =
                assertFact h.Store team (draftWith "margin" (Restricted "sales") 4242m)

            let deps: FactBrowseHandler.FactBrowseDeps = {
                Tables = Grounding.FactTableRegistry.build [] []
                Writer = None
                Store = h.Store
                Gate = h.Armed
                Registry = None
                Clock = fun () -> DateTime.UtcNow
            }

            let get viewer =
                asViewer viewer (FactBrowseHandler.getFact deps (teamScope team) "p" restricted.FactId)
                |> Async.RunSynchronously

            Expect.isError (get (Some member')) "refused to a member"
            Expect.isOk (get (Some admin)) "served to an admin")
    ]

// ─── The per-team record ────────────────────────────────────────────

type private CapturingAudit() =
    let rows = ResizeArray<AuditEvent>()

    member _.Custom(kind: string) =
        lock rows (fun () -> List.ofSeq rows)
        |> List.choose (fun e ->
            match e with
            | RemotingMethodAudited p when p.Kind = kind -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(_, event) = async { lock rows (fun () -> rows.Add event) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private World = {
    Storage: IBlobStorage
    Teams: ITeamStore
    Audit: CapturingAudit
}

let private newWorld () = async {
    let teams =
        TeamStore(InMemoryBlobStorage() :> IBlobStorage, InMemoryNotificationChannel(None) :> INotificationChannel)
        :> ITeamStore

    let! _ = teams.CreateTeam(team, "Alpha")

    for userId, role in Map.toList roles do
        let! _ = teams.AddMember(team, userId, role)
        ()

    return {
        Storage = InMemoryBlobStorage() :> IBlobStorage
        Teams = teams
        Audit = CapturingAudit()
    }
}

let private requestFor (world: World) (output: TeamOutputVisibilitySettings option) (userId: string) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(world.Storage) |> ignore
    services.AddSingleton<ITeamStore>(world.Teams) |> ignore
    services.AddSingleton<IAuditLog>(world.Audit) |> ignore

    output
    |> Option.iter (fun s -> services.AddSingleton<TeamOutputVisibilitySettings>(s) |> ignore)

    // The assistant is composed in these requests: since Phase 936 it
    // registers the conversation declaration, which is how the platform's
    // output API knows there are conversations to check a change against.
    services.AddSingleton<TeamConversationVisibilitySettings>(TeamConversationVisibilitySettings.unrestricted)
    |> ignore

    services.AddSingleton<AccessContext>(
        {
            AccessContext.unrestricted (TeamMember(userId, team)) with
                PlatformRole =
                    if userId = "pat" then
                        Some PlatformRole.PlatformAdmin
                    else
                        None
        }
    )
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx :> HttpContext

let private enabled = Some TeamOutputVisibilitySettings.unrestricted

let private blobText (world: World) =
    match
        world.Storage.Download(TeamConversationPolicyRecord.containerOf team, TeamConversationPolicyRecord.BlobName)
        |> Async.RunSynchronously
    with
    | Ok bytes -> Encoding.UTF8.GetString bytes
    | Error e -> failtestf "no record: %s" e

let private recordTests =
    testList "stored in the per-team policy record, under its change rules" [

        testCaseAsync
            "the output level lives in the same record as the conversation level, and neither disturbs the other"
        <| async {
            let! world = newWorld ()
            let convApi = teamConversationVisibilityApi (requestFor world enabled "olga")
            let outApi = teamOutputVisibilityApi (requestFor world enabled "olga")

            let! conv = convApi.SetConversationVisibility TeamAdmins
            Expect.isOk conv "narrow conversations first"
            let conversationOnly = blobText world

            Expect.isFalse
                (conversationOnly.Contains TeamOutputPolicyRecord.MemberName)
                "nothing new until output is set"

            let! out = outApi.SetOutputVisibility TeamAdmins
            Expect.isOk out "then output"
            let both = blobText world
            Expect.stringContains both TeamOutputPolicyRecord.MemberName "the output history is in the same record"
            Expect.stringContains both "\"Changes\"" "beside the conversation history"

            let! view = convApi.GetConversationVisibility()
            Expect.equal view.Level TeamAdmins "the conversation level reads back unchanged"
            let! outView = outApi.GetOutputVisibility()
            Expect.equal outView.Level TeamAdmins "the output level reads back"

            // A later conversation change keeps the output part.
            let! _ = convApi.SetConversationVisibility TeamAdmins
            let! outAgain = outApi.GetOutputVisibility()
            Expect.equal outAgain.Level TeamAdmins "kept across a conversation write"

            let! current =
                (TeamPolicyOutputVisibilitySource((requestFor world enabled "olga").RequestServices)
                :> ITeamOutputVisibilitySource)
                    .Current
                    team

            Expect.equal current (Ok TeamAdmins) "the gate's source reads the same record"
        }

        testCaseAsync
            "narrowing output past conversation visibility is refused, naming both — and so is widening conversations past output"
        <| async {
            let! world = newWorld ()
            let convApi = teamConversationVisibilityApi (requestFor world enabled "olga")
            let outApi = teamOutputVisibilityApi (requestFor world enabled "olga")

            match! outApi.SetOutputVisibility TeamAdmins with
            | Error message ->
                Expect.stringContains message "Conversation visibility TeamVisible" "names the conversation level"
                Expect.stringContains message "output visibility TeamAdmins" "names the output level"
            | Ok _ -> failtest "admin-only output under whole-team conversations must be refused"

            let! _ = convApi.SetConversationVisibility TeamAdmins
            let! narrowed = outApi.SetOutputVisibility TeamAdmins
            Expect.isOk narrowed "allowed once conversations are no wider"

            match! convApi.SetConversationVisibility TeamVisible with
            | Error message ->
                Expect.stringContains message "TeamVisible" "names the conversation level"
                Expect.stringContains message "TeamAdmins" "names the output level"
            | Ok _ -> failtest "widening conversations past output must be refused"
        }

        testCaseAsync "only the owner changes it, and every change is audited"
        <| async {
            let! world = newWorld ()

            let! _ =
                (teamConversationVisibilityApi (requestFor world enabled "olga")).SetConversationVisibility TeamAdmins

            let! byAdmin = (teamOutputVisibilityApi (requestFor world enabled "ada")).SetOutputVisibility TeamAdmins
            Expect.isError byAdmin "a team admin may not"
            let! byMember = (teamOutputVisibilityApi (requestFor world enabled "mia")).SetOutputVisibility TeamAdmins
            Expect.isError byMember "a member may not"

            let! byOwner = (teamOutputVisibilityApi (requestFor world enabled "olga")).SetOutputVisibility TeamAdmins
            Expect.isOk byOwner "the owner may"

            match world.Audit.Custom OutputVisibilityAudit.LevelChangedKind with
            | [ row ] ->
                Expect.equal row.Payload["changedBy"] "olga" "who"
                Expect.equal row.Payload["oldLevel"] "TeamVisible" "from"
                Expect.equal row.Payload["newLevel"] "TeamAdmins" "to"
            | rows -> failtestf "expected one change row, got %d" rows.Length

            let! again = (teamOutputVisibilityApi (requestFor world enabled "olga")).SetOutputVisibility TeamAdmins
            Expect.isOk again "a no-op change"
            Expect.equal (world.Audit.Custom OutputVisibilityAudit.LevelChangedKind).Length 1 "a no-op is not audited"
        }

        testCaseAsync "not composed: nothing to set, and the conversation path is unchanged"
        <| async {
            let! world = newWorld ()
            let outApi = teamOutputVisibilityApi (requestFor world None "olga")
            let! view = outApi.GetOutputVisibility()
            Expect.isFalse view.Enabled "not enabled"
            Expect.equal view.Level TeamVisible "whole team"
            let! set = outApi.SetOutputVisibility TeamAdmins
            Expect.isError set "refused"

            let! conv =
                (teamConversationVisibilityApi (requestFor world None "olga")).SetConversationVisibility TeamVisible

            Expect.isOk conv "no policy-change check without output visibility"

            let! convNarrow =
                (teamConversationVisibilityApi (requestFor world None "olga")).SetConversationVisibility TeamAdmins

            Expect.isOk convNarrow "and none when narrowing"
        }

        testCaseAsync
            "the source reads the deployment default for a team that chose nothing, and fails closed on an unreadable record"
        <| async {
            let! world = newWorld ()

            let defaultAdmins =
                TeamOutputVisibilitySettings.create TeamAdmins [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            let source =
                TeamPolicyOutputVisibilitySource((requestFor world (Some defaultAdmins) "olga").RequestServices)
                :> ITeamOutputVisibilitySource

            let! unset = source.Current team
            Expect.equal unset (Ok TeamAdmins) "the deployment default"

            let! _ =
                world.Storage.Upload(
                    TeamConversationPolicyRecord.containerOf team,
                    TeamConversationPolicyRecord.BlobName,
                    Encoding.UTF8.GetBytes """{"Changes":[],"OutputChanges":"not a list"}"""
                )

            let! broken = source.Current team
            Expect.isError broken "unreadable ⇒ Error, and the gate denies"
        }

        testCaseAsync "deployment defaults that already conflict are refused at startup"
        <| async {
            let! world = newWorld ()

            // The assistant's conversation declaration (the unrestricted
            // default), as `AIServerApp` registers it.
            let validate (output: TeamOutputVisibilitySettings) =
                let services = ServiceCollection()

                services.AddSingleton<TeamConversationVisibilitySettings>(
                    TeamConversationVisibilitySettings.unrestricted
                )
                |> ignore

                (TeamVisibilityDefaultsValidator(output, services) :> ConfigValidation.IConfigValidator).Validate()

            let adminsDefault =
                TeamOutputVisibilitySettings.create TeamAdmins [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            match! validate adminsDefault with
            | ConfigValidation.ValidationResult.Error message ->
                Expect.stringContains message "TeamAdmins" "names the output default"
            | other -> failtestf "expected a refusal, got %A" other

            let! fine = validate TeamOutputVisibilitySettings.unrestricted
            Expect.equal fine ConfigValidation.ValidationResult.Ok "the default pair"
        // Registration (only with the output axis) and the one-axis
        // cases are pinned through preflight in the Phase 936 list.
        }
    ]

// ─── Composition ─────────────────────────────────────────────────────

let private composeTests =
    testList "composition" [

        test "withTeamOutputVisibility validates its declaration, and a deployment without a fact store is unchanged" {
            let noFacts = ServerApp.empty

            Expect.equal
                (FactsCompose.withTeamOutputVisibility TeamAdmins [ TeamAdmins ] noFacts)
                    .Extensions.PreMiddleware.Length
                noFacts.Extensions.PreMiddleware.Length
                "NoFactStore: nothing composed"

            let withFacts = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = EnabledFactStore
                    }
            }

            Expect.throws
                (fun () ->
                    FactsCompose.withTeamOutputVisibility TeamAdmins [ TeamVisible ] withFacts
                    |> ignore)
                "a default outside the allowed set fails composition"

            let composed =
                FactsCompose.withTeamOutputVisibility TeamVisible [ TeamVisible; TeamAdmins ] withFacts

            Expect.equal
                composed.Extensions.PreMiddleware.Length
                (withFacts.Extensions.PreMiddleware.Length + 1)
                "the viewer middleware"
        }

        test "the composed gate is decorated in place and armed" {
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events

            let restricted =
                assertFact store team (draftWith "margin" (Restricted "sales") 4242m)

            let taint = DisclosureTaintConfig.ofLists [ salesPolicy ] []

            let withGate = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = EnabledFactStore
                    }
                    Extensions = {
                        ServerApp.empty.Extensions with
                            ServiceConfig =
                                Some(fun s ->
                                    s.AddSingleton<IFactDisclosureGate>(
                                        Func<IServiceProvider, IFactDisclosureGate>(fun _ ->
                                            FactDisclosureGate(store, events, taint = taint) :> IFactDisclosureGate)
                                    ))
                    }
            }

            let composed =
                FactsCompose.withTeamOutputVisibility TeamAdmins [ TeamVisible; TeamAdmins ] withGate

            let services = ServiceCollection()

            let configure =
                composed.Extensions.ServiceConfig
                |> Option.defaultWith (fun () -> failtest "no config")

            let provider = (configure services).BuildServiceProvider()
            let gate = provider.GetRequiredService<IFactDisclosureGate>()

            Expect.equal
                (provider.GetRequiredService<TeamOutputVisibilitySettings>()).Default
                TeamAdmins
                "the declaration is registered"

            // No source composed: every team sits at the declared default,
            // so a member (whose role no team store can confirm) is refused.
            let verdicts =
                asViewer (Some member') (gate.Check(team, "p", FactRetrieval, [ restricted.FactId ]))
                |> Async.RunSynchronously

            Expect.equal verdicts[restricted.FactId] (FactNotDisclosable "sales") "armed"

            Expect.equal
                (services
                 |> Seq.filter (fun d -> d.ServiceType = typeof<IFactDisclosureGate>)
                 |> Seq.length)
                1
                "one gate"
        }
    ]

// ─── Phase 936 — the per-team record at the platform tier ────────────

/// A record exactly as the store wrote it before Phase 936: the owner chose
/// `TeamAdmins` for conversations, then for output. Pinned against the
/// pre-936 store's own serialiser before the move.
let private legacyRecord =
    """{"Changes":[{"Level":"TeamAdmins","ChangedBy":"olga","ChangedAt":"2026-09-01T10:00:00.0000000Z"}],"OutputChanges":[{"Level":"TeamAdmins","ChangedBy":"olga","ChangedAt":"2026-09-01T10:05:00.0000000Z"}]}"""

/// Where every record written before Phase 936 lives, spelled out rather
/// than read from the constant, so renaming the constant goes red here.
let private legacyBlobName = "team-policies/ai-conversation-visibility.json"

/// A composition with the fact store's gate and team output visibility
/// declared at `defaultLevel`. `conversation` stands in for the AI
/// assistant: `Some` registers its conversation declaration the way
/// `AIServerApp` does, `None` is a facts-only deployment. `conversationFirst`
/// picks which of the two is registered first.
let private composeWith
    (world: World)
    (store: IFactStore)
    (events: IEventStore)
    (defaultLevel: TeamVisibilityLevel)
    (conversation: TeamConversationVisibilitySettings option)
    (conversationFirst: bool)
    =
    let taint = DisclosureTaintConfig.ofLists [ salesPolicy ] []

    let withGate = {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    FactStore = EnabledFactStore
            }
            Extensions = {
                ServerApp.empty.Extensions with
                    ServiceConfig =
                        Some(fun s ->
                            s.AddSingleton<IFactDisclosureGate>(
                                Func<IServiceProvider, IFactDisclosureGate>(fun _ ->
                                    FactDisclosureGate(store, events, taint = taint) :> IFactDisclosureGate)
                            ))
            }
    }

    let composed =
        FactsCompose.withTeamOutputVisibility defaultLevel [ TeamVisible; TeamAdmins ] withGate

    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(world.Storage) |> ignore
    services.AddSingleton<ITeamStore>(world.Teams) |> ignore
    services.AddSingleton<IAuditLog>(world.Audit) |> ignore

    let addConversation () =
        conversation
        |> Option.iter (fun c -> services.AddSingleton<TeamConversationVisibilitySettings>(c) |> ignore)

    if conversationFirst then
        addConversation ()

    let configure =
        composed.Extensions.ServiceConfig
        |> Option.defaultWith (fun () -> failtest "no config")

    configure services |> ignore

    if not conversationFirst then
        addConversation ()

    composed, services

let private factsOnly world store events =
    composeWith world store events TeamVisible None true

/// A request in `services`, by `userId` in the team.
let private requestIn (services: ServiceCollection) (userId: string) : HttpContext =
    services.AddSingleton<AccessContext>(
        {
            AccessContext.unrestricted (TeamMember(userId, team)) with
                PlatformRole =
                    if userId = "pat" then
                        Some PlatformRole.PlatformAdmin
                    else
                        None
        }
    )
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx :> HttpContext

let private platformTierTests =
    testList "Phase 936 — the per-team record at the platform tier" [

        testCaseAsync "a facts-only composition honours a team owner's output level"
        <| async {
            let! world = newWorld ()

            let! _ = world.Storage.Upload($"team-{team}", legacyBlobName, Encoding.UTF8.GetBytes legacyRecord)

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events

            let restricted =
                assertFact store team (draftWith "margin" (Restricted "sales") 4242m)

            let _, services = factsOnly world store events
            let gate = services.BuildServiceProvider().GetRequiredService<IFactDisclosureGate>()

            Expect.equal
                (verdictOf gate (Some member') FactRetrieval restricted.FactId)
                (Some(FactNotDisclosable "sales"))
                "the owner chose TeamAdmins: a member is refused, not given the deployment default"

            Expect.equal
                (verdictOf gate (Some owner) FactRetrieval restricted.FactId)
                (Some FactDisclosable)
                "the owner sees it"
        }

        test "the stored shape is the legacy one, byte for byte" {
            let at minute =
                DateTime(2026, 9, 1, 10, minute, 0, DateTimeKind.Utc)

            let conversation: TeamConversationPolicyRecord = {
                Changes = [
                    {
                        Level = TeamAdmins
                        ChangedBy = "olga"
                        ChangedAt = at 0
                    }
                ]
            }

            let output: TeamOutputPolicyRecord = {
                OutputChanges = [
                    {
                        Level = TeamAdmins
                        ChangedBy = "olga"
                        ChangedAt = at 5
                    }
                ]
            }

            Expect.equal
                (Encoding.UTF8.GetString(TeamPolicyRecordCodec.serialise conversation output))
                legacyRecord
                "the literal is what the store writes"

            Expect.equal TeamConversationPolicyRecord.BlobName legacyBlobName "the blob name is kept"
        }

        testCaseAsync "a record written before the move reads back the same levels, on both axes"
        <| async {
            let! world = newWorld ()

            let! _ = world.Storage.Upload($"team-{team}", legacyBlobName, Encoding.UTF8.GetBytes legacyRecord)

            let store = TeamPolicyStore world.Storage

            match! store.Read $"team-{team}" with
            | Ok record ->
                Expect.equal (TeamConversationPolicyRecord.current TeamVisible record) TeamAdmins "conversation level"

                Expect.equal
                    (TeamConversationPolicyRecord.levelAt
                        TeamVisible
                        record
                        (DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc)))
                    TeamVisible
                    "before the change, the default"
            | Error e -> failtestf "unreadable: %s" e

            match! store.ReadOutput $"team-{team}" with
            | Ok output -> Expect.equal (TeamOutputPolicyRecord.current TeamVisible output) TeamAdmins "output level"
            | Error e -> failtestf "unreadable: %s" e

            // Through both APIs, as a deployment with the assistant reads it.
            let! conv = (teamConversationVisibilityApi (requestFor world enabled "mia")).GetConversationVisibility()
            Expect.equal conv.Level TeamAdmins "the assistant reads the conversation level"
            let! out = (teamOutputVisibilityApi (requestFor world enabled "mia")).GetOutputVisibility()
            Expect.equal out.Level TeamAdmins "the platform reads the output level"

            // A no-op rewrites nothing; a real write lands in the same blob
            // and keeps the other axis.
            let! kept =
                (teamConversationVisibilityApi (requestFor world enabled "olga")).SetConversationVisibility TeamAdmins

            Expect.isOk kept "a no-op change by the owner"
            Expect.equal (blobText world) legacyRecord "a no-op change rewrites nothing"

            let! widened = (teamOutputVisibilityApi (requestFor world enabled "olga")).SetOutputVisibility TeamVisible
            Expect.isOk widened "the owner widens output"

            let! convAfter =
                (teamConversationVisibilityApi (requestFor world enabled "mia")).GetConversationVisibility()

            Expect.equal convAfter.Level TeamAdmins "the conversation level is kept"
            let! outAfter = (teamOutputVisibilityApi (requestFor world enabled "mia")).GetOutputVisibility()
            Expect.equal outAfter.Level TeamVisible "the output change is read back from the same blob"
        }

        testCaseAsync "facts-only: an owner sets the output level through the platform API, and the gate applies it"
        <| async {
            let! world = newWorld ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events

            let restricted =
                assertFact store team (draftWith "margin" (Restricted "sales") 4242m)

            let services () = snd (factsOnly world store events)

            let! view = (teamOutputVisibilityApi (requestIn (services ()) "olga")).GetOutputVisibility()
            Expect.isTrue view.Enabled "enabled without the assistant"
            Expect.equal view.Selectable [ TeamVisible; TeamAdmins ] "the owner may choose"

            let! byMember = (teamOutputVisibilityApi (requestIn (services ()) "mia")).SetOutputVisibility TeamAdmins
            Expect.isError byMember "a member may not"

            // No conversation axis: nothing can quote output, so no
            // policy-change check refuses narrowing it.
            let! byOwner = (teamOutputVisibilityApi (requestIn (services ()) "olga")).SetOutputVisibility TeamAdmins
            Expect.isOk byOwner "the owner may, with no assistant composed"

            Expect.equal (world.Audit.Custom OutputVisibilityAudit.LevelChangedKind).Length 1 "the change is audited"

            let gate =
                (services ()).BuildServiceProvider().GetRequiredService<IFactDisclosureGate>()

            Expect.equal
                (verdictOf gate (Some member') FactRetrieval restricted.FactId)
                (Some(FactNotDisclosable "sales"))
                "the gate applies the owner's choice"
        }

        testCaseAsync "the startup check runs through preflight, whichever axis is composed first"
        <| async {
            let! world = newWorld ()
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create (InMemoryBlobStorage()) events

            let preflight defaultLevel conversation conversationFirst =
                let _, services =
                    composeWith world store events defaultLevel conversation conversationFirst

                try
                    ConfigValidatorAggregator.validate services None false
                    |> List.filter (fun o -> o.Name = TeamVisibilityDefaultsValidator.ValidatorName)
                    |> List.map _.Result
                    |> Ok
                with :? ConfigValidatorAggregator.ConfigPreflightFailedException as ex ->
                    Error ex.Message

            // Output only (no assistant): nothing to conflict with.
            Expect.equal
                (preflight TeamAdmins None true)
                (Ok [ ConfigValidation.ValidationResult.Ok ])
                "output axis alone passes, and is registered as a runnable instance"

            // Both axes, conflicting defaults: refused in either order.
            for conversationFirst in [ true; false ] do
                match preflight TeamAdmins (Some TeamConversationVisibilitySettings.unrestricted) conversationFirst with
                | Error message -> Expect.stringContains message "TeamAdmins" "names the output default"
                | Ok results ->
                    failtestf "expected a refusal (conversation first: %b), got %A" conversationFirst results

            // Both axes, agreeing defaults.
            let admins =
                TeamConversationVisibilitySettings.create TeamAdmins [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            Expect.equal
                (preflight TeamAdmins (Some admins) false)
                (Ok [ ConfigValidation.ValidationResult.Ok ])
                "agreeing defaults pass"

            // Composing the axis twice registers one check, not two.
            let composed, _ = composeWith world store events TeamVisible None true

            let twice =
                FactsCompose.withTeamOutputVisibility TeamVisible [ TeamVisible ] composed

            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(world.Storage) |> ignore
            (twice.Extensions.ServiceConfig |> Option.get) services |> ignore

            Expect.equal
                (ConfigValidatorAggregator.validate services None false
                 |> List.filter (fun o -> o.Name = TeamVisibilityDefaultsValidator.ValidatorName)
                 |> List.length)
                1
                "one check"
        }

        test "the declaration is projected onto the composition manifest" {
            let withFacts = {
                ServerApp.empty with
                    Config = {
                        ServerConfig.defaults with
                            FactStore = EnabledFactStore
                    }
            }

            let knobsOf app =
                (ServerApp.compositionManifest app).ConfigKnobs
                |> List.filter (fun k -> k.Name.StartsWith CompositionManifest.TeamOutputVisibilityKnobPrefix)
                |> List.map (fun k -> k.Name, k.Value)

            Expect.isEmpty (knobsOf withFacts) "not composed: no knob, the manifest is unchanged"

            Expect.equal
                (knobsOf (FactsCompose.withTeamOutputVisibility TeamAdmins [ TeamAdmins; TeamVisible ] withFacts))
                [
                    "TeamOutputVisibility.Default", "TeamAdmins"
                    "TeamOutputVisibility.Allowed", "TeamVisible, TeamAdmins"
                ]
                "default and allowed set"
        }
    ]

let tests =
    testList "Phase 896 team output visibility" [
        platformTierTests
        ruleTests
        resolverTests
        surfaceTests
        gateTests
        viewerTests
        doorTests
        recordTests
        composeTests
    ]