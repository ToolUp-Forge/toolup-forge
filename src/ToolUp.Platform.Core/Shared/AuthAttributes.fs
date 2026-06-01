// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// 0.4.1 — Forge-native authorisation attributes for Fable.Remoting API
// records. Mirrors the shape of `ToolUp.Remoting.Server`'s Phase 69d
// attributes (`RequiresRole`, `RequiresClaim`, `TenantScoped`,
// `AllowAnonymous`, `PublicEndpoint`) but lives in `ToolUp.Platform.Core`
// — the tier-shared assembly every forge API record already references.
//
// Why mirror rather than reuse: the upstream attribute types live in
// `ToolUp.Remoting.Server`, which is a server-tier-only assembly.
// forge's API records sit in `*.Core` shared projects (compiled by both
// the Fable client and the .NET server), and those projects cannot take
// a Server-tier dependency without breaking the Fable build. The forge
// SDK bridges the two: `Api.make` accepts an optional auth-context
// adapter that translates the forge attributes here into a
// `Remoting.withAuthContext` resolver at composition time. Consumers
// annotate their API records using THESE attributes; the SDK does the
// boundary translation.
//
// Wire shape: these attributes are pure metadata — they're stripped
// from the JSON-on-the-wire payload by the Fable.Remoting serialiser.
// Both client and server reflect the same attribute set at compile
// time; the server's classifier enforces them at request time.

/// Caller must hold the named role for the method to dispatch.
/// Multi-attribute on a method is AND — every role must be held.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresRoleAttribute(role: string) =
    inherit Attribute()
    member _.Role = role

/// Caller must hold the named claim. When `Value` is non-null the claim
/// must equal it exactly; otherwise presence of the claim is sufficient.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RequiresClaimAttribute(claim: string) =
    inherit Attribute()
    member _.Claim = claim
    member val Value: string = null with get, set

/// Method requires an authenticated tenant-bound subject — used to gate
/// per-tenant endpoints against anonymous and platform-scoped callers.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type TenantScopedAttribute() =
    inherit Attribute()

/// Method explicitly accepts anonymous callers AS WELL AS authenticated
/// ones. The auth context, if present, is still made available for
/// telemetry / audit but isn't enforced.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AllowAnonymousAttribute() =
    inherit Attribute()

/// Method is a public endpoint — the auth-context resolver is not
/// consulted and the method dispatches regardless of caller identity.
/// Use for share-token-gated public surfaces (forge's `IPublicFormApi`).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PublicEndpointAttribute() =
    inherit Attribute()

// 0.4.1 — Forge-native validation attributes mirroring
// ToolUp.Remoting.Server's Phase 69e ValidationAttribute family. Same
// rationale as the auth markers above: forge API records sit in
// Platform.Core which can't reference the Server-tier upstream types.
// The forge SDK's Api.make composes a `Remoting.withValidation` adapter
// at server-tier build time that walks these attributes and emits the
// equivalent server-side enforcement.

/// Input field's string value must be at least `n` characters.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type MinLengthAttribute(n: int) =
    inherit Attribute()
    member _.MinLength = n

/// Input field's string value must be at most `n` characters.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type MaxLengthAttribute(n: int) =
    inherit Attribute()
    member _.MaxLength = n

/// Input field's string value must be non-empty after trim.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type NotEmptyAttribute() =
    inherit Attribute()

/// Input field's string value must match the regex `pattern`. The
/// pattern is compiled once per attribute instance at construction.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type RegexAttribute(pattern: string) =
    inherit Attribute()
    member _.Pattern = pattern