module ToolUp.AI.AIConsentHandler

open System
open System.IO
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open Giraffe
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.AI

/// Phase 36.D — `POST /api/ai/consent`.
///
/// The browser's half of the consent round trip: the user answered an
/// `AIConsentRequired` dialog, and this completes the `_platform.ai.*`
/// tool suspended on the matching `TaskCompletionSource`.
///
/// Deliberately shaped after `/api/ai/tool-result` (Phase 6g.A + the
/// Phase 36.A permission re-check bolted onto it), because it is the same
/// endpoint class: a small correlated POST that resumes a parked server
/// thread. The three things that class has to get right are the three
/// things it gets right here — authorise from the SERVER's record of the
/// pending request rather than from the body, authorise BEFORE completing
/// (completion removes the entry, so a later check would check nothing),
/// and answer an unknown id with a quiet 404 rather than an error the user
/// sees.

// ─── Audit ───────────────────────────────────────────────────────

/// The audit source every consent decision is recorded under. One source,
/// two event types, for the same reason Phase 36.A gave the RBAC denial
/// its own stream: an operator asking "what did users allow the agent to
/// read" should read one stream and get every answer, not union two.
[<Literal>]
let ConsentAuditSource = "_platform.ai.consent"

[<Literal>]
let ConsentGrantedEvent = "AIConsentGranted"

[<Literal>]
let ConsentDeniedEvent = "AIConsentDenied"

let private jsonOptions = FableConverters.create ()

/// Record the decision on the event store, best-effort.
///
/// Best-effort by the same rule the loop's own denial audit follows: the
/// control is the DECISION, which has already been applied by the time
/// this runs, and a wedged backend must never turn a user's click into a
/// failed turn. Every field the acceptance names is present —
/// `userId` / `conversationId` / `targetModule` / `decision` — and the
/// decision is written as its stable token, not as an F# DU rendering, so
/// an operator query cuts on a string that outlives this build.
let private writeConsentAudit
    (ctx: HttpContext)
    (logger: ILogger)
    (scopeId: string)
    (pending: AIConsentDispatch.PendingConsent)
    (decision: AllowDecision)
    : Async<unit> =
    async {
        match ctx.RequestServices.GetService typeof<IEventStore> with
        | :? IEventStore as store ->
            let payload = {|
                UserId = pending.UserId
                ConversationId = pending.ConversationId
                TargetModule = pending.TargetModule
                Decision = AllowDecision.toToken decision
            |}

            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scopeId
                SourceModule = ConsentAuditSource
                EventType =
                    match decision with
                    | Denied -> ConsentDeniedEvent
                    | AllowOnce
                    | AllowForConversation -> ConsentGrantedEvent
                Payload = JsonSerializer.Serialize(payload, jsonOptions)
            }

            try
                do! store.Write evt
            with ex ->
                logger.Warn
                    $"[AIConsent] audit write failed (conversation={pending.ConversationId}, module={pending.TargetModule}): {ex.Message}. Record dropped; the decision still applied."
        | _ -> return ()
    }

// ─── Handler ─────────────────────────────────────────────────────

let private noOpLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Giraffe handler for `POST /api/ai/consent`.
///
/// Status codes, and why each is the one it is:
///
///   * **200** — the decision was recorded and the suspended read resumed.
///   * **403** — the caller does not hold `Read` on the module the pending
///     request names. The symmetric half of the gate, exactly as Phase
///     36.A added it to `/api/ai/tool-result`: a consent id is a Guid and
///     guessing one is infeasible, but the permission check is the
///     load-bearing protection and it belongs on both ends of the round
///     trip. The pending request is left registered — its lifetime belongs
///     to the tool's own timeout, and letting an unauthorised POST cancel
///     a legitimate in-flight prompt would be a denial-of-service.
///   * **404** — no such pending request. A late click after the tool's
///     timeout, a double submit, or a replayed body. Silent by design: the
///     read is already over and there is nothing the user can do about it.
///   * **400** — the body did not deserialise.
let consentDecisionHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let logger: ILogger =
            match ctx.RequestServices.GetService typeof<ILogger> with
            | :? ILogger as l -> l
            | _ -> noOpLogger

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            let req = JsonSerializer.Deserialize<AIConsentDecisionRequest>(body, jsonOptions)

            let registry =
                ctx.RequestServices.GetService typeof<AIConsentDispatch.AIConsentRegistry>
                :?> AIConsentDispatch.AIConsentRegistry

            // Read the pending request BEFORE completing it: `TryComplete`
            // removes the entry, so authorising afterwards would authorise
            // nothing and the read would already have resumed.
            match registry.PendingOf req.ConsentId with
            | None ->
                ctx.Response.StatusCode <- 404
                return! next ctx
            | Some pending ->
                // Fails CLOSED: a token this build cannot interpret reads
                // as `Denied`, so a malformed or forged decision refuses
                // the read rather than authorising it.
                let decision = AllowDecision.ofToken req.Decision

                let access = AIToolRegistry.reconstructAccessContext ctx

                if not (AccessContext.hasPermission pending.TargetModule ModulePermission.Read access) then
                    AIToolRegistry.recordUnauthorizedToolDenial
                        ctx
                        access
                        (sprintf "%s %s" ctx.Request.Method (string ctx.Request.Path))
                        (sprintf "consent:%O" req.ConsentId)
                        pending.TargetModule

                    logger.Warn(
                        sprintf
                            "[AIConsent] decision POST rejected (403): caller lacks Read on target module '%s' for consentId %O"
                            pending.TargetModule
                            req.ConsentId
                    )

                    ctx.Response.StatusCode <- 403
                    return! next ctx
                else
                    let scopeId =
                        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                        | true, (:? StorageScope as s) -> s.ScopeId
                        | _ -> access.UserId

                    // Persist BEFORE completing, so the decision is durable
                    // by the time the resumed read (and every read after it
                    // in this conversation) consults the record. Completing
                    // first would leave a window in which a second tool call
                    // in the same parallel batch re-prompts for a module the
                    // user has just answered about.
                    //
                    // The write uses the CONTAINER the suspending tool
                    // recorded, not one re-derived from this request: the
                    // POST is a different request with its own scope
                    // resolution, and the record has to land where the tool
                    // will look for it.
                    match ctx.RequestServices.GetService typeof<IBlobStorage> with
                    | :? IBlobStorage as storage ->
                        do!
                            AIConsentDispatch.recordDecision
                                storage
                                pending.Container
                                pending.ConversationId
                                pending.TargetModule
                                decision
                    | _ ->
                        // No blob substrate: the decision still applies to
                        // the read in front of the user, it simply is not
                        // remembered. Worth a line — a deployment in this
                        // state re-prompts on every read and the cause is
                        // not otherwise visible.
                        logger.Warn
                            "[AIConsent] no IBlobStorage registered; the decision applies to this read but will not be remembered for the conversation."

                    do! writeConsentAudit ctx logger scopeId pending decision

                    if registry.TryComplete(req.ConsentId, decision) then
                        ctx.Response.StatusCode <- 200
                    else
                        // The tool timed out between our read of the pending
                        // entry and this completion. The decision is
                        // recorded and audited — it simply arrived too late
                        // to resume this read — so 404 (nothing was
                        // completed) is the honest answer.
                        ctx.Response.StatusCode <- 404

                    return! next ctx
        with ex ->
            logger.Warn $"[AIConsent] decision POST rejected (400): {ex.Message}"
            ctx.Response.StatusCode <- 400
            return! next ctx
    }