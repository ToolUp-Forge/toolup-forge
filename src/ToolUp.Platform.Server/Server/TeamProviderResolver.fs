// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.TeamProviderResolver

open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.TeamManagement

// ─── Phase 44a — the default ITeamProviderResolver ───────────────
//
// **It lives in `Platform.Server`, not `ToolUp.AI.Server`, and that is
// a deliberate placement rather than convenience.** The phase's key
// files named `ToolUp.AI.Server/Server/TeamProviderResolver.fs` with
// the standing carve-out that a resolver carrying no AI dependency
// belongs beside `ProviderProfileApiHandler.fs` instead. It carries
// none: it composes `IProviderProfile`, `StorageScope`, `Subject` and
// `ITeamStore`, and the one AI-shaped thing in sight — the surface key
// `"ai.assistant"` — is a string a caller passes. Putting it AI-side
// would have made every non-AI BYOK consumer depend on the AI
// assistant companion to answer "which provider applies to this
// request", which is exactly the coupling `IProviderProfile`'s own
// header says the store was moved to the Platform floor to avoid
// (GP 1).
//
// The resolution walks `ProviderScopeChain.forSubject` and STOPS at
// the first rung that answers, so a user override costs one store read
// and never touches the team blob. Each rung's decision is taken by
// `ProviderResolution.resolveOver` over that single rung rather than
// by a re-implementation of the same precedence here — the pure fold
// is the statement of the rule, and a second copy of it in this file
// is exactly the drift a contract pack could not see.
//
// **The role lookup is a function, not an `ITeamStore`.** A deployment
// with no team management registered still resolves reads (its
// subjects are `AuthenticatedUser`, whose chain has no team rung), and
// a test pins the write gate without standing up a team store. The
// `create` overload below adapts a real `ITeamStore` for the
// composition root.

/// Look up a member's role on a team. `None` means not a member —
/// which the write gate treats as a refusal, never as "no restriction".
type TeamRoleLookup = string -> string -> Async<TeamRole option>

/// A lookup for a deployment with no team management wired. Answers
/// `None` for everyone, so every team-rung write is refused. Reads are
/// unaffected — they never consult a role.
let noTeamRoles: TeamRoleLookup = fun _ _ -> async { return None }

/// Adapt a registered `ITeamStore` into the lookup.
let teamStoreRoles (store: ITeamStore) : TeamRoleLookup =
    fun teamId userId -> store.GetMemberRole(teamId, userId)

/// The user acting in a subject, for the role lookup. `None` for a
/// subject that names no user, which cannot reach a team rung anyway.
let private actingUser (subject: Subject) : string option =
    match subject with
    | TeamMember(userId, _) -> Some userId
    | AuthenticatedUser userId -> Some userId
    | AnonymousSession _
    | ClaimBearer _ -> None

/// Default `ITeamProviderResolver` over an `IProviderProfile` and a
/// role lookup.
type TeamProviderResolver(profiles: IProviderProfile, roleOf: TeamRoleLookup) =

    /// Walk the chain, loading one rung at a time and stopping at the
    /// first that produces an answer. `decide` is the pure fold
    /// applied to that one rung.
    let firstAnswer (chain: ProviderScope list) (decide: ProviderScope -> ProviderProfile option -> 'a option) = async {
        let rec walk (rungs: ProviderScope list) = async {
            match rungs with
            | [] -> return None
            | rung :: rest ->
                let! profile = profiles.Get rung.Storage

                match decide rung profile with
                | Some answer -> return Some answer
                | None -> return! walk rest
        }

        return! walk chain
    }

    interface ITeamProviderResolver with
        member _.Resolve(subject, surface, context) = async {
            let chain = ProviderScopeChain.forSubject subject

            let! answer =
                firstAnswer chain (fun rung profile ->
                    match ProviderResolution.resolveOver surface context [ rung, profile ] with
                    | resolved when resolved.Entry.IsSome -> Some resolved
                    | _ -> None)

            return answer |> Option.defaultValue ProviderResolution.platformDefault
        }

        member _.ResolveModelOverride(subject, surface) = async {
            let chain = ProviderScopeChain.forSubject subject

            return!
                firstAnswer chain (fun rung profile -> ProviderResolution.modelOverrideOver surface [ rung, profile ])
        }

        member _.CanWrite(subject, owner) = async {
            match ProviderScopeChain.writeTarget owner subject with
            | None ->
                // Either the subject cannot reach this owner at all
                // (an individual user asking for a team rung), or the
                // owner is `PlatformDefault`, which is deployment
                // configuration rather than a profile this model
                // edits. Both are told apart in the message, because
                // "you are not in that team" and "nobody edits that
                // here" prompt very different next steps.
                match owner with
                | ProviderProfileOwner.PlatformDefault ->
                    return
                        Error
                            "The platform default provider configuration is deployment configuration, not a profile this surface edits."
                | _ -> return Error "You are not acting in a scope that can write this provider configuration."
            | Some rung ->
                match rung.Owner with
                | ProviderProfileOwner.TeamOwned teamId ->
                    match actingUser subject with
                    | None -> return Error "You are not a member of this team."
                    | Some userId ->
                        let! role = roleOf teamId userId

                        match role with
                        | Some r when TeamRoles.canWriteTeamConfig r -> return Ok rung
                        | Some r ->
                            return
                                Error
                                    $"Only team owners and admins can change the team's provider profile. Your role: {TeamRoles.displayName r}."
                        | None -> return Error "You are not a member of this team."
                | _ ->
                    // A user's own rung, or a claim bearer's. You own
                    // your own scope — the same answer
                    // `ProviderProfileApiHandler.ensureWriteAllowed`
                    // gives a non-team caller.
                    return Ok rung
        }

/// Compose over a store and a role lookup.
let create (profiles: IProviderProfile) (roleOf: TeamRoleLookup) : ITeamProviderResolver =
    TeamProviderResolver(profiles, roleOf) :> ITeamProviderResolver

/// Compose over a store and a registered `ITeamStore`.
let forTeamStore (profiles: IProviderProfile) (teams: ITeamStore) : ITeamProviderResolver =
    create profiles (teamStoreRoles teams)