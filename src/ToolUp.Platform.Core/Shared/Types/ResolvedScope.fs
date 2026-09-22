// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 797 — scope as a choke point ────────────────────────────────
//
// `StorageScope` is the record the platform's scope resolution produces
// once per request. It lived in StorageScope.fs beside the resolver's
// request and error types; it moved here, ahead of VectorKnowledgeTypes.fs
// in the compile order, so the disclosure gate's contract can take the
// `ResolvedScope` below. Its shape and namespace are unchanged.
//
// `ResolvedScope` is the value the fact tier keys on since Phase 797. Its
// representation is PRIVATE: nothing outside this file can construct a
// `Resolved` case, and the one minting function is `internal` — reachable
// from `ToolUp.Platform.Server` (where the scope-resolution middleware runs)
// and from nowhere a caller could hand a string of its own choosing. A
// store or gate member that takes a `ResolvedScope` therefore cannot be
// asked about a scope the request did not resolve to; the type is the
// argument, not a runtime check. The anonymous case is public and is a
// scope like any other — its own shard, never a wildcard.

/// Resolved storage scope for a request — determines where data is stored
/// and whether it persists beyond the session.
type StorageScope = {
    /// The shard key every scoped store partitions by: a user id, a team
    /// id, or an anonymous session id, depending on the platform mode.
    ScopeId: string // userId, teamId, or sessionId
    /// The storage container the scope's data lives in — "user-abc",
    /// "team-xyz", "session-abc".
    Container: string // "user-abc", "team-xyz", "session-abc"
    /// Whether the scope's data persists beyond the session. `false` for
    /// anonymous and ephemeral-authenticated surfaces.
    Persist: bool // false for anonymous + ephemeral-authenticated surfaces
}

/// A storage scope the platform's scope resolution minted for the current
/// request (Phase 797) — or the explicit anonymous scope when it minted
/// none. The representation is private and the mint is internal to the
/// platform's server tier: a caller cannot construct one from a string, so
/// a fact-store or disclosure-gate member that takes this type is
/// structurally unable to read a scope the principal did not resolve to.
///
/// Compare `StorageScope`, which any code can build. That record remains
/// the resolver's OUTPUT and the general-purpose scope value the rest of
/// the platform carries in `HttpContext.Items`; this type is the fact
/// tier's PROOF that a scope came from the resolver.
type ResolvedScope =
    private
    | Resolved of StorageScope
    | AnonymousScope

    /// The shard key the scope resolves to — the resolved scope's
    /// `StorageScope.ScopeId`, or `"anonymous"` for the anonymous scope.
    /// This is the string the string-keyed store overloads take, and a
    /// store keyed on it treats the anonymous value as one more shard.
    member this.ScopeId =
        match this with
        | Resolved scope -> scope.ScopeId
        | AnonymousScope -> "anonymous"

    /// `true` for the explicit anonymous scope — the platform resolved no
    /// scope for the request. Its shard holds only what was asserted
    /// anonymously; it never widens to other scopes.
    member this.IsAnonymous =
        match this with
        | Resolved _ -> false
        | AnonymousScope -> true

    /// The resolver's `StorageScope`, when the request resolved to one.
    /// `None` for the anonymous scope, which has no container and does
    /// not persist.
    member this.Storage =
        match this with
        | Resolved scope -> Some scope
        | AnonymousScope -> None

/// Constructors and accessors for `ResolvedScope`. The only constructor a
/// consumer can reach is `anonymous`; `ofStorageScope` is internal and is
/// called by the scope-resolution middleware alone.
[<RequireQualifiedAccess>]
module ResolvedScope =

    /// The scope id the anonymous scope keys on.
    [<Literal>]
    let AnonymousScopeId = "anonymous"

    /// The explicit anonymous scope — a request the platform resolved no
    /// scope for. A store treats it as its own shard, never as a wildcard.
    let anonymous: ResolvedScope = AnonymousScope

    /// Mint a `ResolvedScope` from the scope the resolver produced.
    /// Internal on purpose: the scope-resolution middleware is the one
    /// caller, and `InternalsVisibleTo` reaches no further than the
    /// platform's own server tier.
    let internal ofStorageScope (scope: StorageScope) : ResolvedScope = Resolved scope

    /// The shard key — see `ResolvedScope.ScopeId`.
    let scopeId (scope: ResolvedScope) : string = scope.ScopeId