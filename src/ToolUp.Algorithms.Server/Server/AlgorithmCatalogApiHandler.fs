// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmCatalogApiHandler

open Microsoft.AspNetCore.Http
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmCatalogApi

// ─── Phase 11.E.2 — ToolUp.Remoting handler ─────────────────────────
//
// Builds a per-request `IAlgorithmCatalogApi` over the DI-registered
// `IAlgorithmCatalog`. The catalog is deployment metadata identical for
// every caller, so there is no scope resolution here — nothing in the
// response varies by tenant, which is exactly why the contract is
// classified `AllowAnonymous`.

/// Per-request API record over a resolved catalog.
let algorithmCatalogApi (catalog: IAlgorithmCatalog) (_ctx: HttpContext) : IAlgorithmCatalogApi = {
    ListAlgorithms = fun () -> catalog.ListAlgorithms()
    GetAlgorithm = fun id -> catalog.GetAlgorithm id

    ListByKind =
        fun kindName ->
            match AlgorithmKind.parse kindName with
            | Some kind -> catalog.ListByKind kind
            // An unknown kind is an empty result, not a failure — the
            // contract takes the wire string precisely so a client on a
            // newer/older build degrades to "nothing of that kind"
            // rather than a transport-level deserialisation error.
            | None -> async { return [] }
}