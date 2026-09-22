// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ExternalContactStore

open System
open ToolUp.Platform
open ToolUp.Platform.EntityTypes

// ─── SDK-default IExternalContactStore (Phase 6f.A) ──────────────────
//
// `EntityBackedExternalContactStore` is the one implementation the SDK
// ships. It is `IEntityStore`-backed rather than laid out by hand over
// `IBlobStorage`, and that choice buys three things the address book
// actually needs:
//
//   * **Versioning.** A consent record is a legal artefact. The entity
//     store keeps every version of the contact, so "what did this
//     contact's consent look like in March" is answerable from storage
//     and not only from the audit trail.
//   * **Dedupe indexes.** Two rows for one person means two sends, and a
//     withdrawal that silences only one of them. `Create` and `Update`
//     both consult the email and phone indexes before writing.
//   * **Per-scope containers.** The entity store already lands each
//     scope's objects in that scope's own container, so team isolation
//     (GP 4) is carried by the substrate rather than re-implemented in a
//     path template here.
//
// **The stored layout is therefore the entity store's, not a bespoke
// `_platform/external-contacts/` path.** The phase file proposed the
// latter, which the shipped entity substrate does not produce: entities
// live under `{scopeId}/objects/_entity__ExternalContact__{id}/v{N}.json`
// with index refs under `{scopeId}/entities/_indexes/ExternalContact/`.
// The user-keyed `_platform/contacts/{scopeId}/{userId}.json` address
// book is untouched and still byte-for-byte what it was (GP 11).
//
// **This store is the audit chokepoint for the contact lifecycle.** The
// consent rows are emitted here and not in the API handler, because the
// handler is one caller of several — a job, an intake pipeline, an
// inbound webhook can all reach the store — and an audit emitted at each
// caller is an audit some caller will forget. `IAuditLog option` follows
// the `BlobEntityStore` shape: `None` in a deployment with no audit log
// composed, which degrades to no rows rather than to a failure.

/// The entity registration for `ExternalContact`. Public so a
/// composition root can register the entity itself with
/// `ServerApp.withEntity` where it wants the address book's storage
/// without the rest of the substrate; the SDK registers it for you when
/// `ServerConfig.ExternalContactStore = EnabledExternalContactStore`.
///
/// Three single-field indexes, each earning its place:
///   * `owner` serves `ListByOwner`, so a personal lookup in a scope that
///     also holds a shared team list does not cost a full scan;
///   * `email` and `phone` serve the duplicate check on write.
///
/// There is deliberately no tag index. A tag is multi-valued per
/// contact, and an `EntityIndex` extracts ONE string per entity — a tag
/// index would therefore have to pick a tag, which is worse than no
/// index. Tag filtering runs in memory over `ListByOwner`, whose
/// cardinality is one owner's address book.
let registration: EntityRegistration<ExternalContact> =
    EntityRegistration.create<ExternalContact> ExternalContact.EntityType
    |> EntityRegistration.withIndex ExternalContact.IndexOwner ExternalContact.ownerIndexValue
    |> EntityRegistration.withIndex ExternalContact.IndexEmail ExternalContact.emailIndexValue
    |> EntityRegistration.withIndex ExternalContact.IndexPhone ExternalContact.phoneIndexValue

/// Page size for the paged walk `List` does over `ListAll`. Not a cap on
/// the result — the walk continues until a short page — just the read
/// granularity.
[<Literal>]
let private ListPageSize = 200

/// Translate an `EntityError` into the address book's own vocabulary.
/// `NotFound` and a scope that does not hold the entity are the same
/// observable outcome by design (see `IExternalContactStore.Get`).
let private ofEntityError (error: EntityError) : ExternalContactError =
    match error with
    | EntityError.NotFound _ -> ExternalContactError.NotFound
    | EntityError.UnknownEntityType entityType ->
        ExternalContactError.StorageFailed
            $"The '{entityType}' entity type is not registered. Enable the external contact store at compose time."
    | EntityError.InvalidIndex indexName -> ExternalContactError.StorageFailed $"Unknown index '{indexName}'."
    | EntityError.InvalidEntityShape reason -> ExternalContactError.InvalidShape reason
    | EntityError.VersionConflict(_, _, expected, actual) ->
        ExternalContactError.StorageFailed
            $"The contact was modified by someone else (expected version {expected}, found {actual}). Reload and try again."
    | EntityError.StorageFailure message -> ExternalContactError.StorageFailed message

/// `IExternalContactStore` over the composed `IEntityStore`.
///
/// Stateless across calls (portability rule 4) — every member reads what
/// it needs. Async at every boundary (rule 2). Expected failures are
/// `ExternalContactError` values, never exceptions (rule 3).
type EntityBackedExternalContactStore(entities: IEntityStore.IEntityStore, auditLog: IAuditLog option) =

    let audit (scopeId: string) (event: AuditEvent) : Async<unit> =
        match auditLog with
        | Some log -> log.Record(scopeId, event)
        | None -> async { return () }

    /// Read one contact, coercing the collections a payload written
    /// before a field existed can deserialise as `null`.
    let get (scopeId: string) (contactId: string) : Async<Result<ExternalContact, ExternalContactError>> = async {
        let! result = entities.Get<ExternalContact>(scopeId, ExternalContact.EntityType, contactId)

        return
            match result with
            | Ok contact -> Ok(ExternalContact.coerce contact)
            | Error error -> Error(ofEntityError error)
    }

    /// Resolve a list of refs to their full records, dropping any whose
    /// read fails. A ref whose entity has since been deleted is a stale
    /// index entry, not an error the caller should see.
    let resolve (scopeId: string) (refs: EntityRef<ExternalContact> list) : Async<ExternalContact list> = async {
        let! resolved =
            refs
            |> List.map (fun r -> async {
                let! contact = get scopeId r.Id

                return
                    match contact with
                    | Ok c -> Some c
                    | Error _ -> None
            })
            |> Async.Parallel

        return resolved |> Array.choose id |> Array.toList
    }

    /// Every contact in the scope, walked a page at a time.
    let listAll (scopeId: string) : Async<ExternalContact list> = async {
        let rec walk (skip: int) (acc: ExternalContact list) = async {
            let! page = entities.ListAll<ExternalContact>(scopeId, ExternalContact.EntityType, skip, ListPageSize)
            let! resolved = resolve scopeId page
            let acc = acc @ resolved

            if List.length page < ListPageSize then
                return acc
            else
                return! walk (skip + ListPageSize) acc
        }

        return! walk 0 []
    }

    /// Contacts in `scopeId` matching an exact index value.
    let byIndex (scopeId: string) (indexName: string) (value: string) : Async<ExternalContact list> = async {
        let! refs = entities.FindByIndex<ExternalContact>(scopeId, ExternalContact.EntityType, indexName, value)

        match refs with
        | Error _ -> return []
        | Ok refs -> return! resolve scopeId refs
    }

    /// The duplicate check both writes run. `exceptId` excludes the
    /// contact being edited, so re-saving a contact without changing its
    /// address is not a duplicate of itself.
    let findDuplicate
        (scopeId: string)
        (owner: ContactOwner)
        (exceptId: string option)
        (email: string option)
        (phone: string option)
        : Async<string option> =
        async {
            let ownerWire = ContactOwner.toWireString owner

            let candidates (indexName: string) (value: string option) = async {
                match value with
                | None -> return []
                | Some v when String.IsNullOrWhiteSpace v -> return []
                | Some v -> return! byIndex scopeId indexName v
            }

            let! byEmail = candidates ExternalContact.IndexEmail (email |> Option.map ExternalContact.normaliseEmail)
            let! byPhone = candidates ExternalContact.IndexPhone (phone |> Option.map ExternalContact.normalisePhone)

            return
                byEmail @ byPhone
                |> List.filter (fun c -> ContactOwner.toWireString c.Owner = ownerWire && Some c.Id <> exceptId)
                |> List.tryHead
                |> Option.map _.Id
        }

    /// Persist a contact that has already been validated, mapping the
    /// entity store's error vocabulary onto the address book's.
    let save
        (scopeId: string)
        (actorUserId: string)
        (contact: ExternalContact)
        : Async<Result<ExternalContact, ExternalContactError>> =
        async {
            let actor = EntityPrincipal.ofPrincipal actorUserId
            let! saved = entities.Save<ExternalContact>(scopeId, actor, contact)

            return
                match saved with
                | Ok entityRef ->
                    Ok {
                        contact with
                            Version = entityRef.Version
                    }
                | Error error -> Error(ofEntityError error)
        }

    /// Read, apply `change`, write. The read is what makes the stored
    /// `Version` current, so a caller cannot write a stale one.
    let mutate
        (scopeId: string)
        (actorUserId: string)
        (contactId: string)
        (change: ExternalContact -> Result<ExternalContact, ExternalContactError>)
        : Async<Result<ExternalContact, ExternalContactError>> =
        async {
            match! get scopeId contactId with
            | Error error -> return Error error
            | Ok existing ->
                match change existing with
                | Error error -> return Error error
                | Ok updated -> return! save scopeId actorUserId updated
        }

    interface IExternalContactStore with

        member _.Create(scopeId, actorUserId, owner, request) = async {
            let candidate: ExternalContact = {
                Id = Guid.NewGuid().ToString "N"
                Type = ExternalContact.EntityType
                Version = 0
                DisplayName = request.DisplayName
                OptionalEmailAddress = request.EmailAddress
                OptionalPhoneNumber = request.PhoneNumber
                OptionalWhatsAppNumber = request.WhatsAppNumber
                Owner = owner
                // A newly filed contact consents to nothing. This is the
                // safe default AND the legally correct one: consent is
                // something a person gives, never something a contact
                // record is born holding.
                OptIns = Map.empty
                Tags = (if isNull (box request.Tags) then [] else request.Tags)
                CreatedAt = DateTime.UtcNow
                LastInboundUtc = None
                Notes = request.Notes
            }

            match ExternalContact.validate candidate with
            | Error reason -> return Error(ExternalContactError.InvalidShape reason)
            | Ok validated ->
                let! duplicate =
                    findDuplicate scopeId owner None validated.OptionalEmailAddress validated.OptionalPhoneNumber

                match duplicate with
                | Some existingId -> return Error(ExternalContactError.Duplicate existingId)
                | None ->
                    match! save scopeId actorUserId validated with
                    | Error error -> return Error error
                    | Ok stored ->
                        do!
                            audit
                                scopeId
                                (ContactCreated {
                                    UserId = actorUserId
                                    ScopeId = scopeId
                                    ContactId = stored.Id
                                    Owner = ContactOwner.toWireString stored.Owner
                                    Tags = stored.Tags
                                })

                        return Ok stored
        }

        member _.Get(scopeId, contactId) = get scopeId contactId

        member _.List scopeId = listAll scopeId

        member _.ListByOwner(scopeId, owner) =
            byIndex scopeId ExternalContact.IndexOwner (ContactOwner.toWireString owner)

        member _.Update(scopeId, actorUserId, request) = async {
            match! get scopeId request.ContactId with
            | Error error -> return Error error
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
                    let! duplicate =
                        findDuplicate
                            scopeId
                            validated.Owner
                            (Some validated.Id)
                            validated.OptionalEmailAddress
                            validated.OptionalPhoneNumber

                    match duplicate with
                    | Some existingId -> return Error(ExternalContactError.Duplicate existingId)
                    | None ->
                        // The changed-field list is what the audit row
                        // carries INSTEAD of the values, so an operator
                        // can see that an address moved without the
                        // trail itself accumulating addresses.
                        let changed = [
                            if existing.DisplayName <> validated.DisplayName then
                                "DisplayName"
                            if existing.OptionalEmailAddress <> validated.OptionalEmailAddress then
                                "EmailAddress"
                            if existing.OptionalPhoneNumber <> validated.OptionalPhoneNumber then
                                "PhoneNumber"
                            if existing.OptionalWhatsAppNumber <> validated.OptionalWhatsAppNumber then
                                "WhatsAppNumber"
                            if existing.Tags <> validated.Tags then
                                "Tags"
                            if existing.Notes <> validated.Notes then
                                "Notes"
                        ]

                        match! save scopeId actorUserId validated with
                        | Error error -> return Error error
                        | Ok stored ->
                            do!
                                audit
                                    scopeId
                                    (ContactUpdated {
                                        UserId = actorUserId
                                        ScopeId = scopeId
                                        ContactId = stored.Id
                                        ChangedFields = changed
                                    })

                            return Ok stored
        }

        member _.Delete(scopeId, actorUserId, contactId) = async {
            match! get scopeId contactId with
            // Idempotent: a deletion request honoured twice is still a
            // deletion request honoured, and the second call has no
            // consent left to report.
            | Error ExternalContactError.NotFound -> return Ok []
            | Error error -> return Error error
            | Ok existing ->
                let discarded = existing.OptIns |> Map.toList |> List.map fst

                let actor = EntityPrincipal.ofPrincipal actorUserId
                let! deleted = entities.Delete(scopeId, actor, ExternalContact.EntityType, contactId)

                match deleted with
                | Error error -> return Error(ofEntityError error)
                | Ok() ->
                    do!
                        audit
                            scopeId
                            (ContactDeleted {
                                UserId = actorUserId
                                ScopeId = scopeId
                                ContactId = contactId
                                WithdrawnChannels = discarded |> List.map NotificationKind.SinkKind.toWireString
                            })

                    return Ok discarded
        }

        member _.RecordOptIn(scopeId, actorUserId, contactId, channel, record) = async {
            match! mutate scopeId actorUserId contactId (ExternalContact.withOptIn channel record >> Ok) with
            | Error error -> return Error error
            | Ok stored ->
                do!
                    audit
                        scopeId
                        (ContactOptInRecorded {
                            UserId = actorUserId
                            ScopeId = scopeId
                            ContactId = contactId
                            Channel = NotificationKind.SinkKind.toWireString channel
                            Source = record.Source
                            ExpiresAt = record.ExpiresAt
                        })

                return Ok stored
        }

        member _.WithdrawOptIn(scopeId, actorUserId, contactId, channel, reason) = async {
            match! mutate scopeId actorUserId contactId (ExternalContact.withoutOptIn channel >> Ok) with
            | Error error -> return Error error
            | Ok stored ->
                // Audited even when there was nothing to withdraw: the
                // row records that the request was made and honoured,
                // which is the fact a recipient's complaint turns on.
                do!
                    audit
                        scopeId
                        (ContactOptInWithdrawn {
                            UserId = actorUserId
                            ScopeId = scopeId
                            ContactId = contactId
                            Channel = NotificationKind.SinkKind.toWireString channel
                            Reason = reason
                        })

                return Ok stored
        }

        member _.RecordInbound(scopeId, contactId, atUtc) =
            // The system actor, not a user: an inbound webhook is the
            // emitter and no person made this write.
            mutate scopeId EntityPrincipal.SystemPrincipal contactId (fun contact ->
                Ok {
                    contact with
                        LastInboundUtc = Some atUtc
                })

/// Build the SDK-default store over a composed `IEntityStore`.
let entityBacked (entities: IEntityStore.IEntityStore) (auditLog: IAuditLog option) : IExternalContactStore =
    EntityBackedExternalContactStore(entities, auditLog) :> IExternalContactStore