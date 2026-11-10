// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ConversationExportAuditHandler

open System
open System.IO
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open ToolUp.Platform
open ToolUp.AI.AIAuditEvents
open ToolUp.Platform.Compose

// ─── Conversation-export audit beacon ──────────────
//
// `POST /api/ai/conversation/export-audit`. The chat side panel's
// `Export ▾` flow fires this (fire-and-forget) after a successful
// download so an admin can see *that* a conversation was exported
// and *whether* the user opted into tool-detail inclusion.
//
// Deliberately a thin fire-and-forget endpoint mirroring
// `FastPathBeaconHandler` rather than a new ToolUp.Remoting method:
// it is the codebase's established "client tells server something
// happened, for audit" pattern, keeps the shared `AIAssistantApi`
// Remoting contract untouched, and the acceptance criterion ("one
// `ConversationExported` audit event per export click queryable via
// `IAuditLog.GetAuditTrail`") is satisfied identically either way.
//
// The emitted `ConversationExported` event is METADATA ONLY — the
// conversation content and tool payloads never reach the audit sink.
// `ExportedBy` is taken from the server-side request identity, never
// trusted from the client body.

// Wire shape — must match Client/ConversationPanel.fs. Plain record
// decoded via FableConverters (same idiom as `FastPathBeacon`).
type ExportAuditRequest = {
    ConversationId: Guid
    IncludeToolDetails: bool
}

let private jsonOptions = PlatformJson.options

let exportAuditHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let req = JsonSerializer.Deserialize<ExportAuditRequest>(body, jsonOptions)

            match AIRequest.access ctx with
            | _ when isNull (box req) ->
                ctx.Response.StatusCode <- 400
                return! next ctx
            | None ->
                ctx.Response.StatusCode <- 401
                return! next ctx
            | Some access ->
                let scope = AIRequest.scopeOf access

                match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                | :? IAuditLog as auditLog ->
                    do!
                        auditLog.Record(
                            StorageScope.auditScope scope,
                            ToolUp.Platform.Config.Background.requestActor "ConversationExport" ctx,
                            AIAuditEvent.event (
                                ConversationExported {
                                    ConversationId = string req.ConversationId
                                    IncludeToolDetails = req.IncludeToolDetails
                                    ExportedBy = access.UserId
                                }
                            )
                        )
                        |> AuditRow.orRefuse
                | _ -> ()

                ctx.Response.StatusCode <- 202
                return! next ctx
        with _ ->
            ctx.Response.StatusCode <- 400
            return! next ctx
    }