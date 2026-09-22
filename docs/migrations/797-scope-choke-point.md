# Scope as a choke point — `ResolvedScope` on the fact store, the disclosure gate and the fact doors

**Ships in:** ToolUp.Platform.Core (`ToolUp.Platform.ResolvedScope`, `IFactDisclosureGate`),
ToolUp.Platform.Server (`StorageScopeResolver.ScopeResolution`, the scope-resolution middleware),
ToolUp.Facts.Server (`IFactStore`, `FactDisclosureGate`, the three fact AI tools).

## What changes

Every fact door used to read its storage scope as a **string** from the request items
(`ctx.Items["ToolUp.StorageScope"]`), falling back to the user id and then to the literal
`"anonymous"`, and the store keyed on whatever it was handed. The admissibility theorem over the
model's input (`proofs/ModelInput.fst`) therefore had to *assume* the scope on a disclosed fact
was the principal's, because nothing made it so.

Now the scope is a value only the platform's scope resolution can mint:

- **`ToolUp.Platform.ResolvedScope`** — a union with a **private representation**. Its one
  constructor (`ResolvedScope.ofStorageScope`) is `internal` to `ToolUp.Platform.Core` and reachable
  only from `ToolUp.Platform.Server`. The only public entry is `ResolvedScope.anonymous`, the explicit
  anonymous scope, which keys the `"anonymous"` shard like any other id — its own scope, never a
  wildcard. Accessors: `ScopeId`, `IsAnonymous`, `Storage`.
- **`StorageScopeResolver.ScopeResolution`** — the mint point. The scope-resolution middleware calls
  the internal `remember` exactly where it resolves the request's scope; a door calls the public
  `forRequest`, which returns that value or the anonymous scope. There is **no** user-id rung and
  **no** literal fallback, and the module deliberately runs no resolver of its own.
- **`IFactStore` and `IFactDisclosureGate` gain a `ResolvedScope` overload of every scoped member.**
  The string overloads stay, unchanged in semantics, as the platform-carried form (a job's persisted
  scope, an import, a coherence sweep, a knowledge-base dependency record) and as the one-release
  compatibility path for consumers on the old shape. An implementation keys both on the same shard:
  the typed form is exactly the string form over `scope.ScopeId`.
- **`FactQueryTool`, `PopulationQueryTool` and `CoverageTool`** take their scope from
  `ScopeResolution.forRequest`, call the store and the gate through the typed overloads, and their
  `executeWith` executors take a `ResolvedScope` where they took a `string`.
- `StorageScope` **moved files**, not namespaces: it now lives in `Shared/Types/ResolvedScope.fs`
  ahead of the gate's contract in the compile order. No consumer sees a difference.

## Diff to apply

**Custom `IFactStore` or `IFactDisclosureGate` implementations** must implement the new overloads.
Delegate to the string form over the shard key; annotate the string form's first parameter so
overload resolution is unambiguous:

```fsharp skip=fragment
// Before
interface IFactStore with
    member _.Get(scopeId, factId) = ...

// After
interface IFactStore with
    member _.Get(scopeId: string, factId: string) = ...
    member this.Get(scope: ResolvedScope, factId: string) = (this :> IFactStore).Get(scope.ScopeId, factId)
```

```fsharp skip=fragment
// Before
interface IFactDisclosureGate with
    member _.Check(scopeId, principal, surface, factIds) = ...

// After
interface IFactDisclosureGate with
    member _.Check(scopeId: string, principal: string, surface: FactEgressSurface, factIds: string list) = ...
    member this.Check(scope: ResolvedScope, principal: string, surface: FactEgressSurface, factIds: string list) =
        (this :> IFactDisclosureGate).Check(scope.ScopeId, principal, surface, factIds)
```

**Callers of the three tools' `executeWith`** (test harnesses, typically) pass a `ResolvedScope`. A
test pack inside the platform's own trust boundary mints one through the internal
`ScopeResolution.ofStorageScope`; a consumer test drives the registered `execute` with a request the
platform middleware has resolved, or accepts the anonymous scope.

**Callers of the string store / gate members** need no change. Nothing is marked `[<Obsolete>]` in
this release: the string form is not merely tolerated, it is the correct form for every caller whose
scope the platform carries rather than resolves from a request principal, and those callers exist
in this repository. The removal of the string form is gated on giving carried scopes their own typed
provenance — a later phase, not this one.

**A deployment that bypasses the platform's scope-resolution middleware** (its own middleware
writing `ctx.Items["ToolUp.StorageScope"]`) will find the fact tools reading the **anonymous** shard:
the planted record is not a resolved scope and is not honoured. This is visible (the tools return
what the anonymous shard holds, which is what was asserted anonymously) rather than silent, and it is
the point of the phase — wire the platform middleware, which is the one mint.

## Verification

- `dotnet build ToolUp.Forge.sln` — surfaces every custom implementer that must add the overloads.
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter "Phase 797"` — the choke-point pack:
  no public constructor on `ResolvedScope`; a planted `StorageScope` never reaches a door; a source
  guard over the three doors, go-red pinned.
- The Phase 559 / 703 re-denial tests (a leaky store's cross-scope member is denied at the gate)
  are unchanged and still green — belt and braces both hold.
- `proofs/check.ps1` is unaffected: the F\* model of the input side is unchanged, and its ladder now
  records scope resolution on the proved rung (`proofs/README.md`, `proofs.json`).

## Rollback

Revert the SDK version pin. Nothing persisted changes shape: the store keys on the same shard string
under both forms, the audit rows carry the same `ScopeId`, and a fact asserted through a
`ResolvedScope` is readable through the string form and vice versa.
