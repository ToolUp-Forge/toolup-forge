// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmsClient

open ToolUp.Platform
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmCatalogApi

// ─── Phase 11.E.2 — ToolUp.Remoting client proxy ────────────────────
//
// Thin wrapper over `Api.makeProxy<IAlgorithmCatalogApi>` so callers do
// not import `UserSession.withRequestHeaders` themselves. Same
// convention as the Forms / AI / KnowledgeBase clients.
//
// **Deliberately sparse.** The client tier lists the catalog and
// nothing else — there is no execution surface on the wire (see
// `AlgorithmCatalogApi.fs` for why). A client-side UI renders "what
// this deployment can compute"; the computing happens server-side,
// reached through the AI tool family or a module's own handler.

/// The proxy. Lazy-initialised at first access; platform-default
/// `RemoteBuilderOptions`.
let proxy: IAlgorithmCatalogApi =
    Api.makeProxy<IAlgorithmCatalogApi> (customOptions = UserSession.withRequestHeaders)

/// Every registered algorithm.
let listAlgorithms () : Async<AlgorithmInfo list> = proxy.ListAlgorithms()

/// One algorithm by id.
let getAlgorithm (algorithmId: AlgorithmId) : Async<AlgorithmInfo option> = proxy.GetAlgorithm algorithmId

/// Every registered algorithm of one kind. Takes the DU and maps to the
/// wire string, so a client call site stays typed while the wire stays
/// tolerant of an unknown tag.
let listByKind (kind: AlgorithmKind) : Async<AlgorithmInfo list> =
    proxy.ListByKind(AlgorithmKind.name kind)