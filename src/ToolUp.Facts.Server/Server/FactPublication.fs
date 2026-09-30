// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open ToolUp.ArtefactSigning
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Grounding
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Team-to-team fact publication (Phase 897) ───────────────────────
//
// A consolidated view is built by PUBLISHING, never by reading across
// teams. The shape, end to end:
//
//   1. **A grant.** An owner of either team proposes it: source team and
//      table, target team and table, a visibility level. Each team's owner
//      then consents, in a request resolved to their own team — an audited
//      act each. A grant with one consent publishes nothing.
//   2. **Publication is a job in the SOURCE team's scope.** It reads the
//      source's own declared table in its own scope and passes every cell
//      through the disclosure gate at the `FactTeamPublication` door, so the
//      source team decides what leaves. A row with any cell denied is
//      absent from the publication. The gate decides a `Restricted` cell for
//      the least-privileged viewer of the source team, so restricted output
//      never travels past the team's own output level (Phase 896).
//   3. **One sanctioned seam writes across.** `FactPublicationSeam` is the
//      only code in the fact tier that holds two teams' scopes at once — a
//      `ResolvedScopePair` built from two MINTED scopes, never from a string
//      (Phase 797's rule). Given a grant in force it writes a run of the
//      target's declared table. The test pack enumerates its callers.
//   4. **The target receives a writer run.** Each row's path is the origin
//      team followed by the source row's path — the origin team is the root
//      level of the target table's hierarchy, so a question scoped to one
//      origin is a path-prefix population query. Every cell is written with
//      `Imported` provenance naming the grant, its evidence names the origin
//      run, and its disclosure is the FLOOR of what the source published and
//      what the target's declaration allows (`Disclosure.floor`, the Phase
//      683 rule): an import narrows or leaves alone, never widens.
//   5. **Withdrawal.** Either owner may withdraw a grant. At the target
//      table's next refresh the origin's rows are removed, and every fact
//      already minted from them is superseded by an absence naming the
//      withdrawal. A grant this process no longer holds is treated as
//      withdrawn — fail closed.
//   6. **Both sides audit each run** under the reserved `_facts` source
//      module: `FactPublicationRunPublished` in the source scope and
//      `FactPublicationRunReceived` in the target scope. The two records
//      carry one run id and cite each other by record id.
//
// **Signing between teams of one deployment — which profile requires
// which.** `PublicationSigning.RecordedProvenance` is the DEFAULT: one
// deployment is one trust boundary, and each run carries recorded provenance
// — the grant, the origin team, the origin run and the two audit records
// citing each other. `PublicationSigning.SignedCertificate` is the
// REGULATED profile: the source side additionally seals each run's manifest
// (grant, origin team, source table, origin run, a digest of the rows) with
// the deployment's `IArtefactSigner`, and the seam verifies it with
// `IArtefactVerifier` before anything is written; a run that cannot be
// signed or verified writes nothing. A deployment chooses the profile at
// composition (`FactPublicationConfig.Signing`), and composing the regulated
// profile without a signer and a verifier fails at startup.
//
// **Where the target's scope comes from.** The target owner's consent is
// given in a request the platform resolved to the target team; the minted
// scope of that request is what the seam writes under. A minted scope is a
// process value — the platform deliberately offers no way to persist and
// re-mint one outside its scheduler — so grants are held by this service in
// process. A restart forgets them, and every consolidation then reads its
// origins as withdrawn at the next refresh until both owners consent again.
// That is the fail-closed direction: the alternative is a third mint.
//
// **What this is not.** It is not a cross-team query. Nothing here answers a
// question in one team with another team's facts; the target answers from
// the rows it received, inside its own scope, and a live fan-out across
// teams is deliberately not offered.
//
// **Nothing here is reachable unless a deployment composes it**
// (`FactsCompose.withFactPublication`). A deployment that declares no grant
// is byte-for-byte what it was (GP 11, GP 13).

/// Event-type discriminators for the publication audit trail. Every record
/// rides the fact store's `_facts` source module, so one read of a scope's
/// `_facts` events returns its facts, its table runs and its publications.
module FactPublicationEvents =

    /// A grant was proposed.
    [<Literal>]
    let ProposedType = "FactPublicationProposed"

    /// A team's owner consented to a grant.
    [<Literal>]
    let ConsentedType = "FactPublicationConsented"

    /// A team's owner withdrew a grant.
    [<Literal>]
    let RevokedType = "FactPublicationRevoked"

    /// The source side of a publication run (written in the source scope).
    [<Literal>]
    let PublishedType = "FactPublicationRunPublished"

    /// The target side of a publication run (written in the target scope).
    [<Literal>]
    let ReceivedType = "FactPublicationRunReceived"

    /// A publication run was refused; nothing was written to the target.
    [<Literal>]
    let RefusedType = "FactPublicationRunRefused"

    /// A consolidation table was rewritten from the origins in force.
    [<Literal>]
    let RefreshedType = "FactPublicationRefreshed"

/// The payload every publication audit record carries (Phase 897). Fields a
/// given act has no value for are `None` / zero.
type FactPublicationEvent = {
    /// The grant the act concerns.
    GrantId: string
    /// Which side's scope this record was written in.
    Side: string
    /// The source team.
    SourceTeam: string
    /// The source table.
    SourceTable: string
    /// The target team.
    TargetTeam: string
    /// The target table.
    TargetTable: string
    /// The grant's visibility level.
    Visibility: string
    /// The owner who performed an owner act.
    ByUserId: string option
    /// The publication run both sides' records share.
    PublicationRunId: string option
    /// The source table's committed run the rows came from.
    OriginRun: string option
    /// The target table's run the rows were written in.
    TargetRun: string option
    /// Rows published.
    RowsPublished: int
    /// Rows withheld by the source's gate.
    RowsWithheld: int
    /// The signing profile in force.
    Signing: string
    /// The signing key, under the regulated profile.
    SignatureKeyId: string option
    /// The OTHER side's record of the same run.
    CounterpartRecordId: Guid option
    /// Why a refusal or a withdrawal happened.
    Reason: string option
}

/// What a deployment declares about team-to-team publication.
type FactPublicationConfig = {
    /// The consolidation tables publications may be written into.
    Targets: FactPublicationTarget list
    /// The signing profile runs are published under.
    Signing: PublicationSigning
}

/// Construction for `FactPublicationConfig`.
[<RequireQualifiedAccess>]
module FactPublicationConfig =

    /// Publication into `targets` under recorded provenance (the default
    /// profile).
    let create (targets: FactPublicationTarget list) : FactPublicationConfig = {
        Targets = targets
        Signing = PublicationSigning.RecordedProvenance
    }

    /// The same declaration under the regulated signing profile.
    let signed (config: FactPublicationConfig) : FactPublicationConfig = {
        config with
            Signing = PublicationSigning.SignedCertificate
    }

/// The team-to-team publication seam (Phase 897). Every member takes the
/// caller's minted scope; an owner act reads the acting owner from the
/// platform's resolved request viewer, never from an argument.
///
/// GP 12 audit: identity by value (grant, table and team ids are strings;
/// grants and receipts are values); async at every boundary; failure as data
/// (`PublicationRefusal`); the target's rows and ledger live in the target's
/// storage keyed by table and origin; no ordering across scopes; times at
/// second precision from the injected clock.
type IFactPublication =
    /// Propose a grant. The scope must be the source or the target team and
    /// the viewer that team's owner. Records no consent.
    abstract Propose:
        scope: ResolvedScope * proposal: PublicationProposal -> Async<Result<PublicationGrant, PublicationRefusal>>

    /// Record the calling team's consent. The scope must be one of the
    /// grant's two teams and the viewer that team's owner.
    abstract Consent: scope: ResolvedScope * grantId: string -> Async<Result<PublicationGrant, PublicationRefusal>>

    /// Withdraw a grant. The scope must be one of its teams and the viewer
    /// that team's owner. The target's rows go at its next refresh.
    abstract Revoke: scope: ResolvedScope * grantId: string -> Async<Result<PublicationGrant, PublicationRefusal>>

    /// The grants the scope's team is a party to, on either side.
    abstract Grants: scope: ResolvedScope -> Async<PublicationGrant list>

    /// Run one publication: read the source table in `source` through the
    /// gate and write it into the target through the seam. `source` is the
    /// scope a publication job runs under (`JobContext.Scope`).
    abstract Publish: source: ResolvedScope * grantId: string -> Async<Result<PublicationReceipt, PublicationRefusal>>

    /// Rewrite a consolidation table in the target's own scope from the
    /// origins still in force — the refresh at which a withdrawn origin's
    /// rows are removed. Returns the committed run's watermark token.
    abstract Refresh: target: ResolvedScope * tableId: string -> Async<Result<string, PublicationRefusal>>

// ─── What the target keeps per origin ────────────────────────────────

/// One published row as the target keeps it: the origin-prefixed row and
/// each cell's disclosure as the source published it.
type internal PublishedRow = {
    Row: FactTableRow
    Disclosures: Map<string, Disclosure>
}

/// The last publication an origin made into a consolidation table, kept in
/// the TARGET's scope — the table's current content is the union of these
/// over the origins in force.
type internal PublishedOrigin = {
    GrantId: string
    OriginTeam: string
    SourceTable: string
    OriginRun: string option
    PublicationRunId: string
    Rows: PublishedRow list
    Signing: PublicationSigning
    SignatureKeyId: string option
}

/// A grant as this service holds it: the record, and the minted scope the
/// target owner consented in — the scope the seam writes under.
type internal GrantEntry = {
    Grant: PublicationGrant
    ConsentedTarget: ResolvedScope option
}

/// The substrate a publication service runs over.
type internal FactPublicationDeps = {
    Store: IFactStore
    Storage: IBlobStorage
    Events: IEventStore
    Gate: IFactDisclosureGate
    Tables: IFactTableRegistry
    Registry: IMetricRegistry option
    Writer: IFactTableWriter option
    TeamRole: string -> string -> Async<TeamRole option>
    Signer: IArtefactSigner option
    Verifier: IArtefactVerifier option
    Clock: unit -> DateTime
    Config: FactPublicationConfig
    /// Phase 935 — the platform's scope carrier, when grants persist.
    /// `None` holds grants in process, as Phase 897 did.
    Carrier: ScopeCarrier option
}

/// Blob names and the canonical manifest under the target's scope.
module internal PublicationLedger =

    let root = "_fact-publication/"

    let originPrefix (tableId: string) = sprintf "%s%s/origins/" root tableId

    let originName (tableId: string) (originTeam: string) =
        sprintf "%s%s.json" (originPrefix tableId) originTeam

    let sha256Hex (text: string) : string =
        SHA256.HashData(Encoding.UTF8.GetBytes text)
        |> Array.map (fun b -> b.ToString "x2")
        |> String.concat ""

    /// The canonical bytes a signed run seals: every field that says what
    /// was published, in a fixed order, with the rows digested.
    let manifest (origin: PublishedOrigin) (targetTable: string) : byte[] =
        let rows =
            origin.Rows
            |> List.map (fun r -> FactTableBlobIo.serialize r)
            |> List.sort
            |> String.concat "\n"

        [
            "team-publication-manifest/1"
            "grant=" + origin.GrantId
            "origin=" + origin.OriginTeam
            "source-table=" + origin.SourceTable
            "target-table=" + targetTable
            "origin-run=" + (origin.OriginRun |> Option.defaultValue "")
            "run=" + origin.PublicationRunId
            "rows=" + sha256Hex rows
        ]
        |> String.concat "\n"
        |> Encoding.UTF8.GetBytes

    let signatureName (tableId: string) (originTeam: string) =
        sprintf "%s%s/signatures/%s.json" root tableId originTeam

/// The single-scope half of a consolidation: rewrite the target table from
/// the origins its ledger holds, dropping every origin whose grant is no
/// longer in force. Takes ONE scope id — the target's own.
module internal Consolidation =

    let private now (clock: unit -> DateTime) =
        let t = clock().ToUniversalTime()
        DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)

    let private storageFailure (e: FactTableWriteError) =
        PublicationStorageFailure(FactTableWriteError.describe e)

    /// The origins the target's ledger holds for a table.
    let ledger
        (deps: FactPublicationDeps)
        (targetScopeId: string)
        (tableId: string)
        : Async<Result<PublishedOrigin list, PublicationRefusal>> =
        async {
            let! names = deps.Storage.List(targetScopeId, PublicationLedger.originPrefix tableId)

            let rec load (remaining: string list) (acc: PublishedOrigin list) = async {
                match remaining with
                | [] -> return Ok(List.rev acc)
                | name :: rest ->
                    match! FactTableBlobIo.tryGet<PublishedOrigin> deps.Storage targetScopeId name with
                    | Ok(Some origin) -> return! load rest (origin :: acc)
                    | Ok None -> return! load rest acc
                    | Error e -> return Error(storageFailure e)
            }

            return! load (names |> List.filter (fun n -> n.EndsWith ".json") |> List.sort) []
        }

    /// Why an origin's grant no longer publishes, or `None` while in force.
    let withdrawalOf (lookup: string -> PublicationGrant option) (origin: PublishedOrigin) : string option =
        match lookup origin.GrantId with
        | Some grant when PublicationGrant.inForce grant -> None
        | Some({ Revocation = Some r } as grant) ->
            Some(
                sprintf
                    "withdrawn: publication grant %s from team %s was revoked by team %s at %s"
                    grant.GrantId
                    origin.OriginTeam
                    r.ByTeam
                    (r.At.ToString("yyyy-MM-ddTHH:mm:ssZ"))
            )
        | Some grant ->
            Some(
                sprintf
                    "withdrawn: publication grant %s from team %s is no longer in force"
                    grant.GrantId
                    origin.OriginTeam
            )
        | None ->
            Some(
                sprintf
                    "withdrawn: publication grant %s from team %s is not held by this deployment"
                    origin.GrantId
                    origin.OriginTeam
            )

    /// Write one run of the target table from the ledger's origins in force,
    /// removing every withdrawn origin's rows with absences naming why.
    let commit
        (deps: FactPublicationDeps)
        (targetScopeId: string)
        (table: FactTableDefinition)
        (lookup: string -> PublicationGrant option)
        (publicationRunId: string)
        : Async<Result<FactTableCommit * string list, PublicationRefusal>> =
        async {
            match! ledger deps targetScopeId table.Id with
            | Error e -> return Error e
            | Ok origins ->
                let withdrawals =
                    origins
                    |> List.choose (fun o -> withdrawalOf lookup o |> Option.map (fun why -> o.OriginTeam, why))
                    |> Map.ofList

                let inForce =
                    origins |> List.filter (fun o -> not (withdrawals.ContainsKey o.OriginTeam))

                let provenance =
                    ImportedRun(
                        origins
                        |> List.map (fun o -> {
                            RootMember = o.OriginTeam
                            CertificateRef = PublicationGrant.CertificateRefPrefix + o.GrantId
                            TriggerRef =
                                sprintf
                                    "team-publication-run:%s;origin:%s;origin-run:%s;received-in:%s"
                                    o.PublicationRunId
                                    o.OriginTeam
                                    (o.OriginRun |> Option.defaultValue "none")
                                    publicationRunId
                            Withdrawal = withdrawals.TryFind o.OriginTeam
                            Cells =
                                o.Rows
                                |> List.collect (fun r ->
                                    r.Disclosures
                                    |> Map.toList
                                    |> List.map (fun (metric, d) -> {
                                        Subject = r.Row.Subject
                                        Metric = metric
                                        From = r.Row.Period.From
                                        To = r.Row.Period.To
                                        Disclosure = d
                                    }))
                        })
                    )

                // The run goes through the COMPOSED writer (Phase 932), so its
                // decorations reach a consolidation run as they reach any
                // other. The imported provenance is staged on the run, and
                // only the default writer reads it — so a target bound
                // anywhere else is refused, as it always was.
                let writer =
                    deps.Writer
                    |> Option.defaultWith (fun () ->
                        DefaultFactTableWriter(
                            deps.Store,
                            deps.Storage,
                            deps.Events,
                            deps.Tables,
                            deps.Registry,
                            deps.Clock,
                            DefaultFactTableWriter.Destination
                        )
                        :> IFactTableWriter)

                let rows = inForce |> List.collect (fun o -> o.Rows |> List.map _.Row)

                match deps.Tables.DestinationOf table.Id with
                | boundTo when boundTo <> Some DefaultFactTableWriter.Destination ->
                    return
                        Error(
                            storageFailure (
                                FactTableNotBoundHere(table.Id, boundTo, DefaultFactTableWriter.Destination)
                            )
                        )
                | _ ->
                    match! writer.OpenRun(targetScopeId, table.Id) with
                    | Error e -> return Error(storageFailure e)
                    | Ok run ->
                        let! written = async {
                            match! FactTableRunProvenance.stage deps.Storage targetScopeId run.RunId provenance with
                            | Error e -> return Error e
                            | Ok() -> return! writer.WriteRows(targetScopeId, run.RunId, rows)
                        }

                        match written with
                        | Error e ->
                            let! _ = writer.Abandon(targetScopeId, run.RunId, FactTableWriteError.describe e)
                            return Error(storageFailure e)
                        | Ok _ ->
                            match! writer.Commit(targetScopeId, run.RunId) with
                            | Error e -> return Error(storageFailure e)
                            | Ok commit ->
                                // The withdrawn origins' rows are now absences;
                                // their ledger entries go with them.
                                for originTeam in withdrawals |> Map.keys do
                                    let! _ =
                                        deps.Storage.Delete(
                                            targetScopeId,
                                            PublicationLedger.originName table.Id originTeam
                                        )

                                    ()

                                return Ok(commit, withdrawals |> Map.keys |> List.ofSeq)
        }

    /// Write one audit record under the `_facts` source module.
    let audit
        (deps: FactPublicationDeps)
        (recordId: Guid)
        (scopeId: string)
        (eventType: string)
        (payload: FactPublicationEvent)
        : Async<unit> =
        async {
            try
                do!
                    deps.Events.Write {
                        Id = recordId
                        OccurredAt = now deps.Clock
                        ScopeId = scopeId
                        SourceModule = FactEvents.SourceModule
                        EventType = eventType
                        Payload = FactTableBlobIo.serialize payload
                    }
            with _ ->
                ()
        }

    /// The payload skeleton for a grant.
    let eventFor (deps: FactPublicationDeps) (grant: PublicationGrant) (side: PublicationSide) : FactPublicationEvent = {
        GrantId = grant.GrantId
        Side = PublicationSide.name side
        SourceTeam = grant.SourceTeam
        SourceTable = grant.SourceTable
        TargetTeam = grant.TargetTeam
        TargetTable = grant.TargetTable
        Visibility = PublicationVisibility.label grant.Visibility
        ByUserId = None
        PublicationRunId = None
        OriginRun = None
        TargetRun = None
        RowsPublished = 0
        RowsWithheld = 0
        Signing = PublicationSigning.name deps.Config.Signing
        SignatureKeyId = None
        CounterpartRecordId = None
        Reason = None
    }

// ─── The cross-scope write seam ──────────────────────────────────────
//
// SEAM-BEGIN — the one place in the fact tier that holds two teams' scopes.
// The guard pack reads this file and pins that the pair and the target
// consent's scope are touched between these markers and nowhere else.

/// What the seam wrote into the target.
type internal SeamWrite = {
    TargetRun: string
    TargetRecordId: Guid
}

/// The sanctioned cross-scope write (Phase 897).
module internal FactPublicationSeam =

    /// Pair the source's minted scope with the scope the target owner
    /// consented in. Refuses a grant whose target consent holds no scope.
    let pairFor (source: ResolvedScope) (entry: GrantEntry) : Result<ResolvedScopePair, PublicationRefusal> =
        match entry.ConsentedTarget with
        | None -> Error(PublicationGrantNotInForce(entry.Grant.GrantId, [ PublicationSide.Target ]))
        | Some target ->
            ResolvedScopePair.ofMinted source target
            |> Result.mapError (fun why ->
                PublicationWrongScope(entry.Grant.GrantId, PublicationSide.Target, entry.Grant.TargetTeam, why))

    /// Given a grant in force, write `origin` into the target team's table
    /// as one run of that table. Takes two minted scopes (the pair) and the
    /// grant; checks both sides against the grant before anything is
    /// written, verifies the regulated profile's signature, records the
    /// origin in the target's ledger, commits the run and writes the
    /// target's audit record citing the source's.
    let writeAcross
        (deps: FactPublicationDeps)
        (pair: ResolvedScopePair)
        (grant: PublicationGrant)
        (lookup: string -> PublicationGrant option)
        (table: FactTableDefinition)
        (origin: PublishedOrigin)
        (signature: ArtefactSignature option)
        (sourceRecordId: Guid)
        (withheld: int)
        : Async<Result<SeamWrite, PublicationRefusal>> =
        async {
            let target = pair.Target.ScopeId

            if not (PublicationGrant.inForce grant) then
                match grant.Revocation with
                | Some _ -> return Error(PublicationGrantRevoked grant.GrantId)
                | None ->
                    return Error(PublicationGrantNotInForce(grant.GrantId, PublicationGrant.missingConsents grant))
            elif pair.Source.ScopeId <> grant.SourceTeam then
                return
                    Error(
                        PublicationWrongScope(
                            grant.GrantId,
                            PublicationSide.Source,
                            grant.SourceTeam,
                            pair.Source.ScopeId
                        )
                    )
            elif target <> grant.TargetTeam then
                return Error(PublicationWrongScope(grant.GrantId, PublicationSide.Target, grant.TargetTeam, target))
            else
                let! verified = async {
                    match deps.Config.Signing, signature with
                    | PublicationSigning.RecordedProvenance, _ -> return Ok()
                    | PublicationSigning.SignedCertificate, None ->
                        return Error(PublicationSignatureRefused "the regulated profile requires a signed manifest")
                    | PublicationSigning.SignedCertificate, Some signature ->
                        match deps.Verifier with
                        | None -> return Error(PublicationSignatureRefused "no artefact verifier is composed")
                        | Some verifier ->
                            match! verifier.Verify(PublicationLedger.manifest origin table.Id, signature) with
                            | Ok() -> return Ok()
                            | Error e -> return Error(PublicationSignatureRefused(sprintf "%A" e))
                }

                match verified with
                | Error e -> return Error e
                | Ok() ->
                    match!
                        FactTableBlobIo.put
                            deps.Storage
                            target
                            (PublicationLedger.originName table.Id origin.OriginTeam)
                            origin
                    with
                    | Error e -> return Error(PublicationStorageFailure(FactTableWriteError.describe e))
                    | Ok() ->
                        match signature with
                        | Some s ->
                            let! _ =
                                FactTableBlobIo.put
                                    deps.Storage
                                    target
                                    (PublicationLedger.signatureName table.Id origin.OriginTeam)
                                    s

                            ()
                        | None -> ()

                        match! Consolidation.commit deps target table lookup origin.PublicationRunId with
                        | Error e -> return Error e
                        | Ok(commit, _) ->
                            let targetRun = FactTableWatermark.render commit.Watermark
                            let targetRecordId = Guid.NewGuid()

                            do!
                                Consolidation.audit deps targetRecordId target FactPublicationEvents.ReceivedType {
                                    Consolidation.eventFor deps grant PublicationSide.Target with
                                        PublicationRunId = Some origin.PublicationRunId
                                        OriginRun = origin.OriginRun
                                        TargetRun = Some targetRun
                                        RowsPublished = origin.Rows.Length
                                        RowsWithheld = withheld
                                        SignatureKeyId = origin.SignatureKeyId
                                        CounterpartRecordId = Some sourceRecordId
                                }

                            return
                                Ok {
                                    TargetRun = targetRun
                                    TargetRecordId = targetRecordId
                                }
        }

// SEAM-END

// ─── Phase 935 — publication grants survive a restart ────────────────
//
// A grant is written to durable storage on every owner act, with each
// consent's carried scope beside it: the platform's `ScopeCarrier` issues a
// token from the scope the consenting owner's request resolved to, sealed
// for this grant, this side and this consent. On restore the platform
// redeems the target's token into the scope the seam writes under — so a
// consolidation keeps receiving after a restart without either owner
// consenting again — and verifies the source's without holding it (the
// service holds one scope per grant; the seam is still the only place that
// holds two).
//
// A consent whose token does not redeem — absent, altered, issued by another
// key ring, or copied from another grant or another consent — is dropped on
// restore, with an audit record in both teams' scopes naming why. The grant
// is then not in force, so the target's next refresh withdraws the origin
// with a named reason, exactly as a restart did before this phase. The
// token binds the grant's own fields (teams, tables, visibility) and the
// consent's, so an edit of those in the store drops the consent rather than
// re-pointing it. What the store keeps outright is the rest of the record: a
// party that can write it can delete a grant, or replay an earlier state of
// one — including one from before a withdrawal, which the audit trail still
// records. None of that writes under a scope the platform did not resolve.
//
// Nothing here runs unless the service was composed with a carrier
// (`FactPublication.createDurable`); `createWith` holds grants in process
// exactly as Phase 897 did (GP 11).

/// Durable publication grants (Phase 935).
module internal GrantPersistence =

    /// The platform-reserved container grants are written under — they
    /// belong to two teams, so to neither team's scope.
    [<Literal>]
    let Container = "_platform"

    /// The blob prefix every grant is written under.
    [<Literal>]
    let Prefix = "_fact-publication/grants/"

    /// A grant's blob name.
    let name (grantId: string) = Prefix + grantId + ".json"

    /// The tokens a grant's consents were given with.
    type ConsentTokens = {
        Source: string option
        Target: string option
    }

    /// No consent carried yet.
    let noTokens = { Source = None; Target = None }

    /// What is written: the grant and its consents' tokens.
    type PersistedGrant = {
        Grant: PublicationGrant
        SourceConsentToken: string option
        TargetConsentToken: string option
    }

    /// The purpose a consent's token is issued and redeemed for: the grant
    /// and the consenting side, bound to a digest of every field the
    /// consent agreed to and of the consent itself.
    let purpose (grant: PublicationGrant) (side: PublicationSide) (consent: PublicationConsent) : CarriedScopePurpose =
        let digest =
            [
                grant.GrantId
                grant.SourceTeam
                grant.SourceTable
                grant.TargetTeam
                grant.TargetTable
                PublicationVisibility.label grant.Visibility
                PublicationSide.name side
                consent.TeamId
                consent.ByUserId
                consent.At.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
            ]
            |> FactTableBlobIo.serialize
            |> PublicationLedger.sha256Hex

        CarriedScopePurpose.Grant(
            "fact-publication.consent." + (PublicationSide.name side).ToLowerInvariant(),
            grant.GrantId + ":" + digest
        )

    /// The side's consent, when given.
    let consentOf (grant: PublicationGrant) (side: PublicationSide) : PublicationConsent option =
        match side with
        | PublicationSide.Source -> grant.SourceConsent
        | PublicationSide.Target -> grant.TargetConsent

    /// The tokens after an owner act by `side` in `scope`: a consent issues
    /// the side's token from the consenting scope; a withdrawal drops both,
    /// so the record of a withdrawn grant carries no scope at all.
    let afterAct
        (carrier: ScopeCarrier)
        (scope: ResolvedScope)
        (side: PublicationSide)
        (grant: PublicationGrant)
        (tokens: ConsentTokens)
        : Result<ConsentTokens, PublicationRefusal> =
        if grant.Revocation.IsSome then
            Ok noTokens
        else
            match consentOf grant side with
            | None -> Ok tokens
            | Some consent ->
                match carrier.Issue(scope, purpose grant side consent) with
                | Error reason -> Error(PublicationStorageFailure reason)
                | Ok token ->
                    match side with
                    | PublicationSide.Source -> Ok { tokens with Source = token }
                    | PublicationSide.Target -> Ok { tokens with Target = token }

    /// Write a grant and its tokens.
    let write
        (deps: FactPublicationDeps)
        (grant: PublicationGrant)
        (tokens: ConsentTokens)
        : Async<Result<unit, PublicationRefusal>> =
        async {
            let record = {
                Grant = grant
                SourceConsentToken = tokens.Source
                TargetConsentToken = tokens.Target
            }

            match! FactTableBlobIo.put deps.Storage Container (name grant.GrantId) record with
            | Ok() -> return Ok()
            | Error e -> return Error(PublicationStorageFailure(FactTableWriteError.describe e))
        }

    /// One grant restored: its entry, the tokens that redeemed, and why any
    /// consent was dropped.
    type Restored = {
        Entry: GrantEntry
        Tokens: ConsentTokens
        Dropped: (PublicationSide * string) list
    }

    /// Restore one persisted grant. A consent stands only when its token
    /// redeems for this grant, this side and this consent, into the scope of
    /// the team that side names; the target's redeemed scope is the one the
    /// seam writes under, and the source's is verified and not held.
    let restoreOne (carrier: ScopeCarrier) (record: PersistedGrant) : Restored =
        let grant = record.Grant

        let check (side: PublicationSide) (token: string option) (team: string) =
            match consentOf grant side, token with
            | None, _ -> Ok None
            | Some _, None -> Error "no carried scope was persisted with the consent"
            | Some consent, Some token ->
                match side with
                | PublicationSide.Source ->
                    match carrier.Verify(token, purpose grant side consent) with
                    | Ok scopeId when scopeId = team -> Ok None
                    | Ok scopeId -> Error(sprintf "the consent's carried scope is team '%s', not '%s'" scopeId team)
                    | Error refusal -> Error(CarriedScopeRefusal.describe refusal)
                | PublicationSide.Target ->
                    match carrier.Redeem(token, purpose grant side consent) with
                    | Ok scope when scope.ScopeId = team -> Ok(Some scope)
                    | Ok scope -> Error(sprintf "the consent's carried scope is team '%s', not '%s'" scope.ScopeId team)
                    | Error refusal -> Error(CarriedScopeRefusal.describe refusal)

        let source = check PublicationSide.Source record.SourceConsentToken grant.SourceTeam
        let target = check PublicationSide.Target record.TargetConsentToken grant.TargetTeam

        let dropped = [
            match source with
            | Error why -> PublicationSide.Source, why
            | Ok _ -> ()
            match target with
            | Error why -> PublicationSide.Target, why
            | Ok _ -> ()
        ]

        {
            Entry = {
                Grant = {
                    grant with
                        SourceConsent = (if Result.isOk source then grant.SourceConsent else None)
                        TargetConsent = (if Result.isOk target then grant.TargetConsent else None)
                }
                ConsentedTarget =
                    match target with
                    | Ok scope -> scope
                    | Error _ -> None
            }
            Tokens = {
                Source =
                    (if Result.isOk source then
                         record.SourceConsentToken
                     else
                         None)
                Target =
                    (if Result.isOk target then
                         record.TargetConsentToken
                     else
                         None)
            }
            Dropped = dropped
        }

    /// Every persisted grant, restored, with one audit record per dropped
    /// consent in each team's scope. An unreadable record restores nothing.
    let restore (deps: FactPublicationDeps) (carrier: ScopeCarrier) : Async<Restored list> = async {
        let! names = deps.Storage.List(Container, Prefix)

        let! loaded =
            names
            |> List.filter (fun n -> n.EndsWith ".json")
            |> List.sort
            |> List.map (fun blob -> async {
                match! FactTableBlobIo.tryGet<PersistedGrant> deps.Storage Container blob with
                | Ok(Some record) when not (isNull (box record.Grant)) -> return Some(restoreOne carrier record)
                | _ -> return None
            })
            |> Async.Sequential

        let restored = loaded |> Array.choose id |> List.ofArray

        for r in restored do
            for side, why in r.Dropped do
                let reason =
                    sprintf "the %s team's consent was not restored: %s" (PublicationSide.name side) why

                for team, auditSide in
                    [
                        r.Entry.Grant.SourceTeam, PublicationSide.Source
                        r.Entry.Grant.TargetTeam, PublicationSide.Target
                    ] do
                    do!
                        Consolidation.audit deps (Guid.NewGuid()) team FactPublicationEvents.RefusedType {
                            Consolidation.eventFor deps r.Entry.Grant auditSide with
                                Reason = Some reason
                        }

        return restored
    }

/// The default `IFactPublication`: grants held in process, the source read
/// through the gate at `FactTeamPublication`, the target written through
/// `FactPublicationSeam` alone.
type internal FactPublicationService(deps: FactPublicationDeps) =

    let grants = ConcurrentDictionary<string, GrantEntry>()
    let gate = obj ()

    // Phase 935 — each grant's consent tokens, and the grants an earlier
    // process persisted, restored before this service answers anything.
    let consentTokens = ConcurrentDictionary<string, GrantPersistence.ConsentTokens>()

    do
        match deps.Carrier with
        | None -> ()
        | Some carrier ->
            for restored in GrantPersistence.restore deps carrier |> Async.RunSynchronously do
                grants[restored.Entry.Grant.GrantId] <- restored.Entry
                consentTokens[restored.Entry.Grant.GrantId] <- restored.Tokens

    /// Phase 935 — one write at a time, so two owners acting on one grant at
    /// once cannot persist a record that has lost the other's token.
    let persistLock = new System.Threading.SemaphoreSlim(1, 1)

    /// Phase 935 — persist a grant after an owner act by `side` in `scope`:
    /// the grant as this service now holds it (a concurrent act included),
    /// with the acting side's token issued from `scope`. A no-op without a
    /// carrier.
    let persistAct (scope: ResolvedScope) (side: PublicationSide) (acted: PublicationGrant) = async {
        match deps.Carrier with
        | None -> return Ok()
        | Some carrier ->
            do! persistLock.WaitAsync() |> Async.AwaitTask

            try
                let grant =
                    match grants.TryGetValue acted.GrantId with
                    | true, entry -> entry.Grant
                    | _ -> acted

                let tokens =
                    match consentTokens.TryGetValue grant.GrantId with
                    | true, t -> t
                    | _ -> GrantPersistence.noTokens

                match GrantPersistence.afterAct carrier scope side grant tokens with
                | Error e -> return Error e
                | Ok next ->
                    match! GrantPersistence.write deps grant next with
                    | Error e -> return Error e
                    | Ok() ->
                        consentTokens[grant.GrantId] <- next
                        return Ok()
            finally
                persistLock.Release() |> ignore
    }

    let now () =
        let t = deps.Clock().ToUniversalTime()
        DateTime(t.Ticks - (t.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc)

    let lookup (grantId: string) : PublicationGrant option =
        match grants.TryGetValue grantId with
        | true, entry -> Some entry.Grant
        | _ -> None

    let targetOf (tableId: string) : FactPublicationTarget option =
        deps.Config.Targets |> List.tryFind (fun t -> t.Table = tableId)

    let hierarchyOf (table: FactTableDefinition) : SubjectDefinition option =
        deps.Registry |> Option.bind (fun r -> r.TryGetSubject table.Hierarchy)

    /// The owner check: the platform's resolved viewer must own the team
    /// the scope resolved to.
    let owner (scope: ResolvedScope) : Async<Result<string, PublicationRefusal>> = async {
        match RequestViewerContext.current () with
        | None -> return Error(PublicationNotOwner(scope.ScopeId, None))
        | Some viewer ->
            if scope.IsAnonymous then
                return Error(PublicationNotOwner(scope.ScopeId, Some viewer.UserId))
            else
                let! role = deps.TeamRole scope.ScopeId viewer.UserId

                match role with
                | Some TeamRole.Owner -> return Ok viewer.UserId
                | _ -> return Error(PublicationNotOwner(scope.ScopeId, Some viewer.UserId))
    }

    let partyAct
        (scope: ResolvedScope)
        (grantId: string)
        (act: GrantEntry -> PublicationSide -> string -> Result<GrantEntry, PublicationRefusal>)
        (eventType: string)
        : Async<Result<PublicationGrant, PublicationRefusal>> =
        async {
            match grants.TryGetValue grantId with
            | false, _ -> return Error(PublicationGrantUnknown grantId)
            | true, entry ->
                match PublicationGrant.sideOf scope.ScopeId entry.Grant with
                | None -> return Error(PublicationNotAParty(grantId, scope.ScopeId))
                | Some side ->
                    match! owner scope with
                    | Error e -> return Error e
                    | Ok userId ->
                        let result =
                            lock gate (fun () ->
                                let current = grants[grantId]

                                match act current side userId with
                                | Ok next ->
                                    grants[grantId] <- next
                                    Ok(current, next)
                                | Error e -> Error e)

                        match result with
                        | Error e -> return Error e
                        | Ok(previous, next) ->
                            // Phase 935 — a grant the store did not take is
                            // not changed here either.
                            match! persistAct scope side next.Grant with
                            | Error e ->
                                lock gate (fun () ->
                                    if obj.ReferenceEquals(grants[grantId], next) then
                                        grants[grantId] <- previous)

                                return Error e
                            | Ok() ->
                                do!
                                    Consolidation.audit deps (Guid.NewGuid()) scope.ScopeId eventType {
                                        Consolidation.eventFor deps next.Grant side with
                                            ByUserId = Some userId
                                            Reason =
                                                next.Grant.Revocation
                                                |> Option.map (fun _ -> "withdrawn by the team's owner")
                                    }

                                return Ok next.Grant
        }

    /// The source table's current rows, as the writer's lineage holds them
    /// in the source's own scope, grouped per subject and period.
    let sourceRows
        (source: ResolvedScope)
        (table: FactTableDefinition)
        (depth: int)
        : Async<((string list * TemporalExtent) * Map<string, Fact>) list> =
        async {
            let method =
                Computed(table.ProducingOperation, sprintf "v%d" table.SchemaVersion, table.Id)

            let! facts =
                deps.Store.Query(
                    source,
                    {
                        FactQuery.all with
                            Method = Some method
                    }
                )

            return
                facts
                |> List.filter (fun f -> f.Subject.Hierarchy = table.Hierarchy && f.Subject.Path.Length = depth)
                |> List.groupBy (fun f -> f.Subject.Path, f.Period)
                |> List.map (fun (key, cells) -> key, cells |> List.map (fun f -> f.Metric.Value, f) |> Map.ofList)
                // A row every cell of which is an absence is a row the source
                // removed, not a row it publishes.
                |> List.filter (fun (_, cells) ->
                    cells
                    |> Map.exists (fun _ f ->
                        match f.Value with
                        | Absent _ -> false
                        | _ -> true))
        }

    let refuse
        (source: ResolvedScope)
        (grant: PublicationGrant option)
        (grantId: string)
        (refusal: PublicationRefusal)
        : Async<Result<PublicationReceipt, PublicationRefusal>> =
        async {
            let payload =
                match grant with
                | Some g -> Consolidation.eventFor deps g PublicationSide.Source
                | None -> {
                    GrantId = grantId
                    Side = PublicationSide.name PublicationSide.Source
                    SourceTeam = source.ScopeId
                    SourceTable = ""
                    TargetTeam = ""
                    TargetTable = ""
                    Visibility = ""
                    ByUserId = None
                    PublicationRunId = None
                    OriginRun = None
                    TargetRun = None
                    RowsPublished = 0
                    RowsWithheld = 0
                    Signing = PublicationSigning.name deps.Config.Signing
                    SignatureKeyId = None
                    CounterpartRecordId = None
                    Reason = None
                  }

            do!
                Consolidation.audit deps (Guid.NewGuid()) source.ScopeId FactPublicationEvents.RefusedType {
                    payload with
                        Reason = Some(PublicationRefusal.describe refusal)
                }

            return Error refusal
        }

    let publish (source: ResolvedScope) (grantId: string) : Async<Result<PublicationReceipt, PublicationRefusal>> = async {
        match grants.TryGetValue grantId with
        | false, _ -> return! refuse source None grantId (PublicationGrantUnknown grantId)
        | true, entry ->
            let grant = entry.Grant
            let refuseWith = refuse source (Some grant) grantId

            if source.ScopeId <> grant.SourceTeam || source.IsAnonymous then
                return!
                    refuseWith (
                        PublicationWrongScope(grantId, PublicationSide.Source, grant.SourceTeam, source.ScopeId)
                    )
            elif grant.Revocation.IsSome then
                return! refuseWith (PublicationGrantRevoked grantId)
            elif not (PublicationGrant.inForce grant) then
                return! refuseWith (PublicationGrantNotInForce(grantId, PublicationGrant.missingConsents grant))
            else
                match targetOf grant.TargetTable, deps.Tables.TryGetTable grant.TargetTable with
                | None, _
                | _, None -> return! refuseWith (PublicationTargetUndeclared grant.TargetTable)
                | Some declaration, Some targetTable ->
                    match deps.Tables.TryGetTable grant.SourceTable with
                    | None -> return! refuseWith (PublicationSourceUndeclared grant.SourceTable)
                    | Some sourceTable ->
                        let sourceDepth =
                            hierarchyOf sourceTable
                            |> Option.bind (fun h -> FactTableDefinition.levelDepth h sourceTable.Level)

                        let targetDepth =
                            hierarchyOf targetTable
                            |> Option.bind (fun h -> FactTableDefinition.levelDepth h targetTable.Level)

                        let sourceShapes =
                            sourceTable.Columns |> List.map (fun c -> c.Metric, c.Shape) |> Map.ofList

                        let unfit =
                            targetTable.Columns
                            |> List.filter (fun c -> sourceShapes.TryFind c.Metric <> Some c.Shape)
                            |> List.map _.Metric

                        if not (PublicationVisibility.reaches grant.Visibility declaration.Requires) then
                            return!
                                refuseWith (PublicationVisibilityBelowRequired(grant.Visibility, declaration.Requires))
                        elif
                            sourceDepth.IsNone
                            || targetDepth.IsNone
                            || sourceDepth.Value + 1 <> targetDepth.Value
                        then
                            return!
                                refuseWith (
                                    PublicationShapeMismatch(
                                        sprintf
                                            "target '%s' sits one level below its origin, so a source row must sit at depth %s, but source '%s' sits at %s"
                                            targetTable.Id
                                            (targetDepth
                                             |> Option.map (fun d -> string (d - 1))
                                             |> Option.defaultValue "?")
                                            sourceTable.Id
                                            (sourceDepth
                                             |> Option.map string
                                             |> Option.defaultValue "no registered level")
                                    )
                                )
                        elif not (List.isEmpty unfit) then
                            return!
                                refuseWith (
                                    PublicationShapeMismatch(
                                        sprintf
                                            "source '%s' has no column of the same shape for %s"
                                            sourceTable.Id
                                            (String.concat ", " unfit)
                                    )
                                )
                        else
                            let! rows = sourceRows source sourceTable sourceDepth.Value
                            let columns = targetTable.Columns |> List.map _.Metric

                            let cells =
                                rows
                                |> List.collect (fun (_, byMetric) ->
                                    columns |> List.choose (fun m -> byMetric.TryFind m))

                            let principal = PublicationGrant.CertificateRefPrefix + grant.GrantId

                            let! verdicts =
                                match cells with
                                | [] -> async.Return Map.empty
                                | _ ->
                                    deps.Gate.Check(source, principal, FactTeamPublication, cells |> List.map _.FactId)

                            let disclosable (f: Fact) =
                                match verdicts.TryFind f.FactId with
                                | Some FactDisclosable -> true
                                | _ -> false

                            // A row leaves only when every cell it would carry
                            // passed the gate: a denied row is absent, never
                            // partly published.
                            let published, withheld =
                                rows
                                |> List.partition (fun (_, byMetric) ->
                                    columns
                                    |> List.forall (fun m ->
                                        match byMetric.TryFind m with
                                        | Some f -> disclosable f
                                        | None -> false))

                            let! originRun = async {
                                match deps.Writer with
                                | None -> return None
                                | Some writer ->
                                    match! writer.Status(source.ScopeId, sourceTable.Id) with
                                    | Ok status ->
                                        return
                                            status.LastCommit
                                            |> Option.map (fun c -> FactTableWatermark.render c.Watermark)
                                    | Error _ -> return None
                            }

                            let publicationRunId = Guid.NewGuid().ToString "N"

                            let origin: PublishedOrigin = {
                                GrantId = grant.GrantId
                                OriginTeam = grant.SourceTeam
                                SourceTable = sourceTable.Id
                                OriginRun = originRun
                                PublicationRunId = publicationRunId
                                Rows =
                                    published
                                    |> List.map (fun ((path, period), byMetric) -> {
                                        Row = {
                                            Subject = grant.SourceTeam :: path
                                            Period = period
                                            Values = columns |> List.map (fun m -> m, byMetric[m].Value) |> Map.ofList
                                        }
                                        Disclosures =
                                            columns |> List.map (fun m -> m, byMetric[m].Disclosure) |> Map.ofList
                                    })
                                Signing = deps.Config.Signing
                                SignatureKeyId = None
                            }

                            let! signed = async {
                                match deps.Config.Signing with
                                | PublicationSigning.RecordedProvenance -> return Ok(origin, None)
                                | PublicationSigning.SignedCertificate ->
                                    match deps.Signer with
                                    | None -> return Error(PublicationSignatureRefused "no artefact signer is composed")
                                    | Some signer ->
                                        let keyId = signer.KeyId()

                                        let sealedOrigin = {
                                            origin with
                                                SignatureKeyId = Some keyId
                                        }

                                        match! signer.Sign(PublicationLedger.manifest sealedOrigin targetTable.Id) with
                                        | Ok signature -> return Ok(sealedOrigin, Some signature)
                                        | Error e -> return Error(PublicationSignatureRefused(sprintf "%A" e))
                            }

                            match signed with
                            | Error e -> return! refuseWith e
                            | Ok(origin, signature) ->
                                match FactPublicationSeam.pairFor source entry with
                                | Error e -> return! refuseWith e
                                | Ok pair ->
                                    let sourceRecordId = Guid.NewGuid()

                                    match!
                                        FactPublicationSeam.writeAcross
                                            deps
                                            pair
                                            grant
                                            lookup
                                            targetTable
                                            origin
                                            signature
                                            sourceRecordId
                                            withheld.Length
                                    with
                                    | Error e -> return! refuseWith e
                                    | Ok written ->
                                        do!
                                            Consolidation.audit
                                                deps
                                                sourceRecordId
                                                source.ScopeId
                                                FactPublicationEvents.PublishedType
                                                {
                                                    Consolidation.eventFor deps grant PublicationSide.Source with
                                                        PublicationRunId = Some publicationRunId
                                                        OriginRun = originRun
                                                        TargetRun = Some written.TargetRun
                                                        RowsPublished = published.Length
                                                        RowsWithheld = withheld.Length
                                                        SignatureKeyId = origin.SignatureKeyId
                                                        CounterpartRecordId = Some written.TargetRecordId
                                                }

                                        return
                                            Ok {
                                                GrantId = grant.GrantId
                                                PublicationRunId = publicationRunId
                                                OriginRun = originRun
                                                RowsPublished = published.Length
                                                RowsWithheld = withheld.Length
                                                TargetRun = written.TargetRun
                                                SourceRecordId = sourceRecordId
                                                TargetRecordId = written.TargetRecordId
                                                Signing = deps.Config.Signing
                                            }
    }

    interface IFactPublication with

        member _.Propose(scope, proposal) = async {
            let side =
                if scope.ScopeId = proposal.SourceTeam then
                    Some PublicationSide.Source
                elif scope.ScopeId = proposal.TargetTeam then
                    Some PublicationSide.Target
                else
                    None

            if proposal.SourceTeam = proposal.TargetTeam then
                return Error(PublicationSameTeam proposal.SourceTeam)
            else
                match side with
                | None -> return Error(PublicationNotAParty("(proposal)", scope.ScopeId))
                | Some side ->
                    match targetOf proposal.TargetTable with
                    | None -> return Error(PublicationTargetUndeclared proposal.TargetTable)
                    | Some _ ->
                        match deps.Tables.TryGetTable proposal.SourceTable with
                        | None -> return Error(PublicationSourceUndeclared proposal.SourceTable)
                        | Some _ ->
                            match! owner scope with
                            | Error e -> return Error e
                            | Ok userId ->
                                let grant: PublicationGrant = {
                                    GrantId = Guid.NewGuid().ToString "N"
                                    SourceTeam = proposal.SourceTeam
                                    SourceTable = proposal.SourceTable
                                    TargetTeam = proposal.TargetTeam
                                    TargetTable = proposal.TargetTable
                                    Visibility = proposal.Visibility
                                    ProposedBy = userId
                                    ProposedAt = now ()
                                    SourceConsent = None
                                    TargetConsent = None
                                    Revocation = None
                                }

                                grants[grant.GrantId] <- {
                                    Grant = grant
                                    ConsentedTarget = None
                                }

                                // Phase 935 — persisted before it is
                                // reported; a grant the store did not take
                                // is not proposed.
                                match! persistAct scope side grant with
                                | Error e ->
                                    grants.TryRemove grant.GrantId |> ignore
                                    return Error e
                                | Ok() ->
                                    do!
                                        Consolidation.audit
                                            deps
                                            (Guid.NewGuid())
                                            scope.ScopeId
                                            FactPublicationEvents.ProposedType
                                            {
                                                Consolidation.eventFor deps grant side with
                                                    ByUserId = Some userId
                                            }

                                    return Ok grant
        }

        member _.Consent(scope, grantId) =
            partyAct
                scope
                grantId
                (fun entry side userId ->
                    match entry.Grant.Revocation with
                    | Some _ -> Error(PublicationGrantRevoked grantId)
                    | None ->
                        let consent = {
                            TeamId = scope.ScopeId
                            ByUserId = userId
                            At = now ()
                        }

                        match side with
                        | PublicationSide.Source ->
                            Ok {
                                entry with
                                    Grant = {
                                        entry.Grant with
                                            SourceConsent = Some consent
                                    }
                            }
                        | PublicationSide.Target ->
                            Ok {
                                Grant = {
                                    entry.Grant with
                                        TargetConsent = Some consent
                                }
                                ConsentedTarget = Some scope
                            })
                FactPublicationEvents.ConsentedType

        member _.Revoke(scope, grantId) =
            partyAct
                scope
                grantId
                (fun entry _ userId ->
                    match entry.Grant.Revocation with
                    | Some _ -> Ok entry
                    | None ->
                        Ok {
                            entry with
                                Grant = {
                                    entry.Grant with
                                        Revocation =
                                            Some {
                                                ByTeam = scope.ScopeId
                                                ByUserId = userId
                                                At = now ()
                                            }
                                }
                        })
                FactPublicationEvents.RevokedType

        member _.Grants(scope) = async {
            return
                grants.Values
                |> Seq.map _.Grant
                |> Seq.filter (fun g -> (PublicationGrant.sideOf scope.ScopeId g).IsSome)
                |> Seq.sortBy (fun g -> g.ProposedAt, g.GrantId)
                |> List.ofSeq
        }

        member _.Publish(source, grantId) = publish source grantId

        member _.Refresh(target, tableId) = async {
            if target.IsAnonymous then
                return Error(PublicationWrongScope("(refresh)", PublicationSide.Target, tableId, target.ScopeId))
            else
                match targetOf tableId, deps.Tables.TryGetTable tableId with
                | None, _
                | _, None -> return Error(PublicationTargetUndeclared tableId)
                | Some _, Some table ->
                    let refreshId = Guid.NewGuid().ToString "N"

                    match! Consolidation.commit deps target.ScopeId table lookup refreshId with
                    | Error e -> return Error e
                    | Ok(commit, withdrawn) ->
                        let token = FactTableWatermark.render commit.Watermark

                        do!
                            Consolidation.audit deps (Guid.NewGuid()) target.ScopeId FactPublicationEvents.RefreshedType {
                                GrantId = ""
                                Side = PublicationSide.name PublicationSide.Target
                                SourceTeam = ""
                                SourceTable = ""
                                TargetTeam = target.ScopeId
                                TargetTable = tableId
                                Visibility = ""
                                ByUserId = None
                                PublicationRunId = Some refreshId
                                OriginRun = None
                                TargetRun = Some token
                                RowsPublished = commit.RowCount
                                RowsWithheld = 0
                                Signing = PublicationSigning.name deps.Config.Signing
                                SignatureKeyId = None
                                CounterpartRecordId = None
                                Reason =
                                    if List.isEmpty withdrawn then
                                        None
                                    else
                                        Some(sprintf "withdrawn origins removed: %s" (String.concat ", " withdrawn))
                            }

                        return Ok token
        }

/// Construction for the default `IFactPublication`.
module FactPublication =

    /// The declaration's defects against the composed registries, or the
    /// empty list.
    let defects
        (config: FactPublicationConfig)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        : string list =
        config.Targets
        |> List.collect (fun target ->
            let table = tables.TryGetTable target.Table

            let hierarchy =
                table
                |> Option.bind (fun t -> registry |> Option.bind (fun r -> r.TryGetSubject t.Hierarchy))

            FactPublicationTarget.defects target table hierarchy)

    /// The one construction both public factories share (Phase 935).
    let private build
        (carrier: ScopeCarrier option)
        (config: FactPublicationConfig)
        (store: IFactStore)
        (storage: IBlobStorage)
        (events: IEventStore)
        (gate: IFactDisclosureGate)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (writer: IFactTableWriter option)
        (teamRole: string -> string -> Async<TeamRole option>)
        (signer: IArtefactSigner option)
        (verifier: IArtefactVerifier option)
        (clock: unit -> DateTime)
        : IFactPublication =
        match defects config tables registry with
        | _ :: _ as problems -> failwith ("withFactPublication: " + String.concat "; " problems)
        | [] ->
            match config.Signing, signer, verifier with
            | PublicationSigning.SignedCertificate, None, _
            | PublicationSigning.SignedCertificate, _, None ->
                failwith
                    "withFactPublication: the SignedCertificate profile needs an IArtefactSigner and an IArtefactVerifier composed."
            | _ ->
                FactPublicationService(
                    {
                        Store = store
                        Storage = storage
                        Events = events
                        Gate = gate
                        Tables = tables
                        Registry = registry
                        Writer = writer
                        TeamRole = teamRole
                        Signer = signer
                        Verifier = verifier
                        Clock = clock
                        Config = config
                        Carrier = carrier
                    }
                )
                :> IFactPublication

    /// The default publication service. `teamRole` answers a user's role in
    /// a team (`teamId`, then `userId`) — the owner check every owner act
    /// makes. The signer and verifier are required by the regulated profile
    /// and unused otherwise. Fails when the declaration has defects.
    /// Grants are held in process: a restart forgets them (Phase 897).
    let createWith
        (config: FactPublicationConfig)
        (store: IFactStore)
        (storage: IBlobStorage)
        (events: IEventStore)
        (gate: IFactDisclosureGate)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (writer: IFactTableWriter option)
        (teamRole: string -> string -> Async<TeamRole option>)
        (signer: IArtefactSigner option)
        (verifier: IArtefactVerifier option)
        (clock: unit -> DateTime)
        : IFactPublication =
        build None config store storage events gate tables registry writer teamRole signer verifier clock

    /// `createWith`, with grants that survive a restart (Phase 935): every
    /// owner act is persisted with each consent's scope carried by the
    /// platform's `ScopeCarrier`, and the grants an earlier process
    /// persisted are restored when the service is built — a consent whose
    /// carried scope does not redeem is dropped, with an audit record naming
    /// why. `FactsCompose.withFactPublication` builds this form when the
    /// deployment composed a carrier.
    let createDurable
        (carrier: ScopeCarrier)
        (config: FactPublicationConfig)
        (store: IFactStore)
        (storage: IBlobStorage)
        (events: IEventStore)
        (gate: IFactDisclosureGate)
        (tables: IFactTableRegistry)
        (registry: IMetricRegistry option)
        (writer: IFactTableWriter option)
        (teamRole: string -> string -> Async<TeamRole option>)
        (signer: IArtefactSigner option)
        (verifier: IArtefactVerifier option)
        (clock: unit -> DateTime)
        : IFactPublication =
        build (Some carrier) config store storage events gate tables registry writer teamRole signer verifier clock

/// The publication and refresh jobs (Phase 897). Publication is a job in
/// the SOURCE team's scope and a refresh a job in the TARGET's: each is
/// scheduled through the scheduler's typed overload with the scope the
/// owner's request resolved to, so it runs under that minted scope
/// (Phase 818). A job scheduled through the string overload runs under the
/// anonymous scope, which publishes and refreshes nothing.
module FactPublicationJobs =

    /// The publication job's handler name.
    [<Literal>]
    let PublishHandler = "_facts.publication.publish"

    /// The refresh job's handler name.
    [<Literal>]
    let RefreshHandler = "_facts.publication.refresh"

    let private outcome (result: Result<'T, PublicationRefusal>) : JobResult =
        match result with
        | Ok _ -> Success
        | Error(PublicationStorageFailure _ as e) -> TransientFailure(PublicationRefusal.describe e)
        | Error e -> PermanentFailure(PublicationRefusal.describe e)

    /// The handler that runs a publication: the payload is the grant id,
    /// and the source is the job's own scope.
    let publishHandler (publication: IFactPublication) : IJobHandler =
        { new IJobHandler with
            member _.Execute(ctx) = async {
                let! result = publication.Publish(ctx.Scope, ctx.Payload.Trim())
                return outcome result
            }
        }

    /// The handler that runs a refresh: the payload is the table id, and
    /// the target is the job's own scope.
    let refreshHandler (publication: IFactPublication) : IJobHandler =
        { new IJobHandler with
            member _.Execute(ctx) = async {
                let! result = publication.Refresh(ctx.Scope, ctx.Payload.Trim())
                return outcome result
            }
        }

    let private registration
        (handler: string)
        (payload: string)
        (trigger: Trigger)
        (createdBy: string)
        : JobRegistration =
        {
            ScopeId = ""
            Handler = handler
            Payload = payload
            Trigger = trigger
            Idempotency = None
            RetryPolicy = JobRetryPolicy.defaults
            ShardKey = Some payload
            Precision = JobPrecision.Minute
            CreatedBy = createdBy
            Tags = Map.ofList [ "origin", "team-publication" ]
        }

    /// Schedule a grant's publication under the source team's minted scope.
    let schedulePublication
        (scheduler: IJobScheduler)
        (source: ResolvedScope)
        (grantId: string)
        (trigger: Trigger)
        (createdBy: string)
        : Async<Result<JobId, ScheduleError>> =
        scheduler.Schedule(source, registration PublishHandler grantId trigger createdBy)

    /// Schedule a consolidation table's refresh under the target team's
    /// minted scope.
    let scheduleRefresh
        (scheduler: IJobScheduler)
        (target: ResolvedScope)
        (tableId: string)
        (trigger: Trigger)
        (createdBy: string)
        : Async<Result<JobId, ScheduleError>> =
        scheduler.Schedule(target, registration RefreshHandler tableId trigger createdBy)