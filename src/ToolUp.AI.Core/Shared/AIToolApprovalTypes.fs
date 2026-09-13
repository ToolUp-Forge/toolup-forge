// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System

// ─── Phase 503 — human-in-the-loop tool approval ─────────────────
//
// Four gates already stand between a model's tool call and the tool
// running: the caller's per-module `Read` permission (Phase 36.A), the
// liveness of the authority behind it (Phase 730), the module's declared
// AI-queryability (Phase 36.C), and — for a client-resident invocation —
// the deployment's static allowlist (`IClientToolAuthorizer`, Phase 46).
// Every one of them answers with a policy decided ahead of time, and
// every one of them can only say yes or no.
//
// A consequential action — a write, a deletion, a spend, an outbound
// call — needs a third answer: *ask the person*. This file carries the
// seam that says so and the wire shapes of the round trip, deliberately
// primitive-only and free of any server type, because the SSE event and
// the decision POST are a published contract a non-.NET client has to be
// able to render and answer (GP 10 / GP 12 rule 1).
//
// **Why this is a NEW seam rather than a third case on
// `ClientToolAuthDecision`.** Two reasons, and the second is the
// decisive one:
//
//   1. `ClientToolAuthDecision` is a closed, released DU. A third case
//      breaks every exhaustive `match` a consumer has written over it —
//      a decorating authorizer, a policy that inspects an inner
//      decision — so it is a breaking change to a published contract and
//      not the additive growth this phase is entitled to make.
//   2. `IClientToolAuthorizer` is CLIENT-RESIDENT ONLY, by its own
//      contract and at its single consult site. The actions this phase
//      exists for — writes, deletions, spend, external calls — are
//      server-resident. Folding approval into that seam would deliver a
//      confirmation gate for exactly the tools that do not need one.
//
// So the approval policy is its own seam, consulted at the dispatch site
// for BOTH tool locations and strictly AFTER the allowlist. An action
// refused by any outer gate never reaches a prompt.

/// What the user is being asked to approve, as the dialog renders it.
///
/// Authored by the deployment's own policy, so it can say what the
/// deployment knows and this tier does not — that a tool spends money,
/// that it deletes rather than archives, that it reaches a third party.
/// Both fields are shown to the user; neither is shown to the model.
type ApprovalPrompt = {
    /// One line, in the imperative or the interrogative — the question
    /// the buttons answer. Rendered as the dialog's heading.
    Summary: string
    /// The consequence, in a sentence or two: what will happen if the
    /// user approves, and what is irreversible about it. Rendered under
    /// the summary. May be empty when the summary says everything.
    Detail: string
}

/// Whether a tool invocation needs a human decision before it runs.
///
/// `ApprovalNotRequired` is the seam-absent default and the answer for
/// every tool a policy does not name, so a deployment that composes no
/// policy — and a tool the policy is silent about — behaves byte-for-byte
/// as it did before this phase (GP 11 / GP 13).
type ToolApprovalRequirement =
    /// Run it. No prompt, no suspended dispatch, no audit row: there is
    /// no decision to record.
    | ApprovalNotRequired
    /// Suspend and ask. The invocation runs only if the user approves;
    /// a rejection and an unanswered prompt both abort it.
    | ApprovalRequired of prompt: ApprovalPrompt

/// The deployment's policy on which tool invocations need a human
/// decision. Resolved from DI at the agent loop's dispatch site;
/// absent ⇒ nothing is ever held for approval.
///
/// It is consulted for EVERY tool the loop is about to run, server- and
/// client-resident alike, and it sees the invocation rather than only the
/// tool: `argsJson` is what lets a policy hold `delete_records` for a
/// filter that matches everything and wave through one that names a
/// single row.
///
/// Implementations MUST NOT throw — an argument blob it cannot parse is a
/// reason to REQUIRE approval, not to raise — and MUST be cheap: this
/// runs on the agent-loop dispatch path, so it is deliberately
/// synchronous and value-in / value-out, the same sync justification
/// `IClientToolAuthorizer` and `IMetricsSink` carry.
type IToolApprovalPolicy =
    /// Decide whether this invocation needs a human decision.
    ///
    /// `sourceModule` is the tool's declaring module — the axis a policy
    /// most often cuts on, and the one the decision is audited against.
    abstract member Requires:
        toolName: string *
        sourceModule: string *
        argsJson: string *
        activeModule: string option *
        activePage: string option ->
            ToolApprovalRequirement

/// What the user decided about one held invocation.
///
/// Two cases and no more, because the question is about ONE invocation
/// with arguments the user has just read. A standing "approve this tool
/// for the rest of the conversation" would be an allowance granted over
/// arguments nobody has seen yet, which is the protection this phase
/// exists to add rather than a convenience to layer on it. (Contrast
/// `AllowDecision`, whose `AllowForConversation` is safe precisely
/// because its subject is a module rather than a call.)
type ToolApprovalDecision =
    /// Run the invocation, exactly as the prompt described it.
    | Approved
    /// Do not run it. The model is told the user refused, in terms that
    /// tell it not to re-plan the same action.
    | Rejected

/// Stable wire tokens for `ToolApprovalDecision`. The decision POST and
/// the audit rows both outlive any particular build, so the tokens are
/// part of the contract.
module ToolApprovalDecision =
    /// The token for a decision.
    let toToken (decision: ToolApprovalDecision) : string =
        match decision with
        | Approved -> "Approved"
        | Rejected -> "Rejected"

    /// Parse a token, failing CLOSED. Anything this build cannot
    /// interpret — a token from a newer node, a truncated body, `null` —
    /// reads as `Rejected` rather than as an approval: the wrong answer
    /// in the safe direction costs the user a re-run they asked for, and
    /// the wrong answer in the other direction runs a consequential
    /// action nobody approved.
    let ofToken (token: string) : ToolApprovalDecision =
        match token with
        | "Approved" -> Approved
        | _ -> Rejected

/// Payload of the `POST /api/ai/tool-approval` request. The browser
/// sends this when the user answers an approval dialog.
///
/// **It carries the decision and nothing else that matters.** The tool,
/// its arguments, the conversation and the user are read server-side from
/// the pending record the gate registered before it emitted the SSE
/// event — never from this body — so a client cannot approve an
/// invocation it was not asked about. Same posture as
/// `AIConsentDecisionRequest` and `ClientToolResultRequest`.
type ToolApprovalDecisionRequest = {
    /// Correlates with the `approvalId` of the `ToolApprovalRequired` SSE
    /// event. The server's registry has a `TaskCompletionSource` keyed by
    /// this value; a matching POST completes it and the held invocation
    /// resumes.
    ApprovalId: Guid
    /// Which button the user pressed, as its `ToolApprovalDecision`
    /// token.
    ///
    /// A string rather than the DU itself, deliberately, and for the
    /// reason `AIConsentDecisionRequest.Decision` gives: this record
    /// crosses a hand-written `fetch` from the browser, so its JSON is a
    /// published contract a non-F# client has to be able to produce, and
    /// a DU on the wire would bind that contract to one serialiser's case
    /// encoding. `ToolApprovalDecision.ofToken` — which fails closed — is
    /// the single place it is interpreted.
    Decision: string
}