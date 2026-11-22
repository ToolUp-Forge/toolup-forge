// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Collections.Generic
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Compose

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
//
// The release window below the projection is federation-tier too: it keys
// on clean-room templates and filter cohorts, which only the gate reads.

type BudgetEpoch = ToolUp.Platform.Compose.BudgetEpoch
type BudgetScope = ToolUp.Platform.Compose.BudgetScope
type WithholdCharge = ToolUp.Platform.Compose.WithholdCharge
type PrivacyBudgetPolicy = ToolUp.Platform.Compose.PrivacyBudgetPolicy
type BudgetSpend = ToolUp.Platform.Compose.BudgetSpend
type PrivacyBudget = ToolUp.Platform.Compose.PrivacyBudget
type BudgetRefusal = ToolUp.Platform.Compose.BudgetRefusal
type BudgetDecision = ToolUp.Platform.Compose.BudgetDecision
type SpendOutcome = ToolUp.Platform.Compose.SpendOutcome
type IPrivacyBudgetLedger = ToolUp.Platform.Compose.IPrivacyBudgetLedger
type NoPrivacyBudgetLedger = ToolUp.Platform.Compose.NoPrivacyBudgetLedger
type InMemoryPrivacyBudgetLedger = ToolUp.Platform.Compose.InMemoryPrivacyBudgetLedger
type BlobPrivacyBudgetLedger = ToolUp.Platform.Compose.BlobPrivacyBudgetLedger
type PrivacyBudgetMeter = ToolUp.Platform.Compose.PrivacyBudgetMeter

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

// ─── The release window: what a near-duplicate submission is compared with ──
//
// A budget bounds how many answers cross; it cannot see that two of them,
// each clearing the floor, differ by one member, which the difference of
// the two answers then discloses. The window is the record that can: per
// template and partition (a counterparty, or a vintage), the filter cohorts
// of the answers released recently, as each cohort's size, membership hash
// and count sketch, never a member. The gate compares a submission with it
// and records the submission in the same act, so two near-duplicate
// submissions racing each other cannot both be admitted.
//
// It lives beside the budget because it is the same kind of record, kept on
// the data holder's side and never sent, and because a refusal it causes is
// charged to the budget.

/// The partition a release window keeps its releases under.
[<RequireQualifiedAccess>]
type ReleaseWindowPartition =
    /// The releases to one counterparty.
    | Counterparty of peerId: string
    /// The releases on one vintage, to every counterparty.
    | Vintage of vintage: string

/// Which releases a submission is compared with: one template's, in one
/// partition.
type ReleaseWindowKey = {
    TemplateId: string
    Partition: ReleaseWindowPartition
}

/// One released answer's filter cohort, as the window keeps it.
type ReleasedFilter = {
    Filter: FilterCohort
    ReleasedAt: DateTimeOffset
    CounterpartyPeerId: string
    MethodName: string
}

/// The window's answer to a submission.
[<RequireQualifiedAccess>]
type WindowAdmission =
    /// No release in the window is near the submission, which is now
    /// recorded as released.
    | Admitted
    /// The submission is within the floor of the release whose membership
    /// hash this is. Nothing was recorded.
    | NearDuplicateOf of earlier: string
    /// The window could not answer. Nothing was recorded, and the gate
    /// withholds rather than release an answer it could not compare.
    | WindowUnavailable of reason: string

/// How far back a submission is compared, and how long a release is kept.
type ReleaseWindowPolicy = {
    /// A `PerCounterparty` submission is compared with the releases made
    /// within this span. A `PerVintage` submission is compared with every
    /// release the window keeps on the vintage.
    Length: TimeSpan
    /// A release older than this is dropped from the window.
    Retention: TimeSpan
}

[<RequireQualifiedAccess>]
module ReleaseWindowPolicy =

    /// 365 days for both: a vintage's members do not change with the
    /// calendar, so a short window would only let a patient counterparty
    /// wait out the check.
    let defaults: ReleaseWindowPolicy = {
        Length = TimeSpan.FromDays 365.0
        Retention = TimeSpan.FromDays 365.0
    }

    /// Set the span a `PerCounterparty` submission is compared over.
    let withLength (length: TimeSpan) (policy: ReleaseWindowPolicy) = { policy with Length = length }

    /// Set how long a release is kept.
    let withRetention (retention: TimeSpan) (policy: ReleaseWindowPolicy) = { policy with Retention = retention }

/// The record of released filter cohorts the near-duplicate check reads.
type IReleaseWindow =
    /// Compare `release` with the releases under `key` made at or after
    /// `comparedFrom`, after dropping those made before `keptFrom`, and, when
    /// none is within `floor` of it, record it. Atomic: two submissions
    /// under one key cannot both be compared before either is recorded.
    abstract Admit:
        key: ReleaseWindowKey *
        release: ReleasedFilter *
        floor: int *
        comparedFrom: DateTimeOffset *
        keptFrom: DateTimeOffset ->
            Async<WindowAdmission>

    /// Whether the window survives a restart and is shared across
    /// instances.
    abstract IsDurable: bool

/// One key's releases, and the admission every window implements.
type internal ReleaseWindowState = { Releases: ReleasedFilter list }

module internal ReleaseWindowState =

    let empty: ReleaseWindowState = { Releases = [] }

    /// Drop what retention has passed, compare `release` with what is left
    /// inside the comparison span, and append it when nothing is near.
    /// `Error` carries the near release's membership hash.
    let admit
        (release: ReleasedFilter)
        (floor: int)
        (comparedFrom: DateTimeOffset)
        (keptFrom: DateTimeOffset)
        (state: ReleaseWindowState)
        : Result<ReleaseWindowState, string> =
        let kept = state.Releases |> List.filter (fun r -> r.ReleasedAt >= keptFrom)

        let near =
            kept
            |> List.tryFind (fun r ->
                r.ReleasedAt >= comparedFrom
                && FilterCohort.isNear floor r.Filter release.Filter)

        match near with
        | Some earlier -> Error earlier.Filter.MembershipHash
        | None -> Ok { Releases = kept @ [ release ] }

/// In-process window: right for one instance, lost on restart and wrong
/// across replicas, which `IsDurable = false` says. Atomic under a monitor.
type InMemoryReleaseWindow() =
    let gate = obj ()
    let states = Dictionary<ReleaseWindowKey, ReleaseWindowState>()

    interface IReleaseWindow with
        member _.IsDurable = false

        member _.Admit(key, release, floor, comparedFrom, keptFrom) = async {
            return
                lock gate (fun () ->
                    let state =
                        match states.TryGetValue key with
                        | true, s -> s
                        | false, _ -> ReleaseWindowState.empty

                    match ReleaseWindowState.admit release floor comparedFrom keptFrom state with
                    | Ok next ->
                        states[key] <- next
                        WindowAdmission.Admitted
                    | Error earlier -> WindowAdmission.NearDuplicateOf earlier)
        }

/// `IConditionalBlobStorage`-backed window (`IsDurable = true`): one JSON
/// document per key under the reserved `_platform` container, every
/// admission a compare-and-swap against its etag. Conditional writes are
/// required at construction, on `BlobPrivacyBudgetLedger`'s argument: a
/// download-modify-upload lets two near-duplicate submissions both read a
/// window holding neither and both be released.
type BlobReleaseWindow(blobs: IBlobStorage) =
    let container = ScopeKey.PlatformContainer
    let maxAttempts = 8

    let cas =
        match box blobs with
        | :? IConditionalBlobStorage as c -> c
        | _ ->
            invalidArg
                "blobs"
                "BlobReleaseWindow requires an IBlobStorage that also implements IConditionalBlobStorage (conditional writes). The compare-and-swap is what stops two near-duplicate submissions both being compared with a window that holds neither. Use InMemoryReleaseWindow for a single-instance holder, or a backend with conditional-write support."

    /// The key's document, named by a digest of the key, so no free-text
    /// component reaches a path.
    let blobNameFor (key: ReleaseWindowKey) =
        let partition =
            match key.Partition with
            | ReleaseWindowPartition.Counterparty peerId -> [ "counterparty"; peerId ]
            | ReleaseWindowPartition.Vintage vintage -> [ "vintage"; vintage ]

        $"cleanroom/releases/{Sha256.hexOfUtf8 (CanonicalFrame.text (key.TemplateId :: partition))}.json"

    let parse (bytes: byte[]) =
        try
            let parsed = JsonRpc.deserialize<ReleaseWindowState> (Encoding.UTF8.GetString bytes)

            if isNull (box parsed) || isNull (box parsed.Releases) then
                None
            else
                Some parsed
        with _ ->
            None

    let rec transact attempts name release floor comparedFrom keptFrom = async {
        if attempts <= 0 then
            return
                WindowAdmission.WindowUnavailable
                    $"the release window could not settle a conditional write on '{name}' within {maxAttempts} attempts"
        else
            let! read = cas.DownloadWithETag(container, name)

            let current, condition =
                match read with
                | Ok(bytes, etag) -> parse bytes, IfMatch etag
                | Error _ -> Some ReleaseWindowState.empty, IfAbsent

            match current with
            | None ->
                // Present but unreadable: refusing is the only safe reading,
                // since overwriting it would forget what was released.
                return
                    WindowAdmission.WindowUnavailable
                        $"the stored release window '{name}' could not be read; refusing rather than overwriting it"
            | Some state ->
                match ReleaseWindowState.admit release floor comparedFrom keptFrom state with
                | Error earlier -> return WindowAdmission.NearDuplicateOf earlier
                | Ok next ->
                    let bytes = Encoding.UTF8.GetBytes(JsonRpc.serialize next)
                    let! written = cas.UploadWithETag(container, name, bytes, condition)

                    match written with
                    | Ok _ -> return WindowAdmission.Admitted
                    | Error(ETagMismatch _) -> return! transact (attempts - 1) name release floor comparedFrom keptFrom
                    | Error(ConditionalWriteFailure message) ->
                        return
                            WindowAdmission.WindowUnavailable $"the release window could not write '{name}': {message}"
    }

    interface IReleaseWindow with
        member _.IsDurable = true

        member _.Admit(key, release, floor, comparedFrom, keptFrom) =
            transact maxAttempts (blobNameFor key) release floor comparedFrom keptFrom

/// The composed window posture: the window, its policy, and the clock the
/// policy's spans are read against.
type ReleaseWindowMeter = {
    Window: IReleaseWindow
    Policy: ReleaseWindowPolicy
    /// Injected so a span is testable without waiting for one.
    Now: unit -> DateTimeOffset
}

[<RequireQualifiedAccess>]
module ReleaseWindowMeter =

    /// Compare through `window` under `policy`, on the wall clock.
    let create (window: IReleaseWindow) (policy: ReleaseWindowPolicy) : ReleaseWindowMeter = {
        Window = window
        Policy = policy
        Now = fun () -> DateTimeOffset.UtcNow
    }

    /// Compare on an injected clock.
    let withClock (now: unit -> DateTimeOffset) (meter: ReleaseWindowMeter) = { meter with Now = now }