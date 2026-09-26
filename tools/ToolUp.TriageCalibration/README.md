# ToolUp.TriageCalibration

Measures the Tier-3 fast-path **triage** tier against a case file: for every
case, whether the pre-filter would refuse it (and why), what one model call
answered, and what that one answer becomes at each of four confidence floors.
The output is a result file for resolve-rate vs false-fire analysis.

```bash
dotnet run --project tools/ToolUp.TriageCalibration -- \
    --corpus cases.json --out result.json --provider claude
```

| Argument | |
|---|---|
| `--corpus <file>` | the case file (schema below). Required. |
| `--out <file>` | where the result file is written (schema below). Required. |
| `--provider claude\|openai\|gemini` | the live connector to call. Its key is read from `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or `GEMINI_API_KEY`. Required. |
| `--model <id>` | build the connector at this model; every call is served on it. Without it, a connector that declares `Capabilities.TriageModelId` is asked for that model through the per-call override (Phase 661) — the same route the resolver takes when no separate triage provider is composed. |
| `--timeout-ms <n>` | per-call wall-clock budget. Default: the resolver's `DefaultTimeoutMs` (3000). |
| `--max-chars <n>` | the pre-filter's instruction-length ceiling. Default: the resolver's `DefaultMaxInstructionChars` (240). |

Exit `0` means the result file was written; `2` means the run could not start
(bad arguments, an unreadable case file, a provider that cannot be built). A
call that failed or timed out is a **row** in the result, never a non-zero
exit: the tool measures, it does not gate.

Shipped by Phase 663. `examples/cases.json` is a worked case file; the test
pack decodes it, so it stays in step with this page.

## What it runs, and what it does not

The tool calls the resolver's own public, pure stages and modifies none of
them: `isEligibleInstruction`, `buildTriagePrompt`, `triageSchema`,
`parseTriageDecision` and `planTriage` from
`ToolUp.AI.FastPathTriageResolver`. The provider call is shaped exactly as the
resolver shapes it — the same system prompt, the same schema, one attempt, and
the same wall-clock backstop (`Async.StartChild` with the budget), so a hung
connector is cut and recorded as `timeout` rather than blocking the run.

For each case:

1. **Pre-filter (free).** `isEligibleInstruction` decides. A refused case makes
   no call; its latency is recorded under the `pre-filter` stage. An eligible
   instruction over a snapshot with no fields is also refused here — the
   resolver does not attempt triage on an empty surface.
2. **One call (not free).** An eligible case makes exactly ONE structured call.
   The raw content is cached in the result. Latency is recorded under the
   `model-call` stage.
3. **Four floors, zero further calls.** The one parsed answer is re-run through
   `planTriage` at each floor in `{0.70, 0.78, 0.85, 0.92}`. The sweep costs
   one pass, not four.

**The eligibility reason is derived, the verdict is not.** `isEligibleInstruction`
returns a verdict with no reason, so the tool names the rule by elimination over
the pre-filter's documented rules, in the resolver's order. If the resolver
ever gains a rule this tool does not know, the refusal is reported as
`unclassified` — never attributed to the wrong rule.

## The case file — `toolup.triage-calibration.cases/1`

```json
{
  "schema": "toolup.triage-calibration.cases/1",
  "cases": [
    {
      "id": "set-country-alias",
      "instruction": "set country to britain",
      "snapshot": { "moduleId": "sales", "page": "overview", "stateSummary": "country=FR" },
      "fields": [
        {
          "fieldId": "country",
          "description": "the country filter applied to the dataset",
          "valueType": "enum",
          "instructionPatterns": [ "set country to {value}" ],
          "valueAliases": { "britain": "UK" }
        }
      ],
      "expected": { "decision": "set_field", "fieldId": "country", "value": "UK" }
    }
  ]
}
```

| Member | Type | Meaning |
|---|---|---|
| `schema` | string | must be `toolup.triage-calibration.cases/1`; any other id is refused. |
| `cases[].id` | string | unique within the file. |
| `cases[].instruction` | string | the user's instruction, verbatim. |
| `cases[].snapshot.moduleId` | string | the active module (required). |
| `cases[].snapshot.page` | string \| null | the active page; absent or `null` for a single-page module. |
| `cases[].snapshot.stateSummary` | string | the state line the prompt carries; absent means `""`. |
| `cases[].fields[]` | array | the declared fields — an `AIFieldDescriptor` each. |
| `fields[].fieldId` | string | required. |
| `fields[].valueType` | string | required. A type ending in `-option` is clearable. |
| `fields[].description` | string | optional, default `""`. |
| `fields[].instructionPatterns` | string[] | optional. |
| `fields[].valueAliases` | object | optional; `{ "alias": "canonical" }`. |
| `cases[].expected.decision` | string | `set_field` or `needs_full_agent`. |
| `cases[].expected.fieldId` | string | the field a `set_field` should name. |
| `cases[].expected.value` | string \| null | the CANONICAL value (after alias folding); `null` is a clear. |

A malformed file is refused as a whole with the offending case named; nothing
is called.

## The result file — `toolup.triage-calibration.result/1`

**This schema is a wire contract.** Under `/1` members are only ever *added*;
a reader must ignore members it does not know. A rename, a removal or a change
of meaning is a new schema id. Member order in the written file is fixed.

```json
{
  "schema": "toolup.triage-calibration.result/1",
  "corpusSha256": "<64 lowercase hex over the case file's bytes>",
  "provider": { "name": "…", "configuredModel": "…", "supportsTriage": true, "requestedModel": "…" | null },
  "settings": { "floors": [0.7, 0.78, 0.85, 0.92], "maxInstructionChars": 240, "timeoutMs": 3000 },
  "cases": [ /* one row per case, in file order */ ],
  "summary": { /* below */ }
}
```

### A case row

| Member | Meaning |
|---|---|
| `id`, `instruction`, `expected` | echoed from the case file. |
| `eligibility.eligible` | the resolver's pre-filter verdict. |
| `eligibility.reason` | `eligible`, `empty`, `too-long`, `question-mark`, `question-opener`, `no-declared-fields`, or `unclassified`. |
| `latency.stage` | `pre-filter` (refused locally, free) or `model-call` (cost a call). |
| `latency.ms` | wall-clock milliseconds for that stage. |
| `call` | `null` for a refused case; otherwise the one call, below. |
| `floors[]` | one verdict per floor for a case that reached the model; empty otherwise. |

`call`:

| Member | Meaning |
|---|---|
| `outcome` | `answered`, or the resolver's own token: `unparseable`, `provider-error`, `timeout`. |
| `route` | `configured`, `override` or `override-fallback` (the `ModelOverrideOutcome` vocabulary). For a timeout or an error the route is the one that was attempted. |
| `servedModel` | the model that served the call (for a timeout or error, the configured one). |
| `rawContent` | the provider's content verbatim, or `null` when the call returned nothing. |
| `error` | the failure text for `provider-error` / `timeout`, else `null`. |
| `decision` | the parsed answer (`decision`, `fieldId`, `value`, `confidence`, `reason`), or `null`. |
| `inputTokens`, `outputTokens` | provider-reported usage, or `null`. |

`floors[]`:

| Member | Meaning |
|---|---|
| `floor` | the confidence floor. |
| `outcome` | `hit`, or the resolver's fall-through token (`needs-full-agent`, `low-confidence`, `unknown-field`, `clear-unsupported`); when the call produced no answer, the call's own outcome (`unparseable`, `provider-error`, `timeout`) at every floor. |
| `fieldId`, `value` | what a `hit` would set (the value after alias folding; `null` is a clear). |
| `score` | against `expected`: `correct-resolve`, `wrong-resolve` (a hit on the wrong field or value), `false-fire` (a hit where `needs_full_agent` was expected), `missed` (a fall-through where `set_field` was expected), `correct-decline`. |

Values compare trimmed and case-insensitively; an empty value equals `null`.

### The summary

| Member | Meaning |
|---|---|
| `cases`, `ineligible`, `modelCalls` | counts. |
| `answered`, `unparseable`, `providerErrors`, `timeouts` | the model calls, by outcome. |
| `preFilterMsTotal` | total latency of the refused cases (free). |
| `modelCallMsTotal`, `modelCallMsP50`, `modelCallMsP95` | model-call latency (nearest-rank percentiles; `null` with no calls). |
| `floors[]` | per floor: `hits`, `correctResolve`, `wrongResolve`, `falseFire`, `missed`, `correctDecline`, `resolveRate`, `falseFireRate`. |

Per-floor counts cover **every** case. A refused case never reaches a floor; in
production it falls through, so each floor counts it as `missed` (if a set was
expected) or `correct-decline`.

- `resolveRate` = `correctResolve` / cases whose `expected.decision` is `set_field` (`null` when there are none).
- `falseFireRate` = (`falseFire` + `wrongResolve`) / `hits` — the share of hits that would have changed the screen wrongly (`null` with no hits).

The raw counts are there so any other ratio can be derived downstream.

## Tests

`tools/ToolUp.TriageCalibration.Tests` (a `VerifyAll` pack) drives the plumbing
with a fake provider — the eligibility short-circuit, one call re-scored at four
floors, the timeout / provider-error / unparseable strata, and the result-file
round-trip. No network, no key. A real calibration needs a live provider and is
run by hand.
