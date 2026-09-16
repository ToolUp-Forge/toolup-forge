// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.TeamScopedSecretStore

open ToolUp.Platform.Secrets

// ─── Phase 44a — team-scoped ISecretStore layer ──────────────────
//
// `ISecretStore` already carries a scope, and its contract already
// says implementations MUST NOT serve one scope's secret to another.
// So what does this layer add?
//
// Two things, on two INDEPENDENT axes, and the second is the one that
// earns the file:
//
//   1. The scope is PINNED rather than passed. A caller holding this
//      store cannot name another team's scope, because the store
//      substitutes nothing and simply does not answer for one. The
//      `scopeId` parameter stops being an argument the caller chooses
//      and becomes an assertion the store checks — which is what turns
//      "remember to pass the right scope" into GP 4's structural
//      enforcement.
//
//   2. The KEY carries the team too. Axis 1 is only as strong as the
//      inner store's own isolation, and the interface admits
//      implementations that flatten — an env-var store whose scope
//      sanitiser collides, a misconfigured backend, a legacy store
//      wired at `_platform`. Against a flattening inner store, axis 1
//      alone still leaks; with the key prefixed, team A's `openai` and
//      team B's `openai` are different keys at rest and neither can
//      read the other by construction rather than by trust.
//
// **The prefix is length-tagged, and that is not decoration.** The
// obvious `team-{teamId}-{key}` is AMBIGUOUS whenever a team id may
// contain the separator, which a GUID always does: team `a` holding
// key `b-x` and team `a-b` holding key `x` produce the identical
// string. Under the flattening store axis 2 exists to defend against,
// that is a crafted cross-team read. `t{len}-{teamId}-{key}` cannot
// collide: the length names exactly how much of what follows is the
// team id. It also stays inside `[a-zA-Z0-9-]` whenever the team id
// does, which is the narrowest alphabet any shipped companion accepts
// (Azure Key Vault's).
//
// **What this layer is NOT.** It is not a migration of the keys the
// Phase 44 `ProviderProfileApiHandler` and the AI settings handler
// already write at the caller's own `configScope` — those keep their
// placement, keep working, and are untouched (GP 11). It is the store
// a TEAM-SCOPED consumer holds, and a team surface that writes through
// it reads back through it; the two namespaces are disjoint on
// purpose, because silently adopting keys written through an
// unprefixed path would mean claiming an isolation property for
// material this layer never placed.

/// The key transform, as a pure pair of total functions so the
/// isolation claim is testable without a store.
module TeamSecretKey =

    /// The scope container a team's secrets live under — the same
    /// `team-{teamId}` shape `AccessContext.configScope` and the
    /// `ISecretStore` doc comment both name.
    let container (teamId: string) = $"team-{teamId}"

    /// The at-rest key for `key` under `teamId`. Length-tagged so no
    /// two `(teamId, key)` pairs can produce the same string — see the
    /// module header.
    let scoped (teamId: string) (key: string) = $"t{teamId.Length}-{teamId}-{key}"

    /// Recover the key from an at-rest name, when and only when it
    /// belongs to `teamId`. `None` for another team's key and for any
    /// name that was not written through this transform — which is
    /// what makes `ListKeys` able to report a team's own keys without
    /// reporting the existence of anyone else's.
    let unscoped (teamId: string) (atRest: string) : string option =
        let prefix = $"t{teamId.Length}-{teamId}-"

        if atRest.StartsWith(prefix, System.StringComparison.Ordinal) then
            Some(atRest.Substring prefix.Length)
        else
            None

/// An `ISecretStore` pinned to one team's scope, holding every key
/// under that team's at-rest prefix.
///
/// A call naming any other scope — another team, a user scope, or the
/// reserved `_platform` scope — is refused rather than served: `None`
/// from `GetSecret` (the interface's own "not found here", which is
/// the honest answer, since it genuinely is not visible from here),
/// `Error` from the writes, and `[]` from `ListKeys`. `_platform` is
/// refused with the rest deliberately: a team layer that could reach
/// the deployment's own credentials would be a back door out of the
/// boundary it exists to draw.
type TeamScopedSecretStore(inner: ISecretStore, teamId: string) =
    let container = TeamSecretKey.container teamId

    let refusal (scopeId: string) =
        $"This secret store is scoped to {container} and cannot reach {scopeId}."

    /// True when the caller named this team's own scope.
    let isOwnScope (scopeId: string) =
        System.String.Equals(scopeId, container, System.StringComparison.Ordinal)

    /// The team this store is pinned to. Read by composition and by
    /// diagnostics; never used to widen a lookup.
    member _.TeamId = teamId

    /// The scope container every call is pinned to.
    member _.Container = container

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            if isOwnScope scopeId then
                return! inner.GetSecret(container, TeamSecretKey.scoped teamId key)
            else
                return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            if isOwnScope scopeId then
                return! inner.SetSecret(container, TeamSecretKey.scoped teamId key, value)
            else
                return Error(refusal scopeId)
        }

        member _.DeleteSecret(scopeId, key) = async {
            if isOwnScope scopeId then
                return! inner.DeleteSecret(container, TeamSecretKey.scoped teamId key)
            else
                return Error(refusal scopeId)
        }

        member _.ListKeys(scopeId) = async {
            if isOwnScope scopeId then
                let! atRest = inner.ListKeys container
                return atRest |> List.choose (TeamSecretKey.unscoped teamId)
            else
                return []
        }

    /// The posture is the inner store's, DELEGATED rather than
    /// asserted — the same choice `ResilientSecretStore` makes, and for
    /// the same reason: prefixing a key changes nothing about what the
    /// backend does to the bytes, so a wrapper answering for itself
    /// would relabel a KMS-backed companion or, worse, launder a
    /// plaintext one.
    interface ISecretStoreAtRestPosture with
        member _.AtRestPosture =
            match box inner with
            | :? ISecretStoreAtRestPosture as declared -> declared.AtRestPosture
            | _ ->
                UnknownAtRest
                    "the store wrapped by TeamScopedSecretStore declares no at-rest posture (ISecretStoreAtRestPosture)"

/// Pin `inner` to `teamId`. The one construction route, so a consumer
/// never holds the unpinned store by accident.
let forTeam (teamId: string) (inner: ISecretStore) : ISecretStore =
    TeamScopedSecretStore(inner, teamId) :> ISecretStore