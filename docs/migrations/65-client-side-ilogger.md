# Phase 65 — Client-side `ILogger` (consumer migration)

**What changes.** SDK ships a client-tier default `ILogger` implementation (`ConsoleLogger`) inside `ToolUp.Platform.Client` plus a `Logger` module exposing `defaultLogger` + `forCategory` helpers. The shared `ILogger` interface (in `ToolUp.Platform.Core/Shared/Interfaces/ILogger.fs`) is unchanged. The SDK's own client-tier call sites that previously routed through `Fable.Core.JS.console.{log,info,warn,error}` now go through the new seam.

**Scope.** Additive opt-in convention — no forced migration. Downstream client-tier code calling `Fable.Core.JS.console.*` directly continues to work byte-for-byte. Consumers who choose to adopt the convention for consistency with their server-side `ILogger` usage do so call-site by call-site.

## Diff to apply (consumer-side)

For client-tier code that currently writes raw `console.*` calls, the substitution is mechanical:

```fsharp
// Before
module YourCompanion.Client.Foo

open Fable.Core
open ToolUp.Platform

let doThing () =
    try
        ...
    with ex ->
        Fable.Core.JS.console.error ($"Foo failed: {ex.Message}")
```

```fsharp
// After
module YourCompanion.Client.Foo

open Fable.Core
open ToolUp.Platform

let private log = Logger.forCategory "yourcompanion.foo"

let doThing () =
    try
        ...
    with ex ->
        log.Error("Foo failed", Some ex)
```

Key changes:

1. **Module-level `let private log = Logger.forCategory "<category>"`** immediately after the `open` declarations.
2. **Severity mapping** — call sites that wrote `console.warn` write `log.Warn`; `console.error` becomes `log.Error(message, Some ex)` when an exception is in hand, or `log.Error(message, None)` otherwise. `console.log` maps to `log.Debug` for verbose-but-routine output and `log.Info` for "this should always appear" boot/state lines.
3. **Drop inline category prefixes** — if your existing messages start with `[YourCompanion]`, drop the prefix; the `[<category>] ` prefix `ConsoleLogger` emits replaces it.
4. **Exception objects flow through the second `Error` argument** rather than being formatted into the message string. Devtools render `Message` + `StackTrace` on click when the exception arrives that way.

## When to adopt

- **Build a new client-tier companion** — adopt from the first commit. Cost is one `let log` line + tier-aligned call shape.
- **Existing companion with > ~10 console.* sites** — adopt incrementally during natural touch-ups of each file. The Phase 65 sweep showed the per-file cost is one `let log` line + one substitution per site; commit each file as its own atomic change.
- **Existing companion with <= ~3 console.* sites** — defer. The asymmetry is small and the substitution can ride along with the next functional change to those files.
- **Test renders, isolated component playgrounds** — never required. `console.*` works fine; the categories the `ConsoleLogger` produces aren't load-bearing for tests.

## Worked example — `SSEClient.fs`

The Phase 65 sweep migrated the SDK's own `ToolUp.AI.Client/Client/SSEClient.fs` (3 sites, category `ai.sse`):

```fsharp
// Before
module ToolUp.AI.Client.SSEClient
open Fable.Core
open ToolUp.AI
...
| ParseFailure preview ->
    Fable.Core.JS.console.warn ("SSE parse failure", preview)
    dispatch (StreamError $"Malformed SSE event: {preview}")
```

```fsharp
// After
module ToolUp.AI.Client.SSEClient
open Fable.Core
open ToolUp.Platform
open ToolUp.AI

let private log = Logger.forCategory "ai.sse"
...
| ParseFailure preview ->
    log.Warn $"SSE parse failure: {preview}"
    dispatch (StreamError $"Malformed SSE event: {preview}")
```

Devtools then renders `[ai.sse] SSE parse failure: <preview text>` — same severity icon (warn), same message content, plus the category tag that lets you filter the SSE subsystem out of the noise when something else is breaking.

## Verification

1. `dotnet build` clean.
2. Open devtools; trigger a Warn path (e.g. force an SSE reconnect by toggling network). The console row reads `[<category>] <message>` with the warn icon.
3. Trigger an Error path that carries an exception. The console row reads `[<category>] <message>` with the error icon; clicking expands to show `Message` + `StackTrace`.
4. `rg -n "Fable\.Core\.JS\.console\.(log|info|warn|error)|JS\.console\.(log|info|warn|error)" <your-client-tier-source>/` returns zero matches outside the file housing the `ConsoleLogger` implementation (or, for consumer code, anywhere the substitution has landed).

## Per-file site count + category assignment (Phase 65 sweep, SDK-internal)

The following inventory reflects the actual sweep that landed against `toolup-forge`. Total: 15 call sites across 11 files.

| File                                                                     | Sites | Category                  |
|--------------------------------------------------------------------------|-------|---------------------------|
| `ToolUp.AI.Client/Client/SSEClient.fs`                                   | 3     | `ai.sse`                  |
| `ToolUp.AI.Client/Client/ClientToolRuntime.fs`                           | 1     | `ai.tool`                 |
| `ToolUp.Platform.Client/Components/ModuleBoundary.fs`                    | 1     | `client.module-boundary`  |
| `ToolUp.Platform.Client/Client/ModuleStateObserver.fs`                   | 1     | `client.module-state`     |
| `ToolUp.Platform.Client/Client/ModuleQueryClient.fs`                     | 1     | `client.module-query`     |
| `ToolUp.Platform.Client/Client/NavigationRequest.fs`                     | 1     | `client.navigation`       |
| `ToolUp.Platform.Client/Client/NotificationClient.fs`                    | 2     | `client.notification`     |
| `ToolUp.Platform.Client/Client/SidebarPreferences.fs`                    | 1     | `client.preferences`      |
| `ToolUp.Platform.Client/Client/FeatureFlags.fs`                          | 1     | `client.flags`            |
| `ToolUp.Platform.Client/Client/I18n.fs`                                  | 1     | `client.i18n`             |
| `ToolUp.Platform.Client/Client/SDK.Client.fs`                            | 4     | `client.bootstrap`        |

### `emitJsStatement` sweep decision

`FeatureFlags.fs` and `I18n.fs` previously wrote their warnings via `emitJsStatement msg "console.warn($0)"` — a Fable-codegen escape hatch that emits the JS literally. The Phase 65 sweep migrated both to `log.Warn` after confirming the emitJsStatement form was not load-bearing in either file: both sites pre-dated the client-tier `ILogger` seam and were equivalent to a normal `console.warn` call. The CSRF client's embedded JS template at `CsrfClient.fs:139` (a `console.warn` literal inside an `emitJsExpr`/`emitJsStatement` JS string) is a different pattern — the JS string runs at the patched-`window.fetch` boundary and is intentionally JS-side; it's not an F# logging site and stays as-is.

## Rollback

Revert the `let log = Logger.forCategory "..."` declaration plus the call-site substitutions in the affected file. The `ConsoleLogger` type and `Logger` module stay shipped (they're additive substrate); only the call-site adoption rolls back. Behaviour returns byte-for-byte to the pre-adoption state because the substituted calls produce equivalent JS output modulo the `[<category>] ` prefix.

## Consumers

This is an additive opt-in convention, not a forced migration — every consumer is **N-A** by default. Downstream apps adopt only when they choose to, for consistency with their own server-side `ILogger` usage. The SDK-internal client-tier files (the 11 listed above) shipped with the change.

## See also

- [`platform/client-logging.md`](../platform/client-logging.md) — the longer-form companion docs covering category conventions, injection patterns, and deferred capabilities.
- The shared `ILogger` interface at [`src/ToolUp.Platform.Core/Shared/Interfaces/ILogger.fs`](../../src/ToolUp.Platform.Core/Shared/Interfaces/ILogger.fs).
- The client-tier implementation at [`src/ToolUp.Platform.Client/Client/Logger.fs`](../../src/ToolUp.Platform.Client/Client/Logger.fs).
