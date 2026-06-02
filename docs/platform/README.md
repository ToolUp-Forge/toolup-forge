# ToolUp Platform

A modular F# full-stack SDK for building production multi-tenant analytical applications.

ToolUp Platform ships as a set of independently-versioned NuGet packages — pick the ones you need, compose them with your own domain modules, and deploy. Built on Giraffe + ASP.NET Core (server), Fable + Feliz with an in-tree Elmish runtime (client), in-tree ToolUp.Remoting transport (type-safe wire).

> The Elmish runtime (forked from Fable.Elmish v5.x) and the ToolUp.Remoting transport (forked from Fable.Remoting) ship in-tree under `ToolUp.Platform.{Core,Client,Server}`. The `namespace Elmish` and `namespace Fable.Remoting.*` are preserved verbatim, so consumer `open Elmish` / `open Fable.Remoting.*` call sites compile unchanged. See the [forge README](../../README.md#in-tree-client--transport-forks) for the full list of fork additions (typed dispatch handle, lifetime-aware effects, structured error context, prefetch gating, integrated body normalisation, bundled JSON converter).

## What's in the box

The Platform provides the **infrastructure** — routing, authentication, scope resolution, storage, eventing, notifications, health checks, audit trails, jobs, data ingestion, encryption, rate limiting, observability. The companions provide **shared application capabilities** that sit on that infrastructure — AI assistants, retrieval-augmented generation, document knowledge bases, schema-driven forms, scheduling.

You bring the **domain** — your modules. The shell handles wiring; modules declare what they are, what data they need, what they provide, and how they behave.

## Quick start

Add the meta-manifest to your `Directory.Packages.props`:

```xml
<PropertyGroup>
  <ToolUpSdkVersion>0.1.0</ToolUpSdkVersion>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="ToolUp.Sdk" />
</ItemGroup>
```

`ToolUp.Sdk` is a meta-package — adding it propagates `<PackageVersion>` entries for every `ToolUp.*` package keyed off `ToolUpSdkVersion`. Bumping the whole SDK is a one-line edit.

Then in a consuming server project's fsproj:

```xml
<PackageReference Include="ToolUp.Platform.Server" />
```

And the minimum composition root (`Server.fs`):

```fsharp
open ToolUp.Platform

[<EntryPoint>]
let main _ =
    ServerApp.empty
    |> ServerApp.withConfig { ServerConfig.defaults with Port = 5000 }
    |> ServerApp.run
```

For a runnable end-to-end sample (server + client + a "Hello World" module), see [`samples/HelloWorld/`](../../samples/HelloWorld/).

## When this SDK is a good fit

- **F# full-stack** apps where the server (Giraffe) and the client (Fable + Elmish) share types directly via ToolUp.Remoting.
- **Multi-tenant production** deployments needing built-in scope isolation, audit trails, RBAC, and per-tenant data scoping out of the box.
- **AI-augmented apps** where the LLM is a peer of the UI — agent loop, tool calling, SSE streaming, prompt caching, and conversation persistence work without writing the plumbing.
- **Schema-driven internal tools** where form definitions + workflow state machines beat hand-rolled CRUD.

## When this SDK is not a good fit

- **C#-only or non-F# stacks** — the SDK is F# end-to-end (Fable transpiles F# to JavaScript for the client).
- **Single-tenant local-only apps** — the multi-tenant infrastructure carries weight you don't need.
- **Stateless API-only services** — you don't need the Elmish shell or the file management UI.
- **High-throughput data pipelines** — the Platform optimises for human-facing analytical workflows, not bulk-data engineering.

## Package families

The SDK is split into independent packages so you can pull just what you need. Each top-level companion has its own page in this docs site.

| Package | What it is |
|---|---|
| [`ToolUp.Platform`](README.md) (this page) | Core SDK: composition root, scope resolution, default in-process implementations of every interface |
| [`ToolUp.AI`](../ai/README.md) | AI assistant: agent loop, SSE streaming, tool registry, system-prompt composition |
| [`ToolUp.RAG`](../rag/README.md) | Retrieval-augmented generation: chunking, vector store, retrieval pipeline, ingestion |
| [`ToolUp.KnowledgeBase`](../knowledge-base/README.md) | Document KB: upload + multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV), notes, narrative-commit |
| [`ToolUp.Forms`](../forms/README.md) | Schema-driven forms + workflows + publishable surveys |
| [`ToolUp.Scheduling`](../scheduling/README.md) | Booking + recurrence + iCalendar |

Plus a wide set of provider companions for the extension-point interfaces — see [`companions/`](../companions/).

> AI-driven UI control (set fields, click buttons, navigate, select rows) is **not** shipped as a forge OSS companion. forge keeps the `IClientToolAuthorizer` seam in `ToolUp.AI.Core` for any consumer wanting to gate AI tool dispatch; consumers register their own authorizer (default-deny allowlist) or accept the unconfigured "allow" behaviour at their own risk.

## Architecture

See [`architecture.md`](architecture.md) for the full architecture overview — composition roots, `ServerApp` / `AIServerApp` / `RAGServerApp` pipelines, scope resolution, how modules plug in.

## Module convention

See [`modules.md`](modules.md) for the 4-file module pattern.

## Surfaces — the auth, scope, and persistence model

See [`surfaces.md`](surfaces.md) for the Subject / `SurfaceProfile` / `SurfaceRequirement` model — how a deployment declares which subject shapes it supports (anonymous sessions, authenticated users, team members, share-token bearers), how per-route requirements gate access, and how single-shape and mixed-shape deployments share the same shape.

## Other reference

- [`portability-rules.md`](portability-rules.md) — six rules every distributed-implementation-friendly interface satisfies.
- [`auth.md`](auth.md) — auth providers and how to write one.
- [`storage.md`](storage.md) — `IBlobStorage` companions and the encryption-at-rest decorator.
- [`events.md`](events.md) — event store, audit log, audit-sink replication.
- [`jobs.md`](jobs.md) — cron + event-triggered + manual background jobs.
- [`data-subject-requests.md`](data-subject-requests.md) — GDPR Article 15/17 export + erasure, the erasure-policy choice tree, per-store behaviour.
- [`client-remoting-proxies.md`](client-remoting-proxies.md) — module-level proxy convention + send-time request-guard contract for `*.Client` companions.
- [`svg-helpers.md`](svg-helpers.md) — typed `svgProp.*` helpers for hand-rolled SVG; React requires camelCase attribute names and silently drops kebab-case forms.
- [`ads.md`](ads.md) — AdSense embedding substrate (`<AdSlot>` Feliz component, `AdScriptLoader`, `IAdAnalyticsSink`) + consent-gate composition.
- [`premium.md`](premium.md) — operator-granted premium-tier substrate (`IUserClaims`, `PremiumGate`, `usePremium`, `PremiumOnly` flag-source composition).
- [`adsense-approval.md`](adsense-approval.md) — operator-facing AdSense site-approval gotchas (HTTPS, content / policy requirements, test-mode parameter, review delay).

## Versioning

`0.x.y` while the public surface is unstable. Per the SemVer-on-`0.x` policy:
- **Minor bumps** (`0.1.0 → 0.2.0`) may include breaking changes.
- **Patch bumps** (`0.1.0 → 0.1.1`) are non-breaking.
- `1.0.0` is declared once the surface is stable enough to commit to.

Each companion versions independently — `ToolUp.Platform.Core 0.3.0` can pair with `ToolUp.AI 0.5.0`. Compatibility documented per release.

## Contributing

ToolUp Platform is Apache 2.0 licensed. See [CONTRIBUTING.md](../../CONTRIBUTING.md) for the contribution flow, [CODE_OF_CONDUCT.md](../../CODE_OF_CONDUCT.md), and [SECURITY.md](../../SECURITY.md). Every commit MUST carry a DCO `Signed-off-by:` line; CI enforces this.

## License

[Apache License 2.0](../../LICENSE). Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK).
