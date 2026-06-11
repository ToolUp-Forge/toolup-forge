// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.PermissionStore

// ─── Phase 113 — IPermissionStore/tier-backed IActionAuthorizer ───────
//
// The shipped default for the host-neutral action-authorization seam.
// Resolves an `ActionDescriptor` against a declarative `ActionPolicy`
// whose requirements are backed by ToolUp's permission + tier
// substrate:
//
//   - `ActionRequirement.Permission(module, perm)` — for a `TeamMember`
//     the member's effective permissions are read from
//     `IPermissionStore.GetEffectivePermissions` per call (stateless,
//     rule 4 — a rotated grant takes effect on the next action); for
//     every other subject kind the per-request resolved
//     `AccessContext.ModulePermissions` is consulted.
//   - `ActionRequirement.PremiumTier` — resolved via `IUserClaims`
//     (the platform tier provider). No provider registered → `Deny`
//     (fail closed), not silently allowed.
//
// **Default-deny semantics — deliberately stricter than
// `AccessContext.hasPermission`.** The RBAC helpers treat an EMPTY
// permission map as unrestricted (the opt-in pre-RBAC behaviour). This
// authorizer does not: an action is allowed only when a policy rule
// covers it AND the rule's requirement is positively satisfied. A team
// that has never configured permissions therefore denies
// permission-gated actions — which is the point of routing an external
// UI runtime's open-ended action surface through a default-deny gate.
//
// Never throws: a storage blip, a malformed descriptor, or a missing
// provider is a `Deny of reason` (fail closed), mirroring the Phase 46
// `IClientToolAuthorizer` contract.

module PermissionStoreActionAuthorizer =

    let private evaluatePermission
        (store: IPermissionStore)
        (ctx: AccessContext)
        (moduleName: string)
        (required: ModulePermission)
        : Async<AuthorizationDecision> =
        async {
            let! effective =
                match ctx.Subject with
                | TeamMember(userId, teamId) -> store.GetEffectivePermissions(userId, teamId)
                | _ -> async { return ctx.ModulePermissions }

            let granted =
                effective
                |> Map.tryFind moduleName
                |> Option.map (List.exists (fun g -> ModulePermission.implies g required))
                |> Option.defaultValue false

            if granted then
                return AuthorizationDecision.Allow
            else
                return
                    AuthorizationDecision.Deny
                        $"no permission grant for module '%s{moduleName}' covers %A{required} (default-deny)"
        }

    let private evaluateTier (claims: IUserClaims option) (ctx: AccessContext) : Async<AuthorizationDecision> = async {
        match claims with
        | None ->
            return AuthorizationDecision.Deny "action requires the premium tier but no tier provider is registered"
        | Some c ->
            let! status = c.GetPremiumStatus ctx.UserId

            match status with
            | Premium _ -> return AuthorizationDecision.Allow
            | NotPremium -> return AuthorizationDecision.Deny "action requires the premium tier"
    }

    let rec private evaluate
        (store: IPermissionStore)
        (claims: IUserClaims option)
        (ctx: AccessContext)
        (requirement: ActionRequirement)
        : Async<AuthorizationDecision> =
        async {
            match requirement with
            | ActionRequirement.Unrestricted -> return AuthorizationDecision.Allow
            | ActionRequirement.PremiumTier -> return! evaluateTier claims ctx
            | ActionRequirement.Permission(m, p) -> return! evaluatePermission store ctx m p
            | ActionRequirement.All reqs ->
                let rec go remaining = async {
                    match remaining with
                    | [] -> return AuthorizationDecision.Allow
                    | r :: rest ->
                        match! evaluate store claims ctx r with
                        | AuthorizationDecision.Allow -> return! go rest
                        | AuthorizationDecision.Deny reason -> return AuthorizationDecision.Deny reason
                }

                return! go reqs
        }

    /// The principal's own scope id — the active team for a
    /// `TeamMember`, the stable subject id otherwise. A descriptor
    /// pinned to a different scope is denied structurally (GP 4).
    let private ownScopeId (ctx: AccessContext) : string =
        ctx.TeamId |> Option.defaultValue ctx.UserId

    /// Build the shipped `IActionAuthorizer` over a declarative policy,
    /// the permission store, and (optionally) the tier provider. Pass
    /// `claims = None` when the deployment has no tier model — a
    /// `PremiumTier` requirement then denies rather than silently
    /// allowing.
    let create (policy: ActionPolicy) (store: IPermissionStore) (claims: IUserClaims option) : IActionAuthorizer =
        { new IActionAuthorizer with
            member _.Authorize action ctx = async {
                try
                    match action.Scope with
                    | Some s when s <> ownScopeId ctx ->
                        return
                            AuthorizationDecision.Deny
                                $"action targets scope '%s{s}' outside the principal's own scope (default-deny)"
                    | _ ->
                        match ActionPolicy.tryFindRule action policy with
                        | None ->
                            return
                                AuthorizationDecision.Deny
                                    $"no policy rule covers action kind '%s{action.Kind}' target '%s{action.Target}' (default-deny)"
                        | Some rule -> return! evaluate store claims ctx rule.Requirement
                with ex ->
                    // Fail closed — a storage blip never silently grants.
                    return AuthorizationDecision.Deny $"authorization check failed: %s{ex.Message} (fail-closed)"
            }
        }