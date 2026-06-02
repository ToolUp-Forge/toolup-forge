// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ConsentApiHandler

open System
open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 59 — server-side consent-audit endpoint ────────────────
//
// Mounted only when `ServerConfig.ConsentAudit = EnabledConsentAudit`.
// Receives client-side `ConsentEvent` posts from the configured
// `IConsentProvider` and records them via `IAuditLog` under
// `_platform` scope. The audit emission is fire-and-forget — failures
// to record do not fail the client's consent capture; the client
// already has the decision in localStorage / CMP state.
//
// Recorded under `_platform` scope (deployment-wide; no tenant
// scope — consent is browser-local). Body shape matches
// `ToolUp.Platform.ConsentEvent` verbatim — the wire is the same
// record type the client serialises.

let private PlatformScope = "_platform"

let private jsonOptions = FableConverters.create ()

let private readBody (ctx: HttpContext) : System.Threading.Tasks.Task<string> = task {
    use reader = new System.IO.StreamReader(ctx.Request.Body)
    return! reader.ReadToEndAsync()
}

let private consentHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! body = readBody ctx

        let event =
            try
                Some(JsonSerializer.Deserialize<ConsentEvent>(body, jsonOptions))
            with _ ->
                None

        match event with
        | None ->
            ctx.Response.StatusCode <- 400
            return! ctx.WriteTextAsync "Malformed ConsentEvent payload"
        | Some ev ->
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as auditLog ->
                // Audit emission is fire-and-forget per the `IAuditLog`
                // contract — failure to record does not fail the client
                // capture.
                auditLog.Record(PlatformScope, AuditEvent.ConsentRecorded ev) |> Async.Start
            | _ -> ()

            ctx.Response.StatusCode <- 204
            return! next ctx
    }

/// Routes table. Mounted by `compose` only when
/// `ServerConfig.ConsentAudit = EnabledConsentAudit`.
let routes: HttpHandler list = [ POST >=> route "/api/_platform/consent" >=> consentHandler ]