module ToolUp.Remoting.Harness.Shared

open System
open ToolUp.Remoting.Server

/// Representative API record for v0 of the harness. Exercises three
/// method shapes that load-bear most of the Phase 69 / 69a / 69b
/// validation: a basic argument round-trip, a unit method (the body
/// normalisation path), and an exception case (the error envelope path).
type IHarnessApi = {
    Echo: string -> Async<string>
    Heartbeat: unit -> Async<DateTimeOffset>
    Boom: string -> Async<int>
    BoomCategorised: string -> Async<int>
}

/// Phase 69b.B coverage — exercises `Remoting.fromContextAsync`. The
/// `Subject` is resolved per-request from the `X-Subject` header by an
/// async resolver, and surfaced via `WhoAmI`. Demonstrates the resolver
/// runs per request (different `X-Subject` values from different
/// requests produce different `WhoAmI` responses).
///
/// Phase 69b.D coverage — `WhereAreWe` returns the ambient correlation
/// id without the handler threading it manually. Proves
/// `CallContext.correlationId()` flows through Async transparently.
type IContextApi = {
    WhoAmI: unit -> Async<string>
    WhereAreWe: unit -> Async<string>
}

/// Phase 69d coverage — three classifications exercise the dispatcher's
/// auth pre-flight. The harness wires a resolver that reads `X-Roles`
/// from request headers; tests verify each classification's behaviour.
type ISecureApi = {
    [<RequiresRole("Admin")>]
    AdminOnly: unit -> Async<string>

    [<AllowAnonymous>]
    OpenToAll: unit -> Async<string>

    [<PublicEndpoint>]
    PublicOnly: unit -> Async<string>
}

/// Phase 69g coverage — three rate-limit shapes:
/// - `Fast` carries a tight 3-per-2-seconds budget (single attribute).
/// - `Burst` carries TWO budgets — 2/sec AND 4/min — exercising AND
///   semantics across multiple attributes on one method.
/// - `Unlimited` carries no budget at all — proves the per-call cost
///   stays zero when the dispatcher's rate-limit pre-flight skips.
/// Phase 69h coverage — audit-emission demonstration.
type IAuditedApi = {
    [<AllowAnonymous>]
    [<Audit("PolicyChanged")>]
    UpdatePolicy: unit -> Async<string>

    [<AllowAnonymous>]
    [<Audit("Custom:HarnessExport")>]
    CustomExport: unit -> Async<string>

    [<AllowAnonymous>]
    NoAudit: unit -> Async<string>
}

type IRateLimitedApi = {
    [<AllowAnonymous>]
    [<RateLimit(3, RateLimitWindow.perMinute)>]
    Fast: unit -> Async<string>

    [<AllowAnonymous>]
    [<RateLimit(2, RateLimitWindow.perSecond)>]
    [<RateLimit(4, RateLimitWindow.perMinute)>]
    Burst: unit -> Async<string>

    [<AllowAnonymous>]
    Unlimited: unit -> Async<string>
}

/// Route shape. Mirrors the forge default
/// (`toolup-forge/src/ToolUp.Platform.Server/Server/Api.fs` line 33)
/// so the harness exercises the same path layout downstream consumers see.
let routeBuilder (typeName: string) (methodName: string) =
    sprintf "/api/%s/%s" typeName methodName