// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.RequestContextBuilder

open Microsoft.AspNetCore.Http
open ToolUp.Platform.Auth

// Phase 11.C.5 Tier 3 — server-tier `ofHttpContext` constructor for
// the Core-tier `RequestContext` newtype. Named `RequestContextBuilder`
// rather than `…Server.Auth.RequestContext` because `SDK.Server.fs`
// declares `module ToolUp.Platform.Server`, and re-using that prefix
// as a namespace path would clash (F# requires every dotted name to
// be unambiguously a namespace OR a module within an assembly).

/// Wrap a live `HttpContext` for hand-off to an `IAuthProvider`. The
/// wrapper is transparent — this function is essentially `box`, but
/// routing it through one named site means every caller knows where
/// the boxing happens and why the `HttpContext`-back-cast in each
/// `IAuthProvider` impl is sound. The type signature also forbids
/// callers from boxing a non-`HttpContext` value through this path.
let ofHttpContext (ctx: HttpContext) : RequestContext = RequestContext.pkg (ctx :> obj)