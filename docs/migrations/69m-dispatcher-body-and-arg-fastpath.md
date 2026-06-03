# Phase 69m — Dispatcher body + argument-parse fastpath (migration)

## What changes

Two related perf wins on the ToolUp.Remoting dispatcher hot path, surfaced by Findings F2 + F3 of [`application-plans/toolup-remoting-hot-path-perf.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting-hot-path-perf.md):

**F2 — argument-parse single-walk.** Previously the dispatcher parsed the outer arguments-array JSON into a `JsonDocument`, captured each element's raw JSON text via `GetRawText()`, then re-parsed each captured string into its own `JsonDocument` via `JsonSerializer.Deserialize<'inp>(argText, opts)`. For an N-argument method that's `N + 1` parses per call.

After 69m: `parseArgumentArray` returns `JsonElement list` (each element is `Clone()`d so it survives the parent `JsonDocument`'s `use` scope). The per-arg deserialise calls `JsonElement.Deserialize<'inp>(opts)` which walks the existing element's tokens — no re-parse. **One parse total per call.**

**F3 — body re-read elimination.** The Giraffe adapter maintains a lazy body cache (bytes / text / hash) populated when an upstream pre-flight stage (validation / audit / idempotency-hash) demands it. Pre-69m the proxy still independently read `ctx.Request.Body` via a `StreamReader`, materialising the body string a second time. For a 1 MiB body on a validation-armed method that was ~2 MiB of string allocations per request.

After 69m: `InvocationProps<'impl>` carries `InputBytes: byte[] option`. The Giraffe adapter populates it from `cachedBodyBytesCell.Value` just before invoking the proxy. When populated, the proxy parses directly from the bytes via `parseArgumentArrayBytes` (using `JsonDocument.Parse(ReadOnlyMemory bytes)` — no string materialisation). When `None` (cache wasn't forced; or non-Giraffe adapters without a cache), the proxy falls back to the `StreamReader` path unchanged.

**G — audit reuses validation-parsed args.** When both `[<Validate>]` and `[<Audit>]` are armed for the same method, the audit-payload extraction at the end of dispatch now reuses the first-arg value validation already parsed (cached in a per-request `validationParsedFirstArg` cell). Saves one `parseFirstArgFromBody` call per request on dual-armed methods.

Net effect on a 1 MiB validation + audit + multi-arg call: from ~3 string materialisations × 1 MiB + N+1 JsonDocument parses → 1 byte-array materialisation + 1 JsonDocument parse + 1 `parseFirstArgFromBody` (validation; audit reuses).

## Diff to apply

**No consumer source change required for SDK consumers.** The wire format is unchanged (the byte-pin tests in the Remoting suite confirm it). Consumers pick up the win automatically at the next package upgrade.

**Custom-adapter authors who build their own `InvocationProps<'impl>`** (third-party adapters that compose against the public `ToolUp.Remoting.Server.Proxy` surface) MUST add `InputBytes = None` to their record construction:

```fsharp
let props : InvocationProps<'impl> = {
    ImplementationBuilder = ...
    EndpointName = ...
    Input = ctx.Request.Body
    InputBytes = None   // ← Phase 69m additive field; None = fall back to Input stream
    IsProxyHeaderPresent = ...
    HttpVerb = ...
    InputContentType = ...
    Output = output
}
```

In-tree forge adapters (Giraffe + AspNetCore middleware) have already been updated.

The `InvocationPropsInt.Arguments` field changed from `Choice<byte[], string> list` to `Choice<byte[], JsonElement> list`. This type is `internal` so no consumer-visible break.

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — green (new `Phase 69m — Dispatcher body + arg-parse fastpath` pack adds 10 source-audit tests pinning the JsonElement-shape, `InputBytes` plumbing, audit-reuse cache, and exception-path body-text materialisation).
- The 69m test pack lives in [`src/ToolUp.Platform.Tests/InProcess/DispatcherBodyAndArgFastpathTests.fs`](../../src/ToolUp.Platform.Tests/InProcess/DispatcherBodyAndArgFastpathTests.fs).
- An integration-shape allocation-pin test (compose a multi-arg API + dispatch through a TestServer; assert one outer `JsonDocument.Parse` call and zero `StreamReader` allocations on the cached-bytes path) is deferred to a follow-up TIDY-UP item — same TestServer-scaffold gap as the Phase 69l deferral.
- Wire format byte-identical: the existing 704+ Remoting byte-pin tests cover the regression class.

## Rollback

Revert in this order:
1. Per-arg path in `Proxy.fs`'s `makeEndpointProxy` — restore the `Choice2Of2 argText :: t -> deserialiseArgWithBackend<'inp> ... argText` shape.
2. `deserialiseArgWithBackend` signature back to `string -> 'inp` using `JsonSerializer.Deserialize<'inp>(argText, opts)`.
3. `parseArgumentArray` signature back to `string list` via `el.GetRawText()`.
4. `parseArgumentArrayBytes` — delete.
5. Multipart text section — restore the `Choice2Of2(System.Text.Encoding.UTF8.GetString(buffer.GetReadOnlySequence().ToArray()))` shape.
6. `Types.fs`: `InvocationPropsInt.Arguments` back to `Choice<byte[], string> list`; remove `InvocationProps<'impl>.InputBytes`.
7. `GiraffeAdapter.fs`: remove the `propsWithCache` rebuild + the `InputBytes` line in initial props.
8. `Middleware.fs` (AspNetCore): remove the `InputBytes = None` line.
9. `Proxy.fs:memberVisitor` body-read: restore the `use sr = new StreamReader(props.Input); let! text = sr.ReadToEndAsync()` direct path.
10. Exception path — restore `requestBodyText` (drop the `resolvedBodyText` materialisation).

## See also

- [Phase 69b — `ToolUp.Remoting.Server` platform seams](../../../ToolUp-Diametrical/roadmap/phases/69b-toolup-remoting-server-platform-seams.md) — the substrate this fastpath composes on top of.
- [Phase 69l — Telemetry seam zero-cost gate](69l-telemetry-zero-cost-gate.md) — sister perf phase; the allocation-pin pattern.
- [Phase 69n — `fromContextAsync` build-once](../../../ToolUp-Diametrical/roadmap/phases/69n-remoting-fromcontextasync-build-once.md) — sequenced after this phase; lifts the per-request dispatcher rebuild on the `fromContextAsync` arm.
- Source plan: [`application-plans/toolup-remoting-hot-path-perf.md`](../../../ToolUp-Diametrical/application-plans/toolup-remoting-hot-path-perf.md) (Findings F2 + F3 + audit-reuse refinement).
