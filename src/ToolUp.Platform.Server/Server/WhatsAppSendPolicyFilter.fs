// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── WhatsAppSendPolicyFilter (Phase 827) ─────────────────────────────
//
// The vendor-neutral WhatsApp Business rules, enforced once on the send
// path so neither vendor companion has to:
//
//   1. A FREE-FORM message (`Body`, no `TemplateName`) may only reach a
//      recipient inside the 24-hour customer-care window their own last
//      inbound message opened. A recipient outside it — or with no
//      inbound on record at all — is refused.
//   2. A TEMPLATE message must name a template the deployment's
//      `IWhatsAppTemplateRegistry` knows, in a language it is approved
//      in, with exactly the parameter count it declares. Anything else
//      is refused before a sink is called, rather than bounced by the
//      vendor as a billed `PermanentFailure`.
//
// **Where it sits.** A channel decorator composed directly INSIDE the
// Phase 6f.A consent gate and outside the Phase 441 preference filter:
//
//     ExternalContactConsentFilter      (consent — outermost)
//       -> WhatsAppSendPolicyFilter     (this file)
//         -> NotificationPreferenceFilter
//           -> DispatchingNotificationChannel -> TransactionalDispatcher
//
// Never before the consent gate: a recipient with no lawful basis is
// refused as `no_opt_in` first, and only a consented recipient is then
// asked the window question. Never inside the dispatcher core: the
// dispatcher runs after the preference filter, and a rule enforced
// there would be answered after a preference had already been applied
// (and after a digest could have held the send past its window).
//
// **A refusal is audited, not skipped.** Every refused recipient lands
// on a `NotificationDeliveryRefused` row with a reason naming the rule.
// The payload's `Reason` is a string precisely so a channel arm can add
// its own; no new audit case is introduced.
//
// **Fail closed.** A contact that cannot be read, a registry that
// throws, a template that cannot be verified: each is a refusal. The
// window and the template rule are the vendor's terms of service, and
// a send that breaks them risks the deployment's sender standing.
//
// **Default-off.** Composed only when a `SinkKind.WhatsApp` sink is
// registered; every other notification kind passes through untouched.

/// Literals and pure rules for the WhatsApp send policy.
module WhatsAppSendPolicy =
    /// A free-form send to a recipient outside the 24-hour window.
    [<Literal>]
    let OutsideWindowReason = "outside_24h_window_no_template"

    /// A template the registry does not know (or cannot vouch for).
    [<Literal>]
    let TemplateUnknownReason = "whatsapp_template_unknown"

    /// A template language the registered template is not approved in.
    [<Literal>]
    let TemplateLanguageReason = "whatsapp_template_language_unsupported"

    /// A parameter count that disagrees with the registered arity.
    [<Literal>]
    let TemplateArityReason = "whatsapp_template_arity_mismatch"

    /// An envelope carrying neither a template nor a body.
    [<Literal>]
    let NoContentReason = "whatsapp_no_content"

    /// An envelope carrying both a template and a body — a template send
    /// cannot carry free text, and silently dropping either would send
    /// something other than what the caller wrote.
    [<Literal>]
    let TemplateAndBodyReason = "whatsapp_template_and_body"

    /// The customer-care window a recipient's last inbound message opens.
    let CustomerCareWindow: TimeSpan = TimeSpan.FromHours 24.0

    /// `true` when a free-form message may reach a recipient whose last
    /// inbound message arrived at `lastInboundUtc`. The boundary is
    /// inclusive: exactly 24 hours after the inbound is still inside.
    let isInsideWindow (now: DateTime) (lastInboundUtc: DateTime option) : bool =
        match lastInboundUtc with
        | None -> false
        | Some inbound -> now - inbound <= CustomerCareWindow

    /// The template rule on its own: `None` when the send is admissible,
    /// `Some reason` when it is refused. A `TemplateLanguage` of `None`
    /// leaves the language to the sink and is not checked.
    let checkTemplate (envelope: WhatsAppEnvelope) (descriptor: WhatsAppTemplateDescriptor option) : string option =
        match descriptor with
        | None -> Some TemplateUnknownReason
        | Some d ->
            let parameters =
                if isNull (box envelope.TemplateParameters) then
                    []
                else
                    envelope.TemplateParameters

            match envelope.TemplateLanguage with
            | Some language when not (WhatsAppTemplateDescriptor.supportsLanguage language d) ->
                Some TemplateLanguageReason
            | _ when List.length parameters <> WhatsAppTemplateDescriptor.totalParameterCount d ->
                Some TemplateArityReason
            | _ -> None

/// `INotificationChannel` decorator enforcing the WhatsApp Business send
/// rules on `TransactionalWhatsApp`. Every other kind passes through
/// untouched.
type WhatsAppSendPolicyFilter
    (
        inner: INotificationChannel,
        templates: IWhatsAppTemplateRegistry,
        contacts: IExternalContactStore option,
        auditLog: IAuditLog option,
        logger: ILogger,
        clock: unit -> DateTime
    ) =

    let kindWire =
        NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.WhatsApp

    /// Emit one refusal row for `refused`. An empty list, or no audit
    /// log composed, writes nothing — the refusal itself still stands.
    let auditRefusal
        (scopeId: string)
        (reason: string)
        (refused: RecipientId list)
        (correlationId: string option)
        : Async<unit> =
        async {
            logger.Debug
                $"[WhatsAppSendPolicy] refused scope=%s{scopeId} count=%d{List.length refused} reason=%s{reason}"

            match auditLog, refused with
            | _, []
            | None, _ -> return ()
            | Some log, recipients ->
                do!
                    log.Record(
                        scopeId,
                        NotificationDeliveryRefused {
                            NotificationKind = kindWire
                            ScopeId = scopeId
                            Reason = reason
                            RecipientHashes =
                                recipients
                                |> List.map (RecipientId.toAuditString >> NotificationPreferenceFilter.hashRecipient)
                            ContactIds = RecipientId.contactIds recipients
                            CorrelationId = correlationId
                        }
                    )
        }

    /// Whether `recipient` is inside the customer-care window. A platform
    /// user has no inbound record the SDK can read, so reads as outside;
    /// an external contact is inside only on a readable `LastInboundUtc`
    /// no older than the window.
    let insideWindow (scopeId: string) (now: DateTime) (recipient: RecipientId) : Async<bool> = async {
        match recipient, contacts with
        | RecipientId.User _, _
        | RecipientId.External _, None -> return false
        | RecipientId.External contactId, Some store ->
            let! result = store.Get(scopeId, contactId)

            match result with
            | Ok contact -> return WhatsAppSendPolicy.isInsideWindow now contact.LastInboundUtc
            | Error ExternalContactError.NotFound -> return false
            | Error error ->
                logger.Warn
                    $"[WhatsAppSendPolicy] contact read failed scope=%s{scopeId} contact=%s{contactId}: %s{ExternalContactError.describe error} — refusing"

                return false
    }

    let lookupTemplate (name: string) : Async<WhatsAppTemplateDescriptor option> = async {
        try
            return! templates.GetTemplate name
        with ex ->
            logger.Warn
                $"[WhatsAppSendPolicy] template registry failed template=%s{name}: %s{ex.GetType().Name}: %s{ex.Message} — refusing"

            return None
    }

    let publishWhatsApp (scopeId: string) (notification: Notification) (envelope: WhatsAppEnvelope) = async {
        let recipients =
            if isNull (box envelope.Recipients) then
                []
            else
                envelope.Recipients

        match envelope.TemplateName, envelope.Body with
        | None, None -> do! auditRefusal scopeId WhatsAppSendPolicy.NoContentReason recipients envelope.CorrelationId
        | Some _, Some _ ->
            do! auditRefusal scopeId WhatsAppSendPolicy.TemplateAndBodyReason recipients envelope.CorrelationId
        | Some name, None ->
            let! descriptor = lookupTemplate name

            match WhatsAppSendPolicy.checkTemplate envelope descriptor with
            | Some reason -> do! auditRefusal scopeId reason recipients envelope.CorrelationId
            | None -> do! inner.Publish(scopeId, notification)
        | None, Some _ ->
            let now = clock ()

            let! verdicts =
                recipients
                |> List.map (fun recipient -> async {
                    let! inside = insideWindow scopeId now recipient
                    return recipient, inside
                })
                |> Async.Parallel

            let kept = verdicts |> Array.filter snd |> Array.map fst |> Array.toList
            let refused = verdicts |> Array.filter (snd >> not) |> Array.map fst |> Array.toList

            do! auditRefusal scopeId WhatsAppSendPolicy.OutsideWindowReason refused envelope.CorrelationId

            // An envelope with no recipient left is not published: an
            // empty one would reach the sink as a routine "no
            // addressable recipient" skip and hide the refusal. An
            // envelope that was empty to begin with passes through
            // unchanged, as every other kind's does.
            if List.isEmpty recipients then
                do! inner.Publish(scopeId, notification)
            elif not (List.isEmpty kept) then
                do! inner.Publish(scopeId, NotificationPreferenceFilter.narrowTo kept notification)
    }

    /// Production shape: the system clock.
    new
        (
            inner: INotificationChannel,
            templates: IWhatsAppTemplateRegistry,
            contacts: IExternalContactStore option,
            auditLog: IAuditLog option,
            logger: ILogger
        ) =
        WhatsAppSendPolicyFilter(inner, templates, contacts, auditLog, logger, fun () -> DateTime.UtcNow)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) =
            match notification with
            | TransactionalWhatsApp envelope -> publishWhatsApp scopeId notification envelope
            | _ -> inner.Publish(scopeId, notification)

        member _.Subscribe(scopeId, handler) = inner.Subscribe(scopeId, handler)

        member _.Unsubscribe(subscriptionId) = inner.Unsubscribe(subscriptionId)