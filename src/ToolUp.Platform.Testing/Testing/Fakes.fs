// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.Fakes

open System
open System.Collections.Concurrent
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.Auth
open ToolUp.Platform.StorageScopeResolver

// ─── Fakes for SDK interfaces ─────────────────────────────────────────
//
// Test doubles that satisfy each SDK interface with an in-memory
// implementation suitable for unit tests. Every fake is hermetic — no
// disk, no network, no clock dependency beyond `DateTime.UtcNow` for
// metadata stamps. Pass each fake through the matching contract pack
// in `ToolUp.Platform.Tests.Contracts.*Contract` to verify portability.

/// In-memory `IBlobStorage`. Per-(container,blob) byte buffer in a
/// concurrent dictionary; `List` is a prefix scan within a container.
type TestBlobStorage() =
    let blobs = ConcurrentDictionary<string * string, byte[]>()

    interface IBlobStorage with
        member this.Erase(container, prefix, policy, dryRun) =
            ToolUp.Platform.BlobStorage.eraseByPrefix (this :> IBlobStorage) container prefix policy dryRun

        member _.Upload(container, blobName, content) = async {
            blobs[(container, blobName)] <- content
            return Ok blobName
        }

        member _.Download(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes -> return Ok bytes
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

        member _.Delete(container, blobName) = async {
            blobs.TryRemove((container, blobName)) |> ignore
            return Ok()
        }

        member _.List(container, prefix) = async {
            return
                blobs.Keys
                |> Seq.filter (fun (c, n) -> c = container && n.StartsWith prefix)
                |> Seq.map snd
                |> List.ofSeq
        }

        member _.Exists(container, blobName) = async { return blobs.ContainsKey((container, blobName)) }

        member _.GetMetadata(container, blobName) = async {
            match blobs.TryGetValue((container, blobName)) with
            | true, bytes ->
                return
                    Ok {
                        Size = int64 bytes.Length
                        LastModified = DateTime.UtcNow
                        ContentType = None
                    }
            | false, _ -> return Error $"not found: {container}/{blobName}"
        }

/// In-memory `IEventStore`. Thin alias over the SDK's
/// `InMemoryEventStore`, re-exposed under the Testing namespace so
/// test authors don't need to know which Platform module owns the
/// default impl.
type TestEventStore() =
    let inner = InMemoryEventStore.InMemoryEventStore()

    interface IEventStore with
        member _.Write(event) = (inner :> IEventStore).Write(event)
        member _.ReadAll(scopeId) = (inner :> IEventStore).ReadAll(scopeId)

        member _.ReadByType(scopeId, eventType) =
            (inner :> IEventStore).ReadByType(scopeId, eventType)

        member _.ReadBySource(scopeId, sourceModule) =
            (inner :> IEventStore).ReadBySource(scopeId, sourceModule)

        member _.ListScopes() = (inner :> IEventStore).ListScopes()

        member _.Erase(scopeId, subjectUserId, policy, dryRun) =
            (inner :> IEventStore).Erase(scopeId, subjectUserId, policy, dryRun)

/// In-memory `ISecretStore`. Per-(scope,key) string entries in a
/// concurrent dictionary; writes always succeed, deletes are
/// idempotent.
type TestSecretStore() =
    let secrets = ConcurrentDictionary<string * string, string>()

    /// Pre-seed a secret without going through `SetSecret`. Useful for
    /// arrange-act-assert flows that don't want to assert on the write
    /// path.
    member _.Seed(scopeId: string, key: string, value: string) = secrets[(scopeId, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match secrets.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            secrets[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            secrets.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                secrets.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

/// Configurable `IAuthProvider`. Constructed with a fixed authenticated
/// user; `GetUser` returns it unconditionally, `ValidateRequest`
/// returns `Ok` of the same user. Pass `AuthenticatedUser.anonymous`
/// for a "no credentials" deployment shape.
///
/// `isCryptographicallyVerified` defaults to `true` — the fake stands in
/// for a real verified provider in the common arrange case, so composing
/// it in an auth-requiring mode does not trip the unverified-provider
/// startup gate. Pass `false` to model a header-trusting / unverified
/// provider for tests that exercise that gate.
type TestAuthProvider(user: AuthenticatedUser, ?isCryptographicallyVerified: bool) =
    let verified = defaultArg isCryptographicallyVerified true

    new() = TestAuthProvider(AuthenticatedUser.anonymous)

    interface IAuthProvider with
        member _.GetUser(_) = async { return user }
        member _.ValidateRequest(_) = async { return Ok user }
        member _.IsCryptographicallyVerified = verified

/// `IStorageScopeResolver` that returns a fixed `StorageScope`. The
/// real resolvers branch on platform mode + request headers; tests
/// usually want the resolved scope as an arrange input rather than
/// to exercise the resolver itself.
type TestStorageScopeResolver(scope: StorageScope) =
    new(scopeId: string) =
        TestStorageScopeResolver(
            {
                ScopeId = scopeId
                Container = $"test-{scopeId}"
                Persist = false
            }
        )

    interface IStorageScopeResolver with
        member _.Resolve(_) = async { return Ok scope }

/// Convenience constructors so tests can compose substrate with one
/// expression: `let fakes = Fakes.standard ()` returns a record of
/// every fake wired with sensible defaults. Tests that need to control
/// a single fake construct it directly.
let standardBlobStorage () : IBlobStorage = TestBlobStorage() :> _
let standardEventStore () : IEventStore = TestEventStore() :> _
let standardSecretStore () : ISecretStore = TestSecretStore() :> _

let anonymousAuthProvider () : IAuthProvider =
    TestAuthProvider(AuthenticatedUser.anonymous) :> _

let authedAuthProvider (user: AuthenticatedUser) : IAuthProvider = TestAuthProvider(user) :> _

let fixedScopeResolver (scopeId: string) : IStorageScopeResolver = TestStorageScopeResolver(scopeId) :> _