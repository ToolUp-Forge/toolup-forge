module ToolUp.Platform.Tests.InProcess.EncryptedBlobStorageTests

open System
open System.IO
open System.Text
open Expecto
open Microsoft.Extensions.Caching.Memory
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption
open ToolUp.Platform.EncryptedBlobStorage
open ToolUp.Platform.Secrets

// ─── Phase 22 EncryptedBlobStorage tests ────────────────────────────
//
// Round-trip + scope-isolation + crypto-shred + key-rotation
// verification for both shipped resolvers (`SingleKeyResolver`,
// `PerScopeKeyResolver`). Uses `LocalFileStorage` over a temp dir as
// the inner storage and `FileSecretStore` over a temp dir for
// `ISecretStore`. Mirrors the testing pattern in `LocalFileStorageTests.fs`
// and `FileSecretStoreTests.fs`.

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-enc-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private newSecretStore () : ISecretStore =
    FileSecretStore.FileSecretStore(baseDir = newTempDir ()) :> ISecretStore

let private newInnerStorage () : IBlobStorage =
    LocalFileStorage.LocalFileStorage(newTempDir ()) :> IBlobStorage

let private newCache () : IMemoryCache =
    new MemoryCache(MemoryCacheOptions()) :> IMemoryCache

let private samplePayload =
    Encoding.UTF8.GetBytes "the quick brown fox jumps over the lazy dog"

let private container = "team-test-scope"

let tests =
    testList "EncryptedBlobStorage" [

        testCaseAsync "SingleKeyResolver round-trip preserves bytes"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! upload = storage.Upload(container, "doc.bin", samplePayload)
            Expect.isOk upload "upload succeeds"

            let! download = storage.Download(container, "doc.bin")

            match download with
            | Result.Ok bytes -> Expect.equal bytes samplePayload "decrypted bytes match plaintext"
            | Result.Error e -> failwithf "download failed: %s" e
        }

        testCaseAsync "SingleKeyResolver — inner storage holds ciphertext (not plaintext)"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            // Read raw envelope bytes directly from the inner storage,
            // bypassing the decorator. The first 4 bytes must be the
            // EncryptionEnvelope.Magic prefix and the bytes must NOT
            // equal the plaintext.
            let! rawResult = inner.Download(container, "doc.bin")

            match rawResult with
            | Result.Ok envelope ->
                Expect.notEqual envelope samplePayload "stored bytes are not plaintext"
                Expect.isGreaterThan envelope.Length samplePayload.Length "envelope adds magic+nonce+tag overhead"
                Expect.equal envelope[0] EncryptionEnvelope.Magic[0] "envelope starts with magic byte 0"
                Expect.equal envelope[1] EncryptionEnvelope.Magic[1] "envelope starts with magic byte 1"
                Expect.equal envelope[2] EncryptionEnvelope.Magic[2] "envelope starts with magic byte 2"
                Expect.equal envelope[3] EncryptionEnvelope.Magic[3] "envelope starts with magic byte 3"
            | Result.Error e -> failwithf "raw download failed: %s" e
        }

        testCaseAsync "PerScopeKeyResolver round-trip preserves bytes"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create secrets cache None
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! upload = storage.Upload(container, "doc.bin", samplePayload)
            Expect.isOk upload "upload succeeds"

            let! download = storage.Download(container, "doc.bin")

            match download with
            | Result.Ok bytes -> Expect.equal bytes samplePayload "decrypted bytes match plaintext"
            | Result.Error e -> failwithf "download failed: %s" e
        }

        testCaseAsync "PerScopeKeyResolver — different scopes get different keys"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create secrets cache None

            let teamA = "team-aaa"
            let teamB = "team-bbb"

            let scopeA = {
                Container = teamA
                ScopeId = "aaa"
                Persist = true
            }

            let scopeB = {
                Container = teamB
                ScopeId = "bbb"
                Persist = true
            }

            let resolverIface = resolver :> IBlobEncryptionKeyResolver
            let! keyA = resolverIface.ResolveKey scopeA
            let! keyB = resolverIface.ResolveKey scopeB

            Expect.notEqual keyA.KeyId keyB.KeyId "scopes get different KeyIds"
            Expect.notEqual keyA.Material keyB.Material "scopes get different key material"
            Expect.equal keyA.Material.Length 32 "AES-256 key length"
            Expect.equal keyB.Material.Length 32 "AES-256 key length"
        }

        testCaseAsync "PerScopeKeyResolver — DestroyKey makes blob undecryptable"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            use cache = newCache ()
            let resolver = PerScopeKeyResolver.create secrets cache None
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            // Destroy the scope's key.
            let! destroyResult = resolver.DestroyKey(container.Substring 5, "test-actor")
            Expect.isOk destroyResult "destroy succeeded"

            // Subsequent download should fail with a KeyDestroyed-shaped error.
            let! download = storage.Download(container, "doc.bin")

            match download with
            | Result.Ok _ -> failtest "expected error after key destruction; got Ok"
            | Result.Error msg -> Expect.stringContains msg "destroyed" "error message mentions destruction"
        }

        testCaseAsync "PerScopeKeyResolver — keys persist across resolver restarts"
        <| async {
            let secrets = newSecretStore ()
            let inner = newInnerStorage ()

            // First resolver instance encrypts.
            let resolver1 = PerScopeKeyResolver.create secrets (newCache ()) None
            let storage1 = EncryptedBlobStorage(inner, resolver1) :> IBlobStorage
            let! _ = storage1.Upload(container, "doc.bin", samplePayload)

            // Second resolver instance reads (cold cache) — the key is
            // persisted, so the second instance recovers it.
            let resolver2 = PerScopeKeyResolver.create secrets (newCache ()) None
            let storage2 = EncryptedBlobStorage(inner, resolver2) :> IBlobStorage
            let! download = storage2.Download(container, "doc.bin")

            match download with
            | Result.Ok bytes -> Expect.equal bytes samplePayload "second resolver recovers the persisted key"
            | Result.Error e -> failwithf "second resolver download failed: %s" e
        }

        testCaseAsync "Pass-through ops — Exists / GetMetadata / List / Delete bypass crypto"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            let! exists = storage.Exists(container, "doc.bin")
            Expect.isTrue exists "Exists confirms upload"

            let! metaResult = storage.GetMetadata(container, "doc.bin")

            match metaResult with
            | Result.Ok meta ->
                Expect.isGreaterThan
                    meta.Size
                    (int64 samplePayload.Length)
                    "metadata Size reflects ciphertext (envelope overhead)"
            | Result.Error e -> failwithf "GetMetadata failed: %s" e

            let! listed = storage.List(container, "")
            Expect.contains listed "doc.bin" "List returns the uploaded blob"

            let! deleteResult = storage.Delete(container, "doc.bin")
            Expect.isOk deleteResult "Delete succeeds"

            let! existsAfter = storage.Exists(container, "doc.bin")
            Expect.isFalse existsAfter "Exists confirms deletion"
        }

        testCaseAsync "Envelope decryption fails when ciphertext is tampered"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            // Tamper: read the ciphertext back, flip a byte well past the
            // header (somewhere in the ciphertext region), write it back.
            let! rawResult = inner.Download(container, "doc.bin")

            match rawResult with
            | Result.Ok envelope ->
                let tampered = Array.copy envelope
                let tamperOffset = envelope.Length - 4 // late ciphertext byte
                tampered[tamperOffset] <- tampered[tamperOffset] ^^^ 0xFFuy

                let! _ = inner.Upload(container, "doc.bin", tampered)

                // Decrypt should fail (AES-GCM tag mismatch).
                let! download = storage.Download(container, "doc.bin")

                match download with
                | Result.Ok _ -> failtest "expected error after tamper; got Ok"
                | Result.Error msg ->
                    Expect.stringContains (msg.ToLowerInvariant()) "decrypt" "error message mentions decryption failure"
            | Result.Error e -> failwithf "raw fetch failed: %s" e
        }
    ]