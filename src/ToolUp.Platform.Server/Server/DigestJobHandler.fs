// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text

// ─── Phase 441 — digest job ──────────────────────────────────────────
//
// `DigestJobHandler` drains the pending queues the send-path filter
// fills. Each tick, for every scope and user with held items:
//
//   1. **Release expired quiet-hours holds** — each item whose
//      `DeferredUntil` has passed is published on its own, on the
//      channel BELOW the filter, so it is not re-deferred.
//   2. **Send due digest buckets** — items held `ForDigest f` are
//      grouped by frequency; a bucket is due when `f`'s period has
//      elapsed since the user's last digest of that frequency (or it
//      has never been sent). One templated send per channel family
//      folds the bucket's items (oldest first, capped by
//      `MaxItemsPerDigest`) into a single email / SMS / push.
//
// **Idempotent on re-run, by construction.** The job holds no state
// between ticks (GP 12 rule 4): everything it needs is re-read from
// the store, items are removed as soon as they are sent, and the
// watermark is written after the removal. A crash between the publish
// and the removal re-sends the same bucket on the next tick — with the
// SAME `CorrelationId`, because the digest marker is a hash of the
// item ids it contains rather than a fresh guid, so a vendor that
// honours correlation ids (the Phase 6f sinks forward it) deduplicates
// the retry. That is the at-least-once guarantee every other
// `_platform.*` job offers, tightened to exactly-once where the sink
// can honour the key.
//
// **The digest marker.** The dispatcher records `NotificationSent`
// with the envelope's `CorrelationId`, so a digest send audits as
// `CorrelationId = Some "digest:daily:<16 hex>"` — greppable, and the
// per-item `digest_queued` skip audits recorded when the items were
// held are the other half of the trail.

/// Pure rendering of a digest bucket into one send per channel. Public
/// so a deployment can read the exact shape and the tests can pin it.
module DigestRenderer =
    let private describe (item: PendingNotification) : string =
        let queued = item.QueuedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm") + "Z"

        match item.Notification with
        | TransactionalEmail e ->
            match e.Content with
            | InlineEmail(subject, bodyText, _) -> $"[{item.Category} · {queued}] {subject}\n{bodyText}"
            | TemplatedEmail(templateId, _) -> $"[{item.Category} · {queued}] (templated message: {templateId})"
        | TransactionalSms s -> $"[{item.Category} · {queued}] {s.Body}"
        | MobilePush p -> $"[{item.Category} · {queued}] {p.Title}\n{p.Body}"
        | other -> $"[{item.Category} · {queued}] {other}"

    /// The digest body: one block per item, oldest first, separated by
    /// a rule.
    let body (items: PendingNotification list) : string =
        items |> List.map describe |> String.concat "\n\n---\n\n"

    /// `{count}` substituted into the configured subject template.
    let subject (template: string) (count: int) : string =
        template.Replace("{count}", string count)

    /// The stable digest marker for a bucket: `digest:<frequency>:<16 hex>`
    /// over the sorted item ids, so the same bucket always carries the
    /// same correlation id.
    let marker (frequency: DigestFrequency) (items: PendingNotification list) : string =
        let joined =
            items |> List.map (fun i -> i.Id.ToString "N") |> List.sort |> String.concat ","

        use sha = SHA256.Create()
        let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes joined)
        let hex = bytes |> Seq.take 8 |> Seq.map (sprintf "%02x") |> String.concat ""
        $"digest:{DigestFrequency.toWireString frequency}:{hex}"

    /// One transactional notification folding `items` (all of one
    /// channel family) for `userId`, tagged with `marker`.
    let render
        (settings: NotificationPreferenceSettings)
        (channel: PreferenceChannel)
        (userId: string)
        (marker: string)
        (items: PendingNotification list)
        : Notification =
        let count = List.length items

        match channel with
        | PreferenceChannel.Email ->
            TransactionalEmail {
                RecipientUserIds = [ userId ]
                Content = InlineEmail(subject settings.DigestEmailSubject count, body items, None)
                CorrelationId = Some marker
            }
        | PreferenceChannel.Sms ->
            TransactionalSms {
                RecipientUserIds = [ userId ]
                Body = body items
                CorrelationId = Some marker
            }
        | PreferenceChannel.Push ->
            MobilePush {
                RecipientUserIds = [ userId ]
                Title = subject settings.DigestEmailSubject count
                Body = body items
                DeepLink = None
                CorrelationId = Some marker
            }

/// `IJobHandler` draining the preference substrate's pending queues:
/// releases expired quiet-hours holds and sends due digest buckets.
/// `channel` MUST be the channel beneath `NotificationPreferenceFilter`
/// (the composition passes the dispatcher-facing one), or a released
/// item would be deferred again.
type DigestJobHandler
    (
        store: INotificationPreferenceStore,
        channel: INotificationChannel,
        settings: NotificationPreferenceSettings,
        logger: ILogger
    ) =

    /// Publish an expired hold on its own and drop it from the queue.
    let release (scopeId: string) (item: PendingNotification) = async {
        do! channel.Publish(scopeId, item.Notification)

        match! store.Remove(scopeId, item.UserId, [ item.Id ]) with
        | Ok() -> ()
        | Error e -> logger.Warn $"[NotificationDigest] released {item.Id} but could not remove it: {e}"
    }

    /// Send one due bucket: one publish per channel family, then remove
    /// the items and advance the watermark.
    let sendBucket
        (scopeId: string)
        (userId: string)
        (nowUtc: DateTime)
        (frequency: DigestFrequency)
        (items: PendingNotification list)
        =
        async {
            let batch = items |> List.truncate (max 1 settings.MaxItemsPerDigest)
            let marker = DigestRenderer.marker frequency batch

            for channelFamily, group in batch |> List.groupBy _.Channel do
                do! channel.Publish(scopeId, DigestRenderer.render settings channelFamily userId marker group)

            match! store.Remove(scopeId, userId, batch |> List.map _.Id) with
            | Ok() -> ()
            | Error e ->
                logger.Warn $"[NotificationDigest] sent {marker} to {userId} but could not remove its items: {e}"

            match! store.SetDigestWatermark(scopeId, userId, frequency, nowUtc) with
            | Ok() -> ()
            | Error e ->
                logger.Warn $"[NotificationDigest] sent {marker} to {userId} but could not record the watermark: {e}"

            logger.Info $"[NotificationDigest] sent {marker} ({List.length batch} items) to {userId} in {scopeId}"
        }

    let drainUser (scopeId: string) (userId: string) (nowUtc: DateTime) = async {
        let! items = store.ListPending(scopeId, userId)

        for item in items do
            match item.Disposition with
            | PendingDisposition.DeferredUntil until when until <= nowUtc -> do! release scopeId item
            | _ -> ()

        let! watermarks = store.GetDigestWatermarks(scopeId, userId)

        let buckets =
            items
            |> List.choose (fun item ->
                match item.Disposition with
                | PendingDisposition.ForDigest frequency -> Some(frequency, item)
                | PendingDisposition.DeferredUntil _ -> None)
            |> List.groupBy fst
            |> List.map (fun (frequency, pairs) -> frequency, pairs |> List.map snd)

        for frequency, bucket in buckets do
            let due =
                match Map.tryFind (DigestFrequency.toWireString frequency) watermarks with
                | None -> true
                | Some lastSent -> nowUtc - lastSent >= DigestFrequency.period frequency

            if due then
                do! sendBucket scopeId userId nowUtc frequency bucket
    }

    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            let nowUtc = DateTime.SpecifyKind(ctx.RunningAt, DateTimeKind.Utc)
            let failures = ResizeArray<string>()

            let! scopes = store.ListScopesWithPending()

            for scopeId in scopes do
                let! users = store.ListPendingUsers scopeId

                for userId in users do
                    try
                        do! drainUser scopeId userId nowUtc
                    with ex ->
                        // One user's failure must not starve the rest of
                        // the tick; the tick as a whole reports transient
                        // so the scheduler retries.
                        logger.Warn $"[NotificationDigest] {scopeId}/{userId}: {ex.Message}"
                        failures.Add $"{scopeId}/{userId}: {ex.Message}"

            if failures.Count = 0 then
                return JobResult.Success
            else
                return JobResult.TransientFailure(String.Join("; ", failures))
        }

/// Registration surface for the digest job.
module NotificationDigest =
    /// The reserved handler name, under the `_platform.notifications`
    /// audit source module.
    [<Literal>]
    let HandlerName = "_platform.notifications.digest"

    /// Construct the handler over the composed store and the channel
    /// beneath the filter.
    let create
        (store: INotificationPreferenceStore)
        (channel: INotificationChannel)
        (settings: NotificationPreferenceSettings)
        (logger: ILogger)
        : IJobHandler =
        DigestJobHandler(store, channel, settings, logger) :> IJobHandler

    /// The recurring declaration on `settings.DigestCron`, scoped to
    /// `_platform` (the declaration default) because the job discovers
    /// scopes itself.
    let declaration
        (store: INotificationPreferenceStore)
        (channel: INotificationChannel)
        (settings: NotificationPreferenceSettings)
        (logger: ILogger)
        : ScheduledJobDeclaration =
        ScheduledJobDeclaration.create
            HandlerName
            (create store channel settings logger)
            (Trigger.CronTrigger settings.DigestCron)