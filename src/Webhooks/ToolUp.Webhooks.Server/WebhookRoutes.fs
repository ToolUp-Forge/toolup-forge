namespace ToolUp.Webhooks.Server

open System.Text
open System.IO
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open Giraffe
open ToolUp.Webhooks

/// Giraffe / ASP.NET Core wiring for the generic inbound-webhook
/// receiver: verify (fail-closed) → dedup → dispatch. Opt-in — nothing
/// runs unless a deployment mounts `WebhookRoutes.routes`.
module WebhookRoutes =

    /// HTTP status for a verification failure. `TimestampDrift` → 408
    /// (matching the freshness-window convention); a missing handler →
    /// 404; every other verification failure → 400.
    let private statusForError (err: WebhookError) : int =
        match err with
        | TimestampDrift _ -> 408
        | NoHandlerRegistered _ -> 404
        | MissingSignature
        | MalformedHeader
        | SignatureMismatch
        | DuplicateDelivery -> 400

    /// Reason text for a non-200 path. Contains NO secret material — no
    /// signature bytes, no secret, no body.
    let private describe (err: WebhookError) : string =
        match err with
        | MissingSignature -> "missing signature header"
        | MalformedHeader -> "malformed signature header"
        | TimestampDrift seconds -> sprintf "timestamp drift %ds outside the freshness window" seconds
        | SignatureMismatch -> "signature mismatch"
        | DuplicateDelivery -> "duplicate delivery"
        | NoHandlerRegistered kind -> sprintf "no handler registered for kind '%s'" kind

    let private readRawBody (ctx: HttpContext) : Task<string> = task {
        use reader =
            new StreamReader(
                ctx.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks = false,
                leaveOpen = true
            )

        return! reader.ReadToEndAsync()
    }

    let private headerLookup (ctx: HttpContext) : string -> string option =
        fun name ->
            match ctx.Request.Headers.TryGetValue name with
            | true, values -> Some(string values)
            | _ -> None

    /// Handle one verified-by-kind delivery: resolve secret → verify →
    /// dedup → dispatch. `resolveSecret kind` yields the signing secret
    /// for that integration (the composer wires it over `ISecretStore`,
    /// per the companion-authoring guide — this module never reads
    /// secrets directly).
    let private handleKind
        (scheme: WebhookScheme)
        (resolveSecret: string -> Async<string option>)
        (dedup: IWebhookDedupStore)
        (registry: WebhookRegistry)
        (kind: string)
        : HttpHandler =
        fun (next: HttpFunc) (ctx: HttpContext) -> task {
            let logger = ctx.GetLogger "ToolUp.Webhooks.Server.Routes"
            let finish (code: int) (payload: string) : HttpHandler = setStatusCode code >=> text payload

            match registry.TryFind kind with
            | None ->
                let err = NoHandlerRegistered kind
                logger.LogWarning("Inbound webhook rejected: {Reason}", describe err)
                return! finish (statusForError err) "no handler" next ctx
            | Some handler ->
                let! secretOpt = resolveSecret kind |> Async.StartImmediateAsTask

                match secretOpt with
                | None ->
                    // No secret configured for this kind — fail closed
                    // (never dispatch an unverifiable delivery).
                    logger.LogWarning("Inbound webhook for kind '{Kind}' has no configured secret — rejecting.", kind)
                    return! finish 400 "no secret configured" next ctx
                | Some secret ->
                    let! body = readRawBody ctx

                    match WebhookVerifier.verify scheme kind secret body (headerLookup ctx) with
                    | Error err ->
                        logger.LogWarning(
                            "Inbound webhook verification failed for '{Kind}': {Reason}",
                            kind,
                            describe err
                        )

                        return! finish (statusForError err) "verification failed" next ctx
                    | Ok verified ->
                        // Dedup only when the scheme yielded an event id;
                        // an idless delivery is processed without dedup
                        // (a redelivery would re-run) — surfaced once.
                        let! claimed =
                            match verified.EventId with
                            | Some id -> dedup.TryClaim(kind, id) |> Async.StartImmediateAsTask
                            | None ->
                                logger.LogWarning(
                                    "Inbound webhook '{Kind}' has no event id (scheme.EventIdHeader unset or absent) — processing WITHOUT dedup; a redelivery will be processed again.",
                                    kind
                                )

                                Task.FromResult true

                        if not claimed then
                            logger.LogInformation("Inbound webhook '{Kind}' replay ignored — short-circuit 200", kind)
                            return! finish 200 "ok (replay)" next ctx
                        else
                            let! ack = handler.Handle verified |> Async.StartImmediateAsTask

                            match ack with
                            | Acknowledged -> return! finish 200 "ok" next ctx
                            | Ignored reason ->
                                logger.LogInformation(
                                    "Inbound webhook '{Kind}' ignored by handler: {Reason}",
                                    kind,
                                    reason
                                )

                                return! finish 200 "ok (ignored)" next ctx
        }

    /// Mount `POST /webhooks/{kind}` for every registered handler. The
    /// `kind` route segment selects the handler; an unknown kind → 404.
    let routes
        (scheme: WebhookScheme)
        (resolveSecret: string -> Async<string option>)
        (dedup: IWebhookDedupStore)
        (registry: WebhookRegistry)
        : HttpHandler =
        POST >=> routef "/webhooks/%s" (handleKind scheme resolveSecret dedup registry)