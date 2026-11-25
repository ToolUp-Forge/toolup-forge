# Forge takes Fuaran.MVU — the client runtime leaves the tree

## What changes

`ToolUp.Platform.Client` no longer carries its own MVU runtime. The shell runs on the published
[`Fuaran.MVU`](https://www.nuget.org/packages/Fuaran.MVU) runtime and mounts through its React
adapter, `Fuaran.MVU.React` (both 0.1.1, both Apache-2.0, both arriving transitively through
`ToolUp.Platform.Client` — add no `PackageReference`). Coming from the 0.42 line:

- **The `ToolUp.Elmish`, `ToolUp.Elmish.React` and `ToolUp.Elmish.HMR` namespaces are gone**, and so
  is the `ToolUp.Elmish.Kernel` package. What forge keeps over the runtime lives in
  `namespace ToolUp.Platform`: `RemoteEffect`, `Subscription`, `RemoteRetryPolicy`, `RemoteCallInfo`,
  `IRemotingInterceptor`, `Dispatch<'msg>`, `ErrorContext` / `ErrorPhase` and `Memo`.
- **A module's `init` and `update` return a step list**, `Step<'Msg, RemoteEffect<'Msg>> list`, where
  they returned `Cmd<'Msg>`. A step is `Do effect` (perform an effect value) or `Then msg` (process a
  follow-up message next). Views do not change: they still return `PageContent` / `ReactElement`.
- **A remote call is a value** `update` returns, `RemoteEffect.call`, which the shell performs. Its
  interceptors, retry policy and declared read policies work as before.
- **A long-lived source is a resource** — a `Subscription` the program lists — which the runtime
  starts once and stops once.
- **Three behaviours change**: the order of follow-ups, a declared cached read answers once, and a
  throwing `update` is refused whole (all below).

## Search and replace

Mechanical; apply in this order over the client project.

| Was | Now |
|---|---|
| `open ToolUp.Elmish` | `open Fuaran.MVU` (and `open ToolUp.Platform`, which most client files already have) |
| `open ToolUp.Elmish.React`, `open ToolUp.Elmish.HMR` | delete the line |
| `Cmd<'msg>` | `Step<'msg, RemoteEffect<'msg>> list` |
| `Cmd.none` | `[]` |
| `Cmd.ofMsg m` | `[ Then m ]` |
| `Cmd.batch [ a; b ]` | `a @ b` |
| `Cmd.map f cmd` | `Step.mapAll f (RemoteEffect.map f) steps` |
| `Cmd.OfRemoting.call api.M arg ok err` | `[ Do(RemoteEffect.call "Api.M" api.M arg (function Ok r -> ok r \| Error e -> err e)) ]` |
| `Cmd.OfRemoting.callWithName name api.M arg ok err` | as above, with `name` as the first argument |
| `Cmd.OfRemoting.callWithRetry policy …` / `callWithRetryAndName name policy …` | `RemoteEffect.callWithRetry name policy api.M arg reply` |
| `Cmd.RetryPolicy` (`.defaultPolicy`, `.none`) | `RemoteRetryPolicy` (`RemoteRetryPolicy.defaultPolicy`, `RemoteRetryPolicy.none`) — renamed so it cannot shadow `ToolUp.Platform.Transport.RetryPolicy` or `ToolUp.Offline.RetryPolicy` in a file that opens `ToolUp.Platform` after them |
| `Cmd.OfRemoting.CallInfo` | `RemoteCallInfo` |
| `Cmd.OfRemoting.IRemotingInterceptor` | `IRemotingInterceptor` |
| `Cmd.OfRemoting.Interceptors.register` / `unregister` / `setErrorReporter` | `RemoteEffect.Interceptors.register` / `unregister` / `setErrorReporter` |
| `Program.safeMsgRepr` | `ErrorContext.safeMsgRepr` |
| `ToolUp.Elmish.ErrorContext`, `ErrorPhase`, `Memo`, `Dispatch` | the same names in `ToolUp.Platform` |
| `ModuleHarness` `.Cmd`, `AssertCmd`, `AssertNoCmd` (`ToolUp.Platform.Testing`) | `.Steps`, `AssertSteps`, `AssertNoSteps`; `fromUnitInit` / `fromContextInit` take step-returning functions |
| `ModuleEvents.publish topic payload` used as a `Cmd` | the same call, now a step list: `ModuleEvents.publish topic payload @ steps`; `ModuleEvents.publishNow` from a view's event handler |
| `Client.programLifetimeEffects`, `Client.withShellLifetimeEffects` | `Client.resources config`, lifted with `Subscription.map` |
| a `PackageReference` to `ToolUp.Elmish.Kernel` | delete it |

Forge's own public renames in the same release, for code that called them: `FactBrowseModel.stepWith`
→ `updateWith`; `UserDirectory.resolveSteps` → `resolve`; `SidebarPreferences.persist` returns a
`RemoteEffect<'msg>` and answers a good save; the screens' step functions are their public `update`.

## The judgement changes

**Remote calls — effect values are the default.** Name the call, so interceptors and traces see it:

```fsharp
// before
| Load -> model, Cmd.OfRemoting.call api.GetOrders () OrdersLoaded LoadFailed
// after
| Load -> model, [ Do(RemoteEffect.call "OrdersApi.GetOrders" api.GetOrders () (function
                      | Ok orders -> OrdersLoaded orders
                      | Error ex -> LoadFailed ex)) ]
```

A body with no value form — it composes several asyncs, or reads the browser — ports as the legacy
case, which may dispatch **once**: `Do(RemoteEffect.legacy "Orders.importFile" (fun dispatch -> …))`.
A one-shot async that is not a remote call is `RemoteEffect.await "Orders.parse" parse text reply`; a
body that dispatches nothing (a redirect, a beacon) is `RemoteEffect.signal "Orders.redirect" (fun () -> …)`.
`Cmd.OfAsync.either` / `perform` / `attempt` and `Cmd.ofEffect` port to one of these three.

**Streams and program-lifetime effects become resources.** A `Subscription` is a site name and a
start function returning the handle that stops it; a program lists its resources by key, and the
runtime starts each once and stops it when it leaves the list or the program unmounts:

```fsharp
// before
let init () = model, Cmd.ofEffect (fun dispatch -> Feed.subscribe (Tick >> dispatch) |> ignore)
// after: in the composer's resources
let feed =
    Subscription("orders-feed", fun dispatch ->
        let stop = Feed.subscribe (Tick >> dispatch)
        { new System.IDisposable with member _.Dispose() = stop () })
let resources (_: Model) = Map.ofList [ ResourceKey.constant feed.Site, feed ]
```

The module contract has no resource list: a module's own long-lived source lives in a React component
of its view (an effect hook with cleanup, the shape the assistant's stream takes), or the composer
lists it. A `Subscription` answers as many times as it likes; every other effect answers at most once.

**The order of follow-ups.** `Then m` now runs inside the same chain, straight after `update`, before
any message already queued; the chain commits as one step, then its effects are performed in the order
they were emitted, and a reply is queued behind whatever is queued. On the 0.42 loop `Cmd.ofMsg m` was
queued behind earlier messages, and an effect listed before it could answer first. Only a step list
that holds an effect answering synchronously ahead of a `Then`, or an `update` that dispatches
synchronously, can see the difference; nothing in forge does. To keep the old order, compute the
follow-up's change in the same `update`, or turn the synchronous work into a signal whose answer arrives
as an ordinary message. A drain renders once, however many messages it processed.

**A declared cached read answers once.** On a cache hit, `RemoteEffect.call` answers with the cached
value and the refresh refills the cache for the next caller; it no longer sends the fresh value as a
second message. A module that relied on the second message re-reads on its next `update` that needs it.

**A throwing `update` is refused whole.** No model of the chain is installed and none of its effects is
performed; the error reaches `ClientConfig.Composition.OnElmishError` as an `Update` error for the
message that started it. A chain longer than 256 messages is refused.

**How a composer mounts.** `Client.run` and `AIClientConfig.run` mount for you. A composer that wraps
the shell builds a `Client.ShellProgram` and mounts it on a store:

```fsharp
// before
Client.programOver config init update build view
|> Program.withReactStore store
|> Program.run
// after
let store = Fuaran.MVU.React.ModelStore<OuterModel, ElementView<OuterMsg>>()
Client.programOver config init update resources build view
|> Client.mountAt "elmish-app" Fresh store
|> ignore
```

`resources` lists the shell's own (`Client.resources config`, lifted with `Subscription.map`) beside the
composer's. An app with no shell mounts a component directly: `Live.elements "elmish-app" Fresh host
(Component.plain init update resources (ElementView.ofView view)) RemoteEffect.interpreter ()`.

## What you lose, and what to use instead

| Removed | Use instead |
|---|---|
| `Program.mkProgram`, `mkSimple`, `withSubscription`, `withReactSynchronous`, `withReactBatched`, `withReactStore`, `withReactStoreHydrate`, `withStableDispatch`, `withTermination`, `withRingBufferCapacity`, `Program.run`, `Program.map` | `Fuaran.MVU.Component` (`Component.plain`, `Component.lift`), `Fuaran.MVU.React.Mount` / `Live.elements`, `Client.programOver` + `Client.mountAt`; queue and fuel on `Host` |
| `Program.withErrorReporter`, `withErrorHandler`, `withTrace` | `ClientConfig.Composition.OnElmishError`; `Host.Record` for a trace |
| `Sub<'msg>`, `SubId` | `Subscription` resources |
| `IDispatcher` (`Dispatch`, `DispatchAsync`, `IsActive`, `Terminate`), `Program.withDispatcherHandle` | a `Subscription` whose start receives the dispatch and whose stop marks it inactive; `Mounted.Dispatch` / `Mounted.Unmount()` from a mount. `DispatchAsync` has no counterpart |
| `EffectHandle`, `EffectLifetime`, `IEffectController` | `Subscription` resources (a composer), or a React component with cleanup (a module) |
| `Prefetch<'a>`, `Prefetch.onAllReady`, `PrefetchStatus` | a model field per source (`Remote<unit, string>`) and a guard in `update` that acts when the last one settles |
| `Cmd.OfRemoting.callStreaming`, `callStreamingWithName`, `callStreamingWithHandle` | a `Subscription` over `RemoteStream.subscribe` |
| forge's `ModelStore` and store bindings | `Fuaran.MVU.React.ModelStore` and `ModelStore.useSelector` |
| `ToolUp.Elmish.HMR` | nothing to write: every mount carries a hot-reload key |
| `ErrorContext.ofUpstreamShape`, `ErrorPhase.Termination` | read `ctx.Message` / `ctx.Exception` in your reporter; nothing reports termination |
| the `TOOLUP_REGEN_ELMISH_PROOF_CORPUS` switch, the `elmish-proof-corpus` regeneration stem | none: the runtime's evidence ships with `Fuaran.MVU` |

## Verification

1. `git grep -n "ToolUp.Elmish\|Cmd\."` over your client projects finds nothing.
2. `dotnet build` the solution, then compile the client with Fable (`dotnet fable -o output`).
3. Run your module tests: every `ModuleHarness` assertion now reads steps.
4. Exercise one remote call, one follow-up (`Then`) and one long-lived source in the browser; with
   `OnElmishError` set, a throwing `update` reports once and leaves the screen on the last good state.

## Rollback

Pin every `ToolUp.*` package back to the 0.42 line and revert the client edits. This change touches
no server or wire contract: the runtime move is confined to the client tier.
