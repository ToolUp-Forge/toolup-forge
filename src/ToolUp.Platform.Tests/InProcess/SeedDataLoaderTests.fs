module ToolUp.Platform.Tests.InProcess.SeedDataLoaderTests

open System.Text
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 447 — SeedDataLoader ──────────────────────────────────────
//
// Exercises the loader against a real `IServiceProvider` (packs +
// stores resolved exactly as at compose time) over the hermetic
// `InMemoryBlobStorage`: idempotency (applied-marker), version-bump
// re-apply, mode gating (`NoSeedData` no-op, Team-shape refusal, forced
// override), and target-scope isolation.

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

/// Counting pack — records how many times `Apply` ran so idempotency and
/// re-apply are observable without any store wiring.
type private CountingSeedPack(name: string, version: string, counter: int ref) =
    interface ISeedPack with
        member _.Name = name
        member _.Version = version

        member _.Apply(_ctx) = async {
            counter.Value <- counter.Value + 1

            return {
                PackName = name
                Version = version
                ItemsSeeded = 1
                Notes = []
            }
        }

/// Pack that writes a demo blob into the scope the loader handed it
/// (`ctx.ScopeId`) — lets a test assert the seed lands in the target
/// scope's container and nowhere else.
type private BlobWritingSeedPack(name: string, version: string) =
    interface ISeedPack with
        member _.Name = name
        member _.Version = version

        member _.Apply(ctx) = async {
            let! _ = ctx.BlobStorage.Upload(ctx.ScopeId, "seeded/demo.json", Encoding.UTF8.GetBytes "{}")

            return {
                PackName = name
                Version = version
                ItemsSeeded = 1
                Notes = [ "demo.json" ]
            }
        }

let private logger = SilentLogger() :> ILogger

let private providerFor (blob: IBlobStorage) (packs: ISeedPack list) : System.IServiceProvider =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(blob) |> ignore

    for pack in packs do
        services.AddSingleton<ISeedPack>(pack) |> ignore

    services.BuildServiceProvider() :> System.IServiceProvider

let private enabledAnon = {
    ServerConfig.defaults with
        SeedData = EnabledSeedData
}

let private enabledTeam = {
    ServerConfig.defaults with
        SeedData = EnabledSeedData
        Surfaces = Surfaces.team
}

let private forcedTeam = {
    ServerConfig.defaults with
        SeedData = ForcedSeedData
        Surfaces = Surfaces.team
}

let tests =
    testList "SeedDataLoader" [

        testCase "applies a registered pack once; a second boot is a no-op (idempotent)"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let counter = ref 0
            let pack = CountingSeedPack("demo", "1", counter) :> ISeedPack
            let provider = providerFor blob [ pack ]

            SeedDataLoader.runIfEnabled provider enabledAnon blob logger
            SeedDataLoader.runIfEnabled provider enabledAnon blob logger

            Expect.equal counter.Value 1 "pack Apply ran exactly once across two boots"

            let markerExists = blob.Exists("_platform", "seed/demo@1") |> Async.RunSynchronously

            Expect.isTrue markerExists "applied-marker blob was written"

        testCase "a version bump re-applies the pack"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let counter = ref 0

            let v1 = CountingSeedPack("demo", "1", counter) :> ISeedPack
            SeedDataLoader.runIfEnabled (providerFor blob [ v1 ]) enabledAnon blob logger
            Expect.equal counter.Value 1 "v1 applied"

            // Same pack name, bumped version → distinct marker → re-apply.
            let v2 = CountingSeedPack("demo", "2", counter) :> ISeedPack
            SeedDataLoader.runIfEnabled (providerFor blob [ v2 ]) enabledAnon blob logger
            Expect.equal counter.Value 2 "v2 re-applied on the version bump"

        testCase "NoSeedData (default) applies nothing"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let counter = ref 0
            let pack = CountingSeedPack("demo", "1", counter) :> ISeedPack

            // ServerConfig.defaults has SeedData = NoSeedData.
            SeedDataLoader.runIfEnabled (providerFor blob [ pack ]) ServerConfig.defaults blob logger

            Expect.equal counter.Value 0 "no pack applied under NoSeedData"

        testCase "EnabledSeedData on a Team shape refuses startup"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let counter = ref 0
            let pack = CountingSeedPack("demo", "1", counter) :> ISeedPack
            let provider = providerFor blob [ pack ]

            Expect.throws
                (fun () -> SeedDataLoader.runIfEnabled provider enabledTeam blob logger)
                "seeding a Team/multi-team production shape without the force flag must refuse"

            Expect.equal counter.Value 0 "no pack applied when refused"

        testCase "ForcedSeedData applies even on a Team shape"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let counter = ref 0
            let pack = CountingSeedPack("demo", "1", counter) :> ISeedPack

            SeedDataLoader.runIfEnabled (providerFor blob [ pack ]) forcedTeam blob logger

            Expect.equal counter.Value 1 "forced override applies on a Team shape"

        testCase "seeded data lands in the pack's target scope and does not leak cross-container"
        <| fun () ->
            let blob = InMemoryBlobStorage() :> IBlobStorage
            let pack = BlobWritingSeedPack("blobs", "1") :> ISeedPack

            SeedDataLoader.runIfEnabled (providerFor blob [ pack ]) enabledAnon blob logger

            let inTargetScope =
                blob.Exists("_platform", "seeded/demo.json") |> Async.RunSynchronously

            let inOtherScope =
                blob.Exists("team-other", "seeded/demo.json") |> Async.RunSynchronously

            Expect.isTrue inTargetScope "seed landed in the target (_platform) scope container"
            Expect.isFalse inOtherScope "seed did not leak into another scope's container"
    ]