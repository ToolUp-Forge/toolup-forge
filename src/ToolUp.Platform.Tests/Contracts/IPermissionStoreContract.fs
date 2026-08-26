module ToolUp.Platform.Tests.Contracts.IPermissionStoreContract

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.PermissionStore

// ModulePermission carries `[<RequireQualifiedAccess>]` because its
// `Admin` case collides with TeamRole.Admin (different concepts).
// Use shorthand local bindings so test assertions stay readable;
// every mention is still unambiguously ModulePermission-typed.
let private Read = ModulePermission.Read
let private Write = ModulePermission.Write
let private Admin = ModulePermission.Admin

/// Contract test list for any `IPermissionStore` implementation.
/// Factory produces a fresh, empty store per test. Tests GUID-suffix
/// their team IDs so implementations that share underlying state stay
/// isolated.
///
/// The store's load-bearing correctness property is the effective-
/// permission merge: a user with an explicit per-module entry sees
/// that; a user without sees the team default; a user for a module
/// with neither sees nothing (no-access).
let tests (name: string) (factory: unit -> IPermissionStore) =
    let uniqueTeam () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "team-" + suffix

    testList $"{name} — IPermissionStore contract" [
        testCaseAsync "GetTeamPermissions on untouched team returns empty"
        <| async {
            let store = factory ()
            let! perms = store.GetTeamPermissions(uniqueTeam ())
            Expect.isEmpty perms.Defaults "no defaults configured"
            Expect.isEmpty perms.Members "no members configured"
        }

        testCaseAsync "GetEffectivePermissions on untouched team returns empty (unrestricted)"
        <| async {
            let store = factory ()
            let! perms = store.GetEffectivePermissions("alice", uniqueTeam ())
            // Empty map = unrestricted via AccessContext.canAccessModule.
            Expect.isEmpty perms "no perms configured"
        }

        testCaseAsync "SetMemberPermissions round-trips"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            match! store.SetMemberPermissions(team, "alice", "SkuAnalysis", [ Read; Write ]) with
            | Error e -> failtestf "SetMemberPermissions failed: %s" e
            | Ok() -> ()

            let! perms = store.GetEffectivePermissions("alice", team)
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

            match! store.SetTeamDefaults(team, defaults) with
            | Error e -> failtestf "SetTeamDefaults failed: %s" e
            | Ok() -> ()

            let! perms = store.GetEffectivePermissions("bob", team)
            Expect.equal (perms.TryFind "SkuAnalysis") (Some [ Read ]) "bob inherits default"
        }

        testCaseAsync "Explicit member entries override defaults"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let! _ = store.SetTeamDefaults(team, Map.ofList [ "SkuAnalysis", [ Read ] ])

            let! _ = store.SetMemberPermissions(team, "alice", "SkuAnalysis", [ Admin ])

            let! alicePerms = store.GetEffectivePermissions("alice", team)
            Expect.equal (alicePerms.TryFind "SkuAnalysis") (Some [ Admin ]) "alice's explicit entry wins"

            let! bobPerms = store.GetEffectivePermissions("bob", team)
            Expect.equal (bobPerms.TryFind "SkuAnalysis") (Some [ Read ]) "bob still gets default"
        }

        testCaseAsync "Empty permission list on SetMemberPermissions removes the entry"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let! _ = store.SetTeamDefaults(team, Map.ofList [ "SkuAnalysis", [ Read ] ])
            let! _ = store.SetMemberPermissions(team, "alice", "SkuAnalysis", [ Admin ])

            // Revoke alice's explicit override — she should fall back
            // to the team default.
            let! _ = store.SetMemberPermissions(team, "alice", "SkuAnalysis", [])

            let! perms = store.GetEffectivePermissions("alice", team)
            Expect.equal (perms.TryFind "SkuAnalysis") (Some [ Read ]) "alice falls back to default"
        }

        testCaseAsync "SetTeamPermissions replaces the entire document"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            // Populate via individual ops.
            let! _ = store.SetMemberPermissions(team, "alice", "M1", [ Read ])
            let! _ = store.SetTeamDefaults(team, Map.ofList [ "M2", [ Write ] ])

            // Replace wholesale.
            let replacement = {
                Defaults = Map.ofList [ "X", [ Admin ] ]
                Members = Map.ofList [ "bob", Map.ofList [ "X", [ Write ] ] ]
                Exposure = Map.empty
                // Phase 551 — no module declares a grant policy in this
                // contract's fixture, so the grant map is empty and the
                // document round-trips exactly as it did pre-551.
                Grants = Map.empty
            }

            let! _ = store.SetTeamPermissions(team, replacement)
            let! perms = store.GetTeamPermissions team

            Expect.equal perms replacement "document replaced exactly"
        }

        testCaseAsync "Team isolation — writes to team A don't affect team B"
        <| async {
            let store = factory ()
            let teamA = uniqueTeam ()
            let teamB = uniqueTeam ()

            let! _ = store.SetMemberPermissions(teamA, "alice", "SkuAnalysis", [ Admin ])

            let! fromB = store.GetEffectivePermissions("alice", teamB)
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
            }

            let! _ = store.SetTeamPermissions(team, original)
            let! roundTripped = store.GetTeamPermissions team

            Expect.equal roundTripped original "round-trip preserves the document"
        }

        testCaseAsync "Module exposure (Phase 245): SetModuleExposure records the tri-state"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            // A fresh team has no exposure overrides — everything Available.
            let! initial = store.GetModuleExposure team
            Expect.isEmpty initial "fresh team has no exposure overrides (default Available)"

            // Mark one Hidden and one Unavailable.
            let! _ = store.SetModuleExposure(team, "Marketing", ModuleExposure.Hidden)
            let! _ = store.SetModuleExposure(team, "Finance", ModuleExposure.Unavailable)
            let! afterSet = store.GetModuleExposure team

            Expect.equal
                afterSet
                (Map.ofList [ "Marketing", ModuleExposure.Hidden; "Finance", ModuleExposure.Unavailable ])
                "both states recorded"

            // Setting a module back to Available clears its entry.
            let! _ = store.SetModuleExposure(team, "Marketing", ModuleExposure.Available)
            let! afterClear = store.GetModuleExposure team

            Expect.equal
                afterClear
                (Map.ofList [ "Finance", ModuleExposure.Unavailable ])
                "Available clears only that module's entry"

            // Exposure is orthogonal to the permission maps.
            let! _ = store.SetTeamDefaults(team, Map.ofList [ "Finance", [ Read ] ])
            let! stillSet = store.GetModuleExposure team

            Expect.equal
                stillSet
                (Map.ofList [ "Finance", ModuleExposure.Unavailable ])
                "editing defaults leaves exposure untouched"
        }
    ]