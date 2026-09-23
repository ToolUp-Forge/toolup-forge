# External contacts and non-platform recipients

*Phase 6f.A. Server: `IExternalContactStore`, `ExternalContactConsentFilter`,
`ExternalContactApiHandler`. Core: `ExternalContactTypes.fs`, `RecipientId`. Client:
`ExternalContactManagerUI`.*

Transactional notifications (Phase 6f) address platform users: an envelope carries recipient ids,
`INotificationAddressBook` resolves them to an address at sink-dispatch time, and PII never crosses
the channel wire. That model has one thing it cannot express — a recipient who has **no account on
the deployment at all**.

That recipient is common and unremarkable: a client of a small business receiving an appointment
reminder, a member of an accountability group getting a nudge, a household member who never
installed the app, a survey respondent. They need the same dispatch substrate. What they do *not*
have is an account, a preference record, or any prior relationship that could be read as agreement
to be contacted.

So the substrate splits into two halves, and the second half is the one that matters:

1. **An address book** — `ExternalContact`, stored per scope, holding a display name and up to
   three ways to reach someone.
2. **A consent record** — per channel, with the evidence of how it was obtained, and a gate on the
   send path that refuses rather than drops when it is missing.

**An address on file is not permission to use it.** Everything below follows from that sentence.

## Turning it on

Two deliberate acts, server and client:

```powershell
$env:TOOLUP_ENTITY_STORE = "enabled"          # the contact store is entity-backed
$env:TOOLUP_EXTERNAL_CONTACT_STORE = "enabled"
```

or in code:

```fsharp skip=fragment
let serverConfig = {
    ServerConfig.defaults with
        EntityStore = EnabledEntityStore
        ExternalContactStore = EnabledExternalContactStore
}

let clientConfig = {
    ClientConfig.create handlers with
        ExternalContactManager = DefaultExternalContactManager
}
```

The default is `NoExternalContactStore`. On it, no entity is registered, no store is in DI, no
consent filter wraps the notification channel, and no route is mounted — the composition is
byte-for-byte what it was (GP 11 + GP 13). The shipped user-keyed address book at
`_platform/contacts/{scopeId}/{userId}.json` is untouched either way.

Enabling the contact store without the entity store is **refused at compose time**, naming both
settings. The contact store is entity-backed and the mismatch would otherwise surface as a failure
the first time someone tried to file a contact.

## Addressing: `RecipientId`

The three transactional envelopes widened from `RecipientUserIds: string list` to
`Recipients: RecipientId list`:

```fsharp skip=fragment
[<RequireQualifiedAccess>]
type RecipientId =
    | User of userId: string
    | External of contactId: string
```

A `userId` and a `contactId` are both opaque strings, and resolving one as the other is a
team-isolation bug no test would catch — they are different rows, in different stores, under
different rules. The DU forces every resolution site to disambiguate at the type level.

Migrating a call site is mechanical: wrap each id in `RecipientId.User`, or the whole list in
`RecipientId.ofUserIds`.

```fsharp skip=fragment
// before Phase 6f.A
TransactionalEmail {
    RecipientUserIds = [ userId ]
    Content = InlineEmail("Subject", "Body", None)
    CorrelationId = None
}

// after
TransactionalEmail {
    Recipients = [ RecipientId.User userId ]
    Content = InlineEmail("Subject", "Body", None)
    CorrelationId = None
}
```

Audit payloads still carry `RecipientUserIds: string list`, and a `User` recipient still renders as
its **bare** id there (`RecipientId.toAuditString`), so existing audit rows and the
`SHA256(recipient)[..8]` correlation hashes derived from them are unchanged. An external recipient
renders as `external:{contactId}`, because a contact id and a user id are different namespaces and
a reader must be able to tell which one a row names.

## The opt-in model

Consent is keyed on the **shipped routing key**, `NotificationKind.SinkKind`:

```fsharp skip=fragment
type OptInRecord = {
    GrantedAt: DateTime
    Source: string
    ExpiresAt: DateTime option
}

// on ExternalContact
OptIns: Map<NotificationKind.SinkKind, OptInRecord>
```

There is deliberately no second channel vocabulary. `Email`, `Sms` and `Push of PushVariant` are
what sinks register under, what the compose-time uniqueness check keys on, and what the audit rows
carry — a parallel `NotificationChannel` DU would be one more thing to keep in sync and one more
place for the two to disagree.

Three rules about the record:

- **A newly filed contact consents to nothing.** `Create` cannot seed an opt-in. This is the safe
  default and the legally correct one: consent is something a person gives, never something a
  contact record is born holding. A bulk import cannot manufacture a lawful basis.
- **`Source` is required, not optional.** It is the GDPR **Article 7(1)** evidence — "the controller
  shall be able to demonstrate that the data subject has consented". A consent whose provenance
  nobody recorded cannot be demonstrated, so both the admin UI and the API handler refuse a blank
  one. Conventional forms: `"form-submission:{submissionId}"`, `"signed-form:{documentId}"`,
  `"manual-admin-entry:{userId}"`, `"imported:{batchId}"`. The SDK stores it verbatim and never
  parses it.
- **`ExpiresAt` is a hard bound, not a reminder.** An expired opt-in resolves exactly like one that
  was never given — `None` from the address book, a refusal from the consent filter. `None` means
  no expiry was recorded, the common case for a transactional consent.

**Withdrawal removes the stored record**, it does not flag it. Retaining the granted-at evidence of
a consent that no longer exists is precisely the data the withdrawal was meant to end; the audit
trail is where the history belongs. Withdrawal is idempotent: honouring the same request twice is
still honouring it, and it audits either way, because the fact a complaint turns on is that the
request was made and honoured.

## Consent over preference — the layering with Phase 441

Phase 441 shipped per-user notification preferences: a category × channel matrix, digest
frequencies and quiet hours that a signed-in person sets for themselves. It would be tempting to
store consent in the same place. **It is a separate layer over preference, not the same store.**

They are facts about different subjects:

| | Preference (441) | Consent (6f.A) |
|---|---|---|
| Subject | a user with an account | a person with no account |
| Nature | a choice about notifications they already receive | the lawful basis for contacting them at all |
| Set by | the person themselves, in a preference centre | an operator, recording evidence of an agreement |
| Absent means | deliver (the prior behaviour) | **refuse** |
| Failure mode | fail **open** | fail **closed** |

Merged, a category-level "mute" would read as a withdrawal of consent, a withdrawal would read as a
mute, and the one that matters legally would be the one easiest to lose.

So they compose as two decorators on the notification channel, consent outermost:

```
ExternalContactConsentFilter      (consent   — outer)
  └─ NotificationPreferenceFilter (preference — inner, Phase 441, untouched)
       └─ DispatchingNotificationChannel
            └─ TransactionalDispatcher → INotificationSink
```

The preference filter reads a preference record for platform users and for nobody else: an external
contact has no account and no record to read, so it rides every narrowing through untouched. The
ordering is pinned by a test that places a recording channel *between* the two filters — a test
asserting only "an unconsented contact is not delivered to" would pass under either order.

The two failure postures are opposite on purpose. A preference lookup that errors **delivers**,
because dropping mail over an unreadable preference blob turns an infrastructure blip into lost
password resets. A consent lookup that errors **refuses**, because an unreadable consent is not a
consent.

## What a refusal looks like

`NotificationDeliveryRefused` is a distinct audit case from the shipped
`NotificationSilentlySkipped`, and the distinction is the point. A silent skip means the deployment
chose not to send on that kind; nobody is owed an explanation. A refusal means the deployment had no
lawful basis to contact this person — a fact a regulator, an operator and the recipient each have an
interest in. They call for different action: one is a setting, the other is a consent to go and
obtain.

The row carries the sink kind, the reason (`"no_opt_in"`), the PII-free recipient hashes the
dispatcher's own rows use, the contact ids an operator needs in order to act, and the envelope's
correlation id. An envelope whose every recipient was refused is **not published at all** —
publishing an empty one would reach the sinks, which would skip it as "no addressable recipients"
and hide a refusal behind a routine skip.

The contact lifecycle is audited too — `ContactCreated`, `ContactUpdated`, `ContactDeleted`,
`ContactOptInRecorded`, `ContactOptInWithdrawn` — and those rows are emitted by the **store**, not
by the API handler. The store is the chokepoint every caller passes through (a handler, a job, an
intake pipeline, an inbound webhook); an audit emitted per caller is an audit some caller will
forget.

## Storage and dedupe

`ExternalContact` is an `IEntityStore` entity, not a bespoke blob layout, for three reasons that all
matter here: versioning (a consent is a legal artefact, so "what did this consent look like in
March" should be answerable from storage), the email and phone indexes that back the duplicate
check, and the per-scope container that carries GP 4 without a path template re-implementing it.

Records land at `{scopeId}/objects/_entity__ExternalContact__{id}/v{N}.json` with index refs under
`{scopeId}/entities/_indexes/ExternalContact/`.

**The duplicate check is not a nicety.** Two rows for one person means two sends, and a withdrawal
that silences only one of them. `Create` and `Update` both refuse a contact whose normalised email
or phone already exists in the same owner's address book, naming the existing contact's id.
Normalisation is case- and whitespace-folding: `A@x.com` beside `a@x.com`, and `+44 7700 900123`
beside `+447700900123`, are each one entry.

There is deliberately **no tag index**. A tag is multi-valued per contact and an `EntityIndex`
extracts one string per entity, so a tag index would have to pick a tag — worse than none. Tag
filtering runs in memory over `ListByOwner`, whose cardinality is one owner's address book.

## Authority

Reads (`ListContacts`, `GetContact`) need team membership: a team member composing a message needs
the contact list. Writes need `TeamRoles.canWriteTeamConfig` — Owner or Admin — because "who may
assert that this person consented" is exactly the authority that should not be ambient. In a
personal (non-team) scope the caller owns the scope outright and there is no role to check.

Every member except one refuses a machine caller (`ClaimBearer`): a machine credential that can
record a consent can manufacture the lawful basis for its own sends. The exception is
`RecordInbound`, which stamps `LastInboundUtc` from a channel's inbound webhook — a machine caller
by construction, writing one timestamp that grants no authority. That timestamp exists so a
session-window rule (a carrier permitting free-form outbound only within N hours of an inbound
message) has one authoritative value per contact rather than one per channel adapter.

The owner a contact is filed under is derived server-side from the caller's own subject and never
taken from the request: a request that could name an owner could file a contact into someone else's
address book.

## Substituting the store

`IExternalContactStore` satisfies the six portability rules, so a deployment with an existing CRM
binds its own implementation. Validate it against `IExternalContactStoreContract` — the same pack
the shipped store is held to. A deployment swapping this store is swapping the place a legal consent
record lives, so the rules that pack pins (the duplicate rule, the scope rule, removal-on-withdrawal,
expiry-reads-as-absent) are not conveniences.
