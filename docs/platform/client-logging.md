# Client-side logging

The SDK ships a client-tier default `ILogger` implementation alongside the existing server-tier `ConsoleLogger`, so client diagnostics flow through the same interface shape the server already uses. Downstream client-tier modules consume `ILogger` directly — receive it via init parameters, or reach for the built-in `Logger.forCategory` helper for module-private call sites.

## The `ILogger` interface

`ILogger` lives in `ToolUp.Platform.Core/Shared/Interfaces/ILogger.fs` and is shared by both tiers — same shape on server and client, no separate client surface to learn:

```fsharp
type ILogger =
    abstract Debug: message: string -> unit
    abstract Info:  message: string -> unit
    abstract Warn:  message: string -> unit
    abstract Error: message: string * ex: exn option -> unit
```

The client-side default implementation, `ConsoleLogger`, routes severity-tagged messages to the matching `Fable.Core.JS.console` method so devtools render them with the appropriate icon and stack-trace affordance:

| `ILogger`       | `console.*`     |
|-----------------|------------------|
| `Debug`         | `console.log`    |
| `Info`          | `console.info`   |
| `Warn`          | `console.warn`   |
| `Error`         | `console.error`  |

`Error` passes the optional exception as a second argument to `console.error`; Fable lifts `Message` + `StackTrace` into the JS error shape so devtools render the stack on click.

## Convenience helpers

The `Logger` module (in `ToolUp.Platform.Client/Client/Logger.fs`) wraps `ConsoleLogger` with two ergonomics:

```fsharp
/// Uncategorised — no `[category]` prefix on emitted messages.
Logger.defaultLogger : ILogger

/// Returns a fresh categorised logger.
Logger.forCategory (category: string) : ILogger
```

`forCategory` is the everyday call. Categories follow a **dotted-namespace** convention matching the server-side `ITraceLogger` vocabulary (`ai.sse`, `ai.agent`, `auth`, `client.bootstrap`, …) so a single category space spans both tiers when correlating across logs.

## Module-level `let log` placement convention

Declare one categorised logger per file, at module level, immediately after the `open` declarations:

```fsharp
module YourCompanion.Client.Foo

open Fable.Core
open ToolUp.Platform

let private log = Logger.forCategory "yourcompanion.foo"

// ... rest of the module ...

let doThing () =
    try
        ...
    with ex ->
        log.Error("doThing failed", Some ex)
```

Rules:

- **One `log` per file**, not per function. Call sites read `log.Warn "..."` directly, not `let log = ...` re-declared inline.
- **Immediately after `open`** declarations, before any other module-level binding. Keeps the file's "what category am I writing under?" question answerable from the first ~15 lines.
- **`let private log`** — the value is internal to the compilation unit. Lowercase `log` is the canonical name (consistent across every SDK-internal call site that adopted the convention in the Phase 65 sweep).
- **No `private` qualifier needed in F# files that have no other module-level lets exposed publicly** — F# module bindings default to public, but `private` is fine and explicit.

## Injecting a non-default `ILogger`

The SDK provides no global swap-point. Downstream client-tier modules that need a non-default logger receive `ILogger` through their init parameters — the same dependency-injection shape the server tier already uses. The shell does not own a "Logger" field on `ClientConfig`; consumer code threads `ILogger` explicitly:

```fsharp
// Companion that wants a non-default sink:
type YourCompanionInit = {
    Logger: ILogger
    // ... other deps ...
}

let init (deps: YourCompanionInit) =
    deps.Logger.Info "boot"
    ...
```

The consumer composing the app passes whichever `ILogger` they want — `Logger.forCategory "your.category"` for the built-in default, or a custom implementation for structured sinks, level filtering, etc.

This deliberately avoids module-level `mutable` state. The `defaultLogger` value is plain `let`-bound, immutable; the absence of `Logger.setDefault` is intentional.

## Categories used by the SDK

The Phase 65 sweep established the following file-to-category mapping inside `toolup-forge/src/*.Client/`:

| Category                  | File                                     |
|---------------------------|------------------------------------------|
| `ai.sse`                  | `SSEClient.fs`                           |
| `ai.tool`                 | `ClientToolRuntime.fs`                   |
| `client.module-boundary`  | `Components/ModuleBoundary.fs`           |
| `client.module-state`     | `ModuleStateObserver.fs`                 |
| `client.module-query`     | `ModuleQueryClient.fs`                   |
| `client.navigation`       | `NavigationRequest.fs`                   |
| `client.notification`     | `NotificationClient.fs`                  |
| `client.preferences`      | `SidebarPreferences.fs`                  |
| `client.flags`            | `FeatureFlags.fs`                        |
| `client.i18n`             | `I18n.fs`                                |
| `client.bootstrap`        | `SDK.Client.fs`                          |

Downstream consumers SHOULD pick categories outside the `client.*` / `ai.*` namespaces the SDK reserves — `mycompany.checkout`, `mycompany.payments`, etc. — so logs from consumer code don't masquerade as SDK output when filtering by category prefix.

## Deferred capabilities

Two additive surfaces are deliberately not in scope today; both retain `ILogger` compatibility when they ship:

- **Level filtering**: a `LevelFilteredLogger(inner: ILogger, minLevel: LogLevel)` decorator can drop `Debug` / `Info` below a configured floor. Today both tiers stay uniform — the server tier has no equivalent filter either, and noise control is a deployment concern. Promotes if production noise-control demand surfaces.
- **Structured-payload capability**: an `IStructuredLogger { Log: LogLevel * string * Map<string, obj> -> unit }` sibling interface (mirroring the `ITraceLogger` precedent in `ILogger.fs`) lets opt-in implementations carry typed properties for structured-sink integrations (Datadog Browser Logs, Sentry, OpenTelemetry-Web). Callers reach the structured surface via a helper that no-ops when the underlying logger doesn't support it; existing `ILogger`-only code is unaffected.

Both surfaces are additive — adopting either does not change the convention documented above.

## See also

- [`platform/architecture.md`](architecture.md) for the SDK's tier split + interface-in-Core principle.
- The server-side `ConsoleLogger` at `ToolUp.Platform.Server/Server/Infra/ConsoleLogger.fs` for the matching `ITraceLogger` implementation.
- [`migrations/65-client-side-ilogger.md`](../migrations/65-client-side-ilogger.md) for consumer-side adoption guidance if you currently call `Fable.Core.JS.console.*` directly.
