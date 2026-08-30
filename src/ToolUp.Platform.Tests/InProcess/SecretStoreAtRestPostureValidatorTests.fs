module ToolUp.Platform.Tests.InProcess.SecretStoreAtRestPostureValidatorTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.Secrets

// ─── Phase 457 — secret-store at-rest posture ───────────────────────
//
// The gap this closes, stated as the deployment that had it: `RequireAuth`
// on a store that does not encrypt, with no connector OAuth flow. Neither
// existing validator sees it — one reads the master-key env var and never
// looks at the composed store, the other inspects the store but is
// registered only when the OAuth substrate is active — so BYOK keys and
// webhook secrets landed on disk in the clear behind one easily-missed
// boot line.
//
// The cases below pin all four halves of the acceptance criterion: the
// refusal, the acknowledgement's downgrade, the silence of a correctly
// encrypting deployment, and the fact that `SkipPreflight` cannot bypass
// any of it.

// ─── Env helpers ─────────────────────────────────────────────────────

let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let saved = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, saved)

// ─── Stores ──────────────────────────────────────────────────────────

let private rawFileStore () : ISecretStore =
    FileSecretStore.FileSecretStore(baseDir = IO.Path.GetTempPath()) :> ISecretStore

let private envStore () : ISecretStore =
    EnvironmentSecretStore.EnvironmentSecretStore() :> ISecretStore

let private encryptingStore () : ISecretStore =
    EncryptedSecretStore.EncryptedSecretStore(envStore (), Some(Array.zeroCreate 32)) :> ISecretStore

let private passthroughStore () : ISecretStore =
    EncryptedSecretStore.EncryptedSecretStore(envStore (), None) :> ISecretStore

/// A store that implements `ISecretStore` and nothing else — the shape of
/// a consumer's own implementation, and of any companion written before
/// the posture seam existed.
let private undeclaringStore () : ISecretStore =
    { new ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Result.Ok() }
        member _.DeleteSecret(_, _) = async { return Result.Ok() }
        member _.ListKeys _ = async { return [] }
    }

/// A store that declares itself encrypting without being any type the SDK
/// knows — the case the seam exists for.
let private declaredEncryptingStore () : ISecretStore =
    { new ISecretStore with
        member _.GetSecret(_, _) = async { return None }
        member _.SetSecret(_, _, _) = async { return Result.Ok() }
        member _.DeleteSecret(_, _) = async { return Result.Ok() }
        member _.ListKeys _ = async { return [] }

      interface ISecretStoreAtRestPosture with
          member _.AtRestPosture = EncryptsAtRest "a consumer-supplied encrypting backend"
    }

// ─── Config + invocation ─────────────────────────────────────────────

let private cfg (surfaces: SurfaceProfile list) (acknowledged: bool) = {
    ServerConfig.defaults with
        Surfaces = surfaces
        AcceptPlaintextSecretsWhenAuthRequired = acknowledged
}

let private validator (config: ServerConfig) (store: ISecretStore) =
    SecretStoreAtRestPostureValidator.SecretStoreAtRestPostureValidator(config, store) :> IConfigValidator

let private validate (config: ServerConfig) (store: ISecretStore) : ValidationResult =
    (validator config store).Validate() |> Async.RunSynchronously

[<Tests>]
let tests =
    // Sequential: several cases mutate `TOOLUP_SECRET_STORE` /
    // `TOOLUP_ACCEPT_PLAINTEXT_SECRETS`, which are process-global.
    testSequenced
    <| testList "Phase 457 — secret-store at-rest posture validator" [

        // ── The refusal ────────────────────────────────────────────────

        test "Individual mode + raw FileSecretStore → Error (the gap this phase closes)" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    match validate (cfg Surfaces.individual false) (rawFileStore ()) with
                    | Error msg ->
                        Expect.stringContains msg "Individual" "names the offending surface"
                        Expect.stringContains msg "FileSecretStore" "names the store that does not encrypt"
                        Expect.stringContains msg "TOOLUP_SECRETS_MASTER_KEY" "carries the remediation"

                        Expect.stringContains
                            msg
                            "TOOLUP_ACCEPT_PLAINTEXT_SECRETS"
                            "names the acknowledgement that lowers it"
                    | other -> failtestf "expected Error, got %A" other))
        }

        test "Team mode + EncryptedSecretStore with no master key → Error (the wrapper is not encrypting)" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    match validate (cfg Surfaces.team false) (passthroughStore ()) with
                    | Error msg ->
                        Expect.stringContains
                            msg
                            "no master key"
                            "the message distinguishes the missing key from a store that never encrypts"
                    | other -> failtestf "expected Error, got %A" other))
        }

        test "Individual mode + a store that declares nothing → Error, named as UNKNOWN not plaintext" {
            // Fail-closed, but the message must not assert a fact nobody
            // established: the store may well encrypt, it has simply never
            // said so. A guard that overstates its evidence is one
            // operators learn to override on reflex.
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    match validate (cfg Surfaces.individual false) (undeclaringStore ()) with
                    | Error msg ->
                        Expect.stringContains
                            msg
                            "nothing has stated"
                            "reports absence of a declaration, not plaintext"

                        Expect.stringContains
                            msg
                            "ISecretStoreAtRestPosture"
                            "tells the implementor how to answer the question"
                    | other -> failtestf "expected Error, got %A" other))
        }

        // ── The acknowledgement ────────────────────────────────────────

        test "Individual mode + raw store + acknowledgement flag → Warning, naming the flag" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    match validate (cfg Surfaces.individual true) (rawFileStore ()) with
                    | Warning msg ->
                        Expect.stringContains
                            msg
                            "TOOLUP_ACCEPT_PLAINTEXT_SECRETS"
                            "says which acknowledgement suppressed the refusal"

                        Expect.stringContains msg "NOT being refused" "says what was suppressed"
                    | other -> failtestf "expected Warning, got %A" other))
        }

        test "TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1 downgrades the refusal on a hand-built ServerConfig" {
            // `fromEnv` folds this key into
            // `AcceptPlaintextSecretsWhenAuthRequired`, but a consumer that
            // builds its config by hand never goes through `fromEnv` — so
            // the validator resolves the key itself, or the documented
            // acknowledgement would work on only one composition path.
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets (Some "1") (fun () ->
                    match validate (cfg Surfaces.individual false) (rawFileStore ()) with
                    | Warning _ -> ()
                    | other -> failtestf "expected Warning under the env acknowledgement, got %A" other))
        }

        test "an unrecognised acknowledgement value does not acknowledge" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets (Some "maybe") (fun () ->
                    match validate (cfg Surfaces.individual false) (rawFileStore ()) with
                    | Error _ -> ()
                    | other -> failtestf "only the canonical truthy spellings acknowledge; got %A" other))
        }

        test "the acknowledgement does NOT manufacture a finding on an encrypting store" {
            // The flag is a suppression, not an assertion of risk. A
            // deployment that sets it and also encrypts properly must stay
            // silent, or the Warning becomes noise people learn to skip.
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.team true) (encryptingStore ()))
                    Ok
                    "an encrypting store wins over the acknowledgement")
        }

        // ── The silent paths ───────────────────────────────────────────

        test "Individual mode + EncryptedSecretStore with a master key → Ok" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.individual false) (encryptingStore ()))
                    Ok
                    "an encrypting deployment boots with no new finding (GP 11)")
        }

        test "Individual mode + a store that DECLARES it encrypts → Ok (the seam's whole point)" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.individual false) (declaredEncryptingStore ()))
                    Ok
                    "a store the SDK has never heard of passes on its own declaration")
        }

        test "Anonymous mode + raw store → Ok (the gate is auth-requiring by design)" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.anonymous false) (rawFileStore ()))
                    Ok
                    "an anonymous surface holds no per-user credentials; its connector grants are the OAuth validator's beat")
        }

        test "an undeclaring store under a recognised TOOLUP_SECRET_STORE → Ok (existing KMS deployments unchanged)" {
            // The carve-out is a RECOGNITION for a store that declares
            // nothing — which is every KMS companion in a deployment that
            // has not yet taken this SDK version. Without it, upgrading
            // would refuse a correctly-encrypting production deployment.
            withEnv ConfigKeys.Names.secretStore (Some "azure-key-vault") (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.individual false) (undeclaringStore ()))
                    Ok
                    "the env switch answers for a store that has not declared")
        }

        test "a DECLARED plaintext store is not rescued by the env switch" {
            // Declaration beats recognition: the switch records what was
            // asked for, and a companion that fell back to the local
            // default still matches it. This is the Phase 138 gap — a store
            // that says it writes plaintext must not pass because an env
            // var names a vault.
            withEnv ConfigKeys.Names.secretStore (Some "azure-key-vault") (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    match validate (cfg Surfaces.individual false) (rawFileStore ()) with
                    | Error _ -> ()
                    | other -> failtestf "the composed store's own declaration must win; got %A" other))
        }

        // ── SkipPreflight ──────────────────────────────────────────────

        test "SkipPreflight = true still refuses — the validator is security-class" {
            withEnv ConfigKeys.Names.secretStore None (fun () ->
                withEnv ConfigKeys.Names.acceptPlaintextSecrets None (fun () ->
                    let services = ServiceCollection()

                    services.AddSingleton<IConfigValidator>(
                        validator (cfg Surfaces.individual false) (rawFileStore ())
                    )
                    |> ignore

                    try
                        ConfigValidatorAggregator.validate services None true |> ignore

                        failtest
                            "expected ConfigPreflightFailedException — SkipPreflight must not bypass a security-class refusal"
                    with :? ConfigValidatorAggregator.ConfigPreflightFailedException as ex ->
                        Expect.stringContains
                            ex.Message
                            "secret-store-at-rest-posture"
                            "the refusal named this validator"))
        }

        // ── The posture declarations themselves ────────────────────────

        test "each shipped store declares the posture it actually has" {
            let posture (store: ISecretStore) =
                match box store with
                | :? ISecretStoreAtRestPosture as d -> d.AtRestPosture
                | _ -> failtest "store does not declare a posture"

            match posture (encryptingStore ()) with
            | EncryptsAtRest mechanism -> Expect.stringContains mechanism "AES-256-GCM" "names the mechanism"
            | other -> failtestf "EncryptedSecretStore with a key must encrypt; got %A" other

            match posture (passthroughStore ()) with
            | PlaintextAtRest _ -> ()
            | other -> failtestf "EncryptedSecretStore without a key is plaintext-passthrough; got %A" other

            match posture (rawFileStore ()) with
            | PlaintextAtRest _ -> ()
            | other -> failtestf "FileSecretStore writes flat JSON; got %A" other

            match posture (envStore ()) with
            | PlaintextAtRest _ -> ()
            | other -> failtestf "EnvironmentSecretStore serves unencrypted process state; got %A" other
        }

        test "ResilientSecretStore DELEGATES the posture rather than answering for itself" {
            // A retry policy must never be able to turn a KMS-backed
            // deployment into a refusal.
            let wrapped =
                ResilientSecretStore.ResilientSecretStore(
                    encryptingStore (),
                    TransientFault.TransientFaultPolicy.identity
                )
                :> ISecretStore

            withEnv ConfigKeys.Names.secretStore None (fun () ->
                Expect.equal
                    (validate (cfg Surfaces.individual false) wrapped)
                    Ok
                    "wrapping an encrypting store in the resilience decorator keeps it encrypting")

            let wrappedUndeclaring =
                ResilientSecretStore.ResilientSecretStore(
                    undeclaringStore (),
                    TransientFault.TransientFaultPolicy.identity
                )
                :> ISecretStore

            match box wrappedUndeclaring with
            | :? ISecretStoreAtRestPosture as d ->
                match d.AtRestPosture with
                | UnknownAtRest _ -> ()
                | other -> failtestf "a decorator over an undeclaring store knows nothing, and says so; got %A" other
            | _ -> failtest "ResilientSecretStore must declare a (delegated) posture"
        }

        test "Validator metadata is well-formed" {
            let v = validator (cfg Surfaces.anonymous false) (rawFileStore ())
            Expect.equal v.Name "secret-store-at-rest-posture" "stable identifier"
            Expect.isGreaterThan v.Timeout.TotalMilliseconds 0.0 "non-zero timeout"
        }
    ]