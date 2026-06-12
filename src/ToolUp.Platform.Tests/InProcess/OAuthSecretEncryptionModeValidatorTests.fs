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
        test "Anonymous mode + non-encrypting store → Ok (no auth, no refusal)" {
            Expect.equal (validate (cfg Surfaces.anonymous false) (rawStore ())) Ok "anon path passes"
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

        test "Team mode + raw store + escape hatch → Ok" {
            Expect.equal (validate (cfg Surfaces.team true) (rawStore ())) Ok "escape hatch passes"
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