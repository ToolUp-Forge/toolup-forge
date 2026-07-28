# Composition rule errata

A published correction channel for the composition well-formedness rules the SDK exports as data.

`composition-rule-errata.json` enumerates errata against a **rule code** at a **range of rule
versions**. It is data, not code: an external checker holding a stamped preflight result — one that
records which rule versions it was proven under — reads this file and answers, mechanically,
"was that conclusion drawn under a rule version now known to be wrong?".

An **empty document (`[]`) means no correction has been published**, and a **missing document is
read the same way**. Absence is never a load failure: a checker must not fail shut because nobody
has found anything wrong yet.

## Format

A JSON array of objects:

```json
[
  {
    "ErratumId": "ERR-2026-001",
    "RuleCode": "orphaned-tool-reference",
    "AffectedFrom": "1.0.0",
    "AffectedTo": "1.0.2",
    "Description": "The rule exempted every '_'-prefixed SourceModule, including consumer modules that happen to start with an underscore, so an orphaned reference in such a module was not reported.",
    "Disposition": "corrected-in:1.0.3"
  }
]
```

| Field | Meaning |
|---|---|
| `ErratumId` | Stable, citable id. External records reference it, so it never changes once published. |
| `RuleCode` | The rule this erratum is against — the same stable code the rule manifest publishes. |
| `AffectedFrom` / `AffectedTo` | The affected rule-version range, **inclusive**, as `major.minor.patch`. |
| `Description` | What the rule did, and what it should have done — in the terms a reader of an affected conclusion needs. |
| `Disposition` | `corrected-in:<major.minor.patch>` or `withdrawn`. |

A malformed entry is a load **error** naming the offending `ErratumId` — never a silently-dropped
row. A correction channel that loses corrections is worse than none, because it reads as a clean
bill of health.

## Rule versions and the bump discipline

Every exported rule carries a semantic version. The bump states what a change does to a conclusion
drawn under the previous version:

- **patch** — the message or implementation changed; the same compositions pass and fail. A prior
  conclusion stands.
- **minor** — the rule **tightened**: strictly more compositions now fail. A prior *pass* is no
  longer evidence of a pass today; a prior *fail* still fails.
- **major** — the rule's **meaning** changed (a different check, or a loosening). Neither a prior
  pass nor a prior fail carries over.

The manifest as a whole also carries a version, which moves when the rule *set* moves: minor when a
rule is added, major when one is removed or renamed.

## Consuming this from F#

```fsharp
open ToolUp.Platform

// What the current build's rules are, and what a check was proven under.
let manifest = CompositionRuleVersions.toWire CompositionRuleVersions.allRules
let result = CompositionRuleVersions.checkStamped references compositionManifest

// Is a stamped conclusion affected by a published erratum?
match RuleErrata.tryLoad "rule-errata/composition-rule-errata.json" with
| Ok errata ->
    match RuleErrata.against errata result.StampedUnder with
    | [] -> () // unaffected
    | impacts -> printfn "%s" (RuleErrata.render impacts)
| Error why -> eprintfn "errata document unreadable: %s" why
```

`CompositionRuleVersions.driftSince` answers the neighbouring question against the *current* build:
which rules have moved since a result was stamped, and whether the move was a tightening.
