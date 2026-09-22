// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── IExternalContactStore (Phase 6f.A) ──────────────────────────────
//
// SDK-level interface for the external address book: recipients who have
// NO account on the platform — a client of a small business, a member of
// an accountability group, a household member who never installed the
// app, a survey respondent. The default impl is `ExternalContactStore`,
// over `IEntityStore`; a deployment with an existing CRM binds its own.
//
// The type model — `ExternalContact`, `ContactOwner`, `OptInRecord`, the
// per-channel consent map keyed on `NotificationKind.SinkKind` — lives in
// `ToolUp.Platform.Core`'s `ExternalContactTypes.fs`; read that file's
// preamble first, this one is only the operations.
//
// **The scope parameter is not decoration.** Every member takes `scopeId`
// and the implementation MUST refuse a contact whose scope differs
// (`ExternalContactError.ScopeMismatch`). A team owner in team A naming
// team B's contact id is a cross-tenant read of someone's personal data,
// and the only structural place to stop it is here, at the store — a
// handler that "remembers to filter" is exactly the convention GP 4
// exists to replace.
//
// **Consent moves only through `RecordOptIn` / `WithdrawOptIn`.** `Update`
// deliberately cannot touch `OptIns`: a consent is a legal artefact, and
// a path that could move it as a side effect of an address edit is a path
// where nobody can say afterwards what was consented to and when. The two
// dedicated members are also the two the API handler audits.
//
// **There is no clock in this interface.** `RecordOptIn` takes a complete
// `OptInRecord` and `RecordInbound` takes the timestamp, so the store
// derives nothing from ambient time and an implementation is testable
// without one (portability rule 4). Expiry is evaluated by the reader —
// `ExternalContact.hasLiveOptIn` — not by the store.
//
// **Phase 9c portability rules (all six honoured):**
//
//   1. Identity by value. Every parameter and return is a string, a
//      domain record or a DU. `actorUserId` is a string, not a principal
//      handle; the entity-backed impl builds its own `EntityPrincipal`.
//   2. Async at every boundary. Every member returns `Async<_>`.
//   3. Retry / supervision as data. Failures are `ExternalContactError`
//      values; no callbacks, no exceptions on expected paths.
//   4. Stateless handlers between invocations. Nothing is remembered
//      across calls; a caching implementation owns that decision and its
//      invalidation story.
//   5. No cross-shard ordering promises. Contacts are independent, and
//      `List` promises no ordering beyond what the caller sorts for
//      itself.
//   6. Precision at the lower bound. `GrantedAt` / `ExpiresAt` /
//      `LastInboundUtc` are second-precision; no sub-second promise is
//      made or required.

type IExternalContactStore =
    /// File a new contact in `scopeId`. The store assigns `Id`, stamps
    /// `CreatedAt`, sets `Version`, and starts the contact with NO
    /// consent on any channel — a newly filed contact is unreachable
    /// until someone records one, which is the safe default and the
    /// legally correct one.
    ///
    /// Refuses a contact whose normalised email or phone already exists
    /// in the same `owner`'s address book (`Duplicate existingId`): two
    /// rows for one person means two sends, and a withdrawal that
    /// silences only one of them.
    abstract Create:
        scopeId: string * actorUserId: string * owner: ContactOwner * request: CreateExternalContactRequest ->
            Async<Result<ExternalContact, ExternalContactError>>

    /// Read one contact. `Error ScopeMismatch` when the contact exists
    /// but belongs to another scope — never `Ok`, and never the other
    /// scope's record.
    abstract Get: scopeId: string * contactId: string -> Async<Result<ExternalContact, ExternalContactError>>

    /// Every contact in `scopeId`. No ordering promise (rule 5).
    abstract List: scopeId: string -> Async<ExternalContact list>

    /// Every contact in `scopeId` belonging to `owner`. Served from the
    /// owner index rather than by filtering `List`, so a shared team
    /// address book does not cost a full scan per personal lookup.
    abstract ListByOwner: scopeId: string * owner: ContactOwner -> Async<ExternalContact list>

    /// Replace a contact's reachable fields. **Cannot touch `OptIns`** —
    /// see the preamble. Re-runs the duplicate check when an address
    /// changes, for the same reason `Create` runs it.
    abstract Update:
        scopeId: string * actorUserId: string * request: UpdateExternalContactRequest ->
            Async<Result<ExternalContact, ExternalContactError>>

    /// Delete a contact and every consent it carried. Returns the
    /// channels whose consent the deletion discarded, so the caller can
    /// audit what was given up. Idempotent on a contact that is already
    /// gone (`Ok []`): a deletion request honoured twice is still a
    /// deletion request honoured.
    abstract Delete:
        scopeId: string * actorUserId: string * contactId: string ->
            Async<Result<NotificationKind.SinkKind list, ExternalContactError>>

    /// Record a per-channel consent, replacing any prior record for that
    /// channel. The caller supplies the whole `OptInRecord` including
    /// its Article 7 `Source` evidence.
    abstract RecordOptIn:
        scopeId: string *
        actorUserId: string *
        contactId: string *
        channel: NotificationKind.SinkKind *
        record: OptInRecord ->
            Async<Result<ExternalContact, ExternalContactError>>

    /// Withdraw a per-channel consent. The stored record is REMOVED, not
    /// flagged — retaining the evidence of a consent that no longer
    /// exists is the data the withdrawal was meant to end; the audit
    /// trail is where the history belongs. Idempotent: withdrawing a
    /// consent that was never given succeeds, because the recipient's
    /// request was honoured either way.
    /// `reason` rides onto the audit row — `"recipient-request"`,
    /// `"bounce"`, `"admin"`. Required, because a withdrawal whose cause
    /// nobody recorded is the half of the consent history that a
    /// complaint actually turns on.
    abstract WithdrawOptIn:
        scopeId: string * actorUserId: string * contactId: string * channel: NotificationKind.SinkKind * reason: string ->
            Async<Result<ExternalContact, ExternalContactError>>

    /// Stamp `LastInboundUtc`. Driven by a channel's inbound webhook so
    /// a session-window rule (a carrier permitting free-form outbound
    /// only within N hours of an inbound message) has one authoritative
    /// timestamp per contact rather than one per channel adapter.
    abstract RecordInbound:
        scopeId: string * contactId: string * atUtc: DateTime -> Async<Result<ExternalContact, ExternalContactError>>