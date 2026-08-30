# ToolUp Platform

A modular F# full-stack SDK for building production multi-tenant analytical applications.

ToolUp Platform ships as a set of independently-versioned NuGet packages — pick the ones you need, compose them with your own domain modules, and deploy. Built on Giraffe + ASP.NET Core (server), Fable + Feliz with an in-tree Elmish runtime (client), in-tree ToolUp.Remoting transport (type-safe wire).

> The Elmish runtime (forked from [Fable.Elmish](https://github.com/elmish/elmish) v5.x by Eugene Tolmachev + community, Apache 2.0) and the ToolUp.Remoting transport (forked from [Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) by Zaid Ajaj, MIT) ship in-tree under `ToolUp.Platform.{Core,Client,Server}`. The namespaces are `ToolUp.Elmish` / `ToolUp.Remoting.*` (renamed from the upstream `Elmish` / `Fable.Remoting.*` to make the divergence visible — consumer call sites move via simple search-and-replace per the [namespace-rename migration doc](../migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md)). See the [forge README](../../README.md#in-tree-client--transport-forks) for the full list of fork additions (typed dispatch handle, lifetime-aware effects, structured error context, prefetch gating, integrated body normalisation, bundled JSON converter) and the appreciative upstream credit posture.

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

// Mark this `main` with [<EntryPoint>] in your own Server.fs —
// `ServerApp.run` returns the `int` exit code the runtime wants.
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

**Evaluating the platform for procurement?** [`../security/PLATFORM-SECURITY-RULES.md`](../security/PLATFORM-SECURITY-RULES.md) is the version-stamped statement of what the SDK enforces — tenant isolation, authentication, authorisation, encryption, audit, data-subject rights, portability, AI safety — with an evidence pointer into this source tree for every rule, and an explicit list of what is deliberately out of scope.

> AI-driven UI control (set fields, click buttons, navigate, select rows) is **not** shipped as a forge OSS companion. forge keeps the `IClientToolAuthorizer` seam in `ToolUp.AI.Core` for any consumer wanting to gate AI tool dispatch; consumers register their own authorizer (default-deny allowlist) or accept the unconfigured "allow" behaviour at their own risk.

## Architecture

See [`architecture.md`](architecture.md) for the full architecture overview — composition roots, `ServerApp` / `AIServerApp` / `RAGServerApp` pipelines, scope resolution, how modules plug in.

## Module convention

See [`modules.md`](modules.md) for the 4-file module pattern.

## Surfaces — the auth, scope, and persistence model

See [`surfaces.md`](surfaces.md) for the Subject / `SurfaceProfile` / `SurfaceRequirement` model — how a deployment declares which subject shapes it supports (anonymous sessions, authenticated users, team members, share-token bearers), how per-route requirements gate access, and how single-shape and mixed-shape deployments share the same shape.

## Other reference

- [`command-palette.md`](command-palette.md) — the opt-in Ctrl+K / Cmd+K quick-nav overlay: enabling it, the keybinding contract, how its entries are derived from the same visibility fold as the sidebar, and the `data-toolup-palette-*` theming hooks.
- [`portability-rules.md`](portability-rules.md) — six rules every distributed-implementation-friendly interface satisfies.
- [`auth.md`](auth.md) — auth providers and how to write one.
- [`auth-ui-vendor-neutrality.md`](auth-ui-vendor-neutrality.md) — the vendor-neutral `ProviderAuthUI` config case, why its payload is `obj`, and the companion smart-constructor convention (`ClerkAuthUI` is deprecated).
- [`storage.md`](storage.md) — `IBlobStorage` companions and the encryption-at-rest decorator.
- [`edge-serving.md`](edge-serving.md) — origin vs CDN topology: the purge-only `IEdgeCache` seam and its fire-and-forget fan-out on publish / delete, per-response-class `Cache-Control` declaration on the media routes (and the two postures the preflight refuses because they leak), the HLS key route's unconfigurable `no-store`, and delegated URL signing for a viewer the origin never sees.
- [`events.md`](events.md) — event store, audit log, audit-sink replication.
- [`jobs.md`](jobs.md) — cron + event-triggered + manual background jobs.
- [`external-compute.md`](external-compute.md) — `IExternalComputeDispatcher`: brokering a unit of work to compute outside this process (GPU box, batch service, worker pool), the opaque submit/poll/cancel handle model, the `NoExternalCompute` default, and how it composes with `IJobScheduler`.
- [`budgets.md`](budgets.md) — the one shape every resource ceiling in the SDK takes: declare (subject / period / claims) → check (allowed / near-limit / refused) → account → store, the single `Spent + Requested > Ceiling` predicate a concurrency cap and a token cap both reduce to, the period-as-storage-key rule that makes a reset free, the `IBudgetLedger` seam that keeps a check and its reservation indivisible, where each budget in the SDK actually lives (and which two deliberately stay in band), and how a refusal reaches the caller, the audit log, the log and metering.
- [`model-fit-worker-contract.md`](model-fit-worker-contract.md) — the `modelfit/v1` work-spec convention: everything needed to write a Python / R fit worker with **no SDK from this repository** — the versioned envelope schema, reading the dataset vintage by reference (and why the `format` tag must be read before the bytes), progress by poll, the artifact descriptor a worker returns, the authenticated completion callback and its idempotent re-delivery, and a one-page minimal worker.
- [`data-subject-requests.md`](data-subject-requests.md) — GDPR Article 15/17 export + erasure, the erasure-policy choice tree, per-store behaviour.
- [`appliance-deployment.md`](appliance-deployment.md) — the single-tenant in-situ appliance posture: offline-tolerant boot (external-probe validators downgraded, security / structural guards never), signed-artefact upgrade verification with the verify → migrate-preview → flip runbook, the consent-gated operational-telemetry diode whose closed schema structurally cannot carry content, the data-class-aware redacted support bundle, and a plain statement of what the supplying party can and cannot see.
- [`offline-entitlement.md`](offline-entitlement.md) — signed capability tokens verified offline against a pinned key: the claim model and its canonical signed byte form, capabilities projected onto feature flags as a ceiling no scope override can lift, capacity budgets in the `QuotaBreached` vocabulary, the active → grace → lapsed lifecycle, revocation by bounded token lifetime rather than a CRL fetch, and the structural guarantee that no entitlement state can withhold a customer's own data or stop the deployment booting.
- [`client-remoting-proxies.md`](client-remoting-proxies.md) — module-level proxy convention + send-time request-guard contract for `*.Client` companions.
- [`dom-props.md`](dom-props.md) — typed `svgProp.*` / `dataProp.*` / `ariaProp.*` helpers for hand-rolled DOM attributes; React requires camelCase for SVG and silently drops kebab-case forms, and the `data-*` / `aria-*` / `role` families share the same `prop.custom` code path with a hardcoded exception today.
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
