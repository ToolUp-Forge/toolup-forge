// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ConversationExportAuditHandler

open System
open System.IO
open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Giraffe
open ToolUp.Platform

// ─── Phase 6h.A — conversation-export audit beacon ──────────────
//
// `POST /api/ai/conversation/export-audit`. The chat side panel's
// `Export ▾` flow fires this (fire-and-forget) after a successful
// download so an admin can see *that* a conversation was exported
// and *whether* the user opted into tool-detail inclusion.
//
// Deliberately a thin fire-and-forget endpoint mirroring
// `FastPathBeaconHandler` rather than a new Fable.Remoting method:
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
// decoded via FableJsonConverter (same idiom as `FastPathBeacon`).
type ExportAuditRequest = {
    ConversationId: Guid
    IncludeToolDetails: bool
}

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(Fable.Remoting.Json.FableJsonConverter())
    s

let private resolveScope (ctx: HttpContext) : StorageScope =
    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
    | true, (:? StorageScope as s) -> s
    | _ ->
        let fallback =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        {
            ScopeId = fallback
            Container = $"user-{fallback}"
            Persist = true
        }

let private resolveUserId (ctx: HttpContext) : string =
    match ctx.Items.TryGetValue "ToolUp.UserId" with
    | true, (:? string as id) -> id
    | _ -> "anonymous"

let exportAuditHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()
            let req = JsonConvert.DeserializeObject<ExportAuditRequest>(body, jsonSettings)

            if isNull (box req) then
                ctx.Response.StatusCode <- 400
                return! next ctx
            else
                let scope = resolveScope ctx

                match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                | :? IAuditLog as auditLog ->
                    do!
                        auditLog.Record(
                            scope.ScopeId,
                            ConversationExported {
                                ConversationId = string req.ConversationId
                                IncludeToolDetails = req.IncludeToolDetails
                                ExportedBy = resolveUserId ctx
                            }
                        )
                | _ -> ()

                ctx.Response.StatusCode <- 202
                return! next ctx
        with _ ->
            ctx.Response.StatusCode <- 400
            return! next ctx
    }