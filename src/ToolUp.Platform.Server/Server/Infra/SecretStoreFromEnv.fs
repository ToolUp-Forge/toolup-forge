// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.SecretStore

open System
open ToolUp.Platform
open ToolUp.Platform.Secrets

/// Resolver for one cloud-KMS-backed `ISecretStore` companion. The
/// consumer threads in one entry per companion the deployment has
/// wired (e.g. `{ Name = "azure-key-vault"; Resolve =
/// ToolUp.Secrets.AzureKeyVault.fromEnv }`). Keeps
/// `ToolUp.Platform.Server` free of any direct dependency on cloud
/// companion packages (substrate cleanliness — companion packages
/// exist only at the SDK boundary, per `CLAUDE.md`).
type CloudSecretStoreResolver = {
    /// Matched against `TOOLUP_SECRET_STORE` (case-insensitive). Common
    /// values: `"azure-key-vault"`, `"aws-secrets-manager"`, `"vault"`,
    /// `"gcp-secret-manager"`.
    Name: string
    /// The companion's existing `fromEnv : unit -> ISecretStore option`.
    /// Returns `None` when the companion's required env vars aren't set
    /// — `fromEnv` below falls back to the local encrypted store with
    /// a warning, matching the hand-written reference behaviour.
    Resolve: unit -> ISecretStore option
}

// Phase 698 — resolved through the Phase-696 `ConfigResolution` seam, so a
// manifest can declare which backend the deployment runs on. Absent a
// manifest the seam is the environment read it replaces (GP 11).

/// Build the deployment's `ISecretStore` from `TOOLUP_SECRET_STORE`.
/// Recognised values:
///
///   - unset / `encrypted` (default) — `EncryptedSecretStore` over
///     `FileSecretStore`, master key from `TOOLUP_SECRETS_MASTER_KEY`.
///     Cloud-KMS modes carry their own at-rest encryption so
///     are NOT wrapped — `EncryptedSecretStore` would add a redundant
///     envelope.
///   - `file` — `FileSecretStore`, no encryption envelope.
///   - `env` — `EnvironmentSecretStore`, read-only.
///   - any value matched by a `cloudResolvers` entry — resolve from
///     that companion. Falls back to the encrypted local default with
///     a warning when the resolver returns `None`.
///   - unrecognised value — falls back to encrypted local default
///     with a warning naming the recognised values.
///
/// Store selection is byte-for-byte identical to the hand-written
/// dispatch this helper replaces in a consumer's composition root. The
/// at-rest warning is not: Phase 457 replaced the master-key-specific
/// line on the default path with one driven by the composed store's
/// declared posture, so `file` and `env` — plaintext by construction, and
/// previously silent — now say so too.
/// Phase 457 — the runtime half of the at-rest posture signal.
///
/// Until this phase the only boot-time line about plaintext secrets was
/// emitted on ONE composition path (the default encrypted-file store with
/// no master key). A deployment that chose `TOOLUP_SECRET_STORE=file` or
/// `=env` — both plaintext by construction — booted in total silence, and
/// a deployment that chose a cloud store whose resolver fell back to the
/// local default got a fallback warning that said nothing about at-rest
/// exposure.
///
/// The signal is now driven by what the composed store DECLARES, so it
/// covers every path through the dispatch below, and it names the
/// security-class validator that will refuse alongside it — the log line
/// is a corroboration of the preflight refusal, never the sole signal a
/// deployment gets (`SkipPreflight` cannot suppress the refusal, because
/// `SecretStoreAtRestPostureValidator` is `ISecurityClassValidator`).
///
/// A store that declares nothing is left alone here on purpose: the
/// preflight resolver owns the recognitions (the `TOOLUP_SECRET_STORE`
/// carve-out for an undeclaring companion), and a composition-time guess
/// that contradicted it would be worse than silence.
let private warnIfPlaintextAtRest (logger: ILogger) (store: ISecretStore) : ISecretStore =
    match box store with
    | :? ISecretStoreAtRestPosture as declared ->
        match declared.AtRestPosture with
        | EncryptsAtRest _ -> ()
        | PlaintextAtRest reason
        | UnknownAtRest reason ->
            logger.Warn
                $"Secrets are NOT encrypted at rest: {reason}. Every BYOK provider key, OAuth token and webhook secret this deployment stores is readable by anything that can read the storage medium. An authenticated deployment is refused startup for this by the security-class 'secret-store-at-rest-posture' preflight validator (which SkipPreflight cannot bypass); set TOOLUP_SECRETS_MASTER_KEY with the EncryptedSecretStore decorator composed, switch to a KMS-backed store via TOOLUP_SECRET_STORE, or acknowledge the medium's own encryption with TOOLUP_ACCEPT_PLAINTEXT_SECRETS=1."
    | _ -> ()

    store

let fromEnv (logger: ILogger) (cloudResolvers: CloudSecretStoreResolver list) : ISecretStore =
    let masterKey = EncryptedSecretStore.masterKeyFromEnvironment ()

    let defaultStore () =
        let inner = FileSecretStore.FileSecretStore() :> ISecretStore
        logger.Info "Secret store: encrypted file (default)"
        EncryptedSecretStore.EncryptedSecretStore(inner, masterKey) :> ISecretStore

    let resolveCloud (resolver: CloudSecretStoreResolver) =
        match resolver.Resolve() with
        | Some store ->
            logger.Info $"Secret store: {resolver.Name}"
            store
        | None ->
            logger.Warn
                $"TOOLUP_SECRET_STORE={resolver.Name} but the required env vars are not set. Falling back to encrypted file store."

            defaultStore ()

    let chosen =
        ConfigResolution.tryValue ConfigKeys.Names.secretStore
        |> Option.map _.ToLowerInvariant()

    // Every arm funnels through the posture signal, so no composition path
    // can be the one that stays quiet — which is exactly how `file` and
    // `env` booted silently before Phase 457.
    warnIfPlaintextAtRest
        logger
        (match chosen with
         | None
         | Some "encrypted" -> defaultStore ()
         | Some "file" ->
             logger.Info "Secret store: file (unencrypted)"
             FileSecretStore.FileSecretStore() :> ISecretStore
         | Some "env" ->
             logger.Info "Secret store: environment variables (read-only)"
             EnvironmentSecretStore.EnvironmentSecretStore() :> ISecretStore
         | Some other ->
             match
                 cloudResolvers
                 |> List.tryFind (fun r -> r.Name.Equals(other, StringComparison.OrdinalIgnoreCase))
             with
             | Some resolver -> resolveCloud resolver
             | None ->
                 let recognisedNames =
                     [ "encrypted"; "file"; "env" ] @ (cloudResolvers |> List.map _.Name)
                     |> String.concat ", "

                 logger.Warn
                     $"TOOLUP_SECRET_STORE={other} not recognised. Valid values: {recognisedNames}. Falling back to encrypted file store."

                 defaultStore ())