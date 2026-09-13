// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System

// ─── Phase 36.D — cross-module read consent ──────────────────────
//
// Phases 36.A / 730 / 36.C gate the `_platform.ai.*` cross-module read
// family on three questions, all answered by the deployment: may this
// caller read the module, is the authority behind that permission live,
// and is the module on the AI surface at all. None of them asks the
// USER, in the moment, whether the agent may go and read their data from
// a module they did not name.
//
// This file carries the wire shapes of that fourth, innermost gate. They
// are deliberately primitive-only and free of any server type: the SSE
// event and the decision POST are a published contract a non-.NET client
// has to be able to render and answer (GP 10 / GP 12 rule 1).

/// What the user decided about one target module in one conversation.
///
/// Three cases and no more, because each maps to a button the user
/// actually sees. The lifetimes differ and the difference is the point:
///
///   * `AllowOnce` authorises the read in front of the user and nothing
///     further — the next read from the same module prompts again. It is
///     RECORDED (so the audit trail and the conversation's own history
///     show it), but it never satisfies a later lookup.
///   * `AllowForConversation` is remembered for the lifetime of the
///     conversation: every subsequent read from that module flows
///     without a prompt. Deliberately NOT cross-conversation — a new
///     conversation asks again.
///   * `Denied` is likewise remembered for the conversation, and
///     subsequent reads are refused WITHOUT re-prompting. A user who has
///     said no once should not be asked again for every turn the model
///     spends re-planning.
type AllowDecision =
    | AllowOnce
    | AllowForConversation
    | Denied

/// Stable wire tokens for `AllowDecision`. Used by the persisted
/// per-conversation blob and by the audit rows, both of which outlive any
/// particular build, so the tokens are part of the contract.
module AllowDecision =
    let toToken (decision: AllowDecision) : string =
        match decision with
        | AllowOnce -> "AllowOnce"
        | AllowForConversation -> "AllowForConversation"
        | Denied -> "Denied"

    /// Parse a token, failing CLOSED. Anything this build cannot
    /// interpret — a token from a newer node, a truncated blob, `null` —
    /// reads as `Denied` rather than as an allowance: the wrong answer in
    /// the safe direction costs a re-prompt, the wrong answer in the
    /// other direction reads a module the user never approved.
    let ofToken (token: string) : AllowDecision =
        match token with
        | "AllowOnce" -> AllowOnce
        | "AllowForConversation" -> AllowForConversation
        | _ -> Denied

/// How a deployment wants the consent gate to behave.
///
/// **The default is `RememberPerConversation`, and that is deliberately
/// NOT the byte-for-byte-unchanged choice GP 11 usually asks for.** This
/// is the second place the SDK inverts that posture, for the same reason
/// as the first ([Phase 36.C](../../ToolUp.AI/TECHNICAL_GUIDE.md)): an
/// agent reading across a user's modules without ever asking them is the
/// behaviour the phase exists to remove, so shipping the gate off by
/// default would ship nothing. A deployment that wants the pre-36.D flow
/// back declares `TrustEverything` — one line, and it is a decision
/// somebody made rather than one nobody noticed.
type AIConsentMode =
    /// Prompt on every cross-module read. The per-conversation allowlist
    /// is neither consulted nor written — every read is its own decision.
    /// For deployments where a standing allowance is not acceptable.
    | AlwaysAsk
    /// The default. Prompt on the first read from each target module in a
    /// conversation; honour the recorded decision for the rest of it.
    | RememberPerConversation
    /// Never prompt. For single-user / self-hosted / development
    /// deployments where the person running the agent and the person
    /// owning the data are the same person. No dialog, no suspended
    /// dispatch, and no consent audit row — there is no decision to
    /// record.
    | TrustEverything

module AIConsentMode =
    let toToken (mode: AIConsentMode) : string =
        match mode with
        | AlwaysAsk -> "AlwaysAsk"
        | RememberPerConversation -> "RememberPerConversation"
        | TrustEverything -> "TrustEverything"

    /// Parse a token, failing CLOSED onto the default rather than onto
    /// `TrustEverything`: an unreadable mode must never be the one that
    /// turns the gate off.
    let ofToken (token: string) : AIConsentMode =
        match token with
        | "AlwaysAsk" -> AlwaysAsk
        | "TrustEverything" -> TrustEverything
        | _ -> RememberPerConversation

/// The per-conversation consent record, persisted beside the
/// conversation's own blobs. Keyed by TARGET module name.
///
/// A record with one field rather than a bare `Map` so the persisted
/// shape can gain fields later without a migration — the same reason
/// `ConversationMeta` is a record server-side.
type AIConsentState = {
    CrossModuleAllowlist: Map<string, string>
}

module AIConsentState =
    let empty: AIConsentState = { CrossModuleAllowlist = Map.empty }

    /// The recorded decision for a module, or `None` when the user has
    /// not been asked about it in this conversation.
    ///
    /// A `null` map is the documented additive-field hazard on the STJ
    /// read path (a blob written before this field existed deserialises
    /// it as `null`, and a null F# `Map` throws on every operation), so
    /// it is coerced here rather than at each call site.
    let decisionFor (moduleName: string) (state: AIConsentState) : AllowDecision option =
        if isNull (box state.CrossModuleAllowlist) then
            None
        else
            state.CrossModuleAllowlist
            |> Map.tryFind moduleName
            |> Option.map AllowDecision.ofToken

    let record (moduleName: string) (decision: AllowDecision) (state: AIConsentState) : AIConsentState =
        let existing =
            if isNull (box state.CrossModuleAllowlist) then
                Map.empty
            else
                state.CrossModuleAllowlist

        {
            state with
                CrossModuleAllowlist = existing |> Map.add moduleName (AllowDecision.toToken decision)
        }

    /// Whether a recorded decision lets a read proceed with no prompt.
    ///
    /// `AllowOnce` answers `false` here on purpose: it authorised the one
    /// read it was given for, so a later read has to ask again. That is
    /// the whole behavioural difference between the two allow cases, and
    /// it lives in one function so no call site can implement half of it.
    let satisfies (decision: AllowDecision) : bool =
        match decision with
        | AllowForConversation -> true
        | AllowOnce
        | Denied -> false

/// Payload of the `/api/ai/consent` POST. The browser sends this when
/// the user answers a consent dialog.
///
/// **It carries the decision and nothing else that matters.** The
/// conversation, the target module and the user are read server-side from
/// the pending-consent entry the tool registered before it emitted the
/// SSE event — never from this body — so a client cannot record a
/// decision against a module or a conversation it was not asked about.
/// (Same posture as `ClientToolResultRequest`, whose `TaskId` is
/// likewise audit-only and whose authority comes from the recorded
/// pending entry.)
type AIConsentDecisionRequest = {
    /// Correlates with the `consentId` of the `AIConsentRequired` SSE
    /// event. The server's registry has a `TaskCompletionSource` keyed by
    /// this value; a matching POST completes it and the suspended tool
    /// resumes.
    ConsentId: Guid
    /// Which button the user pressed, as its `AllowDecision` token.
    ///
    /// A string rather than the DU itself, deliberately. This record
    /// crosses a hand-written `fetch` from the browser, so its JSON is a
    /// published contract a non-F# client has to be able to produce; a DU
    /// on the wire would bind that contract to one serialiser's case
    /// encoding. The token vocabulary is the same one the persisted record
    /// and the audit rows use, and `AllowDecision.ofToken` — which fails
    /// closed to `Denied` — is the single place it is interpreted.
    Decision: string
}