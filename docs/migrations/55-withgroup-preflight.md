# Phase 55 — `withGroup` preflight validator (consumer migration)

**What changes.** `Client.boot` now refuses to start when every consumer-supplied `ClientModule` in the deployment has `Group = None` (no `|> ClientModule.withGroup "<name>"` call anywhere in the module list). The validator runs once at startup alongside `validateHandlers` / `validateRequestSeam` and throws a clear `failwithf` naming the first ungrouped module.

**Why.** The sidebar's reserved `_other` bucket renders title-less and starts collapsed when no other declared group is present. A deployment whose every user module is ungrouped therefore boots with an empty-looking sidebar — even though every module is registered, accessible, and wired correctly. A single-module bring-up hit this; documentation alone proved insufficient.

**Scope.** Client-side only. `ClientModule.Group` is a client-tier concept with no server-side projection, so the check lives in `src/ToolUp.Platform.Client/Client/ModuleGroupingValidator.fs` and is invoked from `Client.boot`. No server-side `IConfigValidator` ships for this concern.

## Who's affected

A consumer is affected if **every** module in its `Client.run config modules` list is constructed without `|> ClientModule.withGroup "<name>"`. A single `withGroup` anywhere in the list satisfies the check. A multi-module deployment that already groups at least one module is **N-A**; the affected case is a single-module (or wholly ungrouped) deployment.

## Diff to apply (single ungrouped module — representative)

```fsharp
// Before — single ungrouped module, sidebar collapses to title-less _other:
let register () : ErasedModule =
    ClientModule.create { Init = init; Update = update; Name = "Convert"; Icon = Icons.upload }
    |> ClientModule.withView (fun model dispatch -> view model dispatch)
    |> ClientModule.register

// After — assigns the module to a declared group:
let register () : ErasedModule =
    ClientModule.create { Init = init; Update = update; Name = "Convert"; Icon = Icons.upload }
    |> ClientModule.withView (fun model dispatch -> view model dispatch)
    |> ClientModule.withGroup "Workflow"
    |> ClientModule.register
```

Pick any group name that fits the deployment's information architecture — modules sharing a group name render together under a collapsible sidebar header. A single-module deployment can use anything (`"Main"`, the app name, the workflow name); the name is just the section label.

## Verification

1. `dotnet build` clean on the consumer side.
2. Boot the consumer; confirm the deployment reaches the shell render (the validator no longer throws at startup).
3. Confirm the sidebar renders the registered module under its declared group header — no empty-sidebar regression.

## Rollback

The validator is the only change; reverting `Client.boot`'s call site removes it. The consumer's `withGroup` call is otherwise harmless and should stay even after a rollback — it's the correct sidebar shape regardless of whether the validator is in place.

## Consumers

The migration is **N-A** for any deployment that already groups at least one module; only single-module or wholly ungrouped deployments need the one-line `withGroup` addition.
