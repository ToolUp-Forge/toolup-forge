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
open DataManagementTypes

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

        testCaseAsync "DownloadRange is refused (Phase 455 honest-refusal verdict)"
        <| async {
            // Whole-blob AES-GCM: a mid-blob ciphertext range is
            // undecryptable, so the decorator must refuse rather than
            // silently materialise + decrypt the whole blob.
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "doc.bin", samplePayload)

            match! storage.DownloadRange(container, "doc.bin", 0L, 4) with
            | Result.Ok _ -> failtest "Expected the encryption decorator to refuse ranged reads"
            | Result.Error msg -> Expect.stringContains msg "ranged reads" "refusal names the unsupported operation"

            // Whole-blob Download remains the supported read path.
            match! storage.Download(container, "doc.bin") with
            | Result.Ok bytes -> Expect.equal bytes samplePayload "Download still round-trips"
            | Result.Error e -> failwithf "download failed: %s" e
        }

        testCaseAsync "ComposeFrom is refused, and refused as NotSupported (Phase 741)"
        <| async {
            // The 455 refusal one verb along. Each part is its own
            // whole-blob AES-GCM envelope, so the concatenation of the
            // envelopes is not the envelope of the concatenation — a
            // composed target would `Download` as an unparseable
            // envelope, i.e. a corrupt object rather than a loud
            // failure.
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let storage = EncryptedBlobStorage(inner, resolver) :> IBlobStorage

            let! _ = storage.Upload(container, "part-0", samplePayload)
            let! _ = storage.Upload(container, "part-1", samplePayload)

            Expect.isFalse storage.CanComposeFrom "the decorator declares the refusal before it is called"

            match! storage.ComposeFrom(container, "joined.bin", [ "part-0"; "part-1" ]) with
            | Result.Ok _ -> failtest "Expected the encryption decorator to refuse compose"
            | Result.Error(ComposeRefusal.ComposeFailed message) ->
                failtestf "Expected NotSupported (a fallback signal), got an operational failure: %s" message
            | Result.Error(ComposeRefusal.NotSupported reason) ->
                Expect.stringContains reason "compose" "refusal names the unsupported operation"

            // And the refusal must not have written anything — a caller
            // that falls back must not find a corrupt target waiting.
            let! exists = storage.Exists(container, "joined.bin")
            Expect.isFalse exists "a refused compose writes nothing"

            // The delegation boundary that matters: the INNER store can
            // compose, and must not have been asked to. Composing
            // ciphertext would produce exactly the corrupt object this
            // refusal exists to prevent.
            let! innerExists = inner.Exists(container, "joined.bin")
            Expect.isFalse innerExists "the refusal is the decorator's own, not delegated to the inner store"
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

        // ─── Phase 30d residual — synthetic samples flow through the
        //     Phase 22 encryption-at-rest seam when persisted ──────────
        //
        // Phase 30d generates synthetic samples on-demand and does NOT
        // persist them by default (the substrate path never writes to
        // IBlobStorage). The residual task line — "Synthetic-sample
        // generation respects Phase 22 encryption-at-rest if persisted"
        // — closes by confirming that IF a deployment chooses to persist
        // a sample through `IDataObjectStore.Save`, the existing Phase 22
        // envelope-encryption decorator applies automatically, because
        // `Save` routes content writes through `IBlobStorage.Upload` —
        // the encryption seam. This test proves the seam end-to-end: the
        // synthesised CSV is ciphertext in the inner (undecorated) store,
        // and a decorated `Get` round-trips back to the original bytes.
        // No Phase 30d substrate work is required for this property —
        // only this confirming test, per the migration doc.
        testCaseAsync "Phase 30d — synthetic sample persisted via IDataObjectStore.Save is encrypted at rest"
        <| async {
            let inner = newInnerStorage ()
            let secrets = newSecretStore ()
            let resolver = SingleKeyResolver.create secrets
            let encrypted = EncryptedBlobStorage(inner, resolver) :> IBlobStorage
            let store = DataObjectStore.DataObjectStore(encrypted) :> IDataObjectStore

            // Deterministic Phase 30d synthetic sample.
            let schema =
                Some {
                    Description = "Synthetic test schema"
                    Columns = [
                        {
                            Name = "Region"
                            Type = StringColumn
                            Required = true
                            Description = None
                        }
                        {
                            Name = "Revenue"
                            Type = NumberColumn
                            Required = true
                            Description = None
                        }
                    ]
                }

            let synthObj, synthBytes =
                SyntheticSampleGenerator.generate "Sales" schema 25 42 SyntheticSampleGenerator.DefaultMaxSampleRows

            // A deployment persists the sample (e.g. partner-side cache).
            let scopeId = "team-30d-synth"

            let! saved =
                store.Save(
                    scopeId,
                    "synthetic-cache",
                    synthBytes,
                    "Sales",
                    "partner-alice",
                    synthObj.Metadata,
                    Unversioned
                )

            match saved with
            | Result.Error e -> failwithf "synthetic sample failed to persist: %A" e
            | Result.Ok obj ->
                // At rest: read the content blob directly from the INNER
                // (undecorated) store. It must be the Phase 22 envelope
                // (magic prefix), never the plaintext synthetic CSV.
                let contentBlob = sprintf "objects/_content/%s.data" obj.ContentHash
                let! rawResult = inner.Download(scopeId, contentBlob)

                match rawResult with
                | Result.Ok envelope ->
                    Expect.notEqual envelope synthBytes "content at rest is ciphertext, not plaintext synthetic CSV"
                    Expect.isGreaterThan envelope.Length synthBytes.Length "envelope adds magic+nonce+tag overhead"
                    Expect.equal envelope[0] EncryptionEnvelope.Magic[0] "envelope starts with magic byte 0"
                    Expect.equal envelope[1] EncryptionEnvelope.Magic[1] "envelope starts with magic byte 1"
                    Expect.equal envelope[2] EncryptionEnvelope.Magic[2] "envelope starts with magic byte 2"
                    Expect.equal envelope[3] EncryptionEnvelope.Magic[3] "envelope starts with magic byte 3"
                | Result.Error e -> failwithf "raw content fetch failed: %s" e

                // Decorated round-trip: Get decrypts back to the original
                // synthetic bytes, so the seam is transparent end-to-end.
                let! got = store.Get(scopeId, "synthetic-cache")

                match got with
                | Result.Ok(_, bytes) -> Expect.equal bytes synthBytes "Get decrypts back to original synthetic bytes"
                | Result.Error e -> failwithf "decorated Get failed: %A" e
        }
    ]