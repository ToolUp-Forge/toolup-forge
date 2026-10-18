# Analyzer rules

<!-- GENERATED from src/ToolUp.Remoting.Analyzers/Rules.fs. Do not edit by hand: the `Analyzers` leg of VerifyAll fails when this file differs from the render. Regenerate with `dotnet run --project src/ToolUp.Remoting.Analyzers.Tests -- --emit-rules-doc docs/platform/analyzer-rules.md`. -->

The `ToolUp.Remoting.Analyzers` package is the SDK's static analyser rule set: [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) analysers that read the untyped syntax tree, so they need no project options and run in the editor, on the CLI and in CI. It is versioned with the SDK. Each rule is the compile-time half of a runtime check, so a violation is caught twice, at the earlier of the two, and every message names the sanctioned alternative.

## Rules

| Id | Title | Severity | Since |
|---|---|---|---|
| `TUR0001` | Remoting API method without an authorisation classification | Error | 0.7.0 |
| `TUR0002` | Unaudited Remoting method whose input carries a `[<PiiSafe>]` field | Warning (opt-in) | 0.7.0 |
| `TUA001` | Container resolution outside service registration | Warning | 0.28.0 |
| `TUA002` | `HttpClient` constructed without the platform egress handler | Warning | 0.28.0 |
| `TUA003` | `Async.RunSynchronously` outside a program entry point | Warning | 0.28.0 |

### TUR0001 — Remoting API method without an authorisation classification

- **Severity:** Error
- **Since:** 0.7.0
- **Rationale:** The Remoting dispatcher refuses to start when an API-record method carries none of the authorisation classifications. Finding it at edit time is strictly cheaper than finding it at server start.
- **Sanctioned alternative:** Classify the method: `[<RequiresRole>]`, `[<RequiresClaim>]`, `[<TenantScoped>]`, `[<AllowAnonymous>]` or `[<PublicEndpoint>]`. The codefix inserts the fail-closed placeholder `[<AllowAnonymous>]`, which must be replaced deliberately.
- **Runtime half:** The dispatcher's startup authorisation classifier.

### TUR0002 — Unaudited Remoting method whose input carries a `[<PiiSafe>]` field

- **Severity:** Warning (opt-in)
- **Since:** 0.7.0
- **Rationale:** Personal data flows through the method and nothing records that it did. Off by default: set `TOOLUP_REMOTING_ANALYZER_AUDIT=1` in the analyser host's environment to enable it.
- **Sanctioned alternative:** Annotate the method with `[<Audit(...)>]`. The codefix inserts `[<Audit("Custom:TODO")>]` as a placeholder.
- **Runtime half:** The audit pipeline's per-method annotation.

### TUA001 — Container resolution outside service registration

- **Severity:** Warning
- **Since:** 0.28.0
- **Rationale:** A module that resolves services from the container by hand (`GetService`, `GetRequiredService`, `GetServices`, the keyed forms, or `HttpContext.RequestServices`) reaches substrate its declared seam grant never names, so the grant describes what the module declares rather than what it does.
- **Sanctioned alternative:** Resolve inside a registration factory (the lambda handed to `AddSingleton` / `AddScoped` / `AddTransient` / `AddKeyed*` / `TryAdd*` / `AddHostedService`) and pass the dependency to the handler as a value. An `IServiceProvider` implementation delegating from its own `GetService` member is not flagged.
- **Runtime half:** The seam-aware service provider, which refuses a resolution outside the module's grant at runtime.

### TUA002 — `HttpClient` constructed without the platform egress handler

- **Severity:** Warning
- **Since:** 0.28.0
- **Rationale:** A hand-built `HttpClient` sends its requests around the platform's outbound-call seam, so the egress policy, its audit and its deny decisions never see them.
- **Sanctioned alternative:** `PlatformHttpClient.create surface`, or `new HttpClient(PlatformHttpClient.wrap surface handler)` when a custom handler is needed. A construction whose arguments name `PlatformHttpClient` is not flagged.
- **Runtime half:** The server-side egress policy handler on the outbound-call seam.

### TUA003 — `Async.RunSynchronously` outside a program entry point

- **Severity:** Warning
- **Since:** 0.28.0
- **Rationale:** Blocking a thread-pool thread on an asynchronous computation starves the pool under load and turns a slow dependency into a stalled server; on a request path it is never correct.
- **Sanctioned alternative:** Stay asynchronous: compose with `let!` / `do!` in an `async` or `task` expression and return the computation. Blocking is sanctioned only inside an `[<EntryPoint>]` function.
- **Runtime half:** None: a blocked request thread has no runtime refusal, which is why this rule exists.

## Wiring the analysers into a consumer project

Reference the package; it contributes no compile or runtime surface:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Analyzers" Version="$(ToolUpSdkVersion)" PrivateAssets="all" />
</ItemGroup>
```

Then point the analyser host at the package's `analyzers/dotnet/fs` directory. With the `FSharp.Analyzers.Build` MSBuild integration (and the `fsharp-analyzers` tool installed), set the `FSharpAnalyzersOtherFlags` property and run the `AnalyzeFSharpProject` target:

```xml
<PropertyGroup>
  <FSharpAnalyzersOtherFlags>--analyzers-path &quot;$(PkgToolUp_Remoting_Analyzers)/analyzers/dotnet/fs&quot;</FSharpAnalyzersOtherFlags>
</PropertyGroup>
```

`$(PkgToolUp_Remoting_Analyzers)` is defined when the reference carries `GeneratePathProperty="true"`. From the command line the same run is:

```pwsh
dotnet tool install --global fsharp-analyzers
fsharp-analyzers --project MyProject.fsproj --analyzers-path <restored-package>/analyzers/dotnet/fs
```

## How this repository applies the rules

The `Analyzers` leg of `VerifyAll` parses every tracked `.fs` file under `src/` and runs the `TUA` rules over it. The committed inventory `src/ToolUp.Remoting.Analyzers/analyzer-inventory.json` declares the paths out of scope (test packs, benchmarks, tools) and carries each known site as a per-file count with a one-line reason. A site the inventory does not account for is a finding, and so is an inventory entry the tree no longer needs, so the inventory can only be tightened deliberately. Any finding fails the leg.
