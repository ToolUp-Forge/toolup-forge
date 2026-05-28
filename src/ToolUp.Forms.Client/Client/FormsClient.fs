// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Forms.FormsClient

open ToolUp.Platform
open ToolUp.Forms.FormApi

// ─── Phase 21 — Fable.Remoting client proxy ─────────────────────────
//
// Thin wrapper over `Api.makeProxy<IFormApi>` so callers don't have
// to import `UserSession.withRequestHeaders` themselves. The proxy
// shape mirrors `IFormApi` exactly; every method goes over HTTP via
// `Fable.Remoting.Client`.
//
// Header injection: `UserSession.withRequestHeaders` adds the auth
// token (Bearer) or `X-User-Id` header per the platform's
// `UserSession.configure` SubjectKind setup. Same convention as the
// AI / KnowledgeBase clients.

/// The proxy. Lazy-initialised at first access; the underlying
/// `RemoteBuilderOptions` are platform-default.
let proxy: IFormApi =
    Api.makeProxy<IFormApi> (customOptions = UserSession.withRequestHeaders)