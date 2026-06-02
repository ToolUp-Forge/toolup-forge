# Phase 69b.tail — `Api.make` auto-composes ToolUp.Remoting platform seams

Phase 69b shipped the dispatcher-level platform seams (body normalisation, per-method `IRemotingTelemetry`, per-request `IAuthContext`, correlation-id propagation, categorised error envelopes) as substrate. Phase 69b.tail (forge `0.4.4`) wires those seams through the public `Api.make` wrapper as default-on behaviour. Consumers upgrade by bumping the `ToolUp.Platform.Server` package version — no code change required for the common case.

## What changes

The `Api.make` signature stays source-compatible. Existing call sites:

```fsharp
Api.make (api, errorHandler = errorHandler)
```

continue to compile and run unchanged. What changes is what the wrapper now composes by default:

| Seam | Before 0.4.4 | After 0.4.4 |
|---|---|---|
| Body normalisation (`unit -> Async<T>` methods) | Dispatcher default since Phase 69b.A. | Unchanged — dispatcher default. |
| Correlation-id propagation | Dispatcher default since Phase 69b.D. | Unchanged — dispatcher default. |
| Categorised error envelopes | Dispatcher default since Phase 69b.E. | Unchanged — dispatcher default. |
| `IRemotingTelemetry` hook | Consumer-supplied via `?telemetry`; omitted = zero emissions. | Default sink bridges to forge's registered `IMetricsSink` (one `Record("toolup.remoting.elapsed_ms", elapsedMs, …)` per call). With `NoMetricsEndpoint` the sink resolves to `NoOpMetricsSink`, so the bridge is a true no-op (GP 13). |
| Per-request `IAuthContext` | Consumer-supplied via `?authContext`; the 0.4.3 silent-no-op guard refused at composition when forge auth attrs were declared without a resolver. | Default resolver reads Phase 66's `Subject` + `AuthenticatedUser` from `HttpContext.Items` (populated by `ScopeResolutionMiddleware` running `ISubjectResolver`). 0.4.3 guard collapses into the default. Bespoke claim semantics continue to wire via `?authContext`. |

The body-normalisation behaviour the standalone `RemotingBodyNormalizationMiddleware` provided pre-Phase 69a is **inside the dispatcher** now. The middleware class was deleted from `ToolUp.Platform.Server.Middleware` and its registration removed from `ConfigurePipeline.fs` in the Phase 69a + 69b.A sweep. The behaviour is identical on the wire — `unit -> Async<T>` calls that arrive with empty / `null` / `""` bodies still get rewritten to `[]` before dispatch.

## Diff to apply

For the common case — a forge consumer composing the SDK with no custom remoting customisations — **no diff is required**. The seams light up on the next package upgrade.

The only consumer-visible change is for deployments that previously registered `RemotingBodyNormalizationMiddleware` directly in their pipeline (rare — most consumers used the SDK's auto-registration). If your composition has a line like:

```fsharp
app.UseMiddleware<RemotingBodyNormalizationMiddleware>() |> ignore
```

Remove it. The dispatcher handles body normalisation itself; the middleware class no longer exists.

Consumers who supply custom telemetry continue to override the default:

```fsharp
Api.make (api, errorHandler = eh, telemetry = mySink)
```

`mySink` wins over the IMetricsSink bridge. Same shape for `?authContext`.

## Verification

After bumping `ToolUp.Platform.Server` to `0.4.4`:

1. `dotnet build` succeeds against the consumer's solution.
2. The consumer's `unit -> Async<T>` API methods continue to dispatch successfully (curl `POST /api/<RecordType>/<MethodName>` with an empty body → 200 OK + JSON response).
3. With `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint`, `GET /metrics` shows a `toolup_remoting_elapsed_ms` series with `method` + `outcome` labels stamped per call.
4. The `samples/HelloWorld` reference build at `toolup-forge/samples/HelloWorld/` is the canonical worked example — boot it (`dotnet run --project HelloWorld.Server`), curl a method, and inspect the console output for the per-method telemetry line.

## Rollback

If the auto-composed defaults misbehave for your deployment:

1. Pin back to `0.4.3` until the issue is diagnosed.
2. To opt out of the IMetricsSink bridge for one API record without downgrading, pass `?telemetry` explicitly with a no-op sink:
   ```fsharp
   let noOpTelemetry =
       { new IRemotingTelemetry with
           member _.OnMethodCompleted _ = () }
   Api.make (api, errorHandler = eh, telemetry = noOpTelemetry)
   ```
3. To opt out of the default `ForgeAuthContext` resolver on a specific record, pass `?authContext` with your own resolver (the consumer-supplied resolver wins).

The substrate seams remain available on `Remoting.create*` for direct composition — the wrapper change at the `Api.make` layer is what lights them up automatically, and reverting the package version reverts the wrapper composition.

## See also

- Substrate definitions in `toolup-forge/src/ToolUp.Platform.Server/Server/Remoting/` (Auth.fs, Errors.fs, CallContext.fs, Diagnostics.fs).
- Phase 66's `ISubjectResolver` middleware integration in `toolup-forge/src/ToolUp.Platform.Server/Server/Middleware.fs`.
- Phase 9e's `IMetricsSink` substrate in `toolup-forge/src/ToolUp.Platform.Server/Server/IMetricsSink.fs`.
- Workspace [`SDK-ADOPTION.md`](../../../SDK-ADOPTION.md) for cross-sibling adoption status.
