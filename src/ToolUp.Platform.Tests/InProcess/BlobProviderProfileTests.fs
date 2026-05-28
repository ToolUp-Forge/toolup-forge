module ToolUp.Platform.Tests.InProcess.BlobProviderProfileTests

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts

// Binds the IProviderProfile contract pack (Phase 42.B) to the
// default blob-backed impl over LocalFileStorage rooted in a fresh
// temp directory per factory call — same shape as ShareTokenStoreTests.

let private factory () =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-providerprofile-tests-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(root) |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    BlobProviderProfile.create storage

let tests = IProviderProfileContract.tests "BlobProviderProfile" factory