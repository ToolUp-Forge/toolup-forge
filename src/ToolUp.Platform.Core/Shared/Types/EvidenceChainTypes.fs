// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Phase 713 — the evidence chain as data ──────────────────────────
//
// The links exist and the walk across them does not. A deployment can
// record an upstream work record (Phase 712), a build transcript and a
// resolved dependency closure (Phase 656 / 659), a sealed deploy record
// and a boot verification verdict (Phase 656 / 657), a signed compliance
// evidence pack (Phase 187), and a hash-chained audit ledger (Phase
// 658). Nothing walks them in ONE ordered traversal, so "show me this
// running workload back to the work that authored it" is an
// investigation rather than a query.
//
// This file is the **wire shape** of that traversal: an ordered list of
// hops, each carrying its own typed link verdict. Pure projection —
// records, two unions, and the total functions that fold hops into an
// outcome and frame them for a digest. No I/O, no clock, no crypto (the
// digest is computed server-side over the canonical form declared here).
// Kept Fable-safe per GP 10 so an admin panel renders the same shape
// without re-deriving it.
//
// ── The design decision the artefact is worth nothing without ────────
//
// **A break is a first-class value, never an omission.** Each hop
// resolves to `Linked`, `LinkAbsent`, `LinkBroken` or `LinkWithheld`,
// and a walk ALWAYS returns the full ordered hop list including the
// failures. This is the not-proved discipline of the deployment
// verification report applied to a traversal: a chain that renders only
// its healthy links reads as complete and is not, and a reader cannot
// otherwise tell a deployment that never composed a ledger from one
// whose ledger is broken. Both must be sayable, and distinguishable.
//
// The corollary is that **hop COUNT is invariant to what is composed**.
// A wholly-uncomposed deployment yields a complete hop list of absences,
// which is a meaningful answer rather than an error. That invariance is
// structural rather than remembered: `EvidenceChain.hops` takes an
// `EvidenceChainLinks` record with one field per stage, so a walk cannot
// produce six links for seven hops — the compiler counts them.
//
// ── A verdict is about the LINK, not the node ────────────────────────
//
// Read every case below as a statement about the JOIN between two
// recorded facts, not about whether either fact exists. A build
// transcript sitting in hand that no deploy record names is not a
// present link; it is a recorded artefact with nothing joining it to
// this deployment, so its hop reads `LinkAbsent` and says exactly that.
// The distinction matters because the question the chain answers is
// whether a reader can walk from one end to the other, and a hop that
// reported the presence of its endpoint would answer a different and
// much weaker question.

/// The verdict for one hop of an evidence chain.
///
/// **Four cases, and three of them are non-affirmative in different
/// ways.** A reader's next action differs completely between "nothing
/// joins these two facts in this deployment" (compose the missing
/// substrate, or accept the bound), "the join is recorded and does not
/// hold" (respond to the finding — this is the state tampering
/// produces), and "the join exists and you may not see it" (ask the
/// holder, under whatever policy refused it). Collapsing any pair loses
/// exactly the information the walk was run for.
[<RequireQualifiedAccess>]
type EvidenceLink =
    /// The hop resolves. `reference` is the join key a reader can
    /// re-derive the link from — a digest, a record id, a ledger
    /// position — and `detail` quotes the proof rather than the
    /// conclusion.
    | Linked of reference: string * detail: string
    /// Nothing in this deployment joins the two facts this hop spans.
    /// The substrate was never composed, or it is composed and records
    /// no join. Never a failure and emphatically never a pass; `reason`
    /// names what would have to be composed for the hop to say
    /// something.
    | LinkAbsent of reason: string
    /// The join is recorded and does not hold. `position` names WHERE
    /// the break sits — the digest that disagreed, the ledger index that
    /// failed to chain, the record the lookup died on — so the finding
    /// is actionable without a second read.
    | LinkBroken of position: string * reason: string
    /// The join exists and the holder refused to show it. `policy` is
    /// the refusing system's own policy reference, carried verbatim and
    /// never interpreted here.
    ///
    /// **Not a variety of `LinkAbsent`.** "This exists and you may not
    /// see it" and "there is nothing here" are different answers to a
    /// reader auditing a deployment, and collapsing them turns a working
    /// access control into apparent missing data.
    | LinkWithheld of policy: string

[<RequireQualifiedAccess>]
module EvidenceLink =

    /// Stable lowercase wire label. Never localised — this is what a
    /// dashboard cuts on and what the audit row records.
    let label =
        function
        | EvidenceLink.Linked _ -> "linked"
        | EvidenceLink.LinkAbsent _ -> "absent"
        | EvidenceLink.LinkBroken _ -> "broken"
        | EvidenceLink.LinkWithheld _ -> "withheld"

    /// The link's own account of itself — the detail, reason or policy
    /// the case carries.
    let detail =
        function
        | EvidenceLink.Linked(_, detail) -> detail
        | EvidenceLink.LinkAbsent reason -> reason
        | EvidenceLink.LinkBroken(_, reason) -> reason
        | EvidenceLink.LinkWithheld policy -> policy

    /// The join key, where the hop has one. `LinkBroken` carries its
    /// position here so a canonical form covers the two cases that name
    /// a location in one field.
    let reference =
        function
        | EvidenceLink.Linked(reference, _) -> reference
        | EvidenceLink.LinkBroken(position, _) -> position
        | EvidenceLink.LinkAbsent _
        | EvidenceLink.LinkWithheld _ -> ""

    /// `true` only for `Linked`. Provided so a caller never has to write
    /// the fold itself and accidentally admit a withheld hop.
    let isLinked =
        function
        | EvidenceLink.Linked _ -> true
        | _ -> false

    /// `true` only for `LinkBroken` — the one case that is a finding
    /// rather than a bound.
    let isBroken =
        function
        | EvidenceLink.LinkBroken _ -> true
        | _ -> false

/// One hop of the walk: which join it spans, where it sits in the order,
/// what the join said, and the evidence lines behind that verdict.
///
/// `Findings` is separate from the link's own detail for the reason the
/// verification report separates them: the verdict is one line a reader
/// takes in, the findings are the enumeration behind it (which closure
/// entries are unattested, which ancestors were withheld). A verdict
/// that tried to carry both would be neither.
type EvidenceHop = {
    /// Stable id — one of the `*Hop` literals in `EvidenceChain`.
    Id: string
    /// Human title for a rendered chain.
    Title: string
    /// 1-based position in the walk. Carried rather than inferred from
    /// list index so a consumer that filters or re-orders for display
    /// cannot silently renumber the walk.
    Ordinal: int
    Link: EvidenceLink
    /// The enumeration behind the link, in the order the walk produced
    /// it. Empty is normal and means the link's own detail is the whole
    /// of what was found.
    Findings: string list
}

/// The link and the evidence lines for one hop, before the hop's
/// identity and ordinal are attached.
type EvidenceHopOutcome = {
    Link: EvidenceLink
    Findings: string list
}

[<RequireQualifiedAccess>]
module EvidenceHopOutcome =

    /// A hop outcome carrying no enumeration behind its verdict.
    let bare (link: EvidenceLink) : EvidenceHopOutcome = { Link = link; Findings = [] }

/// One outcome per stage of the walk.
///
/// **A record rather than a list, and that is the whole of how hop-count
/// invariance is enforced.** A list lets a walk return five hops for a
/// sparsely-composed deployment and seven for a rich one, which is
/// precisely the silently-shorter chain this phase exists to make
/// impossible. Every field here is mandatory, so a stage cannot be
/// dropped without the walk failing to compile.
type EvidenceChainLinks = {
    /// The deploy's join to the upstream work record that authored its
    /// sources (Phase 712).
    UpstreamWorkRecord: EvidenceHopOutcome
    /// The deploy record's join to the build transcript it was built
    /// under (Phase 656).
    BuildTranscript: EvidenceHopOutcome
    /// The deploy record's join to the resolved dependency closure
    /// (Phase 659).
    DependencyClosure: EvidenceHopOutcome
    /// The deploy record's join to its own seal (Phase 656).
    DeployRecord: EvidenceHopOutcome
    /// The sealed composition's join to the composition that actually
    /// booted (Phase 657).
    BootVerification: EvidenceHopOutcome
    /// The deployment's join to a signed compliance evidence pack
    /// (Phase 187).
    EvidencePack: EvidenceHopOutcome
    /// The join to a position in the hash-chained audit ledger (Phase
    /// 658).
    LedgerPosition: EvidenceHopOutcome
}

/// The chain's top-line shape.
///
/// Deliberately **not** a verdict and deliberately not orderable: it
/// describes the hop set, so a reader who takes only this line is told
/// how much of the walk resolved rather than being handed a pass the
/// deployment did not earn.
[<RequireQualifiedAccess>]
type EvidenceChainOutcome =
    /// Not one hop resolved. The chain is honest and entirely empty of
    /// joins — the state a bare deployment is in, and it is not a
    /// failure because nothing is broken.
    | ChainUnrecorded
    /// Every hop resolved.
    | ChainComplete
    /// At least one hop resolved and at least one did not, and none is
    /// broken. The ordinary state of a partially-composed deployment.
    | ChainPartial
    /// At least one hop is broken. The finding a reader acts on.
    | ChainBroken

[<RequireQualifiedAccess>]
module EvidenceChainOutcome =

    /// Stable lowercase wire label.
    let label =
        function
        | EvidenceChainOutcome.ChainUnrecorded -> "chain-unrecorded"
        | EvidenceChainOutcome.ChainComplete -> "chain-complete"
        | EvidenceChainOutcome.ChainPartial -> "chain-partial"
        | EvidenceChainOutcome.ChainBroken -> "chain-broken"

/// One walked chain.
///
/// `VerdictDigest` is a SHA-256 over the canonical form below, computed
/// server-side. It is what the audited-read record commits to: the audit
/// row carries the digest rather than the chain, so the trail proves
/// what was walked without copying a deployment-wide evidence summary
/// onto a surface with its own readership.
type EvidenceChain = {
    SchemaVersion: int
    /// Who walked. The audited read's subject.
    Actor: string
    WalkedAt: DateTime
    /// The full ordered hop list, failures included. Always the same
    /// length, whatever the deployment composes.
    Hops: EvidenceHop list
    Outcome: EvidenceChainOutcome
    VerdictDigest: string
}

/// The bounds a walker declares, so a caller can size its request
/// instead of discovering the limit as a refusal.
///
/// Mirrors the declared-caps property the upstream work seam already
/// carries, and for the same reason: a shortened answer is
/// indistinguishable from a complete one, so both axes refuse rather
/// than trim.
type EvidenceChainCaps = {
    /// The largest `WorkDepth` a request may ask for. A request above it
    /// is refused before anything is fetched.
    MaxWorkDepth: int
    /// The largest dependency closure a single walk may account for. A
    /// closure above it refuses the WALK, never trims the closure — a
    /// chain reporting a truncated closure would tell a reader the build
    /// stands on less than it does.
    MaxClosureEntries: int
}

[<RequireQualifiedAccess>]
module EvidenceChainCaps =

    /// The shipped defaults, matching the upstream work seam's own
    /// bounds so a deployment reasoning about both does not have to hold
    /// two numbers. A response-size bound, not a modelling statement.
    let defaults = {
        MaxWorkDepth = 10
        MaxClosureEntries = 2000
    }

    /// The bounds a walker that answers nothing declares. Distinct from
    /// `defaults` on purpose: a caller reading zeroes learns that this
    /// deployment walks nothing, rather than sizing a request that will
    /// always come back empty.
    let none = {
        MaxWorkDepth = 0
        MaxClosureEntries = 0
    }

/// A bounded walk request: who is asking, and how far back through the
/// upstream work chain.
type EvidenceChainRequest = {
    /// Who is walking. Recorded on the audited read; never used to
    /// authorise anything here.
    Actor: string
    /// Hops to walk back through the upstream work chain. Must be at
    /// least 1 and at most the declared `MaxWorkDepth`; anything else is
    /// refused rather than clamped.
    WorkDepth: int
}

[<RequireQualifiedAccess>]
module EvidenceChainRequest =

    /// The default depth a request asks for when the caller states none.
    /// One hop — the covering record itself — because a walk that
    /// silently pulled an ancestor tree nobody asked for would make the
    /// cheap question expensive by default.
    [<Literal>]
    let DefaultWorkDepth = 1

    /// A request at the default depth.
    let forActor (actor: string) : EvidenceChainRequest = {
        Actor = actor
        WorkDepth = DefaultWorkDepth
    }

/// Why a walk was refused. Every case names both what was asked and what
/// the limit is, so a caller can correct the request without a second
/// round-trip.
///
/// **A refusal is not an absent chain.** A deployment composing nothing
/// gets a complete hop list of absences — a meaningful answer. These are
/// the cases where the walk could not be performed AS ASKED, and
/// answering them with a chain would be answering a different question.
type EvidenceChainError =
    /// `WorkDepth` was below 1. A zero-or-negative walk is a caller bug,
    /// not a request for the covering record on its own.
    | ChainWorkDepthInvalid of requested: int
    /// `WorkDepth` exceeded the walker's declared `MaxWorkDepth`.
    | ChainWorkDepthExceedsCap of requested: int * cap: int
    /// The recorded dependency closure carries more entries than the
    /// declared `MaxClosureEntries`. The walk is refused whole — nothing
    /// is truncated.
    | ChainClosureExceedsCap of entries: int * cap: int

[<RequireQualifiedAccess>]
module EvidenceChainError =

    /// Human-readable refusal text. One place, so a diagnostic, a test
    /// and a consumer's error surface all read the same wording.
    let describe (error: EvidenceChainError) : string =
        match error with
        | ChainWorkDepthInvalid requested ->
            $"work depth {requested} is invalid — an evidence chain walk needs at least 1 hop"
        | ChainWorkDepthExceedsCap(requested, cap) ->
            $"work depth {requested} exceeds this deployment's evidence chain depth cap of {cap}"
        | ChainClosureExceedsCap(entries, cap) ->
            $"the recorded dependency closure carries {entries} entries, above this deployment's cap of {cap} — the walk is refused rather than trimmed, because a reader cannot tell a trimmed closure from a complete one"

/// What the boot verification preflight said, mirrored into the chain's
/// own vocabulary.
///
/// Tier-neutral on purpose: the preflight's verdict type compiles in a
/// tier this file cannot see, and mirroring it keeps the chain shape
/// renderable by any consumer. Three cases where the source has more —
/// what the chain needs is whether the running composition JOINS to the
/// sealed one, and the source's finer distinctions ride in `detail`.
[<RequireQualifiedAccess>]
type BootVerificationReading =
    /// The running composition is the sealed one.
    | BootVerified of detail: string
    /// The preflight ran and had nothing to compare against. Honest,
    /// legitimate, and emphatically not a verification.
    | BootUnsealed of reason: string
    /// The preflight ran and its answer was adverse. `position` names
    /// what disagreed.
    | BootRejected of position: string * detail: string

/// What a compliance evidence pack read reported, mirrored.
[<RequireQualifiedAccess>]
type EvidencePackReading =
    /// A pack was assembled and its manifest signed. `manifestDigest`
    /// names the signed bytes; `entries` counts the segments the
    /// manifest pins.
    | PackSigned of manifestDigest: string * entries: int
    /// A pack was assembled and carries no signature — a valid bundle
    /// that binds nothing, because no envelope signer is composed.
    | PackUnsigned of manifestDigest: string * entries: int
    /// A pack exists and the holder refused it under a stated policy.
    | PackWithheld of policy: string

/// Where this deployment's evidence sits in a hash-chained audit ledger,
/// mirrored.
[<RequireQualifiedAccess>]
type LedgerPositionReading =
    /// The chain walked clean to `position`, whose head is `headDigest`.
    | LedgerRecorded of position: int64 * headDigest: string
    /// The ledger is readable and holds no position for this
    /// deployment's evidence.
    | LedgerUnrecorded of reason: string
    /// The walk found a break. `position` is the FIRST one; everything
    /// after it is meaningless, which is why only the first is carried.
    | LedgerBroken of position: int64 * detail: string

module EvidenceChain =

    /// Schema version of `EvidenceChain`.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version for the chain's canonical form. Part of the
    /// framed string the verdict digest is taken over, so a chain
    /// canonicalised under a future scheme can never collide with one
    /// canonicalised under this.
    [<Literal>]
    let FramingVersion = "toolup.evidencechain.v1"

    // ── Stable hop ids ───────────────────────────────────────────────
    //
    // String literals rather than a closed union, for the reason the
    // verification report's section ids are literals: a later phase
    // adding an eighth hop must not stop every consumer that matched on
    // seven from compiling. The ids themselves never change. The
    // COUNT is pinned by `EvidenceChainLinks` on the producing side,
    // where a missing stage is a compile error rather than a wire break.

    [<Literal>]
    let UpstreamWorkRecordHop = "upstream-work-record"

    [<Literal>]
    let BuildTranscriptHop = "build-transcript"

    [<Literal>]
    let DependencyClosureHop = "dependency-closure"

    [<Literal>]
    let DeployRecordHop = "deploy-record"

    [<Literal>]
    let BootVerificationHop = "boot-verification"

    [<Literal>]
    let EvidencePackHop = "evidence-pack"

    [<Literal>]
    let LedgerPositionHop = "ledger-position"

    /// The hop ids in walk order — from the work that authored the
    /// sources to the ledger position that anchors the evidence.
    let order = [
        UpstreamWorkRecordHop
        BuildTranscriptHop
        DependencyClosureHop
        DeployRecordHop
        BootVerificationHop
        EvidencePackHop
        LedgerPositionHop
    ]

    /// Human title for a hop id. Unknown ids echo themselves rather than
    /// throwing — a consumer holding a chain from a newer schema should
    /// render it, not fail on it.
    let titleOf (hopId: string) : string =
        match hopId with
        | UpstreamWorkRecordHop -> "Upstream work record"
        | BuildTranscriptHop -> "Build transcript"
        | DependencyClosureHop -> "Dependency closure"
        | DeployRecordHop -> "Sealed deploy record"
        | BootVerificationHop -> "Boot verification"
        | EvidencePackHop -> "Compliance evidence pack"
        | LedgerPositionHop -> "Audit ledger position"
        | other -> other

    /// Build the ordered hop list from one outcome per stage.
    ///
    /// The single construction point, so hop count, hop order and hop
    /// titles have exactly one definition. A walk cannot reach a
    /// different length by any path.
    let hops (links: EvidenceChainLinks) : EvidenceHop list =
        let outcomes = [
            UpstreamWorkRecordHop, links.UpstreamWorkRecord
            BuildTranscriptHop, links.BuildTranscript
            DependencyClosureHop, links.DependencyClosure
            DeployRecordHop, links.DeployRecord
            BootVerificationHop, links.BootVerification
            EvidencePackHop, links.EvidencePack
            LedgerPositionHop, links.LedgerPosition
        ]

        outcomes
        |> List.mapi (fun index (hopId, outcome) -> {
            Id = hopId
            Title = titleOf hopId
            Ordinal = index + 1
            Link = outcome.Link
            Findings = outcome.Findings
        })

    /// Every stage absent, with one reason. The shape a walker with no
    /// sources at all produces, and the value a consumer can build a
    /// wholly-uncomposed chain from without enumerating the stages.
    let allAbsent (reason: string) : EvidenceChainLinks =
        let absent = EvidenceHopOutcome.bare (EvidenceLink.LinkAbsent reason)

        {
            UpstreamWorkRecord = absent
            BuildTranscript = absent
            DependencyClosure = absent
            DeployRecord = absent
            BootVerification = absent
            EvidencePack = absent
            LedgerPosition = absent
        }

    /// Fold the hop links into the top-line outcome. Total, pure, and
    /// the only place the precedence lives:
    ///   * any broken hop       ⇒ `ChainBroken`;
    ///   * else no linked hop   ⇒ `ChainUnrecorded`;
    ///   * else any non-linked  ⇒ `ChainPartial`;
    ///   * else                 ⇒ `ChainComplete`.
    ///
    /// Note a withheld hop depresses the outcome to `ChainPartial` and
    /// never to `ChainBroken`: a refusal is a working control, not a
    /// finding, and reddening on one would teach a reader to route around
    /// the control rather than ask its holder.
    let outcomeOf (hops: EvidenceHop list) : EvidenceChainOutcome =
        let links = hops |> List.map _.Link

        if links |> List.exists EvidenceLink.isBroken then
            EvidenceChainOutcome.ChainBroken
        elif not (links |> List.exists EvidenceLink.isLinked) then
            EvidenceChainOutcome.ChainUnrecorded
        elif links |> List.exists (EvidenceLink.isLinked >> not) then
            EvidenceChainOutcome.ChainPartial
        else
            EvidenceChainOutcome.ChainComplete

    /// The canonical form the verdict digest is taken over: the framing
    /// version, the schema version, then one line per hop in walk order.
    ///
    /// **Deliberately excludes `WalkedAt`, `Actor` and the hop
    /// `Findings`.** The digest names the LINK SET, so two walks a
    /// minute apart against an unchanged deployment produce the same
    /// digest and a reader sees at a glance that nothing moved. A digest
    /// that folded in the clock would change on every walk and commit to
    /// nothing.
    let canonicalForm (hops: EvidenceHop list) : string =
        let sb = StringBuilder()
        sb.Append(FramingVersion).Append('\n') |> ignore
        sb.Append(SchemaVersion).Append('\n') |> ignore

        // Length-prefixed fields, so the canonical form is injective
        // over free-text detail without needing a separator byte the
        // detail could contain. A delimiter-only scheme would let two
        // different link sets frame to identical bytes.
        let field (value: string) =
            let value = if isNull value then "" else value
            sb.Append(value.Length).Append(':').Append(value).Append(';') |> ignore

        // An explicit LF, never `AppendLine`, which emits
        // `Environment.NewLine` — the same chain would then frame to
        // different bytes on Windows and Linux and the digest would stop
        // being a property of the chain.
        for hop in hops do
            field "hop"
            field hop.Id
            field (string hop.Ordinal)
            field (EvidenceLink.label hop.Link)
            field (EvidenceLink.reference hop.Link)
            field (EvidenceLink.detail hop.Link)
            sb.Append('\n') |> ignore

        sb.ToString()

    /// Render a chain as operator-facing text — the shape a support
    /// bundle quotes. Pure, so a test asserts on it directly.
    ///
    /// Every hop renders, failures included and in walk order. A
    /// renderer that skipped the absent hops would reintroduce, at the
    /// last possible moment, exactly the silence the model exists to
    /// prevent.
    let render (chain: EvidenceChain) : string =
        let sb = StringBuilder()
        sb.AppendLine "── Evidence chain ──" |> ignore

        sb.AppendLine(sprintf "  walked %s by %s" (chain.WalkedAt.ToString "o") chain.Actor)
        |> ignore

        sb.AppendLine(sprintf "  outcome: %s" (EvidenceChainOutcome.label chain.Outcome))
        |> ignore

        sb.AppendLine(sprintf "  verdict digest: %s" chain.VerdictDigest) |> ignore
        sb.AppendLine "" |> ignore

        for hop in chain.Hops do
            sb.AppendLine(
                sprintf
                    "  %d. [%s] %s — %s"
                    hop.Ordinal
                    ((EvidenceLink.label hop.Link).ToUpperInvariant())
                    hop.Title
                    (EvidenceLink.detail hop.Link)
            )
            |> ignore

            for finding in hop.Findings do
                sb.AppendLine(sprintf "        · %s" finding) |> ignore

        sb.ToString()