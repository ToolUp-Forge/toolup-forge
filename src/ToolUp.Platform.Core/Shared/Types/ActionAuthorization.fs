// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 113 — host-neutral default-deny action authorization ──────
//
// A host embedding an external UI runtime (a typed-tree renderer, a
// server-driven session, any surface that turns user interaction into
// typed actions) needs to ask ONE question before executing an action:
// "is this principal allowed to perform this action in this scope?".
// Phase 46 shipped the right-shaped precedent for AI-drivable client
// tools (`IClientToolAuthorizer` in `ToolUp.AI.Core` — sync, hot-loop,
// tool-name-keyed). `IActionAuthorizer` is the same seam one tier up:
// host-neutral, keyed on an identity-by-value `ActionDescriptor`,
// default-deny when no rule matches, and async because the shipped
// default resolves against `IPermissionStore` (a storage read).
//
// The seam lives in `ToolUp.Platform.Core` (not `ToolUp.AI.Core`)
// because gating actions is a platform concern — the AI-tool path is
// one caller among several (client tree-hosting runtimes, live-session
// dispatch, custom hosts). `IClientToolAuthorizer` is untouched; a
// deployment can route its AI-tool decisions through this seam via a
// thin adapter without disturbing the Phase 46 contract.
//
// **Six portability rules** (GP 12):
//   1. Identity by value — `ActionDescriptor` is strings + option;
//      `AccessContext` is a record of values. ✓
//   2. Async at every boundary — `Authorize` returns `Async<_>`. ✓
//   3. Retry/supervision as data — N/A (read-only decision; a
//      distributed impl wraps its own policy). ✓
//   4. Stateless — all state arrives via parameters; impls must not
//      hold per-call state between invocations. ✓
//   5. No cross-call ordering promises — each `Authorize` is a point
//      decision. ✓
//   6. Precision — N/A (no timing primitive). ✓
//
// Implementations MUST NOT throw — a malformed descriptor or an
// unavailable backing store is a `Deny` (fail closed), never an
// exception.

/// Identity-by-value description of an action a host wants to perform
/// on behalf of a principal. `Kind` partitions the action space
/// (`"dispatch"`, `"call"`, `"navigate"`, `"notify"`, `"ai-tool"`, …
/// — host-defined vocabulary); `Target` identifies the specific action
/// within the kind (a message case name, a Remoting method, a route);
/// `Scope` optionally pins the action to a scope container so a
/// cross-scope dispatch is structurally deniable.
type ActionDescriptor = {
    Kind: string
    Target: string
    /// Scope id the action executes against, when the host knows it.
    /// The shipped default denies when this is `Some` and differs from
    /// the principal's own resolved scope (GP 4).
    Scope: string option
}

/// Outcome of an action-authorization check. Mirrors the Phase 46
/// `ClientToolAuthDecision` shape; qualified access avoids case
/// collisions with the other decision DUs in this namespace.
[<RequireQualifiedAccess>]
type AuthorizationDecision =
    | Allow
    | Deny of reason: string

/// Host-neutral, default-deny action-authorization seam. Hosts resolve
/// this from DI (or take it as a compose-time parameter) and consult it
/// immediately before executing a typed action. Absent registration is
/// NOT "allow everything" — hosts fall back to
/// `ActionAuthorizer.denyAll` so no action is silently allowed
/// (the inverse of the Phase 46 AI-tool default, deliberately: an
/// external UI runtime's action surface is open-ended, so the safe
/// absent-seam behaviour is closed).
type IActionAuthorizer =
    abstract Authorize: action: ActionDescriptor -> ctx: AccessContext -> Async<AuthorizationDecision>

// ─── Policy as data ───────────────────────────────────────────────────

/// What a principal must hold for a rule to grant its action. Expressed
/// as data (GP 12 rule 3 spirit — no callback-shaped policy) so a
/// deployment's gating is declarative and auditable.
[<RequireQualifiedAccess>]
type ActionRequirement =
    /// Grant when the principal holds `permission` (or one implying it)
    /// on `moduleName` per the permission model.
    | Permission of moduleName: string * permission: ModulePermission
    /// Grant only when the principal holds the premium tier (resolved
    /// by the server-side tier provider). Below the tier → `Deny`.
    | PremiumTier
    /// Grant when every nested requirement grants.
    | All of ActionRequirement list
    /// Grant unconditionally. Dev-only — pairs with
    /// `ActionAuthorizer.allowAll`; never ship in a production policy.
    | Unrestricted

/// One policy rule: which actions it covers (`Kind` / `Target`, where
/// `"*"` matches anything and a trailing `*` is a prefix match) and
/// what the principal must hold. First matching rule wins
/// (registration order); no matching rule → deny (default-deny).
type ActionRule = {
    Kind: string
    Target: string
    Requirement: ActionRequirement
}

/// A deployment's action policy — an ordered rule list. Empty policy =
/// every action denied (the default-deny floor).
type ActionPolicy = { Rules: ActionRule list }

module ActionPolicy =
    /// The default-deny floor: no rules, every action denied.
    let empty: ActionPolicy = { Rules = [] }

    /// True when `pattern` covers `value` (`"*"` wildcard, trailing-`*`
    /// prefix, otherwise exact ordinal match).
    let matches (pattern: string) (value: string) : bool =
        if pattern = "*" then
            true
        elif pattern.EndsWith "*" then
            value.StartsWith(pattern.Substring(0, pattern.Length - 1))
        else
            pattern = value

    /// First rule covering the descriptor, in registration order.
    let tryFindRule (action: ActionDescriptor) (policy: ActionPolicy) : ActionRule option =
        policy.Rules
        |> List.tryFind (fun r -> matches r.Kind action.Kind && matches r.Target action.Target)

module ActionAuthorizer =
    /// The absent-seam fallback: every action denied. Hosts use this
    /// when no `IActionAuthorizer` is registered, so "forgot to wire
    /// the authorizer" fails closed rather than open.
    let denyAll: IActionAuthorizer =
        { new IActionAuthorizer with
            member _.Authorize _ _ = async {
                return AuthorizationDecision.Deny "no action authorizer registered (default-deny)"
            }
        }

    /// DEV-ONLY: every action allowed. For local iteration before a
    /// policy exists; never register in a production composition.
    let allowAll: IActionAuthorizer =
        { new IActionAuthorizer with
            member _.Authorize _ _ = async { return AuthorizationDecision.Allow }
        }