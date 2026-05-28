// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Extension point for `/dev/inspect`.
///
/// `DevDiagnosticsHandler` resolves all DI-registered
/// `IDevDiagnosticsContributor` instances at request time, awaits each
/// `Contribute()`, and folds the `(panelName, payload)` pairs into a new
/// `Contributors` map on the `DevDiagnosticsReport` so downstream
/// observability surfaces (the JSON endpoint, the HTML panel view, future
/// support-bundle exports) can treat them uniformly.
///
/// Companions register a contributor via the same DI singleton pattern as
/// `IHealthCheck` / `IConfigValidator`:
///
/// ```fsharp
/// services.AddSingleton<IDevDiagnosticsContributor>(fun sp -> upcast MyContributor(...))
/// ```
///
/// ## Caller scope
///
/// `Contribute()` is invoked once per `/dev/inspect` request from the
/// caller's resolved `AccessContext` thread. Implementations must:
/// - Return data scoped to the *deployment* (not just the caller) — the
///   endpoint is debug-only and Owner/Admin-gated by `EnableDevEndpoints`,
///   so cross-team visibility is acceptable. (Mirrors the
///   `HealthMonitorUI` team-isolation carve-out.)
/// - Be idempotent and side-effect-free. The contributor may run at
///   request rate; do not perform expensive computation or external I/O.
/// - Be quick. Slow contributors block the entire `/dev/inspect` response.
///   Aim for <50ms; bound external calls with explicit timeouts.
///
/// ## Payload shape
///
/// Return `Async<string * obj>` where:
/// - `string` is the panel name shown in the HTML view and used as the
///   key in `Contributors`. Use a short Title-Case label
///   (e.g., `"SSE trace"`, `"Module hooks"`).
/// - `obj` is anything `JsonConvert.SerializeObject` can render. F# records,
///   anonymous records, `Map`, `list`, primitives all serialise cleanly.
///
/// Multiple contributors registering the same panel name overwrite each
/// other (last wins) — pick distinctive names.
type IDevDiagnosticsContributor =
    abstract Contribute: unit -> Async<string * obj>