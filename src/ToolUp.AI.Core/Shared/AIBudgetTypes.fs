// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform

// ─── Phase 9s — per-user AI token budget ─────────────────────────
//
// Phase 9d already bounds a SCOPE's AI spend: `_platform.usage`
// carries `MaxAITokensPerDay` / `MaxAITokensPerMonth`, and
// `QuotaEnforcingProvider` refuses before any provider spend. What a
// per-scope ceiling cannot do is stop ONE member of a team consuming
// the whole team's allowance in an afternoon — the team stays inside
// its budget and every other member is locked out until the window
// rolls. This file is the per-USER window that 9d deliberately does
// not carry, and nothing else: the two team windows stay where they
// are and are not restated here.
//
// **Expressed on the Phase 689 budget seam, not beside it.** The cap
// is a `BudgetClaim` (`Ceiling` = the configured cap, `Spent` = the
// user's tokens summed from `IEventStore`, `Requested` = this call's
// estimate) over `BudgetPeriod.Hourly`; the check is
// `BudgetPolicy.verdict`, which also returns the near-limit case an
// operator wants before the wall is hit; a refusal is a
// `BudgetDenial`, so the message names the window, the cap and the
// used figure separately rather than as one opaque sentence. The
// seam's `<= 0` = unrestricted rule is what makes `PerUserPerHour =
// None` byte-for-byte today's behaviour (GP 11): an unconfigured
// deployment short-circuits before it reads anything at all.
//
// **Consumption is DERIVED, never reserved.** A token budget is
// unusual among the seam's instances in that nobody can know a turn's
// cost until the provider reports it, so there is nothing honest to
// reserve at admission. The window is therefore summed from the Phase
// 6i.A `AILatencyRecord` stream — the always-on per-turn telemetry
// that already carries `PromptTokens` / `OutputTokens` — rather than
// held in an `IBudgetLedger` counter. That is also why this file
// composes the seam's *pure* half (`BudgetClaim` / `BudgetPolicy` /
// `BudgetAccount`) and not `IBudgetLedger`.
//
// **Fable-safe** (GP 10): primitives, records and the seam's own
// Fable-safe types. The enforcer, the event-store sum and the
// provider decorator are Server tier.

/// Phase 9s — reserved `IConfigStore` key for the per-scope AI token
/// budget schema, and its field keys.
///
/// A dedicated module key rather than more fields on Phase 9d's
/// `_platform.usage`, for the reason 9d gave for splitting from the
/// general `_platform` schema: the two have different lifecycles. The
/// 9d ceilings are billing-coupled ceilings on the TEAM's spend; this
/// one is a fairness control WITHIN a team, and an operator who sets
/// one has not necessarily opted into the other.
[<RequireQualifiedAccess>]
module AIBudgetConfigKey =
    [<Literal>]
    let value = "_platform.ai.budget"

    /// Field key — maximum AI tokens (prompt + output) one user may
    /// consume within a UTC hour. `int`, default `0` (= unbounded).
    [<Literal>]
    let maxTokensPerUserPerHour = "MaxAITokensPerUserPerHour"

/// Phase 9s — the per-user token budget for one scope.
///
/// One window, deliberately. The two team windows
/// (`MaxAITokensPerDay` / `MaxAITokensPerMonth`) are Phase 9d's and
/// live in `_platform.usage`; duplicating them here would give a
/// deployment two places to configure one ceiling and two answers when
/// they disagree.
///
/// `None` = unbounded, and is what an unconfigured deployment reads.
type AITokenBudgetPolicy = {
    /// Maximum tokens (`PromptTokens + OutputTokens`) one user may
    /// consume within a single UTC hour. `None` = unbounded.
    PerUserPerHour: int option
}

[<RequireQualifiedAccess>]
module AITokenBudgetPolicy =
    /// The unbounded policy — what a scope with no configuration has,
    /// and what every parse failure degrades to. An unreadable cap
    /// must never become a refusal: a config store that hiccups would
    /// otherwise deny every AI call in the deployment.
    let unbounded: AITokenBudgetPolicy = { PerUserPerHour = None }

    /// `BudgetSubject.Domain` every claim, denial and ledger key in
    /// this family is namespaced under. Named by the Phase 689 header.
    [<Literal>]
    let Domain = "ai-tokens"

    /// `BudgetClaim.Dimension` of the per-user hourly ceiling. Appears
    /// verbatim in the denial, so a client branches on the dimension
    /// rather than string-matching the message.
    [<Literal>]
    let PerUserPerHourDimension = "tokens-per-user-hour"

    /// The window the per-user ceiling is measured over.
    let perUserPeriod = BudgetPeriod.Hourly

    /// Read the policy out of the raw `_platform.ai.budget` config map.
    ///
    /// Missing, unparseable and `<= 0` all read as `None`. That the
    /// three collapse is the point: `0` is the schema default an admin
    /// UI writes when a field is cleared, and a deployment that has
    /// never touched the tab has no key at all — they mean the same
    /// thing and must not take different branches.
    let ofRaw (raw: Map<string, string>) : AITokenBudgetPolicy =
        let perUserPerHour =
            match raw |> Map.tryFind AIBudgetConfigKey.maxTokensPerUserPerHour with
            | Some s ->
                match Int32.TryParse s with
                | true, v when v > 0 -> Some v
                | _ -> None
            | None -> None

        { PerUserPerHour = perUserPerHour }

    /// The subject a per-user claim is made for. The user id rides
    /// `ClassLabel` — the seam's documented axis for "who policy
    /// discriminates on WITHIN a scope", which is exactly what a
    /// per-user window inside a team scope is.
    let subject (scopeId: string) (userId: string) : BudgetSubject =
        BudgetSubject.create Domain scopeId userId

    /// The hourly claim for a user who has consumed `spent` tokens and
    /// is asking for an estimated `requested` more.
    ///
    /// `None` becomes a ceiling of `0M`, which
    /// `BudgetClaim.isUnrestricted` reads as unbounded — so the absent
    /// budget and the empty budget are one value and neither needs a
    /// branch at the call site.
    let perUserClaim (policy: AITokenBudgetPolicy) (spent: decimal) (requested: decimal) : BudgetClaim =
        let ceiling =
            match policy.PerUserPerHour with
            | Some cap -> decimal cap
            | None -> 0M

        BudgetClaim.create PerUserPerHourDimension ceiling spent requested

    /// `true` when this policy constrains nothing — the fast path the
    /// enforcer short-circuits on before reading the event store.
    let isUnbounded (policy: AITokenBudgetPolicy) : bool = policy.PerUserPerHour.IsNone

/// Phase 9s — one recorded per-user budget refusal. Written through
/// `IEventStore` under `AITokenBudgetExceeded.SourceModule` each time
/// a call is refused, so "which users are hitting the wall" is a query
/// rather than a log grep.
///
/// Sibling of `AIProviderFailoverRecord`: a reserved source module of
/// its own, read back with `ReadBySource(scope, _)`. Carries no prompt
/// content and no key material — identities, a window label and three
/// counts.
type AITokenBudgetExceeded = {
    OccurredAt: DateTime
    /// The user whose window was exhausted. This is the whole point of
    /// the record: Phase 9d's refusal names only the scope.
    UserId: string
    /// Storage scope the refused call belonged to — the same scope key
    /// `AILatencyRecord`s are written under, so the two streams join.
    ScopeId: string
    /// `BudgetPeriod.label` of the window that was exhausted —
    /// `"hourly"` for the only window this phase adds. Present as a
    /// field rather than implied by the event type so a second window
    /// added later needs no new stream.
    WindowKind: string
    /// The configured ceiling, in tokens.
    CapTokens: decimal
    /// Tokens the user had already consumed in this window when the
    /// call arrived.
    UsedTokens: decimal
    /// The estimate this call asked for on top of `UsedTokens`.
    /// Carried separately because "already over" and "this one request
    /// takes you over" have different remedies (Phase 689).
    RequestedTokens: decimal
    /// `BudgetPeriod.key` of the window — `"2026-09-16T14"`. Two
    /// refusals in the same key are the same wall.
    PeriodKey: string
}

module AITokenBudgetExceeded =
    /// Reserved `IEventStore` source-module namespace for per-user
    /// token-budget refusals. Sibling of `AILatencyRecord.SourceModule`
    /// and `AIProviderFailoverRecord.SourceModule`.
    [<Literal>]
    let SourceModule = "_platform.ai.budget_exceeded"

    /// Reserved `IEventStore` event-type for one recorded refusal.
    [<Literal>]
    let EventType = "AITokenBudgetExceeded"

    /// Build the record from a Phase 689 denial. The denial already
    /// carries every figure; this is a projection, not a second
    /// accounting of the same refusal.
    let ofDenial (occurredAt: DateTime) (denial: BudgetDenial) : AITokenBudgetExceeded = {
        OccurredAt = occurredAt
        UserId = denial.ClassLabel
        ScopeId = denial.ScopeId
        WindowKind = BudgetPeriod.label AITokenBudgetPolicy.perUserPeriod
        CapTokens = denial.Quota
        UsedTokens = denial.Spent
        RequestedTokens = denial.Requested
        PeriodKey = denial.PeriodKey
    }

    /// The user-facing refusal message, as the phase's acceptance
    /// criterion words it. The typed record above, not this string, is
    /// the contract — but the wording is pinned by a test because it is
    /// what a user reads when their work stops, and "quota exceeded"
    /// with no numbers tells them nothing about when to try again.
    let message (denial: BudgetDenial) : string =
        sprintf "Token budget exceeded for this user this hour — %M tokens used vs %M cap" denial.Spent denial.Quota