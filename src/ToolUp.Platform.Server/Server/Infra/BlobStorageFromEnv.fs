// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BlobStorageEnv

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

/// Resolver for one cloud-blob-storage companion. The consumer passes
/// one entry per companion the deployment has wired (e.g. `{ Name =
/// "azure"; Resolve = fun () -> ToolUp.Storage.AzureBlobStorage.fromEnv
/// None }`). Keeps `ToolUp.Platform.Server` free of any direct
/// dependency on cloud-storage companion packages.
type CloudBlobStorageResolver = {
    /// Matched against `TOOLUP_BLOB_STORAGE` (case-insensitive). Common
    /// values: `"azure"`, `"s3"`, `"gcs"`.
    Name: string
    /// The companion's `fromEnv` wrapped to `unit -> IBlobStorage
    /// option`. Azure's companion takes an explicit `rootContainer:
    /// string option` argument — wrap in a closure: `Resolve = fun ()
    /// -> ToolUp.Storage.AzureBlobStorage.fromEnv None`.
    Resolve: unit -> IBlobStorage option
}

let private envVar (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | v -> Some v

/// Build the deployment's `IBlobStorage` from `TOOLUP_BLOB_STORAGE`.
/// Recognised values:
///
///   - unset / `local` (default) — `LocalFileStorage` against
///     `{CurrentDirectory}/data`.
///   - any value matched by a `cloudResolvers` entry — resolve from
///     that companion. Falls back to local filesystem with a warning
///     when the resolver returns `None`.
///   - unrecognised value — falls back to local filesystem with a
///     warning naming the recognised values.
///
/// Warning text and behaviour are byte-for-byte identical to the
/// hand-written dispatch this helper replaces in a consumer's
/// composition root.
let fromEnv (logger: ILogger) (cloudResolvers: CloudBlobStorageResolver list) : IBlobStorage =
    let defaultLocal () =
        let storagePath = Path.Combine(Directory.GetCurrentDirectory(), "data")
        logger.Info $"Blob storage: local filesystem at {storagePath}"
        LocalFileStorage.LocalFileStorage(storagePath) :> IBlobStorage

    let resolveCloud (resolver: CloudBlobStorageResolver) =
        match resolver.Resolve() with
        | Some store ->
            logger.Info $"Blob storage: {resolver.Name}"
            store
        | None ->
            logger.Warn
                $"TOOLUP_BLOB_STORAGE={resolver.Name} but the required env vars are not set. Falling back to local filesystem."

            defaultLocal ()

    let chosen = envVar "TOOLUP_BLOB_STORAGE" |> Option.map _.ToLowerInvariant()

    match chosen with
    | None
    | Some "local" -> defaultLocal ()
    | Some other ->
        match
            cloudResolvers
            |> List.tryFind (fun r -> r.Name.Equals(other, StringComparison.OrdinalIgnoreCase))
        with
        | Some resolver -> resolveCloud resolver
        | None ->
            let recognisedNames =
                "local" :: (cloudResolvers |> List.map _.Name) |> String.concat ", "

            logger.Warn
                $"TOOLUP_BLOB_STORAGE={other} not recognised. Valid values: {recognisedNames}. Falling back to local filesystem."

            defaultLocal ()