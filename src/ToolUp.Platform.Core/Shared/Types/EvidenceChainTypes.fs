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

// ─── Phase 716 — the walk says what it enumerated, not only what it
//                 liked ───────────────────────────────────────────────
//
// The hop list above proves the walk visited every STAGE. It does not
// prove that, within a stage, the walk enumerated everything the stage's
// own linkage named. A walk that quietly stops at the first unresolvable
// parent ref, or whose enumeration is truncated on the way to the
// reader, renders as a clean short list — and selective omission is then
// indistinguishable from a genuinely short history. That is the same
// argument the hop list makes one level down, and it needs its own
// answer.
//
// ── The expected set is DERIVED, and that is the whole guarantee ──────
//
// The positions below are computed from the chain's own linkage — the
// parent refs a work record carries, the entries a dependency closure
// records, the index a ledger read reached — never from a configured
// list and never from what the walk rendered. A configured expectation
// could be tuned until it matched whatever was produced, at which point
// the claim measures nothing; an expectation read back out of the render
// is the same failure wearing a different hat. So the two halves are
// deliberately separate functions over separate inputs: one derives what
// must appear, the other reads what did.
//
// ── Three states, and the third is not a softer failure ──────────────
//
// A walk that stopped at a DECLARED bound is `Bounded`, never
// `Incomplete`. The caller was told the limit before it asked — the
// walker publishes its caps, and the enumeration cap states its own
// truncation in the render — and a told limit is not an omission.
// Collapsing the two would teach a reader to treat every capped walk as
// a finding, which ends with the finding ignored. `Incomplete` is
// reserved for the case that has no such excuse: the linkage named a
// position, no declared bound accounts for it, and the render is silent.
//
// **The claim is over ENUMERATION, not over scope.** A party seeing less
// because policy withheld it is a different subject with a different
// answer, and it is not narrowed here; a withheld hop enumerates nothing
// and claims nothing.

/// One position the chain's own linkage says an enumeration must
/// account for.
///
/// `Bound` is what makes the three-state verdict possible: a position
/// carrying `Some limit` is one a DECLARED bound legitimately holds
/// back, so its absence from the render reads as `Bounded`; a position
/// carrying `None` must appear, and its absence is an omission.
/// Attributing the bound to the position rather than tracking a separate
/// excuse list is deliberate — an excuse that could not name which
/// positions it covered would excuse everything.
type EnumerationPosition = {
    /// The hop whose enumeration this position belongs to — one of the
    /// `*Hop` literals in `EvidenceChain`.
    Hop: string
    /// Stable lowercase label for what kind of position this is. Never
    /// localised; this is what a dashboard cuts on.
    Kind: string
    /// The key the render must name for this position to be accounted
    /// for: a work record id, a closure entry's id and version, a ledger
    /// index. Derived from the linkage that named it.
    Key: string
    /// The declared limit that legitimately holds this position back, or
    /// `None` where the linkage says it must be enumerated.
    Bound: string option
}

/// One declared limit that held positions back, and how many.
///
/// A count rather than the positions themselves: this rides on every
/// walk and into every exported bundle, and a cap that held back two
/// thousand closure entries would otherwise carry two thousand lines of
/// what it deliberately did not enumerate.
type EnumerationBound = {
    /// The hop whose enumeration the limit applied to.
    Hop: string
    /// Stable lowercase name of the declared limit.
    Bound: string
    /// How many positions the limit held back. Zero where the limit
    /// refused a walk outright, since no enumeration began.
    Unenumerated: int
}

/// Whether the walk enumerated everything its own linkage named.
///
/// Three cases, and the middle one exists so the third keeps its
/// meaning — see the section header.
[<RequireQualifiedAccess>]
type EnumerationCompleteness =
    /// Every position the linkage named appears in what was rendered.
    /// Vacuously true where the linkage names nothing, which is the
    /// honest answer for a deployment that composed nothing: an empty
    /// enumeration is complete, and the chain's own outcome is what says
    /// it is empty.
    | Complete
    /// Every position that is missing is missing because a declared
    /// limit held it back. Not a failure and emphatically not a pass on
    /// the missing positions — the reader is told which limit applied
    /// and how much it covered, which is what makes the bound a bound
    /// rather than a silence.
    | Bounded of bounds: EnumerationBound list
    /// The linkage named positions that no declared limit accounts for
    /// and the render does not carry. The finding this phase exists to
    /// make sayable.
    | Incomplete of missing: EnumerationPosition list * reason: string

[<RequireQualifiedAccess>]
module EnumerationCompleteness =

    /// Stable lowercase wire label. Never localised — this is what a
    /// dashboard cuts on and what an exported bundle's qualifier carries.
    let label =
        function
        | EnumerationCompleteness.Complete -> "complete"
        | EnumerationCompleteness.Bounded _ -> "bounded"
        | EnumerationCompleteness.Incomplete _ -> "incomplete"

    /// `true` only for `Complete`. Provided so a caller cannot write
    /// `<> Incomplete …` and accidentally read `Bounded` as a pass on
    /// positions nobody enumerated.
    let isComplete =
        function
        | EnumerationCompleteness.Complete -> true
        | _ -> false

    /// How many positions of a rendered `Incomplete` are named before
    /// the description says how many more there are. The list itself is
    /// never truncated — only this one-line rendering is, and it says so.
    [<Literal>]
    let DescribedMissingCap = 10

    /// One-line description, so a diagnostic, a test, an exported
    /// bundle's qualifier and an operator surface all read the same
    /// wording.
    let describe (completeness: EnumerationCompleteness) : string =
        match completeness with
        | EnumerationCompleteness.Complete ->
            "every position this chain's own linkage names appears in the enumeration the walk rendered"
        | EnumerationCompleteness.Bounded bounds ->
            let described =
                bounds
                |> List.map (fun bound ->
                    $"{bound.Hop} held {bound.Unenumerated} position(s) back at its declared '{bound.Bound}' limit")
                |> String.concat "; "

            $"the enumeration is bounded rather than short — {described}; a declared limit the caller was told about is not an omission"
        | EnumerationCompleteness.Incomplete(missing, reason) ->
            let shown =
                missing
                |> List.truncate DescribedMissingCap
                |> List.map (fun position -> $"{position.Hop}/{position.Kind} {position.Key}")
                |> String.concat ", "

            let withheld = List.length missing - min (List.length missing) DescribedMissingCap

            let tail =
                if withheld > 0 then
                    $" (and {withheld} further position(s) carried on the verdict but not listed here)"
                else
                    ""

            $"{reason}: {shown}{tail}"

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
    /// Whether the walk enumerated everything its own linkage named.
    ///
    /// **Deliberately outside `VerdictDigest`.** That digest names the
    /// LINK SET, and this is a statement ABOUT the enumeration behind
    /// the links rather than a link — folding it in would move every
    /// chain digest an existing reader holds, to commit to something the
    /// hop lines do not carry. It is bound instead at the layer that has
    /// a claim boundary to bind it to: an exported bundle states it as a
    /// typed qualifier, which the bundle's content id covers and its
    /// verifier cross-checks against this field.
    Enumeration: EnumerationCompleteness
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

        // Rendered on every chain, complete ones included, for the reason
        // the claim boundary is: a line that appeared only on failures is
        // a line a reader learns to expect the absence of.
        sb.AppendLine(
            sprintf
                "  enumeration: %s — %s"
                (EnumerationCompleteness.label chain.Enumeration)
                (EnumerationCompleteness.describe chain.Enumeration)
        )
        |> ignore

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

/// Deriving what an enumeration must account for, and reading what it
/// did.
///
/// **Two halves over two inputs, deliberately.** `assess` reads only the
/// rendered hops; every producer of expected positions reads only the
/// linkage. Nothing here can consult the render to decide what should
/// have been rendered, which is the one way an expectation stops
/// measuring anything.
[<RequireQualifiedAccess>]
module EvidenceEnumeration =

    // ── Position kinds ───────────────────────────────────────────────
    //
    // Literals rather than a closed union, for the reason the hop ids
    // are literals: a later phase deriving a new kind of position must
    // not stop every consumer that matched on these from compiling.

    /// A stage of the walk that the model's declared order names.
    [<Literal>]
    let HopKind = "hop"

    /// One upstream work record a parent ref names.
    [<Literal>]
    let WorkAncestorKind = "work-ancestor"

    /// One dependency-closure entry the closure records.
    [<Literal>]
    let ClosureEntryKind = "closure-entry"

    /// One position in the hash-chained audit ledger.
    [<Literal>]
    let LedgerIndexKind = "ledger-index"

    // ── Declared bounds ──────────────────────────────────────────────

    /// The walk's own per-hop enumeration cap — the one that states its
    /// truncation in the render rather than truncating silently.
    [<Literal>]
    let EnumerationCapBound = "declared-enumeration-cap"

    /// The requested work depth: the level naming this record was
    /// walked, the level that would resolve it was not.
    [<Literal>]
    let WorkDepthBound = "declared-work-depth"

    /// The upstream source's own declared ancestor bound, which refused
    /// the ancestor walk rather than trimming it.
    [<Literal>]
    let WorkAncestorBound = "declared-work-ancestor-bound"

    /// The walker's declared closure cap, which refuses a walk rather
    /// than reporting a trimmed closure.
    [<Literal>]
    let ClosureCapBound = "declared-closure-cap"

    /// A position the linkage says must be enumerated.
    let required (hop: string) (kind: string) (key: string) : EnumerationPosition = {
        Hop = hop
        Kind = kind
        Key = key
        Bound = None
    }

    /// A position a declared limit legitimately holds back.
    let bounded (hop: string) (kind: string) (key: string) (bound: string) : EnumerationPosition = {
        Hop = hop
        Kind = kind
        Key = key
        Bound = Some bound
    }

    /// Everything one hop actually rendered: its join key, its own
    /// account of itself, and the enumeration behind it.
    ///
    /// The join key is part of the surface because several hops enumerate
    /// through it — a ledger read names its position there and nowhere
    /// else — and a check that ignored it would report a rendered
    /// position as missing.
    let renderedSurface (hop: EvidenceHop) : string =
        let parts = [
            EvidenceLink.reference hop.Link
            EvidenceLink.detail hop.Link
            yield! hop.Findings
        ]

        parts |> String.concat "\n"

    /// Whether a hop's render names a position's key.
    ///
    /// Containment rather than equality, because a rendered enumeration
    /// line is prose about the position and not the key alone. Keys are
    /// derived to be discriminating within their hop for exactly this
    /// reason — a closure entry's key carries its version, so two
    /// packages sharing a name prefix cannot account for one another.
    let accountsFor (hop: EvidenceHop) (key: string) : bool =
        key <> "" && (renderedSurface hop).Contains key

    /// Fold the derived expectation and the rendered hops into the
    /// verdict. Total, pure, and the only place the precedence lives:
    ///   * any position missing with no declared bound ⇒ `Incomplete`;
    ///   * else any position a bound held back          ⇒ `Bounded`;
    ///   * else                                         ⇒ `Complete`.
    ///
    /// **`Incomplete` outranks `Bounded` and never the reverse.** A cap
    /// that applied somewhere else in the walk is not an excuse for a
    /// position nothing accounts for, and a fold that let one cap silence
    /// one omission would let a deployment buy silence by declaring a
    /// bound it never needed.
    ///
    /// The stage tier is checked from `EvidenceChain.order` rather than
    /// from anything a caller supplies, so **a render carrying fewer
    /// hops fails here whatever expectation it is handed** — including
    /// an empty one.
    let assess (expected: EnumerationPosition list) (hops: EvidenceHop list) : EnumerationCompleteness =
        let byId = hops |> List.map (fun hop -> hop.Id, hop) |> Map.ofList

        let missingHops =
            EvidenceChain.order
            |> List.filter (fun hopId -> not (byId |> Map.containsKey hopId))
            |> List.map (fun hopId -> required hopId HopKind hopId)

        let unaccounted =
            expected
            |> List.filter (fun position ->
                match byId |> Map.tryFind position.Hop with
                | None -> true
                | Some hop -> not (accountsFor hop position.Key))

        let missing =
            missingHops
            @ (unaccounted |> List.filter (fun position -> Option.isNone position.Bound))

        if not (List.isEmpty missing) then
            EnumerationCompleteness.Incomplete(
                missing,
                $"{List.length missing} position(s) this chain's own linkage names are absent from the enumeration the walk rendered, and no declared bound accounts for them"
            )
        else
            let bounds =
                unaccounted
                |> List.choose (fun position -> position.Bound |> Option.map (fun bound -> position.Hop, bound))
                |> List.countBy id
                |> List.map (fun ((hop, bound), count) -> {
                    Hop = hop
                    Bound = bound
                    Unenumerated = count
                })
                |> List.sortBy (fun bound -> bound.Hop, bound.Bound)

            if List.isEmpty bounds then
                EnumerationCompleteness.Complete
            else
                EnumerationCompleteness.Bounded bounds

    /// The verdict for a walk that was REFUSED at a declared cap.
    ///
    /// **Always `Bounded`, never `Incomplete`, and that is the phase's
    /// third state doing its work.** A refused walk enumerated nothing
    /// and hid nothing: the caller named a request outside a bound the
    /// walker had already published, and got a typed refusal saying so.
    /// Reading that as an incomplete enumeration would put the one state
    /// reserved for silent omission on the loudest possible answer.
    ///
    /// `Unenumerated` is the closure's own entry count where the closure
    /// cap refused, and zero where the depth axis refused before any
    /// enumeration began — nothing was held back from an enumeration
    /// that never started.
    let ofRefusal (error: EvidenceChainError) : EnumerationCompleteness =
        match error with
        | ChainWorkDepthInvalid _
        | ChainWorkDepthExceedsCap _ ->
            EnumerationCompleteness.Bounded [
                {
                    Hop = EvidenceChain.UpstreamWorkRecordHop
                    Bound = WorkDepthBound
                    Unenumerated = 0
                }
            ]
        | ChainClosureExceedsCap(entries, _) ->
            EnumerationCompleteness.Bounded [
                {
                    Hop = EvidenceChain.DependencyClosureHop
                    Bound = ClosureCapBound
                    Unenumerated = entries
                }
            ]

// ─── Phase 714 — the walked chain as a portable, checkable bundle ─────
//
// A chain answers a question for whoever is holding the deployment. It
// is not yet an artefact a counterparty can hold: nothing names the
// bundle's own identity, nothing states what the bundle does and does
// not claim, and the only thing that could check it is the deployment
// that produced it. This section is the **bundle**: the chain, plus the
// claim boundary, plus a content id over a canonical framing — and a
// verifier that needs none of the above to be running.
//
// ── What a bundle claims, exactly ────────────────────────────────────
//
// **These records, with these digests, linked this way, were observed
// by this deployment at this time.** Nothing more. It does not claim
// that the upstream records are true, that the work they describe was
// done well, that code never composed behaved as declared, or that
// anything the deployment did not record happened or did not happen.
// The boundary is not a footnote about the artefact; it IS half the
// artefact, and it is carried as data on every bundle — clean ones
// included — because a caveat that appears only on failures is a
// caveat nobody reads.
//
// ── Two rulings this shape makes, both written INTO the document ─────
//
// *Nested signatures are CARRIED VERBATIM, never re-signed.* Several of
// the chain's hops name an artefact that already carries somebody's
// signature: a deploy record's seal, a signed evidence-pack manifest, a
// signed ledger head. A bundle can either transcode those — carry each
// inner attestation exactly as recorded and add ONE outer signature over
// the whole — or re-sign: extract the content, drop the inner signature,
// and assert it afresh under the bundle key. This shape transcodes, and
// the disposition rides in the document as
// `NestedAttestationDisposition` so a verifier reads the ruling out of
// the bundle rather than having to know it about the producer.
// Re-signing would convert an observation into an origin claim in the
// one act that is supposed to preserve the difference: the surviving
// signature would say "this deployment asserts these upstream facts"
// where the record said "this deployment observed that somebody else
// asserted them", and no later reader could recover which. It would
// also make a compromised bundle key sufficient to manufacture upstream
// attestations that were never made.
//
// *The content id names the RECORD SET, not the observation.* The
// canonical form below deliberately excludes the observer and the
// observation time, exactly as the chain's own verdict digest excludes
// its actor and clock. Two bundles taken a minute apart from an
// unchanged deployment therefore carry the SAME content id, and a reader
// sees at a glance that nothing moved. The observer and the time are
// still covered by the outer signature — they sit inside the signed
// payload — so the "observed by this deployment at this time" half of
// the claim is attested; it simply is not what the artefact is
// ADDRESSED BY.

/// One additional typed verdict qualifying what a bundle claims.
///
/// **Deliberately open-ended, and appended rather than inserted.** A
/// later phase that measures something new about the walk — whether the
/// enumeration behind a hop was complete, say — attaches it here instead
/// of widening the chain or the not-proved list. The canonical form
/// renders qualifiers last and in carried order, so adding one appends
/// lines and moves nothing before them: a reader diffing two canonical
/// forms across the upgrade can tell a growth from a re-statement.
///
/// `Verdict` is a stable lowercase wire label, never localised — the
/// thing a dashboard cuts on. `Detail` is the qualifier's own account of
/// itself.
type BundleClaimQualifier = {
    /// Stable id, so a consumer can find one qualifier without
    /// string-matching prose.
    Id: string
    /// Stable lowercase wire label for this qualifier's verdict.
    Verdict: string
    /// What the verdict means for this bundle, quoting the evidence
    /// rather than the conclusion.
    Detail: string
}

/// A walked chain packaged as a portable artefact.
///
/// The chain rides VERBATIM — its hops, its outcome and its own verdict
/// digest are the ones the walk produced, not a re-derivation. That is
/// the transcode ruling applied to the bundle's own innermost artefact,
/// and it is what lets a holder check the chain's digest independently
/// of anything this shape does.
type EvidenceBundle = {
    SchemaVersion: int
    /// How this bundle treats an inner artefact that already carries a
    /// signature. One recognised value —
    /// `EvidenceBundle.CarriedVerbatim` — and a verifier refuses a
    /// document declaring anything else rather than guessing, because
    /// the whole point of writing the ruling down is that a reader need
    /// not know which producer wrote the bundle.
    NestedAttestationDisposition: string
    /// The deployment that observed these records. An opaque,
    /// deployment-chosen id; never interpreted here, and never a claim
    /// about who the observer IS.
    Observer: string
    ObservedAt: DateTime
    /// The walk, exactly as it was produced.
    Chain: EvidenceChain
    /// What this bundle does NOT prove. Present on every bundle,
    /// including one whose chain is complete.
    NotProved: DeploymentVerification.NotProvedStatement list
    /// Additional typed verdicts qualifying the claim. Empty is normal.
    Qualifiers: BundleClaimQualifier list
    /// Lowercase-hex SHA-256 over `EvidenceBundle.canonicalForm`,
    /// computed by the producer. This is the bundle's identity and the
    /// in-toto subject digest an envelope publishes it under.
    ContentId: string
}

/// Whether a bundle hangs together.
///
/// **Two cases, and the negative one names a POSITION.** A reader told
/// only "this bundle does not verify" has to re-derive the whole
/// document to find out what moved; `BrokenAt` carries a stable
/// structural coordinate (`bundle/chain/hops[3]`, `bundle/contentId`, …)
/// plus the reason, so the finding is actionable from the one line.
///
/// **`BrokenAt` also covers "I cannot check this".** A bundle written
/// under a schema this verifier does not know is reported broken with a
/// reason saying exactly that — never `Intact`. A verifier that passed
/// what it could not read would be worse than one that did not exist.
[<RequireQualifiedAccess>]
type BundleIntegrity =
    /// Every structural property this verifier can establish holds.
    /// Emphatically NOT a statement that the bundle's signature is
    /// valid, nor that the records it carries are true — see
    /// `EvidenceBundle.verifyWith`.
    | Intact
    /// A named property does not hold at a named position.
    | BrokenAt of position: string * reason: string

[<RequireQualifiedAccess>]
module BundleIntegrity =

    /// Stable lowercase wire label.
    let label =
        function
        | BundleIntegrity.Intact -> "intact"
        | BundleIntegrity.BrokenAt _ -> "broken"

    /// `true` only for `Intact`. Provided so a caller cannot write
    /// `<> BrokenAt …` and accidentally read a future case as a pass.
    let isIntact =
        function
        | BundleIntegrity.Intact -> true
        | BundleIntegrity.BrokenAt _ -> false

    /// One-line description, so a diagnostic, a test and a command's
    /// stdout all read the same wording.
    let describe =
        function
        | BundleIntegrity.Intact -> "bundle intact"
        | BundleIntegrity.BrokenAt(position, reason) -> $"broken at {position}: {reason}"

module EvidenceBundle =

    /// Schema version of `EvidenceBundle`.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version for the bundle's canonical form. Part of the
    /// framed string the content id is taken over, so a bundle
    /// canonicalised under a future scheme can never collide with one
    /// canonicalised under this.
    [<Literal>]
    let FramingVersion = "toolup.evidencebundle.v1"

    /// The one recognised nested-attestation disposition: an inner
    /// artefact's own signature is carried exactly as recorded, and the
    /// bundle adds its own signature over the whole. The rejected
    /// alternative — re-signing the extracted content under the bundle
    /// key — is priced in this substrate's migration note.
    [<Literal>]
    let CarriedVerbatim = "carried-verbatim"

    /// The in-toto subject name a bundle publishes under. The digest is
    /// what a holder claim-checks; the name is how a reader recognises
    /// the shape at a glance.
    [<Literal>]
    let SubjectName = "evidence-chain-bundle"

    /// The qualifier id under which a bundle states its chain's
    /// enumeration-completeness verdict.
    ///
    /// A stable id rather than prose in the claim boundary, so a consumer
    /// finds the verdict without string-matching a sentence — and so the
    /// verifier below can cross-check it against the chain it qualifies.
    [<Literal>]
    let EnumerationQualifierId = "enumeration-completeness"

    /// The qualifier a bundle carries its chain's enumeration verdict as.
    ///
    /// Rendered last in the canonical form, per the qualifier slot's own
    /// contract, so a bundle stating it appends lines and moves nothing
    /// before them.
    let enumerationQualifier (completeness: EnumerationCompleteness) : BundleClaimQualifier = {
        Id = EnumerationQualifierId
        Verdict = EnumerationCompleteness.label completeness
        Detail = EnumerationCompleteness.describe completeness
    }

    /// The canonical form the content id is taken over.
    ///
    /// Five blocks in a fixed order: the framing, the nested-attestation
    /// ruling, the chain (its own canonical hop lines plus its outcome
    /// and verdict digest), the not-proved statements, and the
    /// qualifiers. Qualifiers render LAST so a later phase's addition
    /// appends rather than shifting.
    ///
    /// **Excludes `Observer` and `ObservedAt`** — see the section header:
    /// the id names the record set, so an unchanged deployment bundles to
    /// the same id every time.
    let canonicalForm (bundle: EvidenceBundle) : string =
        let sb = StringBuilder()
        sb.Append(FramingVersion).Append('\n') |> ignore
        sb.Append(SchemaVersion).Append('\n') |> ignore

        // Length-prefixed fields, so the canonical form is injective
        // over free-text detail without needing a separator byte the
        // detail could contain. A delimiter-only scheme would let two
        // different bundles frame to identical bytes.
        let field (value: string) =
            let value = if isNull value then "" else value
            sb.Append(value.Length).Append(':').Append(value).Append(';') |> ignore

        // An explicit LF, never `AppendLine`, which emits
        // `Environment.NewLine` — the same bundle would then frame to
        // different bytes on Windows and Linux and the id would stop
        // being a property of the bundle.
        let endLine () = sb.Append('\n') |> ignore

        field "disposition"
        field bundle.NestedAttestationDisposition
        endLine ()

        // The chain's own canonical form, spliced verbatim. Sharing it
        // rather than restating the hop framing is what keeps the
        // bundle's id and the chain's verdict digest derived from ONE
        // definition of what a hop is.
        sb.Append(EvidenceChain.canonicalForm bundle.Chain.Hops) |> ignore

        field "chain"
        field (string bundle.Chain.SchemaVersion)
        field (EvidenceChainOutcome.label bundle.Chain.Outcome)
        field bundle.Chain.VerdictDigest
        endLine ()

        for statement in bundle.NotProved do
            field "not-proved"
            field statement.Id
            field statement.Statement
            field (defaultArg statement.Narrowing "")
            endLine ()

        for qualifier in bundle.Qualifiers do
            field "qualifier"
            field qualifier.Id
            field qualifier.Verdict
            field qualifier.Detail
            endLine ()

        sb.ToString()

    /// The hop-set fault a bundle's chain carries, if any: a short or
    /// long hop list, a hop out of walk order, or a hop whose ordinal
    /// does not match its position.
    let private hopFault (bundle: EvidenceBundle) : (string * string) option =
        let hops = bundle.Chain.Hops
        let expected = EvidenceChain.order

        if List.length hops <> List.length expected then
            Some(
                "bundle/chain/hops",
                $"the walk carries {List.length hops} hop(s) where a complete chain is {List.length expected} — a short hop list reads as a complete one, which is exactly what the chain shape exists to prevent"
            )
        else
            List.zip hops expected
            |> List.indexed
            |> List.tryPick (fun (index, (hop, expectedId)) ->
                if hop.Id <> expectedId then
                    Some(
                        $"bundle/chain/hops[{index}]",
                        $"hop {index} is '{hop.Id}' where the walk order names '{expectedId}'"
                    )
                elif hop.Ordinal <> index + 1 then
                    Some(
                        $"bundle/chain/hops[{index}]",
                        $"hop '{hop.Id}' carries ordinal {hop.Ordinal} at position {index + 1} — a renumbered walk cannot be read in the order it was taken"
                    )
                else
                    None)

    /// The digest-dependent fault a bundle carries, if any: a chain
    /// outcome that is not the fold of its own hops, a verdict digest
    /// that is not the digest of its own hops, a stripped claim
    /// boundary, or a content id that is not the digest of the whole.
    let private contentFault (digest: string -> string) (bundle: EvidenceBundle) : (string * string) option =
        let recomputedOutcome = EvidenceChain.outcomeOf bundle.Chain.Hops
        let recomputedVerdict = digest (EvidenceChain.canonicalForm bundle.Chain.Hops)

        if bundle.Chain.Outcome <> recomputedOutcome then
            Some(
                "bundle/chain/outcome",
                $"the chain reports outcome '{EvidenceChainOutcome.label bundle.Chain.Outcome}' and folding its own hops gives '{EvidenceChainOutcome.label recomputedOutcome}'"
            )
        elif bundle.Chain.VerdictDigest <> recomputedVerdict then
            Some(
                "bundle/chain/verdictDigest",
                $"the chain carries verdict digest '{bundle.Chain.VerdictDigest}' and its own hops canonicalise to '{recomputedVerdict}' — a hop was altered after the walk"
            )
        elif List.isEmpty bundle.NotProved then
            Some(
                "bundle/notProved",
                "the bundle carries no claim boundary — every bundle states what it does not prove, including one whose chain is complete, so an empty list is a stripped document rather than a clean one"
            )
        else
            // The enumeration verdict rides in two places — on the chain,
            // where the walk recorded it, and on the qualifier, where the
            // bundle states it — and only the second reaches the content
            // id. A document whose two halves disagree is refused rather
            // than read from whichever half a reader happened to look at.
            let disagreeing =
                bundle.Qualifiers
                |> List.tryFind (fun qualifier ->
                    qualifier.Id = EnumerationQualifierId
                    && qualifier.Verdict <> EnumerationCompleteness.label bundle.Chain.Enumeration)

            match disagreeing with
            | Some qualifier ->
                Some(
                    $"bundle/qualifiers/{EnumerationQualifierId}",
                    $"the bundle states enumeration verdict '{qualifier.Verdict}' and the chain it qualifies carries '{EnumerationCompleteness.label bundle.Chain.Enumeration}' — a document that says two things about what its walk enumerated says neither"
                )
            | None ->
                let recomputedContentId = digest (canonicalForm bundle)

                if bundle.ContentId <> recomputedContentId then
                    Some(
                        "bundle/contentId",
                        $"the bundle is addressed as '{bundle.ContentId}' and its own canonical form digests to '{recomputedContentId}'"
                    )
                else
                    None

    /// Verify a bundle **structurally**, using a caller-supplied digest
    /// function over the canonical form.
    ///
    /// **Pure, and crypto-free by construction.** The digest arrives as
    /// an argument rather than being computed here, because this file is
    /// packed for hosts where `System.Security.Cryptography` does not
    /// exist; a party checking a bundle supplies the SHA-256 their own
    /// platform provides and gets the same answer. Bundle verification
    /// therefore needs no deployment, no store, no network, and no
    /// package beyond a hash.
    ///
    /// **What it establishes:** the schema is one this verifier knows;
    /// the nested-attestation ruling is the recognised one; the chain
    /// carries the full hop set, in order, correctly ordinalled; the
    /// chain's outcome is the fold of its own hops; the chain's verdict
    /// digest is the digest of its own canonical form; the claim
    /// boundary is present; and the content id is the digest of the
    /// whole canonical form.
    ///
    /// **What it does NOT establish, and this is the important half:**
    /// nothing about the outer signature — a holder checks that against
    /// a public key, with stock DSSE tooling if they prefer — and
    /// nothing about whether the records the chain carries are TRUE.
    /// `Intact` means the document has not been altered since it was
    /// framed and says what it says consistently. It is not a pass on
    /// the deployment.
    let verifyWith (digest: string -> string) (bundle: EvidenceBundle) : BundleIntegrity =
        if bundle.SchemaVersion <> SchemaVersion then
            BundleIntegrity.BrokenAt(
                "bundle/schemaVersion",
                $"this bundle declares schema version {bundle.SchemaVersion} and this verifier reads version {SchemaVersion} — it is not checkable here, which is not the same as being wrong"
            )
        elif bundle.NestedAttestationDisposition <> CarriedVerbatim then
            BundleIntegrity.BrokenAt(
                "bundle/nestedAttestationDisposition",
                $"this bundle declares nested-attestation disposition '{bundle.NestedAttestationDisposition}' and the only disposition this verifier can check is '{CarriedVerbatim}' — a document whose inner signatures were treated some other way makes a different claim, and is refused rather than read hopefully"
            )
        else
            match hopFault bundle with
            | Some(position, reason) -> BundleIntegrity.BrokenAt(position, reason)
            | None ->
                match contentFault digest bundle with
                | Some(position, reason) -> BundleIntegrity.BrokenAt(position, reason)
                | None -> BundleIntegrity.Intact

    /// Render a bundle as operator-facing text — the shape the verify
    /// command prints and a support bundle quotes. Pure, so a test
    /// asserts on it directly.
    ///
    /// The claim boundary renders on EVERY bundle, for the reason it is
    /// carried as data at all.
    let render (bundle: EvidenceBundle) : string =
        let sb = StringBuilder()
        sb.AppendLine "── Evidence bundle ──" |> ignore
        sb.AppendLine(sprintf "  content id: %s" bundle.ContentId) |> ignore

        sb.AppendLine(sprintf "  observed %s by %s" (bundle.ObservedAt.ToString "o") bundle.Observer)
        |> ignore

        sb.AppendLine(sprintf "  nested attestations: %s" bundle.NestedAttestationDisposition)
        |> ignore

        sb.AppendLine "" |> ignore
        sb.Append(EvidenceChain.render bundle.Chain) |> ignore

        if not (List.isEmpty bundle.Qualifiers) then
            sb.AppendLine "" |> ignore
            sb.AppendLine "  Qualifiers:" |> ignore

            for qualifier in bundle.Qualifiers do
                sb.AppendLine(sprintf "    - [%s] %s — %s" qualifier.Verdict qualifier.Id qualifier.Detail)
                |> ignore

        sb.AppendLine "" |> ignore
        sb.AppendLine "  What this bundle does NOT prove:" |> ignore

        for statement in bundle.NotProved do
            sb.AppendLine(sprintf "    - %s" statement.Statement) |> ignore

            match statement.Narrowing with
            | Some narrowing -> sb.AppendLine(sprintf "      (narrowed: %s)" narrowing) |> ignore
            | None -> ()

        sb.ToString()

    /// The disclaimer a verification run prints on EVERY outcome,
    /// including a pass.
    ///
    /// It is a literal rather than prose assembled at the call site
    /// because it is the sentence most likely to be read as
    /// boilerplate and skipped, and a sentence that varies between
    /// runs invites exactly that. One wording, one place.
    [<Literal>]
    let StructuralPassDisclaimer =
        "  A structural pass says this document is self-consistent and unaltered since it was framed. It says nothing about who signed it — check the signature against a public key with any DSSE verifier — and nothing about whether the records it carries are true."

    /// The operator-facing text one verification run prints: the
    /// verdict, the disclaimer, and — where the document was readable
    /// at all — the bundle itself, claim boundary included.
    ///
    /// **In `Core`, and that placement is the point.** A party checking
    /// a bundle offline should reach the SAME output as the deployment
    /// that produced it, byte for byte, and a report assembled in the
    /// server tier could only be approximated by anybody else. Every
    /// input here is a value; nothing reads a clock, a store or a key,
    /// so a cold run over the same document produces the same bytes.
    let verificationReport (integrity: BundleIntegrity) (bundle: EvidenceBundle option) : string =
        let sb = StringBuilder()
        sb.AppendLine "── Evidence bundle verification ──" |> ignore

        sb.AppendLine(
            sprintf
                "  verdict: %s — %s"
                ((BundleIntegrity.label integrity).ToUpperInvariant())
                (BundleIntegrity.describe integrity)
        )
        |> ignore

        sb.AppendLine "" |> ignore
        sb.AppendLine StructuralPassDisclaimer |> ignore

        match bundle with
        | Some bundle ->
            sb.AppendLine "" |> ignore
            sb.Append(render bundle) |> ignore
        | None -> ()

        sb.ToString()