// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Support.InMemoryStores

open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets

/// Trivial in-memory `ISecretStore` for the integration tests. Mirrors
/// the local `InMemorySecretStore` used by `ShareTokenStoreTests` /
/// `BlobPlatformAIKeyStoreTests` — kept inline rather than extracted
/// because each test pack wants its own isolated state.
type InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// Trivial in-memory `IProviderProfile` for the integration tests. The
/// blob-backed `BlobProviderProfile` is already exercised by the
/// `IProviderProfileContract` pack against `LocalFileStorage`; this
/// fake exists so the per-provider factory round-trip stays free of
/// disk I/O and survives parallel test runs without temp-dir contention.
type InMemoryProviderProfile() =
    let store = ConcurrentDictionary<string, ProviderProfile>()

    interface IProviderProfile with
        member _.Get(scope: StorageScope) = async {
            match store.TryGetValue(scope.Container) with
            | true, p -> return Some p
            | false, _ -> return None
        }

        member _.Set(scope: StorageScope, profile: ProviderProfile) = async {
            store[scope.Container] <- profile
            return Ok()
        }

        member _.Clear(scope: StorageScope) = async {
            store.TryRemove(scope.Container) |> ignore
            return ()
        }

        member this.ResolveEntry(scope: StorageScope, surface: string, context: string option) = async {
            let! profileOpt = (this :> IProviderProfile).Get scope

            match profileOpt with
            | None -> return None
            | Some profile -> return ProviderProfile.resolveEntry surface context profile
        }

        member this.SetEntryHealth(scope: StorageScope, label: string, health: ProviderHealth) = async {
            match store.TryGetValue(scope.Container) with
            | false, _ -> return Ok()
            | true, profile ->
                let updatedEntries =
                    profile.Entries
                    |> List.map (fun e -> if e.Label = label then { e with Health = health } else e)

                store[scope.Container] <- {
                    profile with
                        Entries = updatedEntries
                }

                return Ok()
        }