# Phase 294 — Composition invariant rule-manifest (well-formedness as data)

**Status:** additive. No consumer action required; runtime preflight behaviour is byte-identical to
pre-294.

## What changes

[Phase 281](281-composition-preflight.md)'s `CompositionValidator` rule set is now exposed as
introspectable data: `CompositionValidator.ruleManifest : CompositionRuleDescriptor list`. Each
descriptor carries a stable `Code`, a `Severity`, and a `Description` — everything an external
pre-build checker needs to validate a composition against **forge's own rules** without re-encoding
them.

The manifest is projected from the same internal `rules` list the runtime preflight check runs
(`CompositionValidator.checkWith`). Because both readers derive from one definition, a rule cannot
appear in the runtime check without appearing in the manifest, or vice versa — one source of truth
for "well-formed forge app," checkable at authoring time (by the external tool) and at compose time
(by forge), with no drift.

Shipped rule codes (Phase 281):

| Code | Severity |
|---|---|
| `duplicate-component-id` | Error |
| `companion-slot-legality` | Error |
| `orphaned-tool-reference` | Error |

## Behaviour / compatibility

- **GP 11 — runtime unchanged.** `ruleManifest` is a pure read accessor. The compose-time preflight
  is byte-identical to Phase 281; nothing new runs at startup.
- **GP 1 — generic substrate.** The descriptor carries no vendor / domain / tree-language type — just
  a code string, a severity, and a description.

## Consuming the manifest

```fsharp
open ToolUp.Platform

CompositionValidator.ruleManifest
|> List.iter (fun r -> printfn "%s (%A): %s" r.Code r.Severity r.Description)
```

An external pre-build checker enumerates `ruleManifest` to learn which structural invariants forge
enforces, then validates a candidate composition against the same codes forge will check at compose
time — so a defect is caught at authoring time with the identical vocabulary forge reports at boot.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `ToolUp.Platform.Tests` — `InvariantRuleManifest` pack: every shipped rule appears in the manifest
  with a stable, unique code; each descriptor carries a non-empty description; the runtime check and
  the manifest enumerate the same rule codes (bijection — a rule added to one without a matching
  fixture fails the test).

## Rollback

Remove the `ruleManifest` accessor + the `CompositionRuleDescriptor` type. The runtime check is
untouched.
