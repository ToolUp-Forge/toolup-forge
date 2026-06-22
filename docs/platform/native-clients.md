# Non-web clients over the typed contract (Phase 244)

The SDK ships a **Fable + Feliz** web client, but the type-safe API surface a server exposes is
**framework-neutral**: nothing about consuming it is web-only. This page documents the
contract a non-web client (native desktop / mobile — MAUI, Avalonia, a console tool) consumes,
and how it talks to the server.

> **Status.** This is a *reference contract*, not a shipped native client. A worked
> MAUI/Avalonia sample is demand-gated (no current customer driver) and tracked as a follow-on
> to this phase. The point here is that the "any client over the typed contract" claim is
> **structural**, not aspirational — verified below by where the web-only code actually lives.

## Why the contract is framework-neutral (audit)

A ToolUp.Remoting API contract is an ordinary **record of functions** declared in a shared
`<Compile>` file in the `*.Core`/shared tier (GP 10) — e.g. `type MyApi = { DoThing: string -> Async<string> }`.
That declaration has **no Fable, browser, or ASP.NET Core dependency**:

- The **contract types** (the API record, its DTOs) live in `ToolUp.Platform.Core` / your
  module's shared file. They are F# records, unions, primitives — compiled for both tiers.
- All **browser-only** machinery (`fetch`, `window`, `Fable.SimpleHttp`, `Fable.SimpleJson`)
  lives in `ToolUp.Platform.Client`'s `Client/Remoting/` — the *Fable* proxy builder
  (`Remoting.buildProxy<'TApi>`), **not** the contract.
- The **wire format** is plain HTTP + JSON: a `POST` to `/{ApiTypeName}/{MethodName}` whose
  body and response are serialised with the System.Text.Json converter set
  (`ToolUp.Remoting.Json.SystemTextJson.FableConverters`). Both ends speak the same converters,
  so any .NET caller can produce/consume the wire without Fable.

So a native .NET client references the **same shared contract assembly** the server does, and
needs only an HTTP proxy that maps each contract method to the route + wire above.

## Building a native .NET proxy

A minimal native client:

1. Reference the shared contract project (the one holding your `type MyApi = { … }`).
2. Construct a `System.Text.Json.JsonSerializerOptions` via
   `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()` (the same converter set the
   server uses — Option / DU / record / tuple / Map / decimal / DateTime, camelCase-insensitive).
3. For each call, `POST {baseUrl}/{ApiTypeName}/{MethodName}` with the argument(s) serialised by
   those options; deserialise the response the same way.
4. Supply identity per request from your own token source (see the seam below).

A typed `.NET` proxy *builder* (mirroring the Fable `Remoting.buildProxy`) is the natural next
addition — until it ships, the hand-rolled per-method proxy above is the supported path and is
small (one `POST` helper + the FableConverters options).

## The one seam to keep neutral — identity per request

The web client attaches `Authorization` / `X-User-Id` / `X-CSRF-Token` / `x-correlation-id` at
**send time** from live caches (the SDK request guard), *not* at proxy-build time, so they never
go stale on sign-in / token refresh. A native client must do the same: read the current token
from its own store on **each** call and set the headers per request. Do not bake a token into a
long-lived proxy at construction — that is the "build-once / read-per-call" trap the SDK's guard
exists to avoid. This is the only client-shape-specific concern; everything else is the shared
contract + wire above.

## Out of scope

- A shipped MAUI / Avalonia reference app (demand-gated follow-on).
- Offline / request queueing for a native client — a separate offline-first / PWA concern.
