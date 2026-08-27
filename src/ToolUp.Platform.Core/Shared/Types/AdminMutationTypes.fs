// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Dual control for sensitive admin mutations (Phase 555) ──────────
//
// Phase 551 gave a MODULE a say in who may be granted it. This is the
// complement from the ADMIN side, and it answers a different question:
// 551 asks "does this grant satisfy the module's precondition?", 555 asks
// "did more than one administrator agree to it?". Neither subsumes the
// other — a module carrying `AdminDiscretion` is still a module one
// mistyped user id can hand out, and a policy-bearing module's
// acknowledgement ceremony is still performed by exactly one person.
//
// The control is the ordinary two-person rule from change management: a
// gated write does not apply. It is captured as a PENDING record naming
// who proposed it and exactly what it would do, and a SECOND, DISTINCT
// administrator approves or rejects. Only approval applies it. That
// catches the fat-finger accidental grant even when no counterparty
// exists to consent, and it is the vocabulary a SOC 2 change-management
// control is written in.
//
// **Everything here is inert until a deployment opts in.** The default
// `SingleAdmin` composes no store, wraps no decorator, and reads no
// blob — a deployment that upgrades is byte-for-byte unchanged (GP 11 /
// GP 13). That is why the policy is a `ServerConfig` mode rather than a
// behaviour the store always has and sometimes skips.
//
// **Why the types are here and the machinery is not.** These are the
// shapes an approval surface renders — a pending queue, who proposed it,
// what it would do, when it lapses — so they are Fable-safe and live in
// Core (GP 10). The store, the SHA-256 fingerprint and the
// `IPermissionStore` decorator are server-only and live in
// `Server/Scope/AdminMutationApproval.fs`.

// ─── Scope + policy (555.A) ──────────────────────────────────────────

/// Which mutation classes a `DualControl` deployment gates.
///
/// The narrower arm exists because dual control has a real cost — every
/// gated write becomes two acts by two people — and a deployment that
/// only wants it on the modules it has already declared sensitive should
/// not have to pay it on routine RBAC housekeeping.
[<RequireQualifiedAccess>]
type AdminMutationScope =
    /// Every widening permission write and every exposure increase, on
    /// any module. The strong reading of the two-person rule.
    | AllPermissionWrites
    /// Only writes touching a module that declares a `GrantPolicy`
    /// stricter than `AdminDiscretion` (Phase 551). A deployment that
    /// declares no policy at all gates nothing under this arm — which is
    /// deliberate, and is why it is not the default for a deployment that
    /// asked for dual control.
    | PolicyBearingModulesOnly

module AdminMutationScope =
    /// Stable wire token for persistence + audit.
    let toToken =
        function
        | AdminMutationScope.AllPermissionWrites -> "all-permission-writes"
        | AdminMutationScope.PolicyBearingModulesOnly -> "policy-bearing-modules-only"

    /// Parse a persisted token, **fail-closed**: an unrecognised token
    /// reads as the BROADER scope. Gating a write nobody asked to gate
    /// costs an approval; failing to gate one costs the control.
    let ofToken (token: string) =
        let normalised =
            if isNull (box token) then
                ""
            else
                token.Trim().ToLowerInvariant()

        match normalised with
        | "policy-bearing-modules-only" -> AdminMutationScope.PolicyBearingModulesOnly
        | _ -> AdminMutationScope.AllPermissionWrites

/// The tuning a `DualControl` deployment supplies.
type DualControlSettings = {
    /// Which mutation classes are gated.
    Scope: AdminMutationScope
    /// How long a pending record stays approvable, in minutes. A record
    /// past it is refused rather than approved, and swept.
    ///
    /// Expiry is not tidiness: an approval queue that accumulates
    /// indefinitely is one where an approver eventually rubber-stamps a
    /// proposal whose context nobody remembers, and a stale proposal
    /// carries a mutation computed against a document that has since
    /// moved. A non-positive value is read as the default rather than as
    /// "never expires" — see `DualControlSettings.ttlMinutes`.
    PendingTtlMinutes: int
}

module DualControlSettings =
    /// 72 hours — long enough to span a weekend, short enough that a
    /// proposal is still recognisable to the person approving it.
    [<Literal>]
    let DefaultTtlMinutes = 4320

    let defaults = {
        Scope = AdminMutationScope.AllPermissionWrites
        PendingTtlMinutes = DefaultTtlMinutes
    }

    /// The effective TTL. A non-positive configured value falls back to
    /// the default: "0" from a misread env var must not silently mean
    /// "every proposal is born expired" (which would refuse every gated
    /// write forever) nor "never expires".
    let ttlMinutes (settings: DualControlSettings) =
        if settings.PendingTtlMinutes > 0 then
            settings.PendingTtlMinutes
        else
            DefaultTtlMinutes

/// Phase 555 — whether sensitive admin mutations require a second,
/// distinct administrator's approval before they take effect.
///
/// Default `SingleAdmin` — byte-identical to every release before this
/// one: no approval store is registered, no decorator wraps
/// `IPermissionStore`, no pending blob is written or read, and every
/// admin write applies exactly as it did (GP 11 / GP 13).
[<RequireQualifiedAccess>]
type AdminMutationPolicy =
    /// The pre-555 default. One administrator's write stands on its own.
    | SingleAdmin
    /// A gated write is captured as a pending record and applies only
    /// when a second, distinct administrator approves it.
    | DualControl of DualControlSettings

// ─── The captured mutation (555.B) ───────────────────────────────────

/// The class of `IPermissionStore` write a pending record captures.
/// Carried as a stable token on the audit rows so an operator can
/// dashboard "what kind of change is queued" without decoding a payload.
///
/// Team ROLE changes (`ITeamStore`) are deliberately absent: they are a
/// different store with a different write seam, and gating them is its
/// own phase rather than a field bolted onto this DU. See the file
/// header in `Server/Scope/AdminMutationApproval.fs`.
[<RequireQualifiedAccess>]
type AdminMutationKind =
    /// Whole-document replacement of a team's permission document.
    | TeamPermissions
    /// One member's permissions on one module.
    | MemberPermissions
    /// The team's default per-module permissions.
    | TeamDefaults
    /// One module's exposure state for the team.
    | ModuleExposure

module AdminMutationKind =
    let toToken =
        function
        | AdminMutationKind.TeamPermissions -> "team-permissions"
        | AdminMutationKind.MemberPermissions -> "member-permissions"
        | AdminMutationKind.TeamDefaults -> "team-defaults"
        | AdminMutationKind.ModuleExposure -> "module-exposure"

/// The exact mutation a pending record will replay on approval.
///
/// Captured as a VALUE rather than as a closure or a re-derivable
/// intention, for the reason the whole control exists: what the second
/// administrator approves must be what the first administrator proposed,
/// not a recomputation against a document that has moved since. The
/// fingerprint on `PendingAdminMutation` is taken over this value.
[<RequireQualifiedAccess>]
type AdminMutation =
    | SetTeamPermissions of permissions: TeamPermissions
    | SetMemberPermissions of userId: string * moduleName: string * permissions: ModulePermission list
    | SetTeamDefaults of defaults: Map<string, ModulePermission list>
    | SetModuleExposure of moduleName: string * state: ModuleExposure

module AdminMutation =
    let kind =
        function
        | AdminMutation.SetTeamPermissions _ -> AdminMutationKind.TeamPermissions
        | AdminMutation.SetMemberPermissions _ -> AdminMutationKind.MemberPermissions
        | AdminMutation.SetTeamDefaults _ -> AdminMutationKind.TeamDefaults
        | AdminMutation.SetModuleExposure _ -> AdminMutationKind.ModuleExposure

    /// One-line operator-facing summary for an approval queue. Names what
    /// would change, never the full document — an approval UI that has to
    /// render a whole permission blob is one nobody reads.
    let summary =
        function
        | AdminMutation.SetTeamPermissions p ->
            $"replace the team permission document ({p.Members.Count} member entries, {p.Defaults.Count} defaults)"
        | AdminMutation.SetMemberPermissions(userId, moduleName, permissions) ->
            let perms = permissions |> List.map string |> String.concat ", "
            $"grant '{userId}' [{perms}] on module '{moduleName}'"
        | AdminMutation.SetTeamDefaults defaults -> $"replace the team default permissions ({defaults.Count} modules)"
        | AdminMutation.SetModuleExposure(moduleName, state) ->
            $"set module '{moduleName}' exposure to '{ModuleExposure.toToken state}'"

/// A mutation captured and awaiting a second administrator.
///
/// `ProposedBy` is required and never defaulted: a two-person rule whose
/// first person is "unknown" is not a two-person rule, so the decorator
/// refuses an unattributable gated write rather than parking one.
type PendingAdminMutation = {
    /// Opaque, deterministic-per-proposal identifier. Identity by value
    /// (GP 12 rule 1) — an approver names this string, never a handle.
    RequestId: string
    /// The team whose permission document the mutation targets.
    TeamId: string
    /// The captured mutation, replayed verbatim on approval.
    Mutation: AdminMutation
    /// SHA-256 over the canonical serialisation of `Mutation`. Binds the
    /// approval to the exact bytes proposed: an approver's decision names
    /// the fingerprint, and a record whose fingerprint no longer matches
    /// its payload cannot be approved.
    Fingerprint: string
    /// Operator-facing one-liner (`AdminMutation.summary`), stored so a
    /// queue renders without re-deriving it and so the audit row carries
    /// the same words the approver saw.
    Summary: string
    /// The administrator who proposed it.
    ProposedBy: string
    ProposedAtUtc: DateTimeOffset
    /// After this instant the record is refused rather than approved.
    ExpiresAtUtc: DateTimeOffset
}

module PendingAdminMutation =
    /// Has the record lapsed as at `now`? Evaluated lazily at every read
    /// rather than by a sweeper hosted service, so a `DualControl`
    /// deployment costs no background timer (GP 13) and an unswept record
    /// is still not approvable.
    let isExpired (now: DateTimeOffset) (pending: PendingAdminMutation) = now >= pending.ExpiresAtUtc

/// A typed refusal from the dual-control ceremony. Every arm names the
/// request, so a caller renders an actionable message without parsing
/// prose and an audit row carries the discriminator an operator
/// dashboards on — the `GrantRefusal` shape (Phase 551).
[<RequireQualifiedAccess>]
type AdminMutationRefusal =
    /// No pending record under that id for that team. Also what an
    /// already-decided request looks like: the record is removed when it
    /// is applied or rejected, so a replayed approval finds nothing —
    /// which is the idempotence property, not a lost record.
    | UnknownRequest of requestId: string
    /// The approver is the proposer. Structurally refused: this is the
    /// control, not a policy check that a configuration could relax.
    | SelfApprovalRefused of requestId: string * actorId: string
    /// The record lapsed before anyone approved it.
    | Expired of requestId: string * expiredAtUtc: DateTimeOffset
    /// A gated write arrived with no resolvable acting administrator, so
    /// there is no first person to be distinct from. Refused rather than
    /// parked.
    | UnattributedProposer
    /// The record's payload no longer hashes to its recorded
    /// fingerprint — the shape a tampered or partially-written blob
    /// takes. Refused; nothing is applied.
    | FingerprintMismatch of requestId: string
    /// The pending store could not be read or written. Refused, never
    /// admitted: a two-person rule that fails open on a storage blip is
    /// a single-person rule with extra steps.
    | ApprovalStoreUnavailable of detail: string

module AdminMutationRefusal =
    /// The stable discriminator an audit row and an operator dashboard
    /// group by.
    let code =
        function
        | AdminMutationRefusal.UnknownRequest _ -> "unknown-request"
        | AdminMutationRefusal.SelfApprovalRefused _ -> "self-approval-refused"
        | AdminMutationRefusal.Expired _ -> "expired"
        | AdminMutationRefusal.UnattributedProposer -> "unattributed-proposer"
        | AdminMutationRefusal.FingerprintMismatch _ -> "fingerprint-mismatch"
        | AdminMutationRefusal.ApprovalStoreUnavailable _ -> "approval-store-unavailable"

    /// Human-readable rendering, prefixed with a stable machine-greppable
    /// code. Interface members that return `Result<unit, string>` carry
    /// this; callers wanting the typed value use the ceremony's own entry
    /// points, which never stringify.
    let describe =
        function
        | AdminMutationRefusal.UnknownRequest id ->
            $"DUAL-CONTROL-UNKNOWN-REQUEST: no pending admin mutation '{id}' (it may have already been approved, rejected, or swept)."
        | AdminMutationRefusal.SelfApprovalRefused(id, actor) ->
            $"DUAL-CONTROL-SELF-APPROVAL: '{actor}' proposed request '{id}' and may not approve it; a second, distinct administrator must."
        | AdminMutationRefusal.Expired(id, at) ->
            $"DUAL-CONTROL-EXPIRED: pending admin mutation '{id}' lapsed at {at:o} and must be proposed again."
        | AdminMutationRefusal.UnattributedProposer ->
            "DUAL-CONTROL-UNATTRIBUTED: the acting administrator could not be resolved, so the write cannot enter a two-person ceremony. Propose it through the dual-control entry point with an explicit actor."
        | AdminMutationRefusal.FingerprintMismatch id ->
            $"DUAL-CONTROL-FINGERPRINT-MISMATCH: pending admin mutation '{id}' does not hash to its recorded fingerprint; it will not be applied."
        | AdminMutationRefusal.ApprovalStoreUnavailable detail ->
            $"DUAL-CONTROL-STORE-UNAVAILABLE: the pending-approval store could not be reached ({detail}); the write is refused rather than applied unreviewed."

/// What an approval or rejection actually did.
[<RequireQualifiedAccess>]
type AdminMutationDecision =
    /// A second administrator approved it and the captured mutation was
    /// applied to the underlying store.
    | Applied of requestId: string
    /// A second administrator rejected it; nothing was applied and the
    /// record is gone.
    | Rejected of requestId: string

/// The outcome of a gated write that did NOT apply. Returned by the
/// dual-control entry point so a policy-aware admin surface can render
/// "queued for approval, request X" rather than inferring it from an
/// error string.
type AdminMutationQueued = {
    RequestId: string
    Fingerprint: string
    ExpiresAtUtc: DateTimeOffset
}

/// Phase 555 — what a write through the dual-control seam did.
[<RequireQualifiedAccess>]
type AdminMutationWriteOutcome =
    /// The write was not gated (or the deployment runs `SingleAdmin`) and
    /// applied immediately, exactly as it would have pre-555.
    | AppliedImmediately
    /// The write was gated and is parked awaiting a second administrator.
    /// Nothing changed.
    | QueuedForApproval of AdminMutationQueued

/// Phase 730 — the ONE place the "this write was parked, not applied"
/// signal on the legacy `Result<unit, string>` channel is defined.
///
/// **Why a shared module rather than a string literal at each end.** The
/// dual-control gate has to report a parked write through
/// `IPermissionStore`'s `Result<unit, string>`, which has no room for a
/// typed outcome, so the fact rides a prefixed message. A reader that
/// wanted to distinguish "parked" from "storage broke" therefore had to
/// recognise a message minted in another file — and classifying by a prose
/// prefix matched against a renderer somewhere else is exactly the defect
/// Phase 36.A recorded against `AIAgentEngine.isErrorToolResult`, whose
/// comment says "update both" where a shared constant would do. Reworded
/// either end independently and the recognition silently stops matching,
/// with no compile error and no failing test that names the cause.
///
/// So the mint and the recognition are BOTH derived from `Code` here, in
/// `Platform.Core` — which both `GrantPolicyGuard.fs` (the reader) and
/// `AdminMutationApproval.fs` (the writer) compile after, and neither of
/// which can see the other. Rewording the human half of the message is
/// free; the machine half cannot drift because there is only one of it.
[<RequireQualifiedAccess>]
module DualControlSignal =

    /// The stable, greppable discriminator. Operator runbooks and log
    /// queries cut on this string; treat it as a wire token.
    [<Literal>]
    let Code = "DUAL-CONTROL-PENDING-APPROVAL"

    /// Render the parked-write message. The request id is delimited by
    /// single quotes so `tryParseRequestId` can recover it without the
    /// surrounding prose being load-bearing.
    let message (requestId: string) (expiresAtUtc: DateTimeOffset) =
        $"{Code}: the write did not apply. It is queued as request '{requestId}' and requires approval by a second, distinct administrator before {expiresAtUtc:o}."

    /// Recover the queued request id from a message minted by `message`.
    /// `None` for any other error text — including an error that merely
    /// mentions dual control — because the id is what makes the signal
    /// actionable, and a "parked" verdict a caller cannot route to an
    /// approver is worse than an honest "the store refused".
    let tryParseRequestId (errorText: string) : string option =
        if
            String.IsNullOrEmpty errorText
            || not (errorText.StartsWith(Code, StringComparison.Ordinal))
        then
            None
        else
            let opening = errorText.IndexOf '\''

            if opening < 0 then
                None
            else
                let closing = errorText.IndexOf('\'', opening + 1)

                if closing <= opening + 1 then
                    None
                else
                    Some(errorText.Substring(opening + 1, closing - opening - 1))