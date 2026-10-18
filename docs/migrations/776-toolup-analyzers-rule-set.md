# Phase 776 — the containment rules (TUA001–TUA003) in `ToolUp.Remoting.Analyzers`

**What changes.** The SDK's analyser package gains a second analyser, `ToolUpContainment`, with three
rules. Each is the compile-time half of a runtime check, and every message names the sanctioned
alternative:

| Code | Flags | Sanctioned form |
|------|-------|-----------------|
| `TUA001` | Container resolution (`GetService`, `GetRequiredService`, `GetServices`, the keyed forms, `RequestServices`) outside service registration | Resolve inside a registration factory (`AddSingleton` / `AddScoped` / `AddTransient` / `AddKeyed*` / `TryAdd*` / `AddHostedService`) and pass the dependency to the handler as a value |
| `TUA002` | An `HttpClient` constructed without the platform egress handler | `PlatformHttpClient.create surface`, or `new HttpClient(PlatformHttpClient.wrap surface handler)` |
| `TUA003` | `Async.RunSynchronously` outside a program entry point | Stay asynchronous (`let!` / `do!`); block only inside an `[<EntryPoint>]` |

All three report as **Warning** in the editor and on the CLI. The full rule reference, generated from
the rule metadata, is [`docs/platform/analyzer-rules.md`](../platform/analyzer-rules.md).

**Scope.** Additive, opt-in tooling (GP 11 / GP 13). The package keeps its id
(`ToolUp.Remoting.Analyzers`) and its version line; `TUR0001` / `TUR0002` are unchanged. A consumer
that does not reference the package is byte-for-byte unchanged, and one that does sees the three new
warnings the next time the analyser host runs.

## Adopting

Nothing to do beyond the existing reference — upgrading the package brings the rules:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Analyzers" Version="$(ToolUpSdkVersion)" PrivateAssets="all" GeneratePathProperty="true" />
</ItemGroup>
<PropertyGroup>
  <FSharpAnalyzersOtherFlags>--analyzers-path &quot;$(PkgToolUp_Remoting_Analyzers)/analyzers/dotnet/fs&quot;</FSharpAnalyzersOtherFlags>
</PropertyGroup>
```

To silence a finding you have decided to keep, use the analyser host's ignore comment on the line
(`// fsharpanalyzer: ignore-line TUA003`) and say why beside it.

## Verifying

Run the analyser host over a project that holds one of each shape: a `ctx.RequestServices.GetService`
call in a handler, a bare `new HttpClient()`, and an `Async.RunSynchronously` in an ordinary
function. Each reports its code; moving the resolution into an `AddSingleton` factory, the client
behind `PlatformHttpClient.create`, and the blocking call into `main` clears it.

## Rollback

Pin the package to the previous SDK version, or drop the `--analyzers-path` flag. Nothing at runtime
depends on the analyser.
