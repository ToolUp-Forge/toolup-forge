// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── ExternalContactConsentFilter (Phase 6f.A) ───────────────────────
//
// The OUTER gate on the transactional send path: an `External`
// recipient is dropped from the envelope — and the drop audited as a
// REFUSAL — unless the contact holds a live per-channel consent.
//
// **Why it is a channel decorator and not a check inside
// `TransactionalDispatcher`.** The phase file called for "an outer gate
// in the dispatcher that refuses before Phase 441's filter runs", and on
// this tree those are two different places: 441's
// `NotificationPreferenceFilter` is itself an `INotificationChannel`
// decorator composed OUTSIDE the dispatcher, so anything inside the
// dispatcher runs strictly AFTER it. Composing the consent gate as a
// further decorator around the preference filter is what actually
// produces the stated order:
//
//     ExternalContactConsentFilter      (consent — outer, this file)
//       -> NotificationPreferenceFilter (preference — inner, Phase 441)
//         -> DispatchingNotificationChannel
//           -> TransactionalDispatcher -> INotificationSink
//
// Phase 441's types, filter and tests are untouched, which was the other
// half of the operator's decision.
//
// **Why consent is outside preference rather than merged with it.** They
// are facts about different subjects. A preference is a USER's choice
// about notifications they already receive by virtue of having an
// account; consent is a legal fact about a person with no account, no
// preference record and no preference UI. Merged, a category-level mute
// would read as a withdrawal of consent and a withdrawal would read as a
// mute — and the one that matters legally would be the one that is
// easiest to lose.
//
// **A refusal is not a skip.** `NotificationSilentlySkipped` means the
// deployment chose not to send on that kind. `NotificationDeliveryRefused`
// means the deployment had no lawful basis to contact this person. The
// SDK keeps them apart because they call for different action from an
// operator: one is a setting, the other is a consent to go and obtain.
//
// **Fail closed.** Every failure path here — no store composed, a store
// read that errors, a contact that does not exist, an expired opt-in —
// resolves to REFUSED. That is the opposite of the fail-open rule the
// preference filter follows, deliberately: a preference lookup that
// fails should not stop a notification the user never objected to, while
// a consent lookup that fails cannot be read as consent.

/// Literals for the consent filter.
module ExternalContactConsent =
    /// `Reason` on a `NotificationDeliveryRefused` row for a recipient
    /// with no live consent on the channel. The one reason the SDK
    /// emits; the payload field is a string so a channel arm can name
    /// its own without a DU change reaching every consumer.
    [<Literal>]
    let NoOptInReason = "no_opt_in"

/// `INotificationChannel` decorator enforcing per-channel consent for
/// `RecipientId.External` recipients. Platform users pass through
/// untouched — they are governed by Phase 441's filter and by the
/// dispatcher's team-level kill switch, neither of which this decorator
/// duplicates.
type ExternalContactConsentFilter
    (
        inner: INotificationChannel,
        contacts: IExternalContactStore option,
        auditLog: IAuditLog option,
        logger: ILogger,
        clock: unit -> DateTime
    ) =

    /// Whether a stored consent channel satisfies the notification being
    /// published. Email and SMS match exactly; a push envelope is
    /// satisfied by a consent on ANY push variant, because the variant
    /// records which transport a device registered on and the recipient
    /// consented to being pushed, not to a vendor.
    let satisfies (notification: Notification) (channel: NotificationKind.SinkKind) : bool =
        match notification, channel with
        | TransactionalEmail _, NotificationKind.SinkKind.Email -> true
        | TransactionalSms _, NotificationKind.SinkKind.Sms -> true
        | MobilePush _, NotificationKind.SinkKind.Push _ -> true
        | _ -> false

    /// The sink-kind label the refusal row carries. A push envelope has
    /// no single variant at this point in the pipeline — the fan-out to
    /// registered push sinks happens below — so the row says `"Push"`.
    let kindLabel (notification: Notification) : string =
        match notification with
        | TransactionalEmail _ -> NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.Email
        | TransactionalSms _ -> NotificationKind.SinkKind.toWireString NotificationKind.SinkKind.Sms
        | MobilePush _ -> "Push"
        | other -> NotificationKind.ofNotification other

    /// `true` when `contactId` holds a live consent covering
    /// `notification`. Fail-closed at every step — see the preamble.
    let isConsented (scopeId: string) (notification: Notification) (contactId: string) : Async<bool> = async {
        match contacts with
        | None ->
            // No address book composed at all. There is no consent
            // record, so there is no consent.
            return false
        | Some store ->
            let! result = store.Get(scopeId, contactId)

            match result with
            | Error ExternalContactError.NotFound -> return false
            | Error error ->
                logger.Warn
                    $"[ExternalContactConsent] contact read failed scope=%s{scopeId} contact=%s{contactId}: %s{ExternalContactError.describe error} — refusing"

                return false
            | Ok contact ->
                let now = clock ()

                return
                    contact.OptIns
                    |> Map.toSeq
                    |> Seq.exists (fun (channel, record) ->
                        satisfies notification channel && OptInRecord.isLiveAt now record)
    }

    /// Emit the refusal row. `None` audit log degrades to no row rather
    /// than to a failure — the refusal itself still stands.
    let auditRefusal
        (scopeId: string)
        (notification: Notification)
        (refused: string list)
        (correlationId: string option)
        : Async<unit> =
        async {
            match auditLog, refused with
            | _, [] -> return ()
            | None, _ -> return ()
            | Some log, contactIds ->
                do!
                    log.Record(
                        scopeId,
                        NotificationDeliveryRefused {
                            NotificationKind = kindLabel notification
                            ScopeId = scopeId
                            Reason = ExternalContactConsent.NoOptInReason
                            RecipientHashes =
                                contactIds
                                |> List.map (fun id ->
                                    NotificationPreferenceFilter.hashRecipient (
                                        RecipientId.toAuditString (RecipientId.External id)
                                    ))
                            ContactIds = contactIds
                            CorrelationId = correlationId
                        }
                    )
        }

    /// Production shape: the system clock.
    new
        (
            inner: INotificationChannel,
            contacts: IExternalContactStore option,
            auditLog: IAuditLog option,
            logger: ILogger
        ) =
        ExternalContactConsentFilter(inner, contacts, auditLog, logger, fun () -> DateTime.UtcNow)

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async {
            if not (NotificationKind.isTransactional notification) then
                // SSE-bound kinds carry no recipient list to gate.
                do! inner.Publish(scopeId, notification)
            else
                let recipients, correlationId =
                    NotificationPreferenceFilter.recipientsOf notification

                let externals = RecipientId.contactIds recipients

                if List.isEmpty externals then
                    // The overwhelmingly common path: no external
                    // recipient, so not one store read and not one
                    // allocation beyond this check (GP 13).
                    do! inner.Publish(scopeId, notification)
                else
                    let! verdicts =
                        externals
                        |> List.map (fun contactId -> async {
                            let! consented = isConsented scopeId notification contactId
                            return contactId, consented
                        })
                        |> Async.Parallel

                    let refused = verdicts |> Array.filter (snd >> not) |> Array.map fst |> Array.toList

                    let permitted = verdicts |> Array.filter snd |> Array.map fst |> Set.ofArray

                    do! auditRefusal scopeId notification refused correlationId

                    if not (List.isEmpty refused) then
                        logger.Debug
                            $"[ExternalContactConsent] refused scope=%s{scopeId} kind=%s{kindLabel notification} count=%d{List.length refused} reason=%s{ExternalContactConsent.NoOptInReason}"

                    let kept =
                        recipients
                        |> List.filter (function
                            | RecipientId.User _ -> true
                            | RecipientId.External contactId -> permitted.Contains contactId)

                    // An envelope whose every recipient was refused is
                    // not published at all. Publishing an empty one
                    // would reach the sinks, which would skip it as
                    // "no addressable recipients" and hide a refusal
                    // behind a routine skip.
                    if not (List.isEmpty kept) then
                        do! inner.Publish(scopeId, NotificationPreferenceFilter.narrowTo kept notification)
        }

        member _.Subscribe(scopeId, handler) = inner.Subscribe(scopeId, handler)

        member _.Unsubscribe(subscriptionId) = inner.Unsubscribe(subscriptionId)