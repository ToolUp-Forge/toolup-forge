# ToolUp Platform

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

A modular F# full-stack SDK for building production multi-tenant analytical applications. Giraffe over ASP.NET Core (server); Fable + Feliz with an in-tree Elmish runtime (client); in-tree ToolUp.Remoting transport (type-safe wire).

> **Status: pre-release (`0.x.y`).** SemVer-on-`0.x` policy — minor bumps may include breaking changes; `1.0.0` is declared once the surface is stable.

## Why this SDK

- **Multi-tenant by construction.** Scope isolation, RBAC, audit trail, per-tenant data scoping are first-class — not retrofitted.
- **AI-augmented as a peer.** Agent loop, SSE streaming, tool calling, prompt caching, conversation persistence — drop in a provider companion and the LLM is wired.
- **Schema-driven modules.** `FormSchema` + `WorkflowDefinition` + module convention collapses CRUD-heavy intake / approval flows.
- **F# end to end.** Shared types cross the wire via ToolUp.Remoting without DTO duplication.
- **Sector-agnostic.** The SDK ships infrastructure; you bring domain.

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

`ToolUp.Sdk` is a meta-package — adding it propagates `<PackageVersion>` entries for every `ToolUp.*` package at the same version. Bumping the whole SDK is a one-line edit.

Reference what you need in each consuming `.fsproj`:

```xml
<PackageReference Include="ToolUp.Platform.Server" />
<PackageReference Include="ToolUp.Platform.Client" />
```

Minimum composition root (`Server.fs`):

```fsharp
open ToolUp.Platform

[<EntryPoint>]
let main _ =
    ServerApp.empty
    |> ServerApp.withConfig { ServerConfig.defaults with Port = 5000 }
    |> ServerApp.run
```

For a runnable end-to-end sample — module + server + client — see [`samples/HelloWorld/`](samples/HelloWorld/).

### Starter templates

Two `dotnet new` paths sit alongside the bare-`PackageReference` shape above; pick whichever matches the deployment shape you're aiming at:

- **`platformsdk-solution`** — full F# full-stack scaffold with `{AppName}-Server` + `{AppName}-Client`, one starter module, `Build.fs`, `compose.yml`, and CI workflow. Production-multi-tenant shape; the right starter for the typical commercial deployment. See [`templates/platformsdk-solution/`](templates/platformsdk-solution/) (and `platformsdk-application` / `platformsdk-module` / `platformsdk-datamanager` / `platformsdk-docker` for adding to an existing solution).
- **`toolup-safer`** — minimal SAFE-Stack-shaped starter; one chat module, anonymous mode, no auth, no persistence, in-memory only. Useful for SAFE-Stack-familiar developers who want to learn the in-tree Elmish + ToolUp.Remoting primitives via a Tiny Chat demo. **An option**, not the recommended path — for production multi-tenant + auth + persistence, use `platformsdk-solution`. See [`docs/getting-started/safer.md`](docs/getting-started/safer.md).

## Documentation

The full docs site lives in [`docs/`](docs/):

- [`docs/platform/`](docs/platform/) — core SDK (architecture, modules, platform modes, auth, storage, events, jobs, portability rules).
- [`docs/ai/`](docs/ai/) — `ToolUp.AI` companion: agent loop, system-prompt composition, BYOK, capability flags.
- [`docs/rag/`](docs/rag/) — `ToolUp.RAG` companion: vector store, retrieval pipeline, ingestion, prompt builder.
- [`docs/knowledge-base/`](docs/knowledge-base/) — `ToolUp.KnowledgeBase`: document upload, multi-format extraction, notes, narrative-commit.
- [`docs/forms/`](docs/forms/) — `ToolUp.Forms`: schema-driven forms, workflows, publishable surveys.
- [`docs/scheduling/`](docs/scheduling/) — `ToolUp.Scheduling`: booking with concurrency lock, recurrence, iCalendar.
- [`docs/companions/`](docs/companions/) — provider-companion overviews (auth, storage, AI, embedding, notifications).

## In-tree client + transport forks

The two load-bearing client libraries — **ToolUp.Elmish** (MVU runtime, forked from [Fable.Elmish](https://github.com/elmish/elmish) under Apache 2.0) and **ToolUp.Remoting** (type-safe RPC transport, forked from [Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) under MIT) — are vendored in-tree under `ToolUp.Platform.{Core,Client,Server}` rather than pulled as third-party packages. Consumer code uses `open ToolUp.Elmish` / `open ToolUp.Remoting.Server` etc.; no separate `PackageReference` is required for either.

### Standing on shoulders — upstream credit

The full appreciative attribution lives in [`NOTICE.md`](NOTICE.md). Short form:

**ToolUp.Remoting — built on Fable.Remoting (Zaid Ajaj, MIT).** ToolUp.Remoting began as a fork of [Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) by Zaid Ajaj. Years of careful work on type-safe RPC over HTTP for F# — wire-shape conventions, route-info encoding, JSON-converter behaviour, the ergonomics of binding API records on both client and server — are the substrate ToolUp's transport rests on. The Phase 69b–69k seam family added substantial new surface for the ToolUp use case (per-request `CallContext`, structured error envelopes, `RateLimit`, `Idempotency`, `Audit`, typed `Validation`, `JobHandle`, source-generator dispatch, schema-versioned wire envelopes), but the core wire model and converter heritage stay Zaid's. **If your use case fits upstream Fable.Remoting unmodified, use it directly** — it's well-maintained, broadly adopted, and excellent. ToolUp forked because we needed seams that didn't exist upstream, not because anything was wrong with what's there.

**ToolUp.Elmish — built on Fable.Elmish (Eugene Tolmachev + the Elmish community, Apache 2.0).** ToolUp.Elmish began as a fork of [Fable.Elmish](https://github.com/elmish/elmish) by Eugene Tolmachev with contributions from a community of F# developers over 8+ years. The Elm Architecture core (`Program<>`, `Cmd<'msg>`, `Sub<'msg>`, `Dispatch<'msg>`, `init`/`update`/`view`) is bit-for-bit Eugene's design and code; ToolUp's variant is an opinionated trim of that surface (dropping `Cmd.OfFunc` / `OfPromise` / `OfTask` / `OfValueTask` / `OfAsyncWith` / `OfAsyncImmediate` / WebSharper paths / `cmd.obsolete.fs` v3.x shims that ToolUp consumers never used) plus ToolUp-specific additions (`IDispatcher`, `Prefetch`, lifetime-aware `EffectHandle`, structured `ErrorContext`, `Cmd.OfRemoting`). The trim is opinionated, not adversarial. **If your code base uses any of the dropped surface or you simply want canonical Elmish, use upstream directly** — it remains the F# MVU standard and the right choice for the broad community it serves. ToolUp forked to fit a specific consumer base, not to replace what's there.

Both upstream projects remain under their original licences (MIT for Fable.Remoting, Apache 2.0 for Fable.Elmish); the relevant attributions are reproduced in [NOTICE.md](NOTICE.md).

**What the Elmish fork adds over upstream Fable.Elmish v5.x:**

- **`IDispatcher<'msg>`** — typed out-of-band dispatch handle, replacing the `let mutable shellDispatch : (Msg -> unit) option = None` pattern every non-trivial Elmish app reinvents. Carries an `IsActive` flag so background callbacks (SSE reconnect timers, notification listeners) no-op cleanly after `Program.withTermination` fires rather than dispatching against a torn-down loop.
- **`Prefetch<'a>` + `Prefetch.onAllReady`** — codifies boot-time multi-source data loading (load Configs in parallel with Flags, fire `ReinitActiveModule` when the last one resolves) without ad-hoc `IsConfigsPending` / `IsFlagsPending` bookkeeping fields.
- **Structured `ErrorContext` + `Program.withErrorReporter`** — the upstream `(string * exn) -> unit` shape loses module id, phase, correlation id; the structured reporter carries all of them.
- **`EffectHandle<'msg>` with explicit `Lifetime`** (`Program` / `Module of moduleId` / `Manual`) — subscription cleanup that doesn't leak across hot reloads. The runtime + HMR dispose lifetime-scoped effects automatically; the old `Cmd.ofEffect (fun d -> SomeClient.subscribe (M >> d) |> ignore)` pattern that never tears down is the leak this primitive replaces.
- **`Cmd.OfRemoting.{call, callWithRetry}`** — typed Cmd helpers for the dominant RPC-call pattern (`Cmd.OfAsync.either api.Method arg OkMsg ErrMsg`). Same shape with intent-naming, plus a `RetryPolicy` knob for transient transport failures and integration with the transport's correlation-id propagation.
- **Trimmed unused surface** — `Cmd.OfFunc`, `Cmd.OfPromise`, `Cmd.OfTask`, `Cmd.OfValueTask`, `Cmd.OfAsyncWith`, `Cmd.OfAsyncImmediate`, the WebSharper paths and v3.x shims are dropped (zero observed call sites across the consumer base). Migration cost: none.

**What the ToolUp.Remoting fork adds over upstream Fable.Remoting:**

- **Body normalisation folded into the dispatcher.** `unit -> Async<T>` API methods, empty / `"null"` / `""` request bodies, and `DateTimeOffset` round-tripping all work out of the box — the standalone `RemotingBodyNormalizationMiddleware` consumers used to wire into the pipeline is no longer required.
- **Bundled `FableJsonConverter`** — F# discriminated unions land as `{ "Case": "X", "Fields": [...] }` on SSE / non-Remoting JSON surfaces too, matching the shape `Fable.SimpleJson` parses on the client. No second JSON-converter pick-list to maintain.
- **Foundation for categorised error envelopes, typed validation, idempotency-key memoisation, AsyncSeq streaming, and JobHandle long-running operations** as those land in subsequent point releases. The single-source-of-truth dispatcher means each addition arrives without consumer-side middleware changes.

The Elmish runtime's classical Elm Architecture (Init / Update / View / Cmd / Sub) is fully preserved — the additions above are refinements *inside* that pattern, not departures from it. `Program<'arg, 'model, 'msg, 'view>`, `mkProgram`, `mkSimple`, `withSubscription`, `withReactSynchronous`, `Cmd.batch` / `Cmd.ofMsg` / `Cmd.OfAsync.{either, perform, attempt}` all match upstream bit-for-bit.

## Package families

| Package | Purpose |
|---|---|
| `ToolUp.Platform.{Core,Server,Client,Build}` | Core SDK: composition root, scope resolution, default in-process implementations |
| `ToolUp.AI.{Core,Server,Client}` | AI agent loop, SSE streaming, tool registry, system-prompt composition |
| `ToolUp.RAG.{Core,Server}` | Retrieval-augmented generation: chunking, vector store, retrieval pipeline |
| `ToolUp.KnowledgeBase.{Core,Server,Client}` | Document KB: upload + multi-format extraction + notes + narrative-commit |
| `ToolUp.Forms.{Core,Server,Client}` | Schema-driven forms + workflows + publishable surveys |
| `ToolUp.Scheduling.{Core,Server}` | Booking + recurrence + iCalendar |
| `ToolUp.AIProviders.{Claude,OpenAI}` | LLM provider implementations |
| `ToolUp.EmbeddingProviders.{Local,OpenAI}` | Embedding provider implementations |
| `ToolUp.AuthProviders.{Oidc,OidcClient,ClerkUI}` | Auth providers |
| `ToolUp.Storage.{AwsS3,Azure,GoogleCloud}` | `IBlobStorage` companions |
| `ToolUp.AuditSinks.{S3Archive,SplunkHec,DatadogLogs}` | Audit-trail replication |
| `ToolUp.NotificationChannels.{Redis,Email.Smtp,Email.SendGrid,Sms.Twilio,Push.WebPush}` | Real-time + transactional notification companions |
| `ToolUp.Metrics.OpenTelemetry` | OTLP metrics export |
| `ToolUp.Secrets.AzureKeyVault` | `ISecretStore` companion |
| `ToolUp.VectorStores.Hnsw` | Scalable HNSW vector store |
| `ToolUp.AgGridEnterprise` | AG Grid Enterprise initialisation shim |

## Building from source

```bash
dotnet build ToolUp.Forge.sln            # full build
# Test suites are Expecto console runners — run EACH via `dotnet run`, not `dotnet test`
# (Expecto runners ship as `<OutputType>Exe</OutputType>`, so `dotnet test` exits 0 with no tests run — a silent false-green):
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj
dotnet run --project src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj
dotnet run -- Pack                       # produce nupkgs to ../local-nuget-feed/
dotnet run -- Format                     # fantomas
```

See [`CLAUDE.md`](CLAUDE.md) for contributor conventions when working with Claude Code.

## Six portability rules

Infrastructure interfaces that could plausibly be implemented by a distributed framework (`IJobScheduler`, `IModuleQueryBus`, `INotificationChannel`, `IShareTokenStore`, etc.) satisfy six rules: identity by value, async at every boundary, retry as data, stateless handlers, no cross-shard ordering promises, explicit precision floor. See [`docs/platform/portability-rules.md`](docs/platform/portability-rules.md).

Contract test packs in `ToolUp.Platform.Tests` / `ToolUp.Forms.Tests` / `ToolUp.Scheduling.Tests` bind to any conforming implementation — external impls validate against the same conformance bar.

## Contributing

- License: [Apache 2.0](LICENSE).
- DCO `Signed-off-by:` required on every commit — CI enforces.
- Contribution flow + maintenance tiers: [CONTRIBUTING.md](CONTRIBUTING.md).
- Code of Conduct: Contributor Covenant v2.1 (shipping shortly).
- Security disclosure: [SECURITY.md](SECURITY.md).
- Trademark policy: [TRADEMARK.md](TRADEMARK.md).

## Versioning

`0.x.y` while the public surface is unstable. SemVer-on-`0.x` policy:
- **Minor bumps** may include breaking changes.
- **Patch bumps** are non-breaking.
- `1.0.0` declared once the surface is stable.

Each companion versions independently — `ToolUp.Platform.Core 0.3.0` can pair with `ToolUp.AI 0.5.0`. Compatibility documented per release.

## License

[Apache License 2.0](LICENSE). Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK).
