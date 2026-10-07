// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open ToolUp.Platform

// ─── Phase 86 — page audience authorization ──────────────────────────
//
// The pure decision behind gated SSR. `PublicPageHandler` resolves the
// per-request `AccessContext` (the same one module routes are gated by),
// then calls `AudienceGate.evaluate` with the resolved page's
// `PageAudience`. The result drives 200 / 401 / 403.
//
// No new auth machinery (GP — "reuse the surface that gates module
// routes"): role checks read the same `AccessContext.ModulePermissions`
// map the SDK's per-module RBAC reads (plus, since Phase 987, the roles
// the identity provider asserted, `AccessContext.TokenRoles`), and
// client-portal scoping goes
// through the principal's own resolved scope ids — a principal can only
// satisfy a `ClientGated` page whose relationship is structurally its own
// (GP 4). Platform admins bypass both gates.

/// Outcome of the per-request audience authorization check.
[<RequireQualifiedAccess>]
type AudienceDecision =
    /// Serve the page.
    | Allow
    /// No resolved principal for a page that requires one → HTTP 401.
    | RequireAuthentication
    /// A resolved principal that fails the role / relationship gate →
    /// HTTP 403.
    | Forbidden

module AudienceGate =

    /// The scope identifiers a principal structurally owns — its user id,
    /// active team id, and resolved config-scope id. Used for
    /// `ClientGated` matching: a principal may only view a client-portal
    /// page whose `relationship` is one of its own scopes (GP 4 —
    /// structural, not a caller-supplied claim).
    let private principalScopeIds (ctx: AccessContext) : Set<string> =
        [ ctx.UserId ]
        @ (ctx.TeamId |> Option.toList)
        @ (AccessContext.configScope ctx |> Option.map _.ScopeId |> Option.toList)
        |> Set.ofList

    /// Phase 989 — whether the principal HOLDS a named role: the role is a
    /// key of its `ModulePermissions` with at least one permission.
    ///
    /// Deliberately not `AccessContext.canAccessModule`, which reads an
    /// empty permission map as "every module is accessible" (GP 11). That
    /// reading is right for module access, where a deployment that has
    /// configured no RBAC must keep working for its users. It is wrong for
    /// a page that names the roles allowed to read it: there the author has
    /// restricted the page, and a principal with no configured permissions
    /// has been granted none of those roles. An empty map therefore holds
    /// no role.
    ///
    /// Phase 987 — OR the identity provider asserted the role:
    /// `AccessContext.TokenRoles` (the token's role and group claims, as
    /// the deployment's `ClaimMapping` maps them). So a page can be
    /// restricted to a group in the organisation's directory, and read by
    /// its members without their being app users or team members. The
    /// comparison is exact (ordinal, case-sensitive), as the IdP issues it.
    let private holdsRole (ctx: AccessContext) (role: string) : bool =
        (ctx.ModulePermissions
         |> Map.tryFind role
         |> Option.exists (fun perms -> not (List.isEmpty perms)))
        || List.contains role ctx.TokenRoles

    /// Phase 996 — the `Publication` audience decision (see `evaluate`).
    let private publication (ctx: AccessContext) (audience: PublicationAudience) : AudienceDecision =
        if not (AccessContext.isAuthenticated ctx) then
            AudienceDecision.RequireAuthentication
        elif System.String.IsNullOrWhiteSpace audience.PublishingScope then
            AudienceDecision.Forbidden
        elif audience.Readers |> List.exists (fun r -> List.contains r ctx.TokenRoles) then
            AudienceDecision.Allow
        else
            AudienceDecision.Forbidden

    /// Pure authorization decision for a page audience against a resolved
    /// `AccessContext`.
    ///
    /// - `Public` — always `Allow` (byte-for-byte pre-86).
    /// - `Authenticated` — `Allow` for any non-anonymous principal,
    ///   else `RequireAuthentication`.
    /// - `ScopeGated roles` — anonymous → `RequireAuthentication`;
    ///   platform admin → `Allow`; an empty role list → `Allow` (gated to
    ///   "any authenticated"); otherwise `Allow` iff the principal holds
    ///   one of the roles — a `ModulePermissions` entry of that name with
    ///   at least one permission, or a provider-asserted `TokenRoles`
    ///   entry (Phase 987) — else `Forbidden`. An empty permission
    ///   map holds no role (Phase 989; unlike module access, where it
    ///   means unrestricted per GP 11).
    /// - `ClientGated relationship` — anonymous → `RequireAuthentication`;
    ///   platform admin → `Allow`; otherwise `Allow` iff `relationship`
    ///   is one of the principal's own scope ids, else `Forbidden`.
    ///
    /// Phase 996:
    /// - `Publication a` — anonymous → `RequireAuthentication`; `Allow` iff
    ///   the principal holds one of `a.Readers` as a provider-asserted
    ///   `TokenRoles` entry and `a.PublishingScope` is not blank, else
    ///   `Forbidden`. No bypass: neither module permissions, team
    ///   membership nor the platform-admin role admits — the readers are an
    ///   explicit audience, and an app member outside it is refused.
    /// - A publication reader (`AccessContext.isPublicationReader`) is
    ///   confined: `Public` and the `Publication` pages above only; every
    ///   other audience is `Forbidden` to it.
    let evaluate (ctx: AccessContext) (audience: PageAudience) : AudienceDecision =
        match audience with
        | PageAudience.Publication publicationAudience -> publication ctx publicationAudience
        | PageAudience.Public -> AudienceDecision.Allow
        | _ when AccessContext.isPublicationReader ctx -> AudienceDecision.Forbidden
        | PageAudience.Authenticated ->
            if AccessContext.isAuthenticated ctx then
                AudienceDecision.Allow
            else
                AudienceDecision.RequireAuthentication
        | PageAudience.ScopeGated roles ->
            if not (AccessContext.isAuthenticated ctx) then
                AudienceDecision.RequireAuthentication
            elif AccessContext.canModifyPlatformConfig ctx then
                AudienceDecision.Allow
            elif List.isEmpty roles then
                AudienceDecision.Allow
            elif roles |> List.exists (holdsRole ctx) then
                AudienceDecision.Allow
            else
                AudienceDecision.Forbidden
        | PageAudience.ClientGated relationship ->
            if not (AccessContext.isAuthenticated ctx) then
                AudienceDecision.RequireAuthentication
            elif AccessContext.canModifyPlatformConfig ctx then
                AudienceDecision.Allow
            elif Set.contains relationship (principalScopeIds ctx) then
                AudienceDecision.Allow
            else
                AudienceDecision.Forbidden