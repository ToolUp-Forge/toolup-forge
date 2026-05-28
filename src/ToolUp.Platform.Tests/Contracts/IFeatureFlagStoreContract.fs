module ToolUp.Platform.Tests.Contracts.IFeatureFlagStoreContract

open System
open Expecto
open ToolUp.Platform

/// Contract test list for any `IFeatureFlagStore` implementation.
/// Factory produces a fresh, empty store per test. Tests GUID-suffix
/// the scope ids they write to so implementations that share
/// underlying state (e.g. shared temp dir across a test run) stay
/// isolated from each other.
///
/// The store's load-bearing correctness properties are:
///   * scope isolation — a team-A write never appears in a team-B read;
///   * kind isolation — a user with the same id string as a team is
///     still a different scope on the wire (the `FlagScope` DU's whole
///     reason to exist);
///   * DU round-trip — `FlagValue` survives `FableJsonConverter`
///     serialisation for both `Bool` and `Variant` shapes.
let tests (name: string) (factory: unit -> IFeatureFlagStore) =
    let uniqueId prefix =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        prefix + "-" + suffix

    let uniqueTeam () = FlagScope.Team(uniqueId "team")
    let uniqueUser () = FlagScope.User(uniqueId "user")

    testList $"{name} — IFeatureFlagStore contract" [
        testCaseAsync "GetFlag on untouched scope returns None"
        <| async {
            let store = factory ()
            let! v = store.GetFlag(uniqueTeam (), "any.key")
            Expect.isNone v "no flag document → no value"
        }

        testCaseAsync "ListFlags on untouched scope returns empty map"
        <| async {
            let store = factory ()
            let! m = store.ListFlags(uniqueTeam ())
            Expect.isEmpty m "no flag document → empty map"
        }

        testCaseAsync "SetFlag + GetFlag round-trip — Bool"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()

            match! store.SetFlag(scope, "mymodule.new-ui", FlagValue.Bool true) with
            | Error e -> failtestf "SetFlag failed: %s" e
            | Ok() -> ()

            let! v = store.GetFlag(scope, "mymodule.new-ui")
            Expect.equal v (Some(FlagValue.Bool true)) "Bool true survives round-trip"
        }

        testCaseAsync "SetFlag + GetFlag round-trip — Variant"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()
            let value = FlagValue.Variant([ "legacy"; "new"; "beta" ], "new")

            match! store.SetFlag(scope, "mymodule.theme", value) with
            | Error e -> failtestf "SetFlag failed: %s" e
            | Ok() -> ()

            let! v = store.GetFlag(scope, "mymodule.theme")
            Expect.equal v (Some value) "Variant shape survives round-trip"
        }

        testCaseAsync "SetFlag overwrites an existing value at the same scope/key"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()

            let! _ = store.SetFlag(scope, "k", FlagValue.Bool false)
            let! _ = store.SetFlag(scope, "k", FlagValue.Bool true)

            let! v = store.GetFlag(scope, "k")
            Expect.equal v (Some(FlagValue.Bool true)) "last write wins"
        }

        testCaseAsync "SetFlag of a second key preserves the first"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()

            let! _ = store.SetFlag(scope, "a", FlagValue.Bool true)
            let! _ = store.SetFlag(scope, "b", FlagValue.Bool false)

            let! m = store.ListFlags scope
            Expect.equal (Map.tryFind "a" m) (Some(FlagValue.Bool true)) "a retained"
            Expect.equal (Map.tryFind "b" m) (Some(FlagValue.Bool false)) "b added"
        }

        testCaseAsync "ClearFlag removes just one key; other keys survive"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()

            let! _ = store.SetFlag(scope, "a", FlagValue.Bool true)
            let! _ = store.SetFlag(scope, "b", FlagValue.Bool true)

            do! store.ClearFlag(scope, "a")

            let! m = store.ListFlags scope
            Expect.isFalse (Map.containsKey "a" m) "a cleared"
            Expect.equal (Map.tryFind "b" m) (Some(FlagValue.Bool true)) "b untouched"
        }

        testCaseAsync "ClearFlag on a non-existent key is a no-op"
        <| async {
            let store = factory ()
            let scope = uniqueTeam ()

            // Clearing a key on an empty / missing document must not
            // throw — idempotence is part of the contract.
            do! store.ClearFlag(scope, "nonexistent")

            let! m = store.ListFlags scope
            Expect.isEmpty m "document still empty"
        }

        testCaseAsync "Scope isolation — team-A writes don't appear in team-B reads"
        <| async {
            let store = factory ()
            let teamA = uniqueTeam ()
            let teamB = uniqueTeam ()

            let! _ = store.SetFlag(teamA, "shared.key", FlagValue.Bool true)

            let! fromB = store.GetFlag(teamB, "shared.key")
            Expect.isNone fromB "team B sees nothing"

            let! listB = store.ListFlags teamB
            Expect.isEmpty listB "team B list empty"
        }

        testCaseAsync "Kind isolation — User 'x' and Team 'x' are different scopes"
        <| async {
            let store = factory ()
            // Deliberately identical id string across two FlagScope
            // kinds: this is the scenario the `FlagScope` DU exists to
            // prevent silent collision on. A `string` scopeId would
            // alias these two; the typed DU must not.
            let shared = Guid.NewGuid().ToString("N").Substring(0, 8)
            let userScope = FlagScope.User shared
            let teamScope = FlagScope.Team shared

            let! _ = store.SetFlag(userScope, "k", FlagValue.Bool true)

            let! fromTeam = store.GetFlag(teamScope, "k")
            Expect.isNone fromTeam "Team scope does not see User's write with the same id string"
        }

        testCaseAsync "Platform scope is independent of team scopes"
        <| async {
            let store = factory ()
            let team = uniqueTeam ()

            let! _ = store.SetFlag(FlagScope.Platform, "k", FlagValue.Bool true)

            // Team scope doesn't inherit platform values at the store
            // layer — that walk is FlagEvaluator's job, not the store's.
            let! fromTeam = store.GetFlag(team, "k")
            Expect.isNone fromTeam "team scope does not leak platform values at the store layer"

            let! fromPlatform = store.GetFlag(FlagScope.Platform, "k")
            Expect.equal fromPlatform (Some(FlagValue.Bool true)) "platform write readable at platform scope"
        }

        testCaseAsync "Persistence round-trip: mixed Bool + Variant map"
        <| async {
            let store = factory ()
            let scope = uniqueUser ()

            let! _ = store.SetFlag(scope, "bool-key", FlagValue.Bool true)
            let! _ = store.SetFlag(scope, "variant-key", FlagValue.Variant([ "a"; "b" ], "b"))

            let! m = store.ListFlags scope
            Expect.equal (Map.tryFind "bool-key" m) (Some(FlagValue.Bool true)) "bool retained"

            Expect.equal (Map.tryFind "variant-key" m) (Some(FlagValue.Variant([ "a"; "b" ], "b"))) "variant retained"
        }
    ]