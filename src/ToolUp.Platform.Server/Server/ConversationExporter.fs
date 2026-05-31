// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConversationExporter

open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.IDataExporter

// ─── Phase 53 — IConversationStore DSAR exporter ─────────────────
//
// Bridges `IConversationReader.ListByUser` + `GetConversation` into
// the DSR pipeline's `IDataExporter` extension point (Phase 9h).
// Same additive shape as `DataObjectStoreExporter` /
// `EventStoreExporter` — a deployment opts conversations into DSAR
// coverage by registering this exporter at compose time; the
// orchestrator and every other exporter are untouched.
//
// **Sanitised by default per Phase 6h.A's safeguard semantics.**
// Phase 6h.A's `Export ▾` flow established the policy that
// admin-side conversation internals (tool-call arguments, tool-call
// results, provider/model identifiers, system-prompt digests) are
// not part of the data subject's personal data and are stripped
// from exports by default — the user clicking the export button
// must explicitly opt in to tool-detail inclusion. DSAR exports
// run unattended on the data subject's behalf with no such opt-in
// UI, so this adapter applies the sanitised shape unconditionally:
//
//   - Conversation level — strip `Provider`, `ModelName`,
//     `SystemPromptDigest`, `SdkVersion` (operator / SDK metadata
//     that doesn't name the subject).
//   - Turn level — strip `Content.ToolCalls`, `Content.ToolResults`,
//     `Content.Parts` (admin / system internals). Preserve
//     `Role`, `Content.Content` (the textual body), `Timestamp`,
//     `TokensIn`, `TokensOut` (the user-visible record).
//
// One JSON segment per scope named `conversations/<scopeId>.json`,
// matching `DataObjectStoreExporter` shape so the orchestrator's
// alphabetical-by-name ordering produces a stable export layout.
//
// Reader-only — depends on `IConversationReader`, not the full
// `IConversationStore`, per the ISP split in `IConversationStore.fs`.

[<Literal>]
let private HandlerName = "conversations"

/// Sanitised projection of `Conversation` — strips operator / SDK
/// metadata that doesn't name the data subject. Distinct type so
/// `JsonConvert.SerializeObject` writes exactly these fields without
/// custom contract resolvers; the source `Conversation` record is
/// unchanged. Public so `FableJsonConverter` reflects into the
/// record fields rather than emitting `{}`; nothing outside this
/// module references these types.
type ExportedConversation = {
    ConversationId: ConversationId
    SchemaVersion: int
    CreatedAt: System.DateTime
    CreatedBy: string
    ScopeId: string
    Turns: ExportedTurn list
}

and ExportedTurn = {
    TurnId: TurnId
    SchemaVersion: int
    Role: string
    Content: string
    Timestamp: System.DateTime
    TokensIn: int option
    TokensOut: int option
}

let private sanitiseTurn (turn: ConversationTurn) : ExportedTurn = {
    TurnId = turn.TurnId
    SchemaVersion = turn.SchemaVersion
    Role = turn.Role
    Content = turn.Content.Content
    Timestamp = turn.Timestamp
    TokensIn = turn.TokensIn
    TokensOut = turn.TokensOut
}

let private sanitiseConversation (header: Conversation) (turns: ConversationTurn list) : ExportedConversation = {
    ConversationId = header.ConversationId
    SchemaVersion = header.SchemaVersion
    CreatedAt = header.CreatedAt
    CreatedBy = header.CreatedBy
    ScopeId = header.ScopeId
    Turns = turns |> List.map sanitiseTurn
}

// `ExportedConversation` carries `int option` / `DateTime` which
// FableJsonConverter round-trips cleanly; system-text-json mangles
// F# option types. Mirror the JsonSettings shape established by
// `DataObjectStoreExporter`.
let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

/// `IDataExporter` for the conversation substrate. Emits the
/// sanitised projection of every conversation in the caller's scope
/// whose `CreatedBy` matches the subject. Operator-side internals
/// (tool-call args/results, provider, model, system-prompt digest)
/// are stripped per Phase 6h.A's safeguard semantics — DSAR exports
/// run without a tool-detail opt-in UI.
type ConversationExporter(reader: IConversationReader) =
    interface IDataExporter with
        member _.Name = HandlerName

        member _.Export(scopeId, subjectUserId) = async {
            if Erasure.isBlankSubject subjectUserId then
                return []
            else
                let! headers = reader.ListByUser(scopeId, subjectUserId)

                if List.isEmpty headers then
                    return []
                else
                    let! exported =
                        headers
                        |> List.map (fun header -> async {
                            let! result = reader.GetConversation(scopeId, header.ConversationId)

                            return
                                match result with
                                | Result.Ok(header, turns, _status) -> Some(sanitiseConversation header turns)
                                | Result.Error _ -> None
                        })
                        |> Async.Sequential

                    let payload = exported |> Array.choose id |> Array.toList

                    if List.isEmpty payload then
                        return []
                    else
                        let json = JsonConvert.SerializeObject(payload, jsonSettings)

                        return [
                            {
                                Name = sprintf "conversations/%s.json" scopeId
                                MimeType = "application/json"
                                Body = Encoding.UTF8.GetBytes json
                            }
                        ]
        }

/// Compose-time registration helper. Pair with `withDataExporter`
/// when DSAR coverage of conversations is desired:
/// `ServerApp.withDataExporter (ConversationExporter.exporter conversationReader) app`.
let exporter (reader: IConversationReader) : IDataExporter =
    ConversationExporter reader :> IDataExporter