// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Threading

// ─── Phase 441 — send-path preference filter ─────────────────────────
//
// `NotificationPreferenceFilter` is an `INotificationChannel` decorator
// composed OUTSIDE the Phase 6f `DispatchingNotificationChannel`, so it
// sees every transactional publish before the dispatcher does and can
// narrow, hold, or drop per recipient:
//
//   * `Muted`      → the recipient is removed; audited `user_muted`.
//   * `Digest f`   → held in the recipient's pending queue for the
//                    digest job; audited `digest_queued`.
//   * `Immediate`  → delivered now, unless the recipient's quiet-hours
//                    window is open, in which case it is held with a
//                    release instant at the window's end; audited
//                    `quiet_hours_deferred`.
//
// The remaining recipients go to the inner channel on a copy of the
// notification narrowed to them. In-app / SSE kinds pass straight
// through (preferences govern outbound channels only — Phase 6a).
//
// **Where the category comes from.** Nothing on a transactional
// envelope names a category — the wire records predate this phase and
// widening them is a breaking change every consumer would pay. The
// publisher states it instead, as ambient dispatch-scoped context in
// the `JobProgressScope` idiom (GP 7):
//
//     NotificationCategoryScope.publish channel "reports.subscription" scopeId
//         (TransactionalEmail envelope)
//
// The filter reads the ambient value SYNCHRONOUSLY at `Publish` entry —
// before its own `async` block is built — so the read never depends on
// `AsyncLocal` flowing across a thread hop. An uncategorised publish
// (every pre-441 caller) passes through unchanged: with no category
// there is no preference to look up, and delivering is the prior
// behaviour (GP 11). A category no module declared is treated as
// suppressible, so a publisher cannot bypass the filter by tagging a
// name nobody registered; a declared `Suppressible = false` category
// bypasses everything.
//
// **Fail open.** A store read that errors, a zone id that does not
// resolve, or a hold that cannot be written each resolve to DELIVER NOW
// with a `Warn`. The alternative — dropping mail because a preference
// blob was unreadable — turns an infrastructure blip into lost
// password resets.

/// Ambient category for the transactional publish in flight. Pushed by
/// `publish` (or `push` for a scope covering several publishes) and read
/// by `NotificationPreferenceFilter` at `Publish` entry.
module NotificationCategoryScope =
    let private ambient = AsyncLocal<string>()

    /// The category in scope, or `None` outside any `push`.
    let current () : string option =
        match ambient.Value with
        | null -> None
        | value -> Some value

    /// Make `category` ambient until the returned scope is disposed,
    /// restoring the exact prior value (nested pushes pop LIFO).
    let push (category: string) : IDisposable =
        let prior = ambient.Value
        ambient.Value <- category

        { new IDisposable with
            member _.Dispose() = ambient.Value <- prior
        }

    /// Publish `notification` on `channel` as `category`. The one-line
    /// way for a module to tag a send; identical to `channel.Publish`
    /// on a deployment with `NoNotificationPreferences`.
    let publish
        (channel: INotificationChannel)
        (category: string)
        (scopeId: string)
        (notification: Notification)
        : Async<unit> =
        async {
            use _scope = push category
            do! channel.Publish(scopeId, notification)
        }

/// Server-side quiet-hours evaluation: the zone lookup and the UTC
/// release-instant arithmetic the Fable-safe `QuietHours` module leaves
/// to the server.
module QuietHoursClock =
    /// `TimeZoneInfo.FindSystemTimeZoneById`, `None` when the id is
    /// unknown on this host (IANA and Windows ids both resolve on .NET
    /// 10 where ICU is present).
    let systemZone (timeZoneId: string) : TimeZoneInfo option =
        try
            Some(TimeZoneInfo.FindSystemTimeZoneById timeZoneId)
        with _ ->
            None

    /// When `nowUtc` falls inside `quiet` in the user's zone, the UTC
    /// instant the window closes (the top of its end minute); otherwise
    /// `None`. An unresolvable zone yields `None` — the window is
    /// disabled rather than holding mail on a typo. Across a DST
    /// transition the release is computed on the local clock and may
    /// land an hour off; the digest job releases on the next tick
    /// after the instant, so the cost is one delayed tick, never a
    /// lost send.
    let deferralFor
        (resolveZone: string -> TimeZoneInfo option)
        (nowUtc: DateTime)
        (quiet: QuietHours)
        : DateTime option =
        match resolveZone quiet.TimeZoneId with
        | None -> None
        | Some zone ->
            let utc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)
            let local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone)
            let minute = local.Hour * 60 + local.Minute

            if not (QuietHours.contains quiet minute) then
                None
            else
                let minutes = QuietHours.minutesUntilEnd quiet minute
                let releaseLocal = local.AddSeconds(-float local.Second).AddMinutes(float minutes)

                let releaseUtc =
                    try
                        TimeZoneInfo.ConvertTimeToUtc(
                            DateTime.SpecifyKind(releaseLocal, DateTimeKind.Unspecified),
                            zone
                        )
                    with _ ->
                        // The release minute sits in a DST gap; fall back
                        // to elapsed-minute arithmetic on the UTC clock.
                        utc.AddSeconds(-float utc.Second).AddMinutes(float minutes)

                Some releaseUtc

/// Audit `Reason` tokens the filter records on
/// `NotificationSilentlySkipped`, and helpers over transactional
/// notifications shared with the digest job.
module NotificationPreferenceFilter =
    /// Reason on a `NotificationSilentlySkipped` audit for a muted recipient.
    [<Literal>]
    let MutedReason = "user_muted"

    /// Reason on a `NotificationSilentlySkipped` audit for a recipient
    /// whose copy was held for a digest.
    [<Literal>]
    let DigestQueuedReason = "digest_queued"

    /// Reason on a `NotificationSilentlySkipped` audit for a recipient
    /// whose copy was held by quiet hours.
    [<Literal>]
    let QuietHoursDeferredReason = "quiet_hours_deferred"

    /// The recipients and correlation id of a transactional
    /// notification; `[], None` for any other kind. Widened to
    /// `RecipientId` by Phase 6f.A; this filter reads only the `User`
    /// arm (see `platformUserIdsOf`).
    let recipientsOf (notification: Notification) : RecipientId list * string option =
        match notification with
        | TransactionalEmail e -> e.Recipients, e.CorrelationId
        | TransactionalSms s -> s.Recipients, s.CorrelationId
        | MobilePush p -> p.Recipients, p.CorrelationId
        | TransactionalWhatsApp w -> w.Recipients, w.CorrelationId
        | _ -> [], None

    /// The recipients this filter has anything to say about: platform
    /// users, who have an account and therefore a preference record.
    ///
    /// **An external contact is deliberately out of scope here.** It has
    /// no account, no preference record and no preference UI, so there
    /// is nothing for a per-user filter to read and nothing a user could
    /// have chosen. Its send/do-not-send question is a consent question,
    /// answered by the OUTER `ExternalContactConsentFilter` before this
    /// one runs. Passing an external recipient through `verdictFor`
    /// would read an empty record and return `Keep` - the same answer -
    /// at the cost of a store round-trip per recipient and a
    /// `PendingNotification` keyed on a user id that does not exist.
    let platformUserIdsOf (recipients: RecipientId list) : string list = RecipientId.userIds recipients

    /// The external recipients, carried through every narrowing
    /// untouched. They were admitted by the consent gate; this filter
    /// neither widens nor narrows that decision.
    let externalRecipientsOf (recipients: RecipientId list) : RecipientId list =
        recipients
        |> List.filter (function
            | RecipientId.External _ -> true
            | RecipientId.User _ -> false)

    /// A copy of a transactional notification addressed to `recipients`
    /// only. Any other kind is returned unchanged.
    let narrowTo (recipients: RecipientId list) (notification: Notification) : Notification =
        match notification with
        | TransactionalEmail e -> TransactionalEmail { e with Recipients = recipients }
        | TransactionalSms s -> TransactionalSms { s with Recipients = recipients }
        | MobilePush p -> MobilePush { p with Recipients = recipients }
        | TransactionalWhatsApp w -> TransactionalWhatsApp { w with Recipients = recipients }
        | other -> other

    /// `SHA256(RecipientId.toAuditString)[..8]` — the first 4 bytes, as
    /// 8 lowercase hex chars, of the SHA-256 of the UTF-8 AUDIT string:
    /// the bare user id for a platform user, `"external:{contactId}"`
    /// for an external contact (NOT the `"user:"`-prefixed wire string).
    /// Callers pass the audit string — this filter's own platform-user
    /// ids already are one; the consent and WhatsApp filters map
    /// `RecipientId.toAuditString` first. The same PII-free correlation
    /// token the Phase 6f dispatcher records on its own skip audits, so
    /// one recipient reads as one hash across every emitter.
    let hashRecipient (userId: string) : string =
        if String.IsNullOrEmpty userId then
            ""
        else
            use sha = SHA256.Create()
            let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes userId)
            bytes |> Seq.take 4 |> Seq.map (sprintf "%02x") |> String.concat ""

/// Per-recipient outcome of the preference lookup.
[<RequireQualifiedAccess>]
type private RecipientVerdict =
    | Keep
    | Mute
    | Digest of DigestFrequency
    | Defer of DateTime

/// `INotificationChannel` decorator applying per-user notification
/// preferences to categorised transactional publishes. See the file
/// preamble for the decision table and the fail-open rule.
type NotificationPreferenceFilter
    (
        inner: INotificationChannel,
        store: INotificationPreferenceStore,
        categories: NotificationCategory list,
        auditLog: IAuditLog option,
        logger: ILogger,
        clock: unit -> DateTime,
        resolveZone: string -> TimeZoneInfo option
    ) =
    let categoryIndex = categories |> List.map (fun c -> c.Id, c) |> Map.ofList


    let audit
        (scopeId: string)
        (channel: PreferenceChannel)
        (reason: string)
        (correlationId: string option)
        (userIds: string list)
        =
        async {
            match auditLog, userIds with
            | Some log, _ :: _ ->
                let payload: NotificationSilentlySkippedPayload = {
                    NotificationKind = PreferenceChannel.toWireString channel
                    ScopeId = scopeId
                    Reason = reason
                    RecipientHashes = userIds |> List.map NotificationPreferenceFilter.hashRecipient
                    CorrelationId = correlationId
                }

                do! log.Record(scopeId, NotificationSilentlySkipped payload)
            | _ -> ()
        }

    let verdictFor
        (scopeId: string)
        (category: string)
        (channel: PreferenceChannel)
        (nowUtc: DateTime)
        (userId: string)
        =
        async {
            let! prefs = store.GetPreferences(scopeId, userId)

            let prefs =
                match prefs with
                | Ok p -> p
                | Error e ->
                    logger.Warn $"[NotificationPreferences] could not read preferences for {userId}; delivering: {e}"
                    UserNotificationPreferences.empty

            return
                match UserNotificationPreferences.resolve prefs category channel with
                | Muted -> RecipientVerdict.Mute
                | Digest frequency -> RecipientVerdict.Digest frequency
                | Immediate ->
                    match prefs.QuietHours with
                    | None -> RecipientVerdict.Keep
                    | Some quiet ->
                        match QuietHoursClock.deferralFor resolveZone nowUtc quiet with
                        | Some until -> RecipientVerdict.Defer until
                        | None -> RecipientVerdict.Keep
        }

    /// Hold one recipient's copy; on a store failure deliver it now.
    let hold
        (scopeId: string)
        (category: string)
        (channel: PreferenceChannel)
        (nowUtc: DateTime)
        (notification: Notification)
        (userId: string)
        (disposition: PendingDisposition)
        =
        async {
            let item: PendingNotification = {
                Id = Guid.NewGuid()
                UserId = userId
                Category = category
                Channel = channel
                // A held copy is addressed to ONE platform user — the
                // one whose preference held it. Any external recipient
                // on the original envelope stays on the copy the filter
                // publishes now, never on the held one, or a digest
                // release would re-send to a contact that already
                // received it.
                Notification = NotificationPreferenceFilter.narrowTo [ RecipientId.User userId ] notification
                QueuedAt = nowUtc
                Disposition = disposition
            }

            match! store.Enqueue(scopeId, item) with
            | Ok() -> return true
            | Error e ->
                logger.Warn $"[NotificationPreferences] could not hold notification for {userId}; delivering: {e}"
                return false
        }

    let filter (scopeId: string) (category: string) (channel: PreferenceChannel) (notification: Notification) = async {
        let allRecipients, correlationId =
            NotificationPreferenceFilter.recipientsOf notification

        // Only platform users have a preference record. Externals ride
        // through untouched — the consent gate outside this filter has
        // already decided about them.
        let recipients = NotificationPreferenceFilter.platformUserIdsOf allRecipients

        let externals = NotificationPreferenceFilter.externalRecipientsOf allRecipients

        let nowUtc = clock ()
        let kept = ResizeArray<string>()
        let muted = ResizeArray<string>()
        let digested = ResizeArray<string>()
        let deferred = ResizeArray<string>()

        for userId in recipients do
            match! verdictFor scopeId category channel nowUtc userId with
            | RecipientVerdict.Keep -> kept.Add userId
            | RecipientVerdict.Mute -> muted.Add userId
            | RecipientVerdict.Digest frequency ->
                match!
                    hold scopeId category channel nowUtc notification userId (PendingDisposition.ForDigest frequency)
                with
                | true -> digested.Add userId
                | false -> kept.Add userId
            | RecipientVerdict.Defer until ->
                match!
                    hold scopeId category channel nowUtc notification userId (PendingDisposition.DeferredUntil until)
                with
                | true -> deferred.Add userId
                | false -> kept.Add userId

        do! audit scopeId channel NotificationPreferenceFilter.MutedReason correlationId (List.ofSeq muted)
        do! audit scopeId channel NotificationPreferenceFilter.DigestQueuedReason correlationId (List.ofSeq digested)

        do!
            audit
                scopeId
                channel
                NotificationPreferenceFilter.QuietHoursDeferredReason
                correlationId
                (List.ofSeq deferred)

        let keptRecipients = (kept |> Seq.map RecipientId.User |> List.ofSeq) @ externals

        if not (List.isEmpty keptRecipients) then
            do! inner.Publish(scopeId, NotificationPreferenceFilter.narrowTo keptRecipients notification)
    }

    /// Production shape: the system clock and the host's zone table.
    new
        (
            inner: INotificationChannel,
            store: INotificationPreferenceStore,
            categories: NotificationCategory list,
            auditLog: IAuditLog option,
            logger: ILogger
        ) =
        NotificationPreferenceFilter(
            inner,
            store,
            categories,
            auditLog,
            logger,
            (fun () -> DateTime.UtcNow),
            QuietHoursClock.systemZone
        )

    interface INotificationChannel with
        member _.Publish(scopeId, notification) =
            // Read the ambient category HERE, synchronously, before the
            // workflow below exists — see the preamble.
            let category = NotificationCategoryScope.current ()

            match PreferenceChannel.ofNotification notification, category with
            | None, _ -> inner.Publish(scopeId, notification)
            | Some _, None ->
                logger.Debug "[NotificationPreferences] uncategorised transactional publish passed through"
                inner.Publish(scopeId, notification)
            | Some channel, Some categoryId ->
                match Map.tryFind categoryId categoryIndex with
                | Some declared when not declared.Suppressible -> inner.Publish(scopeId, notification)
                | Some _ -> filter scopeId categoryId channel notification
                | None ->
                    logger.Debug
                        $"[NotificationPreferences] category '{categoryId}' is not declared by any module; treating as suppressible"

                    filter scopeId categoryId channel notification

        member _.Subscribe(scopeId, handler) = inner.Subscribe(scopeId, handler)
        member _.Unsubscribe subscriptionId = inner.Unsubscribe subscriptionId