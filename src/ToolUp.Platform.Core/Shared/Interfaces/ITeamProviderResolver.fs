// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── ITeamProviderResolver (Phase 44a) ───────────────────────────
//
// `IProviderProfile` answers "what is configured AT this scope". This
// seam answers the question a request actually asks: "what applies to
// THIS subject", which is a walk down the precedence chain
// `ProviderScopeChain.forSubject` builds — the caller's own profile
// first, the team's second, the deployment's platform default last.
//
// It is a SEPARATE seam rather than two more members on
// `IProviderProfile` for the reason `ISecretStoreAtRestPosture` is
// separate from `ISecretStore`: every shipped and external
// `IProviderProfile` implementation keeps compiling (GP 11), and the
// two surfaces answer different questions — one is a per-scope
// persistence store, this is a per-subject policy resolution over
// several of them plus a role gate. An implementation of this seam
// composes an `IProviderProfile`; it does not replace one.
//
// **Why the write gate lives here.** `CanWrite` is the same
// Owner/Admin question `ProviderProfileApiHandler` already answers for
// the caller's own `configScope`, generalised to an explicitly-named
// owner — because once a subject has TWO reachable scopes, "may I
// write" stops being a property of the caller and becomes a property
// of the (caller, target) pair. Keeping it beside `Resolve` is what
// lets one contract pack state the isolation claim in both
// directions: what a subject may read, and what it may write.
//
// Six-rule portability audit (the portability contract):
//   1. Identity by value      — every method keys by `Subject` (value
//                               DU) + strings; no live handle, no
//                               `HttpContext`, no DI container.
//   2. Async at every boundary — every method returns `Async<_>`; the
//                               role lookup behind `CanWrite` is I/O.
//   3. Retry / supervision as data — none on this surface; it reads
//                               and decides. Callers retry by
//                               re-issuing the `Async`.
//   4. Stateless between calls — no in-memory continuity contract; a
//                               distributed implementation may serve
//                               successive calls from different nodes.
//   5. No cross-shard ordering — each subject's resolution is
//                               independent; no ordering claim across
//                               subjects or scopes.
//   6. Precision at the lower bound — no timing primitive on this
//                               surface; N/A by construction.

/// Per-subject resolution of the effective provider configuration,
/// over the `ProviderScopeChain` precedence the Phase 44a team-scope
/// binding defines.
type ITeamProviderResolver =
    /// The entry that applies to `subject` for `(surface, context)`.
    /// Never fails: a subject with no reachable scope, no saved
    /// profile, or no routing rule for the surface resolves to
    /// `ProviderResolution.platformDefault`, which is the consumer's
    /// signal to apply whatever the deployment wired (GP 13 — the
    /// platform default is unchanged when no team profile is set).
    ///
    /// The returned `OwningScope` is load-bearing, not diagnostic: it names
    /// where the entry's `SecretKeyName` must be read, and for a
    /// team-owned entry resolved on behalf of a member that is the
    /// TEAM's scope, not the member's.
    abstract Resolve: subject: Subject * surface: string * context: string option -> Async<ProviderResolution>

    /// The surface model override in force for `subject`, by the same
    /// precedence. Read separately from `Resolve` because the two are
    /// independent knobs: a member may override the model for a
    /// surface whose provider they inherit from the team.
    abstract ResolveModelOverride: subject: Subject * surface: string -> Async<string option>

    /// The scope `subject` may write `owner`'s provider configuration
    /// at, or a human-readable refusal.
    ///
    /// `Ok scope` is the ONLY route to a write target — a caller that
    /// derives a scope itself and writes to `IProviderProfile`
    /// directly has bypassed the gate, which is why the successful
    /// answer is the scope rather than a boolean. Refusals: a subject
    /// that cannot reach the owner at all, a team member without
    /// `TeamRoles.canWriteTeamConfig` on the team rung, and
    /// `ProviderProfileOwner.PlatformDefault`, which is deployment
    /// configuration rather than a profile this model edits.
    abstract CanWrite: subject: Subject * owner: ProviderProfileOwner -> Async<Result<ProviderScope, string>>