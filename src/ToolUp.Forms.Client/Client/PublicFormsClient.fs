// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.PublicFormsClient

open ToolUp.Remoting.Client
open ToolUp.Forms.PublicFormApi

// ─── Phase 21b — Public-form Fable.Remoting client proxy ────────────
//
// Deliberately does NOT use `UserSession.withRequestHeaders` — the
// public surface authenticates via the share token in the request
// body, not via Bearer / X-User-Id headers. The respondent's browser
// might not have any auth state at all (incognito tab, fresh
// install) and any leftover platform headers are irrelevant to the
// token-gated handler.
//
// The route is `/api/public/forms/<MethodName>` (matches
// `PublicFormApi.routeBuilder`); `SurfaceEnforcementMiddleware`
// admits only `ClaimBearerKind` subjects on these routes via the
// per-route `SurfaceRequirement.claimBearerOnly` declarations
// registered by `FormsCompose` (Phase 66 Stream B.6).

let proxy: IPublicFormApi =
    Remoting.createApi ()
    |> Remoting.withRouteBuilder PublicFormApi.routeBuilder
    |> Remoting.buildProxy<IPublicFormApi>