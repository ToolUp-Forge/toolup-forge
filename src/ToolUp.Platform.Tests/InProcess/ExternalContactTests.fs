// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ExternalContactTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EntityStore
open ToolUp.Platform.AuditLog
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 6f.A — external contacts ──────────────────────────────────
//
// Three things are under test and only the first is routine:
//
//   1. `IExternalContactStoreContract` bound to the SDK-default
//      entity-backed store.
//   2. The address book's `External` arm: an address is returned only
//      behind a live consent, and an expired consent reads as none.
//   3. **The ORDER of the two send-path filters.** The operator's
//      decision was that consent is the OUTER gate and Phase 441's
//      preference filter the inner one. A test that merely asserted
//      "a contact with no consent is not delivered to" would pass
//      under either order, so the ordering case puts a recording
//      channel BETWEEN the two filters: what that probe receives is
//      what the consent filter let through, and it can only have been
//      narrowed already if consent ran first.

// ─── Doubles ─────────────────────────────────────────────────────────

type private CapturingChannel(name: string) =
    let published = ConcurrentQueue<string * Notification>()

    member _.Name = name
    member _.Published = published |> Seq.toList

    member this.RecipientsSeen =
        this.Published
        |> List.collect (fun (_, n) -> fst (NotificationPreferenceFilter.recipientsOf n))

    interface INotificationChannel with
        member _.Publish(scopeId, notification) = async { published.Enqueue((scopeId, notification)) }
        member _.Subscribe(_, _) = async { return (Guid.NewGuid(): NotificationSubscriptionId) }
        member _.Unsubscribe _ = async { () }

type private CapturingAuditLog() =
    let events = ConcurrentQueue<string * AuditEvent>()

    member _.Events = events |> Seq.toList

    member this.Refusals =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | NotificationDeliveryRefused p -> Some p
            | _ -> None)

    member this.OptInsRecorded =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | ContactOptInRecorded p -> Some p
            | _ -> None)

    member this.Withdrawals =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | ContactOptInWithdrawn p -> Some p
            | _ -> None)

    member this.Created =
        this.Events
        |> List.choose (fun (_, e) ->
            match e with
            | ContactCreated p -> Some p
            | _ -> None)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { events.Enqueue((scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

// ─── Fixtures ────────────────────────────────────────────────────────

let private actor = "admin-1"
let private scope = "team-6fA"

/// A fresh entity-backed contact store over in-memory blob storage,
/// with the `ExternalContact` entity registered exactly as
/// `registerEntityStore` registers it in a composed deployment.
let private freshStore (auditLog: IAuditLog option) : IExternalContactStore =
    let blob = InMemoryBlobStorage() :> IBlobStorage
    let dos = DataObjectStore.DataObjectStore(blob) :> IDataObjectStore
    let registry = EntityRegistry()
    registry.Register ExternalContactStore.registration

    let entities =
        BlobEntityStore(dos, blob, registry, None) :> IEntityStore.IEntityStore

    ExternalContactStore.entityBacked entities auditLog

/// A second, INDEPENDENT `IExternalContactStore` over a dictionary.
///
/// The contract pack is bound to both this and the shipped
/// entity-backed store, for the reason `ILogStoreContract` gives: a rule
/// only the entity store satisfies, or only the fold satisfies, is a
/// rule the next backend will get wrong. Nothing here shares code with
/// the shipped implementation — the duplicate check, the scope rule and
/// the removal-on-withdrawal rule are each re-derived, so a pack that
/// passes against both is pinning the contract and not one store's
/// habits.
type private InMemoryExternalContactStore() =
    let rows = ConcurrentDictionary<string * string, ExternalContact>()

    let inScope (scopeId: string) =
        rows
        |> Seq.filter (fun kv -> fst kv.Key = scopeId)
        |> Seq.map _.Value
        |> Seq.toList

    let duplicateOf (scopeId: string) (owner: ContactOwner) (exceptId: string option) (candidate: ExternalContact) =
        let ownerWire = ContactOwner.toWireString owner

        let clashes (a: string option) (b: string option) =
            match a, b with
            | Some x, Some y when not (String.IsNullOrWhiteSpace x) -> x = y
            | _ -> false

        inScope scopeId
        |> List.filter (fun existing ->
            ContactOwner.toWireString existing.Owner = ownerWire
            && Some existing.Id <> exceptId
            && (clashes existing.OptionalEmailAddress candidate.OptionalEmailAddress
                || clashes existing.OptionalPhoneNumber candidate.OptionalPhoneNumber))
        |> List.tryHead
        |> Option.map _.Id

    let write (scopeId: string) (contact: ExternalContact) =
        let stored = {
            contact with
                Version = contact.Version + 1
        }

        rows[(scopeId, contact.Id)] <- stored
        stored

    let read (scopeId: string) (contactId: string) =
        match rows.TryGetValue((scopeId, contactId)) with
        | true, contact -> Ok contact
        | _ -> Error ExternalContactError.NotFound

    let mutate scopeId contactId (change: ExternalContact -> ExternalContact) = async {
        match read scopeId contactId with
        | Error e -> return Error e
        | Ok existing -> return Ok(write scopeId (change existing))
    }

    interface IExternalContactStore with
        member _.Create(scopeId, _, owner, request) = async {
            let candidate: ExternalContact = {
                Id = Guid.NewGuid().ToString "N"
                Type = ExternalContact.EntityType
                Version = 0
                DisplayName = request.DisplayName
                OptionalEmailAddress = request.EmailAddress
                OptionalPhoneNumber = request.PhoneNumber
                OptionalWhatsAppNumber = request.WhatsAppNumber
                Owner = owner
                OptIns = Map.empty
                Tags = (if isNull (box request.Tags) then [] else request.Tags)
                CreatedAt = DateTime.UtcNow
                LastInboundUtc = None
                Notes = request.Notes
            }

            match ExternalContact.validate candidate with
            | Error reason -> return Error(ExternalContactError.InvalidShape reason)
            | Ok validated ->
                match duplicateOf scopeId owner None validated with
                | Some existingId -> return Error(ExternalContactError.Duplicate existingId)
                | None -> return Ok(write scopeId validated)
        }

        member _.Get(scopeId, contactId) = async { return read scopeId contactId }
        member _.List scopeId = async { return inScope scopeId }

        member _.ListByOwner(scopeId, owner) = async {
            let ownerWire = ContactOwner.toWireString owner

            return
                inScope scopeId
                |> List.filter (fun c -> ContactOwner.toWireString c.Owner = ownerWire)
        }

        member _.Update(scopeId, _, request) = async {
            match read scopeId request.ContactId with
            | Error e -> return Error e
            | Ok existing ->
                let candidate = {
                    existing with
                        DisplayName = request.DisplayName
                        OptionalEmailAddress = request.EmailAddress
                        OptionalPhoneNumber = request.PhoneNumber
                        OptionalWhatsAppNumber = request.WhatsAppNumber
                        Tags = (if isNull (box request.Tags) then [] else request.Tags)
                        Notes = request.Notes
                }

                match ExternalContact.validate candidate with
                | Error reason -> return Error(ExternalContactError.InvalidShape reason)
                | Ok validated ->
                    match duplicateOf scopeId validated.Owner (Some validated.Id) validated with
                    | Some existingId -> return Error(ExternalContactError.Duplicate existingId)
                    | None -> return Ok(write scopeId validated)
        }

        member _.Delete(scopeId, _, contactId) = async {
            match read scopeId contactId with
            | Error ExternalContactError.NotFound -> return Ok []
            | Error e -> return Error e
            | Ok existing ->
                let discarded = existing.OptIns |> Map.toList |> List.map fst
                rows.TryRemove((scopeId, contactId)) |> ignore
                return Ok discarded
        }

        member _.RecordOptIn(scopeId, _, contactId, channel, record) =
            mutate scopeId contactId (ExternalContact.withOptIn channel record)

        member _.WithdrawOptIn(scopeId, _, contactId, channel, _) =
            mutate scopeId contactId (ExternalContact.withoutOptIn channel)

        member _.RecordInbound(scopeId, contactId, atUtc) =
            mutate scopeId contactId (fun c -> { c with LastInboundUtc = Some atUtc })

let private contactRequest (name: string) (email: string option) (phone: string option) = {
    DisplayName = name
    EmailAddress = email
    PhoneNumber = phone
    WhatsAppNumber = None
    Owner = ContactOwner.Team "team-1"
    Tags = []
    Notes = None
}

let private consentNow (source: string) : OptInRecord = {
    GrantedAt = DateTime.UtcNow
    Source = source
    ExpiresAt = None
}

/// File a contact and, optionally, record one consent on it.
let private seedContact
    (store: IExternalContactStore)
    (name: string)
    (email: string option)
    (phone: string option)
    (channel: NotificationKind.SinkKind option)
    : Async<ExternalContact> =
    async {
        let! created = store.Create(scope, actor, ContactOwner.Team "team-1", contactRequest name email phone)

        let contact =
            match created with
            | Ok c -> c
            | Error e -> failtestf "could not seed a contact: %s" (ExternalContactError.describe e)

        match channel with
        | None -> return contact
        | Some channel ->
            let! updated = store.RecordOptIn(scope, actor, contact.Id, channel, consentNow "manual-admin-entry")

            return
                match updated with
                | Ok c -> c
                | Error e -> failtestf "could not seed a consent: %s" (ExternalContactError.describe e)
    }

let private smsTo (recipients: RecipientId list) : Notification =
    TransactionalSms {
        Recipients = recipients
        Body = "Your appointment is tomorrow at 10."
        CorrelationId = Some "corr-6fA"
    }

let private emailTo (recipients: RecipientId list) : Notification =
    TransactionalEmail {
        Recipients = recipients
        Content = InlineEmail("Subject", "Body", None)
        CorrelationId = Some "corr-6fA"
    }

// ─── The contract pack binding ───────────────────────────────────────

let private contractBinding: IExternalContactStoreContract.ExternalContactBinding = {
    Factory = fun () -> freshStore None
    Scopes =
        fun () ->
            let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
            $"team-a-{suffix}", $"team-b-{suffix}"
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 6f.A — external contacts" [

        IExternalContactStoreContract.tests "EntityBackedExternalContactStore" contractBinding

        IExternalContactStoreContract.tests "InMemoryExternalContactStore (fake)" {
            contractBinding with
                Factory = fun () -> InMemoryExternalContactStore() :> IExternalContactStore
        }

        testList "audit trail" [
            testCaseAsync "the store, not the handler, emits the contact lifecycle rows"
            <| async {
                // The store is the chokepoint every caller passes
                // through; an audit emitted per caller is one some
                // caller will forget.
                let audit = CapturingAuditLog()
                let store = freshStore (Some(audit :> IAuditLog))

                let! contact =
                    seedContact store "Audited" (Some "audited@example.com") None (Some NotificationKind.SinkKind.Email)

                Expect.equal (List.length audit.Created) 1 "one ContactCreated row"
                Expect.equal (List.head audit.Created).ContactId contact.Id "naming the contact"

                Expect.equal (List.length audit.OptInsRecorded) 1 "one ContactOptInRecorded row"
                let recorded = List.head audit.OptInsRecorded
                Expect.equal recorded.Channel "Email" "on the channel consented to"

                Expect.equal
                    recorded.Source
                    "manual-admin-entry"
                    "carrying the Article 7 evidence verbatim, which is the whole point of the row"

                let! _ =
                    store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, "recipient-request")

                Expect.equal (List.length audit.Withdrawals) 1 "one ContactOptInWithdrawn row"
                Expect.equal (List.head audit.Withdrawals).Reason "recipient-request" "carrying why"
            }

            testCaseAsync "a withdrawal is audited even when there was nothing to withdraw"
            <| async {
                // The row records that the request was made and
                // honoured, which is the fact a recipient's complaint
                // turns on — not whether a record happened to exist.
                let audit = CapturingAuditLog()
                let store = freshStore (Some(audit :> IAuditLog))
                let! contact = seedContact store "Never opted in" (Some "never@example.com") None None

                let! result = store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, "bounce")

                Expect.isOk result "the withdrawal succeeds"
                Expect.equal (List.length audit.Withdrawals) 1 "and is recorded"
            }
        ]

        testList "address book — the External arm" [
            testCaseAsync "an address resolves ONLY behind a live consent"
            <| async {
                let store = freshStore None
                let blob = InMemoryBlobStorage() :> IBlobStorage

                let book =
                    NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None, Some store)
                    :> INotificationAddressBook

                let! unconsented = seedContact store "No consent" (Some "no@example.com") None None
                let! resolved = book.ResolveEmail(RecipientId.External unconsented.Id, scope)

                Expect.isNone
                    resolved
                    "an address on file is not permission to use it — that asymmetry is the whole phase"

                let! consented =
                    seedContact store "Consented" (Some "yes@example.com") None (Some NotificationKind.SinkKind.Email)

                let! resolved = book.ResolveEmail(RecipientId.External consented.Id, scope)

                match resolved with
                | Some address ->
                    Expect.equal address.Address "yes@example.com" "the consented address resolves"
                    Expect.equal address.DisplayName (Some "Consented") "with the contact's display name"
                | None -> failtest "expected the consented address to resolve"
            }

            testCaseAsync "consent is per channel: an SMS opt-in does not unlock the email address"
            <| async {
                let store = freshStore None
                let blob = InMemoryBlobStorage() :> IBlobStorage

                let book =
                    NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None, Some store)
                    :> INotificationAddressBook

                let! contact =
                    seedContact
                        store
                        "SMS only"
                        (Some "smsonly@example.com")
                        (Some "+447700900401")
                        (Some NotificationKind.SinkKind.Sms)

                let! phone = book.ResolvePhone(RecipientId.External contact.Id, scope)
                Expect.isSome phone "the consented channel resolves"

                let! email = book.ResolveEmail(RecipientId.External contact.Id, scope)
                Expect.isNone email "the unconsented one does not, though the address is right there"
            }

            testCaseAsync "with NO external store composed, every external recipient resolves to nothing"
            <| async {
                // A deployment on the default `NoExternalContactStore`
                // holds no consent record, and no consent record means
                // no lawful basis. `None` is the correct answer, not a
                // degraded one.
                let blob = InMemoryBlobStorage() :> IBlobStorage

                let book =
                    NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None) :> INotificationAddressBook

                let! email = book.ResolveEmail(RecipientId.External "anything", scope)
                Expect.isNone email "no store, no address"

                let! phone = book.ResolvePhone(RecipientId.External "anything", scope)
                Expect.isNone phone "on any channel"
            }

            testCaseAsync "an external recipient never has push tokens"
            <| async {
                let store = freshStore None
                let blob = InMemoryBlobStorage() :> IBlobStorage

                let book =
                    NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None, Some store)
                    :> INotificationAddressBook

                let! contact =
                    seedContact
                        store
                        "Pushy"
                        (Some "pushy@example.com")
                        None
                        (Some(NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush))

                let! tokens = book.ResolvePushTokens(RecipientId.External contact.Id, scope)

                Expect.isEmpty
                    tokens
                    "push is a channel you opt into from inside an app you installed; a contact who never installed it has no token, consent or no consent"
            }

            testCaseAsync "the User arm is untouched by the widening"
            <| async {
                let blob = InMemoryBlobStorage() :> IBlobStorage

                let book =
                    NotificationAddressBook.BlobBackedNotificationAddressBook(blob, None) :> INotificationAddressBook

                let! _ =
                    NotificationAddressBook.saveContact blob scope {
                        UserId = "alice"
                        Email =
                            Some {
                                Address = "alice@example.com"
                                DisplayName = Some "Alice"
                            }
                        Phone = None
                        PushTokens = []
                    }

                let! resolved = book.ResolveEmail(RecipientId.User "alice", scope)

                Expect.equal
                    (resolved |> Option.map _.Address)
                    (Some "alice@example.com")
                    "same blob path, same shape, same answer as before Phase 6f.A"
            }
        ]

        testList "the consent gate" [
            testCaseAsync "a recipient with no consent is REFUSED, and the refusal is audited"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(
                        inner,
                        Some store,
                        Some(audit :> IAuditLog),
                        SilentLogger(),
                        fun () -> DateTime.UtcNow
                    )
                    :> INotificationChannel

                let! contact = seedContact store "Unconsented" None (Some "+447700900501") None

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])

                Expect.isEmpty inner.Published "an envelope whose every recipient was refused is not published at all"

                match audit.Refusals with
                | [ refusal ] ->
                    Expect.equal refusal.Reason "no_opt_in" "the reason names the missing consent"
                    Expect.equal refusal.ContactIds [ contact.Id ] "and the row names who was refused"
                    Expect.equal refusal.NotificationKind "Sms" "on the channel that was refused"
                    Expect.equal refusal.CorrelationId (Some "corr-6fA") "carrying the envelope's correlation id"

                    Expect.equal
                        (List.length refusal.RecipientHashes)
                        1
                        "with the PII-free correlation hash the dispatcher's own rows use"
                | other -> failtestf "expected exactly one refusal row, got %A" other
            }

            testCaseAsync "a refusal is NOT a silent skip — the two are different audit cases"
            <| async {
                // A deployment choosing not to send and a deployment
                // having no lawful basis to send call for different
                // action from an operator: one is a setting, the other
                // is a consent to go and obtain.
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                let! contact = seedContact store "Unconsented" None (Some "+447700900502") None
                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])

                let skips =
                    audit.Events
                    |> List.filter (fun (_, e) ->
                        match e with
                        | NotificationSilentlySkipped _ -> true
                        | _ -> false)

                Expect.isEmpty skips "nothing was recorded as a silent skip"
                Expect.equal (List.length audit.Refusals) 1 "it was recorded as a refusal"
            }

            testCaseAsync "a consenting recipient is delivered to"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                let! contact =
                    seedContact store "Consented" None (Some "+447700900503") (Some NotificationKind.SinkKind.Sms)

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])

                Expect.equal (List.length inner.Published) 1 "the envelope passes through"
                Expect.isEmpty audit.Refusals "and nothing is refused"
            }

            testCaseAsync "a mixed envelope keeps the consenting recipients and drops the rest"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                let! yes = seedContact store "Yes" None (Some "+447700900504") (Some NotificationKind.SinkKind.Sms)

                let! no = seedContact store "No" None (Some "+447700900505") None

                do!
                    filter.Publish(
                        scope,
                        smsTo [
                            RecipientId.User "alice"
                            RecipientId.External yes.Id
                            RecipientId.External no.Id
                        ]
                    )

                Expect.equal
                    inner.RecipientsSeen
                    [ RecipientId.User "alice"; RecipientId.External yes.Id ]
                    "platform users pass through untouched; only the unconsented external is dropped"

                Expect.equal (List.head audit.Refusals).ContactIds [ no.Id ] "and only it is refused"
            }

            testCaseAsync "the SMS consent does not admit an email send"
            <| async {
                // The phase's own acceptance criterion: opt in to SMS
                // only, then watch the email channel refuse.
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                let! contact =
                    seedContact
                        store
                        "Sms only"
                        (Some "smsonly@example.com")
                        (Some "+447700900506")
                        (Some NotificationKind.SinkKind.Sms)

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "the SMS is delivered"

                do! filter.Publish(scope, emailTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "the email is not"

                match audit.Refusals with
                | [ refusal ] -> Expect.equal refusal.NotificationKind "Email" "and the refusal names the email channel"
                | other -> failtestf "expected one refusal, got %A" other
            }

            testCaseAsync "withdrawing the consent refuses the next send"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                let! contact =
                    seedContact store "Changed mind" None (Some "+447700900507") (Some NotificationKind.SinkKind.Sms)

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "delivered while the consent stood"

                let! _ =
                    store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Sms, "recipient-request")

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "and refused after it was withdrawn"
                Expect.equal (List.head audit.Refusals).Reason "no_opt_in" "for want of a consent"
            }

            testCaseAsync "an EXPIRED consent refuses exactly like an absent one"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"
                let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
                let clock = ref (expiry.AddDays -1.0)

                let filter =
                    ExternalContactConsentFilter(
                        inner,
                        Some store,
                        Some(audit :> IAuditLog),
                        SilentLogger(),
                        fun () -> clock.Value
                    )
                    :> INotificationChannel

                let! contact = seedContact store "Lapsing" None (Some "+447700900508") None

                let! _ =
                    store.RecordOptIn(
                        scope,
                        actor,
                        contact.Id,
                        NotificationKind.SinkKind.Sms,
                        {
                            consentNow "signed-form:7" with
                                ExpiresAt = Some expiry
                        }
                    )

                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "delivered inside the consent's window"

                clock.Value <- expiry.AddDays 1.0
                do! filter.Publish(scope, smsTo [ RecipientId.External contact.Id ])
                Expect.equal (List.length inner.Published) 1 "and refused once it lapsed"
                Expect.equal (List.length audit.Refusals) 1 "with a refusal row"
            }

            testCaseAsync "fail closed: a store that errors refuses rather than admits"
            <| async {
                // Deliberately opposite to Phase 441's fail-open rule.
                // A preference lookup that fails must not stop mail the
                // user never objected to; a consent lookup that fails
                // cannot be read as consent.
                let failing =
                    { new IExternalContactStore with
                        member _.Create(_, _, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }

                        member _.Get(_, _) = async { return Error(ExternalContactError.StorageFailed "disk full") }
                        member _.List _ = async { return [] }
                        member _.ListByOwner(_, _) = async { return [] }

                        member _.Update(_, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }

                        member _.Delete(_, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }

                        member _.RecordOptIn(_, _, _, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }

                        member _.WithdrawOptIn(_, _, _, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }

                        member _.RecordInbound(_, _, _) = async {
                            return Error(ExternalContactError.StorageFailed "disk full")
                        }
                    }

                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some failing, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                do! filter.Publish(scope, smsTo [ RecipientId.External "whoever" ])

                Expect.isEmpty inner.Published "an unreadable consent is not a consent"
                Expect.equal (List.length audit.Refusals) 1 "and the refusal is recorded"
            }

            testCaseAsync "an envelope with no external recipient costs nothing and passes through"
            <| async {
                let store = freshStore None
                let audit = CapturingAuditLog()
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                do! filter.Publish(scope, smsTo [ RecipientId.User "alice"; RecipientId.User "bob" ])

                Expect.equal (List.length inner.Published) 1 "published"

                Expect.equal
                    inner.RecipientsSeen
                    [ RecipientId.User "alice"; RecipientId.User "bob" ]
                    "unchanged, recipient for recipient"

                Expect.isEmpty audit.Events "and nothing audited"
            }

            testCaseAsync "a non-transactional notification is not gated at all"
            <| async {
                let store = freshStore None
                let inner = CapturingChannel "inner"

                let filter =
                    ExternalContactConsentFilter(inner, Some store, None, SilentLogger()) :> INotificationChannel

                do! filter.Publish(scope, SystemMessage(SystemMessageLevel.Info, "hello"))
                Expect.equal (List.length inner.Published) 1 "SSE-bound kinds carry no recipient list to gate"
            }
        ]

        testList "filter order — consent is OUTER, preference is INNER" [
            testCaseAsync "a refused external never reaches the preference filter"
            <| async {
                // THE ordering case. The probe sits BETWEEN the two
                // filters, so what it receives is what the consent
                // filter let through. If preference ran first, the
                // probe would see the refused contact still on the
                // envelope; it can only be absent if consent ran first.
                let contacts = freshStore None
                let audit = CapturingAuditLog()
                let sink = CapturingChannel "sink"
                let probe = CapturingChannel "between-the-filters"

                let blob = InMemoryBlobStorage() :> IBlobStorage
                let prefStore = BlobNotificationPreferenceStore.create blob None

                let categories = [ NotificationCategory.create "reports.summary" "Report summaries" ]

                // Inner: Phase 441, untouched.
                let preference =
                    NotificationPreferenceFilter(sink, prefStore, categories, Some(audit :> IAuditLog), SilentLogger())
                    :> INotificationChannel

                // The probe, then the outer consent gate over it.
                let probeOverPreference =
                    { new INotificationChannel with
                        member _.Publish(scopeId, notification) = async {
                            do! (probe :> INotificationChannel).Publish(scopeId, notification)
                            do! preference.Publish(scopeId, notification)
                        }

                        member _.Subscribe(s, h) = preference.Subscribe(s, h)
                        member _.Unsubscribe id = preference.Unsubscribe id
                    }

                let consent =
                    ExternalContactConsentFilter(
                        probeOverPreference,
                        Some contacts,
                        Some(audit :> IAuditLog),
                        SilentLogger()
                    )
                    :> INotificationChannel

                // Alice has muted the category; the contact has no consent.
                do!
                    prefStore.SavePreferences(
                        scope,
                        "alice",
                        {
                            Channels = [
                                {
                                    Category = "reports.summary"
                                    Channel = PreferenceChannel.Email
                                    Delivery = Muted
                                }
                            ]
                            QuietHours = None
                        }
                    )
                    |> Async.Ignore

                let! refused = seedContact contacts "Unconsented" (Some "refused@example.com") None None

                let! consented =
                    seedContact contacts "Consented" (Some "ok@example.com") None (Some NotificationKind.SinkKind.Email)

                do!
                    NotificationCategoryScope.publish
                        consent
                        "reports.summary"
                        scope
                        (emailTo [
                            RecipientId.User "alice"
                            RecipientId.External refused.Id
                            RecipientId.External consented.Id
                        ])

                // The probe is between the filters: consent has run,
                // preference has not.
                Expect.equal
                    probe.RecipientsSeen
                    [ RecipientId.User "alice"; RecipientId.External consented.Id ]
                    "the refused contact was already gone before the preference filter saw the envelope"

                // The sink is below both: preference has run too.
                Expect.equal
                    sink.RecipientsSeen
                    [ RecipientId.External consented.Id ]
                    "and alice's mute is applied only after that, by the inner filter"

                Expect.equal (List.length audit.Refusals) 1 "one consent refusal"
                Expect.equal (List.head audit.Refusals).ContactIds [ refused.Id ] "naming the contact"

                let mutes =
                    audit.Events
                    |> List.choose (fun (_, e) ->
                        match e with
                        | NotificationSilentlySkipped p when p.Reason = NotificationPreferenceFilter.MutedReason ->
                            Some p
                        | _ -> None)

                Expect.equal (List.length mutes) 1 "and one preference mute, from the inner filter"
            }

            testCaseAsync "the preference filter does not read a preference record for an external recipient"
            <| async {
                // An external contact has no account, no preference
                // record and no preference UI. Passing one through the
                // per-user lookup would read an empty record and return
                // the same answer at the cost of a store round-trip per
                // recipient, and would key a held copy on a user id
                // that does not exist.
                let reads = ConcurrentQueue<string>()
                let blob = InMemoryBlobStorage() :> IBlobStorage
                let backing = BlobNotificationPreferenceStore.create blob None

                let spying =
                    { new INotificationPreferenceStore with
                        member _.GetPreferences(s, u) =
                            reads.Enqueue u
                            backing.GetPreferences(s, u)

                        member _.SavePreferences(s, u, p) = backing.SavePreferences(s, u, p)
                        member _.Enqueue(s, i) = backing.Enqueue(s, i)
                        member _.ListPending(s, u) = backing.ListPending(s, u)
                        member _.Remove(s, u, ids) = backing.Remove(s, u, ids)
                        member _.ListPendingUsers s = backing.ListPendingUsers s
                        member _.ListScopesWithPending() = backing.ListScopesWithPending()
                        member _.GetDigestWatermarks(s, u) = backing.GetDigestWatermarks(s, u)
                        member _.SetDigestWatermark(s, u, f, t) = backing.SetDigestWatermark(s, u, f, t)
                    }

                let sink = CapturingChannel "sink"
                let categories = [ NotificationCategory.create "reports.summary" "Report summaries" ]

                let preference =
                    NotificationPreferenceFilter(sink, spying, categories, None, SilentLogger()) :> INotificationChannel

                do!
                    NotificationCategoryScope.publish
                        preference
                        "reports.summary"
                        scope
                        (emailTo [ RecipientId.User "alice"; RecipientId.External "contact-1" ])

                Expect.equal (reads |> Seq.toList) [ "alice" ] "only the platform user's record was read"

                Expect.equal
                    sink.RecipientsSeen
                    [ RecipientId.User "alice"; RecipientId.External "contact-1" ]
                    "and the external rides the narrowing through untouched"
            }
        ]
    ]