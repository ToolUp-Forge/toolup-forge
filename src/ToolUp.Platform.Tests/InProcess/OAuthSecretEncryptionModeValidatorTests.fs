module ToolUp.Platform.Tests.InProcess.OAuthSecretEncryptionModeValidatorTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 138 — OAuth secret-encryption mode validator ─────────────
//
// Store-type-aware: refuses an authenticated OAuth-connector deployment
// whose ISecretStore does not encrypt at rest. The default raw
// FileSecretStore and an EncryptedSecretStore in plaintext-passthrough
// mode (no master key) both report non-encrypting; only an
// EncryptedSecretStore WITH a key (or a cloud-KMS store) passes. This
// is the gap the env-var-only EncryptedSecretStoreModeValidator misses:
// a raw store + a master key env var set passes that validator while
// writing plaintext.
//
// ─── Phase 340 — the scope is no longer auth-gated ──────────────────
//
// Two paths that used to return a silent `Ok` now return a `Warning`:
// a non-auth-requiring (Anonymous-surface) deployment persisting OAuth
// credentials to a non-encrypting store, and any deployment whose
// `AcceptPlaintextSecretsWhenAuthRequired` escape hatch is suppressing
// the finding. The refusal path (auth-requiring, no escape hatch) and the
// clean path (an encrypting store) are unchanged, which is the GP 11
// promise: an already-encrypting deployment sees nothing new.

let private cfg (surfaces: SurfaceProfile list) (escapeHatch: bool) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptPlaintextSecretsWhenAuthRequired = escapeHatch
}

let private validate (config: ServerConfig) (store: Secrets.ISecretStore) : ValidationResult =
    let v =
        OAuthSecretEncryptionModeValidator.OAuthSecretEncryptionModeValidator(config, store) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

let private rawStore () : Secrets.ISecretStore =
    EnvironmentSecretStore.EnvironmentSecretStore() :> Secrets.ISecretStore

let private encryptingStore () : Secrets.ISecretStore =
    EncryptedSecretStore.EncryptedSecretStore(rawStore (), Some(Array.zeroCreate 32)) :> Secrets.ISecretStore

let private passthroughStore () : Secrets.ISecretStore =
    EncryptedSecretStore.EncryptedSecretStore(rawStore (), None) :> Secrets.ISecretStore

[<Tests>]
let tests =
    testList "Phase 138 — OAuth secret-encryption mode validator" [
        test "Phase 340 — Anonymous mode + non-encrypting store → Warning (caught, not refused)" {
            // The pre-340 assertion here was `Ok`: an anonymous-surface
            // deployment running OAuth connectors persisted plaintext
            // refresh tokens and preflight said nothing at all. It is
            // surfaced now, and deliberately as a Warning — the
            // aggregator treats Warning as non-blocking, so an existing
            // deployment upgrades, reads the finding, and still boots.
            match validate (cfg Surfaces.anonymous false) (rawStore ()) with
            | Warning msg ->
                Expect.stringContains msg "Anonymous" "names the offending mode"
                Expect.stringContains msg "plaintext" "explains the consequence"
                Expect.stringContains msg "EncryptedSecretStore" "carries the same remediation menu as the refusal"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Phase 340 — Anonymous mode + encrypting store → Ok (GP 11: no new noise)" {
            Expect.equal
                (validate (cfg Surfaces.anonymous false) (encryptingStore ()))
                Ok
                "an encrypting deployment is byte-for-byte unchanged, whatever its surface"
        }

        test "Individual mode + raw (non-encrypting) store + no escape hatch → Error" {
            match validate (cfg Surfaces.individual false) (rawStore ()) with
            | Error msg ->
                Expect.stringContains msg "Individual" "names the offending mode"
                Expect.stringContains msg "plaintext" "explains the consequence"
                Expect.stringContains msg "AcceptPlaintextSecretsWhenAuthRequired" "documents the escape hatch"
            | other -> failtestf "expected Error, got %A" other
        }

        test "Individual mode + EncryptedSecretStore with key → Ok" {
            Expect.equal (validate (cfg Surfaces.individual false) (encryptingStore ())) Ok "encrypting store passes"
        }

        test "Individual mode + EncryptedSecretStore without key (plaintext passthrough) → Error (store-type-aware)" {
            match validate (cfg Surfaces.individual false) (passthroughStore ()) with
            | Error _ -> ()
            | other -> failtestf "passthrough wrapper must be caught by store inspection; got %A" other
        }

        test "Phase 340 — Team mode + raw store + escape hatch → Warning naming the flag" {
            // Still not refused (the informed opt-out is honoured), but no
            // longer silent: before 340 this returned `Ok`, so a
            // deployment holding the flag and one with a correctly
            // encrypting store produced identical preflight output.
            match validate (cfg Surfaces.team true) (rawStore ()) with
            | Warning msg ->
                Expect.stringContains
                    msg
                    "AcceptPlaintextSecretsWhenAuthRequired"
                    "names the flag that suppressed the refusal"

                Expect.stringContains msg "startup refusal" "says what was suppressed"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Phase 340 — Anonymous mode + raw store + escape hatch → Warning (not a refusal either way)" {
            match validate (cfg Surfaces.anonymous true) (rawStore ()) with
            | Warning msg -> Expect.stringContains msg "AcceptPlaintextSecretsWhenAuthRequired" "names the flag"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Phase 340 — escape hatch does NOT manufacture a finding on an encrypting store" {
            // The flag is a suppression, not an assertion of risk: a
            // deployment that sets it AND encrypts properly must stay
            // silent, or the Warning becomes noise people learn to skip.
            Expect.equal
                (validate (cfg Surfaces.team true) (encryptingStore ()))
                Ok
                "encrypting store wins over the flag"
        }

        test "Validator metadata is well-formed" {
            let v =
                OAuthSecretEncryptionModeValidator.OAuthSecretEncryptionModeValidator(
                    cfg Surfaces.anonymous false,
                    rawStore ()
                )
                :> IConfigValidator

            Expect.equal v.Name "oauth-secret-encryption-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]