namespace ToolUp.Platform

/// Lookup surface for resolving authenticated `userId`s into the
/// vendor-neutral address types (`EmailAddress`, `PhoneNumber`,
/// `PushToken`) that transactional sinks consume. Phase 6f.
///
/// **Why this interface exists.** Transactional envelopes
/// (`EmailEnvelope`, `SmsEnvelope`, `PushEnvelope`) carry only
/// `RecipientUserIds: string list` so PII never crosses an
/// `INotificationChannel` wire (especially relevant when the channel
/// is Redis-backed). The sink resolves PII at dispatch time via this
/// interface and immediately hands it to the upstream vendor — no
/// resolved address is persisted to logs, audit trails, or Redis
/// topics.
///
/// **Identity by value.** Phase 9c rule 1 — `userId` and `scopeId` are
/// strings. Implementations must not return vendor handles or runtime
/// references; the returned shapes (`EmailAddress`, `PhoneNumber`,
/// `PushToken`) are plain records that round-trip through any
/// distributed transport.
///
/// **Async at every boundary.** Phase 9c rule 2. Implementations
/// reading from a blob store, an LDAP directory, or a remote auth
/// service all use `Async<_>` uniformly.
///
/// **Statelessness.** Phase 9c rule 4 — `Resolve*` derives its result
/// from `(userId, scopeId)` plus injected infrastructure. No in-memory
/// caching is part of the contract; an implementation may layer one on
/// internally but must remain correct under cold-call cardinality.
///
/// **Scope isolation.** Phase 6f respects GP 4 — a `(userId, scopeId)`
/// pair is the lookup key, not just `userId`. A user belonging to two
/// teams may register different push tokens or different forwarding
/// emails per team; cross-team resolution returning a different team's
/// data is a team-isolation breach. Implementations that aggregate a
/// global directory must filter by `scopeId` before returning.
///
/// **Failure semantics.** No PII to resolve is a normal case, not an
/// error: `ResolveEmail` returns `None`, `ResolvePushTokens` returns
/// `[]`. Sinks short-circuit to `SinkResult.Skipped` on no-address
/// recipients without retrying. Implementations that hit a transient
/// upstream failure should still raise — sinks classify upstream
/// exceptions as `TransientFailure` so the dispatcher's retry loop
/// covers them.
type INotificationAddressBook =
    /// Look up `userId`'s registered email address inside `scopeId`.
    /// `None` means "no address known" — sinks treat this as a
    /// recipient skip, NOT as a failure.
    abstract ResolveEmail: userId: string * scopeId: string -> Async<EmailAddress option>

    /// Look up `userId`'s registered phone number (E.164). `None`
    /// means "no phone known" — sink skips this recipient. Default
    /// implementations that don't track phone numbers (the auth-
    /// provider-driven default) simply return `None` for every call.
    abstract ResolvePhone: userId: string * scopeId: string -> Async<PhoneNumber option>

    /// Look up every push token registered for `userId` inside
    /// `scopeId`. A user may have multiple registered devices
    /// (browser tab + mobile app); the sink fan-outs delivery per
    /// token. Empty list = no devices, sink skips this recipient.
    abstract ResolvePushTokens: userId: string * scopeId: string -> Async<PushToken list>