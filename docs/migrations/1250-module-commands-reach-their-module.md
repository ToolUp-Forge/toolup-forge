# forge#1250: a module's commands reach that module

**What changes.** The shell's `ModuleMsg of obj` becomes `ModuleMsg of moduleId: string * message: obj`. A command from a
module's `Init` or `Update`, and a dispatch from its view, now names that module and goes to it whichever module is
active. Module code does not change: a module's `Cmd` and its view's `dispatch` have the same types as before.

**A link can publish in either order.** Publish then navigate now opens the target: the subscriber's read used to be
discarded, and the page stayed on "Loading…". Navigate then publish still works, so a consumer that reordered its link
to get round this may leave it or put it back.

**The change, for code that names `ModuleMsg`.** Only a host or a test that constructs or matches the shell's `Msg` does.

```diff
-dispatch (Client.ModuleMsg(box Increment))
+dispatch (Client.ModuleMsg(moduleId, box Increment))
```

```diff
-| Client.ModuleMsg payload -> …
+| Client.ModuleMsg(moduleId, payload) -> …
```

**What behaves differently at runtime.**

- A module that subscribes to an event, or takes a server action, while inactive now runs the commands its `Init` and
  `Update` return. One that relied on them being dropped will now make those calls. Before this change its `Init` ran
  and its boot calls were thrown away, so check any `Init` that does expensive work for a subscriber readers rarely open.
- A read answered after its module is left updates that module. It used to reach the active module's `Update`, which
  under Fable reads a message by its tag and fields and so could take it for one of its own.
- A message for a module with no state is dropped. It used to fail on `Map.find` when the active module had no state. A
  team switch or a reset clears a module's state, and an answer still in flight then has nothing to land on.
- A module the route guard refuses gets no event and no action, so its `Init` does not run and its boot calls do not come
  back as 403s. Before this change its `Init` and `Update` ran with their commands dropped. A role or access response not
  yet loaded reads as refused, as it does for the sidebar, so an event published in that window does not reach a gated
  module; opening the module once the response lands initialises it as usual.

**Verify.** `dotnet build`, then a link from another module that publishes and navigates: the request goes out and the
table opens.

**Rollback.** Pin the previous SDK version.
