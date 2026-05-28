// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Severity of a `SystemMessage` notification. Drives default toast
/// styling and auto-dismiss timing in the SDK's `ToastCentre`: Info
/// dismisses quickly, Warning/Error stay until acknowledged. Callers
/// that want different UI behaviour subscribe to the notification
/// stream directly and render their own.
[<RequireQualifiedAccess>]
type SystemMessageLevel =
    | Info
    | Warning
    | Error

/// Which kind of membership change a `MembershipChanged` notification
/// describes. Subscribers care: cache invalidation reacts only to
/// `Removed` and `ActiveTeamSet` (they change *which* team is active
/// for a user); `Added` and `RoleChanged` are informational and
/// inform UI but don't invalidate scope caches.
[<RequireQualifiedAccess>]
type MembershipChangeKind =
    | Added
    | Removed
    | RoleChanged
    | ActiveTeamSet

/// Payload of a `MembershipChanged` notification — published by
/// `TeamStore` after every successful membership write, consumed by
/// every cache that depends on team membership (`TeamScopeResolver`
/// server-side, the client shell's `TeamSwitched` reset path).
///
/// Identity is by value (string ids, not framework handles) per
/// portability rule 1. `PublishedAt` is informational — not a
/// scheduling primitive, no precision contract.
type MembershipChangedPayload = {
    TeamId: string
    AffectedUserId: string
    ChangeKind: MembershipChangeKind
    PublishedAt: DateTime
}

/// Vendor-neutral email recipient. `Address` is RFC 5321 form
/// (server validates before queueing). `DisplayName`, when present,
/// is the human label sinks include in the rendered `To:` header
/// (e.g. SMTP, SendGrid template variables).
///
/// Resolved server-side from `userId` via `INotificationAddressBook`
/// at sink dispatch time — `EmailEnvelope` carries `RecipientUserIds`
/// only, never the address itself, so PII never crosses the wire of
/// a cross-process notification channel (team isolation;
/// cloud-neutral surface).
type EmailAddress = {
    Address: string
    DisplayName: string option
}

/// Vendor-neutral phone number in E.164 format (`+`-prefixed,
/// digits-only). Server validates the shape before resolution
/// returns; sinks may further validate per their vendor's rules.
type PhoneNumber = { E164: string }

/// Vendor-neutral push registration. `Platform` is a free-form
/// discriminator (`"WebPush"`, future `"iOS"`, `"Android"`, `"FCM"`)
/// that lets sinks ignore tokens for platforms they can't deliver
/// to. The token is opaque — typically the W3C Push API endpoint URL
/// for `WebPush`, an APNs device token for `iOS`, etc.
type PushToken = { Token: string; Platform: string }

/// Persisted contact record consumed by the SDK-default
/// `BlobBackedNotificationAddressBook`. Lives in the shared
/// layer so future Fable-side admin UIs can read / write the same
/// shape without re-deriving the wire format.
///
/// `Email`, `Phone`, and `PushTokens` reuse the vendor-neutral types
/// the notification envelopes already use, so a blob-backed lookup
/// can return them directly without a translation step.
type UserContact = {
    /// Identity of the user this record describes. Duplicated as the
    /// blob filename for human readability, but the canonical lookup
    /// key is the path.
    UserId: string
    Email: EmailAddress option
    Phone: PhoneNumber option
    PushTokens: PushToken list
}

module UserContact =
    /// Empty contact record — no email, no phone, no tokens. Returned
    /// by the blob-backed implementation when no contact JSON exists
    /// for the queried `(userId, scopeId)`.
    let empty (userId: string) : UserContact = {
        UserId = userId
        Email = None
        Phone = None
        PushTokens = []
    }

/// Body of a transactional email. Either an inline triplet (subject,
/// plain-text body, optional HTML alternate) or a reference to a
/// vendor-side template plus per-recipient variables. Sinks that
/// don't support templates surface `PermanentFailure` on
/// `TemplatedEmail` — the `INotificationSinkContract` test pack
/// asserts this so deployments switching adapters fail fast.
type EmailContent =
    | InlineEmail of subject: string * bodyText: string * bodyHtml: string option
    | TemplatedEmail of templateId: string * variables: Map<string, string>

/// Payload of a `TransactionalEmail` notification. `RecipientUserIds`
/// resolve to `EmailAddress`es server-side via `INotificationAddressBook`
/// — recipients with no resolvable address are silently dropped (no
/// audit event), the remaining list is delivered. `CorrelationId`
/// forwards to vendors that support idempotent send (SendGrid
/// `X-Message-Id`, SMTP `Message-ID`) so retries don't double-send.
type EmailEnvelope = {
    RecipientUserIds: string list
    Content: EmailContent
    CorrelationId: string option
}

/// Payload of a `TransactionalSms` notification. SMS is always inline
/// — vendors mostly don't expose template substitution at the
/// per-recipient layer. `Body` is the raw text the carrier delivers;
/// callers are responsible for honouring the 160-character GSM-7
/// constraint or accepting multi-segment billing.
type SmsEnvelope = {
    RecipientUserIds: string list
    Body: string
    CorrelationId: string option
}

/// Payload of a `MobilePush` notification. `DeepLink`, when present,
/// is the URL the service worker / mobile app navigates to on click;
/// when absent, the click dismisses the notification with no further
/// action. `Title` and `Body` are inline strings — vendor templates
/// are deferred to a follow-up if push providers warrant them.
type PushEnvelope = {
    RecipientUserIds: string list
    Title: string
    Body: string
    DeepLink: string option
    CorrelationId: string option
}

/// Real-time notification delivered from server to client.
///
/// Kinds are deliberately small and infrastructure-flavoured — the
/// SDK is sector-agnostic and must not name domain concepts.
/// Modules that need a feature-specific payload use `CustomNotification`
/// with a module-owned `key`.
///
/// `payloadJson` on `CustomNotification` is a serialised JSON string,
/// not `obj` — `obj` does not round-trip cleanly through Fable
/// deserialisation, and making callers own the serialisation keeps
/// the wire format predictable.
type Notification =
    | SystemMessage of level: SystemMessageLevel * text: string
    | JobCompleted of jobId: Guid * status: string * resultLink: string option
    | DataRefreshed of dataTypeId: string * scopeId: string
    | TeamActivity of kind: string * summary: string
    /// Server-driven command targeting a specific client module. The
    /// client router looks up the module by `moduleId`, checks that the
    /// caller's `AccessibleModules` admit it, then hands
    /// `(actionKey, payloadJson)` to the module's `ActionDecoder` which
    /// returns an Elmish `Msg` for dispatch. Modules without a decoder
    /// silently ignore the action. This is the two-tier
    /// (chat / client action) pattern.
    | ModuleAction of moduleId: string * actionKey: string * payloadJson: string
    | CustomNotification of key: string * payloadJson: string
    /// Reserved platform-level event published by `TeamStore` after a
    /// successful membership write. Crosses the per-scope topic
    /// boundary on `PlatformReservedScope` so caches keyed on the
    /// affected user's prior scope can evict after they no longer
    /// belong to it. Subscribers must be idempotent — at-least-once
    /// delivery, no cross-publisher ordering.
    | MembershipChanged of MembershipChangedPayload
    /// Out-of-band transactional email. Filtered out of SSE delivery
    /// so the client EventSource never sees it; an `INotificationSink`
    /// of `Kind = "Email"` consumes it, resolves recipients via
    /// `INotificationAddressBook`, checks `_platform.notification_prefs`,
    /// and dispatches via the configured vendor adapter (SMTP /
    /// SendGrid / Postmark). Fire-and-forget from the publisher's view
    /// — terminal outcome lands in the audit trail as `NotificationSent`
    /// or `NotificationDeliveryFailed`.
    | TransactionalEmail of EmailEnvelope
    /// Out-of-band transactional SMS. Same dispatch model as
    /// `TransactionalEmail` — SSE-filtered, sink-routed by
    /// `Kind = "Sms"`, dispatched via the configured vendor (Twilio).
    | TransactionalSms of SmsEnvelope
    /// Out-of-band mobile push. Same dispatch model as
    /// `TransactionalEmail` — SSE-filtered, sink-routed by
    /// `Kind = "Push"`, dispatched via the configured vendor
    /// (WebPush / future FCM / APNs). One envelope fan-outs across
    /// every registered `PushToken` for each recipient.
    | MobilePush of PushEnvelope

/// Envelope wrapping a `Notification` with delivery metadata. The
/// server stamps `Id` and `OccurredAt` at publish time; subscribers
/// use them to deduplicate replays and order toasts.
///
/// **`TraceContext`** (Phase 9l) carries a W3C `traceparent` header
/// value (`00-<32 hex traceId>-<16 hex spanId>-<2 hex flags>`)
/// captured from the publisher's ambient `Activity.Current`. When the
/// envelope is consumed by a distributed-channel subscriber (Redis,
/// Service Bus, Orleans Streams), the subscriber re-parses it via
/// `ActivityContext.TryParse` and starts its own child activity under
/// that parent — the trace then spans the publisher's request, the
/// transport hop, and the subscriber's work. `None` is the default
/// for envelopes minted outside a request context (replay, tests,
/// process boot-time announcements). Channels MUST NOT trust the
/// caller-supplied value for routing or authorisation — it is
/// observability metadata only.
type NotificationEnvelope = {
    Id: Guid
    OccurredAt: DateTime
    ScopeId: string
    Notification: Notification
    TraceContext: string option
}

/// Helpers for building `NotificationEnvelope` values. Publishers
/// typically don't construct envelopes themselves — the channel stamps
/// id/timestamp inside `Publish`. This module exists for tests and for
/// implementations that need to synthesise envelopes (e.g. replay).
module NotificationEnvelope =
    /// Build a fresh envelope for the given scope and notification,
    /// stamping a new `Id` and UTC `OccurredAt`. The canonical way
    /// for the channel implementation to wrap incoming notifications
    /// before handing them to subscribers. `TraceContext` defaults to
    /// `None`; channels that want to carry the publisher's W3C trace
    /// id forward call `createWithTraceContext`.
    let create (scopeId: string) (notification: Notification) : NotificationEnvelope = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = scopeId
        Notification = notification
        TraceContext = None
    }

    /// Build a fresh envelope and stamp the supplied W3C `traceparent`
    /// string. Channels resolve `traceContext` from
    /// `System.Diagnostics.Activity.Current.Id` (which BCL formats as
    /// `00-traceId-spanId-flags` on .NET 10) at publish time; the
    /// helper exists so the dependency on `System.Diagnostics` stays
    /// in the server tier and the Core tier remains BCL-string-typed.
    let createWithTraceContext
        (scopeId: string)
        (notification: Notification)
        (traceContext: string option)
        : NotificationEnvelope =
        {
            Id = Guid.NewGuid()
            OccurredAt = DateTime.UtcNow
            ScopeId = scopeId
            Notification = notification
            TraceContext = traceContext
        }

/// Identity handle for an in-process notification subscription.
///
/// A `Guid` rather than an `IDisposable`: portability rule 1
/// (identity by value) forbids runtime handles in any interface that
/// could plausibly be replaced by a distributed implementation. An
/// Orleans grain or Akka actor can keep a `Map<Guid, ObserverRef>`
/// without exposing the framework-specific ref to callers; an
/// `IDisposable` return would force every implementation to surface
/// one, which they cannot.
type NotificationSubscriptionId = Guid

/// Case name of a `Notification` variant. Exposed as a stable kind
/// string so the client router can dispatch without pattern-matching
/// the whole payload — the shell subscribes once and routes by kind
/// to feature-specific handlers. Kept in sync with the DU manually;
/// there is no reflection on the client (Fable-compatibility rule).
module NotificationKind =
    [<Literal>]
    let SystemMessage = "SystemMessage"

    [<Literal>]
    let JobCompleted = "JobCompleted"

    [<Literal>]
    let DataRefreshed = "DataRefreshed"

    [<Literal>]
    let TeamActivity = "TeamActivity"

    [<Literal>]
    let ModuleAction = "ModuleAction"

    [<Literal>]
    let CustomNotification = "CustomNotification"

    [<Literal>]
    let MembershipChanged = "MembershipChanged"

    [<Literal>]
    let TransactionalEmail = "TransactionalEmail"

    [<Literal>]
    let TransactionalSms = "TransactionalSms"

    [<Literal>]
    let MobilePush = "MobilePush"

    /// Per-platform variant for `SinkKind.Push`. The compose-time
    /// uniqueness check keys on `SinkKind.toWireString`, so
    /// `Push WebPush` and `Push Fcm` register concurrently without
    /// collision. `Other name` is the open extension point for vendor-
    /// specific push variants the SDK hasn't anticipated; validators
    /// reject empty / whitespace `name` values, and the wire string
    /// includes the discriminator verbatim.
    [<RequireQualifiedAccess>]
    type PushVariant =
        | WebPush
        | Fcm
        | Apns
        | Other of name: string

    /// Stable kind discriminators used by `INotificationSink.Kind`.
    /// Replaces the pre-11.C.5 literal-string discriminator with a
    /// structured DU so two push companions (WebPush + Fcm, etc.) can
    /// register side by side. Convert to the wire-format string via
    /// `SinkKind.toWireString`; that string is what the compose-time
    /// uniqueness validator keys on and what the audit payload's
    /// `NotificationKind` field carries.
    [<RequireQualifiedAccess>]
    type SinkKind =
        | Email
        | Sms
        | Push of PushVariant

    module SinkKind =
        /// Stable wire-format string used by `INotificationSink.Kind`
        /// when serialised (audit log, uniqueness check, subscriber
        /// dispatch). Round-trips via `tryParse`. The `Push` variant
        /// emits `"Push.WebPush"` / `"Push.Fcm"` / `"Push.Apns"` /
        /// `"Push.<other-name>"` so two push companions distinguish
        /// at the wire format.
        let toWireString =
            function
            | SinkKind.Email -> "Email"
            | SinkKind.Sms -> "Sms"
            | SinkKind.Push PushVariant.WebPush -> "Push.WebPush"
            | SinkKind.Push PushVariant.Fcm -> "Push.Fcm"
            | SinkKind.Push PushVariant.Apns -> "Push.Apns"
            | SinkKind.Push(PushVariant.Other name) -> sprintf "Push.%s" name

        /// Inverse of `toWireString`. Returns `None` for unknown
        /// discriminators (a sink registering a wire-format the
        /// SDK does not recognise is a registration defect).
        let tryParse (wire: string) : SinkKind option =
            match wire with
            | "Email" -> Some SinkKind.Email
            | "Sms" -> Some SinkKind.Sms
            | "Push.WebPush" -> Some(SinkKind.Push PushVariant.WebPush)
            | "Push.Fcm" -> Some(SinkKind.Push PushVariant.Fcm)
            | "Push.Apns" -> Some(SinkKind.Push PushVariant.Apns)
            | s when s.StartsWith "Push." && s.Length > 5 -> Some(SinkKind.Push(PushVariant.Other(s.Substring 5)))
            | _ -> None

    /// Reserved `scopeId` for platform-level notifications that
    /// intentionally cross the per-scope topic boundary. Published by
    /// `TeamStore` (`MembershipChanged`) and any future infrastructure
    /// event whose subscribers don't all share a single tenant scope —
    /// e.g. cache invalidation has to reach every node, including
    /// those holding state for a removed user's prior scope.
    ///
    /// `INotificationChannel` exposes only `scopeId` as a routing key,
    /// so "topic" here means "magic scopeId value the publisher and
    /// every subscriber agree on".
    [<Literal>]
    let PlatformReservedScope = "_platform"

    /// Returns the kind string for a notification. Client code uses
    /// this to tag dispatched events; server code uses it to build
    /// the SSE `event:` line.
    let ofNotification (n: Notification) : string =
        match n with
        | Notification.SystemMessage _ -> SystemMessage
        | Notification.JobCompleted _ -> JobCompleted
        | Notification.DataRefreshed _ -> DataRefreshed
        | Notification.TeamActivity _ -> TeamActivity
        | Notification.ModuleAction _ -> ModuleAction
        | Notification.CustomNotification _ -> CustomNotification
        | Notification.MembershipChanged _ -> MembershipChanged
        | Notification.TransactionalEmail _ -> TransactionalEmail
        | Notification.TransactionalSms _ -> TransactionalSms
        | Notification.MobilePush _ -> MobilePush

    /// `true` when a notification represents an out-of-band transactional
    /// delivery (email / SMS / push) that must NOT be written to the
    /// SSE stream — those kinds are routed to `INotificationSink`
    /// implementations only.
    let isTransactional (n: Notification) : bool =
        match n with
        | Notification.TransactionalEmail _
        | Notification.TransactionalSms _
        | Notification.MobilePush _ -> true
        | _ -> false