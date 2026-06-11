# Migration — Phase 110: host-neutral client tree-hosting seam (`ClientHostCapabilities`)

**Status:** additive, opt-in. Existing `withView` / `withPages` / `withFullWidthView` callers compile and run unchanged; a pipeline that never constructs a `ClientHostCapabilities` is byte-for-byte identical to a pre-110 client build (GP 11 / GP 13).

## What changes

A `ClientModule` view body was already an arbitrary `ReactElement` — a rendered typed Node-tree (any external UI language) hosts in the SDK shell today via `withFullWidthView`. Phase 110 ships the missing **runtime seam**: the named capability bag a tree's typed actions route through, so each hosting module doesn't hand-roll the same wiring.

New public surface (all in `ToolUp.Platform.Client`, `Client/ClientHostBridge.fs`):

| Symbol | Purpose |
|---|---|
| `ClientHostCapabilities<'Msg>` | Four hooks: `Navigate` (→ `NavigationRequest.request`), `Notify` (→ `NotificationClient.publishLocal` / `ToastCentre`), `Dispatch` (→ the module's Elmish dispatch), `Call<'r>` (→ `Cmd.OfRemoting` semantics, executed against dispatch) |
| `ToastIntent` (+ `info` / `warning` / `error`) | What a host-raised toast says, at which `SystemMessageLevel` |
| `ClientHostCapabilities.create` | Build the bag from a view's dispatch (the only per-module value needed) |
| `ClientHostView.withElementView` | `withFullWidthView`-shaped builder step whose view also receives the bag |

`ClientHostView` is a separate module (not `ClientModule`) because the builder module is sealed at its definition site earlier in the compile order; the pipeline shape is identical.

## Adopting it

```fsharp
ClientModule.create spec
|> ClientHostView.withElementView (fun model dispatch host ->
    // render the external tree; bind its action vocabulary onto the bag
    MyTreeRuntime.render (page model) {|
        onNavigate = host.Navigate
        onCall = fun call -> host.Call(call, GotResult, Errored)
        onNotify = host.Notify
        onDispatch = host.Dispatch
    |})
|> ClientModule.register
```

Pair with [Phase 113](113-action-authorizer.md) to gate dispatched actions default-deny before executing them.

## Breaking change

None. New file + new types; no existing signature touched.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- Fable: `cd samples/MinimalClient && dotnet fable -o output` clean (drives the full Client tier through the Fable compiler).
- Node harness (`src/ToolUp.AI.Client.Tests` — the established Fable-tier client rig): the `ClientHostBridge (Phase 110)` suite proves Navigate routes through `NavigationRequest` pub/sub, Dispatch round-trips, `ToastIntent` levels, and `Call` maps success / error into the module's `Msg` via dispatch.

## Rollback

Stop calling `ClientHostView.withElementView` / `ClientHostCapabilities.create`. The file is inert when unused.
