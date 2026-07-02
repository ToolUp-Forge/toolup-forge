// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.HostActionAuditHook

open System
open ToolUp.Platform

// ─── Phase 272 — hosted-tree action audit emission (GP 6) ─────────────
//
// Phase 113's `IActionAuthorizer` *authorizes* a hosted tree's `Dispatch` /
// `Call` / `Navigate` / `Invoke`, but GP 6 mandates "audit everything that
// changes state" — and an authorized hosted action left NO audit trail. For
// the regulated / Sovereign buyers in the Vision (provenance is the moat), an
// action a user drove through a hosted UI must be traceable. This hook emits
// one `HostActionDispatched` audit event per authorized action (and per
// DENIED action — the security-relevant case) through the shipped
// `IAuditLog`, keyed on the neutral `ActionDescriptor`.
//
// **Default-on when an audit sink is composed.** A deployment that wires an
// `IAuditLog` gets the trail for free by composing `HostActionAuditHook` over
// it beside the Phase 113 authorizer (see `authorizeAndAudit`). A deployment
// with no audit sink composes `disabled` and pays nothing (GP 13).
//
// **Scope carried structurally (GP 4).** The event is written under the
// action's own `ActionDescriptor.Scope` (or `_platform` when unscoped) — the
// same scope the authorizer gated on, so the trail is scope-isolated by
// construction.
//
// **Best-effort (never blocks / throws).** Every audit write is wrapped; a
// failure is logged at `Warn` and swallowed, matching `IAuditLog.Record`'s
// contract — an audit-write failure must not fail the primary action.
//
// **Open-core boundary.** The event is keyed on the neutral `ActionDescriptor`
// (kind / target / scope) + the decision; no tree-language type appears.

/// Records one `HostActionDispatched` audit event per hosted-tree action
/// decision (allow OR deny). Wired beside the Phase 113 authorizer so
/// authorize-then-audit is one path (see `authorizeAndAudit`).
type IHostActionAuditHook =
    /// Record the decision for `action` taken on behalf of `principal`. The
    /// event is written under the action's own scope. Never throws.
    abstract RecordAction:
        principal: Subject -> action: ActionDescriptor -> decision: AuthorizationDecision -> Async<unit>

/// Sanitise a `Subject` to the `(kind, id option)` pair the audit row carries
/// — the only subject information that reaches the trail (no PII beyond the
/// id). Mirrors the Phase 120 `AuthAuditHook` sanitisation.
let internal subjectFields (subject: Subject) : string * string option =
    match subject with
    | AnonymousSession _ -> "anonymous", None
    | AuthenticatedUser uid -> "user", Some uid
    | TeamMember(uid, _) -> "team", Some uid
    | ClaimBearer claim -> "claim", Some claim.TokenId

/// SDK-default `IHostActionAuditHook`. Writes a `HostActionDispatched` row
/// through `IAuditLog` under the action's own scope. `now` is injectable for
/// tests; production uses the wall clock.
type HostActionAuditHook(auditLog: IAuditLog, logger: ILogger, ?now: unit -> DateTimeOffset) =

    let clock = defaultArg now (fun () -> DateTimeOffset.UtcNow)

    interface IHostActionAuditHook with
        member _.RecordAction
            (principal: Subject)
            (action: ActionDescriptor)
            (decision: AuthorizationDecision)
            : Async<unit> =
            async {
                try
                    let kind, subjectId = subjectFields principal
                    let scopeId = action.Scope |> Option.defaultValue "_platform"

                    let allowed, reason =
                        match decision with
                        | AuthorizationDecision.Allow -> true, "allowed"
                        | AuthorizationDecision.Deny r -> false, r

                    do!
                        auditLog.Record(
                            scopeId,
                            HostActionDispatched {
                                SubjectKind = kind
                                SubjectId = subjectId
                                ActionKind = action.Kind
                                ActionTarget = action.Target
                                ScopeId = action.Scope
                                Allowed = allowed
                                Reason = reason
                                OccurredAt = clock ()
                            }
                        )
                with ex ->
                    // Best-effort — an audit-write failure must not affect the
                    // action the caller is about to perform.
                    logger.Warn(
                        sprintf
                            "[HostActionAuditHook] action-audit write failed kind=%s target=%s: %s"
                            action.Kind
                            action.Target
                            ex.Message
                    )
            }

/// No-op hook — the absent-sink default (GP 13). `authorizeAndAudit` with this
/// hook is byte-for-byte the bare `authorizer.Authorize`.
let disabled: IHostActionAuditHook =
    { new IHostActionAuditHook with
        member _.RecordAction _ _ _ = async { return () }
    }

/// Authorize-then-audit as ONE path: authorize the action through the Phase
/// 113 authorizer, record the decision (allow OR deny) through `hook`, and
/// return the decision unchanged. Wire this at every host-action call site so
/// a gated hosted action is audited by construction (GP 6). Composing
/// `disabled` as the hook makes this an audit-free passthrough (GP 13).
let authorizeAndAudit
    (authorizer: IActionAuthorizer)
    (hook: IHostActionAuditHook)
    (principal: Subject)
    (action: ActionDescriptor)
    (ctx: AccessContext)
    : Async<AuthorizationDecision> =
    async {
        let! decision = authorizer.Authorize action ctx
        do! hook.RecordAction principal action decision
        return decision
    }