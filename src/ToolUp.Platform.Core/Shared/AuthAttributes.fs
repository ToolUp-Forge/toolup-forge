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

/// Input field's string value must be a syntactically valid email.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type EmailAttribute() =
    inherit Attribute()

/// Input field's string value must parse as an absolute URI.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type UriAttribute() =
    inherit Attribute()

/// Input field's numeric value must be in the inclusive range
/// `[min, max]` (accepts int / int64 / float / decimal).
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type RangeAttribute(min: float, max: float) =
    inherit Attribute()
    member _.Min = min
    member _.Max = max

/// Input field's numeric value must be >= `n`.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type MinValueAttribute(n: float) =
    inherit Attribute()
    member _.MinValue = n

/// Input field's numeric value must be <= `n`.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type MaxValueAttribute(n: float) =
    inherit Attribute()
    member _.MaxValue = n

// 0.5.0 (Phase 69h.tail) — forge-native audit attributes mirroring
// ToolUp.Remoting.Server's Phase 69h AuditAttribute / PiiSafeAttribute.
// Same rationale as the auth markers above: forge API records sit in
// Platform.Core (compiled by the Fable client too) and cannot take a
// server-tier dependency. The dispatcher's audit classifier recognises
// both families by simple type name + a reflective `KindName` read.

/// Opts a method into dispatcher-emitted audit. After every successful
/// invocation the dispatcher emits an audit event with the configured
/// kind, the caller's subject id, the request correlation id, and a
/// PII-redacted snapshot of the input record.
///
/// `kindName` is the string-literal name of a well-known kind
/// (`"MoneyMoved"`, `"PolicyChanged"`, `"PiiAccessed"`, `"DataExported"`,
/// `"PermissionGranted"`, `"PermissionRevoked"`, `"TenantCreated"`,
/// `"TenantDeleted"`) or `"Custom:<name>"` for an open-vocabulary kind.
/// F# attributes can't take DU values directly so the string encoding is
/// the workaround — mirrored verbatim from the server-tier attribute.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type AuditAttribute(kindName: string) =
    inherit Attribute()
    member _.KindName = kindName

/// Opts a record field into PII-safe audit payload inclusion. Fields
/// WITHOUT this attribute are redacted to `<redacted:TypeName>` in the
/// emitted audit row's payload. Fail-safe: forgetting the attribute
/// keeps PII out of audit rows.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field)>]
type PiiSafeAttribute() =
    inherit Attribute()

// 0.5.0 (Phase 69g.tail) — forge-native rate-limit attribute mirroring
// ToolUp.Remoting.Server's Phase 69g RateLimitAttribute. Same rationale
// as the auth / audit markers above: forge API records sit in
// Platform.Core (compiled by the Fable client too) and cannot take a
// server-tier dependency. The dispatcher's rate-limit classifier
// recognises both families by simple type name + a reflective
// Count / WindowSeconds read, normalising to the server-tier budget.

/// Caps a method at `count` calls per `windowSeconds`-second sliding
/// window, per subject (the resolved auth-context `SubjectId`, or the
/// per-IP fallback for anonymous callers). Multi-attribute is AND —
/// every budget must pass (short-burst + sustained caps compose). Use
/// the `RateLimitSeconds` constants for the window, e.g.
/// `[<RateLimit(30, RateLimitSeconds.perMinute)>]`.
///
/// Dormant until an `IRateLimitStore` is composed (GP 13 — zero cost
/// when no store is wired). On denial the dispatcher returns 429 +
/// `Retry-After`, emits `MethodOutcome.RateLimited` telemetry, and
/// emits a `RateLimitExceeded` audit row.
[<AttributeUsage(AttributeTargets.Property ||| AttributeTargets.Field, AllowMultiple = true)>]
type RateLimitAttribute(count: int, windowSeconds: int) =
    inherit Attribute()
    member _.Count = count
    member _.WindowSeconds = windowSeconds

/// Named window-size constants (in seconds) for
/// `[<RateLimit(count, window)>]`. Named `RateLimitSeconds` rather than
/// `RateLimitWindow` because the latter is already a DU type in this
/// namespace (the Phase 56 inbound-limiter window); these are plain
/// integer-seconds literals the attribute constructor takes.
module RateLimitSeconds =
    [<Literal>]
    let perSecond = 1

    [<Literal>]
    let perMinute = 60

    [<Literal>]
    let perHour = 3600

    [<Literal>]
    let perDay = 86400