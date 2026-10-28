// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 1130 — the series read a conversation block part is re-read
/// through, from the fact browse proxy.
///
/// A block part persists the presentation request a model wrote, never its
/// rows, so the AI client re-runs the request when it draws the block. That
/// read is the reader's own: `IFactBrowseApi.QuerySeries` is served under
/// the disclosure gate for the signed-in caller, so every figure the block
/// draws was admitted for that reader at that moment. A client boot that
/// composes the fact store registers it with the AI client:
///
///     AIClientConfig.registerBlockSeriesResolver BlockSeriesResolver.resolve
///
/// The function's shape is the AI client's `BlockSeriesResolver`; neither
/// package references the other, and a deployment without the fact store
/// registers nothing.
module ToolUp.Facts.Client.BlockSeriesResolver

open ToolUp.Platform
open ToolUp.Facts

// Header freshness is the CsrfClient request-guard's job — see
// `UserSession.withRequestHeaders`. The same route and options as the fact
// browse module's own proxy.
let private factBrowseApi: IFactBrowseApi =
    Api.makeProxy<IFactBrowseApi> (
        routeBuilder = FactBrowseApi.routeBuilder,
        customOptions = UserSession.withRequestHeaders
    )

/// The series read over the fact browse proxy: one gated series per query,
/// or the read's refusal verbatim.
let resolve (query: FactSeriesQuery) : Async<Result<FactSeries, string>> = factBrowseApi.QuerySeries query