// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Algorithms

open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — IAlgorithmCatalog ───────────────────────────────
//
// Server-side query surface over the algorithms a deployment offers.
// Deliberately shaped after `IDataCatalog` (Phase 7a): the same
// snapshot-at-compose model, the same async-everywhere boundary, and
// the same "identity is a string, never a handle" posture — an AI tool
// that already knows how to enumerate data types learns nothing new to
// enumerate algorithms.
//
// **Stateless contract (GP 12 rule 4).** The catalog snapshots the
// registered providers' declarations at construction. The provider set
// is fixed for the server's lifetime, so the snapshot is by-value
// equivalent to repeated lookups.
//
// **Async at every boundary (GP 12 rule 2).** Even the in-process
// implementation returns `Async<_>` from every method, so a
// distributed catalog is a drop-in without touching call sites.

/// Server-side query interface for the algorithm catalog. Registered in
/// DI by `AlgorithmsCompose.withAlgorithms`; consumed by the
/// `IAlgorithmCatalogApi` handler, by the algorithm AI tools, and by
/// the `/dev/inspect` contributor.
type IAlgorithmCatalog =
    /// Every registered algorithm, in provider-registration order
    /// (providers in composition order, each provider's declarations in
    /// declaration order). Every entry carries its provider stamp.
    abstract ListAlgorithms: unit -> Async<AlgorithmInfo list>

    /// One algorithm by id. `None` when no provider declared it.
    abstract GetAlgorithm: algorithmId: AlgorithmId -> Async<AlgorithmInfo option>

    /// Every registered algorithm of one kind, in registration order.
    /// Empty when no provider declares that kind.
    abstract ListByKind: kind: AlgorithmKind -> Async<AlgorithmInfo list>

/// Server-side execution surface. Resolves `algorithmId` to the
/// provider that declared it, validates the request, and delegates.
///
/// Split from `IAlgorithmCatalog` deliberately: a consumer that only
/// needs to enumerate (a UI, a `/dev/inspect` panel, a model deciding
/// what is available) takes a dependency on the read surface alone, and
/// a deployment can compose a catalog with no execution path at all.
type IAlgorithmDispatcher =
    /// Execute `invocation` against `algorithmId`.
    ///
    /// Failure modes, all typed: `NotFound` when no provider declared
    /// the id; `InvalidArguments` when the request fails the shared
    /// validation in `AlgorithmValidation`; `KindMismatch` when the
    /// invocation's shape disagrees with the declared kind;
    /// `Unsupported` when the resolved provider cannot serve it; and
    /// `ExecutionFailed` wrapping anything the provider raised.
    abstract Execute:
        algorithmId: AlgorithmId * invocation: AlgorithmInvocation -> Async<Result<AlgorithmOutcome, AlgorithmError>>