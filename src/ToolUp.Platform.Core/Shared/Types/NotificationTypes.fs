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
/// Resolved server-side from a `RecipientId` via
/// `INotificationAddressBook`
/// at sink dispatch time — `EmailEnvelope` carries `Recipients`
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

/// Who a transactional envelope is addressed to. Phase 6f.A widened
/// the three envelope payloads from `RecipientUserIds: string list` to
/// `Recipients: RecipientId list` so the same dispatch substrate can
/// address a recipient who has no account on the platform — a client of
/// a small business, a household member who never installed the app, a
/// survey respondent.
///
/// **Why a DU and not a bare string.** A `userId` and a `contactId` are
/// both opaque strings, and resolving one as the other is a
/// team-isolation bug that no test would catch: `User "abc"` and
/// `External "abc"` are different rows in different stores with
/// different consent rules. The DU forces every resolution site to
/// disambiguate at the type level — the same argument
/// `FlagScope` makes for flag scopes.
///
/// **Consent asymmetry is the point.** A `User` recipient has an
/// account, a preference record and a relationship with the
/// deployment; an `External` recipient has none of those, so the only
/// lawful basis for contacting them is a recorded per-channel opt-in.
/// `INotificationAddressBook` therefore resolves an `External`
/// recipient to `None` unless the contact carries a live opt-in for
/// that `SinkKind`, and the consent filter refuses the send rather
/// than dropping it silently.
///
/// Fable-safe: two string-carrying cases, no BCL beyond `string`.
/// `[<RequireQualifiedAccess>]` because `User` and `External` are
/// already taken unqualified in this namespace (`FlagScope.User`,
/// `AICapabilityOrigin.External`).
[<RequireQualifiedAccess>]
type RecipientId =
    /// An authenticated platform user, resolved through the address
    /// book's `(userId, scopeId)` lookup exactly as before Phase 6f.A.
    | User of userId: string
    /// A non-platform recipient held as an `ExternalContact` in the
    /// scope's external address book. Resolution is consent-gated.
    | External of contactId: string

/// Helpers for `RecipientId`. The wire strings are what audit payloads
/// and log lines carry, so they are a stable format, not a debug
/// rendering.
module RecipientId =
    /// Stable wire-format string — `"user:{id}"` / `"external:{id}"`.
    /// Round-trips via `tryParse`. Audit payloads that record a
    /// recipient list carry this form so one recipient reads the same
    /// way whichever kind it is, and so a reader can tell the two
    /// apart without consulting the envelope.
    let toWireString (recipient: RecipientId) : string =
        match recipient with
        | RecipientId.User userId -> "user:" + userId
        | RecipientId.External contactId -> "external:" + contactId

    /// Inverse of `toWireString`. Returns `None` for an unrecognised
    /// discriminator. A BARE string (no prefix) parses as
    /// `RecipientId.User` — that is the pre-Phase-6f.A wire form, and a
    /// persisted envelope written before the widening must still read
    /// back as the user recipient it was.
    let tryParse (wire: string) : RecipientId option =
        if System.String.IsNullOrEmpty wire then
            None
        elif wire.StartsWith "user:" then
            Some(RecipientId.User(wire.Substring 5))
        elif wire.StartsWith "external:" then
            Some(RecipientId.External(wire.Substring 9))
        elif wire.Contains ":" then
            None
        else
            Some(RecipientId.User wire)

    /// The form audit payloads carry. A `User` recipient renders as the
    /// BARE `userId` — byte-for-byte what `NotificationSentPayload`
    /// carried before Phase 6f.A, so existing audit rows and the
    /// `SHA256(recipient)[..8]` correlation hashes derived from them are
    /// unchanged (GP 11). An `External` recipient keeps its prefix,
    /// because a contact id and a user id are different namespaces and a
    /// reader must be able to tell which one a row names. Parses back
    /// through `tryParse`, whose bare-string arm exists for exactly this.
    let toAuditString (recipient: RecipientId) : string =
        match recipient with
        | RecipientId.User userId -> userId
        | RecipientId.External contactId -> "external:" + contactId

    /// Wrap a plain `userId` — the migration helper for every call site
    /// that addressed an envelope before Phase 6f.A widened it.
    let ofUserId (userId: string) : RecipientId = RecipientId.User userId

    /// Wrap a list of plain `userId`s.
    let ofUserIds (userIds: string list) : RecipientId list = userIds |> List.map RecipientId.User

    /// The `userId`s among `recipients`, dropping external contacts.
    /// Used by the per-user preference filter, which has no record to
    /// read for a recipient with no account.
    let userIds (recipients: RecipientId list) : string list =
        recipients
        |> List.choose (function
            | RecipientId.User userId -> Some userId
            | RecipientId.External _ -> None)

    /// The `contactId`s among `recipients`, dropping platform users.
    let contactIds (recipients: RecipientId list) : string list =
        recipients
        |> List.choose (function
            | RecipientId.User _ -> None
            | RecipientId.External contactId -> Some contactId)

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

/// Payload of a `TransactionalEmail` notification. `Recipients`
/// resolve to `EmailAddress`es server-side via `INotificationAddressBook`
/// — recipients with no resolvable address are silently dropped (no
/// audit event), the remaining list is delivered. An `External`
/// recipient with no email opt-in is REFUSED rather than dropped, and
/// the refusal is audited. `CorrelationId`
/// forwards to vendors that support idempotent send (SendGrid
/// `X-Message-Id`, SMTP `Message-ID`) so retries don't double-send.
type EmailEnvelope = {
    /// Who to deliver to. Wrap a plain `userId` with
    /// `RecipientId.User` (or the whole list with
    /// `RecipientId.ofUserIds`) — the field carried `string list`
    /// before Phase 6f.A.
    Recipients: RecipientId list
    Content: EmailContent
    CorrelationId: string option
}

/// Payload of a `TransactionalSms` notification. SMS is always inline
/// — vendors mostly don't expose template substitution at the
/// per-recipient layer. `Body` is the raw text the carrier delivers;
/// callers are responsible for honouring the 160-character GSM-7
/// constraint or accepting multi-segment billing.
type SmsEnvelope = {
    /// Who to deliver to. See `EmailEnvelope.Recipients` for the
    /// migration shape.
    Recipients: RecipientId list
    Body: string
    CorrelationId: string option
}

/// Payload of a `MobilePush` notification. `DeepLink`, when present,
/// is the URL the service worker / mobile app navigates to on click;
/// when absent, the click dismisses the notification with no further
/// action. `Title` and `Body` are inline strings — vendor templates
/// are deferred to a follow-up if push providers warrant them.
type PushEnvelope = {
    /// Who to deliver to. See `EmailEnvelope.Recipients` for the
    /// migration shape.
    Recipients: RecipientId list
    Title: string
    Body: string
    DeepLink: string option
    CorrelationId: string option
}

/// Payload of a `TransactionalWhatsApp` notification (Phase 827). The
/// vendor-neutral half of WhatsApp: a sink (one per vendor) reads it,
/// resolves the recipients' numbers and sends.
///
/// **Two message shapes, one envelope.** WhatsApp distinguishes a
/// business-initiated message — which MUST be a pre-approved template —
/// from a free-form reply, which is permitted only inside the 24-hour
/// customer-care window that the recipient's own last inbound message
/// opens. `TemplateName = Some _` is a template send (`TemplateLanguage`
/// and `TemplateParameters` qualify it, `Body` must be `None`);
/// `TemplateName = None` is a free-form send carried by `Body`. The
/// server refuses, before any sink runs, a free-form send to a recipient
/// outside the window, a template the deployment's template registry
/// does not know, and a parameter count the registry disagrees with.
///
/// **`TemplateParameters` is flat and ordered** — header parameters
/// first, then body, then buttons — so the envelope stays a plain list
/// across every transport; the registry's per-component arity says
/// where one component ends and the next begins.
type WhatsAppEnvelope = {
    /// Who to deliver to. See `EmailEnvelope.Recipients` for the
    /// migration shape.
    Recipients: RecipientId list
    /// Name of the approved template, for a business-initiated send.
    /// `None` makes this a free-form send, allowed only inside the
    /// recipient's 24-hour customer-care window.
    TemplateName: string option
    /// Language code the template is sent in (e.g. `"en_GB"`). `None`
    /// leaves the choice to the sink (the template's first registered
    /// language). Ignored on a free-form send.
    TemplateLanguage: string option
    /// Template parameter values, header then body then buttons. Must
    /// match the registered template's total arity exactly.
    TemplateParameters: string list
    /// Free-form text. Only for a send inside the 24-hour window; must
    /// be `None` when `TemplateName` is set.
    Body: string option
    /// Vendor-neutral key/value hints a sink may forward (a tag, a
    /// campaign label). Never PII: the envelope crosses the channel
    /// wire, and resolved addresses must not.
    Metadata: Map<string, string>
    /// Forwarded to vendors that support idempotent send, and carried on
    /// every audit row the send produces.
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
    /// Out-of-band WhatsApp message (Phase 827). Same dispatch model as
    /// `TransactionalSms` — SSE-filtered, sink-routed by
    /// `Kind = SinkKind.WhatsApp` — with the WhatsApp Business rules
    /// (template for business-initiated sends, the 24-hour window for
    /// free-form ones) enforced server-side before any sink runs.
    | TransactionalWhatsApp of WhatsAppEnvelope

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

    /// Kind string for `Notification.TransactionalWhatsApp` (Phase 827).
    [<Literal>]
    let TransactionalWhatsApp = "TransactionalWhatsApp"

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
        /// WhatsApp, vendor-neutral (Phase 827): every WhatsApp vendor
        /// companion registers under this one kind, so a deployment
        /// composes at most one WhatsApp sink.
        | WhatsApp

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
            | SinkKind.WhatsApp -> "WhatsApp"

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
            | "WhatsApp" -> Some SinkKind.WhatsApp
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
        | Notification.TransactionalWhatsApp _ -> TransactionalWhatsApp

    /// `true` when a notification represents an out-of-band transactional
    /// delivery (email / SMS / push / WhatsApp) that must NOT be written to the
    /// SSE stream — those kinds are routed to `INotificationSink`
    /// implementations only.
    let isTransactional (n: Notification) : bool =
        match n with
        | Notification.TransactionalEmail _
        | Notification.TransactionalSms _
        | Notification.MobilePush _
        | Notification.TransactionalWhatsApp _ -> true
        | _ -> false