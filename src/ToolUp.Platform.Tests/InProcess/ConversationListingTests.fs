// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationListingTests

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 516 — conversation titles, search + pagination ────────
//
// 516.A — the listing returns each conversation's real title, message
//         count and timestamps, read from the same UI blob the view and
//         the retention sweep read; a deleted or purged conversation is
//         not listed.
// 516.B — titling: the first-message fallback, the model-generated title
//         (one call, after the first turn, persisted on the meta sibling),
//         and a clean fallback when the provider fails or throws.
// 516.C — keyset paging is stable (no repeat, no skip, also under a
//         concurrent insert); search matches title and content.
// 516.D — `GetTaskStatus` reports the status the turn reached, to the
//         caller that submitted it only.
//
// No live provider: every provider here is a fake.

let private jsonOptions = FableConverters.create ()

let private container = "team-listing"
let private t0 = DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)

let private message (conversationId: Guid) (participant: ParticipantType) (content: string) (at: DateTime) =
    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = participant
        Content = content
        Timestamp = at
        ToolCalls = []
        RetrievedSources = []
        Parts = []
        CreatedBy = "alice"
        BeaconId = ""
        Verification = None
    }
    : ConversationMessage

let private upload (storage: IBlobStorage) (name: string) (text: string) = async {
    let! _ = storage.Upload(container, name, Encoding.UTF8.GetBytes text)
    ()
}

/// Seed a conversation: a user message at `startedAt`, an assistant reply a
/// minute later, and a legacy (pre-516) meta blob with no `Title` field.
let private seed (storage: IBlobStorage) (id: Guid) (firstMessage: string) (reply: string) (startedAt: DateTime) = async {
    let messages = [
        message id User firstMessage startedAt
        message id AIAssistant reply (startedAt.AddMinutes 1.0)
    ]

    do! upload storage $"ai-conversations/{id}.json" (JsonSerializer.Serialize(messages, jsonOptions))
    do! upload storage $"ai-conversations/{id}.meta.json" """{"OverrideProviderLabel":null}"""
}

// ─── Fakes ───────────────────────────────────────────────────────

type private TitleBehaviour =
    | Answer of string
    | Fail
    | Throw

/// A provider that answers the turn with `reply` and the titling call
/// (recognised by its system prompt) per `title`. Records every system
/// prompt and user text it was sent.
type private FakeProvider(reply: string, title: TitleBehaviour) =
    let calls = ResizeArray<string option * string>()

    member _.Calls = lock calls (fun () -> calls |> Seq.toList)

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-fake"
            Model = "test-fake-model"
        }

        member _.SendMessage(messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            let lastUser =
                messages
                |> List.tryFindBack (fun m -> m.Role = "user")
                |> Option.map _.Content
                |> Option.defaultValue ""

            lock calls (fun () -> calls.Add((systemPrompt, lastUser)))

            let ok content =
                Ok {
                    Content = content
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }

            if systemPrompt = Some ConversationTitling.Instruction then
                match title with
                | Answer t -> return ok t
                | Fail -> return Error(TransientServer(503, "overloaded"))
                | Throw -> return failwith "provider exploded"
            else
                return ok reply
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

type private FakeFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Ok provider }
        member _.BuildPlatform(_, _, _) = None

let private buildContext
    (storage: IBlobStorage)
    (provider: IAIProvider option)
    (titling: ConversationTitlingPolicy option)
    (userId: string)
    : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore

    // The handler casts its factory unconditionally, so every context
    // carries one; a listing-only context gets a provider it never calls.
    let provider =
        provider
        |> Option.defaultWith (fun () -> FakeProvider("unused", Fail) :> IAIProvider)

    services.AddSingleton<IAIProviderFactory>(FakeFactory provider) |> ignore

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

    titling
    |> Option.iter (fun p -> services.AddSingleton<ConversationTitlingPolicy>(p) |> ignore)

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = container
            Container = container
            Persist = true
        }

    ctx.Items["ToolUp.UserId"] <- box userId
    ctx :> HttpContext

let private apiFor (ctx: HttpContext) : AIAssistantApi =
    let manager = new SSEConnectionManager()
    fst (aiAssistantApi None Map.empty manager ctx)

/// Poll `GetTaskStatus` until the task is terminal — bounded, so a hang
/// fails the test instead of the run.
let private awaitTerminal (api: AIAssistantApi) (taskId: Guid) = async {
    let mutable result = None
    let mutable attempts = 0

    while result.IsNone && attempts < 200 do
        match! api.GetTaskStatus taskId with
        | Some task when
            (match task.Status with
             | AITaskCompleted
             | AITaskFailed _ -> true
             | _ -> false)
            ->
            result <- Some task
        | _ ->
            attempts <- attempts + 1
            do! Async.Sleep 50

    return result
}

let private submit (api: AIAssistantApi) (conversationId: Guid) (content: string) =
    api.SubmitMessage {
        ConversationId = conversationId
        Content = content
        ActiveModule = None
        ActivePage = None
        ActivePageNarrative = None
        OverrideProviderLabel = None
        Surface = FullPage
        RetrievalFilters = None
    }

// ─── Pure listing fixtures ───────────────────────────────────────

let private row (id: Guid) (title: string) (updatedAt: DateTime) (text: string list) : ConversationListingRow = {
    Conversation = {
        Id = id
        Title = Some title
        CreatedAt = updatedAt.AddMinutes -10.0
        UpdatedAt = updatedAt
        MessageCount = text.Length
        OverrideProviderLabel = None
    }
    SearchText = title :: text
}

let private rows n = [
    for i in 1..n -> row (Guid.NewGuid()) $"Conversation {i}" (t0.AddMinutes(float i)) [ $"body of conversation {i}" ]
]

/// Walk every page of `rows` under `query`, following the cursor.
let private walk (query: ConversationListQuery) (rows: ConversationListingRow list) =
    let rec go (cursor: string option) (acc: Conversation list list) guard =
        if guard > 100 then
            failwith "cursor did not terminate"

        let page = ConversationListing.page { query with Cursor = cursor } rows
        let acc = page.Items :: acc

        match page.NextCursor with
        | Some next -> go (Some next) acc (guard + 1)
        | None -> List.rev acc

    go None [] 0

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

[<Tests>]
let tests =
    testList "Phase 516 — conversation titles, search + pagination" [

        testList "516.C — keyset paging" [
            testCase "pages cover every row once, newest first, and the last page has no cursor"
            <| fun () ->
                let all = rows 7

                let pages =
                    walk
                        {
                            ConversationListQuery.firstPage with
                                PageSize = 3
                        }
                        all

                Expect.equal (pages |> List.map List.length) [ 3; 3; 1 ] "3 + 3 + 1 rows"

                let seen = pages |> List.concat |> List.map _.Id

                let expected =
                    all
                    |> List.sortByDescending (fun r -> r.Conversation.UpdatedAt)
                    |> List.map _.Conversation.Id

                Expect.equal seen expected "every row once, in newest-activity order"

            testCase "TotalCount is the match count across pages"
            <| fun () ->
                let page =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                PageSize = 2
                        }
                        (rows 5)

                Expect.equal page.TotalCount 5 "five rows match"
                Expect.equal page.Items.Length 2 "two on the page"

            testCase "a conversation started mid-paging neither repeats nor skips a row"
            <| fun () ->
                let all = rows 6

                let query = {
                    ConversationListQuery.firstPage with
                        PageSize = 3
                }

                let first = ConversationListing.page query all
                let newer = row (Guid.NewGuid()) "Brand new" (t0.AddHours 5.0) [ "fresh" ]

                let second =
                    ConversationListing.page { query with Cursor = first.NextCursor } (newer :: all)

                let expectedSecond =
                    all
                    |> List.sortByDescending (fun r -> r.Conversation.UpdatedAt)
                    |> List.skip 3
                    |> List.map _.Conversation.Id

                Expect.equal
                    (second.Items |> List.map _.Id)
                    expectedSecond
                    "page two is exactly the rows after page one"

            testCase "rows sharing a timestamp page deterministically by id"
            <| fun () ->
                let tied = [ for i in 1..5 -> row (Guid.NewGuid()) $"Tied {i}" t0 [] ]

                let query = {
                    ConversationListQuery.firstPage with
                        PageSize = 2
                }

                let once = walk query tied |> List.concat |> List.map _.Id
                let again = walk query (List.rev tied) |> List.concat |> List.map _.Id

                Expect.equal once again "input order does not change the listing"
                Expect.equal (once |> List.distinct |> List.length) 5 "no tied row repeated or lost"

            testCase "an undecodable cursor yields an empty last page, not the first page again"
            <| fun () ->
                let page =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                Cursor = Some "not-a-cursor"
                        }
                        (rows 4)

                Expect.isEmpty page.Items "no rows"
                Expect.isNone page.NextCursor "no further page"

            testCase "page size is clamped to 1 .. MaxPageSize"
            <| fun () ->
                let all = rows (ConversationListQuery.MaxPageSize + 5)

                let tiny =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                PageSize = 0
                        }
                        all

                let huge =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                PageSize = 10_000
                        }
                        all

                Expect.equal tiny.Items.Length 1 "zero is read as one"
                Expect.equal huge.Items.Length ConversationListQuery.MaxPageSize "clamped to the maximum"
        ]

        testList "516.C — search" [
            testCase "matches the title, case-insensitively"
            <| fun () ->
                let all = [
                    row (Guid.NewGuid()) "Quarterly SALES review" t0 [ "hello" ]
                    row (Guid.NewGuid()) "Holiday plans" (t0.AddMinutes 1.0) [ "beach" ]
                ]

                let page =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                Search = Some "sales"
                        }
                        all

                Expect.equal (page.Items |> List.map _.Title) [ Some "Quarterly SALES review" ] "title match"
                Expect.equal page.TotalCount 1 "one match"

            testCase "matches message content"
            <| fun () ->
                let all = [
                    row (Guid.NewGuid()) "Untitled" t0 [ "what drove churn in March?" ]
                    row (Guid.NewGuid()) "Other" (t0.AddMinutes 1.0) [ "nothing relevant" ]
                ]

                let page =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                Search = Some "  CHURN "
                        }
                        all

                Expect.equal page.Items.Length 1 "content match, trimmed and case-insensitive"

            testCase "a blank search matches everything"
            <| fun () ->
                let page =
                    ConversationListing.page
                        {
                            ConversationListQuery.firstPage with
                                Search = Some "   "
                        }
                        (rows 3)

                Expect.equal page.TotalCount 3 "blank is no filter"
        ]

        testList "516.B — titles" [
            testCase "the fallback title is the first user message, whitespace-collapsed and truncated"
            <| fun () ->
                let id = Guid.NewGuid()

                let messages = [
                    message id AIAssistant "Hi, how can I help?" t0
                    message id User "  Please   compare the revenue of every region over the last two fiscal years  " t0
                ]

                let title = ConversationListing.fallbackTitle 30 messages

                Expect.equal title (Some "Please compare the revenue of…") "first user message, 30 chars with ellipsis"

            testCase "cleanTitle strips the quoting and labels a model adds"
            <| fun () ->
                Expect.equal
                    (ConversationTitling.cleanTitle 60 "\"Regional revenue comparison.\"")
                    (Some "Regional revenue comparison")
                    "quotes + period"

                Expect.equal
                    (ConversationTitling.cleanTitle 60 "Title: **Churn drivers**\nextra")
                    (Some "Churn drivers")
                    "label + markdown, first line"

                Expect.equal (ConversationTitling.cleanTitle 60 "  \n  \"\"  ") None "nothing left"

            testCaseAsync "a generated title comes from one budget-capped call on the policy's model"
            <| async {
                let provider = FakeProvider("reply", Answer "\"Revenue by region\"")

                let policy = {
                    ConversationTitlingPolicy.generatedOn "small-model" with
                        MaxInputChars = 20
                }

                let! title =
                    ConversationTitling.generate
                        policy
                        (provider :> IAIProvider)
                        "Compare revenue across all of our regions please"

                Expect.equal title (Ok "Revenue by region") "cleaned model title"

                match provider.Calls with
                | [ (prompt, input) ] ->
                    Expect.equal prompt (Some ConversationTitling.Instruction) "the titling instruction"
                    Expect.isLessThanOrEqual input.Length 20 "input capped at MaxInputChars"
                | calls -> failtestf "expected exactly one call, got %d" calls.Length
            }

            testCaseAsync "a failing titling call falls back to the first message"
            <| async {
                let provider = FakeProvider("reply", Fail)

                let! title =
                    ConversationTitling.titleOrFallback
                        ConversationTitlingPolicy.generated
                        (provider :> IAIProvider)
                        "Why did churn rise?"

                Expect.equal title (Some "Why did churn rise?") "fallback"
            }

            testCaseAsync "a throwing titling call falls back to the first message"
            <| async {
                let provider = FakeProvider("reply", Throw)

                let! generated =
                    ConversationTitling.generate
                        ConversationTitlingPolicy.generated
                        (provider :> IAIProvider)
                        "Why did churn rise?"

                Expect.isError generated "the exception is an Error, not a throw"

                let! title =
                    ConversationTitling.titleOrFallback
                        ConversationTitlingPolicy.generated
                        (provider :> IAIProvider)
                        "Why did churn rise?"

                Expect.equal title (Some "Why did churn rise?") "fallback"
            }

            testCaseAsync "the default policy makes no provider call"
            <| async {
                let provider = FakeProvider("reply", Answer "Unused")

                let! title =
                    ConversationTitling.titleOrFallback
                        ConversationTitlingPolicy.firstMessage
                        (provider :> IAIProvider)
                        "Why did churn rise?"

                Expect.equal title (Some "Why did churn rise?") "first message"
                Expect.isEmpty provider.Calls "no call"
            }
        ]

        testList "516.A — the handler lists real metadata" [
            testCaseAsync "titles, counts and timestamps come from the stored conversation"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let older = Guid.NewGuid()
                let newer = Guid.NewGuid()
                do! seed storage older "Summarise the Q3 board pack" "Here is a summary" t0
                do! seed storage newer "Forecast next quarter" "Forecast follows" (t0.AddHours 2.0)

                let api = apiFor (buildContext storage None None "alice")
                let! listed = api.ListConversations()

                Expect.equal (listed |> List.map _.Id) [ newer; older ] "newest activity first"

                let first = listed |> List.find (fun c -> c.Id = older)
                Expect.equal first.Title (Some "Summarise the Q3 board pack") "fallback title"
                Expect.equal first.MessageCount 2 "two messages"
                Expect.equal first.CreatedAt t0 "created at the first message"
                Expect.equal first.UpdatedAt (t0.AddMinutes 1.0) "updated at the last message"
            }

            testCaseAsync "a stored generated title wins, and changing the override keeps it"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let id = Guid.NewGuid()
                do! seed storage id "long first message" "reply" t0

                do!
                    upload
                        storage
                        $"ai-conversations/{id}.meta.json"
                        """{"OverrideProviderLabel":null,"Title":"Board pack summary"}"""

                let api = apiFor (buildContext storage None None "alice")
                let! result = api.SetConversationOverride(id, Some "work-claude")
                Expect.isOk result "override saved"

                let! listed = api.ListConversations()
                let conv = listed |> List.exactlyOne
                Expect.equal conv.Title (Some "Board pack summary") "generated title kept"
                Expect.equal conv.OverrideProviderLabel (Some "work-claude") "override applied"
            }

            testCaseAsync "a deleted conversation, and one whose UI blob a purge already removed, are not listed"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let kept = Guid.NewGuid()
                let deleted = Guid.NewGuid()
                let halfPurged = Guid.NewGuid()
                do! seed storage kept "keep me" "ok" t0
                do! seed storage deleted "delete me" "ok" t0
                do! seed storage halfPurged "purge me" "ok" t0

                let api = apiFor (buildContext storage None None "alice")
                let! deletion = api.DeleteConversation deleted
                Expect.isOk deletion "deleted"

                // The UI blob is the first sibling `deleteSiblings` removes;
                // a purge interrupted after it leaves the meta sibling behind.
                let! _ = storage.Delete(container, $"ai-conversations/{halfPurged}.json")

                let! listed = api.ListConversations()
                Expect.equal (listed |> List.map _.Id) [ kept ] "only the surviving conversation"

                let! page = api.ListConversationsPage ConversationListQuery.firstPage
                Expect.equal page.TotalCount 1 "the paged endpoint agrees"
            }

            testCaseAsync "a conversation purged by the retention sweep is not listed"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let stale = Guid.NewGuid()
                let fresh = Guid.NewGuid()
                do! seed storage stale "old question" "ok" (t0.AddDays -40.0)
                do! seed storage fresh "new question" "ok" t0

                let policy = {
                    ConversationRetentionPolicy.retainForever with
                        MaxAge = Some(TimeSpan.FromDays 30.0)
                }

                let! report =
                    ConversationRetention.sweepContainer storage None silentLogger t0 policy container container

                Expect.equal report.Purged [ stale ] "the sweep purged the stale conversation"

                let api = apiFor (buildContext storage None None "alice")
                let! listed = api.ListConversations()
                Expect.equal (listed |> List.map _.Id) [ fresh ] "the purged one is gone from the list"
            }

            testCaseAsync "the paged endpoint pages and searches over stored content"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage

                for i in 1..5 do
                    do!
                        seed
                            storage
                            (Guid.NewGuid())
                            $"Question {i}"
                            $"Answer mentioning widget-{i}"
                            (t0.AddMinutes(float i))

                let api = apiFor (buildContext storage None None "alice")

                let! firstPage =
                    api.ListConversationsPage {
                        ConversationListQuery.firstPage with
                            PageSize = 2
                    }

                Expect.equal firstPage.TotalCount 5 "all five"
                Expect.equal (firstPage.Items |> List.map _.Title) [ Some "Question 5"; Some "Question 4" ] "newest two"

                let! secondPage =
                    api.ListConversationsPage {
                        ConversationListQuery.firstPage with
                            PageSize = 2
                            Cursor = firstPage.NextCursor
                    }

                Expect.equal (secondPage.Items |> List.map _.Title) [ Some "Question 3"; Some "Question 2" ] "next two"

                let! searched =
                    api.ListConversationsPage {
                        ConversationListQuery.firstPage with
                            Search = Some "WIDGET-3"
                    }

                Expect.equal
                    (searched.Items |> List.map _.Title)
                    [ Some "Question 3" ]
                    "matched on the assistant's text"
            }
        ]

        testList "516.B/D — a real turn" [
            testCaseAsync "a turn's status is pollable and a generated title is persisted after it"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage

                let provider =
                    FakeProvider("Here is the breakdown.", Answer "Regional revenue breakdown")

                let ctx =
                    buildContext
                        storage
                        (Some(provider :> IAIProvider))
                        (Some ConversationTitlingPolicy.generated)
                        "alice"

                let api = apiFor ctx
                let conversationId = Guid.NewGuid()
                let! task = submit api conversationId "Break revenue down by region"

                let! terminal = awaitTerminal api task.TaskId
                Expect.equal (terminal |> Option.map _.Status) (Some AITaskCompleted) "the poll sees the completed turn"
                Expect.isSome (terminal |> Option.bind _.CompletedAt) "CompletedAt stamped"

                // Titling runs after the terminal event; wait for its write.
                let mutable listed = []
                let mutable attempts = 0

                while attempts < 100
                      && not (
                          listed
                          |> List.exists (fun (c: Conversation) -> c.Title = Some "Regional revenue breakdown")
                      ) do
                    let! l = api.ListConversations()
                    listed <- l
                    attempts <- attempts + 1

                    if attempts < 100 then
                        do! Async.Sleep 50

                let conv = listed |> List.exactlyOne
                Expect.equal conv.Title (Some "Regional revenue breakdown") "generated title listed"
                Expect.equal conv.MessageCount 2 "user + assistant"

                let titlingCalls =
                    provider.Calls
                    |> List.filter (fun (p, _) -> p = Some ConversationTitling.Instruction)

                Expect.equal titlingCalls.Length 1 "exactly one titling call"

                // Another user in the same container cannot read the task.
                let bob = apiFor (buildContext storage None None "bob")
                let! foreign = bob.GetTaskStatus task.TaskId
                Expect.isNone foreign "a task id is not a capability"
            }

            testCaseAsync "a failing titling call leaves the turn completed and the first-message title"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let provider = FakeProvider("Answer.", Fail)

                let ctx =
                    buildContext
                        storage
                        (Some(provider :> IAIProvider))
                        (Some ConversationTitlingPolicy.generated)
                        "alice"

                let api = apiFor ctx
                let! task = submit api (Guid.NewGuid()) "Why did churn rise in March?"

                let! terminal = awaitTerminal api task.TaskId

                Expect.equal
                    (terminal |> Option.map _.Status)
                    (Some AITaskCompleted)
                    "titling failure does not fail the turn"

                // Give the post-terminal titling attempt time to finish.
                let mutable attempts = 0

                while attempts < 100
                      && not (
                          provider.Calls
                          |> List.exists (fun (p, _) -> p = Some ConversationTitling.Instruction)
                      ) do
                    attempts <- attempts + 1
                    do! Async.Sleep 50

                do! Async.Sleep 100
                let! listed = api.ListConversations()
                Expect.equal (listed |> List.map _.Title) [ Some "Why did churn rise in March?" ] "fallback title"
            }

            testCaseAsync "without a titling policy the turn makes no titling call"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let provider = FakeProvider("Answer.", Answer "Unused")
                let api = apiFor (buildContext storage (Some(provider :> IAIProvider)) None "alice")
                let! task = submit api (Guid.NewGuid()) "Hello there"

                let! terminal = awaitTerminal api task.TaskId
                Expect.equal (terminal |> Option.map _.Status) (Some AITaskCompleted) "completed"

                Expect.isFalse
                    (provider.Calls
                     |> List.exists (fun (p, _) -> p = Some ConversationTitling.Instruction))
                    "no titling call"
            }
        ]

        testList "516.D — the task-status registry" [
            let task (conversationId: Guid) : AITask = {
                TaskId = Guid.NewGuid()
                ConversationId = conversationId
                Prompt = "p"
                Status = Queued
                CreatedAt = t0
                CompletedAt = None
            }

            testCase "a status is readable only by its container and owner"
            <| fun () ->
                let registry = AITaskStatusRegistry(100, TimeSpan.FromHours 1.0)
                let t = task (Guid.NewGuid())
                registry.Register("c1", "alice", t, t0)
                registry.Transition(t.TaskId, InProgress, t0)

                Expect.equal
                    (registry.TryGet("c1", "alice", t.TaskId, t0) |> Option.map _.Status)
                    (Some InProgress)
                    "owner"

                Expect.isNone (registry.TryGet("c1", "bob", t.TaskId, t0)) "other user"
                Expect.isNone (registry.TryGet("c2", "alice", t.TaskId, t0)) "other container"
                Expect.isNone (registry.TryGet("c1", "alice", Guid.NewGuid(), t0)) "unknown id"

            testCase "the first terminal status sticks"
            <| fun () ->
                let registry = AITaskStatusRegistry(100, TimeSpan.FromHours 1.0)
                let t = task (Guid.NewGuid())
                registry.Register("c1", "alice", t, t0)
                registry.Transition(t.TaskId, AITaskFailed "boom", t0)
                registry.Transition(t.TaskId, AITaskCompleted, t0.AddSeconds 1.0)

                let read = registry.TryGet("c1", "alice", t.TaskId, t0.AddSeconds 2.0)
                Expect.equal (read |> Option.map _.Status) (Some(AITaskFailed "boom")) "failure kept"
                Expect.equal (read |> Option.bind _.CompletedAt) (Some t0) "stamped once"

            testCase "a terminal task is forgotten after the retention window"
            <| fun () ->
                let registry = AITaskStatusRegistry(100, TimeSpan.FromMinutes 10.0)
                let t = task (Guid.NewGuid())
                registry.Register("c1", "alice", t, t0)
                registry.Transition(t.TaskId, AITaskCompleted, t0)

                Expect.isSome (registry.TryGet("c1", "alice", t.TaskId, t0.AddMinutes 9.0)) "inside the window"
                Expect.isNone (registry.TryGet("c1", "alice", t.TaskId, t0.AddMinutes 11.0)) "past it"

            testCase "past capacity the oldest tasks are evicted"
            <| fun () ->
                let registry = AITaskStatusRegistry(3, TimeSpan.FromHours 1.0)

                let tasks = [
                    for i in 1..5 ->
                        {
                            task (Guid.NewGuid()) with
                                CreatedAt = t0.AddSeconds(float i)
                        }
                ]

                for t in tasks do
                    registry.Register("c1", "alice", t, t0)

                Expect.equal registry.Count 3 "bounded"
                Expect.isNone (registry.TryGet("c1", "alice", tasks.Head.TaskId, t0)) "oldest evicted"
                Expect.isSome (registry.TryGet("c1", "alice", (List.last tasks).TaskId, t0)) "newest kept"

            testCase "forgetting a conversation drops its tasks and no other"
            <| fun () ->
                let registry = AITaskStatusRegistry(100, TimeSpan.FromHours 1.0)
                let erased = Guid.NewGuid()
                let a = task erased
                let b = task (Guid.NewGuid())
                registry.Register("c1", "alice", a, t0)
                registry.Register("c1", "alice", b, t0)
                registry.ForgetConversation("c1", erased)

                Expect.isNone (registry.TryGet("c1", "alice", a.TaskId, t0)) "erased conversation's task gone"
                Expect.isSome (registry.TryGet("c1", "alice", b.TaskId, t0)) "other conversation's task kept"
        ]
    ]