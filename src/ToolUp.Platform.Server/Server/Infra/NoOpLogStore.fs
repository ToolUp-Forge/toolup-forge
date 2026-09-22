// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Phase 828 — NoOpLogStore ───────────────────────────────────────────
//
// The null object of `ILogStore`: `Append` discards, `Search` returns `[]`,
// `Prune` returns `0`. It stores nothing and never fails.
//
// **This is NOT what `LogStoreMode.NoLogStore` composes.** The default mode
// registers no `ILogStore` at all and decorates no logger, which is what
// makes the opt-out cost literally nothing (GP 13) and the boot path
// byte-identical (GP 11) — a no-op singleton would still be an allocation,
// a DI entry and a decorated logger on every line. This type exists for the
// two callers that genuinely want a resolvable-but-inert store: a consumer
// whose own code resolves `ILogStore` unconditionally and would rather
// branch once at compose than at every call site, and a test that needs the
// seam present without a file on disk.
//
// Because it stores nothing it is deliberately NOT bound to
// `ILogStoreContract` — the round-trip laws are the contract, and a store
// that keeps nothing cannot satisfy them. Its own behaviour is pinned
// directly instead.

/// Inert `ILogStore` — discards every append, finds nothing, prunes
/// nothing. See the note above on why this is not what `NoLogStore`
/// composes.
type NoOpLogStore() =
    interface ILogStore with
        member _.Append(_entry: LogRecord) = async { return () }
        member _.Search(_query: LogSearchQuery) = async { return [] }
        member _.Prune(_olderThanUtc: DateTime) = async { return 0 }

[<RequireQualifiedAccess>]
module NoOpLogStore =
    /// Construct the inert `ILogStore`.
    let create () : ILogStore = NoOpLogStore() :> ILogStore