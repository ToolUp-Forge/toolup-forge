/// The test pack's stand-in for the platform's scope resolution, and its
/// reading of a fact-store answer.
///
/// A `ResolvedScope` is minted by the resolver and nowhere else. The test
/// pack reaches the resolver's internal mint (`ScopeResolution.ofStorageScope`,
/// through `InternalsVisibleTo`) to stand in for the middleware a request
/// would have passed through — exactly as the contract packs always have —
/// and this module is the one place it does so for a bare shard id, so a
/// test names the shard it means and nothing else mints.
[<RequireQualifiedAccess>]
module ToolUp.Platform.TestScope

open ToolUp.Platform
open ToolUp.Facts

/// The resolved scope of the shard `scopeId`, as the resolver would mint it
/// for a principal that resolved to it (its container is the shard id, the
/// shape the pack's blob fixtures write under).
let resolved (scopeId: string) : ResolvedScope =
    StorageScopeResolver.ScopeResolution.ofStorageScope {
        ScopeId = scopeId
        Container = scopeId
        Persist = true
    }

/// A fact-store answer a test expects to succeed: its value, or the store's
/// error raised as `FactStoreFault` — a test that meets a fault fails naming
/// it, never reads it as an empty answer.
let ok (answer: Async<Result<'T, FactStoreError>>) : Async<'T> = FactStoreResult.orRaiseAsync answer
