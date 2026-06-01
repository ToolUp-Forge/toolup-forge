module ToolUp.Platform.Tests.Contracts.IPlatformAIKeyStoreContract

open Expecto
open ToolUp.AI

// ─── IPlatformAIKeyStore contract pack (Phase 70) ────────────────
//
// Parametrised tests for any `IPlatformAIKeyStore` implementation.
// Each test asks the factory for a fresh `store` so concurrent runs
// against a shared substrate cannot interfere.
//
// Coverage targets the interface contract — get / set / delete /
// has — across both scopes (platform + team) with the cross-scope
// isolation invariants the factory's resolution chain relies on:
//
//   * A platform-scope write is invisible to team-scope reads.
//   * A team-scope write to team A is invisible to team B.
//   * Deletes are idempotent (no-op when nothing recorded).
//   * Has* matches Get*.IsSome at the same point in time.
//   * Set* over-writes are idempotent (same value before / after).

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error err -> failtestf "%s: expected Ok, got %A" label err

let tests (name: string) (factory: unit -> IPlatformAIKeyStore) =
    testList $"{name} — IPlatformAIKeyStore contract" [

        // ─── Platform-scope semantics ─────────────────────────────

        testCaseAsync "GetPlatformKey on empty store returns None"
        <| async {
            let store = factory ()
            let! key = store.GetPlatformKey "anthropic-claude"
            Expect.isNone key "no key recorded"
        }

        testCaseAsync "SetPlatformKey then GetPlatformKey round-trips the value"
        <| async {
            let store = factory ()
            let! setResult = store.SetPlatformKey("anthropic-claude", "sk-ant-test-1")
            okOrFail "SetPlatformKey" setResult
            let! key = store.GetPlatformKey "anthropic-claude"
            Expect.equal key (Some "sk-ant-test-1") "key value matches"
        }

        testCaseAsync "HasPlatformKey reflects GetPlatformKey.IsSome"
        <| async {
            let store = factory ()
            let! before = store.HasPlatformKey "openai"
            Expect.isFalse before "no key before write"
            let! _ = store.SetPlatformKey("openai", "sk-test")
            let! after = store.HasPlatformKey "openai"
            Expect.isTrue after "key present after write"
        }

        testCaseAsync "DeletePlatformKey clears the value"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("openai", "sk-test")
            let! delResult = store.DeletePlatformKey "openai"
            okOrFail "DeletePlatformKey" delResult
            let! key = store.GetPlatformKey "openai"
            Expect.isNone key "key absent after delete"
        }

        testCaseAsync "DeletePlatformKey on absent key is idempotent"
        <| async {
            let store = factory ()
            // Delete twice — second call must also succeed.
            let! d1 = store.DeletePlatformKey "never-was-here"
            okOrFail "DeletePlatformKey first" d1
            let! d2 = store.DeletePlatformKey "never-was-here"
            okOrFail "DeletePlatformKey second" d2
        }

        testCaseAsync "SetPlatformKey overwrites existing value idempotently"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("anthropic-claude", "sk-v1")
            let! _ = store.SetPlatformKey("anthropic-claude", "sk-v2")
            let! key = store.GetPlatformKey "anthropic-claude"
            Expect.equal key (Some "sk-v2") "second value wins"
        }

        testCaseAsync "Platform-scope keys do not collide across providers"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("anthropic-claude", "sk-ant")
            let! _ = store.SetPlatformKey("openai", "sk-oai")
            let! anthropic = store.GetPlatformKey "anthropic-claude"
            let! openai = store.GetPlatformKey "openai"
            Expect.equal anthropic (Some "sk-ant") "anthropic key unchanged"
            Expect.equal openai (Some "sk-oai") "openai key unchanged"
        }

        // ─── Team-scope semantics ─────────────────────────────────

        testCaseAsync "GetTeamKey on empty store returns None"
        <| async {
            let store = factory ()
            let! key = store.GetTeamKey("team-alpha", "anthropic-claude")
            Expect.isNone key "no key recorded"
        }

        testCaseAsync "SetTeamKey then GetTeamKey round-trips the value"
        <| async {
            let store = factory ()
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "sk-team-alpha")
            let! key = store.GetTeamKey("team-alpha", "anthropic-claude")
            Expect.equal key (Some "sk-team-alpha") "team key value matches"
        }

        testCaseAsync "HasTeamKey reflects GetTeamKey.IsSome"
        <| async {
            let store = factory ()
            let! before = store.HasTeamKey("team-alpha", "openai")
            Expect.isFalse before "no key before write"
            let! _ = store.SetTeamKey("team-alpha", "openai", "sk-team")
            let! after = store.HasTeamKey("team-alpha", "openai")
            Expect.isTrue after "key present after write"
        }

        testCaseAsync "DeleteTeamKey clears only the targeted team's value"
        <| async {
            let store = factory ()
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "alpha-key")
            let! _ = store.SetTeamKey("team-beta", "anthropic-claude", "beta-key")
            let! _ = store.DeleteTeamKey("team-alpha", "anthropic-claude")
            let! alphaKey = store.GetTeamKey("team-alpha", "anthropic-claude")
            let! betaKey = store.GetTeamKey("team-beta", "anthropic-claude")
            Expect.isNone alphaKey "alpha key cleared"
            Expect.equal betaKey (Some "beta-key") "beta key untouched"
        }

        testCaseAsync "Team-scope writes do not bleed across teams"
        <| async {
            let store = factory ()
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "alpha-only")
            let! beta = store.GetTeamKey("team-beta", "anthropic-claude")
            Expect.isNone beta "team-beta has no key for the same provider"
        }

        // ─── Cross-scope isolation invariants ─────────────────────

        testCaseAsync "Platform-scope write is invisible to team-scope reads"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("anthropic-claude", "platform-default")
            let! teamKey = store.GetTeamKey("team-alpha", "anthropic-claude")
            Expect.isNone teamKey "platform-scope key does not surface as team-scope"
        }

        testCaseAsync "Team-scope write is invisible to platform-scope reads"
        <| async {
            let store = factory ()
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "team-override")
            let! platformKey = store.GetPlatformKey "anthropic-claude"
            Expect.isNone platformKey "team-scope key does not surface as platform-scope"
        }

        testCaseAsync "Both scopes can carry a key for the same provider simultaneously"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("anthropic-claude", "platform-default")
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "team-override")
            let! platformKey = store.GetPlatformKey "anthropic-claude"
            let! teamKey = store.GetTeamKey("team-alpha", "anthropic-claude")
            Expect.equal platformKey (Some "platform-default") "platform key unchanged"
            Expect.equal teamKey (Some "team-override") "team key unchanged"
        }

        testCaseAsync "Deleting platform key does not affect team-scope keys"
        <| async {
            let store = factory ()
            let! _ = store.SetPlatformKey("anthropic-claude", "platform-default")
            let! _ = store.SetTeamKey("team-alpha", "anthropic-claude", "team-override")
            let! _ = store.DeletePlatformKey "anthropic-claude"
            let! teamKey = store.GetTeamKey("team-alpha", "anthropic-claude")
            Expect.equal teamKey (Some "team-override") "team key survives platform delete"
        }
    ]