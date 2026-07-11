module ToolUp.Platform.EncryptedBlobStorage

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.EncryptionTypes
open ToolUp.Platform.BlobEncryption

// ─── Phase 22 — EncryptedBlobStorage decorator ──────────────────────
//
// Wraps any `IBlobStorage` implementation and applies AES-GCM envelope
// encryption transparently on `Upload` / `Download`. `List` / `Delete`
// / `Exists` / `GetMetadata` pass through unchanged — they don't
// touch ciphertext bytes.
//
// The decorator is **policy-free**: it asks the injected
// `IBlobEncryptionKeyResolver` for a key on every Upload and looks up
// the key by id on every Download. Choosing a resolver picks the
// security model: `SingleKeyResolver` (platform-wide), `PerScopeKeyResolver`
// (per-tenant, with crypto-shred), or a custom / KMS-backed resolver
// for advanced deployments.
//
// Scope derivation: `IBlobStorage` operations receive `container` (a
// scope-derived name like `user-{id}`, `team-{id}`, `session-{id}`,
// `_platform`). The decorator reverses that derivation to a
// `StorageScope` for the resolver call. This keeps the decorator
// stateless and testable without `HttpContext`. Container names that
// don't match the documented prefixes fall back to "container ==
// scopeId" — consistent with `IBlobStorage`'s scope-discipline contract.
//
// Envelope layout (raw bytes — no base64; blobs are binary):
//   [Magic:4 "TOBL"][KeyIdLen:1][KeyId:N][Nonce:12][Tag:16][Ciphertext:M]
//
// `Magic` discriminates encrypted blobs from plaintext when a deployment
// wraps an `IBlobStorage` mid-flight (defence in depth).

/// Reverse the `IBlobStorage` container-naming convention to recover
/// the underlying `StorageScope`. Container shapes the SDK uses:
///   - `_platform`        — reserved SDK-level container
///   - `user-{userId}`    — individual user scope
///   - `team-{teamId}`    — team scope
///   - `session-{sessionId}` — anonymous/ephemeral scope
///
/// `Persist` is set to `true` because encryption is only a meaningful
/// concern for blobs that survive longer than a single request — the
/// caller is by definition writing or reading something the backing
/// store will retain.
let private scopeFromContainer (container: string) : StorageScope =
    let scopeId =
        if container = "_platform" then
            "_platform"
        elif container.StartsWith "user-" then
            container.Substring 5
        elif container.StartsWith "team-" then
            container.Substring 5
        elif container.StartsWith "session-" then
            container.Substring 8
        else
            container

    {
        Container = container
        ScopeId = scopeId
        Persist = true
    }

/// Element-wise byte-array equality. `byte[]` defaults to reference
/// equality in .NET; we need value equality for the magic-bytes check.
let private bytesEqual (a: byte[]) (b: byte[]) : bool =
    if a.Length <> b.Length then
        false
    else
        let mutable i = 0
        let mutable ok = true

        while ok && i < a.Length do
            if a[i] <> b[i] then
                ok <- false

            i <- i + 1

        ok

/// Encrypt `plaintext` under `key` using AES-GCM and emit the full
/// envelope. Random nonce per-blob (12 bytes from
/// `RandomNumberGenerator`); 16-byte AEAD tag.
let private encrypt (key: EncryptionKey) (plaintext: byte[]) : byte[] =
    let keyIdBytes = Encoding.UTF8.GetBytes(key.KeyId)

    if keyIdBytes.Length > EncryptionEnvelope.MaxKeyIdBytes then
        invalidArg
            "key.KeyId"
            (sprintf "KeyId UTF-8 byte length %d exceeds maximum %d" keyIdBytes.Length EncryptionEnvelope.MaxKeyIdBytes)

    let nonce = Array.zeroCreate<byte> EncryptionEnvelope.NonceLength
    RandomNumberGenerator.Fill(nonce.AsSpan())

    let ciphertext = Array.zeroCreate<byte> plaintext.Length
    let tag = Array.zeroCreate<byte> EncryptionEnvelope.TagLength
    use aes = new AesGcm(ReadOnlySpan(key.Material), EncryptionEnvelope.TagLength)
    aes.Encrypt(ReadOnlySpan(nonce), ReadOnlySpan(plaintext), Span(ciphertext), Span(tag))

    let totalLen =
        EncryptionEnvelope.MagicLength
        + EncryptionEnvelope.KeyIdLengthBytes
        + keyIdBytes.Length
        + EncryptionEnvelope.NonceLength
        + EncryptionEnvelope.TagLength
        + ciphertext.Length

    let envelope = Array.zeroCreate<byte> totalLen
    let mutable offset = 0
    Array.blit EncryptionEnvelope.Magic 0 envelope offset EncryptionEnvelope.MagicLength
    offset <- offset + EncryptionEnvelope.MagicLength
    envelope[offset] <- byte keyIdBytes.Length
    offset <- offset + EncryptionEnvelope.KeyIdLengthBytes
    Array.blit keyIdBytes 0 envelope offset keyIdBytes.Length
    offset <- offset + keyIdBytes.Length
    Array.blit nonce 0 envelope offset EncryptionEnvelope.NonceLength
    offset <- offset + EncryptionEnvelope.NonceLength
    Array.blit tag 0 envelope offset EncryptionEnvelope.TagLength
    offset <- offset + EncryptionEnvelope.TagLength
    Array.blit ciphertext 0 envelope offset ciphertext.Length
    envelope

type private ParsedEnvelope = {
    KeyId: string
    Nonce: byte[]
    Tag: byte[]
    Ciphertext: byte[]
}

/// Parse the raw envelope bytes into its components, validating the
/// magic prefix and length invariants. Returns a diagnostic string on
/// failure — surfaced to the caller via `Error msg`.
let private parseEnvelope (envelope: byte[]) : Result<ParsedEnvelope, string> =
    if envelope.Length < EncryptionEnvelope.MinimumLength then
        Error(sprintf "Envelope too short: %d bytes (minimum %d)" envelope.Length EncryptionEnvelope.MinimumLength)
    else
        let magic = Array.sub envelope 0 EncryptionEnvelope.MagicLength

        if not (bytesEqual magic EncryptionEnvelope.Magic) then
            Error "Envelope magic mismatch — blob is not in EncryptedBlobStorage format"
        else
            let keyIdLen = int envelope[EncryptionEnvelope.MagicLength]

            let headerLen =
                EncryptionEnvelope.MagicLength
                + EncryptionEnvelope.KeyIdLengthBytes
                + keyIdLen
                + EncryptionEnvelope.NonceLength
                + EncryptionEnvelope.TagLength

            if envelope.Length < headerLen then
                Error(sprintf "Envelope length %d insufficient for declared KeyIdLen %d" envelope.Length keyIdLen)
            else
                let keyId =
                    Encoding.UTF8.GetString(
                        envelope,
                        EncryptionEnvelope.MagicLength + EncryptionEnvelope.KeyIdLengthBytes,
                        keyIdLen
                    )

                let nonceStart =
                    EncryptionEnvelope.MagicLength + EncryptionEnvelope.KeyIdLengthBytes + keyIdLen

                let tagStart = nonceStart + EncryptionEnvelope.NonceLength
                let cipherStart = tagStart + EncryptionEnvelope.TagLength
                let nonce = Array.sub envelope nonceStart EncryptionEnvelope.NonceLength
                let tag = Array.sub envelope tagStart EncryptionEnvelope.TagLength
                let ciphertext = Array.sub envelope cipherStart (envelope.Length - cipherStart)

                Ok {
                    KeyId = keyId
                    Nonce = nonce
                    Tag = tag
                    Ciphertext = ciphertext
                }

/// Decrypt `ciphertext` under `key` using AES-GCM. Returns `Error` on
/// authentication failure (tampered envelope, wrong key) — diagnostic
/// only; the decorator surfaces this to the API boundary.
let private decrypt (key: EncryptionKey) (nonce: byte[]) (tag: byte[]) (ciphertext: byte[]) : Result<byte[], string> =
    try
        let plaintext = Array.zeroCreate<byte> ciphertext.Length
        use aes = new AesGcm(ReadOnlySpan(key.Material), EncryptionEnvelope.TagLength)
        aes.Decrypt(ReadOnlySpan(nonce), ReadOnlySpan(ciphertext), ReadOnlySpan(tag), Span(plaintext))
        Ok plaintext
    with ex ->
        Error(sprintf "AES-GCM decryption failed: %s" ex.Message)

/// `IBlobStorage` decorator that applies AES-GCM envelope encryption
/// transparently. Keys are resolved per scope on every Upload via the
/// injected `IBlobEncryptionKeyResolver`; the resolver's `KeyId` is
/// stamped into the envelope so subsequent Downloads can recover the
/// same key after rotation.
///
/// `List` / `Delete` / `Exists` / `GetMetadata` are pass-through —
/// they don't touch ciphertext bytes and remain semantically identical
/// to the inner storage.
type EncryptedBlobStorage(inner: IBlobStorage, resolver: IBlobEncryptionKeyResolver) =
    interface IBlobStorage with
        member _.Upload(container, blobName, content) = async {
            let scope = scopeFromContainer container
            let! key = resolver.ResolveKey scope
            let envelope = encrypt key content
            return! inner.Upload(container, blobName, envelope)
        }

        member _.Download(container, blobName) = async {
            let! result = inner.Download(container, blobName)

            match result with
            | Error e -> return Error e
            | Ok envelope ->
                match parseEnvelope envelope with
                | Error msg -> return Error(sprintf "Invalid encryption envelope: %s" msg)
                | Ok parsed ->
                    let! keyResult = resolver.ResolveKeyById parsed.KeyId

                    match keyResult with
                    | Error err -> return Error(KeyResolutionError.message err)
                    | Ok key ->
                        match decrypt key parsed.Nonce parsed.Tag parsed.Ciphertext with
                        | Ok plaintext -> return Ok plaintext
                        | Error msg -> return Error msg
        }

        // Phase 455 — honest refusal (the recorded 455.A verdict). The
        // envelope is whole-blob AES-GCM: a mid-blob byte range of
        // ciphertext is undecryptable (the AEAD tag authenticates the
        // whole message), and offsets into the plaintext don't line up
        // with offsets into the envelope anyway. Refusing loudly beats
        // silently downloading + decrypting the whole blob — the caller
        // chose a ranged read precisely to avoid whole-object
        // materialisation. Encrypted deployments that need ranged reads
        // adopt a chunked envelope (a separate phase); until then they
        // use `Download`.
        member _.DownloadRange(_, _, _, _) = async {
            return
                Error
                    "EncryptedBlobStorage does not support ranged reads: content is whole-blob AES-GCM encrypted, so a mid-blob range is undecryptable. Use Download for encrypted content."
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        // Delegate to the inner store. HardDelete just deletes; the
        // Tombstone marker is non-secret (`*ERASED*`), so writing it
        // unencrypted through the inner store is intentional — there
        // is nothing left to protect once content is erased.
        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)