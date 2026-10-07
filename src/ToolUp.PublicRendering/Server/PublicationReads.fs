// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open System.Text.Json
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 996 — publication reads ───────────────────────────────────
//
// A `Publication` page serves readers who are not app users the
// PUBLISHING scope's content. Two things live here, both keyed off that
// scope rather than the reader's:
//
//   1. `PublicationAudit` — the audit row every publication decision
//      writes: who read (or was refused), which publishing scope, which
//      page, what was served. Rows are written to `IEventStore` under the
//      publishing scope, so the publishing team's own trail answers "who
//      read our reports"; a confined reader refused on a page that is not
//      a publication is recorded under `_platform`.
//   2. `PublicationRead.latestNarrative` — the latest narrative a tagged
//      run published in the publishing scope, read only after the page's
//      audience admits the reader, and served only while every fact it
//      cites is still disclosable at `FactNarrativePublication` (a fact
//      reclassified since the run published withholds the narrative — the
//      publication door's refusal posture, never a silent redaction).
//
// The reader never gains the publishing scope: nothing here hands it an
// `AccessContext` for that scope. The read is the SDK's, made on the
// reader's behalf, for one page, with every outcome audited.

/// One audit row of a publication decision. Identity by value (GP 12).
type PublicationAuditRow = {
    /// The reader's user id (`AccessContext.UserId`).
    ReaderId: string
    /// The scope whose content the page reads; `""` when a confined reader
    /// was refused a page that is not a publication.
    PublishingScope: string
    /// The page slug.
    Slug: string
    /// A page decision: `served` | `refused`. A narrative read: `read` |
    /// `withheld` | `nothing-published`.
    Outcome: string
    /// The narrative served (or withheld), when there was one.
    NarrativeId: string option
    /// Why, for a refusal or a withholding — fact ids and policy refs,
    /// never a value.
    Detail: string option
}

module PublicationAudit =
    /// The `SourceModule` of every publication audit row.
    [<Literal>]
    let SourceModule = "_publication"

    /// A publication page was served to a reader (the page decision).
    [<Literal>]
    let ServedType = "PublicationServed"

    /// A publication's narrative was read for a reader, under the publishing
    /// scope — or the run had published nothing there yet.
    [<Literal>]
    let NarrativeReadType = "PublicationNarrativeRead"

    /// A signed-in principal was refused a publication page — or, as a
    /// publication reader, any page that is not one of its publications.
    [<Literal>]
    let RefusedType = "PublicationRefused"

    /// The narrative was withheld because a fact it cites is no longer
    /// disclosable at `FactNarrativePublication`.
    [<Literal>]
    let WithheldType = "PublicationWithheld"

    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let private eventTypeOf (row: PublicationAuditRow) =
        match row.Outcome with
        | "refused" -> RefusedType
        | "withheld" -> WithheldType
        | "read"
        | "nothing-published" -> NarrativeReadType
        | _ -> ServedType

    /// Write `row` to `events`, under the publishing scope (or `_platform`
    /// when the row names none). Best-effort, as every audit write on a
    /// request path is: a store failure never fails the read it records,
    /// and no store composed records nothing.
    let record (events: IEventStore option) (row: PublicationAuditRow) : Async<unit> = async {
        match events with
        | None -> return ()
        | Some store ->
            try
                do!
                    store.Write {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId =
                            if String.IsNullOrWhiteSpace row.PublishingScope then
                                "_platform"
                            else
                                row.PublishingScope
                        SourceModule = SourceModule
                        EventType = eventTypeOf row
                        Payload = JsonSerializer.Serialize(row, jsonOptions)
                    }
            with _ ->
                ()
    }

    /// The `IEventStore` composed in `services`, if any.
    let eventsIn (services: IServiceProvider) : IEventStore option =
        match services.GetService(typeof<IEventStore>) with
        | :? IEventStore as e -> Some e
        | _ -> None

    /// Whether a page decision is one this audit records: any decision on a
    /// `Publication` page, and any decision for a publication reader. An
    /// anonymous request carries no reader identity, so its `401` challenge
    /// is not recorded (it is the sign-in redirect, not a refusal of a
    /// reader); every other app page decision is out of scope here.
    let concerns (access: AccessContext) (audience: PageAudience) : bool =
        AccessContext.isAuthenticated access
        && (AccessContext.isPublicationReader access
            || (match audience with
                | PageAudience.Publication _ -> true
                | _ -> false))

    /// The audit row of a page decision (`AudienceGate.evaluate`'s verdict
    /// on `audience` for `access`).
    let pageRow (access: AccessContext) (slug: string) (audience: PageAudience) (decision: AudienceDecision) = {
        ReaderId = access.UserId
        PublishingScope =
            match audience with
            | PageAudience.Publication a -> a.PublishingScope
            | _ -> ""
        Slug = slug
        Outcome =
            match decision with
            | AudienceDecision.Allow -> "served"
            | AudienceDecision.RequireAuthentication
            | AudienceDecision.Forbidden -> "refused"
        NarrativeId = None
        Detail =
            match decision, audience with
            | AudienceDecision.Allow, _ -> None
            | _, PageAudience.Publication _ -> Some "the principal holds none of the page's readers"
            | _ -> Some "a publication reader may read its publications only"
    }

/// The outcome of reading a publication's latest narrative.
[<RequireQualifiedAccess>]
type PublicationNarrative =
    /// The latest narrative, every cited fact still disclosable.
    | Served of id: NarrativeId * document: NarrativeDocument
    /// The audience admits the reader; the run has published nothing in
    /// the publishing scope yet.
    | NothingPublished
    /// The latest narrative cites facts the disclosure gate now denies at
    /// `FactNarrativePublication` — `(factId, policyRef)`, never a value.
    | Withheld of denied: (string * string) list
    /// The audience does not admit the reader (401 / 403).
    | Refused of AudienceDecision

module PublicationRead =

    /// The latest narrative tagged `tag` in `audience.PublishingScope`,
    /// for `reader` on the page `slug`.
    ///
    /// 1. The audience decides first (`AudienceGate.evaluate` on
    ///    `PageAudience.Publication audience`); a refusal reads nothing.
    /// 2. The store is read under the PUBLISHING scope — the scope the page
    ///    names, never the reader's.
    /// 3. Disclosure still applies: the narrative's fact refs are checked at
    ///    `FactNarrativePublication` in the publishing scope, with the
    ///    reader as principal; any deny withholds the whole narrative. No
    ///    gate composed ⇒ no classified facts to deny (GP 13).
    /// 4. Every READ is audited (`PublicationAudit`) — served, withheld, or
    ///    nothing published — naming the reader, the publishing scope, the
    ///    page and the narrative. A refusal reads nothing and records
    ///    nothing here: the page it belongs to is refused, and recorded, by
    ///    the page handler's own gate (`PublicPageHandler`), which judges the
    ///    same audience — one refusal, one row. A caller using this outside
    ///    the page handler records its own refusal
    ///    (`PublicationAudit.pageRow`).
    let latestNarrative
        (store: INarrativeStore)
        (disclosure: IFactDisclosureGate option)
        (events: IEventStore option)
        (audience: PublicationAudience)
        (slug: string)
        (tag: string)
        (reader: AccessContext)
        : Async<PublicationNarrative> =
        async {
            let row outcome narrativeId detail = {
                ReaderId = reader.UserId
                PublishingScope = audience.PublishingScope
                Slug = slug
                Outcome = outcome
                NarrativeId = narrativeId
                Detail = detail
            }

            match AudienceGate.evaluate reader (PageAudience.Publication audience) with
            | AudienceDecision.Allow ->
                let scope = audience.PublishingScope

                match! store.ListByTag(scope, 1, Some tag) with
                | [] ->
                    do! PublicationAudit.record events (row "nothing-published" None None)
                    return PublicationNarrative.NothingPublished
                | info :: _ ->
                    match! store.Get(scope, info.Id) with
                    | None ->
                        do! PublicationAudit.record events (row "nothing-published" None None)
                        return PublicationNarrative.NothingPublished
                    | Some entry ->
                        let! denied = async {
                            match disclosure, NarrativeFacts.factRefs entry.Document |> Set.toList with
                            | None, _
                            | _, [] -> return []
                            | Some gate, refs ->
                                let! verdicts = gate.Check(scope, reader.UserId, FactNarrativePublication, refs)

                                // Fail-closed: a ref without an affirmative
                                // verdict is denied.
                                return
                                    refs
                                    |> List.choose (fun factId ->
                                        match Map.tryFind factId verdicts with
                                        | Some FactDisclosable -> None
                                        | Some(FactNotDisclosable policyRef) -> Some(factId, policyRef)
                                        | None -> Some(factId, "unknown-fact"))
                        }

                        let narrativeId = Some(string entry.Id)

                        match denied with
                        | [] ->
                            do! PublicationAudit.record events (row "read" narrativeId None)
                            return PublicationNarrative.Served(entry.Id, entry.Document)
                        | denied ->
                            let detail =
                                denied
                                |> List.map (fun (factId, policyRef) -> sprintf "%s (policy %s)" factId policyRef)
                                |> String.concat "; "

                            do!
                                PublicationAudit.record
                                    events
                                    (row "withheld" narrativeId (Some("not disclosable: " + detail)))

                            return PublicationNarrative.Withheld denied
            | decision -> return PublicationNarrative.Refused decision
        }

    /// `latestNarrative` over the services composed in `services`: the
    /// `INarrativeStore` (none composed ⇒ `NothingPublished`, audited), the
    /// `IFactDisclosureGate` and the `IEventStore`, when present.
    let latestNarrativeIn
        (services: IServiceProvider)
        (audience: PublicationAudience)
        (slug: string)
        (tag: string)
        (reader: AccessContext)
        : Async<PublicationNarrative> =
        let events = PublicationAudit.eventsIn services

        let disclosure =
            match services.GetService(typeof<IFactDisclosureGate>) with
            | :? IFactDisclosureGate as g -> Some g
            | _ -> None

        match services.GetService(typeof<INarrativeStore>) with
        | :? INarrativeStore as store -> latestNarrative store disclosure events audience slug tag reader
        | _ -> async {
            match AudienceGate.evaluate reader (PageAudience.Publication audience) with
            | AudienceDecision.Allow ->
                do!
                    PublicationAudit.record events {
                        ReaderId = reader.UserId
                        PublishingScope = audience.PublishingScope
                        Slug = slug
                        Outcome = "nothing-published"
                        NarrativeId = None
                        Detail = Some "no narrative store is composed"
                    }

                return PublicationNarrative.NothingPublished
            | decision -> return PublicationNarrative.Refused decision
          }