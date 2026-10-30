// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Scope as a choke point ────────────────────────────────────────────
//
// `StorageScope` is the record the platform's scope resolution produces
// once per request. It lived in StorageScope.fs beside the resolver's
// request and error types; it moved here, ahead of VectorKnowledgeTypes.fs
// in the compile order, so the disclosure gate's contract can take the
// `ResolvedScope` below. Its shape and namespace are unchanged.
//
// `ResolvedScope` is the value the fact tier keys on. Its
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
///
/// A projection of `ScopeKey`: `StorageScope.ofKey` renders
/// `ScopeId` and `Container` from a key, so a scope minted through it can
/// never spell its container a second way. The record stays constructible
/// for one minor as the wire of `ScopeResolutionRequest`'s resolvers
/// (`IStorageScopeResolver.Resolve` returns it) and of the scopes already
/// carried at rest; new code mints a `ScopeKey` and projects it.
type StorageScope = {
    /// The shard key every scoped store partitions by: a user id, a team
    /// id, or an anonymous session id, depending on the platform mode
    /// (`ScopeKey.scopeId`).
    ScopeId: string
    /// The storage container the scope's data lives in — `user-{id}`,
    /// `team-{id}`, `session-{id}` (`ScopeKey.container`).
    Container: string
    /// Whether the scope's data persists beyond the session. `false` for
    /// anonymous and ephemeral-authenticated surfaces.
    Persist: bool
}

/// The projection from `ScopeKey`.
[<RequireQualifiedAccess>]
module StorageScope =

    /// The storage scope a key renders to: `ScopeId` is `ScopeKey.scopeId`,
    /// `Container` is `ScopeKey.container`, and `Persist` is the caller's —
    /// a key does not know the deployment's `Surfaces` (`ScopeKey.persistence`
    /// does).
    let ofKey (persist: bool) (key: ScopeKey) : StorageScope = {
        ScopeId = ScopeKey.scopeId key
        Container = ScopeKey.container key
        Persist = persist
    }

    /// The key a storage scope was rendered from: `Some key`
    /// when its `Container` reads back through `ScopeKey.tryParseContainer`
    /// AND its `ScopeId` is that key's `ScopeKey.scopeId`, so
    /// `tryKey (ofKey p key) = Some key` for every key a well-formed id
    /// builds. `None` for a shape the grammar does not render: a claim
    /// scope (its `ScopeId` is its container), a container outside the
    /// grammar, a padded or empty id, or a `ScopeId` that disagrees with
    /// its container. A pure function of the record, so it can never
    /// disagree with the `ScopeId` and `Container` a store keys on.
    let tryKey (scope: StorageScope) : ScopeKey option =
        ScopeKey.tryParseContainer scope.Container
        |> Option.filter (fun key -> ScopeKey.scopeId key = scope.ScopeId)

    /// The team a storage scope belongs to: the id of a `ScopeKey.Team`
    /// key (`tryKey`), `None` for every other scope. What a handler asks
    /// of the request's scope before reading team-scoped configuration.
    let tryTeamId (scope: StorageScope) : string option =
        match tryKey scope with
        | Some(ScopeKey.Team teamId) -> Some teamId
        | _ -> None

/// A storage scope the platform's scope resolution minted for the current
/// request — or the explicit anonymous scope when it minted
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

    /// The `ScopeKey` the resolved scope renders from —
    /// `StorageScope.tryKey` of `Storage`. Present for every scope minted
    /// in the grammar (team, user, session, the platform shard); absent
    /// for the anonymous scope, for a claim scope, and for a resolver- or
    /// carrier-supplied shape no key renders. DERIVED from `Storage`
    /// rather than carried beside it: the mints receive a `StorageScope`
    /// (`IStorageScopeResolver.Resolve`'s output, a carried token at rest),
    /// so a carried key would be absent exactly where the container is in
    /// the grammar but the resolver did not say so, and could disagree
    /// with the `Storage` every store keys on. Derived, it cannot.
    member this.Key: ScopeKey option =
        match this with
        | Resolved scope -> StorageScope.tryKey scope
        | AnonymousScope -> None

/// Constructors and accessors for `ResolvedScope`. The only constructor a
/// consumer can reach is `anonymous`; `ofStorageScope` is internal and is
/// called by the scope-resolution middleware alone, and `ofCarried`
/// is internal and is called by the job scheduler alone.
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

    /// Re-mint a scope the platform CARRIED rather than resolved (Phase
    /// 818) — the job scheduler's run-time mint. A job scheduled through
    /// `IJobScheduler.Schedule(scope: ResolvedScope, …)` persists the
    /// resolver's `StorageScope` with its definition; when the job runs,
    /// the scheduler hands the same scope back on `JobContext.Scope`
    /// through this function. A second mint rather than a second use of
    /// `ofStorageScope` so the two provenances stay separately auditable:
    /// this one is called only where a scope that WAS resolved is being
    /// carried across a hop, never where a string is being promoted.
    /// Internal for the same reason as `ofStorageScope`: `InternalsVisibleTo`
    /// reaches the platform's server tier and nothing a caller controls.
    let internal ofCarried (scope: StorageScope) : ResolvedScope = Resolved scope

    /// The shard key — see `ResolvedScope.ScopeId`.
    let scopeId (scope: ResolvedScope) : string = scope.ScopeId
// ─── The two-scope form ─────────────────────────────────────────────
//
// Every read and write in the fact tier holds ONE scope. The single place
// that holds two is the team-to-team publication seam: a run written into a
// target team's table on the strength of a grant the source team published
// under. `ResolvedScopePair` is that pair, and it is built from two scopes
// that were already minted — there is no constructor taking a string, so a
// pair can name no scope the platform did not resolve (the choke point's rule
// carried one step further). The anonymous scope and a pair naming one scope
// twice are refused: a publication moves rows between two resolved teams.
//
// The pair is a CARRIER, not a permission. It says nothing about whether the
// write is allowed — that is the grant's business — and the source-side
// guard pack pins that the fact tier's only holder of a pair is the one
// sanctioned seam.

/// Two minted scopes held together for the one sanctioned cross-scope write:
/// the SOURCE a publication was read in and the TARGET its run
/// is written into. Built only from two `ResolvedScope` values, so neither
/// side can be a scope the platform did not resolve.
type ResolvedScopePair = private {
    SourceScope: ResolvedScope
    TargetScope: ResolvedScope
} with

    /// The scope the publication was read in.
    member this.Source = this.SourceScope

    /// The scope the publication's run is written into.
    member this.Target = this.TargetScope

/// Construction for `ResolvedScopePair`.
[<RequireQualifiedAccess>]
module ResolvedScopePair =

    /// Pair two minted scopes: `source` first, `target` second. Refuses the
    /// anonymous scope on either side and a pair naming one scope twice,
    /// each with a sentence naming why.
    let ofMinted (source: ResolvedScope) (target: ResolvedScope) : Result<ResolvedScopePair, string> =
        if source.IsAnonymous || target.IsAnonymous then
            Error "a cross-scope pair needs two resolved scopes; the anonymous scope publishes and receives nothing"
        elif source.ScopeId = target.ScopeId then
            Error(sprintf "a cross-scope pair names two scopes, but both sides are '%s'" source.ScopeId)
        else
            Ok {
                SourceScope = source
                TargetScope = target
            }