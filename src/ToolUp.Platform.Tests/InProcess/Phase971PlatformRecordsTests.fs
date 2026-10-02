module ToolUp.Platform.Tests.InProcess.Phase971PlatformRecordsTests

open System
open System.Collections.Concurrent
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — platform records: a refused delete that is the operation ──
//
// `IBlobStorage.Delete` answers `Ok` on a missing blob, so an `Error` is a
// refusal. Each operation pinned here IS a delete of a record (a
// conversation, a data-source config, a flag document, the offboard
// ledger, a visibility profile, a webhook subscription): a refusal that
// returned normally claimed the record gone while it was still there.

let private raises (work: Async<'a>) = async {
    match! Async.Catch work with
    | Choice1Of2 value -> return failtestf "expected the operation to raise, it answered %A" value
    | Choice2Of2 _ -> return ()
}

let private suffix () =
    Guid.NewGuid().ToString("N").Substring(0, 8)

// ── Conversations ─────────────────────────────────────────────────────

let private conversation (id: string) (scopeId: string) (userId: string) : Conversation = {
    ConversationId = id
    SchemaVersion = 1
    CreatedAt = DateTime.UtcNow
    CreatedBy = userId
    ScopeId = scopeId
    Provider = "TestProvider"
    ModelName = "test-model"
    SystemPromptDigest = "sha256:test"
    SdkVersion = "0.0.0"
}

let private turn (conversationId: string) (content: string) : ConversationTurn = {
    TurnId = ""
    ConversationId = conversationId
    SchemaVersion = 1
    Role = "user"
    Content = {
        Role = "user"
        Content = content
        ToolCalls = []
        ToolResults = []
        Parts = []
    }
    Timestamp = DateTime.UtcNow
    TokensIn = None
    TokensOut = None
    ContentDigest = ""
}

/// A persistent conversation store whose turn blobs refuse deletion while
/// `refuse` is set. Turn object ids are `conv__{id}__turn__{NNNNNN}`, so
/// their version blobs are `objects/conv__{id}__turn__{NNNNNN}/v{N}.json`.
let private conversationStore (refuse: bool ref) =
    let blob =
        DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun name -> refuse.Value && name.Contains "__turn__")
        :> IBlobStorage

    let objectStore = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore

    let store =
        ConversationStore.PersistentConversationStore(objectStore) :> IConversationStore

    store, objectStore

let private seedConversation (store: IConversationStore) (scopeId: string) (convId: string) (userId: string) = async {
    match! (store :> IConversationWriter).BeginConversation(scopeId, conversation convId scopeId userId) with
    | Ok() -> ()
    | Error e -> failtestf "Begin: %A" e

    for i in 1..2 do
        match! (store :> IConversationWriter).AppendTurn(scopeId, convId, turn convId (sprintf "turn-%d" i)) with
        | Ok() -> ()
        | Error e -> failtestf "Append: %A" e
}

let private turnObjects (objectStore: IDataObjectStore) (scopeId: string) = async {
    let! objects = objectStore.ListObjects scopeId
    return objects |> List.filter (fun o -> o.ObjectId.Contains "__turn__")
}

// ── Data-source configs ───────────────────────────────────────────────

let private dataSource: DataSourceConfig = {
    Id = "source-971"
    Name = "Phase 971 source"
    Kind = "InMemory"
    ConnectionScope = Map.empty
    CredentialKey = "cred-971"
    Tables = None
    Tags = Map.empty
}

// ── Module visibility ─────────────────────────────────────────────────

let private visibilityProfile (scope: FlagScope) : ModuleVisibilityProfile = {
    Scope = scope
    Rule = ModuleVisibilityRule.Deny [ "Reports" ]
    ExcludedEntryIds = []
    Note = Some "Phase 971"
}

let private recordingLogger (lines: ConcurrentQueue<string>) =
    { new ILogger with
        member _.Debug m = lines.Enqueue("DEBUG " + m)
        member _.Info m = lines.Enqueue("INFO " + m)
        member _.Warn m = lines.Enqueue("WARN " + m)
        member _.Error(m, _) = lines.Enqueue("ERROR " + m)
    }

// ── Webhooks ──────────────────────────────────────────────────────────

let private subscription (scopeId: string) : WebhookSubscription =
    let id = Guid.NewGuid()

    {
        SubscriptionId = id
        ScopeId = scopeId
        TargetUrl = "https://hooks.example.com/endpoint"
        SecretRef = WebhookSecretRef.current id
        Secret = None
        EventTypes = [ "FlagChanged" ]
        Status = WebhookStatus.Active
        CreatedBy = "user-1"
        CreatedAt = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        ConsecutiveFailures = 0
        PreviousSecretRef = None
        PreviousSecret = None
        PreviousSecretExpiresAt = None
    }

let tests =
    testList "Phase 971 - PlatformRecords" [

        testCaseAsync "DeleteConversation removes the turns, not just the manifest"
        <| async {
            let store, objectStore = conversationStore (ref false)
            let scopeId = "team-971-" + suffix ()
            let convId = "conv-" + suffix ()
            do! seedConversation store scopeId convId "alice"

            match! (store :> IConversationWriter).DeleteConversation(scopeId, convId) with
            | Ok() -> ()
            | Error e -> failtestf "expected the delete to succeed, got %A" e

            let! turnsLeft = turnObjects objectStore scopeId

            Expect.isEmpty
                (turnsLeft |> List.map _.ObjectId)
                "every turn of a deleted conversation is gone at rest, not orphaned"
        }

        testCaseAsync "DeleteConversation answers StoreUnreachable over a refused turn delete; the manifest stays"
        <| async {
            let refuse = ref true
            let store, objectStore = conversationStore refuse
            let scopeId = "team-971-" + suffix ()
            let convId = "conv-" + suffix ()
            do! seedConversation store scopeId convId "alice"

            match! (store :> IConversationWriter).DeleteConversation(scopeId, convId) with
            | Error(ConversationError.StoreUnreachable detail) ->
                Expect.stringContains detail "__turn__" "the error names the turn objects still there"
            | other -> failtestf "expected Error StoreUnreachable, got %A" other

            match! (store :> IConversationReader).GetConversation(scopeId, convId) with
            | Ok(_, turns, _) -> Expect.equal turns.Length 2 "the manifest still finds both turns for a re-run"
            | Error e -> failtestf "the manifest must survive a refused turn delete, got %A" e

            // The re-run, once the store accepts deletes, finishes the job.
            refuse.Value <- false

            match! (store :> IConversationWriter).DeleteConversation(scopeId, convId) with
            | Ok() -> ()
            | Error e -> failtestf "the re-run should succeed, got %A" e

            let! turnsLeft = turnObjects objectStore scopeId
            Expect.isEmpty (turnsLeft |> List.map _.ObjectId) "the re-run removed every turn"
        }

        testCaseAsync "Erase HardDelete is not a success over a refused conversation delete"
        <| async {
            let store, _ = conversationStore (ref true)
            let scopeId = "team-971-" + suffix ()
            let convId = "conv-" + suffix ()
            do! seedConversation store scopeId convId "alice"

            match! (store :> IConversationEraser).Erase(scopeId, "alice", ErasurePolicy.HardDelete, false) with
            | Ok summary -> failtestf "expected the erasure to fail, it answered %A" summary
            | Error(ErasureError.HandlerPartialFailure(_, _, detail)) ->
                Expect.stringContains detail convId "the failure names the conversation not deleted"
            | Error other -> failtestf "expected HandlerPartialFailure, got %A" other
        }

        testCaseAsync "DataSourceConfigStore.Delete raises over a refusal; the config stays"
        <| async {
            let store =
                DataSourceConfigStore.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "data-sources/")
                )

            let scopeId = "team-971-" + suffix ()
            do! store.Save(scopeId, dataSource)
            do! raises (store.Delete(scopeId, dataSource.Id))
            let! still = store.Get(scopeId, dataSource.Id)
            Expect.isSome still "the config is still there"
        }

        testCaseAsync "FeatureFlagStore Erase HardDelete fails over a refused flag-document delete"
        <| async {
            let store =
                FeatureFlagStore.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "flags/")
                )

            // Only a matching flag, so the erasure deletes the whole document.
            let! _ = store.SetFlag(FlagScope.Team "971", "targeting", FlagValue.Variant([], "for:u1"))

            match! store.Erase("team-971", "u1", ErasurePolicy.HardDelete, false) with
            | Ok summary -> failtestf "expected the erasure to fail, it answered %A" summary
            | Error(ErasureError.StoreUnreachable(handler, _)) -> Expect.equal handler "feature-flags" "handler named"
            | Error other -> failtestf "expected StoreUnreachable, got %A" other

            let! flags = store.ListFlags(FlagScope.Team "971")
            Expect.isTrue (Map.containsKey "targeting" flags) "the flag naming the subject is still there"
        }

        testCaseAsync "BlobBackedLifecycleLedger.Clear raises over a refusal; the done-set stays"
        <| async {
            let ledger =
                BlobBackedLifecycleLedger.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "_tenant-lifecycle-ledger/")
                )

            let scopeId = "team-971-" + suffix ()
            do! ledger.Record(scopeId, Deprovisioning, "crypto-shred", LedgerDisposition.Completed)
            do! raises (ledger.Clear(scopeId, Deprovisioning))
            let! completed = ledger.GetCompleted(scopeId, Deprovisioning)
            Expect.isNonEmpty completed "the done-set is still recorded"
        }

        testCaseAsync "ModuleVisibilityStore.ClearProfile raises over a refusal; the profile stays"
        <| async {
            let store =
                ModuleVisibilityStore.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "module-visibility/")
                )

            let scope = FlagScope.Team("971-" + suffix ())

            match! store.SetProfile(scope, visibilityProfile scope) with
            | Ok() -> ()
            | Error e -> failtestf "SetProfile: %s" e

            do! raises (store.ClearProfile scope)
            let! still = store.GetProfile scope
            Expect.isSome still "the profile is still there"
        }

        testCaseAsync "ModuleVisibility API ClearProfile answers Error over a refusal, with no cleared log"
        <| async {
            let store =
                ModuleVisibilityStore.create (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.StartsWith "module-visibility/")
                )

            let userId = "alice-" + suffix ()
            let scope = FlagScope.User userId

            match! store.SetProfile(scope, visibilityProfile scope) with
            | Ok() -> ()
            | Error e -> failtestf "SetProfile: %s" e

            let lines = ConcurrentQueue<string>()
            let services = ServiceCollection()
            services.AddSingleton<IModuleVisibilityStore>(store) |> ignore
            services.AddSingleton<ILogger>(recordingLogger lines) |> ignore

            services.AddSingleton<AccessContext>(AccessContext.unrestricted (AuthenticatedUser userId))
            |> ignore

            let ctx = DefaultHttpContext()
            ctx.RequestServices <- services.BuildServiceProvider()
            let api = ModuleVisibilityApiHandler.moduleVisibilityApi [ "Reports" ] ctx

            match! api.ClearProfile() with
            | Ok() -> failtest "expected Error over a refused clear, got Ok"
            | Error _ -> ()

            Expect.isFalse
                (lines |> Seq.exists (fun l -> l.Contains "profile cleared"))
                "no 'profile cleared' log over a refusal"

            let! still = store.GetProfile scope
            Expect.isSome still "the profile is still there"
        }

        testCaseAsync "WebhookRegistry.DeleteSubscription answers Error over a refusal; the subscription stays"
        <| async {
            let registry =
                WebhookRegistry.createRegistry (
                    DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n.Contains "/subscriptions/")
                )

            let sub = subscription ("team-971-" + suffix ())

            match! registry.CreateSubscription sub with
            | Ok() -> ()
            | Error e -> failtestf "CreateSubscription: %s" e

            match! registry.DeleteSubscription(sub.ScopeId, sub.SubscriptionId) with
            | Ok() -> failtest "expected Error over a refused delete, got Ok"
            | Error e -> Expect.stringContains e (sub.SubscriptionId.ToString "N") "the error names the subscription"

            let! still = registry.GetSubscription(sub.ScopeId, sub.SubscriptionId)
            Expect.isSome still "the subscription is still there"
        }
    ]