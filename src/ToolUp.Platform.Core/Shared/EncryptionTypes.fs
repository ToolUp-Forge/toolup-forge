// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.EncryptionTypes

// ─── Encryption-at-rest shared types ────────────────────────────────
//
// `EncryptedBlobStorage` (server-only decorator) wraps any `IBlobStorage`
// and applies AES-GCM envelope encryption transparently. Encryption keys
// are looked up per request via `IBlobEncryptionKeyResolver` (server)
// keyed by the active `StorageScope`. The resolver is the policy point
// — deployments choose between platform-wide single-key, per-team keys,
// BYOK, or external-KMS without touching the decorator code.
//
// Why these types live in Shared:
// - `EncryptionKey` and `KeyResolutionError` need to round-trip through
//   audit events which the Fable client can read.
// - Envelope-format types stay server-only (different file, Server.props).
//   The wire boundary is opaque bytes; the Fable client never sees the
//   envelope format.

/// A symmetric encryption key with a stable identifier.
///
/// `KeyId` travels in the encrypted blob's envelope header so a key
/// rotation doesn't strand previously-uploaded data — the decryptor
/// reads the envelope, looks up the historical key by id, and decrypts.
/// New writes always use the resolver's "current key for this scope".
///
/// `Material` is 32 raw bytes (256-bit AES key). Storing the bytes here
/// rather than a `byte[] -> byte[]` cipher closure keeps the type
/// Fable-serialisable for audit-event round-trips and lets the resolver
/// persist via `ISecretStore` (which is byte-blob-shaped).
///
/// **Identity-by-value (portability rule 1).** `KeyId` is a `string`; no
/// runtime handles. A deployment can move encryption to a separate
/// process / grain / actor without changing the signature.
type EncryptionKey = { KeyId: string; Material: byte[] }

/// Why a key lookup failed. Closed DU — adding cases is a wire-format
/// change. Audit events round-trip these so the cases stay
/// Fable-serialisable strings + payloads (no exceptions in the type).
type KeyResolutionError =
    /// The requested `KeyId` was never registered or has been removed
    /// for non-destruction reasons (e.g. resolver swap mid-deployment).
    /// Distinct from `KeyDestroyed` because semantics differ — `KeyNotFound`
    /// usually indicates a configuration error; the caller may want to
    /// retry. `KeyDestroyed` indicates intentional crypto-shredding;
    /// retry is meaningless.
    | KeyNotFound of keyId: string
    /// The key was explicitly destroyed via tenant-offboarding crypto-shred.
    /// All blobs encrypted with this `KeyId` are now permanently
    /// undecryptable. The caller surfaces this to the API boundary as
    /// HTTP 410 Gone (resource gone forever, intentional).
    | KeyDestroyed of keyId: string
    /// Underlying storage failed during the lookup. Usually transient
    /// (network blip to `ISecretStore` backend); the caller may retry.
    /// Message is diagnostic — do not leak verbatim to clients.
    | StorageFailure of message: string

module KeyResolutionError =
    /// String tag for serialisation into audit events. Stable across
    /// SDK versions — adding a new case requires adding a new tag here
    /// and updating audit-trail readers.
    let tag (err: KeyResolutionError) : string =
        match err with
        | KeyNotFound _ -> "key_not_found"
        | KeyDestroyed _ -> "key_destroyed"
        | StorageFailure _ -> "storage_failure"

    /// Diagnostic message — for `ILogger.Warn` / `Error` calls.
    /// Never returned to API clients verbatim.
    let message (err: KeyResolutionError) : string =
        match err with
        | KeyNotFound id -> sprintf "Encryption key not found: %s" id
        | KeyDestroyed id -> sprintf "Encryption key destroyed (crypto-shredded): %s" id
        | StorageFailure m -> sprintf "Encryption key storage failure: %s" m

/// Envelope header constants. The server-side decorator
/// (`EncryptedBlobStorage`) writes and reads the envelope; clients
/// never see this.
///
/// Layout (raw bytes — no base64; blobs are binary by nature):
/// ```
/// [Magic:4 "TOBL"][KeyIdLen:1][KeyId:N bytes UTF-8][Nonce:12][Tag:16][Ciphertext:M]
/// ```
///
/// `Magic` distinguishes encrypted blobs from plaintext when a
/// deployment wraps an `IBlobStorage` mid-flight (defence in depth —
/// if a plaintext blob ever sneaks past the decorator, the magic check
/// surfaces it instead of returning gibberish). The 4-byte magic costs
/// nothing in storage and gives a cheap sanity check on every read.
///
/// `KeyIdLen` is one byte (0..255) — `KeyId`s longer than 255 bytes
/// are rejected at write time. The `_platform/master/v1` /
/// `_platform/scopes/{guid}/v1` shapes the SDK ships are well under
/// this limit.
module EncryptionEnvelope =
    /// Magic bytes prefix — "TOBL" (ToolUp BLob) in ASCII.
    let Magic: byte[] = [| 0x54uy; 0x4Fuy; 0x42uy; 0x4Cuy |]
    let MagicLength = 4
    let KeyIdLengthBytes = 1
    let NonceLength = 12
    let TagLength = 16
    /// Smallest possible envelope (magic + zero-length key id + nonce
    /// + tag + zero-byte ciphertext). Used for read-side sanity check.
    let MinimumLength = MagicLength + KeyIdLengthBytes + NonceLength + TagLength
    /// Maximum `KeyId` byte length (UTF-8 encoded).
    let MaxKeyIdBytes = 255