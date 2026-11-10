module ToolUp.Platform.Tests.Contracts.IPermissionStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Scope.PermissionStore
open ToolUp.Platform.BlobStorage

// ModulePermission carries `[<RequireQualifiedAccess>]` because its
// `Admin` case collides with TeamRole.Admin (different concepts).
// Use shorthand local bindings so test assertions stay readable;
// every mention is still unambiguously ModulePermission-typed.
let private Read = ModulePermission.Read
let private Write = ModulePermission.Write
let private Admin = ModulePermission.Admin

/// A read the case expects to succeed; an `Error` fails the case by name.
let private ok (read: Async<Result<'T, PermissionStoreError>>) : Async<'T> = async {
    match! read with
    | Ok value -> return value
    | Error e -> return failtestf "the permission read failed: %s" (PermissionStoreError.describe e)
}

/// Fails every `Download`; everything else passes through, so a document
/// written through it is present but cannot be read back.
type private DownloadFailingStorage(inner: IBlobStorage) =
    interface IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Download(_, _) = async { return Error "simulated outage" }

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// Contract test list for any `IPermissionStore` implementation.
/// `over` builds a store over the blob storage it is handed, so the pack
/// can put the backing document into each of the three states the store
/// must tell apart — absent, present but unreadable, present but corrupt —
/// on every binding. Tests GUID-suffix their team IDs so implementations
/// that share underlying state stay isolated.
///
/// The store's load-bearing correctness property is the effective-
/// permission merge: a user with an explicit per-module entry sees
/// that; a user without sees the team default; a user for a module
/// with neither sees nothing (no-access). The fail-closed property beside
/// it: only an ABSENT document reads as the empty (unrestricted) map.
let tests (name: string) (over: IBlobStorage -> IPermissionStore) =
    let factory () =
        over (InMemoryBlobStorage.InMemoryBlobStorage())

    let uniqueTeam () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "team-" + suffix

    /// A backing store holding a written document for `team`.
    let seeded (team: string) =
        let backing = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

        match
            (over backing).SetModuleExposure(TeamId team, "Reports", ModuleExposure.Hidden)
            |> Async.RunSynchronously
        with
        | Ok() -> backing
        | Error e -> failtestf "seeding the document failed: %s" e

    testList $"{name} — IPermissionStore contract" [
        testCaseAsync "an absent document is Ok empty — the bootstrap default, decided in the store"
        <| async {
            let store = factory ()
            let team = TeamId(uniqueTeam ())

            match! store.GetTeamPermissions team with
            | Ok perms -> Expect.equal perms TeamPermissions.empty "no document reads as no restrictions"
            | Error e -> failtestf "an absent document must not be an error: %A" e

            match! store.GetEffectivePermissions(UserId "alice", team) with
            | Ok perms -> Expect.isEmpty perms "no document grants nothing explicitly"
            | Error e -> failtestf "an absent document must not be an error: %A" e
        }

        testCaseAsync "a present document that cannot be read is Error on every read, and refuses a write"
        <| async {
            let team = uniqueTeam ()
            let store = over (DownloadFailingStorage(seeded team))

            match! store.GetTeamPermissions(TeamId team) with
            | Error(PermissionStoreError.Unreadable _) -> ()
            | other -> failtestf "expected Unreadable, got %A" other

            let! effective = store.GetEffectivePermissions(UserId "alice", TeamId team)
            Expect.isError effective "effective permissions are not read as unrestricted"

            let! exposure = store.GetModuleExposure(TeamId team)
            Expect.isError exposure "exposure is not read as all-Available"

            let! written = store.SetTeamDefaults(TeamId team, Map.empty)
            Expect.isError written "a write over an unreadable document refuses"
        }

        testCaseAsync "a present document that does not decode is Error (quarantined), never unrestricted"
        <| async {
            let team = uniqueTeam ()
            let backing = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage

            let! _ =
                backing.Upload(
                    ScopeKey.PlatformContainer,
                    $"permissions/{team}.json",
                    Text.Encoding.UTF8.GetBytes "{ not json"
                )

            let store = over backing

            match! store.GetTeamPermissions(TeamId team) with
            | Error(PermissionStoreError.Quarantined _) -> ()
            | other -> failtestf "expected Quarantined, got %A" other

            let! effective = store.GetEffectivePermissions(UserId "alice", TeamId team)
            Expect.isError effective "a corrupt document is not read as unrestricted"
        }

        testCaseAsync "GetTeamPermissions on untouched team returns empty"
        <| async {
            let store = factory ()
            let! perms = ok (store.GetTeamPermissions(TeamId(uniqueTeam ())))
            Expect.isEmpty perms.Defaults "no defaults configured"
            Expect.isEmpty perms.Members "no members configured"
        }

        testCaseAsync "GetEffectivePermissions on untouched team returns empty (unrestricted)"
        <| async {
            let store = factory ()
            let! perms = ok (store.GetEffectivePermissions(UserId "alice", TeamId(uniqueTeam ())))
            // Empty map = unrestricted via AccessContext.canAccessModule.
            Expect.isEmpty perms "no perms configured"
        }

        testCaseAsync "SetMemberPermissions round-trips"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            match! store.SetMemberPermissions(TeamId team, UserId "alice", "SkuAnalysis", [ Read; Write ]) with
            | Error e -> failtestf "SetMemberPermissions failed: %s" e
            | Ok() -> ()

            let! perms = ok (store.GetEffectivePermissions(UserId "alice", TeamId team))
            // Use `Expect.equal` — pattern-matching on `[ Read; Write ]`
            // would treat Read / Write as variable bindings (they're
            // module-level `let`s, not DU cases under RequireQualifiedAccess)
            // and match any two-element list.
            Expect.equal (perms.TryFind "SkuAnalysis") (Some [ Read; Write ]) "alice has Read + Write"
        }

        testCaseAsync "Defaults apply to users without explicit entries"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let defaults = Map.ofList [ "SkuAnalysis", [ Read ] ]

            match! store.SetTeamDefaults(TeamId team, defaults) with
            | Error e -> failtestf "SetTeamDefaults failed: %s" e
            | Ok() -> ()

            let! perms = ok (store.GetEffectivePermissions(UserId "bob", TeamId team))
            Expect.equal (perms.TryFind "SkuAnalysis") (Some [ Read ]) "bob inherits default"
        }

        testCaseAsync "Explicit member entries override defaults"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let! _ = store.SetTeamDefaults(TeamId team, Map.ofList [ "SkuAnalysis", [ Read ] ])

            let! _ = store.SetMemberPermissions(TeamId team, UserId "alice", "SkuAnalysis", [ Admin ])

            let! alicePerms = ok (store.GetEffectivePermissions(UserId "alice", TeamId team))
            Expect.equal (alicePerms.TryFind "SkuAnalysis") (Some [ Admin ]) "alice's explicit entry wins"

            let! bobPerms = ok (store.GetEffectivePermissions(UserId "bob", TeamId team))
            Expect.equal (bobPerms.TryFind "SkuAnalysis") (Some [ Read ]) "bob still gets default"
        }

        testCaseAsync "Empty permission list on SetMemberPermissions removes the entry"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let! _ = store.SetTeamDefaults(TeamId team, Map.ofList [ "SkuAnalysis", [ Read ] ])
            let! _ = store.SetMemberPermissions(TeamId team, UserId "alice", "SkuAnalysis", [ Admin ])

            // Revoke alice's explicit override — she should fall back
            // to the team default.
            let! _ = store.SetMemberPermissions(TeamId team, UserId "alice", "SkuAnalysis", [])

            let! perms = ok (store.GetEffectivePermissions(UserId "alice", TeamId team))
            Expect.equal (perms.TryFind "SkuAnalysis") (Some [ Read ]) "alice falls back to default"
        }

        testCaseAsync "SetTeamPermissions replaces the entire document"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            // Populate via individual ops.
            let! _ = store.SetMemberPermissions(TeamId team, UserId "alice", "M1", [ Read ])
            let! _ = store.SetTeamDefaults(TeamId team, Map.ofList [ "M2", [ Write ] ])

            // Replace wholesale.
            let replacement = {
                Defaults = Map.ofList [ "X", [ Admin ] ]
                Members = Map.ofList [ "bob", Map.ofList [ "X", [ Write ] ] ]
                Exposure = Map.empty
                // Phase 551 — no module declares a grant policy in this
                // contract's fixture, so the grant map is empty and the
                // document round-trips exactly as it did pre-551.
                Grants = Map.empty
                Pages = Map.empty
            }

            let! _ = store.SetTeamPermissions(TeamId team, replacement)
            let! perms = ok (store.GetTeamPermissions(TeamId team))

            Expect.equal perms replacement "document replaced exactly"
        }

        testCaseAsync "Team isolation — writes to team A don't affect team B"
        <| async {
            let store = factory ()
            let teamA = uniqueTeam ()
            let teamB = uniqueTeam ()

            let! _ = store.SetMemberPermissions(TeamId teamA, UserId "alice", "SkuAnalysis", [ Admin ])

            let! fromB = ok (store.GetEffectivePermissions(UserId "alice", TeamId teamB))
            Expect.isEmpty fromB "alice has no perms in team B"
        }

        testCaseAsync "Persistence round-trip: perms survive serialisation"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let original = {
                Defaults = Map.ofList [ "M1", [ Read ]; "M2", [ Read; Write ]; "M3", [ Admin ] ]
                Members = Map.ofList [ "alice", Map.ofList [ "M1", [ Admin ] ]; "bob", Map.ofList [ "M2", [] ] ]
                Exposure = Map.ofList [ "M2", ModuleExposure.Hidden; "M3", ModuleExposure.Unavailable ]
                // Phase 551 — grant records round-trip alongside the rest
                // of the document, so a store implementation that drops
                // them fails this contract rather than silently rendering
                // every policy-bearing grant inert at dispatch.
                Grants =
                    Map.ofList [
                        "alice",
                        Map.ofList [
                            "M1",
                            {
                                State = GrantState.Active
                                SatisfiedPolicy = GrantPolicy.RequiresAcknowledgement
                                Justification = "onboarding"
                                ConsentedBy = None
                            }
                        ]
                        "bob",
                        Map.ofList [
                            "M2",
                            {
                                State = GrantState.PendingConsent
                                SatisfiedPolicy = GrantPolicy.RequiresSubjectConsent
                                Justification = "pilot"
                                ConsentedBy = Some "bob"
                            }
                        ]
                    ]
                // Page-save grants round-trip too: a store that drops them
                // silently revokes every explicit page grant.
                Pages =
                    Map.ofList [
                        "alice", [ PageGrant.SaveTeam ]
                        "bob", [ PageGrant.SaveOwn; PageGrant.SavePlatform ]
                    ]
            }

            let! _ = store.SetTeamPermissions(TeamId team, original)
            let! roundTripped = ok (store.GetTeamPermissions(TeamId team))

            Expect.equal roundTripped original "round-trip preserves the document"
        }

        testCaseAsync "Module exposure (Phase 245): SetModuleExposure records the tri-state"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            // A fresh team has no exposure overrides — everything Available.
            let! initial = ok (store.GetModuleExposure(TeamId team))
            Expect.isEmpty initial "fresh team has no exposure overrides (default Available)"

            // Mark one Hidden and one Unavailable.
            let! _ = store.SetModuleExposure(TeamId team, "Marketing", ModuleExposure.Hidden)
            let! _ = store.SetModuleExposure(TeamId team, "Finance", ModuleExposure.Unavailable)
            let! afterSet = ok (store.GetModuleExposure(TeamId team))

            Expect.equal
                afterSet
                (Map.ofList [ "Marketing", ModuleExposure.Hidden; "Finance", ModuleExposure.Unavailable ])
                "both states recorded"

            // Setting a module back to Available clears its entry.
            let! _ = store.SetModuleExposure(TeamId team, "Marketing", ModuleExposure.Available)
            let! afterClear = ok (store.GetModuleExposure(TeamId team))

            Expect.equal
                afterClear
                (Map.ofList [ "Finance", ModuleExposure.Unavailable ])
                "Available clears only that module's entry"

            // Exposure is orthogonal to the permission maps.
            let! _ = store.SetTeamDefaults(TeamId team, Map.ofList [ "Finance", [ Read ] ])
            let! stillSet = ok (store.GetModuleExposure(TeamId team))

            Expect.equal
                stillSet
                (Map.ofList [ "Finance", ModuleExposure.Unavailable ])
                "editing defaults leaves exposure untouched"
        }
    ]