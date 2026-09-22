namespace ToolUp.Platform

/// Lookup surface for resolving a `RecipientId` into the
/// vendor-neutral address types (`EmailAddress`, `PhoneNumber`,
/// `PushToken`) that transactional sinks consume. Phase 6f; widened
/// from `userId` to `RecipientId` by Phase 6f.A.
///
/// **Why this interface exists.** Transactional envelopes
/// (`EmailEnvelope`, `SmsEnvelope`, `PushEnvelope`) carry only
/// `Recipients: RecipientId list` so PII never crosses an
/// `INotificationChannel` wire (especially relevant when the channel
/// is Redis-backed). The sink resolves PII at dispatch time via this
/// interface and immediately hands it to the upstream vendor — no
/// resolved address is persisted to logs, audit trails, or Redis
/// topics.
///
/// **Why the three members were WIDENED rather than joined by three
/// `Resolve*ForRecipient` siblings.** Keeping the `userId` forms would
/// leave a caller — a future sink, an external implementation, a
/// well-meant refactor — able to reach an address by a path with no
/// consent question in it, and for an `External` recipient that path
/// is a send nobody agreed to. Six members where three suffice is also
/// surface the approval baseline makes permanent. The widening is a
/// breaking change to the interface, taken deliberately on a `0.x`
/// SDK: every shipped caller (the four notification sinks) migrates in
/// the same phase, and an external implementation's migration is
/// mechanical — match on the DU and keep the old body under
/// `RecipientId.User`.
///
/// **An `External` recipient is CONSENT-GATED.** An implementation
/// returns `None` / `[]` for an `External` recipient that carries no
/// live `OptInRecord` for the channel being resolved, exactly as it
/// does for a user with no address. The difference is what the caller
/// does with the `None`: `ExternalContactConsentFilter` refuses the
/// send and audits `NotificationDeliveryRefused` before the envelope
/// ever reaches a sink, so a missing consent is never a silent drop.
/// An implementation that resolves an external recipient WITHOUT
/// consulting consent has defeated the gate.
///
/// **Identity by value.** Phase 9c rule 1 — `RecipientId` carries
/// strings and `scopeId` is a string. Implementations must not return
/// vendor handles or runtime references; the returned shapes
/// (`EmailAddress`, `PhoneNumber`, `PushToken`) are plain records that
/// round-trip through any distributed transport.
///
/// **Async at every boundary.** Phase 9c rule 2. Implementations
/// reading from a blob store, an LDAP directory, or a remote auth
/// service all use `Async<_>` uniformly.
///
/// **Statelessness.** Phase 9c rule 4 — `Resolve*` derives its result
/// from `(recipient, scopeId)` plus injected infrastructure. No in-memory
/// caching is part of the contract; an implementation may layer one on
/// internally but must remain correct under cold-call cardinality.
///
/// **Scope isolation.** Phase 6f respects GP 4 — a `(recipient, scopeId)`
/// pair is the lookup key, not just the recipient. A user belonging to two
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
    /// Look up `recipient`'s email address inside `scopeId`. `None`
    /// means "no address known" — or, for an `External` recipient, "no
    /// live email consent". Sinks treat it as a recipient skip, NOT as
    /// a failure; the refusal that distinguishes the two happens
    /// upstream, in the consent filter.
    abstract ResolveEmail: recipient: RecipientId * scopeId: string -> Async<EmailAddress option>

    /// Look up `recipient`'s phone number (E.164). `None` means "no
    /// phone known", or no live SMS consent for an external recipient
    /// — sink skips this recipient. Default implementations that don't
    /// track phone numbers (the auth-provider-driven default) simply
    /// return `None` for every call.
    abstract ResolvePhone: recipient: RecipientId * scopeId: string -> Async<PhoneNumber option>

    /// Look up every push token registered for `recipient` inside
    /// `scopeId`. A user may have multiple registered devices
    /// (browser tab + mobile app); the sink fan-outs delivery per
    /// token. Empty list = no devices — or no live push consent for an
    /// external recipient — and the sink skips this recipient.
    ///
    /// An external contact has no device registration in the shipped
    /// model, so the default implementation returns `[]` for every
    /// `External` recipient regardless of consent.
    abstract ResolvePushTokens: recipient: RecipientId * scopeId: string -> Async<PushToken list>