# 1055 — `ApiContract`: one description of an API record

## What changes

An API record is now read once, by `Contract.read`, into an `ApiContract` value: one `MethodContract` per
method (route, flattened arguments, `Unary | Streaming | LongRunning` shape, declared `Classified`, rate
budgets, audit kind and input, idempotency, validated input), the synthesised `<m>/status`, `<m>/progress`
and `<m>/cancel` companions of each long-running method, the wire-schema table, and the refusals the record
carries. `Contract.bind` applies a composition's `ContractFlags` (resolver composed, record seams composed,
audit required on role-gated methods, route builder). The dispatcher reads the contract; the refusal
sentences are unchanged, and a record that makes several refusals now names all of them, one per line.

The generator's `dispatch` command emits the contract as data beside the invocation table:
`GeneratedInvocationTable<'impl>` gains `.Contract`, and a registered table means the record is not walked by
reflection at boot. To emit it the generator runs the server's own reader, from
`ToolUp.Platform.Server.Remoting.dll` beside the built assembly or named by `--server-assembly` (the
`ServerAssembly` item metadata).

## What a consumer does

A consumer with a committed generated dispatch table (`<Record>Dispatch.fs`) reruns the generator's
`dispatch` command; the `.targets` integration does it on the next build. If the project that declares the
`ToolUpRemotingDispatch` item does not reference the server tier (a contract project on FSharp.Core or the
shared tier only), name the reader:

```diff
 <ToolUpRemotingDispatch Include="..\MyApp.Server\Generated\OrdersDispatch.fs"
                         ApiRecord="MyApp.Contract.IOrdersApi"
+                        ServerAssembly="..\..\path\to\ToolUp.Platform.Server.Remoting.dll" />
```

A table generated before this release still compiles (`GeneratedInvocation.table`) and reads its contract
off the record the first time it is asked. Code that called a classifier (`AuthClassifier.classify`,
`RateLimit.classify`, …) keeps its signature.

The generator's `DispatchTable` record gains a `Contract` field (the emitted F# source): code that built a
`DispatchTable` by hand adds it.

## Verify

`dotnet build` the declaring project and check the regenerated file now opens with
`let contract: ToolUp.Remoting.Server.ApiContract =` and that `invocations` is built with
`GeneratedInvocation.tableWith contract`.

## Rollback

Restore the previous generated file; `GeneratedInvocation.table` reads the contract by reflection.
