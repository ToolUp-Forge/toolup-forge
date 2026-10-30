// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── A scope is a value of a closed type ───────────────────────────────
//
// Every store partitions by scope, and until this phase the grammar that
// relates a scope to its storage container — `team-{id}`, `user-{id}`,
// `session-{id}`, the reserved `_platform` shard — lived nowhere. It was
// MINTED by string interpolation at each resolver and parsed BACK by
// `StartsWith` at each reader, so two spellings of one scope could be
// produced and a reader could disagree with a writer about which scope a
// container named.
//
// `ScopeKey` is that grammar as a type. `ScopeKey.container` is the one
// function that renders a container, `ScopeKey.scopeId` the one that
// renders a bare store id, and `ScopeKey.tryParseContainer` the one
// legacy parser — kept for the names already at rest (an encrypted
// blob's key derivation reads its scope back from the container it was
// written under). Isolation lives at the layer that constructs keys: the
// `IBlobStorage` implementers keep their string `container` parameter
// (the portability boundary, recorded in `IBlobStorage.fs`).
//
// This file owns the grammar's string literals. The `VerifyScopeLiterals`
// gate in `Build.fs` refuses a new `"team-"` / `"user-"` / `"session-"` /
// `"_platform"` literal anywhere else under `src/`.
//
// Fable-safe: BCL string operations only.

/// The reserved shards that belong to the platform rather than to any
/// principal. Closed: a sweep or a platform store names its shard by case,
/// and a request cannot spell one into a team or user id (the store-id
/// sanitiser refuses a parsed `ScopeKey.Platform` by type).
///
/// One case today: every reserved shard the platform writes is the
/// `_platform` container (the coherence sweep's health scope is that same
/// container, and `_facts` is an event SOURCE module, not a scope). A new
/// case lands only with a container string of its own, so `container`
/// stays injective and `tryParseContainer` stays its inverse.
[<RequireQualifiedAccess>]
type PlatformShard =
    /// The reserved `_platform` container: platform-level configuration,
    /// secrets, health probes and platform audit rows.
    | Platform

/// The scope a store partitions by, as a closed value. Its
/// two renderings are `ScopeKey.container` (the storage container) and
/// `ScopeKey.scopeId` (the bare store id); nothing else spells a scope.
[<RequireQualifiedAccess>]
type ScopeKey =
    /// A reserved platform shard.
    | Platform of PlatformShard
    /// A team's scope, by its bare team id.
    | Team of teamId: string
    /// An individual user's scope, by its bare user id.
    | User of userId: string
    /// An anonymous session's scope, by its bare session id.
    | Session of sessionId: string
    /// A share-token claim's scope. The claim's `ScopeId` is both its
    /// store id and its container (`BlobShareTokenStore` semantics), so
    /// both renderings return it verbatim.
    | Claim of claimScopeId: string

/// How a scope persists under a deployment's declared `Surfaces`
/// (the surface-persistence rule). `Defaulted` is `true` when no `SurfaceProfile`
/// matched the scope's kind and the fail-closed default (ephemeral) fired —
/// the caller's signal to emit its one-time diagnostic.
type ScopePersistence = { Persist: bool; Defaulted: bool }

/// The renderers, the one legacy parser and the persistence rule.
[<RequireQualifiedAccess>]
module ScopeKey =

    /// The reserved platform container, as a compile-time constant:
    /// `ScopeKey.container ScopeKey.platform`, spelled once. A site that
    /// names the shard as a string — a store's container, a secret scope,
    /// an audit stream, another `[<Literal>]`, a pattern — uses this
    /// (the tree's hand-written `"_platform"` literals were swept onto it); a
    /// site holding a `ScopeKey` renders it.
    [<Literal>]
    let PlatformContainer = "_platform"

    [<Literal>]
    let private TeamPrefix = "team-"

    [<Literal>]
    let private UserPrefix = "user-"

    [<Literal>]
    let private SessionPrefix = "session-"

    /// The `_platform` shard's key.
    let platform: ScopeKey = ScopeKey.Platform PlatformShard.Platform

    let private shardContainer (shard: PlatformShard) : string =
        match shard with
        | PlatformShard.Platform -> PlatformContainer

    /// The storage container the scope's data lives in: `team-{id}`,
    /// `user-{id}`, `session-{id}`, `_platform`, or a claim's own scope id.
    /// The ONE place a container string is produced.
    let container (key: ScopeKey) : string =
        match key with
        | ScopeKey.Platform shard -> shardContainer shard
        | ScopeKey.Team teamId -> TeamPrefix + teamId
        | ScopeKey.User userId -> UserPrefix + userId
        | ScopeKey.Session sessionId -> SessionPrefix + sessionId
        | ScopeKey.Claim claimScopeId -> claimScopeId

    /// The bare store id: the team, user or session id itself, the reserved
    /// shard's name, or a claim's scope id. This is what
    /// `StorageScope.ScopeId` carries and what an audit stream is keyed on.
    let scopeId (key: ScopeKey) : string =
        match key with
        | ScopeKey.Platform shard -> shardContainer shard
        | ScopeKey.Team teamId -> teamId
        | ScopeKey.User userId -> userId
        | ScopeKey.Session sessionId -> sessionId
        | ScopeKey.Claim claimScopeId -> claimScopeId

    /// An id is well formed when it is non-empty and carries no leading or
    /// trailing whitespace — the rule the typed identity wrappers' smart
    /// constructors apply.
    let private isWellFormedId (id: string) =
        id.Length > 0 && id.Trim().Length = id.Length

    /// The prefixed cases of the grammar, each with the case it builds.
    let private prefixed: (string * (string -> ScopeKey)) list = [
        TeamPrefix, ScopeKey.Team
        UserPrefix, ScopeKey.User
        SessionPrefix, ScopeKey.Session
    ]

    /// The id after a grammar prefix (ordinal), with the case it names.
    let private stripPrefix (container: string) : (string * (string -> ScopeKey)) option =
        prefixed
        |> List.tryPick (fun (prefix, case) ->
            if container.StartsWith(prefix, System.StringComparison.Ordinal) then
                Some(container.Substring prefix.Length, case)
            else
                None)

    /// Read a container written under the grammar back to its key — the ONE
    /// legacy parser, kept for names already at rest. `_platform` is the
    /// platform shard; `team-` / `user-` / `session-` followed by a
    /// well-formed id are those scopes. Anything else — a padded or empty
    /// id after a prefix, an unknown shape, `null` — is `None`: the caller
    /// decides what an unparseable container means, never this function.
    /// A claim's container is whatever scope it names, so no container
    /// parses to `ScopeKey.Claim`.
    let tryParseContainer (container: string) : ScopeKey option =
        if isNull container then
            None
        elif container = PlatformContainer then
            Some platform
        else
            match stripPrefix container with
            | Some(id, case) when isWellFormedId id -> Some(case id)
            | _ -> None

    /// The bare id a container written before the grammar had a type was read back to,
    /// byte for byte: the prefix stripped whatever follows it (an empty or
    /// padded id included), any other container its own id. The encryption
    /// decorator derives a blob's key material from this, so it must stay
    /// identical for every container already at rest — which is exactly
    /// where it differs from `tryParseContainer`, whose refusal of a
    /// malformed id would move the key under a blob written with one.
    /// Internal: key derivation over names at rest is its one caller.
    let internal scopeIdAtRest (container: string) : string =
        if container = PlatformContainer then
            container
        else
            match stripPrefix container with
            | Some(id, _) -> id
            | None -> container

    /// The surface-persistence rule, written once: whether a scope persists under the
    /// declared `Surfaces`. A session reads the `Anonymous` profile, a user
    /// the `AuthenticatedUser` profile, a team the `Team` profile; no
    /// matching profile fails CLOSED (ephemeral) with `Defaulted = true`. A
    /// claim carries its own persistent scope and a platform shard always
    /// persists — neither is matched against `Surfaces`, so neither defaults.
    let persistence (surfaces: SurfaceProfile list) (key: ScopeKey) : ScopePersistence =
        let pick projector =
            match surfaces |> List.tryPick projector with
            | Some persist -> { Persist = persist; Defaulted = false }
            | None -> { Persist = false; Defaulted = true }

        match key with
        | ScopeKey.Session _ ->
            pick (function
                | SurfaceProfile.Anonymous cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        | ScopeKey.User _ ->
            pick (function
                | SurfaceProfile.AuthenticatedUser cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        | ScopeKey.Team _ ->
            pick (function
                | SurfaceProfile.Team cfg -> Some(cfg.Persistence = Persistent)
                | _ -> None)
        | ScopeKey.Claim _
        | ScopeKey.Platform _ -> { Persist = true; Defaulted = false }

    /// Whether the scope persists under the declared `Surfaces` — see
    /// `ScopeKey.persistence`.
    let persists (surfaces: SurfaceProfile list) (key: ScopeKey) : bool = (persistence surfaces key).Persist

/// The scope an audit event is recorded under: the platform's
/// own trail, or a principal's scope. Its one rendering is
/// `AuditScope.streamKey`.
[<RequireQualifiedAccess>]
type AuditScope =
    /// The platform's own audit trail — deployment-level facts that belong
    /// to no tenant.
    | PlatformScope
    /// A principal's scope.
    | Scoped of ScopeKey

/// The rendering of an audit stream key.
[<RequireQualifiedAccess>]
module AuditScope =

    /// The audit stream key the event store keys a stream on: the scope's
    /// BARE id (`ScopeKey.scopeId`), never its container — what every event
    /// already at rest carries (GP 11). The platform
    /// trail is `_platform`.
    let streamKey (scope: AuditScope) : string =
        match scope with
        | AuditScope.PlatformScope -> ScopeKey.PlatformContainer
        | AuditScope.Scoped key -> ScopeKey.scopeId key