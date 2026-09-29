// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open ToolUp.Platform.Grounding

// ─── Team-to-team fact publication (Phase 897) ───────────────────────
//
// One team holds a consolidated view of other teams' fact tables because
// those teams PUBLISHED them to it. Nothing reads across teams: the source
// team's publication job reads its own table in its own scope, through the
// disclosure gate at its own egress door, and one sanctioned seam writes the
// result as a run of the target team's declared table. Every question the
// target team then asks is an ordinary question inside its own scope.
//
// The types below are the vocabulary of that exchange — the grant, the two
// consents, the target's declaration, the refusals and the receipt. Plain
// data throughout (records, qualified unions, primitives, `DateTime`), so a
// client surface can render a grant or a refusal from the same types the
// server records (GP 10). No domain vocabulary: teams, tables and levels are
// opaque ids (GP 1).

/// The data-visibility level a publication grant carries — the Phase 642
/// vocabulary, restated here because the fact tier references no federation
/// package. Ordered: `AggregatesOnly` < `ViewOnly` < `Full`.
///
/// **What it governs for a table publication.** A declared fact table is a
/// population of COMPUTED metric values — a governed aggregate at every level
/// of its hierarchy — so publishing one needs `AggregatesOnly`, and that is
/// what a consolidation declares by default (`FactPublicationTarget.Requires`).
/// The level is an AUTHORITY check, exactly as at the federation door: a
/// target declaring a higher requirement is refused, by name, against a grant
/// below it. It never widens a fact's disclosure; that is the gate's and the
/// narrowing rule's business.
[<RequireQualifiedAccess>]
type PublicationVisibility =
    /// Governed aggregates and metadata only — the default.
    | AggregatesOnly
    /// Server-rendered bounded views.
    | ViewOnly
    /// Raw data.
    | Full

/// Functions over `PublicationVisibility`.
[<RequireQualifiedAccess>]
module PublicationVisibility =

    /// Every level, weakest first.
    let all: PublicationVisibility list = [
        PublicationVisibility.AggregatesOnly
        PublicationVisibility.ViewOnly
        PublicationVisibility.Full
    ]

    /// The stable label — the Phase 642 case names.
    let label (level: PublicationVisibility) : string =
        match level with
        | PublicationVisibility.AggregatesOnly -> "AggregatesOnly"
        | PublicationVisibility.ViewOnly -> "ViewOnly"
        | PublicationVisibility.Full -> "Full"

    /// The total order every comparison goes through.
    let rank (level: PublicationVisibility) : int =
        match level with
        | PublicationVisibility.AggregatesOnly -> 0
        | PublicationVisibility.ViewOnly -> 1
        | PublicationVisibility.Full -> 2

    /// Whether a grant at `granted` reaches what `required` demands.
    let reaches (granted: PublicationVisibility) (required: PublicationVisibility) : bool =
        rank granted >= rank required

/// Which side of a publication an act belongs to.
[<RequireQualifiedAccess>]
type PublicationSide =
    /// The team whose table is published.
    | Source
    /// The team whose table receives it.
    | Target

/// Functions over `PublicationSide`.
[<RequireQualifiedAccess>]
module PublicationSide =

    /// The stable name, as an audit row and a refusal state it.
    let name (side: PublicationSide) : string =
        match side with
        | PublicationSide.Source -> "Source"
        | PublicationSide.Target -> "Target"

/// One team's consent to a grant — an audited act by that team's owner.
type PublicationConsent = {
    /// The consenting team.
    TeamId: string
    /// The owner who consented, as the platform resolved them.
    ByUserId: string
    /// When the consent was recorded (UTC).
    At: DateTime
}

/// The withdrawal of a grant by either side's owner.
type PublicationRevocation = {
    /// The team whose owner withdrew the grant.
    ByTeam: string
    /// The owner who withdrew it.
    ByUserId: string
    /// When it was withdrawn (UTC).
    At: DateTime
}

/// A publication grant (Phase 897): the source team publishes one declared
/// table into one declared table of the target team, at a visibility level,
/// with both teams' consent. There is no notion of team rank; a grant is
/// never implied by one, and a grant with one consent publishes nothing.
type PublicationGrant = {
    /// The grant's identity, minted when it was proposed.
    GrantId: string
    /// The team whose table is published.
    SourceTeam: string
    /// The source team's declared table (`FactTableDefinition.Id`).
    SourceTable: string
    /// The team whose table receives the publication.
    TargetTeam: string
    /// The target team's declared consolidation table.
    TargetTable: string
    /// The visibility level the grant carries.
    Visibility: PublicationVisibility
    /// Who proposed the grant (an owner of either team).
    ProposedBy: string
    /// When it was proposed (UTC).
    ProposedAt: DateTime
    /// The source team owner's consent, once given.
    SourceConsent: PublicationConsent option
    /// The target team owner's consent, once given.
    TargetConsent: PublicationConsent option
    /// The withdrawal, once either side's owner withdrew it.
    Revocation: PublicationRevocation option
}

/// What an owner proposes: the source team's table, the target team's
/// table, and the visibility level. Proposing records no consent.
type PublicationProposal = {
    /// The team whose table would be published.
    SourceTeam: string
    /// The source team's declared table.
    SourceTable: string
    /// The team whose table would receive it.
    TargetTeam: string
    /// The target team's declared consolidation table.
    TargetTable: string
    /// The visibility level the grant would carry.
    Visibility: PublicationVisibility
}

/// Derivations over a `PublicationGrant`.
[<RequireQualifiedAccess>]
module PublicationGrant =

    /// The prefix of the provenance reference every row imported under a
    /// grant carries as its `Imported` method.
    [<Literal>]
    let CertificateRefPrefix = "team-publication:"

    /// The consents a grant still lacks, source first.
    let missingConsents (grant: PublicationGrant) : PublicationSide list = [
        if grant.SourceConsent.IsNone then
            PublicationSide.Source
        if grant.TargetConsent.IsNone then
            PublicationSide.Target
    ]

    /// Whether the grant is in force: both consents given and not withdrawn.
    let inForce (grant: PublicationGrant) : bool =
        grant.Revocation.IsNone && List.isEmpty (missingConsents grant)

    /// The side a team is on in a grant, or `None` when it is neither.
    let sideOf (teamId: string) (grant: PublicationGrant) : PublicationSide option =
        if teamId = grant.SourceTeam then
            Some PublicationSide.Source
        elif teamId = grant.TargetTeam then
            Some PublicationSide.Target
        else
            None

    /// The provenance reference a row imported under this grant carries
    /// (`MethodRef.Imported`). Stable for the grant's life, so a republished
    /// cell stays in one lineage and a withdrawal's absence supersedes it.
    let certificateRef (grant: PublicationGrant) : string = CertificateRefPrefix + grant.GrantId

/// How publications between teams of one deployment are signed (Phase 897).
[<RequireQualifiedAccess>]
type PublicationSigning =
    /// The default: each run carries recorded provenance — the grant, the
    /// origin team, the origin run and the audit records on both sides,
    /// which cite each other. One deployment, one trust boundary.
    | RecordedProvenance
    /// The regulated profile: each run's manifest is also sealed with the
    /// deployment's artefact signer by the source side and verified by the
    /// target side before anything is written.
    | SignedCertificate

/// Functions over `PublicationSigning`.
[<RequireQualifiedAccess>]
module PublicationSigning =

    /// The stable name.
    let name (signing: PublicationSigning) : string =
        match signing with
        | PublicationSigning.RecordedProvenance -> "RecordedProvenance"
        | PublicationSigning.SignedCertificate -> "SignedCertificate"

/// The target side's declaration of a consolidation table (Phase 897): which
/// declared table receives publications, and which level of its subject
/// hierarchy the ORIGIN TEAM occupies. The origin level is the hierarchy's
/// root, so a question scoped to one origin is a path-prefix population
/// query, and every row's path is the origin team followed by the source
/// row's own path.
type FactPublicationTarget = {
    /// The target team's declared table (`FactTableDefinition.Id`).
    Table: string
    /// The level of the table's hierarchy the origin team sits at. Must be
    /// the hierarchy's first (root) level.
    OriginLevel: string
    /// The visibility level a grant must reach to publish into this table.
    Requires: PublicationVisibility
}

/// Construction and validation for `FactPublicationTarget`.
[<RequireQualifiedAccess>]
module FactPublicationTarget =

    /// A consolidation table whose origin team sits at `originLevel`,
    /// requiring `AggregatesOnly` — what a population table is.
    let create (table: string) (originLevel: string) : FactPublicationTarget = {
        Table = table
        OriginLevel = originLevel
        Requires = PublicationVisibility.AggregatesOnly
    }

    /// Every problem with a declaration, given the table's definition and
    /// its registered hierarchy (either `None` when not declared), or the
    /// empty list. Each problem is a sentence naming the table.
    let defects
        (target: FactPublicationTarget)
        (table: FactTableDefinition option)
        (hierarchy: SubjectDefinition option)
        : string list =
        match table with
        | None -> [ sprintf "publication target '%s' is not a declared fact table" target.Table ]
        | Some table ->
            match hierarchy with
            | None -> [
                sprintf
                    "publication target '%s' sits on hierarchy '%s', which is not registered"
                    target.Table
                    table.Hierarchy
              ]
            | Some hierarchy -> [
                match hierarchy.Levels with
                | root :: _ when root = target.OriginLevel -> ()
                | _ ->
                    sprintf
                        "publication target '%s' declares origin level '%s', but the origin must be the root level of hierarchy '%s' (levels: %s)"
                        target.Table
                        target.OriginLevel
                        hierarchy.Id
                        (String.concat ", " hierarchy.Levels)

                match FactTableDefinition.levelDepth hierarchy table.Level with
                | Some depth when depth >= 2 -> ()
                | _ ->
                    sprintf
                        "publication target '%s' sits at level '%s', which leaves no level below the origin for the published rows"
                        target.Table
                        table.Level
              ]

/// Why a publication act was refused. Every refusal writes nothing to the
/// target and names what it refused.
type PublicationRefusal =
    /// No grant with this id exists.
    | PublicationGrantUnknown of grantId: string
    /// The acting scope is neither team of the grant.
    | PublicationNotAParty of grantId: string * scopeId: string
    /// The act needs the team's owner, and the viewer is not one (or no
    /// viewer was resolved).
    | PublicationNotOwner of teamId: string * userId: string option
    /// The grant lacks one or both consents.
    | PublicationGrantNotInForce of grantId: string * missing: PublicationSide list
    /// The grant was withdrawn.
    | PublicationGrantRevoked of grantId: string
    /// The act ran in a scope other than the one its side requires.
    | PublicationWrongScope of grantId: string * side: PublicationSide * expected: string * actual: string
    /// A grant's teams are one team.
    | PublicationSameTeam of teamId: string
    /// The source table is not declared.
    | PublicationSourceUndeclared of tableId: string
    /// The target table is not declared as a publication target.
    | PublicationTargetUndeclared of tableId: string
    /// The source table's rows do not fit the target's declaration.
    | PublicationShapeMismatch of detail: string
    /// The grant's visibility level is below the target's requirement.
    | PublicationVisibilityBelowRequired of granted: PublicationVisibility * required: PublicationVisibility
    /// The regulated profile's signature could not be produced or verified.
    | PublicationSignatureRefused of detail: string
    /// A read of the source's own table or a write of the target's failed.
    | PublicationStorageFailure of detail: string

/// Rendering for a `PublicationRefusal`.
[<RequireQualifiedAccess>]
module PublicationRefusal =

    /// A one-line, operator-readable account of the refusal.
    let describe (refusal: PublicationRefusal) : string =
        match refusal with
        | PublicationGrantUnknown grantId -> sprintf "no publication grant '%s' exists" grantId
        | PublicationNotAParty(grantId, scopeId) ->
            sprintf "scope '%s' is neither team of publication grant '%s'" scopeId grantId
        | PublicationNotOwner(teamId, userId) ->
            sprintf
                "only an owner of team '%s' may do this, and %s is not one"
                teamId
                (userId
                 |> Option.map (sprintf "'%s'")
                 |> Option.defaultValue "no resolved viewer")
        | PublicationGrantNotInForce(grantId, missing) ->
            sprintf
                "publication grant '%s' is not in force: it lacks the %s consent"
                grantId
                (missing |> List.map PublicationSide.name |> String.concat " and ")
        | PublicationGrantRevoked grantId -> sprintf "publication grant '%s' was withdrawn" grantId
        | PublicationWrongScope(grantId, side, expected, actual) ->
            sprintf
                "publication grant '%s' needs its %s side's scope '%s', not '%s'"
                grantId
                (PublicationSide.name side)
                expected
                actual
        | PublicationSameTeam teamId -> sprintf "a team cannot publish to itself ('%s')" teamId
        | PublicationSourceUndeclared tableId -> sprintf "source table '%s' is not a declared fact table" tableId
        | PublicationTargetUndeclared tableId ->
            sprintf "table '%s' is not declared as a publication target in this composition" tableId
        | PublicationShapeMismatch detail -> sprintf "the source table does not fit the target: %s" detail
        | PublicationVisibilityBelowRequired(granted, required) ->
            sprintf
                "the grant is at %s, below the %s the target table requires"
                (PublicationVisibility.label granted)
                (PublicationVisibility.label required)
        | PublicationSignatureRefused detail -> sprintf "the publication's signature was refused: %s" detail
        | PublicationStorageFailure detail -> sprintf "publication storage failed: %s" detail

/// What one publication run did (Phase 897). The two audit records — one in
/// each team's scope — carry the same run id and cite each other by id.
type PublicationReceipt = {
    /// The grant the run published under.
    GrantId: string
    /// The run's identity, shared by both audit records.
    PublicationRunId: string
    /// The source table's committed run the rows came from
    /// (`FactTableWatermark.render`), when it has one.
    OriginRun: string option
    /// Rows published into the target.
    RowsPublished: int
    /// Rows withheld because the source's gate denied at least one cell.
    RowsWithheld: int
    /// The target run's commit watermark (`FactTableWatermark.render`).
    TargetRun: string
    /// The source side's audit record id.
    SourceRecordId: Guid
    /// The target side's audit record id.
    TargetRecordId: Guid
    /// The signing profile the run was published under.
    Signing: PublicationSigning
}