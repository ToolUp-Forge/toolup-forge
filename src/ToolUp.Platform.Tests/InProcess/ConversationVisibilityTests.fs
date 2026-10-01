// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationVisibilityTests

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.NotificationChannel
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.TeamPolicyStore
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.AI.TeamConversationPolicyStore
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 859 — team conversation visibility ────────────────────
//
// 859.A — the pure rule (`canSee` / `canModify`), pinned as a table.
// 859.B — every read path (list, page, search, open) and task status,
//         per level × role, through the real handler; a search hit never
//         surfaces a conversation its viewer cannot open.
// 859.C — delete (and the provider override, the other write) by the
//         level's elevated role only; an invisible conversation is a no-op.
// 859.D — per-team records, the deployment default and allowed set, and a
//         team that set nothing behaving exactly as before.
// 859.E — elevated opens and level changes audited; listings not.
// 859.G — who may change the level; narrowing at once; widening never
//         exposes the past; the guarded write.
//
// The cast: team `alpha` holds alice (Member, the author), bob (Member),
// ada (Admin), olga (Owner), pat (Member + platform admin) and rhea
// (Owner + platform admin). xavier belongs only to team `beta`.

let private jsonOptions = FableConverters.create ()
let private t0 = DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)

let private containerOf (teamId: string) = $"team-{teamId}"

// ─── Fakes ───────────────────────────────────────────────────────

/// The handler casts its factory unconditionally; nothing here chats.
type private NoChatFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return failwith "no chat in these tests" }
        member _.TryResolveByLabel(_, _) = async { return failwith "no chat in these tests" }
        member _.BuildPlatform(_, _, _) = None

type private CapturingAudit() =
    let rows = ResizeArray<string * AuditEvent>()

    member _.Rows = lock rows (fun () -> List.ofSeq rows)

    member this.Custom(kind: string) =
        this.Rows
        |> List.choose (fun (_, e) ->
            match e with
            | RemotingMethodAudited p when p.Kind = kind -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, event) = async { lock rows (fun () -> rows.Add((scopeId, event))) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private World = {
    Storage: IBlobStorage
    Teams: ITeamStore
    Audit: CapturingAudit
    Settings: TeamConversationVisibilitySettings option
}

let private platformAdmins = set [ "pat"; "rhea" ]

let private newWorld (settings: TeamConversationVisibilitySettings option) = async {
    let teams =
        TeamStore(InMemoryBlobStorage() :> IBlobStorage, InMemoryNotificationChannel(None) :> INotificationChannel)
        :> ITeamStore

    let! _ = teams.CreateTeam("alpha", "Alpha")
    let! _ = teams.CreateTeam("beta", "Beta")

    for userId, role in
        [
            "alice", TeamRole.Member
            "bob", TeamRole.Member
            "ada", TeamRole.Admin
            "olga", TeamRole.Owner
            "pat", TeamRole.Member
            "rhea", TeamRole.Owner
            "mia", TeamRole.Member
        ] do
        let! _ = teams.AddMember("alpha", userId, role)
        ()

    for userId, role in [ "xavier", TeamRole.Owner; "mia", TeamRole.Member; "ben", TeamRole.Member ] do
        let! _ = teams.AddMember("beta", userId, role)
        ()

    return {
        Storage = InMemoryBlobStorage() :> IBlobStorage
        Teams = teams
        Audit = CapturingAudit()
        Settings = settings
    }
}

/// What the assistant handler resolves unconditionally, over `storage`.
let private chatServices (storage: IBlobStorage) : ServiceCollection =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IAIProviderFactory>(NoChatFactory()) |> ignore

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(AIToolRegistry.AIToolRegistry())
    |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    services

/// A request by `userId` in `teamId`'s scope, with `extra` registered last
/// (a later registration wins `GetService`).
let private contextForWith
    (extra: ServiceCollection -> unit)
    (world: World)
    (teamId: string)
    (userId: string)
    : HttpContext =
    let services = chatServices world.Storage
    services.AddSingleton<ITeamStore>(world.Teams) |> ignore
    services.AddSingleton<IAuditLog>(world.Audit) |> ignore

    world.Settings
    |> Option.iter (fun s -> services.AddSingleton<TeamConversationVisibilitySettings>(s) |> ignore)

    let access = {
        AccessContext.unrestricted (TeamMember(userId, teamId)) with
            PlatformRole =
                if platformAdmins.Contains userId then
                    Some PlatformRole.PlatformAdmin
                else
                    None
    }

    services.AddSingleton<AccessContext>(access) |> ignore
    extra services

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = teamId
            Container = containerOf teamId
            Persist = true
        }

    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

/// A request by `userId` in `teamId`'s scope.
let private contextFor (world: World) (teamId: string) (userId: string) : HttpContext =
    contextForWith ignore world teamId userId

let private assistant (world: World) (teamId: string) (userId: string) : AIAssistantApi =
    let manager = new SSEConnectionManager()
    fst (aiAssistantApi None Map.empty manager (contextFor world teamId userId))

let private policyApi (world: World) (teamId: string) (userId: string) =
    teamConversationVisibilityApi (contextFor world teamId userId)

let private message (conversationId: Guid) (participant: ParticipantType) (content: string) (at: DateTime) author =
    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = participant
        Content = content
        Timestamp = at
        ToolCalls = []
        RetrievedSources = []
        Parts = []
        CreatedBy = author
        BeaconId = ""
        Verification = None
    }
    : ConversationMessage

/// Seed a conversation by `author` in `teamId`, started at `at`.
let private seed (world: World) (teamId: string) (author: string) (content: string) (at: DateTime) = async {
    let id = Guid.NewGuid()

    let messages = [
        message id User content at author
        message id AIAssistant "noted" (at.AddMinutes 1.0) author
    ]

    let! _ =
        world.Storage.Upload(
            containerOf teamId,
            $"ai-conversations/{id}.json",
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(messages, jsonOptions))
        )

    return id
}

let private exists (world: World) (teamId: string) (id: Guid) =
    world.Storage.Exists(containerOf teamId, $"ai-conversations/{id}.json")

/// Write a team's record directly, each change at the instant given.
let private setRecord (world: World) (teamId: string) (changes: (TeamConversationVisibility * DateTime) list) = async {
    let record: TeamConversationPolicyRecord = {
        Changes =
            changes
            |> List.map (fun (level, at) -> {
                Level = level
                ChangedBy = "olga"
                ChangedAt = at
            })
    }

    let! _ =
        world.Storage.Upload(
            containerOf teamId,
            TeamConversationPolicyRecord.BlobName,
            TeamPolicyRecordCodec.serialise record TeamOutputPolicyRecord.empty
        )

    ()
}

let private searchFor (term: string) = {
    ConversationListQuery.firstPage with
        Search = Some term
}

// ─── The expected table, written out rather than derived ─────────

type private Level =
    | Unset
    | Chosen of TeamConversationVisibility

let private levelName =
    function
    | Unset -> "unset (default TeamVisible)"
    | Chosen l -> TeamConversationVisibility.name l

/// `(label, userId, team the request is scoped to)`.
let private viewers = [
    "author", "alice", "alpha"
    "member", "bob", "alpha"
    "team admin", "ada", "alpha"
    "team owner", "olga", "alpha"
    "platform admin", "pat", "alpha"
    "another team's member", "xavier", "beta"
]

let private expectedSee (level: Level) (userId: string) =
    match level, userId with
    | _, "alice" -> true
    | _, "xavier" -> false
    | (Unset | Chosen TeamVisible), _ -> true
    | Chosen TeamAdmins, ("ada" | "olga") -> true
    | Chosen PlatformAdmins, "pat" -> true
    | _ -> false

let private expectedModify (level: Level) (userId: string) =
    match level, userId with
    | _, "alice" -> true
    | (Unset | Chosen TeamVisible | Chosen TeamAdmins), ("ada" | "olga") -> true
    | Chosen PlatformAdmins, "pat" -> true
    | _ -> false

let private allLevels = [ Unset; Chosen TeamVisible; Chosen TeamAdmins; Chosen PlatformAdmins ]

let private applyLevel (world: World) (level: Level) = async {
    match level with
    | Unset -> ()
    | Chosen l -> do! setRecord world "alpha" [ l, t0.AddDays -1.0 ]
}

// ─── 859.A — the pure rule ───────────────────────────────────────

let private pureRuleTests =
    testList "859.A — canSee and canModify are the rule" [
        testCase "canSee over every level and role"
        <| fun () ->
            let see level viewer role pa =
                TeamConversationVisibility.canSee level viewer "alice" role pa

            // The author, at every level, whatever their role.
            for level in TeamConversationVisibility.all do
                Expect.isTrue (see level "alice" (Some TeamRole.Member) false) "the author always sees"

            Expect.isTrue (see TeamVisible "bob" (Some TeamRole.Member) false) "TeamVisible: a member"
            Expect.isFalse (see TeamVisible "x" None false) "TeamVisible: not a member"
            Expect.isFalse (see TeamAdmins "bob" (Some TeamRole.Member) false) "TeamAdmins: a member"
            Expect.isTrue (see TeamAdmins "ada" (Some TeamRole.Admin) false) "TeamAdmins: an admin"
            Expect.isTrue (see TeamAdmins "olga" (Some TeamRole.Owner) false) "TeamAdmins: an owner"
            Expect.isFalse (see TeamAdmins "pat" (Some TeamRole.Member) true) "TeamAdmins: a platform admin member"
            Expect.isFalse (see PlatformAdmins "olga" (Some TeamRole.Owner) false) "PlatformAdmins: an owner"
            Expect.isTrue (see PlatformAdmins "pat" (Some TeamRole.Member) true) "PlatformAdmins: a platform admin"

        testCase "a conversation with no recorded author exempts nobody"
        <| fun () ->
            Expect.isFalse
                (TeamConversationVisibility.canSee TeamAdmins "" "" (Some TeamRole.Member) false)
                "an empty viewer id is not the empty author"

            Expect.isTrue
                (TeamConversationVisibility.canSee TeamVisible "bob" "" (Some TeamRole.Member) false)
                "TeamVisible still shows it to members"

        testCase "canModify: the author, else visible and elevated"
        <| fun () ->
            let modify level viewer role pa =
                TeamConversationVisibility.canModify level viewer "alice" role pa

            Expect.isTrue (modify PlatformAdmins "alice" (Some TeamRole.Member) false) "the author"
            Expect.isFalse (modify TeamVisible "bob" (Some TeamRole.Member) false) "TeamVisible: a member cannot"
            Expect.isTrue (modify TeamVisible "ada" (Some TeamRole.Admin) false) "TeamVisible: an admin can"
            Expect.isTrue (modify TeamAdmins "olga" (Some TeamRole.Owner) false) "TeamAdmins: an owner can"
            Expect.isFalse (modify PlatformAdmins "olga" (Some TeamRole.Owner) false) "PlatformAdmins: an owner cannot"

            Expect.isTrue
                (modify PlatformAdmins "pat" (Some TeamRole.Member) true)
                "PlatformAdmins: a platform admin can"
    ]

// ─── 859.B / 859.C — every path, every level, every role ─────────

let private matrixCase (level: Level) (label: string, userId: string, teamId: string) =
    testCaseAsync $"{levelName level} × {label}"
    <| async {
        let! world = newWorld None
        let! id = seed world "alpha" "alice" "the quarterly forecast" t0
        do! applyLevel world level

        let see = expectedSee level userId
        let modify = expectedModify level userId
        let api = assistant world teamId userId
        let what = $"{levelName level}, {label}"

        let! listed = api.ListConversations()
        Expect.equal (listed |> List.exists (fun c -> c.Id = id)) see $"list — {what}"

        let! page = api.ListConversationsPage ConversationListQuery.firstPage
        Expect.equal (page.Items |> List.exists (fun c -> c.Id = id)) see $"page — {what}"
        Expect.equal page.TotalCount (if see then 1 else 0) $"page count — {what}"

        let! found = api.ListConversationsPage(searchFor "forecast")
        Expect.equal (found.Items |> List.exists (fun c -> c.Id = id)) see $"search — {what}"
        Expect.equal found.TotalCount (if see then 1 else 0) $"search count — {what}"

        let! opened = api.GetConversation id
        Expect.equal (not opened.IsEmpty) see $"open — {what}"

        // The author's task: readable by its submitter only, at every level.
        let task: AITask = {
            TaskId = Guid.NewGuid()
            ConversationId = id
            Prompt = "the quarterly forecast"
            Status = InProgress
            CreatedAt = DateTime.UtcNow
            CompletedAt = None
        }

        AITaskStatusRegistry.Shared.Register(containerOf "alpha", "alice", task, DateTime.UtcNow)
        let! status = api.GetTaskStatus task.TaskId
        Expect.equal status.IsSome (userId = "alice") $"task status — {what}"

        let! deletion = api.DeleteConversation id
        let! stillThere = exists world "alpha" id

        if modify then
            Expect.isOk deletion $"delete allowed — {what}"
            Expect.isFalse stillThere $"deleted — {what}"
        elif see then
            Expect.isError deletion $"delete refused — {what}"
            Expect.isTrue stillThere $"kept — {what}"
        else
            // Not visible: exactly what a missing id gets, and nothing goes.
            Expect.isOk deletion $"delete is a no-op — {what}"
            Expect.isTrue stillThere $"kept — {what}"
    }

let private matrixTests =
    testList "859.B/C — list, page, search, open, task status and delete, per level and role" [
        for level in allLevels do
            for viewer in viewers do
                matrixCase level viewer
    ]

let private readPathTests =
    testList "859.B — read paths" [
        testCaseAsync "a search hit is always a conversation its viewer can open"
        <| async {
            for level in allLevels do
                let! world = newWorld None
                let! _ = seed world "alpha" "alice" "budget secret one" t0
                let! _ = seed world "alpha" "bob" "budget secret two" (t0.AddHours 1.0)
                let! _ = seed world "alpha" "ada" "budget secret three" (t0.AddHours 2.0)
                let! _ = seed world "alpha" "" "budget secret legacy" (t0.AddHours 3.0)
                do! applyLevel world level

                for _, userId, teamId in viewers do
                    let api = assistant world teamId userId
                    let! hits = api.ListConversationsPage(searchFor "secret")

                    for hit in hits.Items do
                        let! opened = api.GetConversation hit.Id

                        Expect.isNonEmpty
                            opened
                            $"{levelName level}, {userId}: search surfaced {hit.Id}, which does not open"

                    Expect.equal
                        hits.TotalCount
                        hits.Items.Length
                        $"{levelName level}, {userId}: the count is what is shown"
        }

        testCaseAsync "every author sees their own conversation at every level"
        <| async {
            for level in allLevels do
                let! world = newWorld None
                let! bobs = seed world "alpha" "bob" "bob's notes" t0
                do! applyLevel world level
                let! listed = (assistant world "alpha" "bob").ListConversations()
                Expect.equal (listed |> List.map _.Id) [ bobs ] $"{levelName level}: bob lists his own"
        }

        testCaseAsync "a conversation with no recorded author follows the level alone"
        <| async {
            let! world = newWorld None
            let! legacy = seed world "alpha" "" "pre-6j.D conversation" t0

            let! asMember = (assistant world "alpha" "bob").GetConversation legacy
            Expect.isNonEmpty asMember "unset: a member still sees it"

            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! hidden = (assistant world "alpha" "bob").GetConversation legacy
            Expect.isEmpty hidden "TeamAdmins: a member no longer does"
            let! asAdmin = (assistant world "alpha" "ada").GetConversation legacy
            Expect.isNonEmpty asAdmin "TeamAdmins: an admin does"
        }

        testCaseAsync "a personal scope is not governed by any team policy"
        <| async {
            let! world = newWorld None
            let services = chatServices world.Storage

            services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser "alice"))
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()

            ctx.Items["ToolUp.StorageScope"] <-
                box {
                    ScopeId = "alice"
                    Container = "user-alice"
                    Persist = true
                }

            ctx.Items["ToolUp.UserId"] <- box "alice"
            let id = Guid.NewGuid()

            let! _ =
                world.Storage.Upload(
                    "user-alice",
                    $"ai-conversations/{id}.json",
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize([ message id User "mine" t0 "alice" ], jsonOptions))
                )

            let api = fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx)
            let! listed = api.ListConversations()
            Expect.equal (listed |> List.map _.Id) [ id ] "listed as before"

            let! view = (teamConversationVisibilityApi ctx).GetConversationVisibility()
            Expect.isFalse view.InTeamScope "no team, nothing to show"
            Expect.isEmpty view.Selectable "and nothing to set"
            let! set = (teamConversationVisibilityApi ctx).SetConversationVisibility TeamAdmins
            Expect.isError set "setting a level outside a team is refused"
        }
    ]

let private writePathTests =
    testList "859.C — writes" [
        testCaseAsync "the provider override follows the same rule as delete"
        <| async {
            let! world = newWorld None
            let! id = seed world "alpha" "alice" "a conversation" t0
            let! byMember = (assistant world "alpha" "bob").SetConversationOverride(id, Some "cheap-model")
            Expect.isError byMember "a member cannot redirect another member's conversation"
            let! byAdmin = (assistant world "alpha" "ada").SetConversationOverride(id, Some "cheap-model")
            Expect.isOk byAdmin "a team admin can"

            do! setRecord world "alpha" [ PlatformAdmins, t0.AddDays -1.0 ]
            let! hidden = (assistant world "alpha" "ada").SetConversationOverride(id, None)
            Expect.isOk hidden "invisible: the same no-op a missing id gets"

            let! meta = world.Storage.Download(containerOf "alpha", $"ai-conversations/{id}.meta.json")

            match meta with
            | Ok bytes -> Expect.stringContains (Encoding.UTF8.GetString bytes) "cheap-model" "the no-op wrote nothing"
            | Error e -> failtestf "meta missing: %s" e
        }
    ]

// ─── 859.D — per team, deployment default, byte-identical unset ──

let private perTeamTests =
    testList "859.D — per-team storage and the deployment default" [
        testCaseAsync "two teams at different levels, one user in both"
        <| async {
            let! world = newWorld None
            let! inAlpha = seed world "alpha" "alice" "alpha work" t0
            let! inBeta = seed world "beta" "ben" "beta work" t0
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            do! setRecord world "beta" [ TeamVisible, t0.AddDays -1.0 ]

            let! alphaList = (assistant world "alpha" "mia").ListConversations()
            Expect.isFalse (alphaList |> List.exists (fun c -> c.Id = inAlpha)) "alpha is TeamAdmins: hidden"
            let! betaList = (assistant world "beta" "mia").ListConversations()
            Expect.isTrue (betaList |> List.exists (fun c -> c.Id = inBeta)) "beta is TeamVisible: shown"

            let! alphaView = (policyApi world "alpha" "mia").GetConversationVisibility()
            let! betaView = (policyApi world "beta" "mia").GetConversationVisibility()
            Expect.equal alphaView.Level TeamAdmins "alpha's own level"
            Expect.equal betaView.Level TeamVisible "beta's own level"
        }

        testCaseAsync "the composed default governs a team that has set nothing"
        <| async {
            let settings =
                TeamConversationVisibilitySettings.create TeamAdmins [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            let! world = newWorld (Some settings)
            let! id = seed world "alpha" "alice" "under a narrow default" t0
            let! asMember = (assistant world "alpha" "bob").GetConversation id
            Expect.isEmpty asMember "a member is refused under the TeamAdmins default"
            let! view = (policyApi world "alpha" "olga").GetConversationVisibility()
            Expect.equal view.Level TeamAdmins "the default is what is shown"
            Expect.equal view.Allowed [ TeamVisible; TeamAdmins ] "the allowed set"
        }

        testCaseAsync "no composition and no setting: exactly the pre-859 listing, with no team store at all"
        <| async {
            // The 516 shape: no AccessContext, no ITeamStore, no audit log.
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let services = chatServices storage

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()

            ctx.Items["ToolUp.StorageScope"] <-
                box {
                    ScopeId = "alpha"
                    Container = "team-alpha"
                    Persist = true
                }

            ctx.Items["ToolUp.UserId"] <- box "bob"
            let id = Guid.NewGuid()

            let! _ =
                storage.Upload(
                    "team-alpha",
                    $"ai-conversations/{id}.json",
                    Encoding.UTF8.GetBytes(
                        JsonSerializer.Serialize([ message id User "alice's" t0 "alice" ], jsonOptions)
                    )
                )

            let api = fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx)
            let! listed = api.ListConversations()
            Expect.equal (listed |> List.map _.Id) [ id ] "another member's conversation is listed, as before"
            let! opened = api.GetConversation id
            Expect.isNonEmpty opened "and opens, as before"
        }

        testCase "the declaration is validated"
        <| fun () ->
            Expect.isError (TeamConversationVisibilitySettings.create TeamVisible []) "an empty allowed set"

            Expect.isError
                (TeamConversationVisibilitySettings.create PlatformAdmins [ TeamVisible; TeamAdmins ])
                "a default outside the allowed set"

            Expect.equal
                (TeamConversationVisibilitySettings.create TeamVisible [ PlatformAdmins; TeamVisible ]
                 |> Result.map _.Allowed)
                (Ok [ TeamVisible; PlatformAdmins ])
                "the allowed set, widest first"

        testCaseAsync "an unreadable record fails closed to authors"
        <| async {
            let! world = newWorld None
            let! alices = seed world "alpha" "alice" "alice's" t0

            let! _ =
                world.Storage.Upload(
                    containerOf "alpha",
                    TeamConversationPolicyRecord.BlobName,
                    Encoding.UTF8.GetBytes "{ not json"
                )

            let! asOwner = (assistant world "alpha" "olga").ListConversations()
            Expect.isEmpty asOwner "not even the owner sees another member's conversation"
            let! asAuthor = (assistant world "alpha" "alice").ListConversations()
            Expect.equal (asAuthor |> List.map _.Id) [ alices ] "the author still sees her own"
            let! view = (policyApi world "alpha" "alice").GetConversationVisibility()
            Expect.equal view.Level PlatformAdmins "the narrowest level is shown, never a wider one"
        }
    ]

// ─── 859.E — audit ───────────────────────────────────────────────

let private auditTests =
    testList "859.E — elevated opens and level changes are audited" [
        testCaseAsync "an elevated open names viewer, author and conversation; a listing does not audit"
        <| async {
            let! world = newWorld None
            let! id = seed world "alpha" "bob" "bob's" t0
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]

            let api = assistant world "alpha" "olga"
            let! _ = api.ListConversations()
            let! _ = api.ListConversationsPage(searchFor "bob")
            Expect.isEmpty (world.Audit.Custom ConversationVisibilityAudit.ElevatedReadKind) "listing is not audited"

            let! opened = api.GetConversation id
            Expect.isNonEmpty opened "the owner opens it"

            match world.Audit.Custom ConversationVisibilityAudit.ElevatedReadKind with
            | [ row ] ->
                Expect.equal row.Payload["viewer"] "olga" "viewer"
                Expect.equal row.Payload["author"] "bob" "author"
                Expect.equal row.Payload["conversationId"] (id.ToString()) "conversation"
                Expect.equal row.Payload["level"] "TeamAdmins" "level"
            | rows -> failtestf "expected one elevated-read row, got %d" rows.Length
        }

        testCaseAsync "an author's own open, and any open under TeamVisible, is not audited"
        <| async {
            let! world = newWorld None
            let! id = seed world "alpha" "bob" "bob's" t0
            let! _ = (assistant world "alpha" "olga").GetConversation id
            do! setRecord world "alpha" [ PlatformAdmins, t0.AddDays -1.0 ]
            let! _ = (assistant world "alpha" "bob").GetConversation id
            Expect.isEmpty (world.Audit.Custom ConversationVisibilityAudit.ElevatedReadKind) "nothing elevated"
        }

        testCaseAsync "a level change names who, the old level and the new; a no-op change writes nothing"
        <| async {
            let! world = newWorld None
            let api = policyApi world "alpha" "olga"
            let! changed = api.SetConversationVisibility TeamAdmins
            Expect.isOk changed "the owner changes it"
            let! again = api.SetConversationVisibility TeamAdmins
            Expect.isOk again "setting the same level succeeds"

            match world.Audit.Custom ConversationVisibilityAudit.LevelChangedKind with
            | [ row ] ->
                Expect.equal row.Payload["changedBy"] "olga" "who"
                Expect.equal row.Payload["oldLevel"] "TeamVisible" "old"
                Expect.equal row.Payload["newLevel"] "TeamAdmins" "new"
                Expect.equal row.Payload["teamId"] "alpha" "team"
            | rows -> failtestf "expected one level-change row, got %d" rows.Length
        }
    ]

// ─── 859.G — who may change it, and what a change does ───────────

let private changeTests =
    testList "859.G — changing the level" [
        testCaseAsync "who may select what"
        <| async {
            let! world = newWorld None

            let set userId level =
                (policyApi world "alpha" userId).SetConversationVisibility level

            let! adminTeamAdmins = set "ada" TeamAdmins
            Expect.isError adminTeamAdmins "a team admin selecting TeamAdmins is refused"
            let! adminTeamVisible = set "ada" TeamVisible
            Expect.isError adminTeamVisible "a team admin changes nothing"
            let! memberSet = set "bob" TeamAdmins
            Expect.isError memberSet "a member changes nothing"
            let! platformAdminMember = set "pat" PlatformAdmins
            Expect.isError platformAdminMember "a platform admin who is not the owner changes nothing"
            let! ownerPlatform = set "olga" PlatformAdmins
            Expect.isError ownerPlatform "an owner who is not a platform admin cannot select PlatformAdmins"

            let! rheaPlatform = set "rhea" PlatformAdmins
            Expect.isOk rheaPlatform "an owner who is a platform admin can"
            let! ownerLeaves = set "olga" TeamVisible
            Expect.isError ownerLeaves "leaving PlatformAdmins needs a platform admin too"
            let! rheaLeaves = set "rhea" TeamAdmins
            Expect.isOk rheaLeaves "which rhea is"
            let! ownerWidens = set "olga" TeamVisible
            Expect.isOk ownerWidens "the owner sets any other allowed level"
        }

        testCaseAsync "what each caller is offered matches what they may do"
        <| async {
            let! world = newWorld None

            let offered userId = async {
                let! view = (policyApi world "alpha" userId).GetConversationVisibility()
                return view.Selectable
            }

            let! olga = offered "olga"
            Expect.equal olga [ TeamVisible; TeamAdmins ] "the owner: every allowed level but PlatformAdmins"
            let! rhea = offered "rhea"
            Expect.equal rhea TeamConversationVisibility.all "an owner who is a platform admin: all of them"
            let! ada = offered "ada"
            Expect.isEmpty ada "a team admin: nothing"
            let! bob = offered "bob"
            Expect.isEmpty bob "a member: nothing"
        }

        testCaseAsync "a level outside the deployment's allowed set is refused, naming the set"
        <| async {
            let settings =
                TeamConversationVisibilitySettings.create TeamVisible [ TeamVisible; TeamAdmins ]
                |> Result.defaultWith failwith

            let! world = newWorld (Some settings)
            let! refused = (policyApi world "alpha" "rhea").SetConversationVisibility PlatformAdmins

            match refused with
            | Error message ->
                Expect.stringContains message "PlatformAdmins" "names the level"
                Expect.stringContains message "TeamVisible, TeamAdmins" "names the allowed set"
            | Ok _ -> failtest "a level outside the allowed set was accepted"
        }

        testCaseAsync "narrowing applies at once"
        <| async {
            let! world = newWorld None
            let! id = seed world "alpha" "alice" "written while open" t0
            let! before = (assistant world "alpha" "bob").GetConversation id
            Expect.isNonEmpty before "open while TeamVisible"
            let! _ = (policyApi world "alpha" "olga").SetConversationVisibility TeamAdmins
            let! after = (assistant world "alpha" "bob").GetConversation id
            Expect.isEmpty after "hidden the moment the owner narrows"
        }

        testCaseAsync "widening never exposes the past"
        <| async {
            let! world = newWorld None
            let! older = seed world "alpha" "alice" "written under TeamAdmins" t0
            let! newer = seed world "alpha" "alice" "written after widening" (t0.AddDays 2.0)
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0; TeamVisible, t0.AddDays 1.0 ]

            let! listed = (assistant world "alpha" "bob").ListConversations()
            let ids = listed |> List.map _.Id
            Expect.isFalse (List.contains older ids) "the conversation written under TeamAdmins stays hidden"
            Expect.isTrue (List.contains newer ids) "one written after the widening is visible"
            let! asAdmin = (assistant world "alpha" "ada").ListConversations()
            Expect.equal asAdmin.Length 2 "an admin sees both"
        }

        testCaseAsync "moving between the two non-nested levels shows only who passes both"
        <| async {
            let! world = newWorld None
            let! id = seed world "alpha" "alice" "written under PlatformAdmins" t0
            do! setRecord world "alpha" [ PlatformAdmins, t0.AddDays -1.0; TeamAdmins, t0.AddDays 1.0 ]

            let! asAdmin = (assistant world "alpha" "ada").GetConversation id
            Expect.isEmpty asAdmin "a team admin could not see it when written"
            let! asPlatformAdmin = (assistant world "alpha" "pat").GetConversation id
            Expect.isEmpty asPlatformAdmin "a platform admin cannot see it now"
            let! asBoth = (assistant world "alpha" "rhea").GetConversation id
            Expect.isNonEmpty asBoth "an owner who is also a platform admin passes both"
        }

        testCaseAsync "the write is guarded: concurrent changes all land, in order"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let store = TeamPolicyStore storage

            let append (i: int) =
                store.Change(
                    "team-race",
                    fun record ->
                        Ok {
                            Changes =
                                record.Changes
                                @ [
                                    {
                                        Level = (if i % 2 = 0 then TeamAdmins else TeamVisible)
                                        ChangedBy = $"user-{i}"
                                        ChangedAt = t0.AddSeconds(float i)
                                    }
                                ]
                        }
                )

            let! results = [ 1..12 ] |> List.map append |> Async.Parallel
            Expect.all results Result.isOk "every change landed"
            let! stored = store.Read "team-race"

            match stored with
            | Ok record ->
                Expect.equal record.Changes.Length 12 "no change was lost to a race"

                Expect.equal
                    (record.Changes |> List.map _.ChangedBy |> Set.ofList)
                    (set [ for i in 1..12 -> $"user-{i}" ])
                    "each writer's change is there"
            | Error e -> failtestf "unreadable: %s" e
        }

        testCase "the level in force at an instant"
        <| fun () ->
            let record: TeamConversationPolicyRecord = {
                Changes = [
                    {
                        Level = TeamAdmins
                        ChangedBy = "olga"
                        ChangedAt = t0
                    }
                    {
                        Level = PlatformAdmins
                        ChangedBy = "rhea"
                        ChangedAt = t0.AddDays 1.0
                    }
                ]
            }

            Expect.equal
                (TeamConversationPolicyRecord.levelAt TeamVisible record (t0.AddDays -1.0))
                TeamVisible
                "before any change: the default"

            Expect.equal (TeamConversationPolicyRecord.levelAt TeamVisible record t0) TeamAdmins "at the change"

            Expect.equal
                (TeamConversationPolicyRecord.levelAt TeamVisible record (t0.AddDays 2.0))
                PlatformAdmins
                "after the second"

            Expect.equal (TeamConversationPolicyRecord.current TeamVisible record) PlatformAdmins "now"
    ]

// ─── Phase 946 — the opt-in substrate's prompt history obeys the level ─
//
// `SubmitMessage` takes the conversation id from the REQUEST and, when the
// opt-in `IConversationStore` is composed, reads that conversation's
// trailing turns into the prompt builder's `ConversationHistory`. Until
// Phase 946 that read skipped `ConversationVisibility.canSee`, so a member
// could aim a turn at a teammate's conversation and have its history fed
// to the query-rewrite stage under a level that hides it from them.

/// A request by `userId` in `teamId`'s scope, with the opt-in
/// conversation substrate composed.
let private contextWithStore (world: World) (teamId: string) (userId: string) (store: IConversationStore) =
    let services = chatServices world.Storage
    services.AddSingleton<ITeamStore>(world.Teams) |> ignore
    services.AddSingleton<IAuditLog>(world.Audit) |> ignore
    services.AddSingleton<IConversationStore>(store) |> ignore

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (TeamMember(userId, teamId)))
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = teamId
            Container = containerOf teamId
            Persist = true
        }

    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

/// The history the prompt builder is handed when `userId` submits a turn
/// to conversation `id`.
let private historySeenBy (world: World) (store: IConversationStore) (userId: string) (id: Guid) = async {
    let seen: string list option ref = ref None

    let config: SystemPromptBuilder.AIAssistantServerConfig = {
        Branding = {
            Name = "Assistant"
            Icon = ""
            ShowSidePanel = false
        }
        SystemPrompt =
            Some(fun context -> async {
                seen.Value <- Some context.ConversationHistory
                return ""
            })
        MaxHistoryMessages = None
        AISurfaceDerivation = TrustClient
    }

    let api =
        fst (
            aiAssistantApi
                (Some config)
                Map.empty
                (new SSEConnectionManager())
                (contextWithStore world "alpha" userId store)
        )

    let! _ =
        api.SubmitMessage {
            ConversationId = id
            Content = "and the totals?"
            ActiveModule = None
            ActivePage = None
            ActivePageNarrative = None
            OverrideProviderLabel = None
            Surface = FullPage
            RetrievalFilters = None
        }

    return seen.Value
}

let private promptHistoryTests =
    testList "Phase 946 — prompt history from the opt-in substrate" [
        testCaseAsync "a member's turn aimed at a conversation the level hides reads no history"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]

            let store = ConversationStore.InMemoryConversationStore() :> IConversationStore
            let id = Guid.NewGuid()
            let conversationId = id.ToString("N")

            let! begun =
                store.BeginConversation(
                    "alpha",
                    {
                        ConversationId = conversationId
                        SchemaVersion = 1
                        CreatedAt = t0
                        CreatedBy = "alice"
                        ScopeId = "alpha"
                        Provider = "test"
                        ModelName = "test"
                        SystemPromptDigest = ""
                        SdkVersion = "test"
                    }
                )

            Expect.isOk begun "seeded"

            let! appended =
                store.AppendTurn(
                    "alpha",
                    conversationId,
                    {
                        TurnId = "t1"
                        ConversationId = conversationId
                        SchemaVersion = 1
                        Role = "user"
                        Content = {
                            Role = "user"
                            Content = "alice's private budget"
                            ToolCalls = []
                            ToolResults = []
                            Parts = []
                        }
                        Timestamp = t0
                        TokensIn = None
                        TokensOut = None
                        ContentDigest = ""
                    }
                )

            Expect.isOk appended "seeded"

            let! asMember = historySeenBy world store "bob" id
            Expect.equal asMember (Some []) "a member the level hides it from is handed no history"

            let! asAdmin = historySeenBy world store "ada" id

            Expect.equal
                asAdmin
                (Some [ "alice's private budget" ])
                "an admin the level admits is handed the history, as before"
        }
    ]

// ─── Phase 961 — a refused turn stops ────────────────────────────
//
// `SubmitMessage`'s background turn applies the Phase 6j.D owner gate
// (`ConversationBlobs.checkOwnership`) before it reads anything else of the
// addressed conversation. Until Phase 961 the refusal was a `return ()` in
// one branch of a `match` inside an `async` block, which does not end the
// block, so the turn ran on past its own refusal. These cases drive the
// real handler: member bob submits to alice's conversation under
// `TeamAdmins`, and each reads what the provider was sent, the bytes of
// every blob alice's conversation has, and the caller's terminal status.
//
// This is the control-flow guard. The architecture-fitness scan checks that
// a guard is PRESENT beside every by-id read; only a run can show that the
// guard's answer is obeyed.

/// A provider that records every message list it is sent.
type private RecordingProvider() =
    let calls = ResizeArray<AIProviderMessage list>()

    member _.Calls = lock calls (fun () -> List.ofSeq calls)

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "recording"
            Model = "recording-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            lock calls (fun () -> calls.Add messages)

            return
                Ok {
                    Content = "a reply"
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

type private RecordingFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Ok provider }
        member _.BuildPlatform(_, _, _) = None

/// Storage that records the name of every blob downloaded through it, so
/// a case can say what a refused turn READ, not only what it wrote.
type private ReadRecordingStorage(inner: IBlobStorage) =
    let reads = ResizeArray<string>()

    member _.Reads = lock reads (fun () -> List.ofSeq reads)

    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            lock reads (fun () -> reads.Add blobName)
            return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// The pinned refusal text (`AIStreamFramingPinTests` pins its bytes).
let private refusalText =
    "Conversation does not belong to the current user — refusing to append."

/// The known turn on the seeded provider-history blob.
let private historySecret = "the author's provider-side history"

let private historyBlob (id: Guid) = $"ai-conversations/{id}.history.json"

/// Seed a conversation by `author` in `teamId`, on both its UI blob and its
/// provider-history blob.
let private seedWithHistory (world: World) (teamId: string) (author: string) = async {
    let! id = seed world teamId author "the author's private plan" t0

    let history: AIProviderMessage list = [
        {
            Role = "user"
            Content = historySecret
            ToolCalls = []
            ToolResults = []
            Parts = []
        }
        {
            Role = "assistant"
            Content = "noted"
            ToolCalls = []
            ToolResults = []
            Parts = []
        }
    ]

    let! _ =
        world.Storage.Upload(
            containerOf teamId,
            historyBlob id,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(history, jsonOptions))
        )

    return id
}

/// Every blob a conversation has, as base64 (`<absent>` when missing).
let private snapshot (world: World) (teamId: string) (id: Guid) = async {
    let names = [
        $"ai-conversations/{id}.json"
        historyBlob id
        $"ai-conversations/{id}.meta.json"
    ]

    let! bytes =
        names
        |> List.map (fun name -> async {
            match! world.Storage.Download(containerOf teamId, name) with
            | Ok b -> return name, Convert.ToBase64String b
            | Error _ -> return name, "<absent>"
        })
        |> Async.Sequential

    return List.ofArray bytes
}

/// Wait, bounded, for a turn that should have stopped to show that it did
/// not: a provider call AND a changed blob. A turn that runs on does both
/// in milliseconds; one that stopped does neither, and the wait runs out.
let private settle (world: World) (teamId: string) (id: Guid) before (provider: RecordingProvider) = async {
    let mutable after = before
    let mutable waited = 0

    while waited < 1500 && (provider.Calls.IsEmpty || after = before) do
        do! Async.Sleep 50
        waited <- waited + 50
        let! now = snapshot world teamId id
        after <- now

    return after
}

let private requestTo (id: Guid) : AIMessageRequest = {
    ConversationId = id
    Content = "summarise this for me"
    ActiveModule = None
    ActivePage = None
    ActivePageNarrative = None
    OverrideProviderLabel = None
    Surface = FullPage
    RetrievalFilters = None
}

/// Both surfaces, for `userId` in `teamId`, over `provider`, reading
/// through `storage`.
let private surfacesWith
    (world: World)
    (teamId: string)
    (userId: string)
    (provider: IAIProvider)
    (storage: ReadRecordingStorage)
    =
    let ctx =
        contextForWith
            (fun services ->
                services.AddSingleton<IAIProviderFactory>(RecordingFactory provider) |> ignore

                services.AddSingleton<IBlobStorage>(storage) |> ignore)
            world
            teamId
            userId

    aiAssistantApi None Map.empty (new SSEConnectionManager()) ctx

/// Every event of a typed stream, bounded.
let private collect (stream: Collections.Generic.IAsyncEnumerable<AIStreamEvent>) = async {
    use cts = new Threading.CancellationTokenSource(TimeSpan.FromSeconds 10.0)
    let e = stream.GetAsyncEnumerator cts.Token
    let events = ResizeArray<AIStreamEvent>()
    let mutable more = true

    while more do
        let! next = e.MoveNextAsync().AsTask() |> Async.AwaitTask

        if next then events.Add e.Current else more <- false

    do! e.DisposeAsync().AsTask() |> Async.AwaitTask
    return List.ofSeq events
}

let private statusesOf (events: AIStreamEvent list) =
    events
    |> List.choose (function
        | TaskStatusChanged(_, status) -> Some status
        | _ -> None)

/// What a refused turn must leave as it was: the provider uncalled, every
/// blob byte-identical, and nothing of the conversation read but the UI
/// blob the gate itself reads to learn the owner.
let private expectStopped
    (provider: RecordingProvider)
    (storage: ReadRecordingStorage)
    (id: Guid)
    before
    after
    (surface: string)
    =
    let sawHistory =
        provider.Calls |> List.exists (List.exists (fun m -> m.Content = historySecret))

    Expect.isFalse sawHistory $"{surface}: the model is not run over the teammate's history"
    Expect.isEmpty provider.Calls $"{surface}: no provider call at all"
    Expect.equal after before $"{surface}: every blob of the teammate's conversation is byte-identical"

    let conversationReads =
        storage.Reads
        |> List.filter (fun name -> name.Contains(string id, StringComparison.Ordinal))

    Expect.equal
        conversationReads
        [ $"ai-conversations/{id}.json" ]
        $"{surface}: only the gate's own read of the conversation happens"

/// Submit as `userId` through `SubmitMessage`; what the provider was sent,
/// the blobs before and after, and the terminal status.
let private submitAs (world: World) (userId: string) (id: Guid) = async {
    let! before = snapshot world "alpha" id
    let provider = RecordingProvider()
    let storage = ReadRecordingStorage world.Storage
    let assistant, _ = surfacesWith world "alpha" userId provider storage

    let! task = assistant.SubmitMessage(requestTo id)
    let! after = settle world "alpha" id before provider
    let! status = assistant.GetTaskStatus task.TaskId
    return provider, storage, before, after, status |> Option.map _.Status
}

let private refusedTurnTests =
    testList "Phase 961 — a refused turn stops at the gate" [
        testCaseAsync "SubmitMessage: a member's turn into a teammate's conversation reads and writes nothing"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" "alice"

            let! provider, storage, before, after, status = submitAs world "bob" id

            expectStopped provider storage id before after "SubmitMessage"

            Expect.equal
                status
                (Some(AITaskFailed refusalText))
                "SubmitMessage: the task ends refused, and stays refused"
        }

        testCaseAsync "StreamChatV2: the stream ends refused, with no InProgress, and nothing is read or written"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" "alice"
            let! before = snapshot world "alpha" id
            let provider = RecordingProvider()
            let storage = ReadRecordingStorage world.Storage
            let _, streaming = surfacesWith world "alpha" "bob" provider storage

            let! events = collect (streaming.StreamChatV2(requestTo id))
            let! after = settle world "alpha" id before provider

            let statuses = statusesOf events
            Expect.equal (List.tryLast statuses) (Some(AITaskFailed refusalText)) "StreamChatV2: the terminal event"

            Expect.isFalse
                (statuses |> List.contains InProgress)
                "StreamChatV2: a refused turn never reports InProgress"

            expectStopped provider storage id before after "StreamChatV2"
        }

        testCaseAsync "the author's own turn still runs over their history and is written back"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" "alice"

            let! provider, _, before, after, status = submitAs world "alice" id

            Expect.isTrue
                (provider.Calls |> List.exists (List.exists (fun m -> m.Content = historySecret)))
                "the author's turn is run over their history"

            Expect.notEqual after before "the author's turn is written back"
            Expect.equal status (Some AITaskCompleted) "the author's turn completes"
        }
    ]

// ─── Phase 961 — an ownerless legacy conversation under a level ──
//
// A conversation whose first message records no author (pre-6j.D) used to
// accept an append from anyone in the container. Under a level narrower
// than `TeamVisible` the append now needs `canModify`; under an open level
// it behaves exactly as before (GP 11). Both append paths apply it.

/// POST one fast-path beacon to conversation `id` as `userId` in `teamId`
/// through the real handler; returns the status code.
let private postBeaconAs (world: World) (teamId: string) (userId: string) (id: Guid) = async {
    let ctx = contextForWith ignore world teamId userId

    let beacon: FastPathBeaconHandler.FastPathBeacon = {
        ConversationId = id
        Tier = 1
        ModuleId = "sales"
        FieldName = "country"
        Instruction = "set country to UK"
        SyntheticReply = "Set country to UK."
        PatternMatched = "set {field} to {value}"
        LatencyMs = 3.5
        JsonFragment = "\"UK\""
        BeaconId = Guid.NewGuid().ToString("N")
    }

    ctx.Request.Body <- new IO.MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon, jsonOptions)))
    ctx.Response.Body <- new IO.MemoryStream()

    let! _ =
        FastPathBeaconHandler.beaconHandler (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
        |> Async.AwaitTask

    return ctx.Response.StatusCode
}

let private ownerlessTests =
    testList "Phase 961 — an ownerless legacy conversation under a narrowed level" [
        testCaseAsync "TeamAdmins: a member's turn is refused and nothing is read or written"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" ""

            let! provider, storage, before, after, status = submitAs world "bob" id

            expectStopped provider storage id before after "ownerless, TeamAdmins, member"
            Expect.equal status (Some(AITaskFailed refusalText)) "the member's turn is refused"
        }

        testCaseAsync "TeamAdmins: a team admin, whom canModify admits, may append"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" ""

            let! provider, _, before, after, status = submitAs world "ada" id

            Expect.isNonEmpty provider.Calls "the admin's turn runs"
            Expect.notEqual after before "the admin's turn is written"
            Expect.equal status (Some AITaskCompleted) "the admin's turn completes"
        }

        testCaseAsync "a team that never chose a level: any member may append, as before"
        <| async {
            let! world = newWorld None
            let! id = seedWithHistory world "alpha" ""

            let! provider, _, before, after, status = submitAs world "bob" id

            Expect.isNonEmpty provider.Calls "the member's turn runs"
            Expect.notEqual after before "the member's turn is written"
            Expect.equal status (Some AITaskCompleted) "the member's turn completes"
        }

        testCaseAsync "TeamVisible chosen explicitly: any member may append, as before"
        <| async {
            let! world = newWorld None
            do! setRecord world "alpha" [ TeamVisible, t0.AddDays -1.0 ]
            let! id = seedWithHistory world "alpha" ""

            let! provider, _, _, _, status = submitAs world "bob" id

            Expect.isNonEmpty provider.Calls "the member's turn runs"
            Expect.equal status (Some AITaskCompleted) "the member's turn completes"
        }

        testCaseAsync "the beacon applies the same rule: 403 under TeamAdmins, 202 under an open level"
        <| async {
            let! world = newWorld None
            let! openId = seedWithHistory world "alpha" ""
            let! openStatus = postBeaconAs world "alpha" "bob" openId
            Expect.equal openStatus 202 "an open level: the member's beacon is applied"

            do! setRecord world "alpha" [ TeamAdmins, t0.AddDays -1.0 ]
            let! narrowedId = seedWithHistory world "alpha" ""
            let! before = snapshot world "alpha" narrowedId
            let! narrowedStatus = postBeaconAs world "alpha" "bob" narrowedId
            let! after = snapshot world "alpha" narrowedId
            Expect.equal narrowedStatus 403 "TeamAdmins: the member's beacon is refused"
            Expect.equal after before "TeamAdmins: neither blob is touched"

            let! adminStatus = postBeaconAs world "alpha" "ada" narrowedId
            Expect.equal adminStatus 202 "TeamAdmins: a team admin's beacon is applied"
        }
    ]

let tests =
    testList "Phase 859 — team conversation visibility" [
        pureRuleTests
        matrixTests
        readPathTests
        writePathTests
        perTeamTests
        auditTests
        changeTests
        promptHistoryTests
        refusedTurnTests
        ownerlessTests
    ]