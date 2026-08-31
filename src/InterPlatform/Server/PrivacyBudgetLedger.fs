// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open ToolUp.Platform

// ─── Phase 675 — the ledger moved; this is what stayed ───────────────
//
// Phase 190's privacy-budget ledger — the value types, the
// `IPrivacyBudgetLedger` seam, `LedgerState`, `NoPrivacyBudgetLedger`,
// `InMemoryPrivacyBudgetLedger`, `BlobPrivacyBudgetLedger` and
// `PrivacyBudgetMeter` — now lives in the SDK server core at
// `ToolUp.Platform.Server/Server/PrivacyBudgetLedger.fs`, under
// `namespace ToolUp.Platform`. Nothing was reimplemented and nothing was
// forked: the file was MOVED.
//
// **Why it moved.** Phase 675 binds the same cumulative accounting to
// the grounding tier's declassification routines. The two candidate
// shapes were a `ToolUp.Facts.Server -> ToolUp.InterPlatform` package
// edge (a companion depending on another companion, against GP 1) or a
// second ledger mirrored Facts-side. The second is the worse of the two
// by a distance, and this file's own sibling `CleanRoomGate.fs` gives
// the argument in the general case: "the whole argument for a structural
// gate is that there is ONE path, and a second implementation of 'the
// gate' is how a path that enforces slightly less appears." For a
// privacy budget that is not a style preference — two ledgers that drift
// are two different answers to "has this counterparty spent its
// allowance", and only one of them can be right. So the seam went to the
// layer both tiers already depend on.
//
// **What this leaves behind, and its exact limit.** The type
// abbreviations below keep `ToolUp.InterPlatform.BudgetScope` and its
// siblings resolvable, so a consumer that NAMES a budget type by its old
// qualified path still compiles (GP 11). They cannot do more than that:
// an F# type abbreviation re-exports the type NAME and neither the union
// CASES nor the companion modules. A consumer that pattern-matches
// `BudgetReserved` / `BudgetExhausted` / `PerpetualBudget`, or that calls
// `PrivacyBudgetPolicy.create` / `PrivacyBudgetMeter.spendFor`, needs
// `open ToolUp.Platform` — one line, and the migration doc
// (`docs/migrations/675-declassification-budgets.md`) says so rather
// than leaving it to be discovered. Every in-repo caller already opened
// it, so the move touched no call site but the one below.
//
// The one piece of genuinely federation-tier code in the original file
// is `refusalDecision`, which renders a `BudgetRefusal` into
// `GateDecision` — the clean-room gate's own vocabulary. It stayed here,
// where its return type lives. The grounding tier renders the same
// refusal into `FactDisclosureVerdict` instead, which is exactly why the
// ledger's own refusal type is neither.

type BudgetEpoch = ToolUp.Platform.BudgetEpoch
type BudgetScope = ToolUp.Platform.BudgetScope
type WithholdCharge = ToolUp.Platform.WithholdCharge
type PrivacyBudgetPolicy = ToolUp.Platform.PrivacyBudgetPolicy
type BudgetSpend = ToolUp.Platform.BudgetSpend
type PrivacyBudget = ToolUp.Platform.PrivacyBudget
type BudgetRefusal = ToolUp.Platform.BudgetRefusal
type BudgetDecision = ToolUp.Platform.BudgetDecision
type SpendOutcome = ToolUp.Platform.SpendOutcome
type IPrivacyBudgetLedger = ToolUp.Platform.IPrivacyBudgetLedger
type NoPrivacyBudgetLedger = ToolUp.Platform.NoPrivacyBudgetLedger
type InMemoryPrivacyBudgetLedger = ToolUp.Platform.InMemoryPrivacyBudgetLedger
type BlobPrivacyBudgetLedger = ToolUp.Platform.BlobPrivacyBudgetLedger
type PrivacyBudgetMeter = ToolUp.Platform.PrivacyBudgetMeter

/// The federation tier's projection of a budget refusal into the
/// clean-room gate's decision vocabulary.
///
/// Named for the tier rather than left on `PrivacyBudgetMeter`: that
/// module now lives in `ToolUp.Platform`, and a same-named module in
/// this namespace would SHADOW it for every file here that opens both —
/// silently making `PrivacyBudgetMeter.spendFor` unresolvable. One
/// module per home, and the home is where the return type lives.
[<RequireQualifiedAccess>]
module PeerPrivacyBudget =

    /// A refusal expressed in the broker's own decision vocabulary, so an
    /// exhausted budget denies through the same `GateDecision` shape a
    /// k-floor breach does and lands in the same audit field.
    ///
    /// Quantities appear here and only here: this reason is recorded
    /// receiver-side and never sent on the wire, for exactly the reason
    /// `PeerCleanRoomDecisionPayload` documents — a caller able to read
    /// back "remaining 0.4" while varying its query has a second oracle
    /// beside the one the k-floor already refuses it.
    let refusalDecision (templateId: string) (refusal: BudgetRefusal) : GateDecision =
        match refusal with
        | BudgetExhausted(requested, remaining, ceiling) ->
            Withheld(
                sprintf
                    "privacy budget for template '%s' is exhausted: the query costs %M epsilon against %M remaining of a %M ceiling"
                    templateId
                    requested
                    remaining
                    ceiling
            )
        | BudgetLedgerUnavailable reason ->
            Withheld(
                sprintf
                    "the privacy-budget ledger for template '%s' could not account for this query, so it was refused rather than released unaccounted: %s"
                    templateId
                    reason
            )