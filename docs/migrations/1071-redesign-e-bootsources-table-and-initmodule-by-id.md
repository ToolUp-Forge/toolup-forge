# forge#1071: one boot-source table and one module host

**What changes.** The client shell's `Model` keeps one `ModuleHost` per initialised module in
`Hosts : Map<string, ModuleHost>` (`{ State; Generation }`), replacing `ModuleStates` and
`ResetCounters`. The two first-init gate fields `ConfigsPrefetch` / `FlagsPrefetch` are one set,
`AwaitingFirstInit`. The boot sources are one table, `BootSources.all`: the shell's boot loads,
its team / locale / sign-in resets, the banner's Retry and its labels are filters over it. Module
code does not change; only a host or a test that builds or reads the shell's `Model` does.

**The codemod.** `dotnet run --project Build.fsproj -- Codemod --phase 1071` rewrites
`model.ModuleStates |> Map.tryFind id` to `model |> Client.moduleState id` and a literal's
`ConfigsPrefetch = …` to `AwaitingFirstInit = Client.firstInitGates`, and reports every other
`ModuleStates`, `FlagsPrefetch`, `ResetCounters` and id-less `ModuleMsg` site.

```diff
-let state = model.ModuleStates |> Map.tryFind moduleId
+let state = model |> Client.moduleState moduleId
```

```diff
 let model: Client.Model = {
-    ModuleStates = Map.ofList [ "m", box initial ]
+    Hosts = Map.ofList [ "m", { State = box initial; Generation = 0 } ]
     …
-    ConfigsPrefetch = Remote.Loading
-    FlagsPrefetch = Remote.Loading
-    ResetCounters = Map.empty
+    AwaitingFirstInit = Client.firstInitGates
 }
```

`Client.observeModuleStates` takes the prior `Hosts` (was the prior `ModuleStates`).

**What behaves differently at runtime.**

- Every module `Init` goes through one function. It is deferred while the shell waits for the boot
  configs and flags, and refused when the navigation decision refuses the module. `ResetModule`
  now passes the same guard, so a reset of a module the caller may no longer reach does not
  re-initialise it.
- An `Init` that throws, on any path, leaves the module with no host and is reported through
  `ClientConfig.OnError` from a command. Before, only `ResetModule` caught it; elsewhere it threw
  out of `update`.
- An action or event for a module with no host that arrives while the shell is still waiting for
  the boot configs and flags is dropped. Before, the module was initialised and then cleared when
  the boot loads answered.
- A failed boot config or flag load releases the first init without clearing values a previous
  load brought.
- The sole-team auto-select reports a refusal with the server's reason, not an F# `%A` dump.

**Verify.** `dotnet build`, then sign in, switch team and change language: the active module
re-initialises once each time, and the degradation banner's Retry re-issues a failed load.

**Rollback.** Pin the previous SDK version.
