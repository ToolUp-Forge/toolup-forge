// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ToolApprovalHandler

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open ToolUp.Platform
open ToolUp.AI
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

/// `POST /api/ai/tool-approval`.
///
/// The browser's half of the approval round trip: the user answered a
/// `ToolApprovalRequired` dialog, and this completes the tool invocation
/// held on the matching `TaskCompletionSource`.
///
/// Deliberately shaped after `/api/ai/consent` (36.D) and
/// `/api/ai/tool-result` (6g.A + 36.A), because it is the same endpoint
/// class: a small correlated POST that resumes a parked server thread.
/// The three things that class has to get right are the three things it
/// gets right here — authorise from the SERVER's record of the pending
/// request rather than from the body, authorise BEFORE completing
/// (completion removes the entry, so a later check would check nothing),
/// and answer an unknown id with a quiet 404 rather than an error the
/// user sees.

let private jsonOptions = PlatformJson.options

let private noOpLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Giraffe handler for `POST /api/ai/tool-approval`.
///
/// Status codes, and why each is the one it is:
///
///   * **200** — the decision was recorded and the held invocation
///     resumed (to run, or to be refused).
///   * **403** — the caller does not hold `Read` on the source module of
///     the tool the pending record names. The symmetric half of the gate,
///     exactly as the RBAC re-check added it to `/api/ai/tool-result`: an
///     approval id is a Guid and guessing one is infeasible, but the
///     permission check is the load-bearing protection and it belongs on
///     both ends of the round trip. The pending record is left
///     registered — its lifetime belongs to the gate's own budget, and
///     letting an unauthorised POST cancel a legitimate in-flight prompt
///     would be a denial of service.
///   * **404** — no such pending approval. A late click after the gate's
///     timeout, a double submit, or a replayed body. Silent by design:
///     the invocation is already refused and there is nothing the user
///     can do about it.
///   * **400** — the body did not deserialise.
let toolApprovalDecisionHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let logger: ILogger =
            match ctx.RequestServices.GetService typeof<ILogger> with
            | :? ILogger as l -> l
            | _ -> noOpLogger

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            let req = JsonSerializer.Deserialize<ToolApprovalDecisionRequest>(body, jsonOptions)

            let registry =
                ctx.RequestServices.GetService typeof<ToolApprovalDispatch.ToolApprovalRegistry>
                :?> ToolApprovalDispatch.ToolApprovalRegistry

            // Read the pending record BEFORE completing it: `TryComplete`
            // removes the entry, so authorising afterwards would authorise
            // nothing and the invocation would already have resumed.
            match registry.PendingOf req.ApprovalId with
            | None ->
                ctx.Response.StatusCode <- 404
                return! next ctx
            | Some pending ->
                // Fails CLOSED: a token this build cannot interpret reads
                // as `Rejected`, so a malformed or forged decision refuses
                // the action rather than running it.
                let decision = ToolApprovalDecision.ofToken req.Decision

                let complete (_: AccessContext) = async {
                    let store =
                        match ctx.RequestServices.GetService typeof<IEventStore> with
                        | :? IEventStore as s -> Some s
                        | _ -> None

                    let eventType =
                        match decision with
                        | Approved -> ToolApprovalDispatch.ApprovalGrantedEvent
                        | Rejected -> ToolApprovalDispatch.ApprovalRejectedEvent

                    // Audit BEFORE completing: the invocation resumes the
                    // instant it is completed, and the decision record must
                    // not arrive second.
                    do! ToolApprovalDispatch.writeApprovalAudit store logger eventType pending
                    return registry.TryComplete(req.ApprovalId, decision)
                }

                let! status =
                    AIToolRegistry.Gates.completePending
                        ctx
                        logger
                        $"approval:{req.ApprovalId}"
                        (Some pending.SourceModule)
                        complete

                ctx.Response.StatusCode <- status
                return! next ctx
        with ex ->
            logger.Warn $"[ToolApproval] decision POST rejected (400): {ex.Message}"
            ctx.Response.StatusCode <- 400
            return! next ctx
    }