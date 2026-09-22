module ToolUp.Scheduling.ICalendarBridge

open System
open ToolUp.Scheduling.SchedulingTypes

// ─── Phase 20a — the calendar-bridge seam ───────────────────────────
//
// The portable surface an external calendar provider implements so
// bookings can round-trip with a calendar the deployment does not own.
// The first provider is the generic CalDAV companion
// (`ToolUp.Calendar.CalDAV`); Google Calendar and Microsoft Graph land
// later against this same surface.
//
// **Where the pieces live.** The bridge speaks to the provider and
// nothing else: it holds no state, owns no store, and schedules no
// work. The resource ↔ external-calendar LINK is owned by the sync
// engine (`CalendarSync.fs`, Server tier) over the parent companion's
// storage seam, which is also where the conflict policy and the poll
// job live. That split is what lets a bridge be a ~200-line adapter.
//
// **Six portability rules** (the SDK's portability contract):
//
//   1. Identity by value      — `ExternalEventId` / `ExternalCalendarId`
//                                are `string` aliases and `CalendarLinkRef`
//                                is a plain record. Nothing on this
//                                surface is a live provider handle, an
//                                `HttpClient`, or a vendor session.
//   2. Async at every boundary — every member returns `Async<Result<_,
//                                BridgeError>>`. No sync escape hatch and
//                                no fire-and-forget.
//   3. Retry / supervision as data — the bridge NEVER retries. It
//                                classifies (`Unreachable` /
//                                `RateLimited` retryable, everything else
//                                terminal) and the caller's policy owns
//                                the loop: the sync engine's poll job
//                                carries a `JobRetryPolicy`.
//   4. Stateless between calls — every call carries the whole
//                                `CalendarLinkRef`; nothing is cached
//                                across invocations, and credentials are
//                                resolved from `ISecretStore` per call so
//                                rotation flows through immediately.
//   5. No cross-shard ordering — ordering is promised only within one
//                                `CalendarLinkRef`. Two links, even on the
//                                same resource, are independent.
//   6. Precision at the lower bound — `BridgeCapabilities.MinimumPollInterval`
//                                is the declared floor; the sync engine
//                                refuses to poll a bridge faster than the
//                                provider tolerates rather than promising
//                                a cadence the provider will throttle.
//
// **Credentials never appear here.** A bridge receives `ISecretStore`
// through its own `create` function and reads per call, keyed by
// `CalendarLinkRef.ScopeId` — the standard companion convention. No
// token, password or app-password is ever a parameter on this surface,
// so a `CalendarLinkRef` is safe to persist, log and audit.

/// Stable identifier of a calendar in the external provider. Type alias
/// for `string`: a CalDAV collection URL, a Google calendar id, a Graph
/// calendar id — all flow through unchanged (portability rule 1).
type ExternalCalendarId = string

/// Stable identifier of ONE event inside an external calendar, as the
/// provider names it. Type alias for `string` — a CalDAV resource href,
/// a Google/Graph event id. Opaque to the SDK: it is stored beside the
/// booking and handed back on the next push, never parsed.
type ExternalEventId = string

/// Why a bridge call failed. Deliberately closed and provider-neutral:
/// every case is something a caller can branch on without knowing which
/// provider produced it. Vendor detail rides the message.
type BridgeError =
    /// The provider rejected the supplied credentials, or the deployment
    /// has none for this scope. Terminal — retrying with the same secret
    /// cannot succeed; the operator rotates the credential.
    | AuthenticationFailed of message: string
    /// The external calendar named by the link does not exist, or the
    /// authenticated principal cannot see it. Terminal.
    | CalendarNotFound of calendarId: ExternalCalendarId
    /// The external event named by `existing` is gone (deleted at the
    /// provider since we last pushed). The caller re-pushes without an
    /// `existing` id to recreate it.
    | EventNotFound of eventId: ExternalEventId
    /// The provider refused the request on its merits — an invalid
    /// recurrence, a read-only calendar, a quota. Terminal; `status` is
    /// the transport status where one exists, `0` otherwise.
    | ExternalRejected of status: int * message: string
    /// The provider asked us to slow down. Retryable; `retryAfter` is
    /// the provider's own hint when it supplied one.
    | RateLimited of retryAfter: TimeSpan option
    /// Transport failure — DNS, TLS, timeout, 5xx. Retryable.
    | Unreachable of message: string
    /// The provider's payload could not be understood (malformed ICS,
    /// unparseable multistatus, a webhook body that is not ours).
    /// Terminal for that payload; other payloads are unaffected.
    | MalformedPayload of message: string

module BridgeError =
    /// One-line diagnostic text. Stable enough for a log line, NOT a
    /// wire format — callers branch on the case, never on this string.
    let message (error: BridgeError) : string =
        match error with
        | AuthenticationFailed m -> sprintf "authentication failed: %s" m
        | CalendarNotFound c -> sprintf "external calendar not found: %s" c
        | EventNotFound e -> sprintf "external event not found: %s" e
        | ExternalRejected(status, m) -> sprintf "provider rejected the request (status %d): %s" status m
        | RateLimited(Some after) -> sprintf "rate limited; retry after %O" after
        | RateLimited None -> "rate limited"
        | Unreachable m -> sprintf "provider unreachable: %s" m
        | MalformedPayload m -> sprintf "malformed provider payload: %s" m

    /// Whether another attempt could plausibly succeed. The sync engine
    /// maps this onto `JobResult.TransientFailure` / `PermanentFailure`
    /// — portability rule 3: the classification is data the caller acts
    /// on, not a retry the bridge performs.
    let isRetryable (error: BridgeError) : bool =
        match error with
        | RateLimited _
        | Unreachable _ -> true
        | AuthenticationFailed _
        | CalendarNotFound _
        | EventNotFound _
        | ExternalRejected _
        | MalformedPayload _ -> false

/// What a bridge can do, declared as data so the sync engine can wire
/// itself correctly without knowing the provider. Read once at compose
/// time; a bridge's capabilities do not change between calls.
type BridgeCapabilities = {
    /// `true` when the provider delivers inbound change notifications
    /// that `HandleWebhook` understands. `false` — polling-only: the
    /// sync engine registers a poll job for this bridge, and
    /// `HandleWebhook` is a no-op returning `Ok []`.
    SupportsWebhooks: bool
    /// `true` when `Pull`'s `since` argument is honoured against the
    /// provider's own MODIFICATION cursor, so an unchanged event is not
    /// returned twice. `false` — `since` is honoured as a lower bound on
    /// event TIME (the best a plain CalDAV `time-range` filter can do),
    /// so the caller must treat every returned event as possibly
    /// unchanged and reconcile by value.
    SupportsIncrementalPull: bool
    /// The fastest cadence this provider tolerates being polled at
    /// (portability rule 6). The sync engine clamps its poll trigger to
    /// this floor rather than promising a cadence the provider throttles.
    MinimumPollInterval: TimeSpan
}

module BridgeCapabilities =
    /// The conservative default a new bridge starts from: polling only,
    /// no modification cursor, fifteen-minute floor.
    let pollingOnly: BridgeCapabilities = {
        SupportsWebhooks = false
        SupportsIncrementalPull = false
        MinimumPollInterval = TimeSpan.FromMinutes 15.0
    }

/// Everything a bridge needs to act on one resource's external
/// calendar, by value (portability rule 1). Carried on every call so a
/// bridge holds no state between invocations (rule 4).
type CalendarLinkRef = {
    /// Storage scope the booking lives in. Also the `ISecretStore` scope
    /// the bridge resolves its credentials under, so two tenants linking
    /// the same provider never share a secret (GP 4).
    ScopeId: string
    /// The bookable resource whose schedule is mirrored.
    ResourceId: ResourceId
    /// The external calendar it is mirrored into.
    ExternalCalendarId: ExternalCalendarId
    /// The user whose credentials authorise the mirror. A bridge keys
    /// its `ISecretStore` lookup on this, so one deployment can mirror
    /// several users' calendars through one bridge instance.
    UserId: string
}

/// One event read back out of an external calendar.
///
/// `Booking` is the external event projected into the parent
/// companion's domain shape — the bridge does that projection because
/// only it knows the provider's wire format. Fields the external
/// calendar cannot express (`ResourceId`, `Status`, `BookedBy`,
/// `Metadata` the provider dropped) are filled from the caller-supplied
/// defaults the bridge is handed, exactly as `iCalendar.vEventToBooking`
/// does.
type ExternalEvent = {
    /// The provider's identifier for this event — pass it back as
    /// `Push`'s `existing` argument to update or cancel it.
    ExternalEventId: ExternalEventId
    /// The event as the external calendar now describes it.
    Booking: Booking
    /// When the provider says the event last changed. `None` when the
    /// provider reports no modification stamp — `LatestModifiedWins`
    /// treats that as "older than our last push", so an unordered
    /// external change never silently overwrites a local edit.
    LastModifiedUtc: DateTimeOffset option
}

/// One inbound change the provider notified us about. A bridge that
/// supports webhooks turns a raw notification body into these; the sync
/// engine resolves each to a link and pulls it.
type WebhookNotification = {
    /// The calendar that changed.
    ExternalCalendarId: ExternalCalendarId
    /// The one event that changed, when the provider says which. `None`
    /// — the provider only said "something changed"; pull the whole link
    /// from its cursor.
    ChangedEventId: ExternalEventId option
}

/// Which side wins when a booking changed on BOTH sides since the last
/// synchronisation. Declared per link, so one deployment can mirror an
/// authoritative room calendar one way and a personal calendar the
/// other.
type ConflictPolicy =
    /// The external calendar is authoritative: an external change is
    /// applied locally, overwriting the local edit.
    | ExternalWins
    /// The deployment is authoritative: the local booking is re-pushed,
    /// overwriting the external edit.
    | LocalWins
    /// Whichever side changed most recently wins. "Most recently" is
    /// decided by comparing the external event's `LastModifiedUtc`
    /// against the instant we last pushed this event
    /// (`CalendarEventLink.LastPushedAtUtc`) — the only two instants
    /// both sides agree on. An external event with no modification
    /// stamp is treated as OLDER, so the local edit survives.
    | LatestModifiedWins

module ConflictPolicy =
    /// Stable string form for persistence and index keys.
    let toString (policy: ConflictPolicy) : string =
        match policy with
        | ExternalWins -> "ExternalWins"
        | LocalWins -> "LocalWins"
        | LatestModifiedWins -> "LatestModifiedWins"

    /// Parse the persisted form. Unrecognised input yields `None` —
    /// callers decide whether that is a storage fault or a default.
    let ofString (value: string) : ConflictPolicy option =
        match value with
        | "ExternalWins" -> Some ExternalWins
        | "LocalWins" -> Some LocalWins
        | "LatestModifiedWins" -> Some LatestModifiedWins
        | _ -> None

/// A provider adapter for one external calendar system.
///
/// Implementations are stateless adapters: construct one per process,
/// per provider, at compose time and register it with
/// `SchedulingServerApp.withCalendarBridge`. Registration is keyed by
/// `Kind`, so a deployment composes at most one bridge per provider and
/// a duplicate is refused at compose time naming the contested kind.
type ICalendarBridge =
    /// Stable provider discriminator — `"CalDAV"` / `"Google"` /
    /// `"Microsoft"`. Persisted on every link, so it is a wire value:
    /// changing one strands the links that name it.
    abstract Kind: string

    /// What this bridge can do, as data. Read at compose time.
    abstract Capabilities: BridgeCapabilities

    /// Establish the mirror at the provider. Called once, before any
    /// push or pull, and must be idempotent: a deployment that re-links
    /// an already-linked resource re-runs whatever provider-side setup
    /// the bridge needs (verifying the calendar is reachable and
    /// writable; renewing a watch channel for a webhook-capable
    /// provider) and returns `Ok`.
    ///
    /// Persisting the link is the CALLER's job — the sync engine writes
    /// it to the storage seam. A bridge that stored links itself would
    /// break portability rule 4.
    abstract LinkResource: link: CalendarLinkRef -> Async<Result<unit, BridgeError>>

    /// Tear the mirror down at the provider — cancel a watch channel,
    /// release a subscription. Idempotent: unlinking a resource that was
    /// never linked returns `Ok`. It does NOT delete events already
    /// pushed; a deployment that wants them gone cancels the bookings
    /// first, which pushes the deletions.
    abstract UnlinkResource: link: CalendarLinkRef -> Async<Result<unit, BridgeError>>

    /// Mirror one booking into the external calendar and return the
    /// provider's id for it.
    ///
    /// `existing = None` creates; `existing = Some id` updates that
    /// event in place. A booking whose `Status` is `Cancelled` is
    /// REMOVED from the external calendar rather than mirrored as a
    /// cancelled event, and the returned id is `existing`'s — a cancel
    /// is a push, which is why this surface needs no separate delete
    /// member.
    ///
    /// Must be idempotent per `(link, booking.Id)`: the sync engine
    /// re-pushes after a transient failure, and a provider that created
    /// a second event would leave a duplicate nobody owns.
    abstract Push:
        link: CalendarLinkRef * booking: Booking * existing: ExternalEventId option ->
            Async<Result<ExternalEventId, BridgeError>>

    /// Read events back out of the external calendar.
    ///
    /// `since = None` reads the provider's default window. `since = Some
    /// t` narrows: against a provider declaring
    /// `SupportsIncrementalPull` it is a MODIFICATION cursor (only
    /// events changed since `t`); otherwise it is a lower bound on event
    /// time, and the caller reconciles by value. `defaults` supplies the
    /// booking fields no calendar can express — the bridge fills them
    /// verbatim, exactly as `iCalendar.vEventToBooking` does.
    abstract Pull:
        link: CalendarLinkRef * since: DateTimeOffset option * defaults: Booking ->
            Async<Result<ExternalEvent list, BridgeError>>

    /// Interpret one inbound change notification.
    ///
    /// `headers` carries the request headers the provider signed or
    /// stamped (a Google `X-Goog-Channel-Token`, a Graph
    /// `clientState`); `body` is the raw request body, unparsed. A
    /// bridge verifies the notification is genuinely the provider's
    /// before returning anything — an unverified notification is
    /// `MalformedPayload`, never an empty success.
    ///
    /// A polling-only bridge (`Capabilities.SupportsWebhooks = false`)
    /// returns `Ok []` and does nothing: the deployment may still have a
    /// webhook route mounted, and a no-op is the honest answer.
    abstract HandleWebhook:
        headers: Map<string, string> * body: byte[] -> Async<Result<WebhookNotification list, BridgeError>>