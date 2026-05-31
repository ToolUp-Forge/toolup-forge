module ToolUp.Remoting.Harness.Shared

open System

/// Representative API record for v0 of the harness. Exercises three
/// method shapes that load-bear most of the Phase 69 / 69a / 69b
/// validation: a basic argument round-trip, a unit method (the body
/// normalisation path), and an exception case (the error envelope path).
type IHarnessApi = {
    Echo: string -> Async<string>
    Heartbeat: unit -> Async<DateTimeOffset>
    Boom: string -> Async<int>
}

/// Phase 69b.B coverage — exercises `Remoting.fromContextAsync`. The
/// `Subject` is resolved per-request from the `X-Subject` header by an
/// async resolver, and surfaced via `WhoAmI`. Demonstrates the resolver
/// runs per request (different `X-Subject` values from different
/// requests produce different `WhoAmI` responses).
type IContextApi = {
    WhoAmI: unit -> Async<string>
}

/// Route shape. Mirrors the forge default
/// (`toolup-forge/src/ToolUp.Platform.Server/Server/Api.fs` line 33)
/// so the harness exercises the same path layout downstream consumers see.
let routeBuilder (typeName: string) (methodName: string) =
    sprintf "/api/%s/%s" typeName methodName