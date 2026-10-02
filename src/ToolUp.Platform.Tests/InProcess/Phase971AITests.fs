module ToolUp.Platform.Tests.InProcess.Phase971AITests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Tests.InProcess.DataObjectStoreTests

// ─── Phase 971 — AI: a refused legacy-config purge fails the Clear ────
//
// `LegacyAIConfigProviderProfile.wrap` reads through to a pre-42.B
// `ai-config.json` when a scope has no canonical profile. `Clear` purges
// that legacy blob so a cleared scope cannot resurrect through the
// read-through. `IBlobStorage.Delete` answers `Ok` on a missing blob, so an
// `Error` is a refusal: the legacy blob is still there and the very next
// `Get` hands the "cleared" profile back.

let private uniqueScope () =
    let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)

    {
        ScopeId = "team-971-" + suffix
        Container = "team-971-" + suffix
        Persist = true
    }

let private legacyJson =
    """{"ConfiguredProviders":[{"Label":"mine","ProviderId":"anthropic","Model":null,"SecretKeyName":"ai-key-mine","UpdatedAt":"2026-01-01T00:00:00Z"}],"ActiveProviderLabel":"mine","PlatformModelOverride":null,"UpdatedAt":"2026-01-01T00:00:00Z"}"""

/// A wrapped profile over `blob`, with a legacy `ai-config.json` seeded in
/// `scope` and the read-through proven to see it.
let private seeded (blob: IBlobStorage) (scope: StorageScope) = async {
    let profile =
        LegacyAIConfigProviderProfile.wrap blob (BlobProviderProfile.create blob)

    match! blob.Upload(scope.Container, "ai-config.json", Encoding.UTF8.GetBytes legacyJson) with
    | Ok _ -> ()
    | Error e -> failtestf "seed upload failed: %s" e

    let! before = profile.Get scope
    Expect.isSome before "the legacy read-through resolves the seeded profile"
    return profile
}

let tests =
    testList "Phase 971 - AI" [
        testAsync "Clear raises when the legacy ai-config.json purge is refused, and the profile still resolves" {
            let blob =
                DeleteRefusingBlobStorage(InMemoryBlobStorage(), fun n -> n = "ai-config.json") :> IBlobStorage

            let scope = uniqueScope ()
            let! profile = seeded blob scope

            match! Async.Catch(profile.Clear scope) with
            | Choice1Of2() -> failtest "a refused legacy purge must not read as a completed Clear"
            | Choice2Of2 _ -> ()

            let! after = profile.Get scope
            Expect.isSome after "the kept legacy blob still resurrects the profile, so a re-run must purge it"
        }

        testAsync "Clear over healthy storage leaves nothing for the read-through to resurrect" {
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let scope = uniqueScope ()
            let! profile = seeded blob scope

            do! profile.Clear scope

            let! after = profile.Get scope
            Expect.isNone after "a cleared scope resolves no profile"
        }
    ]