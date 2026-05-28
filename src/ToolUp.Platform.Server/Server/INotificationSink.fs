namespace ToolUp.Platform

open System

/// Outcome of a single `INotificationSink.Send` invocation.
///
/// `Skipped` is for "the system correctly decided not to deliver" —
/// team prefs disabled, no resolvable address, vendor-side dedup
/// rejected the `CorrelationId`. No audit event is emitted on
/// `Skipped`; the dispatcher logs at `Debug` only. This keeps the
/// audit trail focused on actual delivery attempts and outcomes,
/// avoiding noise from configuration-driven no-ops.
///
/// `TransientFailure` and `PermanentFailure` differ in retry semantics:
/// the dispatcher backs off and retries `TransientFailure` per
/// `TransactionalRetryPolicy`; `PermanentFailure` skips the rest of
/// the retry budget and emits `NotificationDeliveryFailed`
/// immediately. Sinks classify on the vendor's response — 5xx and
/// network errors are `TransientFailure`; 4xx and contract violations
/// (malformed address, vendor-side reject for non-rate-limit reason,
/// invalid template id) are `PermanentFailure`.
///
/// `Delivered.vendorMessageId` is the upstream message id when the
/// vendor returned one (SendGrid `X-Message-Id`, SMTP queued response
/// `Message-ID`, Twilio `MessageSid`). Audit emission stamps it into
/// `NotificationSentPayload.VendorMessageId` so deployments can
/// cross-reference vendor logs.
///
/// `[<RequireQualifiedAccess>]` because `JobResult` (Phase 9b) shares
/// `TransientFailure` / `PermanentFailure` case names; without
/// qualification, type inference resolves to whichever DU was most
/// recently declared and breaks `JobScheduler.runDelivery`.
[<RequireQualifiedAccess>]
type SinkResult =
    | Delivered of vendorMessageId: string option
    | Skipped of reason: string
    | TransientFailure of error: string
    | PermanentFailure of error: string

/// Retry policy shared by every transactional sink. Mirrors
/// `WebhookRetryPolicy` so deployments tuning one knob don't have to
/// learn a second vocabulary. Phase 9c rule 3 — retry expressed as
/// data, not callbacks. Scheduling is exponential: attempt N waits
/// `min(InitialBackoff * 2^(N-1), MaxBackoff)`.
type TransactionalRetryPolicy = {
    /// Total attempts including the first. Sinks that exhaust the
    /// budget on `TransientFailure` end up in
    /// `NotificationDeliveryFailed` audit. Default: 3.
    MaxAttempts: int
    /// Backoff before the second attempt. Default: 30 seconds.
    InitialBackoff: TimeSpan
    /// Cap on exponential growth so a long-running sink doesn't park
    /// for hours waiting on a 30-day-old transient. Default: 10
    /// minutes.
    MaxBackoff: TimeSpan
}

module TransactionalRetryPolicy =
    /// Default retry policy. Three attempts, 30-second initial
    /// backoff, 10-minute cap. Conservative on first-attempt latency
    /// (so a `JobCompleted` email lands within seconds when the
    /// vendor is healthy) and on retry budget (so a permanently dead
    /// vendor doesn't burn the dispatcher pool indefinitely).
    let defaults: TransactionalRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromSeconds 30.0
        MaxBackoff = TimeSpan.FromMinutes 10.0
    }

    /// Compute the backoff before attempt `attemptNumber` (1-indexed).
    /// `attemptNumber = 1` returns `Zero` — the first attempt fires
    /// immediately. Subsequent attempts wait
    /// `min(InitialBackoff * 2^(N-2), MaxBackoff)`.
    let delayFor (policy: TransactionalRetryPolicy) (attemptNumber: int) : TimeSpan =
        if attemptNumber <= 1 then
            TimeSpan.Zero
        else
            let exponent = attemptNumber - 2
            let scaled = policy.InitialBackoff.TotalMilliseconds * Math.Pow(2.0, float exponent)
            let capped = min scaled policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped

/// Out-of-band transactional notification adapter. One implementation
/// per vendor (SMTP, SendGrid, Twilio, WebPush) injected via the
/// `src/NotificationChannels/<Family>/<Vendor>/` companion pattern.
///
/// **Lifecycle.** Sinks are constructed once at startup by the
/// composition root and registered via
/// `ServerApp.withTransactionalSink`. The dispatcher
/// (`TransactionalDispatcher`) subscribes to `INotificationChannel`
/// for every scope at `StartAsync` and routes envelopes whose
/// `NotificationKind.ofNotification` value matches the sink's `Kind`.
///
/// **Identity.** `Kind` is a `NotificationKind.SinkKind` DU value —
/// `SinkKind.Email`, `SinkKind.Sms`, or `SinkKind.Push <variant>`
/// where `<variant>` is a `PushVariant` (`WebPush`, `Fcm`, `Apns`,
/// or `Other name`). At most one sink per wire-format key in a
/// deployment — duplicates are caught at registration time in
/// `compose` by grouping on `SinkKind.toWireString`, which keys
/// `Push.WebPush` distinct from `Push.Fcm` so two push companions
/// register concurrently. `Provider` is a vendor label (`"Smtp"`,
/// `"SendGrid"`, `"Twilio"`, `"WebPush"`) surfaced in the audit trail
/// so deployments can tell which adapter actually delivered. Phase 9c
/// rule 1 — identity by value.
///
/// **Statelessness.** `Send` derives its result entirely from
/// `(scopeId, envelope)` plus DI-resolved infrastructure
/// (`IAuthProvider`, `INotificationAddressBook`, `ISecretStore`,
/// `IConfigStore`). No in-memory state between calls — Phase 9c
/// rule 4. Implementations may keep vendor-SDK clients
/// (`HttpClient`, `SmtpClient`) for connection-pooling, but those are
/// pure plumbing, not state.
///
/// **Audit responsibility.** Sinks do NOT emit audit events
/// themselves. The dispatcher reads the `SinkResult` and emits the
/// matching `AuditEvent` (Phase 6f `NotificationSent` /
/// `NotificationDeliveryFailed`). Centralising audit at the
/// dispatcher avoids per-sink bookkeeping drift and keeps sinks
/// minimal — the smallest viable sink is one method.
///
/// **Cross-shard ordering.** Not promised. Phase 9c rule 5 — a sink
/// receiving two `Send` calls in quick succession may dispatch them
/// in either order; vendors that need strict ordering use the
/// `CorrelationId` field for de-duplication, not arrival order.
///
/// **Precision.** Best-effort, vendor-queued. The lower bound on
/// delivery time is whatever the upstream vendor commits to —
/// typically minutes for transactional email, seconds for SMS, no
/// hard guarantee for push (depends on device wake-up). Caller code
/// must not assume sub-minute delivery. Phase 9c rule 6.
type INotificationSink =
    /// Stable kind discriminator — `NotificationKind.SinkKind.Email`,
    /// `NotificationKind.SinkKind.Sms`, or
    /// `NotificationKind.SinkKind.Push <variant>`. The dispatcher
    /// routes by `SinkKind.toWireString this.Kind` — a sink with
    /// `Kind = SinkKind.Email` consumes `TransactionalEmail`
    /// envelopes only.
    abstract Kind: NotificationKind.SinkKind

    /// Vendor label surfaced in audit and `/dev/inspect` diagnostics.
    /// Free-form but conventionally PascalCase (`"Smtp"`,
    /// `"SendGrid"`, `"Postmark"`, `"Twilio"`, `"WebPush"`).
    abstract Provider: string

    /// Dispatch one envelope under `scopeId`. The implementation owns
    /// vendor-SDK invocation, recipient resolution via
    /// `INotificationAddressBook`, and pref consultation via
    /// `IConfigStore` (Phase 5a). Returns one of the four
    /// `SinkResult` cases — see `SinkResult` for retry / audit
    /// implications.
    abstract Send: scopeId: string * envelope: NotificationEnvelope -> Async<SinkResult>