// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.AdPanel

open Fable.SimpleHttp
open Fable.SimpleJson
open ToolUp.Platform

// ─── Reference IAdAnalyticsSink — server-backed ───────────────────
//
// POSTs `AdImpression` / `AdClick` to the optional server endpoint
// at `/api/_platform/ads/impression` / `/api/_platform/ads/click`.
// Server-side handler (mounted only when `ServerConfig.AdAnalytics
// = EnabledAdAnalytics`) records via `IAuditLog` under `_platform`
// scope. Sink swallows network errors — best-effort posture per
// `IAdAnalyticsSink` contract.

type ServerSinkAdAnalytics() =
    let post (path: string) (body: obj) = async {
        try
            let json = Json.serialize body

            let! _ =
                Http.request path
                |> Http.method POST
                |> Http.content (BodyContent.Text json)
                |> Http.header (Headers.contentType "application/json")
                |> Http.send

            return ()
        with _ ->
            return ()
    }

    interface IAdAnalyticsSink with
        member _.LogImpression(event: AdImpression) =
            post "/api/_platform/ads/impression" event

        member _.LogClick(event: AdClick) = post "/api/_platform/ads/click" event