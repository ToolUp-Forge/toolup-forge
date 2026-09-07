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

let private consentHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        // Phase 763 — bound through the shared seam. The previous
        // `try Some(Deserialize) with _ -> None` shape let a body of the
        // JSON literal `null` through as `Some null`, and this handler
        // does not dereference the event at all: it handed the null
        // straight to `IAuditLog.Record`, answered 204, and wrote a
        // meaningless row into the consent trail. That is the same
        // defect class as the ad-analytics 500 and a quieter one.
        let! bound = HttpBodyBinding.tryBindJson<ConsentEvent> jsonOptions ctx

        match bound with
        | Error err ->
            ctx.Response.StatusCode <- HttpBodyBinding.statusCodeFor err
            return! ctx.WriteTextAsync "Malformed ConsentEvent payload"
        | Ok ev ->
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