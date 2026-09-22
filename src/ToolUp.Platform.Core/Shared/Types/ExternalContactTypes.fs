// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 6f.A — external contacts and non-platform recipients ──────
//
// Fable-safe shared types for addressing a recipient who has NO account
// on the platform: a client of a small business, a member of an
// accountability group, a household member who never installed the app,
// a survey respondent. The server-side seams
// (`IExternalContactStore`, the address-book arm, the consent filter)
// live in `ToolUp.Platform.Server`; this file carries only what crosses
// the wire or is rendered by the client.
//
// **Consent is a separate layer OVER preference, not the same store.**
// Phase 441 shipped `UserNotificationPreferences` — a USER's per-category
// choice about notifications they already receive by virtue of having an
// account. Consent is a different fact about a different subject: a legal,
// per-channel record that a person with no account and no preference UI
// agreed to be contacted at all. Merging them would give a recipient with
// no account a preference record nobody can edit, and would let a
// category-level "mute" read as a withdrawal of consent. So the
// external-contact opt-in is checked as the OUTER gate — a send to a
// recipient with no live opt-in is REFUSED before Phase 441's filter runs
// — and 441's types, filter and tests are untouched.
//
// **The opt-in is keyed on the shipped routing key.** `OptIns` is a
// `Map<NotificationKind.SinkKind, OptInRecord>`, so consent speaks the
// same vocabulary as sink registration, the audit payloads' `NotificationKind`
// field and the compose-time uniqueness check. There is deliberately no
// second channel DU to keep in sync.
//
// **Everything here is inert until `ServerConfig.ExternalContactStore` is
// `EnabledExternalContactStore` (GP 13).** The default composition registers
// no store, mounts no route, injects no admin module and adds no consent
// filter; the user-keyed `_platform/contacts/` address-book layout is
// byte-for-byte unchanged (GP 11).

/// Who owns an `ExternalContact` — the subject whose address book it
/// belongs to. Distinct from the storage scope: a personal-mode
/// deployment stores a user's contacts in that user's own scope, while
/// a team stores a shared client list in the team's scope, and both
/// layouts must be expressible without the store guessing from the
/// scope id.
///
/// `[<RequireQualifiedAccess>]` because `User` and `Team` are already
/// taken unqualified in this namespace (`FlagScope`, `KnowledgeScope`).
[<RequireQualifiedAccess>]
type ContactOwner =
    /// Owned by one person — a personal address book. Only that user
    /// (and a platform admin acting in their scope) sees it.
    | User of userId: string
    /// Owned by a team — a shared client / member list. Every member of
    /// the team may read it; Owner/Admin may write it (GP 4).
    | Team of teamId: string

/// Helpers for `ContactOwner`.
module ContactOwner =
    /// Stable wire-format string — `"user:{id}"` / `"team:{id}"`.
    /// Round-trips via `tryParse`. This is also the INDEX value the
    /// entity store keys the per-owner index on, so changing it is a
    /// stored-data change, not a rendering change.
    let toWireString (owner: ContactOwner) : string =
        match owner with
        | ContactOwner.User userId -> "user:" + userId
        | ContactOwner.Team teamId -> "team:" + teamId

    /// Inverse of `toWireString`. `None` for an unrecognised
    /// discriminator.
    let tryParse (wire: string) : ContactOwner option =
        if String.IsNullOrEmpty wire then
            None
        elif wire.StartsWith "user:" then
            Some(ContactOwner.User(wire.Substring 5))
        elif wire.StartsWith "team:" then
            Some(ContactOwner.Team(wire.Substring 5))
        else
            None

/// One recorded consent: the evidence that a named recipient agreed to
/// be contacted on one channel.
///
/// **This is the GDPR Article 7(1) record** — "the controller shall be
/// able to demonstrate that the data subject has consented". `Source`
/// is what demonstrates it, so it is a required string rather than an
/// option: a consent whose provenance nobody recorded cannot be shown
/// to have been given. Conventional forms are
/// `"form-submission:{submissionId}"`, `"signed-form:{documentId}"`,
/// `"manual-admin-entry:{userId}"`, `"imported:{batchId}"`. The SDK
/// does not parse it; it stores it and renders it beside the consent.
///
/// **`ExpiresAt` is a hard bound, not a reminder.** Some jurisdictions
/// and some vendor terms time-box a marketing consent; a contact whose
/// opt-in has expired resolves exactly like one that was never given —
/// `None` from the address book, a refusal from the consent filter.
/// `None` means no expiry was recorded, which is the common case for a
/// transactional consent.
type OptInRecord = {
    /// When the recipient consented. UTC. Informational for audit and
    /// the admin UI; the SDK makes no precision promise beyond the
    /// second (portability rule 6).
    GrantedAt: DateTime
    /// How the consent was obtained — the Article 7 evidence. See the
    /// type's doc comment for the conventional forms.
    Source: string
    /// When the consent lapses, if it was time-boxed. An expired
    /// opt-in is treated as absent.
    ExpiresAt: DateTime option
}

/// Helpers for `OptInRecord`.
module OptInRecord =
    /// A consent granted now with no recorded expiry.
    let granted (source: string) (grantedAt: DateTime) : OptInRecord = {
        GrantedAt = grantedAt
        Source = source
        ExpiresAt = None
    }

    /// `true` when the record is still in force at `now`. A record with
    /// no `ExpiresAt` is always live; one whose `ExpiresAt` has passed
    /// is not.
    let isLiveAt (now: DateTime) (record: OptInRecord) : bool =
        match record.ExpiresAt with
        | None -> true
        | Some expiry -> expiry > now

/// A recipient who is NOT a platform user, held in the owning scope's
/// external address book.
///
/// Carries the three fields every `IEntityStore` entity carries — `Id`,
/// `Type`, `Version` — so it is stored, versioned, indexed and queried
/// by the Phase 19 substrate rather than by a bespoke blob layout.
///
/// **PII lives here and nowhere else.** The transactional envelopes
/// carry a `RecipientId.External contactId` only; the address book
/// resolves it to an address at sink-dispatch time and hands it
/// straight to the vendor. A `contactId` on an audit row or a Redis
/// topic is therefore not an email address, which is the same
/// invariant Phase 6f established for `userId`.
type ExternalContact = {
    /// Stable contact id, assigned by the store at create time. Must
    /// not contain a double underscore (the entity store's object-id
    /// separator).
    Id: string
    /// Entity-type discriminator — always `ExternalContact.EntityType`.
    Type: string
    /// Optimistic-concurrency version, managed by `IEntityStore`.
    Version: int
    /// Human label shown in the admin UI and used as the `DisplayName`
    /// on a resolved `EmailAddress`.
    DisplayName: string
    /// Email address in RFC 5321 form, when one is known.
    OptionalEmailAddress: string option
    /// Phone number in E.164 form (`+`-prefixed, digits only), when one
    /// is known. Used for SMS.
    OptionalPhoneNumber: string option
    /// WhatsApp number in E.164 form, when one is known and differs
    /// from `OptionalPhoneNumber`. Carried here so the WhatsApp arm has
    /// a home before a WhatsApp sink exists; no shipped sink reads it
    /// yet.
    OptionalWhatsAppNumber: string option
    /// Whose address book this is.
    Owner: ContactOwner
    /// Per-channel consent, keyed on the shipped sink routing key. A
    /// channel absent from the map has no consent and is refused.
    OptIns: Map<NotificationKind.SinkKind, OptInRecord>
    /// Free-form grouping labels — `"clients"`, `"family"`,
    /// `"accountability-group"`. The SDK attaches no meaning; the admin
    /// UI filters on them and the store indexes them.
    Tags: string list
    /// When the contact was created. UTC.
    CreatedAt: DateTime
    /// When this contact last sent an inbound message, if ever. Written
    /// by `IExternalContactApi.RecordInbound` from a channel's inbound
    /// webhook. Carried here because a session-window rule (a carrier
    /// that only permits free-form outbound within N hours of an
    /// inbound message) needs one authoritative timestamp per contact
    /// rather than one per channel adapter.
    LastInboundUtc: DateTime option
    /// Operator notes. Never sent anywhere; rendered in the admin UI.
    Notes: string option
}

/// Helpers for `ExternalContact`.
module ExternalContact =
    /// The entity-type discriminator. `"Contact"` is deliberately NOT
    /// used: `UserContact` and the `_platform/contacts/` layout are the
    /// shipped user-keyed address book and mean something else.
    [<Literal>]
    let EntityType = "ExternalContact"

    /// The per-owner index name — `ContactOwner.toWireString`.
    [<Literal>]
    let IndexOwner = "owner"

    /// The email index name, used to refuse a duplicate at create time.
    [<Literal>]
    let IndexEmail = "email"

    /// The phone index name, used to refuse a duplicate at create time.
    [<Literal>]
    let IndexPhone = "phone"

    /// The compound `(owner, tag)` index name.
    [<Literal>]
    let IndexOwnerTag = "owner-tag"

    /// Normalised index value for an absent optional field, so a
    /// contact with no email does not collide with every other contact
    /// with no email on an exact-match index lookup.
    [<Literal>]
    let UnsetIndexValue = "_unset"

    /// Case- and whitespace-normalised email, for indexing and
    /// deduplication. Email local-parts are technically
    /// case-sensitive; in practice no mail provider treats them so, and
    /// a contact list that admits `A@x.com` beside `a@x.com` produces
    /// duplicate sends.
    let normaliseEmail (email: string) : string =
        if String.IsNullOrWhiteSpace email then
            ""
        else
            email.Trim().ToLowerInvariant()

    /// Whitespace-stripped phone number, for indexing and
    /// deduplication. E.164 admits no internal spaces, but hand-entered
    /// numbers routinely carry them.
    let normalisePhone (phone: string) : string =
        if String.IsNullOrWhiteSpace phone then
            ""
        else
            phone.Trim().Replace(" ", "").Replace("-", "")

    /// The `owner` index value for a contact.
    let ownerIndexValue (contact: ExternalContact) : string = ContactOwner.toWireString contact.Owner

    /// The `email` index value for a contact — `UnsetIndexValue` when
    /// no email is recorded.
    let emailIndexValue (contact: ExternalContact) : string =
        match contact.OptionalEmailAddress with
        | Some email when not (String.IsNullOrWhiteSpace email) -> normaliseEmail email
        | _ -> UnsetIndexValue

    /// The `phone` index value for a contact — `UnsetIndexValue` when
    /// no phone is recorded.
    let phoneIndexValue (contact: ExternalContact) : string =
        match contact.OptionalPhoneNumber with
        | Some phone when not (String.IsNullOrWhiteSpace phone) -> normalisePhone phone
        | _ -> UnsetIndexValue

    /// The compound `(owner, tag)` index values — one entry per tag,
    /// plus one bare owner entry so an untagged contact is still
    /// reachable from the compound index.
    let ownerTagIndexValues (contact: ExternalContact) : string list =
        let owner = ownerIndexValue contact

        match contact.Tags with
        | [] -> [ owner ]
        | tags -> owner :: (tags |> List.map (fun tag -> owner + "|" + tag))

    /// The consent record for one channel, `None` when none was given.
    /// Does NOT consider expiry — use `hasLiveOptIn` for the question
    /// the dispatch path asks.
    let optInFor (channel: NotificationKind.SinkKind) (contact: ExternalContact) : OptInRecord option =
        if isNull (box contact.OptIns) then
            None
        else
            contact.OptIns |> Map.tryFind channel

    /// `true` when `contact` has a consent for `channel` that is still
    /// in force at `now`. **This is the predicate the outer consent
    /// gate and the address book both ask** — an expired opt-in reads
    /// exactly like an absent one.
    let hasLiveOptIn (now: DateTime) (channel: NotificationKind.SinkKind) (contact: ExternalContact) : bool =
        match optInFor channel contact with
        | None -> false
        | Some record -> OptInRecord.isLiveAt now record

    /// Record a consent for one channel, replacing any prior record for
    /// it. Does not bump `Version` — the store does that on write.
    let withOptIn
        (channel: NotificationKind.SinkKind)
        (record: OptInRecord)
        (contact: ExternalContact)
        : ExternalContact =
        let existing =
            if isNull (box contact.OptIns) then
                Map.empty
            else
                contact.OptIns

        {
            contact with
                OptIns = existing |> Map.add channel record
        }

    /// Withdraw consent for one channel. **The record is REMOVED, not
    /// flagged** — a withdrawal means the deployment no longer holds a
    /// lawful basis for that channel, and retaining the granted-at
    /// evidence of a consent that no longer exists is the data the
    /// withdrawal was supposed to end. The withdrawal itself is
    /// recorded in the audit trail, which is where the history belongs.
    let withoutOptIn (channel: NotificationKind.SinkKind) (contact: ExternalContact) : ExternalContact =
        let existing =
            if isNull (box contact.OptIns) then
                Map.empty
            else
                contact.OptIns

        {
            contact with
                OptIns = existing |> Map.remove channel
        }

    /// Coerce the reference-typed collections a pre-Phase-6f.A or
    /// hand-edited JSON payload can deserialise as `null`. `Map` and
    /// `list` are NOT null-safe in F# — only `option`'s `None` is — so
    /// every store read path runs this.
    let coerce (contact: ExternalContact) : ExternalContact = {
        contact with
            OptIns =
                (if isNull (box contact.OptIns) then
                     Map.empty
                 else
                     contact.OptIns)
            Tags = (if isNull (box contact.Tags) then [] else contact.Tags)
    }

    /// Reasons `validate` can refuse a contact.
    let private validationErrors (contact: ExternalContact) : string list = [
        if String.IsNullOrWhiteSpace contact.DisplayName then
            "A contact needs a display name."

        if
            contact.OptionalEmailAddress
            |> Option.forall (fun e -> String.IsNullOrWhiteSpace e)
            && contact.OptionalPhoneNumber
               |> Option.forall (fun p -> String.IsNullOrWhiteSpace p)
            && contact.OptionalWhatsAppNumber
               |> Option.forall (fun w -> String.IsNullOrWhiteSpace w)
        then
            "A contact needs at least one of an email address, a phone number or a WhatsApp number — there is nowhere to reach them otherwise."

        match contact.OptionalEmailAddress with
        | Some email when not (String.IsNullOrWhiteSpace email) && not (email.Contains "@") ->
            "That email address is not in a deliverable form."
        | _ -> ()

        match contact.OptionalPhoneNumber with
        | Some phone when
            not (String.IsNullOrWhiteSpace phone)
            && not ((normalisePhone phone).StartsWith "+")
            ->
            "A phone number must be in E.164 form — a leading '+' and the country code."
        | _ -> ()

        match contact.OptionalWhatsAppNumber with
        | Some number when
            not (String.IsNullOrWhiteSpace number)
            && not ((normalisePhone number).StartsWith "+")
            ->
            "A WhatsApp number must be in E.164 form — a leading '+' and the country code."
        | _ -> ()

        if contact.Id.Contains "__" then
            "A contact id must not contain a double underscore."
    ]

    /// Validate a contact's shape. `Ok` carries the contact with its
    /// email and phone normalised, so a caller cannot persist an
    /// un-normalised value that the dedupe index would then miss.
    let validate (contact: ExternalContact) : Result<ExternalContact, string> =
        match validationErrors contact with
        | [] ->
            Ok {
                contact with
                    DisplayName = contact.DisplayName.Trim()
                    OptionalEmailAddress = contact.OptionalEmailAddress |> Option.map normaliseEmail
                    OptionalPhoneNumber = contact.OptionalPhoneNumber |> Option.map normalisePhone
                    OptionalWhatsAppNumber = contact.OptionalWhatsAppNumber |> Option.map normalisePhone
            }
        | first :: _ -> Error first

/// Payload for creating a contact. `Id`, `Type`, `Version`,
/// `CreatedAt` and `LastInboundUtc` are assigned server-side; an
/// `OptIns` map is deliberately absent, because a consent must be
/// recorded through `RecordOptIn` where the audit event fires.
type CreateExternalContactRequest = {
    [<PiiSafe>]
    DisplayName: string
    EmailAddress: string option
    PhoneNumber: string option
    WhatsAppNumber: string option
    /// Whose address book to file it in. The handler refuses an owner
    /// the caller does not control.
    Owner: ContactOwner
    [<PiiSafe>]
    Tags: string list
    Notes: string option
}

/// Payload for updating a contact's editable fields. Consent is NOT
/// editable here — it moves only through `RecordOptIn` /
/// `WithdrawOptIn`, each of which audits.
type UpdateExternalContactRequest = {
    [<PiiSafe>]
    ContactId: string
    [<PiiSafe>]
    DisplayName: string
    EmailAddress: string option
    PhoneNumber: string option
    WhatsAppNumber: string option
    [<PiiSafe>]
    Tags: string list
    Notes: string option
}

/// Payload for recording a consent.
type RecordOptInRequest = {
    [<PiiSafe>]
    ContactId: string
    /// The channel consented to, in `NotificationKind.SinkKind`
    /// wire form (`"Email"` / `"Sms"` / `"Push.WebPush"`).
    [<PiiSafe>]
    Channel: string
    /// The Article 7 evidence — see `OptInRecord.Source`.
    [<PiiSafe>]
    Source: string
    /// Optional hard expiry.
    ExpiresAt: DateTime option
}

/// Payload for withdrawing a consent.
type WithdrawOptInRequest = {
    [<PiiSafe>]
    ContactId: string
    /// The channel, in `NotificationKind.SinkKind` wire form.
    [<PiiSafe>]
    Channel: string
    /// Why — recorded on the audit event. `"recipient-request"`,
    /// `"bounce"`, `"admin"`.
    [<PiiSafe>]
    Reason: string
}

/// Typed failures from the external-contact store. Expected outcomes
/// are values, not exceptions (portability rule 3).
[<RequireQualifiedAccess>]
type ExternalContactError =
    /// No such contact in this scope.
    | NotFound
    /// The contact exists but belongs to another scope — reported the
    /// same way as `NotFound` at the API boundary so the endpoint is
    /// not an oracle for other tenants' contact ids.
    | ScopeMismatch
    /// A contact with the same email or phone already exists in this
    /// owner's address book. Carries the existing contact's id so the
    /// admin UI can offer to open it.
    | Duplicate of existingContactId: string
    /// The contact's shape is not storable — see the message.
    | InvalidShape of reason: string
    /// The channel string did not parse as a `NotificationKind.SinkKind`.
    | UnknownChannel of wire: string
    /// The underlying store failed.
    | StorageFailed of message: string

/// Helpers for `ExternalContactError`.
module ExternalContactError =
    /// The operator-facing sentence for an error. Used by the API
    /// handler, which speaks `Result<_, string>` on the wire.
    let describe (error: ExternalContactError) : string =
        match error with
        | ExternalContactError.NotFound -> "No such contact in this scope."
        | ExternalContactError.ScopeMismatch -> "No such contact in this scope."
        | ExternalContactError.Duplicate existingId ->
            $"A contact with that email or phone number already exists in this address book (id {existingId})."
        | ExternalContactError.InvalidShape reason -> reason
        | ExternalContactError.UnknownChannel wire ->
            $"'{wire}' is not a channel this deployment knows. Expected one of Email, Sms, Push.WebPush, Push.Fcm, Push.Apns."
        | ExternalContactError.StorageFailed message -> $"The contact store could not complete the operation: {message}"

/// Admin-facing API for the external address book. Mounted only when
/// `ServerConfig.ExternalContactStore` opts in.
///
/// **Scope isolation (GP 4).** Every method resolves the caller's scope
/// from `AccessContext` server-side and passes it to
/// `IExternalContactStore`, which refuses a contact belonging to
/// another scope. No method takes a scope parameter, so a caller
/// cannot name one.
///
/// **Reads are open to the team; writes are Owner/Admin.** A team
/// member composing a message needs the contact list, so `ListContacts`
/// and `GetContact` require membership only. Creating a contact,
/// editing it, and above all recording or withdrawing a consent are
/// `TeamRoles.canWriteTeamConfig` acts — a consent record is a legal
/// artefact, and "who may assert that this person agreed" is exactly
/// the authority that should not be ambient.
///
/// `RecordInbound` is the one write a machine caller may make: it is
/// driven by a channel's inbound webhook, not by a person.
type IExternalContactApi = {
    /// Every contact in the caller's scope. Team members may read.
    [<TenantScoped>]
    ListContacts: unit -> Async<Result<ExternalContact list, string>>

    /// One contact by id. Team members may read.
    [<TenantScoped>]
    GetContact: string -> Async<Result<ExternalContact, string>>

    /// File a new contact. Refuses a duplicate email or phone in the
    /// same address book. Owner/Admin.
    [<TenantScoped>]
    [<Audit "Custom:ContactCreated">]
    CreateContact: CreateExternalContactRequest -> Async<Result<ExternalContact, string>>

    /// Edit a contact's reachable fields. Consent is not editable here.
    /// Owner/Admin.
    [<TenantScoped>]
    [<Audit "Custom:ContactUpdated">]
    UpdateContact: UpdateExternalContactRequest -> Async<Result<ExternalContact, string>>

    /// Delete a contact and every consent it carried. Owner/Admin.
    [<TenantScoped>]
    [<Audit "Custom:ContactDeleted">]
    DeleteContact: string -> Async<Result<unit, string>>

    /// Record a per-channel consent with its Article 7 evidence.
    /// Owner/Admin.
    [<TenantScoped>]
    [<Audit "Custom:ContactOptInRecorded">]
    RecordOptIn: RecordOptInRequest -> Async<Result<ExternalContact, string>>

    /// Withdraw a per-channel consent. Idempotent — withdrawing a
    /// consent that was never given succeeds and still audits, because
    /// the recipient's request was honoured either way. Owner/Admin.
    [<TenantScoped>]
    [<Audit "Custom:ContactOptInWithdrawn">]
    WithdrawOptIn: WithdrawOptInRequest -> Async<Result<ExternalContact, string>>

    /// Stamp `LastInboundUtc`. Driven by a channel's inbound webhook so
    /// a session-window rule has an authoritative timestamp to read.
    [<TenantScoped>]
    RecordInbound: string -> Async<Result<ExternalContact, string>>
}

/// Route shape for `IExternalContactApi`.
module ExternalContactApi =
    /// Remoting endpoint prefix — the platform's default
    /// `/api/{type}/{method}` shape, stated explicitly for symmetry
    /// with `ServiceAccountApi.routeBuilder`.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"