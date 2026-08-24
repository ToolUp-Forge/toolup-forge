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
///     Emits a `Warn` when the master key is missing (plaintext at
///     rest). Cloud-KMS modes carry their own at-rest encryption so
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
/// Warning text and behaviour are byte-for-byte identical to the
/// hand-written dispatch this helper replaces in a consumer's
/// composition root.
let fromEnv (logger: ILogger) (cloudResolvers: CloudSecretStoreResolver list) : ISecretStore =
    let masterKey = EncryptedSecretStore.masterKeyFromEnvironment ()

    let defaultStore () =
        if masterKey.IsNone then
            logger.Warn
                "TOOLUP_SECRETS_MASTER_KEY not set. Secrets stored at rest as plaintext. Production deployments should set this to a base64-encoded 32-byte key (see EncryptedSecretStore.generateMasterKey for one-time setup) or switch to a cloud-KMS-backed store via TOOLUP_SECRET_STORE."

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

    match chosen with
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

            defaultStore ()