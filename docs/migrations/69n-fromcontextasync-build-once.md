# Phase 69n — `fromContextAsync` build-once dispatcher table (migration)

## What changes

`Remoting.fromContextAsync` now matches the build-once / read-per-call semantics its docstring promises. Pre-69n the Giraffe adapter's `fromContextAsync` arm called `buildFromImplementation` on every request, rebuilding the entire dispatcher substrate (TypeShape proxy + six attribute-driven classifier maps + `rmsManager` + compose-time guards) per dispatch. The async resolver itself ran per request as intended, but the substrate cost was paid every time — a load-bearing latent landmine for any consumer adopting the documented pattern at scale.

After 69n:

1. **`GiraffeUtil.buildDispatcherTable<'impl>`** is a new function that runs all the one-time substrate construction and compose-time guards, then returns a curried `(HttpContext -> 'impl) -> HttpHandler` function. The proxy + classifier tables live in the returned function's closure.
2. **`GiraffeUtil.buildFromImplementation`** is now a thin shim — `buildDispatcherTable options implBuilder`. Preserves the pre-69n signature so the `StaticValue` / `FromContext` arms of `buildHttpHandler` continue to compile unchanged.
3. **`buildHttpHandler`'s `FromContextAsync` arm** binds `let dispatch = GiraffeUtil.buildDispatcherTable options` ONCE at compose time, then the per-request closure just awaits the async resolver and calls `dispatch (fun _ -> impl) next ctx`. The substrate is built once; only the async resolver and the resulting impl-builder closure run per request.
4. **The `Remoting.fromContextAsync` docstring's "Performance caveat (until Phase 69n ships)" block is retired** (added in the TIDY-UP "ToolUp.Remoting per-request cleanups" bundle's F4-companion item — it was the holding action until this phase shipped).

## Diff to apply

**No consumer source change required.** Internal refactor; the public surface (the `Remoting.*` setters + `Api.make` wrapper + `buildHttpHandler`) is unchanged. Consumers pick up the build-once behaviour automatically at the next package upgrade.

Consumers that had been holding off on `fromContextAsync` because of the per-request rebuild cost can now adopt it at any scale; this migration is the perf alignment that pattern always needed.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — green (new `Phase 69n — fromContextAsync build-once` pack adds source-audit tests pinning the carve-out + the compose-time bind + the retired docstring caveat + the AspNetCore-side parity-refusal preservation).
- Pack lives at [`src/ToolUp.Platform.Tests/InProcess/FromContextAsyncBuildOnceTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/FromContextAsyncBuildOnceTests.fs).
- An integration-shape build-once test (compose `fromContextAsync` with a tracking resolver + a tracking `makeApiProxy` substitute; dispatch 100 requests; assert one `makeApiProxy` call) is deferred to the shared TestServer-scaffold follow-up TIDY-UP item along with the Phase 69l + 69m deferrals.
- `StaticValue` and `FromContext` paths produce byte-identical behaviour — they continue through the `buildFromImplementation` shim which delegates to `buildDispatcherTable`. The existing 704+ Remoting byte-pin test gallery covers the wire format.

## Rollback

Revert in this order:
1. `buildHttpHandler`'s `FromContextAsync` arm: restore the per-request `GiraffeUtil.buildFromImplementation (fun _ -> impl) options next ctx` call inside the closure (drop the `let dispatch = ...` compose-time bind).
2. `GiraffeAdapter.fs`: revert the `buildDispatcherTable<'impl> options = ...` carve-out and the inner `fun implBuilder -> fun next ctx -> task { ... }` wrap. Restore `buildFromImplementation<'impl> implBuilder options = ...` as the top-level shape.
3. `Remoting.fs:fromContextAsync` docstring: restore the "Performance caveat (until Phase 69n ships)" block (it lived in the TIDY-UP F4-companion entry; restoring it from the git history of that bundle would be the canonical undo).

## See also

- [`69b-remoting-platform-seams.md`](69b-remoting-platform-seams.md) — the `ToolUp.Remoting.Server` platform seams this phase hoists, and the `Remoting.fromContextAsync` per-request async resolver this phase makes safe at scale.
- [`69l-telemetry-zero-cost-gate.md`](69l-telemetry-zero-cost-gate.md) — sister perf phase in the same cluster.
- [`69m-dispatcher-body-and-arg-fastpath.md`](69m-dispatcher-body-and-arg-fastpath.md) — sister perf phase; landed before this one.
- AspNetCore middleware adapter `FromContextAsync` refusal at [`Middleware.fs`](../../src/ToolUp.Platform.Server/Server/Remoting/AspNetCore/Middleware.fs) stays in place — that refusal is for adapter-parity reasons (not the rebuild cost). Lifting it is a separate question.
