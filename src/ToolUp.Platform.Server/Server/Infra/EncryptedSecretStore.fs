module EncryptedSecretStore

open System
open System.Security.Cryptography
open System.Text
open ToolUp.Platform
open ToolUp.Platform.Secrets

// ─── Envelope format ─────────────────────────────────────────────
//
// Stored format:  base64( MAGIC || nonce (12) || ciphertext (N) || tag (16) )
//
// `MAGIC` (4 bytes `TOEN`) lets the store distinguish its own
// envelopes from legacy plaintext secrets or base64-encoded user
// input that happens to decode successfully but doesn't follow this
// schema. Without the magic check we'd risk false-positive decryption
// attempts on short plaintext values that happen to be valid base64.
//
// AES-GCM with a 32-byte (AES-256) master key and per-secret random
// 12-byte nonce. Authentication tag is 16 bytes. The master key is
// read from the `TOOLUP_SECRETS_MASTER_KEY` environment variable
// (base64-encoded). When the env var is absent the wrapper becomes a
// pass-through — useful for development, logged at startup so
// production environments don't silently miss the protection.

let private Magic = [| 0x54uy; 0x4Fuy; 0x45uy; 0x4Euy |] // "TOEN"
let private MagicLen = 4
let private NonceLen = 12
let private TagLen = 16
let private MinEnvelopeLen = MagicLen + NonceLen + TagLen // + 0 plaintext

let private magicMatches (bytes: byte[]) =
    bytes.Length >= MagicLen
    && bytes[0] = Magic[0]
    && bytes[1] = Magic[1]
    && bytes[2] = Magic[2]
    && bytes[3] = Magic[3]

let private encryptWithKey (key: byte[]) (plaintext: string) : string =
    let pt = Encoding.UTF8.GetBytes plaintext
    let nonce = RandomNumberGenerator.GetBytes NonceLen
    let ct = Array.zeroCreate pt.Length
    let tag = Array.zeroCreate TagLen
    use gcm = new AesGcm(key, TagLen)
    gcm.Encrypt(nonce, pt, ct, tag)
    // Pack: MAGIC || nonce || ciphertext || tag
    let envelope = Array.zeroCreate (MagicLen + NonceLen + ct.Length + TagLen)
    Array.blit Magic 0 envelope 0 MagicLen
    Array.blit nonce 0 envelope MagicLen NonceLen
    Array.blit ct 0 envelope (MagicLen + NonceLen) ct.Length
    Array.blit tag 0 envelope (MagicLen + NonceLen + ct.Length) TagLen
    Convert.ToBase64String envelope

/// Attempt to decrypt a stored value.
/// Returns `Ok plaintext` when the value is a well-formed envelope we
/// can authenticate; `Error reason` otherwise (not an envelope, bad
/// key, tampering detected). Callers treat Error as "the value was
/// already plaintext" and pass through.
let private tryDecryptWithKey (key: byte[]) (stored: string) : Result<string, string> =
    try
        let envelope = Convert.FromBase64String stored

        if envelope.Length < MinEnvelopeLen then
            Error "too short for envelope"
        elif not (magicMatches envelope) then
            Error "missing envelope magic"
        else
            let nonce = Array.sub envelope MagicLen NonceLen
            let ctStart = MagicLen + NonceLen
            let tagStart = envelope.Length - TagLen
            let ctLen = tagStart - ctStart
            let ct = Array.sub envelope ctStart ctLen
            let tag = Array.sub envelope tagStart TagLen
            let pt = Array.zeroCreate ctLen
            use gcm = new AesGcm(key, TagLen)
            gcm.Decrypt(nonce, ct, tag, pt)
            Ok(Encoding.UTF8.GetString pt)
    with
    | :? FormatException as ex -> Error $"not base64: {ex.Message}"
    | :? CryptographicException as ex -> Error $"cryptographic failure: {ex.Message}"
    | ex -> Error $"unexpected: {ex.Message}"

// ─── Master-key sourcing ─────────────────────────────────────────

/// Phase 6l parser-tightening (gap #5). Distinguishes "set but malformed"
/// from "unset" so the validator's error message can be specific.
/// `Unset` — env var absent or empty.
/// `Malformed reason` — env var present but failed to produce a 32-byte
/// AES-256 key.
/// `Valid bytes` — 32 bytes ready for the AES-GCM cipher.
type MasterKeyResolution =
    | Unset
    | Malformed of reason: string
    | Valid of byte[]

/// Parse a base64-encoded master key. Returns `Valid bytes` only when
/// the decoded payload is exactly 32 bytes (AES-256); `Malformed reason`
/// for an unparseable base64 string or wrong-length decoded payload;
/// `Unset` for an empty / null input.
let parseMasterKeyDetailed (base64: string) : MasterKeyResolution =
    if String.IsNullOrEmpty base64 then
        Unset
    else
        try
            let bytes = Convert.FromBase64String base64

            if bytes.Length = 32 then
                Valid bytes
            else
                Malformed(sprintf "decoded to %d bytes (expected 32)" bytes.Length)
        with ex ->
            Malformed(sprintf "failed to base64-decode (%s)" ex.Message)

/// Parse a base64-encoded master key. Returns `Some key` only when the
/// decoded payload is exactly 32 bytes (AES-256); `None` for any other
/// shape, including unparseable base64. Callers SHOULD log a warning
/// when this returns None so misconfiguration is visible at startup.
let parseMasterKey (base64: string) : byte[] option =
    match parseMasterKeyDetailed base64 with
    | Valid bytes -> Some bytes
    | _ -> None

/// Read the master key from the `TOOLUP_SECRETS_MASTER_KEY` env var.
/// Returns `Unset` when the env var is unset / empty, `Malformed reason`
/// when set but unparseable / wrong length, `Valid bytes` when ready.
let masterKeyFromEnvironmentDetailed () : MasterKeyResolution =
    // Phase 698 — through the Phase-696 `ConfigResolution` seam. The key is
    // a SECRET, so the manifest lane is refused with no hatch and this stays
    // an environment read; going through the seam keeps one answer to "where
    // did this value come from" rather than one per reader.
    match ConfigResolution.tryValue ConfigKeys.Names.secretsMasterKey with
    | None -> Unset
    | Some value -> parseMasterKeyDetailed value

/// Read the master key from the `TOOLUP_SECRETS_MASTER_KEY` env var.
/// Returns `None` when the env var is unset, empty, not valid base64,
/// or the decoded payload is the wrong length.
let masterKeyFromEnvironment () : byte[] option =
    match masterKeyFromEnvironmentDetailed () with
    | Valid bytes -> Some bytes
    | _ -> None

/// Generate a fresh 32-byte master key, base64-encoded. Operators call
/// this once during deployment setup and set `TOOLUP_SECRETS_MASTER_KEY`
/// to the output. Rotating the key requires decrypting with the old
/// key and re-encrypting with the new — not automated in this store.
let generateMasterKey () : string =
    let bytes = RandomNumberGenerator.GetBytes 32
    Convert.ToBase64String bytes

// ─── Store implementation ────────────────────────────────────────

/// Wraps an inner `ISecretStore` with AES-GCM at-rest encryption.
///
/// - Get: reads from inner; if `masterKey` is set and the stored
///   value is a well-formed envelope, decrypts and returns the
///   plaintext. Non-envelope values (legacy plaintext, operator-
///   written raw secrets) pass through unchanged — this lets
///   deployments migrate gradually (the next Set on each key
///   promotes it to encrypted form).
/// - Set: when `masterKey` is present, encrypts before writing; when
///   absent, writes plaintext and relies on the inner store's at-rest
///   guarantees. Log a startup warning in the latter case.
/// - Delete: pure pass-through.
///
/// Thread-safety mirrors the inner store — this type holds no mutable
/// state beyond the injected dependencies.
type EncryptedSecretStore(inner: ISecretStore, masterKey: byte[] option) =
    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            let! stored = inner.GetSecret(scopeId, key)

            match stored, masterKey with
            | None, _ -> return None
            | Some raw, None ->
                // No master key configured: passthrough. Dev mode or
                // a deployment that hasn't enabled encryption yet.
                return Some raw
            | Some raw, Some key ->
                match tryDecryptWithKey key raw with
                | Ok plaintext -> return Some plaintext
                | Error _ ->
                    // Not an envelope (or unreadable one). Treat as
                    // legacy plaintext — preserves migration path.
                    // Genuinely corrupt envelopes (wrong key, tampered)
                    // surface here too; operators notice because
                    // "the key doesn't work" when used downstream.
                    return Some raw
        }

        member _.SetSecret(scopeId, key, value) = async {
            match masterKey with
            | None ->
                // No master key — write plaintext. Acceptable for dev;
                // production should set TOOLUP_SECRETS_MASTER_KEY.
                return! inner.SetSecret(scopeId, key, value)
            | Some mk ->
                let envelope = encryptWithKey mk value
                return! inner.SetSecret(scopeId, key, envelope)
        }

        member _.DeleteSecret(scopeId, key) = async { return! inner.DeleteSecret(scopeId, key) }

        // Enumeration passes through unchanged — the inner store
        // owns the key index. The encryption wrapper only touches
        // value bytes, not key names.
        member _.ListKeys(scopeId) = async { return! inner.ListKeys(scopeId) }

    /// Phase 138 — does this wrapper actually encrypt at rest? True only
    /// when a master key is configured; with `masterKey = None` the
    /// wrapper is in plaintext-passthrough mode (see `SetSecret` above).
    /// Lets a config validator distinguish "encryption-capable store
    /// with a key" from "wrapper that silently writes plaintext", which
    /// the env-var-only `EncryptedSecretStoreModeValidator` cannot —
    /// and the default composition's raw `FileSecretStore` (no wrapper
    /// at all) reports `false` via the seam helper below.
    member _.ProvidesEncryptionAtRest = masterKey.IsSome

// ─── Rotation helper ─────────────────────────────────────────────

/// Per-secret outcome when rotating a scope. `Rotated` = decrypted
/// under the old key and re-encrypted under the new; `Unchanged` = not
/// an envelope (plaintext) or already under the new key (idempotent
/// re-runs); `Failed` = decryption or write failed.
type RotationOutcome =
    | Rotated of key: string
    | Unchanged of key: string * reason: string
    | Failed of key: string * reason: string

/// Re-encrypt every known secret in a scope from `oldMasterKey` to
/// `newMasterKey`. Takes the RAW inner store (not an
/// `EncryptedSecretStore` wrapper) so the rotation sees envelope bytes
/// directly — the wrapper's transparent-decrypt behaviour would
/// defeat rotation by returning already-decrypted plaintext.
///
/// Rotation proceeds key-by-key. Each secret's outcome is captured
/// in the returned list; the function does not stop on first error.
/// Values that don't decrypt under the old key (plaintext, already-
/// new-key, or mangled) stay as-is with an `Unchanged` outcome —
/// rotation is idempotent under re-runs and safe alongside legacy
/// plaintext secrets.
///
/// Typical operator flow:
/// 1. Read current `TOOLUP_SECRETS_MASTER_KEY` (the old key).
/// 2. Generate a new key via `generateMasterKey`.
/// 3. Call `rotateScope inner oldKey newKey scopeId logger` for each
///    scope (use `ISecretStore.ListKeys`-derived scope IDs, or
///    enumerate filesystem-backed scopes directly for FileSecretStore).
///    The logger receives one event per outcome so rotation runs are
///    auditable — Info on rotated, Debug on unchanged (idempotent
///    re-runs and plaintext legacy values), Warn on failed.
/// 4. Update the deployment's `TOOLUP_SECRETS_MASTER_KEY` env var
///    and restart the server.
let rotateScope
    (inner: ISecretStore)
    (oldMasterKey: byte[])
    (newMasterKey: byte[])
    (scopeId: string)
    (logger: ILogger)
    : Async<RotationOutcome list> =
    async {
        let! keys = inner.ListKeys scopeId
        let outcomes = System.Collections.Generic.List<RotationOutcome>()

        for key in keys do
            let! raw = inner.GetSecret(scopeId, key)

            match raw with
            | None ->
                logger.Debug $"Rotation: scope={scopeId} key={key} unchanged (missing on re-read)"
                outcomes.Add(Unchanged(key, "missing on re-read"))
            | Some stored ->
                match tryDecryptWithKey oldMasterKey stored with
                | Error reason ->
                    // Could be plaintext, wrong envelope, or already
                    // under the new key. Either way, don't touch it.
                    logger.Debug $"Rotation: scope={scopeId} key={key} unchanged ({reason})"
                    outcomes.Add(Unchanged(key, reason))
                | Ok plaintext ->
                    let envelope = encryptWithKey newMasterKey plaintext
                    let! result = inner.SetSecret(scopeId, key, envelope)

                    match result with
                    | Ok() ->
                        logger.Info $"Rotation: scope={scopeId} key={key} rotated"
                        outcomes.Add(Rotated key)
                    | Error msg ->
                        logger.Warn $"Rotation: scope={scopeId} key={key} failed ({msg})"
                        outcomes.Add(Failed(key, msg))

        return outcomes |> List.ofSeq
    }

/// Summarise a list of rotation outcomes into counts. Useful for
/// operator scripts to decide whether a rotation run met expectations.
let summariseRotation (outcomes: RotationOutcome list) =
    let rotated =
        outcomes
        |> List.sumBy (function
            | Rotated _ -> 1
            | _ -> 0)

    let unchanged =
        outcomes
        |> List.sumBy (function
            | Unchanged _ -> 1
            | _ -> 0)

    let failed =
        outcomes
        |> List.sumBy (function
            | Failed _ -> 1
            | _ -> 0)

    {|
        Rotated = rotated
        Unchanged = unchanged
        Failed = failed
        Total = outcomes.Length
    |}