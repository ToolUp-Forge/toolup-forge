// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Auth

/// Opaque server-request context handed to `IAuthProvider` impls.
/// Phase 11.C.5 Tier 3 — replaces the pre-11.C.5 `obj` parameter on
/// `GetUser` / `ValidateRequest`, which forced every impl to repeat
/// `ctx :?> HttpContext` and gave external impls no help with the
/// cast. The underlying value on the server tier is a
/// `Microsoft.AspNetCore.Http.HttpContext`; Core declares the wrapper
/// so the interface can live in the shared (Fable-compatible) layer
/// without referencing `Microsoft.AspNetCore.*`. Construct via
/// `ToolUp.Platform.RequestContextBuilder.ofHttpContext` (server
/// tier).
type RequestContext = RequestContext of obj

module RequestContext =
    /// Unwrap the boxed value. Server-tier impls cast to `HttpContext`
    /// once on entry to `GetUser` / `ValidateRequest`; the wrapper
    /// exists to keep the cast localised to one site per impl rather
    /// than scattered through Core. Callers MUST treat the inner
    /// value as opaque — the wrapper's `obj` payload is reserved for
    /// the matching tier's `ofX` constructor (e.g.
    /// `Server.Auth.RequestContext.ofHttpContext` boxes an
    /// `HttpContext`); other server tiers may box other shapes.
    let inline value (RequestContext o) = o

    /// Package-level wrap. Used by server-tier constructors to box
    /// the matching transport value (e.g. `HttpContext`) into a
    /// `RequestContext`. The DU constructor is not marked `private`
    /// because the wrapper type and its sole producer site live in
    /// different assemblies (Core declares the type; the
    /// `ToolUp.Platform.Server` assembly's `RequestContextBuilder`
    /// provides the only canonical construction path), and F# DU
    /// `private` cannot cross assembly boundaries without
    /// `InternalsVisibleTo` plumbing the SDK avoids. Discipline
    /// remains: tests / external impls SHOULD construct via
    /// `RequestContextBuilder.ofHttpContext`, not by directly
    /// invoking the case constructor.
    let inline pkg (boxedValue: obj) : RequestContext = RequestContext boxedValue

/// Represents an authenticated user — provider-agnostic.
/// No provider-specific fields (org_id, org_role, JWT claims) leak into this type.
type AuthenticatedUser = {
    UserId: string
    DisplayName: string
    Email: string option
    TenantId: string option
    Roles: string list
}

module AuthenticatedUser =
    /// Anonymous/unauthenticated user
    let anonymous = {
        UserId = "anonymous"
        DisplayName = "Anonymous"
        Email = None
        TenantId = None
        Roles = []
    }

    let isAnonymous (user: AuthenticatedUser) = user.UserId = "anonymous"

/// Server-side authentication provider interface.
/// Implementations extract and validate user identity from HTTP requests.
/// The SDK owns team management, roles, and permissions — auth providers
/// supply identity only ("prove this user is who they say they are").
type IAuthProvider =
    /// Extract the authenticated user from the current request context.
    /// Returns `anonymous` if no credentials are present. Lenient contract —
    /// use `ValidateRequest` when strict validation is required.
    ///
    /// `ctx` is a `RequestContext` newtype wrapping the underlying
    /// server-tier value (`HttpContext` on ASP.NET Core). Server-tier
    /// impls unwrap with `RequestContext.value` then cast at the
    /// entry point — one cast site per impl, not scattered. The
    /// wrapper keeps the cast localised; external impls inherit the
    /// pattern by reading the contract from this signature alone.
    abstract GetUser: ctx: RequestContext -> Async<AuthenticatedUser>

    /// Validate the request and return the authenticated user or a reason
    /// string on failure. Strict contract: returns `Error` on missing /
    /// invalid / expired credentials. Used by authenticated scope resolvers
    /// to enforce login requirements.
    ///
    /// See `GetUser` for the `RequestContext` contract.
    abstract ValidateRequest: ctx: RequestContext -> Async<Result<AuthenticatedUser, string>>