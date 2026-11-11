// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform

// ─── Per-user AI token budget ─────────────────────────
//
// The team quota already bounds a SCOPE's AI spend: `_platform.usage`
// carries `MaxAITokensPerDay` / `MaxAITokensPerMonth`, and
// `QuotaEnforcingProvider` refuses before any provider spend. What a
// per-scope ceiling cannot do is stop ONE member of a team consuming
// the whole team's allowance in an afternoon — the team stays inside
// its budget and every other member is locked out until the window
// rolls. This file is the per-USER window that 9d deliberately does
// not carry, and nothing else: the two team windows stay where they
// are and are not restated here.
//
// **Expressed on the budget seam, not beside it.** The cap
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
// reserve at admission. The window is therefore summed from the
// `AILatencyRecord` stream — the always-on per-turn telemetry
// that already carries `PromptTokens` / `OutputTokens` — rather than
// held in an `IBudgetLedger` counter. That is also why this file
// composes the seam's *pure* half (`BudgetClaim` / `BudgetPolicy` /
// `BudgetAccount`) and not `IBudgetLedger`.
//
// **Fable-safe** (GP 10): primitives, records and the seam's own
// Fable-safe types. The enforcer, the event-store sum and the
// provider decorator are Server tier.

/// Reserved `IConfigStore` key for the per-scope AI token
/// budget schema, and its field keys.
///
/// A dedicated module key rather than more fields on the team quota's
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

    // ─── The monetary ceilings ───────────────────────
    //
    // The spend budget shares this module key rather than minting a
    // second one. The token budget's rationale for splitting from the team quota's
    // `_platform.usage` — different lifecycles, different decisions —
    // does not apply between these two: they are the same decision
    // ("how much AI may this scope consume") expressed in two units,
    // an operator sets them on one visit to one tab, and a separate
    // key would put a token ceiling and its monetary sibling behind
    // different panels.

    /// Field key — maximum AI spend one user may incur within
    /// `spendPerUserPeriod`, in the registered rate card's currency.
    /// `decimal`, default `0` (= unbounded).
    [<Literal>]
    let maxSpendPerUser = "MaxAISpendPerUser"

    /// Field key — `BudgetPeriod.label` of the window
    /// `maxSpendPerUser` is measured over. Defaults to `"daily"`.
    [<Literal>]
    let spendPerUserPeriod = "AISpendPerUserPeriod"

    /// Field key — maximum AI spend the whole scope may incur within
    /// `spendPerScopePeriod`, in the registered rate card's currency.
    /// `decimal`, default `0` (= unbounded).
    [<Literal>]
    let maxSpendPerScope = "MaxAISpendPerScope"

    /// Field key — `BudgetPeriod.label` of the window
    /// `maxSpendPerScope` is measured over. Defaults to `"daily"`.
    [<Literal>]
    let spendPerScopePeriod = "AISpendPerScopePeriod"

// ─── What a budget does when it cannot read ──────────────────────
//
// A budget check reads two things before it decides: the scope's policy
// from the config store, and the consumption window from the event
// store. Either read can fail. What happens then is the deployment's
// declared `BudgetPosture`, not an accident of a catch arm, and every
// such failure is recorded as a `BudgetWindowUnreadable` event, so an
// operator can tell "no ceiling is configured" from "the ceiling could
// not be read".

/// What a budget check does when it cannot read its policy or its window.
[<RequireQualifiedAccess>]
type BudgetPosture =
    /// Admit the call as if no ceiling applied, and record the failure.
    /// The default: a cost ceiling that converts a config-store or
    /// telemetry outage into a total AI outage fails worse than the spend
    /// it protects against.
    | FailOpen
    /// Refuse the call with a denial naming what could not be read. For a
    /// deployment whose ceiling is a hard commitment rather than a
    /// fairness control.
    | FailClosed

[<RequireQualifiedAccess>]
module BudgetPosture =
    /// Stable lowercase label for logs and the diagnostics panel.
    let label =
        function
        | BudgetPosture.FailOpen -> "fail-open"
        | BudgetPosture.FailClosed -> "fail-closed"

/// Which of a budget check's two reads failed.
[<RequireQualifiedAccess>]
type BudgetRead =
    /// The scope's policy, read from the config store.
    | Policy
    /// The consumption window, summed from the event store.
    | Window

[<RequireQualifiedAccess>]
module BudgetRead =
    /// Stable lowercase label; the suffix of an unreadable denial's
    /// dimension.
    let label =
        function
        | BudgetRead.Policy -> "policy"
        | BudgetRead.Window -> "window"

/// One budget check that could not read its policy or its window. Written
/// through `IEventStore` under `BudgetWindowUnreadable.SourceModule` on
/// every such failure, whichever posture is in force, so "when was the
/// budget blind, and what did it do" is a query.
type BudgetWindowUnreadable = {
    OccurredAt: DateTime
    /// Storage scope the call belonged to.
    ScopeId: string
    /// The caller, `""` when the call was unattributed.
    UserId: UserId
    /// `BudgetSubject.Domain` of the budget that could not read —
    /// `"ai-tokens"` or `"ai-spend"`.
    Domain: string
    /// Which read failed.
    Read: BudgetRead
    /// The posture that decided the call: `FailOpen` admitted it,
    /// `FailClosed` refused it.
    Posture: BudgetPosture
    /// The failure's message. Diagnostic; the wording is not stable.
    Reason: string
}

[<RequireQualifiedAccess>]
module BudgetWindowUnreadable =
    /// Reserved `IEventStore` source-module namespace for unreadable
    /// budget reads. Sibling of `AITokenBudgetExceeded.SourceModule`.
    [<Literal>]
    let SourceModule = "_platform.ai.budget_unreadable"

    /// Reserved `IEventStore` event-type for one unreadable read.
    [<Literal>]
    let EventType = "BudgetWindowUnreadable"

    /// Prefix of the `BudgetDenial.Dimension` a `FailClosed` refusal
    /// carries; the read's label follows it.
    [<Literal>]
    let DimensionPrefix = "unreadable-"

    /// The denial dimension naming an unreadable `read`:
    /// `"unreadable-policy"` or `"unreadable-window"`.
    let dimension (read: BudgetRead) : string = DimensionPrefix + BudgetRead.label read

    /// `true` for a denial a `FailClosed` posture issued because a read
    /// failed, rather than because a ceiling was reached.
    let isDenial (denial: BudgetDenial) : bool =
        not (isNull denial.Dimension) && denial.Dimension.StartsWith DimensionPrefix

    /// The refusal a `FailClosed` posture issues for this record. Quota
    /// and spend are zero because neither is known; the dimension names
    /// the read that failed.
    let denial (periodKey: string) (requested: decimal) (record: BudgetWindowUnreadable) : BudgetDenial = {
        Domain = record.Domain
        ScopeId = record.ScopeId
        ClassLabel = record.UserId.Value
        Dimension = dimension record.Read
        Quota = 0M
        Spent = 0M
        Requested = requested
        PeriodKey = periodKey
    }

    /// The user-facing refusal message for a `FailClosed` refusal.
    let message (denial: BudgetDenial) : string =
        sprintf
            "AI budget unavailable — this deployment's %s budget could not read its %s and refuses calls it cannot account for. Try again shortly."
            denial.Domain
            (denial.Dimension.Substring DimensionPrefix.Length)

/// The per-user token budget for one scope.
///
/// One window, deliberately. The two team windows
/// (`MaxAITokensPerDay` / `MaxAITokensPerMonth`) are the team quota's and
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
    /// and what every parse failure degrades to. A policy that cannot be
    /// READ is not this: it is decided by `BudgetPosture`.
    let unbounded: AITokenBudgetPolicy = { PerUserPerHour = None }

    /// `BudgetSubject.Domain` every claim, denial and ledger key in
    /// this family is namespaced under. Named by the header.
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

/// One recorded per-user budget refusal. Written through
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
    /// the record: the refusal names only the scope.
    UserId: UserId
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
    /// takes you over" have different remedies.
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

    /// Build the record from a denial. The denial already
    /// carries every figure; this is a projection, not a second
    /// accounting of the same refusal.
    let ofDenial (occurredAt: DateTime) (denial: BudgetDenial) : AITokenBudgetExceeded = {
        OccurredAt = occurredAt
        UserId = UserId denial.ClassLabel
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
        if BudgetWindowUnreadable.isDenial denial then
            BudgetWindowUnreadable.message denial
        else
            sprintf "Token budget exceeded for this user this hour — %M tokens used vs %M cap" denial.Spent denial.Quota
// ─── Monetary AI cost budgets ────────────────────────
//
// The per-user token budget above bounds a user's TOKENS. This is the currency-
// denominated sibling: the same budget seam, the same decorator
// chain, the same refusal path — with a price model in front of it so
// an operator can say "5.00 per user per day" instead of a token count
// nobody can convert into money without a rate card.
//
// **The price table is this phase's own and never enters the platform
// core (GP 1).** `Budget.fs`'s seam is deliberately currency-free and
// takes abstract units; `ResourceKinds.computeUnits` says so in as
// many words. Pricing is what turns a request into the `Requested`
// figure a `BudgetClaim` carries, so it belongs on the AI tier where a
// provider and a model are meaningful, and nowhere lower.
//
// **No rate card ships in-tree.** There is no default price for any
// provider or model: a shipped rate card is a vendor's price list
// embedded in a vendor-neutral SDK, it is wrong within a quarter, and
// it silently bills an operator at a number nobody in their
// organisation chose. `ModelPriceTable.empty` is what an unconfigured
// deployment has, and an unpriced model degrades to count-only (GP 11)
// rather than blocking or guessing.
//
// **Fable-safe** (GP 10): primitives, records, `Map`, and the seam's
// own Fable-safe types. The enforcer, the window sum and the provider
// decorator are Server tier.

/// The four rates that price one `(providerId, model)`,
/// each expressed **per million tokens** in the table's currency.
///
/// Per million rather than per token because that is the unit every
/// operator's rate card is written in, and because a per-token rate is
/// a number with six leading zeros that invites a transcription error
/// nobody notices until the ceiling refuses a call it should have
/// admitted. `decimal` throughout, never `float` — the budget seam's
/// precision rule, and the same reason `UsageRecord.Quantity` is
/// decimal.
///
/// Four rates rather than two because a provider's cached-input and
/// cache-write tokens are billed at materially different rates from
/// fresh input, and `TokenUsage` already reports all four. Collapsing
/// them would make a cache-heavy deployment's budget wrong in the
/// expensive direction.
type ModelPrice = {
    /// Rate for input tokens the provider did NOT serve from its
    /// prompt cache — i.e. `PromptTokens - CachedPromptTokens`.
    InputPerMillion: decimal
    /// Rate for the cached portion of `PromptTokens`. Usually a
    /// fraction of `InputPerMillion`.
    CachedInputPerMillion: decimal
    /// Rate for generated output tokens.
    OutputPerMillion: decimal
    /// Rate for tokens WRITTEN to the provider's prompt cache this turn
    /// (`TokenUsage.CacheCreationTokens`). Usually a premium over
    /// `InputPerMillion`; `0M` on providers that do not report it.
    CacheCreationPerMillion: decimal
}

[<RequireQualifiedAccess>]
module ModelPrice =
    /// A price with every rate at zero — what a deployment that
    /// declares a model but no rates has. Prices every turn at `0M`,
    /// which is a real (free) price and NOT the unpriced case: an entry
    /// that exists says "this model costs nothing", an absent entry
    /// says "I do not know what this model costs". The two take
    /// different branches and must not be conflated.
    let free: ModelPrice = {
        InputPerMillion = 0M
        CachedInputPerMillion = 0M
        OutputPerMillion = 0M
        CacheCreationPerMillion = 0M
    }

    /// The common shape: an input rate, an output rate, and no cache
    /// pricing declared (cached input billed as input, cache writes
    /// free).
    let simple (inputPerMillion: decimal) (outputPerMillion: decimal) : ModelPrice = {
        InputPerMillion = inputPerMillion
        CachedInputPerMillion = inputPerMillion
        OutputPerMillion = outputPerMillion
        CacheCreationPerMillion = 0M
    }

/// The operator's rate card: what each `(providerId,
/// model)` costs, and the currency every figure derived from it is
/// denominated in.
///
/// Supplied by the composition root through the documented
/// `ComposeExtensions.ServiceConfig` seam
/// (`s.AddSingleton<ModelPriceTable>(table)`), never read from a vendor.
/// Its **presence in DI is the composition gate**: a deployment that
/// registers none resolves exactly the object graph it always did — no
/// decorator, no config read, no allocation (GP 11 / GP 13).
type ModelPriceTable = {
    /// Operator-supplied currency tag — `"USD"`, `"GBP"`, `"credits"`.
    /// Opaque to the SDK: it is echoed into the refusal, the usage
    /// record's `Unit` and the diagnostics panel so every figure a
    /// human reads is labelled, and it is never parsed, converted or
    /// validated against a currency registry (GP 1).
    Currency: string
    /// Rates keyed by `ModelPriceTable.key providerId model`. A key
    /// with no entry is the UNPRICED case.
    Rates: Map<string, ModelPrice>
}

[<RequireQualifiedAccess>]
module ModelPriceTable =
    /// The rate-card key for one provider/model pair.
    ///
    /// `providerId` is `AIProviderCapabilities.ProviderName` — the same
    /// string `AILatencyRecord.ProviderName` and the metering record's
    /// `provider` metadata carry, so the table, the window sum and the
    /// usage ledger all join on one identifier.
    let key (providerId: string) (model: string) : string =
        let orEmpty (s: string) = if isNull (box s) then "" else s
        orEmpty providerId + "/" + orEmpty model

    /// An empty rate card in `currency` — every model unpriced.
    let empty (currency: string) : ModelPriceTable = {
        Currency = currency
        Rates = Map.empty
    }

    /// A rate card from `(providerId, model, price)` triples.
    let ofList (currency: string) (entries: (string * string * ModelPrice) list) : ModelPriceTable = {
        Currency = currency
        Rates = entries |> List.map (fun (p, m, price) -> key p m, price) |> Map.ofList
    }

    /// The declared price for `(providerId, model)`, or `None` when the
    /// operator has not priced it.
    let tryFind (providerId: string) (model: string) (table: ModelPriceTable) : ModelPrice option =
        table.Rates |> Map.tryFind (key providerId model)

    /// `true` when the rate card prices nothing at all — the fast path
    /// a caller short-circuits on.
    let isEmpty (table: ModelPriceTable) : bool = Map.isEmpty table.Rates

    /// Cost of one turn's token usage, in the table's currency.
    ///
    /// **The single pricing function in this phase.** The pre-call
    /// estimate and the post-call true-up both call it, with different
    /// token figures — so the two can never disagree about what a given
    /// usage costs, and any drift between them is drift in the TOKEN
    /// estimate alone. That is the property the drift case
    /// pins.
    ///
    /// `promptTokens` is the TOTAL input the provider reported (cached +
    /// fresh), matching `TokenUsage.PromptTokens`; the cached portion is
    /// charged at `CachedInputPerMillion` and the remainder at
    /// `InputPerMillion`. Every count is clamped at zero and
    /// `cachedPromptTokens` is clamped to `promptTokens`, so a provider
    /// reporting more cache than prompt produces an implausible price
    /// rather than a negative one.
    let cost
        (price: ModelPrice)
        (promptTokens: int)
        (cachedPromptTokens: int)
        (outputTokens: int)
        (cacheCreationTokens: int)
        : decimal =
        let prompt = max 0 promptTokens
        let cached = min prompt (max 0 cachedPromptTokens)
        let fresh = prompt - cached
        let output = max 0 outputTokens
        let cacheWrite = max 0 cacheCreationTokens

        (decimal fresh * price.InputPerMillion
         + decimal cached * price.CachedInputPerMillion
         + decimal output * price.OutputPerMillion
         + decimal cacheWrite * price.CacheCreationPerMillion)
        / 1_000_000M

    /// Cost of one turn on `(providerId, model)`. `None` when that pair
    /// is unpriced — the caller's signal to degrade to count-only and
    /// warn once, never to guess a rate or to block.
    let tryCost
        (providerId: string)
        (model: string)
        (promptTokens: int)
        (cachedPromptTokens: int)
        (outputTokens: int)
        (cacheCreationTokens: int)
        (table: ModelPriceTable)
        : decimal option =
        tryFind providerId model table
        |> Option.map (fun price -> cost price promptTokens cachedPromptTokens outputTokens cacheCreationTokens)

/// The `UsageRecord.ResourceKind` the post-call true-up
/// writes actual spend under.
///
/// Declared here rather than in `Platform.Core`'s `ResourceKinds`
/// because a currency-denominated kind in the SDK core is the thing GP
/// 1 and `ResourceKinds.computeUnits`'s own comment rule out.
/// `UsageRecord.ResourceKind` is a `string` and the type explicitly
/// says companion packages may publish their own kinds, so this is the
/// sanctioned route rather than a workaround.
[<RequireQualifiedAccess>]
module AISpendResourceKind =
    /// Actual monetary cost of one AI turn, priced from the reported
    /// `TokenUsage` through the registered `ModelPriceTable`. Emitted
    /// beside the existing `ai.tokens.input` / `ai.tokens.output`
    /// records, never instead of them — the token counts stay the
    /// authoritative usage figures and this is the money they came to.
    ///
    /// `UsageRecord.Unit` carries the rate card's currency tag, so a
    /// ledger that mixes deployments can tell pounds from credits.
    [<Literal>]
    let cost = "ai.cost"

/// One monetary ceiling: an amount, and the window it
/// refills over.
///
/// `Ceiling` is denominated in the `ModelPriceTable.Currency` the
/// deployment registered. It is deliberately NOT carried here: a
/// budget that named its own currency could disagree with the rate
/// card that prices the calls it governs, and there is no honest
/// answer to that disagreement. One rate card, one currency, every
/// figure in it.
type AISpendBudget = {
    /// The window this ceiling is measured over and refills at.
    Period: BudgetPeriod
    /// The ceiling, in the rate card's currency. `<= 0` is
    /// unrestricted, per `BudgetClaim.isUnrestricted`.
    Ceiling: decimal
}

/// A scope's monetary AI budget.
///
/// Two independent ceilings, because the phase's own goal names both
/// shapes an operator reasons in: "5.00 per USER per day" (fairness
/// within a team) and "a per-TEAM monthly cap" (the bill). They are
/// evaluated as two `BudgetClaim`s in one `BudgetPolicy.verdict` call,
/// per-user first — the ordering rule: report the ceiling
/// whose remedy the caller can act on before the one whose remedy is
/// "wait for the month to roll".
///
/// `None` on both = unbounded, and is what an unconfigured deployment
/// reads.
type AISpendBudgetPolicy = {
    /// Ceiling on ONE member's spend within the scope.
    PerUser: AISpendBudget option
    /// Ceiling on the whole scope's spend.
    PerScope: AISpendBudget option
}

[<RequireQualifiedAccess>]
module AISpendBudgetPolicy =
    /// The unbounded policy — what a scope with no configuration has,
    /// and what every parse failure degrades to. A policy that cannot be
    /// READ is not this: it is decided by `BudgetPosture`.
    let unbounded: AISpendBudgetPolicy = { PerUser = None; PerScope = None }

    /// `BudgetSubject.Domain` every claim, denial and refusal record in
    /// this family is namespaced under.
    ///
    /// **Its own domain, not `AITokenBudgetPolicy.Domain`.** The
    /// budget seam's substrate note requires budget-exhausted to be
    /// distinguishable from token-quota-exhausted by the LABEL rather
    /// than by a second error type, and `Domain` plus `Dimension` is
    /// exactly the pair that carries it: a client reads
    /// `"ai-spend"` / `"spend-per-user"` off the denial instead of
    /// string-matching a sentence.
    [<Literal>]
    let Domain = "ai-spend"

    /// `BudgetClaim.Dimension` of the per-user monetary ceiling.
    [<Literal>]
    let PerUserDimension = "spend-per-user"

    /// `BudgetClaim.Dimension` of the per-scope monetary ceiling.
    [<Literal>]
    let PerScopeDimension = "spend-per-scope"

    /// The window a ceiling configured with no explicit period is
    /// measured over.
    ///
    /// Daily, for both ceilings: it is the window the phase's own goal
    /// states, and it is the one an operator can react to — a monthly
    /// default would let a misconfiguration run for weeks before
    /// anything refused. Chosen explicitly rather than falling out of
    /// the DU's declaration order, which is a property of a list and
    /// not a decision anyone made.
    let defaultPeriod = BudgetPeriod.Daily

    /// The subject a per-user claim is made for — the user id rides
    /// `ClassLabel`, the seam's documented axis for "who policy
    /// discriminates on WITHIN a scope".
    let userSubject (scopeId: string) (userId: string) : BudgetSubject =
        BudgetSubject.create Domain scopeId userId

    /// The subject a per-scope claim is made for — the scope's default
    /// limits, so `ClassLabel` is empty.
    let scopeSubject (scopeId: string) : BudgetSubject = BudgetSubject.ofScope Domain scopeId

    /// Read one `(ceiling, period)` pair out of the raw config map.
    ///
    /// Missing, unparseable and `<= 0` ceilings all read as `None` —
    /// the same collapse `AITokenBudgetPolicy.ofRaw` makes, and for the
    /// same reason: `0` is what the schema default and a cleared admin
    /// field both produce, and a deployment that never opened the tab
    /// has no key at all. An unparseable or absent PERIOD on a ceiling
    /// that IS set falls back to `defaultPeriod` rather than dropping
    /// the ceiling: losing the window is a formatting problem, losing
    /// the ceiling is a cost control silently switching itself off.
    let private readBudget (raw: Map<string, string>) (ceilingKey: string) (periodKey: string) : AISpendBudget option =
        let ceiling =
            match raw |> Map.tryFind ceilingKey with
            | Some s ->
                match
                    Decimal.TryParse(s, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture)
                with
                | true, v when v > 0M -> Some v
                | _ -> None
            | None -> None

        ceiling
        |> Option.map (fun c ->
            let period =
                raw
                |> Map.tryFind periodKey
                |> Option.bind BudgetPeriod.parse
                |> Option.defaultValue defaultPeriod

            { Period = period; Ceiling = c })

    /// Read the policy out of the raw `_platform.ai.budget` config map.
    let ofRaw (raw: Map<string, string>) : AISpendBudgetPolicy = {
        PerUser = readBudget raw AIBudgetConfigKey.maxSpendPerUser AIBudgetConfigKey.spendPerUserPeriod
        PerScope = readBudget raw AIBudgetConfigKey.maxSpendPerScope AIBudgetConfigKey.spendPerScopePeriod
    }

    /// `true` when this policy constrains nothing — the fast path the
    /// enforcer short-circuits on before reading the event store.
    let isUnbounded (policy: AISpendBudgetPolicy) : bool =
        policy.PerUser.IsNone && policy.PerScope.IsNone

    /// The claim for one configured ceiling, given what the subject has
    /// already spent in its window and what this call is estimated to
    /// add.
    let claim (dimension: string) (budget: AISpendBudget) (spent: decimal) (requested: decimal) : BudgetClaim =
        BudgetClaim.create dimension budget.Ceiling spent requested

/// One recorded monetary budget refusal, written through
/// `IEventStore` under its own reserved source module so "which users
/// and which teams are hitting a spend ceiling" is a query rather than
/// a log grep.
///
/// A separate stream from `AITokenBudgetExceeded` rather than a
/// discriminator field on it, because the two carry different units: a
/// reader summing `CapTokens` across a mixed stream would be adding
/// tokens to pounds. Same shape, same reserved-source-module
/// convention, different unit — which is the honest reason to split a
/// stream.
type AISpendBudgetExceeded = {
    OccurredAt: DateTime
    /// The user whose call was refused. Empty when the refused ceiling
    /// was the SCOPE's rather than a member's.
    UserId: UserId
    /// Storage scope the refused call belonged to.
    ScopeId: string
    /// `BudgetClaim.Dimension` of the ceiling that refused —
    /// `"spend-per-user"` or `"spend-per-scope"`. The field a reader
    /// branches on.
    Dimension: string
    /// The `ModelPriceTable.Currency` every amount here is denominated
    /// in. Carried on the record rather than assumed, so a stream read
    /// back after an operator changed rate cards is still legible.
    Currency: string
    /// `BudgetPeriod.label` of the exhausted window.
    WindowKind: string
    /// The configured ceiling.
    CapAmount: decimal
    /// Spend already booked against the ceiling in this window.
    SpentAmount: decimal
    /// The estimate this call asked for on top of `SpentAmount`.
    RequestedAmount: decimal
    /// `BudgetPeriod.key` of the window. Two refusals in the same key
    /// are the same wall.
    PeriodKey: string
}

module AISpendBudgetExceeded =
    /// Reserved `IEventStore` source-module namespace for monetary
    /// budget refusals. Sibling of `AITokenBudgetExceeded.SourceModule`.
    [<Literal>]
    let SourceModule = "_platform.ai.spend_exceeded"

    /// Reserved `IEventStore` event-type for one recorded refusal.
    [<Literal>]
    let EventType = "AISpendBudgetExceeded"

    /// The window a denial was measured over, recovered from its
    /// dimension and the policy that produced it. The denial carries
    /// the period KEY but not the period, and the key alone cannot be
    /// inverted (`"2026-09-16"` is a daily key, but so is the daily
    /// prefix of nothing else).
    let private windowOf (policy: AISpendBudgetPolicy) (dimension: string) : BudgetPeriod =
        let budget =
            if dimension = AISpendBudgetPolicy.PerScopeDimension then
                policy.PerScope
            else
                policy.PerUser

        budget
        |> Option.map _.Period
        |> Option.defaultValue AISpendBudgetPolicy.defaultPeriod

    /// Build the record from a denial. The denial already
    /// carries every figure; this is a projection, not a second
    /// accounting of the same refusal.
    let ofDenial
        (occurredAt: DateTime)
        (currency: string)
        (policy: AISpendBudgetPolicy)
        (denial: BudgetDenial)
        : AISpendBudgetExceeded =
        {
            OccurredAt = occurredAt
            UserId = UserId denial.ClassLabel
            ScopeId = denial.ScopeId
            Dimension = denial.Dimension
            Currency = currency
            WindowKind = BudgetPeriod.label (windowOf policy denial.Dimension)
            CapAmount = denial.Quota
            SpentAmount = denial.Spent
            RequestedAmount = denial.Requested
            PeriodKey = denial.PeriodKey
        }

    /// The user-facing refusal message.
    ///
    /// Distinct from `AITokenBudgetExceeded.message` in the three ways
    /// that matter to whoever reads it when their work stops: it says
    /// SPEND rather than token budget, every figure is labelled with
    /// the operator's currency, and it names whether the ceiling that
    /// refused was theirs or the team's — which decides whether the
    /// remedy is "wait" or "talk to whoever owns the budget".
    ///
    /// The typed record above, not this string, is the contract; the
    /// wording is pinned by a test because a refusal with no numbers
    /// tells a user nothing about when to try again.
    let message (currency: string) (windowLabel: string) (denial: BudgetDenial) : string =
        if BudgetWindowUnreadable.isDenial denial then
            BudgetWindowUnreadable.message denial
        else
            let whose =
                if denial.Dimension = AISpendBudgetPolicy.PerScopeDimension then
                    "this team"
                else
                    "this user"

            sprintf
                "AI spend budget exceeded for %s (%s) — %s %M spent against a %s %M cap, and this request is estimated at a further %s %M. The budget refills at the start of the next %s window."
                whose
                denial.Dimension
                currency
                denial.Spent
                currency
                denial.Quota
                currency
                denial.Requested
                windowLabel