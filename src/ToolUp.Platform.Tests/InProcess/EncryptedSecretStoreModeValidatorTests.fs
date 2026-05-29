module ToolUp.Platform.Tests.InProcess.EncryptedSecretStoreModeValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Helpers ──────────────────────────────────────────────────────────

/// Wrap test bodies that mutate `TOOLUP_SECRETS_MASTER_KEY` so the
/// env var state is restored on exit. Tests that don't touch it run
/// undisturbed.
let private withMasterKey (value: string option) (body: unit -> unit) =
    let saved = Environment.GetEnvironmentVariable "TOOLUP_SECRETS_MASTER_KEY"

    try
        match value with
        | Some v -> Environment.SetEnvironmentVariable("TOOLUP_SECRETS_MASTER_KEY", v)
        | None -> Environment.SetEnvironmentVariable("TOOLUP_SECRETS_MASTER_KEY", null)

        body ()
    finally
        Environment.SetEnvironmentVariable("TOOLUP_SECRETS_MASTER_KEY", saved)

/// Phase 2a — wrap test bodies that mutate `TOOLUP_SECRET_STORE` so
/// the env var state is restored on exit. Tests that don't touch it
/// run undisturbed.
let private withSecretStore (value: string option) (body: unit -> unit) =
    let saved = Environment.GetEnvironmentVariable "TOOLUP_SECRET_STORE"

    try
        match value with
        | Some v -> Environment.SetEnvironmentVariable("TOOLUP_SECRET_STORE", v)
        | None -> Environment.SetEnvironmentVariable("TOOLUP_SECRET_STORE", null)

        body ()
    finally
        Environment.SetEnvironmentVariable("TOOLUP_SECRET_STORE", saved)

let private validKey () =
    EncryptedSecretStore.generateMasterKey ()

let private cfg (surfaces: SurfaceProfile list) (escapeHatch: bool) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptPlaintextSecretsWhenAuthRequired = escapeHatch
}

let private dummyStore () : Secrets.ISecretStore =
    EnvironmentSecretStore.EnvironmentSecretStore() :> Secrets.ISecretStore

let private validate (config: ServerConfig) : ValidationResult =
    let v =
        EncryptedSecretStoreModeValidator.EncryptedSecretStoreModeValidator(config, dummyStore ()) :> IConfigValidator

    v.Validate() |> Async.RunSynchronously

// ─── Tests ────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    // Sequential because every test mutates `TOOLUP_SECRETS_MASTER_KEY`
    // — a process-global env var. Parallel execution races across the
    // setup/restore paths.
    testSequenced
    <| testList "Phase 6l.E — EncryptedSecretStore mode validator" [

        test "Anonymous mode + no master key → Ok (dev path)" {
            withMasterKey None (fun () ->
                let result = validate (cfg Surfaces.anonymous false)
                Expect.equal result Ok "anon dev path passes")
        }

        test "Individual mode + no master key + no escape hatch → Error" {
            withMasterKey None (fun () ->
                let result = validate (cfg Surfaces.individual false)

                match result with
                | Error msg ->
                    Expect.stringContains msg "Individual" "names the offending mode"
                    Expect.stringContains msg "TOOLUP_SECRETS_MASTER_KEY" "names the missing config"
                    Expect.stringContains msg "plaintext" "explains the consequence"

                    Expect.stringContains msg "AcceptPlaintextSecretsWhenAuthRequired" "documents the escape hatch"
                | other -> failtestf "expected Error, got %A" other)
        }

        test "Team mode + no master key → Error" {
            withMasterKey None (fun () ->
                let result = validate (cfg Surfaces.team false)

                match result with
                | Error _ -> ()
                | other -> failtestf "expected Error, got %A" other)
        }

        test "Individual mode + master key set → Ok (production path)" {
            withMasterKey (Some(validKey ())) (fun () ->
                let result = validate (cfg Surfaces.individual false)
                Expect.equal result Ok "production path passes")
        }

        test "Individual mode + no master key + escape hatch → Ok" {
            withMasterKey None (fun () ->
                let result = validate (cfg Surfaces.individual true)
                Expect.equal result Ok "escape hatch passes")
        }

        test "MultiTeam mode + no master key → Error" {
            withMasterKey None (fun () ->
                let result = validate (cfg Surfaces.multiTeam false)

                match result with
                | Error _ -> ()
                | other -> failtestf "expected Error, got %A" other)
        }

        test "Validator metadata is well-formed" {
            let v =
                EncryptedSecretStoreModeValidator.EncryptedSecretStoreModeValidator(
                    cfg Surfaces.anonymous false,
                    dummyStore ()
                )
                :> IConfigValidator

            Expect.equal v.Name "encrypted-secret-store-mode" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }

        // Phase 6l parser-tightening (gap #5). The validator distinguishes
        // "unset" from "set-but-malformed" so an operator who sets the
        // key but typos it (off-by-one, accidental newline, trailing
        // whitespace) sees the parse failure rather than "is unset".

        test "Individual mode + master key set but not valid base64 → Error names parse failure" {
            withMasterKey (Some "not-valid-base64-!!!") (fun () ->
                let result = validate (cfg Surfaces.individual false)

                match result with
                | Error msg ->
                    Expect.stringContains msg "Individual" "still names the mode"

                    Expect.stringContains
                        msg
                        "TOOLUP_SECRETS_MASTER_KEY is set but"
                        "distinguishes set-but-malformed from unset"

                    Expect.stringContains msg "base64" "names the parse failure"
                | other -> failtestf "expected Error, got %A" other)
        }

        test "Individual mode + master key set but wrong length → Error names byte count" {
            // Valid base64 but decodes to fewer than 32 bytes (16 bytes of zeros).
            let shortKey = Convert.ToBase64String(Array.zeroCreate 16)

            withMasterKey (Some shortKey) (fun () ->
                let result = validate (cfg Surfaces.individual false)

                match result with
                | Error msg ->
                    Expect.stringContains
                        msg
                        "TOOLUP_SECRETS_MASTER_KEY is set but"
                        "distinguishes set-but-malformed from unset"

                    Expect.stringContains msg "decoded to" "names the byte-length issue"
                    Expect.stringContains msg "16 bytes" "names the actual byte count"
                    Expect.stringContains msg "expected 32" "names the expected byte count"
                | other -> failtestf "expected Error, got %A" other)
        }

        test "parseMasterKeyDetailed: empty → Unset" {
            let result = EncryptedSecretStore.parseMasterKeyDetailed ""
            Expect.equal result EncryptedSecretStore.Unset "empty string maps to Unset"
        }

        test "parseMasterKeyDetailed: invalid base64 → Malformed" {
            match EncryptedSecretStore.parseMasterKeyDetailed "not-base64-!!!" with
            | EncryptedSecretStore.Malformed reason ->
                Expect.stringContains reason "base64" "reason names the parse class"
            | other -> failtestf "expected Malformed, got %A" other
        }

        test "parseMasterKeyDetailed: short payload → Malformed with byte count" {
            let short = Convert.ToBase64String(Array.zeroCreate 16)

            match EncryptedSecretStore.parseMasterKeyDetailed short with
            | EncryptedSecretStore.Malformed reason ->
                Expect.stringContains reason "16 bytes" "reason names actual byte count"
                Expect.stringContains reason "expected 32" "reason names expected count"
            | other -> failtestf "expected Malformed, got %A" other
        }

        test "parseMasterKeyDetailed: 32-byte payload → Valid" {
            let bytes = Array.zeroCreate 32
            let validBase64 = Convert.ToBase64String bytes

            match EncryptedSecretStore.parseMasterKeyDetailed validBase64 with
            | EncryptedSecretStore.Valid resultBytes -> Expect.equal resultBytes.Length 32 "Valid carries the 32 bytes"
            | other -> failtestf "expected Valid, got %A" other
        }

        // Phase 2a — cloud-KMS-backed store recognition. When
        // TOOLUP_SECRET_STORE selects a managed cloud KMS, the
        // companion provides its own at-rest encryption and the
        // validator's master-key gate is intentionally bypassed.

        test "Individual mode + no master key + TOOLUP_SECRET_STORE=azure-key-vault → Ok" {
            withMasterKey None (fun () ->
                withSecretStore (Some "azure-key-vault") (fun () ->
                    let result = validate (cfg Surfaces.individual false)
                    Expect.equal result Ok "cloud-KMS path bypasses master-key gate"))
        }

        test "Individual mode + no master key + TOOLUP_SECRET_STORE=AWS-Secrets-Manager (case-insensitive) → Ok" {
            withMasterKey None (fun () ->
                withSecretStore (Some "AWS-Secrets-Manager") (fun () ->
                    let result = validate (cfg Surfaces.individual false)
                    Expect.equal result Ok "case-insensitive recognition"))
        }

        test "Individual mode + no master key + TOOLUP_SECRET_STORE=vault → Ok" {
            withMasterKey None (fun () ->
                withSecretStore (Some "vault") (fun () ->
                    let result = validate (cfg Surfaces.individual false)
                    Expect.equal result Ok "Vault recognised"))
        }

        test "Individual mode + no master key + TOOLUP_SECRET_STORE=encrypted → Error (still gated)" {
            withMasterKey None (fun () ->
                withSecretStore (Some "encrypted") (fun () ->
                    let result = validate (cfg Surfaces.individual false)

                    match result with
                    | Error _ -> ()
                    | other -> failtestf "non-cloud-KMS values must not bypass the gate; got %A" other))
        }

        test "Individual mode + no master key + TOOLUP_SECRET_STORE=file → Error (still gated)" {
            withMasterKey None (fun () ->
                withSecretStore (Some "file") (fun () ->
                    let result = validate (cfg Surfaces.individual false)

                    match result with
                    | Error _ -> ()
                    | other -> failtestf "non-cloud-KMS values must not bypass the gate; got %A" other))
        }

        test "Error message documents cloud-KMS as a third resolution option" {
            withMasterKey None (fun () ->
                withSecretStore None (fun () ->
                    let result = validate (cfg Surfaces.individual false)

                    match result with
                    | Error msg ->
                        Expect.stringContains msg "TOOLUP_SECRETS_MASTER_KEY" "first resolution"
                        Expect.stringContains msg "TOOLUP_SECRET_STORE" "second resolution (cloud KMS)"

                        Expect.stringContains
                            msg
                            "AcceptPlaintextSecretsWhenAuthRequired"
                            "third resolution (escape hatch)"
                    | other -> failtestf "expected Error, got %A" other))
        }
    ]