# Migration 265 — `ClientHostCapabilities` conformance contract pack

**Status:** test/build infrastructure only — **no runtime surface, no consumer action**.

## What changes

Phase 110 shipped the four-capability client host-bridge seam
(`ClientHostCapabilities<'Msg>` — `Navigate` / `Notify` / `Dispatch` / `Call` — plus the
`ClientHostView.withElementView` builder). Phase 202 proved it renderer-neutral once, with a sample
toy tree, but only via ad-hoc routing checks — not against a reusable, bindable conformance bar a
third tree language (or a distributed / alternate host) could run.

This phase adds that bar:

- **New file:** `src/ToolUp.Platform.Tests/Contracts/ClientHostCapabilitiesContract.fs` — a reusable
  `contract` suite parameterised over a `ClientHostCapabilitiesContractFixture<'Msg>` (the bag under
  test + an observation hook per capability + sample inputs). It asserts: `Navigate` routes its target
  to the shell/sidebar router hook; `Notify` routes every `ToastIntent` level (info / warning /
  error); `Dispatch` forwards into the module's Elmish loop; and `Call` maps its async outcome to a
  `Msg` and dispatches it on both the success and the thrown arm (`Cmd.OfRemoting` semantics).
- **Two in-tree witnesses:** the in-tree default routing behaviour and the Phase 202 `ToyNode` second
  binding (the first non-substrate conformance witness — a different tree language's `Msg` vocabulary
  against the same bar), plus a check that the toy's public `ToyEvent` vocabulary binds every
  capability and maps to host-neutral `ActionDescriptor`s.

There is **no change to any runtime code path.** A consumer that never references the pack is
byte-for-byte unchanged (GP 11) and pays nothing (GP 13). `ClientHostBridge.fs` is untouched.

## Tier split (why some routing is pinned by source-shape)

`Navigate` and `Call` route through the genuine shipped seams (`NavigationRequest.request` and
`Cmd.OfRemoting.call`) and are exercised live under the .NET test runner — `Call`'s `Async.Start`
shape means the dispatched message lands on a background turn, so the bar waits with a bounded poll.
`Dispatch` is observed directly. `Notify`'s shipped routing (`NotificationClient.publishLocal` under
the current identity) is Fable-only — `NotificationClient`'s `EventSource` interop and
`UserSession.getUserId` throw under the .NET runner — so the conformance bar observes the toast
vocabulary through a sink, and the genuine `create` routing (the `SystemMessage(level, text)` mapping
+ the `publishLocal` hop, and the `withElementView` bag round-trip) is pinned by a source-shape check
against `ClientHostBridge.fs`. This is the same client-tier .NET/Fable split Phase 202 documents and
the `IConsentProvider` / `OidcClient` contract bindings already use.

## Verification

```
dotnet build src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- \
  --filter-test-list "ClientHostCapabilitiesContract"
# → 16 passed, 0 failed
```

Wired into `dotnet run --project Build.fsproj -- VerifyAll` automatically: the pack is registered in
`ToolUp.Platform.Tests/Program.fs`, which `VerifyAll` runs, so a routing regression fails CI.

## Rollback

Delete `Contracts/ClientHostCapabilitiesContract.fs`, remove its `<Compile>` entry from
`ToolUp.Platform.Tests.fsproj`, and remove the `ClientHostCapabilitiesContract.tests` line from
`Program.fs`. No runtime impact.

## SDK adoption

⛔ **N-A across all consumers** — test/build infrastructure, no public API, byte-for-byte absent from
any consumer build.
