module ToolUp.Platform.Tests.Contracts.ITeamProviderResolverContract

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Providers

/// Contract test list for any `ITeamProviderResolver` implementation
/// (Phase 44a). The factory is handed the `IProviderProfile` the pack
/// has already seeded and the role lookup the case wants, and returns
/// the implementation under test — so a distributed or caching
/// resolver is held to the same bar as the in-tree one without the
/// pack knowing how it is built.
///
/// Three claims, and they are the phase's acceptance criteria stated
/// as executable cases:
///
///   * **Precedence.** A team-owned profile resolves for every member
///     of that team; a user-owned profile overrides it for that user
///     only; absent both, the platform default applies. Fall-through
///     is per-SURFACE, so an override of one surface does not strand a
///     member's inherited routing for another.
///   * **Scope isolation (GP 4).** A member of team A never sees team
///     B's configuration, whatever it holds — and a resolved team-owned
///     entry carries the TEAM's scope out, because a consumer reading
///     its `SecretKeyName` at the member's own scope would find
///     nothing and silently fall back.
///   * **Permission gating.** Only Owner/Admin can write the team rung
///     (`TeamRoles.canWriteTeamConfig`); everyone owns their own rung;
///     nobody writes the platform default through this model.
///
/// Core-only by construction — the pack stands up its own in-memory
/// `IProviderProfile` rather than reaching for a server-tier one, so an
/// external implementer can copy this file and run it against nothing
/// but `ToolUp.Platform.Core`.
let tests
    (name: string)
    (factory: IProviderProfile -> (string -> string -> Async<TeamRole option>) -> ITeamProviderResolver)
    =

    /// The minimal conforming `IProviderProfile` the cases seed. Keyed
    /// by `StorageScope.Container`, which is what makes a scope's blob
    /// a scope's blob.
    let profileStore () =
        let blobs = ConcurrentDictionary<string, ProviderProfile>()

        { new IProviderProfile with
            member _.Get scope = async {
                match blobs.TryGetValue scope.Container with
                | true, p -> return Some p
                | false, _ -> return None
            }

            member _.Set(scope, profile) = async {
                blobs[scope.Container] <- profile
                return Ok()
            }

            member _.Clear scope = async { blobs.TryRemove scope.Container |> ignore }

            member this.ResolveEntry(scope, surface, context) = async {
                let! profile = this.Get scope
                return profile |> Option.bind (ProviderProfile.resolveEntry surface context)
            }

            member this.SetEntryHealth(scope, label, health) = async {
                match blobs.TryGetValue scope.Container with
                | false, _ -> return Ok()
                | true, profile ->
                    let updated =
                        profile.Entries
                        |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)

                    blobs[scope.Container] <- { profile with Entries = updated }
                    return Ok()
            }
        }

    let ts = DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc)

    let entry (label: string) (providerId: string) : ProviderEntry = {
        Label = label
        ProviderId = providerId
        Model = None
        SecretKeyName = label + "-key"
        Tags = []
        Origin = CredentialOrigin.PastedKey
        Health = ProviderHealth.unknown
        OAuthBinding = None
        UpdatedAt = ts
    }

    /// A profile routing one surface to one freshly-made entry.
    let routing (surface: string) (label: string) (providerId: string) : ProviderProfile = {
        ProviderProfile.empty () with
            Entries = [ entry label providerId ]
            Routing = [
                {
                    Surface = surface
                    Context = None
                    EntryLabel = label
                }
            ]
    }

    let seed (store: IProviderProfile) (rung: ProviderScope) (profile: ProviderProfile) =
        store.Set(rung.Storage, profile) |> Async.RunSynchronously |> ignore

    /// Role lookups the permission cases need. Each is total and says
    /// exactly what it means, so a refusal can never be mistaken for an
    /// unwired store.
    let rolesOf (assignments: (string * string * TeamRole) list) =
        fun teamId userId -> async {
            return
                assignments
                |> List.tryPick (fun (t, u, r) -> if t = teamId && u = userId then Some r else None)
        }

    let noRoles: string -> string -> Async<TeamRole option> =
        fun _ _ -> async { return None }

    let expectEntry (label: string) (resolution: ProviderResolution) =
        match resolution.Entry with
        | Some e -> Expect.equal e.Label label "the resolved entry"
        | None -> failtestf "expected the entry %s, got the platform default" label

    testList $"ITeamProviderResolver contract — {name}" [

        // ─── Precedence ──────────────────────────────────────────────

        testCaseAsync "a team-owned profile resolves for a member who has none of their own"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)

            expectEntry "team-primary" resolved
            Expect.equal resolved.Owner (ProviderProfileOwner.TeamOwned "t1") "the team rung answered"
        }

        testCaseAsync "a team-owned profile resolves for EVERY member of that team"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")
            let resolver = factory store noRoles

            let! forAlice = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)
            let! forBob = resolver.Resolve(TeamMember("bob", "t1"), "ai.assistant", None)

            expectEntry "team-primary" forAlice
            expectEntry "team-primary" forBob
            Expect.equal forAlice.Owner forBob.Owner "both members resolve the same owner"
        }

        testCaseAsync "a user-owned profile overrides the team's — for that user only"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")
            seed store (ProviderScope.userOwned "alice") (routing "ai.assistant" "alice-own" "openai")
            let resolver = factory store noRoles

            let! forAlice = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)
            let! forBob = resolver.Resolve(TeamMember("bob", "t1"), "ai.assistant", None)

            expectEntry "alice-own" forAlice
            Expect.equal forAlice.Owner (ProviderProfileOwner.UserOwned "alice") "alice's own rung answered"

            expectEntry "team-primary" forBob
            Expect.equal forBob.Owner (ProviderProfileOwner.TeamOwned "t1") "bob still gets the team's"
        }

        testCaseAsync "fall-through is per SURFACE — an override of one leaves the other inherited"
        <| async {
            let store = profileStore ()

            let teamProfile = {
                routing "ai.assistant" "team-ai" "anthropic" with
                    Entries = [ entry "team-ai" "anthropic"; entry "team-gateway" "stripe" ]
                    Routing = [
                        {
                            Surface = "ai.assistant"
                            Context = None
                            EntryLabel = "team-ai"
                        }
                        {
                            Surface = "rental.gateway"
                            Context = None
                            EntryLabel = "team-gateway"
                        }
                    ]
            }

            seed store (ProviderScope.teamOwned "t1") teamProfile
            seed store (ProviderScope.userOwned "alice") (routing "ai.assistant" "alice-ai" "openai")
            let resolver = factory store noRoles

            let! ai = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)
            let! gateway = resolver.Resolve(TeamMember("alice", "t1"), "rental.gateway", None)

            expectEntry "alice-ai" ai
            expectEntry "team-gateway" gateway

            Expect.equal
                gateway.Owner
                (ProviderProfileOwner.TeamOwned "t1")
                "the un-overridden surface stays the team's"
        }

        testCaseAsync "a user rung whose rule names a stale label falls through to the team's"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")

            // A rule pointing at an entry that is not there — the shape
            // `ProviderProfile.resolveEntry` answers `None` for. One bad
            // personal entry must not take the team's configuration
            // offline for that member.
            let stale = {
                ProviderProfile.empty () with
                    Entries = []
                    Routing = [
                        {
                            Surface = "ai.assistant"
                            Context = None
                            EntryLabel = "deleted-long-ago"
                        }
                    ]
            }

            seed store (ProviderScope.userOwned "alice") stale
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)

            expectEntry "team-primary" resolved
        }

        testCaseAsync "nothing configured anywhere resolves to the platform default"
        <| async {
            let store = profileStore ()
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)

            Expect.isNone resolved.Entry "no entry"
            Expect.equal resolved.Owner ProviderProfileOwner.PlatformDefault "the platform default"
            Expect.isNone resolved.OwningScope "and no scope to read a secret at"
        }

        testCaseAsync "a surface nobody routes resolves to the platform default even with profiles present"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "t1"), "rental.gateway", None)

            Expect.equal resolved.Owner ProviderProfileOwner.PlatformDefault "no rung routes this surface"
        }

        testCaseAsync "the surface model override follows the same precedence"
        <| async {
            let store = profileStore ()

            seed
                store
                (ProviderScope.teamOwned "t1")
                (ProviderProfile.empty ()
                 |> ProviderProfile.withSurfaceModelOverride "ai.platform" (Some "team-model"))

            let resolver = factory store noRoles

            let! inherited = resolver.ResolveModelOverride(TeamMember("bob", "t1"), "ai.platform")
            Expect.equal inherited (Some "team-model") "bob inherits the team's model override"

            seed
                store
                (ProviderScope.userOwned "alice")
                (ProviderProfile.empty ()
                 |> ProviderProfile.withSurfaceModelOverride "ai.platform" (Some "alice-model"))

            let! overridden = resolver.ResolveModelOverride(TeamMember("alice", "t1"), "ai.platform")
            Expect.equal overridden (Some "alice-model") "alice's own override wins"
        }

        // ─── Scope isolation (GP 4) ──────────────────────────────────

        testCaseAsync "a member of team A never resolves team B's configuration"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "team-b") (routing "ai.assistant" "b-secret-provider" "anthropic")
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "team-a"), "ai.assistant", None)

            Expect.equal
                resolved.Owner
                ProviderProfileOwner.PlatformDefault
                "team A sees nothing of team B's — not the entry, not its existence"
        }

        testCaseAsync "a resolved team-owned entry carries the TEAM's scope, not the member's"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.teamOwned "t1") (routing "ai.assistant" "team-primary" "anthropic")
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(TeamMember("alice", "t1"), "ai.assistant", None)

            // The load-bearing half: a consumer reads the entry's
            // SecretKeyName at THIS scope. Answering the member's own
            // scope would find no key and degrade to the platform
            // default with no error anywhere.
            Expect.equal
                (resolved.OwningScope |> Option.map _.Container)
                (Some "team-t1")
                "the secret lives in the team's scope"
        }

        testCaseAsync "a user-owned entry carries the user's own scope"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.userOwned "alice") (routing "ai.assistant" "alice-own" "openai")
            let resolver = factory store noRoles

            let! resolved = resolver.Resolve(AuthenticatedUser "alice", "ai.assistant", None)

            Expect.equal (resolved.OwningScope |> Option.map _.Container) (Some "user-alice") "the user's own scope"
        }

        testCaseAsync "an anonymous subject resolves the platform default and reaches no scope at all"
        <| async {
            let store = profileStore ()
            seed store (ProviderScope.userOwned "sess-1") (routing "ai.assistant" "smuggled" "anthropic")
            let resolver = factory store noRoles

            // The session id matching a user container is the point: an
            // anonymous subject has no persistent scope, so it must not
            // reach one that happens to share its id.
            let! resolved = resolver.Resolve(AnonymousSession "sess-1", "ai.assistant", None)

            Expect.equal resolved.Owner ProviderProfileOwner.PlatformDefault "no rung, no read"
        }

        // ─── Permission gating ───────────────────────────────────────

        testCaseAsync "Owner and Admin may write the team rung; Member may not"
        <| async {
            let store = profileStore ()

            let resolver =
                factory store (rolesOf [ "t1", "olive", Owner; "t1", "adam", Admin; "t1", "mary", Member ])

            let team = ProviderProfileOwner.TeamOwned "t1"

            let! asOwner = resolver.CanWrite(TeamMember("olive", "t1"), team)
            let! asAdmin = resolver.CanWrite(TeamMember("adam", "t1"), team)
            let! asMember = resolver.CanWrite(TeamMember("mary", "t1"), team)

            match asOwner with
            | Ok rung -> Expect.equal rung.Storage.Container "team-t1" "the owner writes the team scope"
            | Error e -> failtestf "Owner refused: %s" e

            Expect.isOk (asAdmin |> Result.map ignore) "Admin may write"
            Expect.isError (asMember |> Result.map ignore) "Member may not write the team rung"
        }

        testCaseAsync "a non-member may not write the team rung"
        <| async {
            let store = profileStore ()
            let resolver = factory store (rolesOf [ "t1", "olive", Owner ])

            let! result = resolver.CanWrite(TeamMember("stranger", "t1"), ProviderProfileOwner.TeamOwned "t1")

            Expect.isError (result |> Result.map ignore) "no membership, no write"
        }

        testCaseAsync "every subject may write its OWN rung without a role lookup"
        <| async {
            let store = profileStore ()
            // `noRoles` refuses everyone: if the user rung consulted it,
            // this case would fail — which is the point of using it here.
            let resolver = factory store noRoles

            let! asTeamMember = resolver.CanWrite(TeamMember("mary", "t1"), ProviderProfileOwner.UserOwned "mary")
            let! asIndividual = resolver.CanWrite(AuthenticatedUser "ida", ProviderProfileOwner.UserOwned "ida")

            match asTeamMember with
            | Ok rung -> Expect.equal rung.Storage.Container "user-mary" "a Member still owns their own override"
            | Error e -> failtestf "own rung refused for a team member: %s" e

            match asIndividual with
            | Ok rung -> Expect.equal rung.Storage.Container "user-ida" "an individual owns their own scope"
            | Error e -> failtestf "own rung refused for an individual: %s" e
        }

        testCaseAsync "a subject may not write another subject's rung"
        <| async {
            let store = profileStore ()
            let resolver = factory store (rolesOf [ "t1", "olive", Owner ])

            let! crossUser = resolver.CanWrite(TeamMember("mary", "t1"), ProviderProfileOwner.UserOwned "olive")
            let! crossTeam = resolver.CanWrite(TeamMember("olive", "t1"), ProviderProfileOwner.TeamOwned "t2")
            let! noTeamAtAll = resolver.CanWrite(AuthenticatedUser "ida", ProviderProfileOwner.TeamOwned "t1")

            Expect.isError (crossUser |> Result.map ignore) "not your user scope"
            Expect.isError (crossTeam |> Result.map ignore) "not your team"
            Expect.isError (noTeamAtAll |> Result.map ignore) "not in any team"
        }

        testCaseAsync "nobody writes the platform default through this model"
        <| async {
            let store = profileStore ()
            let resolver = factory store (rolesOf [ "t1", "olive", Owner ])

            let! asOwner = resolver.CanWrite(TeamMember("olive", "t1"), ProviderProfileOwner.PlatformDefault)

            Expect.isError
                (asOwner |> Result.map ignore)
                "the platform default is deployment configuration, not a profile"
        }

        testCaseAsync "an anonymous subject may write nothing"
        <| async {
            let store = profileStore ()
            let resolver = factory store (rolesOf [ "t1", "olive", Owner ])

            let! ownRung = resolver.CanWrite(AnonymousSession "sess-1", ProviderProfileOwner.UserOwned "sess-1")
            let! teamRung = resolver.CanWrite(AnonymousSession "sess-1", ProviderProfileOwner.TeamOwned "t1")

            Expect.isError (ownRung |> Result.map ignore) "an anonymous session owns no persistent scope"
            Expect.isError (teamRung |> Result.map ignore) "and reaches no team"
        }
    ]