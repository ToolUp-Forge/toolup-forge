// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.BlobProviderProfile

open System
open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Providers

// Default IProviderProfile implementation — one JSON blob per scope
// ("provider-profile.json"). Phase 42.B landed this behind a
// behaviour-preserving IUserAIConfigStore shim; Phase 43.A removed
// the shim, making this the sole canonical BYOK store (the AI
// assistant's legacy "ai-config.json" one-time migration now lives in
// ToolUp.AI's LegacyAIConfigProviderProfile decorator, not here —
// this store stays AI-free). FableJsonConverter is required because
// the profile shape contains F# DUs (ProviderEntry.Model: string
// option, CredentialOrigin, ProviderHealthStatus) that
// System.Text.Json cannot round-trip without hand-rolling the shape.

module private Json =
    let private settings =
        let s = JsonSerializerSettings()
        s.Converters.Add(FableJsonConverter())
        s

    let serialize (value: 'T) : string =
        JsonConvert.SerializeObject(value, settings)

    let deserialize<'T> (json: string) : 'T =
        JsonConvert.DeserializeObject<'T>(json, settings)

/// Blob name within a scope's (already scope-isolated) container.
let private blobName = "provider-profile.json"

/// Build an IProviderProfile backed by an IBlobStorage. Persists one
/// JSON blob per StorageScope. Malformed blobs are treated as missing
/// configuration so a consumer falls back per its own policy rather
/// than surfacing an opaque parse error (tolerant-read posture —
/// preserved from the pre-43.A shim it superseded).
let create (storage: IBlobStorage) : IProviderProfile =
    let read (scope: StorageScope) : Async<ProviderProfile option> = async {
        let! result = storage.Download(scope.Container, blobName)

        match result with
        | Ok bytes ->
            try
                let json = Encoding.UTF8.GetString(bytes)
                return Some(Json.deserialize<ProviderProfile> json)
            with _ ->
                return None
        | Error _ -> return None
    }

    let write (scope: StorageScope) (profile: ProviderProfile) : Async<Result<unit, string>> = async {
        let bytes = Encoding.UTF8.GetBytes(Json.serialize profile)
        let! result = storage.Upload(scope.Container, blobName, bytes)
        return result |> Result.map (fun _ -> ())
    }

    { new IProviderProfile with
        member _.Get scope = read scope

        member _.Set(scope, profile) = write scope profile

        member _.Clear scope = async {
            let! _ = storage.Delete(scope.Container, blobName)
            // Delete errors (blob absent, etc.) are swallowed — Clear
            // is idempotent by contract.
            return ()
        }

        member _.ResolveEntry(scope, surface, context) = async {
            let! profile = read scope

            return profile |> Option.bind (ProviderProfile.resolveEntry surface context)
        }

        member _.SetEntryHealth(scope, label, health) = async {
            let! profile = read scope

            match profile with
            | None ->
                // No profile for the scope — nothing to attach health
                // to. No-op success per contract.
                return Ok()
            | Some p ->
                let hasEntry = p.Entries |> List.exists (fun e -> e.Label = label)

                if not hasEntry then
                    // Stale / unknown label — no-op success per
                    // contract (the probe may race entry deletion).
                    return Ok()
                else
                    let entries =
                        p.Entries
                        |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)

                    return!
                        write scope {
                            p with
                                Entries = entries
                                UpdatedAt = DateTime.UtcNow
                        }
        }
    }