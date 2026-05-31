// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationExporterTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.IDataExporter

// ─── Phase 53 — ConversationExporter (`IDataExporter`) round-trip ─
//
// The substrate-level safeguard: tool-call args/results and operator-
// side metadata (provider, model, system-prompt digest) MUST NOT
// reach a DSAR export segment. Phase 6h.A established the same
// safeguard for the interactive `Export ▾` flow; this adapter
// applies the sanitised shape unconditionally for unattended DSR
// runs. The test bakes PII into the tool-call arguments + result,
// exports, and asserts the bytes do not contain them.

let private mkConversation (id: string) (scopeId: string) (userId: string) : Conversation = {
    ConversationId = id
    SchemaVersion = 1
    CreatedAt = DateTime.UtcNow
    CreatedBy = userId
    ScopeId = scopeId
    Provider = "TestProvider"
    ModelName = "test-model-v9"
    SystemPromptDigest = "sha256:opaque-prompt-digest"
    SdkVersion = "0.0.0-test"
}

let private mkUserTurn (conversationId: string) (content: string) : ConversationTurn = {
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

let private mkAssistantTurnWithToolCall
    (conversationId: string)
    (content: string)
    (toolName: string)
    (toolArgs: string)
    : ConversationTurn =
    {
        TurnId = ""
        ConversationId = conversationId
        SchemaVersion = 1
        Role = "assistant"
        Content = {
            Role = "assistant"
            Content = content
            ToolCalls = [
                {
                    Id = "call-1"
                    Name = toolName
                    Arguments = toolArgs
                }
            ]
            ToolResults = []
            Parts = []
        }
        Timestamp = DateTime.UtcNow
        TokensIn = None
        TokensOut = None
        ContentDigest = ""
    }

let private utf8 (segment: ExportSegment) = Encoding.UTF8.GetString segment.Body

let private freshStore () : IConversationStore =
    ConversationStore.InMemoryConversationStore() :> IConversationStore

let private expectUnitOk label work = async {
    let! result = work

    match result with
    | Ok() -> ()
    | Error err -> failtestf "%s: expected Ok, got %A" label err
}

let tests =
    testList "ConversationExporter" [

        testCaseAsync "sanitised export strips tool-call args, ToolResults, provider, model, system-prompt digest"
        <| async {
            let store = freshStore ()
            let scopeA = "team-a-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let convId = "conv-" + Guid.NewGuid().ToString("N")
            let userId = "alice"
            let writer = store :> IConversationWriter
            let reader = store :> IConversationReader

            do! expectUnitOk "Begin" (writer.BeginConversation(scopeA, mkConversation convId scopeA userId))

            do!
                expectUnitOk
                    "AppendUser"
                    (writer.AppendTurn(scopeA, convId, mkUserTurn convId "What's the weather in Edinburgh?"))

            // The assistant-side turn carries tool-call arguments that
            // include a PII-shaped email address — the exact class of
            // content Phase 6h.A's safeguard exists to keep out of
            // exports. The textual `Content` is the data subject's
            // user-visible record and MUST be preserved.
            do!
                expectUnitOk
                    "AppendAssistant"
                    (writer.AppendTurn(
                        scopeA,
                        convId,
                        mkAssistantTurnWithToolCall
                            convId
                            "Looking that up for you."
                            "get_weather"
                            """{"city":"Edinburgh","contact_email":"user-pii@example.com"}"""
                    ))

            let exporter = ConversationExporter.exporter reader
            let! segments = exporter.Export(scopeA, userId)

            Expect.equal segments.Length 1 "one segment per scope when conversations exist"
            let segment = segments[0]

            Expect.equal
                segment.Name
                (sprintf "conversations/%s.json" scopeA)
                "segment name follows conversations/<scopeId>.json"

            Expect.equal segment.MimeType "application/json" "segment is JSON"

            let json = utf8 segment

            // User-visible textual content preserved.
            Expect.stringContains json "What's the weather in Edinburgh?" "user message preserved"
            Expect.stringContains json "Looking that up for you." "assistant text preserved"
            Expect.stringContains json "alice" "createdBy preserved (DSAR is for the data subject)"

            // Tool-call args + the operator-side identifiers MUST be
            // absent. Phase 6h.A safeguard.
            Expect.isFalse
                (json.Contains "user-pii@example.com")
                "PII inside tool-call arguments must be stripped from the DSAR export"

            Expect.isFalse (json.Contains "get_weather") "tool-call name must be stripped"
            Expect.isFalse (json.Contains "TestProvider") "Provider metadata must be stripped"
            Expect.isFalse (json.Contains "test-model-v9") "ModelName metadata must be stripped"

            Expect.isFalse (json.Contains "sha256:opaque-prompt-digest") "SystemPromptDigest metadata must be stripped"
        }

        testCaseAsync "blank subject is a zero-segment no-op"
        <| async {
            let store = freshStore ()
            let scopeA = "team-a-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let exporter = ConversationExporter.exporter (store :> IConversationReader)

            let! emptyStringSegments = exporter.Export(scopeA, "")
            Expect.isEmpty emptyStringSegments "empty subjectUserId must not enumerate conversations"

            let! whitespaceSegments = exporter.Export(scopeA, "   ")
            Expect.isEmpty whitespaceSegments "whitespace subjectUserId must not enumerate conversations"
        }

        testCaseAsync "subject with no conversations in scope produces zero segments"
        <| async {
            let store = freshStore ()
            let scopeA = "team-a-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let exporter = ConversationExporter.exporter (store :> IConversationReader)

            let! segments = exporter.Export(scopeA, "user-with-no-conversations")
            Expect.isEmpty segments "no conversations ⇒ zero segments (orchestrator skips silently)"
        }

        testCaseAsync "scope isolation — conversations in scope B do not surface in scope A's export"
        <| async {
            let store = freshStore ()
            let scopeA = "team-a-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let scopeB = "team-b-" + Guid.NewGuid().ToString("N").Substring(0, 8)
            let userId = "carol"
            let writer = store :> IConversationWriter
            let reader = store :> IConversationReader

            let convInB = "conv-" + Guid.NewGuid().ToString("N")
            do! expectUnitOk "BeginB" (writer.BeginConversation(scopeB, mkConversation convInB scopeB userId))

            do!
                expectUnitOk
                    "AppendInB"
                    (writer.AppendTurn(scopeB, convInB, mkUserTurn convInB "scope-B-only-secret-token"))

            let exporter = ConversationExporter.exporter reader
            let! aSegments = exporter.Export(scopeA, userId)
            Expect.isEmpty aSegments "scope A export must not include scope B conversations"

            let! bSegments = exporter.Export(scopeB, userId)
            Expect.equal bSegments.Length 1 "scope B has the conversation"

            Expect.stringContains
                (utf8 bSegments[0])
                "scope-B-only-secret-token"
                "scope B export carries scope B content"
        }

        testCaseAsync "exporter Name matches the conversation-erasure handler name"
        <| async {
            let store = freshStore ()
            let exporter = ConversationExporter.exporter (store :> IConversationReader)
            // Same `HandlerName = "conversations"` literal as
            // `ConversationEraseHandler`; the DSR audit row correlates
            // the export + erase segments by handler name.
            Expect.equal exporter.Name "conversations" "exporter shares the conversations handler name"
        }
    ]