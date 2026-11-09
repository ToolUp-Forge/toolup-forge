# 1070 — One async state, one notice, and the memo primitives

## What changes

- **`Remote<'T,'E>`** (`ToolUp.Platform`, in `ToolUp.Platform.UI`): `NotAsked | Loading | Reloading of 'T |
  Loaded of 'T | Failed of 'E`, with `Remote.ofResult / map / mapError / bind / toOption / withDefault /
  isLoading / isSettled / reload` (`reload` keeps the answer on screen as `Reloading`). It is the one state a
  client module holds for a read; the SDK's admin modules' private `LoadState` copies are gone.
- **`Notice`** (`Info of string | Problem of string`) with `StateViews.notice msgs notice onDismiss` (dismissible),
  `StateViews.noticeStatic notice` and `StateViews.remote state describe loading render`. The caller-less
  `StateViews.errorBanner` is removed; strings come from the caller's catalog (`NoticeMessages.Dismiss`).
- **`Memo.oneEntryBy` / `Memo.byRef` / `Memo.byRef2`** (`ToolUp.Elmish`): one-entry memos keyed by reference.
- **`Program.withStableDispatch build view`** sets a view that receives a wrapper set built once per loop from
  the loop's dispatcher. `Client.programOver config init update build view` is the shell's builder (reporter,
  trace, stable dispatch) for any program that hosts the shell.
- **The shell views take `Client.Dispatchers`** instead of `Msg -> unit`: `Client.view`, `Client.viewWithSignIn`
  and `Client.viewSlicedOver`. Build the set with `Client.dispatchers` through `Program.withStableDispatch`
  (or `Client.programOver`), never per render.
- **`Model.ConfigsPrefetch` / `FlagsPrefetch`** are `Remote<unit, string>` (were `Prefetch<unit>`).
- **Removed:** `Prefetch.warnIfStalled`, `Prefetch.setStallReporter`, `Prefetch.resetStallWarning` (no caller in
  the estate). `Prefetch<'a>` itself stays.
- The cross-tab identity watch is the `identity-across-tabs` program-lifetime effect, so a hot reload disposes it.

## Per file

A composer that renders the shell from its own program:

```diff
-let prog = Program.mkProgram init update (fun model dispatch ->
-    Client.viewWithSignIn config modules chrome model.Shell (ShellMsg >> dispatch))
+let prog =
+    Client.programOver config init update
+        (fun dispatch -> Client.dispatchers (ShellMsg >> dispatch))
+        (fun model shell -> Client.viewWithSignIn config modules chrome model.Shell shell)
```

A one-off render (a prerender, a test) passes `Client.dispatchers ignore`.

A model or test literal that sets the boot gates:

```diff
-    ConfigsPrefetch = Prefetch.none
-    FlagsPrefetch = Prefetch.none
+    ConfigsPrefetch = Remote.Loading
+    FlagsPrefetch = Remote.Loading
```

`Prefetch.isComplete gate` on those fields becomes `Remote.isSettled gate`. A call to `Prefetch.warnIfStalled`
is deleted. A module with its own `LoadState` copy may adopt `Remote` when it likes; nothing requires it.

The codemod reports the removed members: `dotnet run --project Build.fsproj -- Codemod --phase 1070`
(rule `1070-prefetch-stall`).

## Verify

`dotnet build` of the consumer is the gate: the view signatures and the gate fields fail to compile until moved.
A composer that built its dispatch per render still renders correctly, but its memo boundaries do not hold.

## Rollback

Pin the previous SDK version. There is no runtime switch.
