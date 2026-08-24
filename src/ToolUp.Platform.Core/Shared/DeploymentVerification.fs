// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Phase 686 — the one-command deployment verification report ──────
//
// Several verifiers now exist with no composed surface: the boot
// verification verdict over the sealed composition (Phase 657), the
// grounding-envelope continuity walk (Phase 684), the hash-chained audit
// ledger walk (Phase 658, head-signed since Phase 682), the certificate
// issuance log (Phase 565 / 685), and the answer-verification provenance
// join (Phase 680). Each answers its own question through its own library
// call. A skeptical assessor holding a running deployment had to know all
// five existed, know what each needed, and stitch the answers together —
// which means the answer they got depended on what they thought to ask.
//
// This file is the **wire shape** of one report that runs whatever the
// deployment composes and states the rest honestly. It is pure
// projection: records, a per-section verdict union, and the two total
// functions that fold sections into an outcome and an exit code. No I/O,
// no clock, no crypto (the digest is computed server-side over the
// canonical form declared here). Kept in Shared per GP 10 so an admin
// panel renders the same shape without re-deriving it.
//
// **Two design constraints are load-bearing, and both are refusals.**
//
// *No boolean rollup.* A section is never `bool`, and the report has no
// `IsVerified` field. The failure this exists to prevent is an unverified
// section reading as a pass: a deployment that composes no ledger and one
// that composes a ledger which verifies are not the same state, and a
// boolean cannot hold the difference. Every section therefore carries a
// five-case verdict where **three of the five are non-affirmative in
// different ways** — absent, unreadable, and failed are distinct
// findings with distinct remedies, and collapsing them loses exactly the
// information an assessor came for.
//
// *The not-proved statements are DATA.* Phase 657's migration doc states
// what its seal does not prove in prose of equal length to what it does,
// and Phase 684 repeated the discipline. Prose in a migration doc is not
// available to the assessor reading a report at 2am. `NotProved` carries
// those statements into the artefact, each able to name the substrate
// that narrows it — so composing Phase 684 does not make the post-boot
// mutation caveat vanish, it makes it say which facets are now covered
// and leaves the rest standing.

/// The verdict for one section of the deployment verification report.
///
/// **Five cases, and the three non-affirmative ones are deliberately not
/// one case.** An assessor's next action differs completely between "this
/// deployment does not compose that substrate" (compose it, or accept the
/// bound), "the substrate is composed and would not answer" (fix the
/// read path — this is the state an attacker manufactures), and "the
/// substrate answered and the answer is bad" (respond to the finding).
/// A three-valued `Pass | Fail | Unknown` folds the first two together
/// and the report then cannot tell an absent ledger from a broken read.
[<RequireQualifiedAccess>]
type VerificationSectionVerdict =
    /// The section's substrate is not composed in this deployment. There
    /// is nothing to read and nothing has failed — never a pass, and
    /// never an error either. `reason` names what would have to be
    /// composed for the section to say anything.
    | NotComposed of reason: string
    /// The substrate is composed, was read, and the check it performs
    /// holds. The only affirmative case. `detail` quotes the proof — the
    /// position walked to, the digest arrived at — rather than the
    /// conclusion, so a reader can re-derive it.
    | Verified of detail: string
    /// The substrate is composed and was read, and there was nothing for
    /// it to affirm: an empty ledger, an issuance log with no rows, a
    /// boot preflight that ran against no seal. Honest and unalarming,
    /// and specifically NOT `Verified` — a check that found nothing to
    /// check has not verified anything.
    | Observed of detail: string
    /// The substrate is composed and the check it performs does not hold.
    /// The finding an assessor acts on. Exits non-zero.
    | Failed of detail: string
    /// The substrate is composed and could not be read — the source
    /// errored, the integrity gate refused, the handle was absent where
    /// the composition said it would be. **Not a variety of
    /// `NotComposed`**: a deployment can make an inconvenient section
    /// fail to read, and if that presented as absence it would be
    /// indistinguishable from a deployment that never composed it. Exits
    /// non-zero for the same reason.
    | Unreadable of reason: string

[<RequireQualifiedAccess>]
module VerificationSectionVerdict =

    /// Stable lowercase wire label. Never localised — this is what a
    /// dashboard cuts on and what the audit row records.
    let label =
        function
        | VerificationSectionVerdict.NotComposed _ -> "not-composed"
        | VerificationSectionVerdict.Verified _ -> "verified"
        | VerificationSectionVerdict.Observed _ -> "observed"
        | VerificationSectionVerdict.Failed _ -> "failed"
        | VerificationSectionVerdict.Unreadable _ -> "unreadable"

    /// The verdict's own account of itself — the `detail` / `reason` the
    /// case carries.
    let detail =
        function
        | VerificationSectionVerdict.NotComposed reason -> reason
        | VerificationSectionVerdict.Verified detail -> detail
        | VerificationSectionVerdict.Observed detail -> detail
        | VerificationSectionVerdict.Failed detail -> detail
        | VerificationSectionVerdict.Unreadable reason -> reason

    /// `true` only for `Verified`. Provided so a caller never has to
    /// write the fold itself and accidentally admit `Observed`.
    let isAffirmative =
        function
        | VerificationSectionVerdict.Verified _ -> true
        | _ -> false

    /// `true` for the two cases that must not exit zero: a composed
    /// substrate whose check failed, and a composed substrate that would
    /// not answer.
    let isAdverse =
        function
        | VerificationSectionVerdict.Failed _
        | VerificationSectionVerdict.Unreadable _ -> true
        | _ -> false

/// The report's top-line shape. Deliberately **not** a verdict and
/// deliberately not orderable: it is a description of the section set, so
/// a reader who takes only this line is told how much was verified rather
/// than being handed a pass they did not earn.
[<RequireQualifiedAccess>]
type DeploymentVerificationOutcome =
    /// Not one section had a substrate composed. The report is honest and
    /// entirely empty of evidence — the state a bare deployment is in,
    /// and it exits zero because nothing failed.
    | NothingComposed
    /// Every composed section verified, and at least one was composed.
    /// Sections that are `NotComposed` remain listed and remain uncovered
    /// — this says nothing about them.
    | AllComposedVerified
    /// At least one section is composed and affirmative and at least one
    /// composed section is `Observed` — read, with nothing to affirm.
    | PartiallyVerified
    /// At least one composed section is `Failed` or `Unreadable`.
    | FailuresPresent

[<RequireQualifiedAccess>]
module DeploymentVerificationOutcome =

    /// Stable lowercase wire label.
    let label =
        function
        | DeploymentVerificationOutcome.NothingComposed -> "nothing-composed"
        | DeploymentVerificationOutcome.AllComposedVerified -> "all-composed-verified"
        | DeploymentVerificationOutcome.PartiallyVerified -> "partially-verified"
        | DeploymentVerificationOutcome.FailuresPresent -> "failures-present"

module DeploymentVerification =

    /// Schema version of `DeploymentVerificationReport`.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version for the report's canonical form. Part of the
    /// framed string the verdict digest is taken over, so a report
    /// canonicalised under a future scheme can never collide with one
    /// canonicalised under this.
    [<Literal>]
    let FramingVersion = "toolup.deploymentverification.v1"

    /// Stable section ids. String literals rather than a DU because the
    /// set is open by construction — a later phase adding a sixth
    /// verifier adds a section, and a closed union would make that a
    /// breaking change to every consumer that matched on it. The ids
    /// themselves never change.
    [<Literal>]
    let BootSealSection = "boot-seal"

    [<Literal>]
    let GroundingContinuitySection = "grounding-continuity"

    [<Literal>]
    let AuditLedgerSection = "audit-ledger"

    [<Literal>]
    let CertificateIssuanceSection = "certificate-issuance"

    [<Literal>]
    let AnswerJoinSection = "answer-verification-join"

    /// Phase 693 — the sixth section, and the first one added after the
    /// report shipped. It exists in this list rather than in a widened
    /// union precisely because the comment above anticipated it: adding a
    /// verifier adds a literal, and no consumer that matched on the five
    /// stops compiling.
    [<Literal>]
    let SeamAuthoritySection = "seam-authority"

    /// One section of the report: what was checked, what the check said,
    /// and any per-item findings the verdict summarises.
    ///
    /// `Findings` is separate from the verdict's own detail on purpose.
    /// The verdict is one line an operator reads; the findings are the
    /// enumeration behind it (which ledger position broke, which
    /// declarations moved, which join did not recompute). A verdict that
    /// tried to carry both would be neither.
    type ReportSection = {
        /// Stable id — one of the `*Section` literals above.
        Id: string
        /// Human title for the rendered report.
        Title: string
        Verdict: VerificationSectionVerdict
        /// The enumeration behind the verdict, in the order the check
        /// produced it. Empty is normal and means the verdict's own
        /// detail is the whole of what was found.
        Findings: string list
    }

    /// One statement of what this report does NOT prove.
    ///
    /// **The point of the whole artefact.** A verification report that
    /// lists only what it checked invites the reader to believe the
    /// complement was checked too. These statements are carried as data
    /// so they survive into every rendering, every JSON export and every
    /// dashboard — rather than living in a migration doc the reader does
    /// not have open.
    type NotProvedStatement = {
        /// Stable id so a consumer can suppress, annotate or track one
        /// statement without string-matching prose.
        Id: string
        /// What is not proved, stated plainly and in full.
        Statement: string
        /// The substrate that NARROWS this statement in this deployment,
        /// when one is composed — never one that closes it. `Some` here
        /// means the bound is smaller than the bare statement, and says
        /// by how much; `None` means the statement stands whole.
        Narrowing: string option
    }

    /// The composed report.
    ///
    /// `VerdictDigest` is a SHA-256 over `canonicalForm` below, computed
    /// server-side. It is what the audited-read record commits to: the
    /// audit row carries the digest rather than the report, so the trail
    /// proves what was reported without copying a deployment-wide
    /// evidence summary onto a surface that has its own readers.
    type DeploymentVerificationReport = {
        SchemaVersion: int
        /// Who ran the report. The audited read's subject.
        Actor: string
        GeneratedAt: DateTime
        Sections: ReportSection list
        NotProved: NotProvedStatement list
        Outcome: DeploymentVerificationOutcome
        VerdictDigest: string
    }

    /// Fold the section verdicts into the top-line outcome. Total, pure,
    /// and the only place the precedence lives:
    ///   * any adverse (`Failed` / `Unreadable`) ⇒ `FailuresPresent`;
    ///   * else no section composed at all       ⇒ `NothingComposed`;
    ///   * else any composed-but-`Observed`      ⇒ `PartiallyVerified`;
    ///   * else                                  ⇒ `AllComposedVerified`.
    ///
    /// Note `NotComposed` sections never inflate the outcome and never
    /// depress it — they are not evidence in either direction, which is
    /// the whole reason they are a separate case.
    let outcomeOf (sections: ReportSection list) : DeploymentVerificationOutcome =
        let verdicts = sections |> List.map _.Verdict

        let composed =
            verdicts
            |> List.filter (function
                | VerificationSectionVerdict.NotComposed _ -> false
                | _ -> true)

        if verdicts |> List.exists VerificationSectionVerdict.isAdverse then
            DeploymentVerificationOutcome.FailuresPresent
        elif List.isEmpty composed then
            DeploymentVerificationOutcome.NothingComposed
        elif
            composed
            |> List.exists (function
                | VerificationSectionVerdict.Observed _ -> true
                | _ -> false)
        then
            DeploymentVerificationOutcome.PartiallyVerified
        else
            DeploymentVerificationOutcome.AllComposedVerified

    /// The process exit code for a CI invocation: non-zero when any
    /// composed section is adverse, zero otherwise.
    ///
    /// **Absence exits zero and that is the contract.** A deployment
    /// composing none of the substrates is not failing anything; a CI job
    /// that reddened on it would be reporting the deployment's shape as a
    /// defect, and would be turned off. **A composed-but-unreadable
    /// section exits NON-zero**, because it is the state a deployment
    /// reaches by breaking its own evidence, and a zero there would make
    /// tampering cheaper than compliance.
    let exitCode (report: DeploymentVerificationReport) : int =
        if
            report.Sections
            |> List.exists (fun s -> VerificationSectionVerdict.isAdverse s.Verdict)
        then
            1
        else
            0

    /// The canonical form the verdict digest is taken over: the framing
    /// version, the schema version, then one line per section in the
    /// order the report carries them, then one line per not-proved
    /// statement.
    ///
    /// **Deliberately excludes `GeneratedAt`, `Actor` and the section
    /// `Findings`.** The digest names the VERDICT SET, so two runs a
    /// minute apart against an unchanged deployment produce the same
    /// digest and an auditor can see at a glance that nothing moved. A
    /// digest that folded in the clock would change on every run and
    /// commit to nothing.
    let canonicalForm (sections: ReportSection list) (notProved: NotProvedStatement list) : string =
        let sb = StringBuilder()
        sb.Append(FramingVersion).Append('\n') |> ignore
        sb.Append(SchemaVersion).Append('\n') |> ignore

        // Length-prefixed fields, so the canonical form is injective
        // over free-text detail without needing a separator byte that the
        // detail could contain. A delimiter-only scheme would let two
        // different verdict sets frame to identical bytes.
        let field (value: string) =
            let value = if isNull value then "" else value
            sb.Append(value.Length).Append(':').Append(value).Append(';') |> ignore

        // An explicit LF, never `AppendLine`. `AppendLine` emits
        // `Environment.NewLine`, so the same report would frame to
        // different bytes on Windows and Linux and the digest would stop
        // being a property of the report. A digest that depends on where
        // it was computed cannot be recomputed by an auditor, which is
        // the whole reason it exists.
        let endLine () = sb.Append('\n') |> ignore

        for section in sections do
            field "section"
            field section.Id
            field (VerificationSectionVerdict.label section.Verdict)
            field (VerificationSectionVerdict.detail section.Verdict)
            endLine ()

        for statement in notProved do
            field "not-proved"
            field statement.Id
            field (defaultArg statement.Narrowing "")
            endLine ()

        sb.ToString()

    /// Render the report as operator-facing text — the CI entry point's
    /// stdout and the shape a support bundle quotes. Pure, so a test
    /// asserts on it directly.
    let render (report: DeploymentVerificationReport) : string =
        let sb = StringBuilder()
        sb.AppendLine "── Deployment verification report ──" |> ignore

        // Round-trip ("o") rather than the sortable/`CultureInfo` overload:
        // this file is Fable-packed, and the single-argument format is the
        // shape the other Fable-packed renderers in Core already use. It is
        // culture-independent by definition, so nothing is lost.
        sb.AppendLine(sprintf "  generated %s by %s" (report.GeneratedAt.ToString "o") report.Actor)
        |> ignore

        sb.AppendLine(sprintf "  outcome: %s" (DeploymentVerificationOutcome.label report.Outcome))
        |> ignore

        sb.AppendLine(sprintf "  verdict digest: %s" report.VerdictDigest) |> ignore
        sb.AppendLine "" |> ignore

        for section in report.Sections do
            sb.AppendLine(
                sprintf
                    "  [%s] %s — %s"
                    ((VerificationSectionVerdict.label section.Verdict).ToUpperInvariant())
                    section.Title
                    (VerificationSectionVerdict.detail section.Verdict)
            )
            |> ignore

            for finding in section.Findings do
                sb.AppendLine(sprintf "        · %s" finding) |> ignore

        sb.AppendLine "" |> ignore
        sb.AppendLine "  What this report does NOT prove:" |> ignore

        for statement in report.NotProved do
            sb.AppendLine(sprintf "    - %s" statement.Statement) |> ignore

            match statement.Narrowing with
            | Some narrowing -> sb.AppendLine(sprintf "      (narrowed: %s)" narrowing) |> ignore
            | None -> ()

        sb.ToString()

/// Platform-Admin-gated read-only surface returning the composed
/// deployment verification report. Mirrors `IDeploymentReadinessApi`:
/// anonymous and non-admin callers both receive `Error` (a deployment's
/// evidence posture is a reconnaissance gift to surface to every
/// visitor), and `Result<_, string>` is the established failure shape.
/// Deployment-wide, never per-tenant (GP 4).
///
/// Mounted only when `ServerConfig.DeploymentVerification =
/// EnabledDeploymentVerification` (default `NoDeploymentVerification`,
/// GP 11/13) — an unopted deployment 404s the route and pays nothing.
type IDeploymentVerificationApi = {
    [<RequiresRole "PlatformAdmin">]
    GetVerificationReport: unit -> Async<Result<DeploymentVerification.DeploymentVerificationReport, string>>
}