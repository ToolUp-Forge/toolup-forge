module ToolUp.Platform.Tests.InProcess.BlobProviderProfileTests

open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

// Binds the IProviderProfile contract pack (Phase 42.B) to the
// default blob-backed impl over LocalFileStorage rooted in a fresh
// temp directory per factory call — same shape as ShareTokenStoreTests.
// The factory has no teardown hook, so each directory outlives its case;
// the family name attributes it in the census.

let private factory () =
    let root = TempDir.create "providerprofile-tests"
    let storage = LocalFileStorage.LocalFileStorage(root.Path) :> IBlobStorage
    BlobProviderProfile.create storage

let tests = IProviderProfileContract.tests "BlobProviderProfile" factory