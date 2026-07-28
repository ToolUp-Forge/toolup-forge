// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmCatalogApi

open ToolUp.Platform // forge-native auth attributes
open ToolUp.Algorithms.AlgorithmTypes

// ─── Phase 11.E.2 — ToolUp.Remoting wire contract ───────────────────
//
// Read-only catalog listing. The client tier renders "what analytical
// primitives does this deployment offer", which is deployment
// metadata — not tenant data, and not an execution surface. **There is
// deliberately no Execute method here.**
//
// Why execution is not on the wire in this phase: an analytical call
// carries the caller's data by value, so exposing it as an unmetered,
// unbudgeted remoting endpoint would let any authenticated session post
// an arbitrarily large sample and consume server compute on it. The
// two shipped execution paths are both server-side and already bounded
// by their own substrate — the AI tool surface (agent-loop budgeted)
// and direct `IAlgorithmDispatcher` resolution from a module's own
// handler, where the module owns the input size. A public execute
// endpoint is a separate decision with its own quota design, and
// nothing here forecloses it.
//
// **Authorisation.** The catalog is a declaration surface: algorithm
// ids, parameter shapes and provider stamps, identical for every caller
// in the deployment and carrying no tenant content. Classified
// `AllowAnonymous` — an honest statement of what the handler does, not
// a policy claim. A deployment wanting the catalog gated puts the
// module behind its own RBAC key via `ServerModule.Name`.

/// ToolUp.Remoting record-of-functions. Server-side handler in
/// `Server/AlgorithmCatalogApiHandler.fs` resolves `IAlgorithmCatalog`
/// from DI and delegates; the client proxy is
/// `ToolUp.Algorithms.AlgorithmsClient.proxy`.
type IAlgorithmCatalogApi = {
    /// Every registered algorithm, in provider-registration order.
    [<AllowAnonymous>]
    ListAlgorithms: unit -> Async<AlgorithmInfo list>

    /// One algorithm by id. `None` when nothing is registered under it.
    [<AllowAnonymous>]
    GetAlgorithm: AlgorithmId -> Async<AlgorithmInfo option>

    /// Every registered algorithm of one kind, in registration order.
    /// Takes the kind's wire string (`AlgorithmKind.name`) rather than
    /// the DU so an unknown tag is an empty result rather than a
    /// deserialisation failure at the transport boundary.
    [<AllowAnonymous>]
    ListByKind: string -> Async<AlgorithmInfo list>
}

/// HTTP route prefix. Mirrors the `IFormApi` / `ISchedulingApi`
/// convention so the client proxy and the server handler agree on the
/// URL shape without naming routes individually.
let routeBuilder (typeName: string) (methodName: string) : string =
    sprintf "/api/%s/%s" typeName methodName