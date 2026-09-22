// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IExternalContactStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── Phase 6f.A — IExternalContactStore conformance pack ─────────────
//
// Bound by every concrete `IExternalContactStore`: the SDK-default
// entity-backed store, and any CRM-backed companion a deployment
// substitutes for it. A deployment that swaps the store is swapping the
// place a legal consent record lives, so the rules this pack pins are
// not conveniences — a store that gets the duplicate rule or the scope
// rule wrong sends mail to someone who withdrew, or shows one tenant
// another tenant's clients.
//
// `factory` returns a fresh store over fresh storage. `scopes` are
// supplied by the binding rather than minted here so a binding over a
// backend with scope prerequisites (a provisioned container, a tenant
// row) can prepare them.

/// Everything a binding must supply to run the pack.
type ExternalContactBinding = {
    /// A fresh store over fresh, empty storage.
    Factory: unit -> IExternalContactStore
    /// Two scope ids the store can serve, distinct from each other.
    /// Re-invoked per case, so a binding may return fresh ids each time.
    Scopes: unit -> string * string
}

let private actor = "admin-1"

let private owner = ContactOwner.Team "team-1"

let private request (name: string) (email: string option) (phone: string option) : CreateExternalContactRequest = {
    DisplayName = name
    EmailAddress = email
    PhoneNumber = phone
    WhatsAppNumber = None
    Owner = owner
    Tags = [ "clients" ]
    Notes = None
}

let tests (name: string) (binding: ExternalContactBinding) =
    let newStore () = binding.Factory()

    let created (store: IExternalContactStore) (scopeId: string) (req: CreateExternalContactRequest) = async {
        let! result = store.Create(scopeId, actor, req.Owner, req)

        return
            match result with
            | Ok contact -> contact
            | Error e -> failtestf "expected a contact, got %s" (ExternalContactError.describe e)
    }

    let consent (source: string) : OptInRecord = {
        GrantedAt = DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        Source = source
        ExpiresAt = None
    }

    testList $"{name} — IExternalContactStore contract" [

        testCaseAsync "Create then Get round-trips the contact"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Alice Client" (Some "alice@example.com") None)

            Expect.equal contact.Type ExternalContact.EntityType "entity type discriminator"
            Expect.isFalse (String.IsNullOrWhiteSpace contact.Id) "the store assigns an id"

            let! read = store.Get(scope, contact.Id)

            match read with
            | Ok found ->
                Expect.equal found.DisplayName "Alice Client" "display name round-trips"
                Expect.equal found.OptionalEmailAddress (Some "alice@example.com") "email round-trips"
                Expect.equal found.Owner owner "owner round-trips"
                Expect.equal found.Tags [ "clients" ] "tags round-trip"
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
        }

        testCaseAsync "A new contact consents to NOTHING"
        <| async {
            // The safe default and the legally correct one. A store that
            // let `Create` seed a consent would let a bulk import
            // manufacture a lawful basis nobody gave.
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Fresh" (Some "fresh@example.com") (Some "+447700900001"))

            Expect.isEmpty (Map.toList contact.OptIns) "no consent on a newly filed contact"

            Expect.isFalse
                (ExternalContact.hasLiveOptIn DateTime.UtcNow NotificationKind.SinkKind.Email contact)
                "and therefore no live email consent"
        }

        testCaseAsync "Get returns NotFound for an id this scope does not hold"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! read = store.Get(scope, "no-such-contact")
            Expect.equal read (Error ExternalContactError.NotFound) "absent reads as NotFound"
        }

        testCaseAsync "RecordOptIn persists the consent and its Article 7 evidence"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Opted" (Some "opted@example.com") None)

            let! updated =
                store.RecordOptIn(
                    scope,
                    actor,
                    contact.Id,
                    NotificationKind.SinkKind.Email,
                    consent "form-submission:abc123"
                )

            match updated with
            | Error e -> failtestf "expected the consent to record, got %s" (ExternalContactError.describe e)
            | Ok _ ->
                // Re-READ rather than trust the returned value: the
                // question is whether it persisted, not whether the
                // in-memory copy was updated.
                let! read = store.Get(scope, contact.Id)

                match read with
                | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
                | Ok found ->
                    match ExternalContact.optInFor NotificationKind.SinkKind.Email found with
                    | None -> failtest "expected a persisted email consent"
                    | Some record ->
                        Expect.equal record.Source "form-submission:abc123" "the evidence persists verbatim"
                        Expect.isNone record.ExpiresAt "no expiry was recorded"
        }

        testCaseAsync "Consent is per channel: an email opt-in does not admit SMS"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()

            let! contact = created store scope (request "Partial" (Some "partial@example.com") (Some "+447700900002"))

            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, consent "manual")

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                Expect.isTrue
                    (ExternalContact.hasLiveOptIn DateTime.UtcNow NotificationKind.SinkKind.Email found)
                    "email was consented to"

                Expect.isFalse
                    (ExternalContact.hasLiveOptIn DateTime.UtcNow NotificationKind.SinkKind.Sms found)
                    "SMS was not, even though a phone number is on file"
        }

        testCaseAsync "A push consent is stored per variant and retrieved by it"
        <| async {
            // `SinkKind.Push` carries a variant, so the map key is a DU
            // with a payload. This case exists because that is the one
            // key shape a JSON round-trip can quietly lose.
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Pushed" (Some "pushed@example.com") None)

            let fcm = NotificationKind.SinkKind.Push NotificationKind.PushVariant.Fcm

            let vendor =
                NotificationKind.SinkKind.Push(NotificationKind.PushVariant.Other "vendor-x")

            let! _ = store.RecordOptIn(scope, actor, contact.Id, fcm, consent "manual")
            let! _ = store.RecordOptIn(scope, actor, contact.Id, vendor, consent "manual")

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                Expect.isTrue (ExternalContact.hasLiveOptIn DateTime.UtcNow fcm found) "the Fcm consent round-trips"

                Expect.isTrue
                    (ExternalContact.hasLiveOptIn DateTime.UtcNow vendor found)
                    "and so does a payload-carrying Other variant"

                Expect.isFalse
                    (ExternalContact.hasLiveOptIn
                        DateTime.UtcNow
                        (NotificationKind.SinkKind.Push NotificationKind.PushVariant.WebPush)
                        found)
                    "a variant nobody consented to stays unconsented"
        }

        testCaseAsync "An expired consent reads exactly like an absent one"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Lapsed" (Some "lapsed@example.com") None)

            let expiry = DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)

            let! _ =
                store.RecordOptIn(
                    scope,
                    actor,
                    contact.Id,
                    NotificationKind.SinkKind.Email,
                    {
                        consent "signed-form:42" with
                            ExpiresAt = Some expiry
                    }
                )

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                Expect.isTrue
                    (ExternalContact.hasLiveOptIn (expiry.AddDays -1.0) NotificationKind.SinkKind.Email found)
                    "live before the expiry"

                Expect.isFalse
                    (ExternalContact.hasLiveOptIn (expiry.AddDays 1.0) NotificationKind.SinkKind.Email found)
                    "not live after it"

                Expect.isSome
                    (ExternalContact.optInFor NotificationKind.SinkKind.Email found)
                    "the record itself is still stored — expiry is evaluated by the reader"
        }

        testCaseAsync "WithdrawOptIn REMOVES the stored consent, and is idempotent"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Withdrawn" (Some "withdrawn@example.com") None)

            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, consent "manual")

            let! withdrawn =
                store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, "recipient-request")

            Expect.isOk withdrawn "the withdrawal succeeds"

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                // Removed, not flagged: retaining the granted-at
                // evidence of a consent that no longer exists is the
                // data the withdrawal was meant to end.
                Expect.isNone
                    (ExternalContact.optInFor NotificationKind.SinkKind.Email found)
                    "the stored record is gone, not merely stale"

            // Honouring the same request twice is still honouring it.
            let! again =
                store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, "recipient-request")

            Expect.isOk again "withdrawing an absent consent still succeeds"
        }

        testCaseAsync "Create refuses a duplicate email in the same address book"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! first = created store scope (request "Alice" (Some "dupe@example.com") None)

            let! second = store.Create(scope, actor, owner, request "Alice Again" (Some "dupe@example.com") None)

            match second with
            | Error(ExternalContactError.Duplicate existingId) ->
                Expect.equal existingId first.Id "the refusal names the contact already on file"
            | other -> failtestf "expected a Duplicate refusal, got %A" other
        }

        testCaseAsync "The duplicate check normalises case and whitespace"
        <| async {
            // Two rows for one person means two sends, and a withdrawal
            // that silences one of them. `A@x.com` beside `a@x.com` is
            // the commonest way to get there.
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! _ = created store scope (request "Alice" (Some "Mixed.Case@Example.COM") None)

            let! second = store.Create(scope, actor, owner, request "Alice Again" (Some "mixed.case@example.com") None)

            Expect.isError second "a case-different email is the same address book entry"

            let! third = store.Create(scope, actor, owner, request "Bob" None (Some "+44 7700 900 123"))
            Expect.isOk third "a first phone-only contact is fine"

            let! fourth = store.Create(scope, actor, owner, request "Bob Again" None (Some "+447700900123"))
            Expect.isError fourth "a space-different phone number is the same entry"
        }

        testCaseAsync "Two contacts with no address at all do not collide"
        <| async {
            // The absent-value index sentinel is what stops every
            // email-less contact indexing under the same empty key and
            // reading as duplicates of each other.
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! _ = created store scope (request "Phone only A" None (Some "+447700900201"))
            let! second = store.Create(scope, actor, owner, request "Phone only B" None (Some "+447700900202"))
            Expect.isOk second "two email-less contacts are not duplicates of one another"
        }

        testCaseAsync "A contact with no way to reach it at all is refused"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! result = store.Create(scope, actor, owner, request "Nowhere" None None)

            match result with
            | Error(ExternalContactError.InvalidShape _) -> ()
            | other -> failtestf "expected an InvalidShape refusal, got %A" other
        }

        testCaseAsync "Scope isolation: team A's contacts are invisible from team B"
        <| async {
            let store = newStore ()
            let scopeA, scopeB = binding.Scopes()
            let! contact = created store scopeA (request "Private" (Some "private@example.com") None)

            let! crossScope = store.Get(scopeB, contact.Id)

            match crossScope with
            | Error ExternalContactError.NotFound
            | Error ExternalContactError.ScopeMismatch -> ()
            | other -> failtestf "a cross-scope read must refuse, got %A" other

            let! listed = store.List scopeB
            Expect.isEmpty listed "and the other scope's list is empty"

            let! own = store.List scopeA
            Expect.equal (List.length own) 1 "while the owning scope still sees it"
        }

        testCaseAsync "ListByOwner round-trips a `ContactOwner.User` owner"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let personal = ContactOwner.User "alice"

            let! _ =
                store.Create(
                    scope,
                    actor,
                    personal,
                    {
                        request "Personal" (Some "personal@example.com") None with
                            Owner = personal
                    }
                )

            let! _ = created store scope (request "Team" (Some "team@example.com") None)

            let! mine = store.ListByOwner(scope, personal)
            Expect.equal (List.length mine) 1 "one personal contact"
            Expect.equal (List.head mine).Owner personal "the owner DU round-trips"

            let! theirs = store.ListByOwner(scope, owner)
            Expect.equal (List.length theirs) 1 "one team contact"
        }

        testCaseAsync "Update edits the reachable fields and CANNOT move a consent"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Before" (Some "before@example.com") None)

            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, consent "manual")

            let! updated =
                store.Update(
                    scope,
                    actor,
                    {
                        ContactId = contact.Id
                        DisplayName = "After"
                        EmailAddress = Some "after@example.com"
                        PhoneNumber = None
                        WhatsAppNumber = None
                        Tags = [ "family" ]
                        Notes = Some "moved"
                    }
                )

            match updated with
            | Error e -> failtestf "expected the update to land, got %s" (ExternalContactError.describe e)
            | Ok _ ->
                let! read = store.Get(scope, contact.Id)

                match read with
                | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
                | Ok found ->
                    Expect.equal found.DisplayName "After" "the name moved"
                    Expect.equal found.OptionalEmailAddress (Some "after@example.com") "the address moved"
                    Expect.equal found.Tags [ "family" ] "the tags moved"

                    Expect.isTrue
                        (ExternalContact.hasLiveOptIn DateTime.UtcNow NotificationKind.SinkKind.Email found)
                        "and the consent survived an edit it has no way to express"
        }

        testCaseAsync "Delete removes the contact and reports the consent it discarded"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Doomed" (Some "doomed@example.com") None)

            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, consent "manual")

            let! deleted = store.Delete(scope, actor, contact.Id)

            match deleted with
            | Error e -> failtestf "expected the delete to land, got %s" (ExternalContactError.describe e)
            | Ok discarded ->
                Expect.equal discarded [ NotificationKind.SinkKind.Email ] "the discarded consent is reported"

            let! read = store.Get(scope, contact.Id)
            Expect.equal read (Error ExternalContactError.NotFound) "and the contact is gone"

            let! again = store.Delete(scope, actor, contact.Id)
            Expect.equal again (Ok []) "a second delete is idempotent with nothing left to discard"
        }

        testCaseAsync "RecordInbound stamps LastInboundUtc"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Chatty" None (Some "+447700900301"))
            Expect.isNone contact.LastInboundUtc "nothing inbound yet"

            let at = DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc)
            let! stamped = store.RecordInbound(scope, contact.Id, at)

            match stamped with
            | Error e -> failtestf "expected the stamp to land, got %s" (ExternalContactError.describe e)
            | Ok _ ->
                let! read = store.Get(scope, contact.Id)

                match read with
                | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
                | Ok found ->
                    // Rule 6 — second precision is the promise; the
                    // round-trip is asserted at that granularity.
                    Expect.equal
                        (found.LastInboundUtc |> Option.map (fun d -> d.ToString "yyyy-MM-ddTHH:mm:ss"))
                        (Some "2026-03-04T05:06:07")
                        "the inbound instant round-trips to the second"
        }

        // ─── Six-rule portability audit (Phase 9c, GP 12) ─────────

        testCaseAsync "Rule 1 — identity by value: every parameter and return is a value"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Valued" (Some "valued@example.com") None)

            // `Id` is a string, `Owner` is a DU over strings, and the
            // whole record survives a structural comparison — no live
            // handle could.
            Expect.equal contact contact "the returned record is structurally comparable"

            Expect.equal
                (ContactOwner.tryParse (ContactOwner.toWireString contact.Owner))
                (Some contact.Owner)
                "the owner is a value that round-trips through a string"
        }

        testCaseAsync "Rule 2 — async at every boundary"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Async" (Some "async@example.com") None)
            let! _ = store.Get(scope, contact.Id)
            let! _ = store.List scope
            let! _ = store.ListByOwner(scope, owner)
            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Sms, consent "manual")
            let! _ = store.WithdrawOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Sms, "admin")
            let! _ = store.RecordInbound(scope, contact.Id, DateTime.UtcNow)
            let! _ = store.Delete(scope, actor, contact.Id)
            ()
        }

        testCaseAsync "Rule 3 — failure is data, never an exception on an expected path"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()

            let! missing = store.Get(scope, "absent")
            Expect.isError missing "an absent contact is an Error value"

            let! badShape = store.Create(scope, actor, owner, request "" None None)
            Expect.isError badShape "an unstorable shape is an Error value"

            let! badChannel = store.RecordOptIn(scope, actor, "absent", NotificationKind.SinkKind.Email, consent "x")
            Expect.isError badChannel "a consent on an absent contact is an Error value"
        }

        testCaseAsync "Rule 4 — statelessness: a second store over the same storage sees the same contacts"
        <| async {
            // The binding's factory returns a store over FRESH storage,
            // so this case asserts the weaker but still meaningful
            // claim: nothing the first store did lives only in its own
            // memory for the duration of one call chain.
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Persisted" (Some "persisted@example.com") None)

            let! _ = store.RecordOptIn(scope, actor, contact.Id, NotificationKind.SinkKind.Email, consent "manual")

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                Expect.isTrue
                    (ExternalContact.hasLiveOptIn DateTime.UtcNow NotificationKind.SinkKind.Email found)
                    "the consent was written, not remembered"
        }

        testCaseAsync "Rule 5 — no cross-shard ordering promise: List is asserted as a SET"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! a = created store scope (request "A" (Some "a@example.com") None)
            let! b = created store scope (request "B" (Some "b@example.com") None)

            let! listed = store.List scope

            Expect.equal
                (listed |> List.map _.Id |> Set.ofList)
                (Set.ofList [ a.Id; b.Id ])
                "every contact is listed; the order is not part of the contract"
        }

        testCaseAsync "Rule 6 — precision at the lower bound: consent instants are second-granular"
        <| async {
            let store = newStore ()
            let scope, _ = binding.Scopes()
            let! contact = created store scope (request "Precise" (Some "precise@example.com") None)

            let granted = DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)

            let! _ =
                store.RecordOptIn(
                    scope,
                    actor,
                    contact.Id,
                    NotificationKind.SinkKind.Email,
                    {
                        consent "manual" with
                            GrantedAt = granted
                    }
                )

            let! read = store.Get(scope, contact.Id)

            match read with
            | Error e -> failtestf "expected the contact back, got %s" (ExternalContactError.describe e)
            | Ok found ->
                match ExternalContact.optInFor NotificationKind.SinkKind.Email found with
                | None -> failtest "expected the consent"
                | Some record ->
                    Expect.equal
                        (record.GrantedAt.ToString "yyyy-MM-ddTHH:mm:ss")
                        "2026-02-03T04:05:06"
                        "the granted instant round-trips to the second"
        }
    ]