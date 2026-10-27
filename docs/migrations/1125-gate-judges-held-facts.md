# The disclosure gate judges facts the caller already holds

**Ships in:** ToolUp.Facts.Server 0.35.0 (`IHeldFactDisclosureGate`, `FactDisclosureGate.held`, `FactsCompose`,
`FactBrowseHandler`). Rides the 0.35.0 draft (untagged, publicly unpinned, breaking-class since Phase 1123):
the new interface and function are additions; `FactBrowseHandler.FactBrowseDeps.Gate` is retyped.

**Affected:** a consumer that builds `FactBrowseHandler.FactBrowseDeps` by hand (a test, or a host that mounts the
browse without `FactBrowseHandler.withFactBrowse`). A deployment that composes the browse through
`withFactBrowse` changes nothing. `IFactDisclosureGate` is unchanged, and so is every implementer of it.

## What changes

1. **A held entry beside the id form.** `IHeldFactDisclosureGate.CheckHeld(scope, principal, surface, factIds,
   held)` judges `factIds` exactly as `IFactDisclosureGate.Check(scope, …)` does — the same verdicts, the same
   audit rows, every facet — resolving each id from `held` instead of the store. An id with no held fact is denied
   `unknown-fact`. The caller vouches that every held fact was read from this scope's store, through that scope,
   during this request; it is for in-process doors only, never a remoting, peer or webhook path.
2. **The fact tier composes it.** `FactsCompose.withFactStore` registers `IHeldFactDisclosureGate` as
   `FactDisclosureGate.held` over the composed gate. The platform gate is its own held form; any other gate
   (a decorator) is judged through its id form, so it is never bypassed.
3. **The fact browse reads ride it.** `FactBrowseDeps.Gate` is now the held form, and each of the four reads hands
   the gate the facts it read. Measured warm, in-memory: a page of 10 rows 20 → 10 downloads, a 100-point chart
   200 → 100, a movers page 52 → 32, a fact with its chain 6 → 4.

## What to do

A hand-built `FactBrowseDeps` that stops compiling on `Gate` wraps its gate:

```fsharp skip=fragment
let deps: FactBrowseHandler.FactBrowseDeps = {
    Tables = tables
    Writer = Some writer
    Store = store
    Gate = FactDisclosureGate.held gate // new in 0.35.0: the held form of the gate
    Registry = Some registry
    Clock = fun () -> DateTime.UtcNow
}
```

An in-process door of your own that reads facts and then judges them may resolve `IHeldFactDisclosureGate`
and pass the facts it read; every other door keeps `IFactDisclosureGate`.

The posture: [`docs/platform/facts.md`](../platform/facts.md#the-gate-judges-facts-the-caller-already-holds-phase-1125).

## Verifying

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1125"
```

The disclosure case compares the held form's verdicts and audit rows with the id form's in the base, viewer-aware
and taint configurations; the browse case counts each read's downloads against the read alone.

## Rolling back

Hand the browse the id form: `Gate` set to `FactDisclosureGate.held` over a decorating gate reads every fact again
at the gate, exactly as before.
