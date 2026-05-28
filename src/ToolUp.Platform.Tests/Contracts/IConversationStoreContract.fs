module ToolUp.Platform.Tests.Contracts.IConversationStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.EntityQueryTypes

// ─── Phase 53 — IConversationStore contract test pack ────────────
//
// Same shape as `IResultStoreContract`. Factory returns a fresh
// `(store, scopeA, scopeB)` triple — scopeA and scopeB are
// GUID-suffixed so cross-scope isolation can be exercised even when
// the underlying substrate is shared.

let tests (name: string) (factory: unit -> IConversationStore * string * string) =

    let mkConversation (id: string) (scopeId: string) (userId: string) : Conversation = {
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

    let mkTurn (conversationId: string) (role: string) (content: string) : ConversationTurn = {
        TurnId = ""
        ConversationId = conversationId
        SchemaVersion = 1
        Role = role
        Content = {
            Role = role
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

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    /// Run an `Async<Result<unit, _>>` and fail the test if the
    /// result is `Error`. Keeps the test body free of the
    /// boilerplate `let! r = ...; okOrFail ... r` pattern.
    let expectOk label work = async {
        let! result = work

        match result with
        | Ok() -> return ()
        | Error err -> return failtestf "%s: expected Ok, got %A" label err
    }

    let reader (store: IConversationStore) = store :> IConversationReader
    let writer (store: IConversationStore) = store :> IConversationWriter
    let eraser (store: IConversationStore) = store :> IConversationEraser

    testList $"{name} — IConversationStore contract" [

        testCaseAsync "BeginConversation then GetConversation round-trips"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            let conv = mkConversation convId scopeA "alice"

            do! expectOk "BeginConversation" ((writer store).BeginConversation(scopeA, conv))

            let! readResult = (reader store).GetConversation(scopeA, convId)
            let header, turns, status = okOrFail "GetConversation" readResult

            Expect.equal header.ConversationId convId "id round-trips"
            Expect.equal header.CreatedBy "alice" "createdBy preserved"
            Expect.equal turns [] "no turns on fresh conversation"
            Expect.equal status ConversationStatus.Active "initial status is Active"
        }

        testCaseAsync "AppendTurn persists the turn"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            let turn = mkTurn convId "user" "hello"
            do! expectOk "Append" ((writer store).AppendTurn(scopeA, convId, turn))

            let! readResult = (reader store).GetConversation(scopeA, convId)
            let _, turns, _ = okOrFail "Get" readResult
            Expect.equal turns.Length 1 "one turn persisted"
            Expect.equal turns[0].Content.Content "hello" "content round-trips"
            Expect.equal turns[0].Role "user" "role preserved"
        }

        testCaseAsync "Multiple AppendTurns persist in order"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            for i in 1..3 do
                let turn = mkTurn convId "user" (sprintf "turn-%d" i)
                do! expectOk "Append" ((writer store).AppendTurn(scopeA, convId, turn))

            let! readResult = (reader store).GetConversation(scopeA, convId)
            let _, turns, _ = okOrFail "Get" readResult
            Expect.equal turns.Length 3 "three turns persisted"
            Expect.equal turns[0].Content.Content "turn-1" "first turn first"
            Expect.equal turns[2].Content.Content "turn-3" "last turn last"
        }

        testCaseAsync "MarkStatus terminal blocks transition back to Active"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            do! expectOk "MarkCompleted" ((writer store).MarkStatus(scopeA, convId, ConversationStatus.Completed))

            match! (writer store).MarkStatus(scopeA, convId, ConversationStatus.Active) with
            | Error(ConversationError.StatusForbidden _) -> ()
            | other -> failtestf "Expected StatusForbidden, got %A" other
        }

        testCaseAsync "GetConversation returns NotFound for unknown id"
        <| async {
            let store, scopeA, _ = factory ()

            match! (reader store).GetConversation(scopeA, "no-such-conv") with
            | Error(ConversationError.NotFound _) -> ()
            | other -> failtestf "Expected NotFound, got %A" other
        }

        testCaseAsync "AppendTurn on missing conversation returns NotFound"
        <| async {
            let store, scopeA, _ = factory ()
            let turn = mkTurn "no-such-conv" "user" "hello"

            match! (writer store).AppendTurn(scopeA, "no-such-conv", turn) with
            | Error(ConversationError.NotFound _) -> ()
            | other -> failtestf "Expected NotFound, got %A" other
        }

        testCaseAsync "BeginConversation refuses ScopeId mismatch"
        <| async {
            let store, scopeA, scopeB = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            // Conversation declares scopeA but we pass scopeB.
            let conv = mkConversation convId scopeA "alice"

            match! (writer store).BeginConversation(scopeB, conv) with
            | Error(ConversationError.ScopeMismatch _) -> ()
            | other -> failtestf "Expected ScopeMismatch, got %A" other
        }

        testCaseAsync "ListByScope returns scope's conversations only"
        <| async {
            let store, scopeA, scopeB = factory ()
            let cA = "conv-A-" + Guid.NewGuid().ToString("N")
            let cB = "conv-B-" + Guid.NewGuid().ToString("N")
            do! expectOk "BeginA" ((writer store).BeginConversation(scopeA, mkConversation cA scopeA "alice"))
            do! expectOk "BeginB" ((writer store).BeginConversation(scopeB, mkConversation cB scopeB "bob"))

            let! listA = (reader store).ListByScope scopeA
            let! listB = (reader store).ListByScope scopeB

            Expect.equal listA.Length 1 "scopeA has 1 conversation"
            Expect.equal listB.Length 1 "scopeB has 1 conversation"
            Expect.equal listA[0].ConversationId cA "scopeA shows its own conv"
            Expect.equal listB[0].ConversationId cB "scopeB shows its own conv"
        }

        testCaseAsync "Cross-scope isolation — scopeA can't see scopeB's conversation"
        <| async {
            let store, scopeA, scopeB = factory ()
            let cB = "conv-B-" + Guid.NewGuid().ToString("N")
            do! expectOk "BeginB" ((writer store).BeginConversation(scopeB, mkConversation cB scopeB "alice"))

            // ScopeA queries by the same conversation id — must NOT
            // resolve. Cross-scope existence is itself information
            // disclosure; substrate refuses to leak it.
            match! (reader store).GetConversation(scopeA, cB) with
            | Error(ConversationError.NotFound _) -> ()
            | other -> failtestf "Expected NotFound on cross-scope read, got %A" other
        }

        testCaseAsync "ListByUser filters by CreatedBy"
        <| async {
            let store, scopeA, _ = factory ()
            let cAlice = "conv-alice-" + Guid.NewGuid().ToString("N")
            let cBob = "conv-bob-" + Guid.NewGuid().ToString("N")
            do! expectOk "BeginAlice" ((writer store).BeginConversation(scopeA, mkConversation cAlice scopeA "alice"))
            do! expectOk "BeginBob" ((writer store).BeginConversation(scopeA, mkConversation cBob scopeA "bob"))

            let! aliceConvs = (reader store).ListByUser(scopeA, "alice")
            Expect.equal aliceConvs.Length 1 "alice has 1"
            Expect.equal aliceConvs[0].ConversationId cAlice "alice's conv returned"
        }

        testCaseAsync "Query with Eq predicate filters by Provider"
        <| async {
            let store, scopeA, _ = factory ()
            let cA = "conv-A-" + Guid.NewGuid().ToString("N")
            let cB = "conv-B-" + Guid.NewGuid().ToString("N")

            let cvA = {
                mkConversation cA scopeA "alice" with
                    Provider = "Claude"
            }

            let cvB = {
                mkConversation cB scopeA "alice" with
                    Provider = "OpenAI"
            }

            do! expectOk "Begin A" ((writer store).BeginConversation(scopeA, cvA))
            do! expectOk "Begin B" ((writer store).BeginConversation(scopeA, cvB))

            let query: EntityQuery<Conversation> = {
                EntityType = "Conversation"
                Where = Some(Eq("Provider", "Claude"))
                OrderBy = None
                Skip = 0
                Take = 100
            }

            let! filtered = (reader store).Query(scopeA, query)
            Expect.equal filtered.Length 1 "one Claude conv"
            Expect.equal filtered[0].ConversationId cA "Claude conv returned"
        }

        testCaseAsync "DeleteConversation removes the conversation"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            do! expectOk "Delete" ((writer store).DeleteConversation(scopeA, convId))

            match! (reader store).GetConversation(scopeA, convId) with
            | Error(ConversationError.NotFound _) -> ()
            | other -> failtestf "Expected NotFound after delete, got %A" other
        }

        testCaseAsync "DeleteConversation is idempotent"
        <| async {
            let store, scopeA, _ = factory ()
            do! expectOk "Delete-missing" ((writer store).DeleteConversation(scopeA, "no-such"))
        }

        testCaseAsync "Erase HardDelete removes the user's conversations"
        <| async {
            let store, scopeA, _ = factory ()
            let cAlice = "conv-alice-" + Guid.NewGuid().ToString("N")
            let cBob = "conv-bob-" + Guid.NewGuid().ToString("N")
            do! expectOk "BeginAlice" ((writer store).BeginConversation(scopeA, mkConversation cAlice scopeA "alice"))
            do! expectOk "BeginBob" ((writer store).BeginConversation(scopeA, mkConversation cBob scopeA "bob"))

            let! eraseResult = (eraser store).Erase(scopeA, "alice", ErasurePolicy.HardDelete, false)

            let summary =
                match eraseResult with
                | Ok s -> s
                | Error err -> failtestf "Erase: expected Ok, got %A" err

            Expect.equal summary.RecordsAffected 1 "one conversation matched alice"

            let! aliceConvs = (reader store).ListByUser(scopeA, "alice")
            Expect.equal aliceConvs.Length 0 "alice's conv gone after HardDelete"

            let! bobConvs = (reader store).ListByUser(scopeA, "bob")
            Expect.equal bobConvs.Length 1 "bob's conv unaffected"
        }

        testCaseAsync "Erase Tombstone redacts content but preserves shape"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))
            do! expectOk "AppendUser" ((writer store).AppendTurn(scopeA, convId, mkTurn convId "user" "secret data"))

            let! eraseResult = (eraser store).Erase(scopeA, "alice", ErasurePolicy.Tombstone, false)

            let _ =
                match eraseResult with
                | Ok s -> s
                | Error err -> failtestf "Erase: expected Ok, got %A" err

            let! readResult = (reader store).GetConversation(scopeA, convId)
            let header, turns, _ = okOrFail "Get-after-erase" readResult

            Expect.equal header.CreatedBy Erasure.TombstoneMarker "createdBy redacted"
            Expect.equal turns.Length 1 "turn shape preserved"
            Expect.equal turns[0].Content.Content Erasure.TombstoneMarker "content redacted to marker"
        }

        testCaseAsync "Erase dryRun does not mutate"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            let! eraseResult = (eraser store).Erase(scopeA, "alice", ErasurePolicy.HardDelete, true)

            let summary =
                match eraseResult with
                | Ok s -> s
                | Error err -> failtestf "Erase: expected Ok, got %A" err

            Expect.equal summary.RecordsAffected 1 "dryRun reports the count"

            let! still = (reader store).ListByUser(scopeA, "alice")
            Expect.equal still.Length 1 "dryRun did not mutate"
        }

        testCaseAsync "Erase blank subject is a zero-count no-op"
        <| async {
            let store, scopeA, _ = factory ()
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            do! expectOk "Begin" ((writer store).BeginConversation(scopeA, mkConversation convId scopeA "alice"))

            let! eraseResult = (eraser store).Erase(scopeA, "", ErasurePolicy.HardDelete, false)

            let summary =
                match eraseResult with
                | Ok s -> s
                | Error err -> failtestf "Erase: expected Ok, got %A" err

            Expect.equal summary.RecordsAffected 0 "blank subject zero-count"

            let! still = (reader store).ListByUser(scopeA, "alice")
            Expect.equal still.Length 1 "no records mutated"
        }
    ]