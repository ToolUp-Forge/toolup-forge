# Reactive recompute runs under the carried scope

**Ships in:** ToolUp.Facts.Server (`ReactiveDataChange`, `ReactiveDataObjectStore`, `DataChangeScope`,
`RecomputeJobHandler`), ToolUp.Platform.Server (`JobApiHandler` now schedules through the typed
`IJobScheduler.Schedule(scope, registration)`).

## What changes

Phase 818 gave jobs a carried mint. Phase 930 carries the scope one step further back: a data-object
write made on a request that resolved its own shard now hands the recompute the resolver's
`ResolvedScope` itself, never a scope rebuilt from a string (Phase 797's rule). A reaction receives a
`DataChangeScope`:

- `Resolved scope`: the write was made under a resolved scope whose shard matches the write's.
  The recompute is scheduled and run through the typed overloads.
- `Carried scopeId`: every other write (off the request path, into another shard, or through a
  scheduler that cannot re-create a scope). This is the string the reaction always received, handled
  exactly as before.

A composed deployment needs no change: `FactsCompose` wires the new scope source for you.

## Who must change

Only code that builds the reactive decorator by hand, or implements a reaction.

```diff
- let reaction: FactDataChangeReaction = fun scopeId dataTypes -> ...
+ let reaction: FactDataChangeReaction = fun scope dataTypes ->
+     // scope.ScopeId is the string you had before
+     ...

- ReactiveDataChange.decorate inner armed react logger
+ ReactiveDataChange.decorate inner armed currentScope react logger
```

`currentScope: unit -> ResolvedScope option` names where the request's resolved scope comes from.
In an ASP.NET host, use `ReactiveDataChange.requestScope (fun () -> Some accessor)`, where
`accessor` is the host's `IHttpContextAccessor`. Pass `fun () -> None` to keep string-only behaviour. The same argument is new on the `ReactiveDataObjectStore` constructor.

## Verify

Build, then run the Platform pack's Phase 797 choke-point list and the reactive recompute lists. A
reaction that reads `scope.ScopeId` sees exactly the string it saw before.

## Rollback

Pass `fun () -> None` as `currentScope`: every change then arrives as `Carried`, which is the
pre-930 behaviour.
