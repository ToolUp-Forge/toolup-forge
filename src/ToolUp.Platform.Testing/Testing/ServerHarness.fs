// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Testing.ServerHarness

open ToolUp.Platform
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.Testing.Fakes

// ─── Server-side HTTP-free harness ────────────────────────────────────
//
// Wraps the four fakes most module server code needs (storage / events
// / secrets / auth + a scope resolver) in a record, alongside the
// module's own API record so call sites read like `harness.Api.method
// args`. Tests construct via `ServerHarness.create` or
// `ServerHarness.createWithFakes` for the common variants; pass
// custom fakes for the few cases that need to control a single
// substrate.
//
// The harness does NOT spin up Saturn / Giraffe. Modules that handle
// requests inside their server-side `apiFor` should expose an
// `apiFor` (or equivalent) that takes the resolved scope + DI
// dependencies and returns the API record; the harness invokes that
// builder directly with the seeded fakes.

/// The collection of fakes most module server code depends on.
type ServerFakes = {
    BlobStorage: IBlobStorage
    EventStore: IEventStore
    SecretStore: ISecretStore
    AuthProvider: IAuthProvider
    ScopeResolver: IStorageScopeResolver
    Scope: StorageScope
}

/// Combines a `ServerFakes` record with the API under test. Tests
/// invoke `harness.Api.SomeMethod args` directly.
type ServerHarness<'Api> = { Fakes: ServerFakes; Api: 'Api }

/// Default fakes for the "single-tenant anonymous session" shape.
/// Every fake is fresh — no shared state across `createDefaultFakes`
/// calls. Override individual fields via the `with` keyword before
/// passing to `withApi`.
let createDefaultFakes () : ServerFakes =
    let scope = {
        ScopeId = "test-scope"
        Container = "test-scope"
        Persist = false
    }

    {
        BlobStorage = TestBlobStorage() :> IBlobStorage
        EventStore = TestEventStore() :> IEventStore
        SecretStore = TestSecretStore() :> ISecretStore
        AuthProvider = TestAuthProvider(AuthenticatedUser.anonymous) :> IAuthProvider
        ScopeResolver = TestStorageScopeResolver(scope) :> IStorageScopeResolver
        Scope = scope
    }

/// Bind a pre-built API record into a harness with the supplied
/// fakes. Useful when the API record is constructed by an external
/// composition root rather than the test itself.
let withApi (fakes: ServerFakes) (api: 'Api) : ServerHarness<'Api> = { Fakes = fakes; Api = api }

/// Construct a harness given a builder that turns the fakes into an
/// API record. The builder shape matches the SDK convention: server
/// modules expose a function `apiFor: deps -> 'Api` where `deps` is
/// the substrate the module needs. The test supplies a tailored
/// builder that pulls the fields it wants from `ServerFakes`.
let create (build: ServerFakes -> 'Api) : ServerHarness<'Api> =
    let fakes = createDefaultFakes ()
    { Fakes = fakes; Api = build fakes }

/// Construct a harness using a caller-supplied `ServerFakes` (e.g.
/// when one fake needs pre-seeding).
let createWithFakes (fakes: ServerFakes) (build: ServerFakes -> 'Api) : ServerHarness<'Api> = {
    Fakes = fakes
    Api = build fakes
}

/// Re-seed the harness's blob storage with a single entry. Convenience
/// for arrange-act-assert flows; returns the same harness for
/// chaining.
let seedBlob
    (harness: ServerHarness<'Api>)
    (container: string)
    (blobName: string)
    (content: byte[])
    : ServerHarness<'Api> =
    async { return! harness.Fakes.BlobStorage.Upload(container, blobName, content) }
    |> Async.RunSynchronously
    |> ignore

    harness

/// Re-seed a secret.
let seedSecret (harness: ServerHarness<'Api>) (scopeId: string) (key: string) (value: string) : ServerHarness<'Api> =
    match harness.Fakes.SecretStore with
    | :? TestSecretStore as ts -> ts.Seed(scopeId, key, value)
    | other ->
        async { return! other.SetSecret(scopeId, key, value) }
        |> Async.RunSynchronously
        |> ignore

    harness