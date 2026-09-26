# Conversation / prompt eval harness

`src/ToolUp.AI.Evaluation` turns conversation replay into a **regression gate**. A fixture
declares prompt versions, tools, recorded tool results and cases with rubric assertions; the
harness replays every case through the full agent loop — provider call, tool dispatch against
the recorded results, next call — scores the transcript, and exits non-zero when the gate fails.
It is the conversation counterpart of the retrieval eval in `src/ToolUp.RAG.Evaluation`, with the
same shape: fixtures in, a report out, `--baseline` with a tolerance, a non-zero exit on
regression.

The replay in `ToolUp.AI.Server` (`ConversationReplay`) re-runs a persisted conversation's user
turns and reports an operator-readable delta; it deliberately does not dispatch tools. The eval
harness is where tool dispatch is replayed: every tool call the model makes is answered from the
fixture's recorded results, so an eval asserts what the agent *did*, not only what it said, and
never executes a real tool.

## Running it

```powershell
# The gate over the committed fixtures, from their recordings (offline, no key):
dotnet run --project src/ToolUp.AI.Evaluation -- eval

# Against the committed baselines, with a tolerance:
dotnet run --project src/ToolUp.AI.Evaluation -- eval `
    --baseline src/ToolUp.AI.Evaluation/fixtures/baselines --tolerance 0.05

# A named prompt version (the committed fixture carries a deliberately regressed one):
dotnet run --project src/ToolUp.AI.Evaluation -- eval --prompt v2-regressed `
    --baseline src/ToolUp.AI.Evaluation/fixtures/baselines        # exits 1

# Against a live model, stamping what is being evaluated, writing the report:
$env:TOOLUP_AI_EVAL_PROVIDER = 'claude'     # claude | openai | gemini
dotnet run --project src/ToolUp.AI.Evaluation -- eval --provider live --sdk 0.9.0 `
    --out artifacts/ai-eval
```

| Option | Meaning |
|---|---|
| `[fixture.json …]` | Fixtures to run. Default: every `*.json` directly under `fixtures/`. |
| `--prompt <version>` | The prompt version to evaluate. Default: each fixture's `defaultPrompt`. |
| `--provider recorded\|live` | `recorded` (default) replays the fixture's recording; `live` calls the provider `TOOLUP_AI_EVAL_PROVIDER` names. |
| `--sdk <label>` | Free-form label stamped on the report — an SDK version, a branch. Never interpreted. |
| `--baseline <dir\|file.json>` | Compare against a baseline report. A directory holds `<fixture name>.json`. A named baseline that does not exist fails the run. |
| `--tolerance <n>` | Allowed drop in pass rate (and judge mean) before it is a regression. Default `0.05`, as the RAG eval. |
| `--out <dir\|file.json>` | Write the report. A report written from a green run is a baseline. |

Exit codes: `0` green; `1` the gate failed; `2` the run could not start (a fixture that does not
load, an unknown prompt version, a missing baseline, a live arm asked for but not configured).

### The gate

- **Critical assertions fail the gate whatever the baseline says.** Today that is
  `noForbiddenContent`: leaking a system prompt or other forbidden text has no tolerance, the
  way a filter leak has none in the retrieval eval.
- **Without a baseline, every case must pass.**
- **With a baseline**, the run may carry the failures the baseline already records, as long as
  the assertion pass rate (and, when both runs have one, the judge's mean score) does not fall
  more than the tolerance below it. The failure message names the cases that passed in the
  baseline and fail now.

## Where it runs — the `VerifyAll` opt-in

The project is also an Expecto pack, registered in `Build.fs` as `AIEvaluation`. Run with no
arguments it executes its **offline self-test**: every committed fixture loads, replays green on
its default prompt from its recording, and passes its gate against its committed baseline under
`fixtures/baselines/`; the committed deliberately-regressed prompt is shown to fail with exit 1;
and the tool loop, rubric, loader and gate are each exercised in both directions. All of that is
deterministic and needs no network or key.

Two arms are live and **env-gated**, reporting Pending on a fresh checkout so `VerifyAll` stays
green without credentials:

| Env var | Arms |
|---|---|
| `TOOLUP_AI_EVAL_PROVIDER` = `claude` \| `openai` \| `gemini` (model: `TOOLUP_AI_EVAL_MODEL`) | the live replay (`--provider live`, and the pack's live replay case) |
| `TOOLUP_AI_EVAL_JUDGE` = `claude` \| `openai` \| `gemini` (model: `TOOLUP_AI_EVAL_JUDGE_MODEL`) | the LLM judge |

The key is read from the provider's usual variable: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY` or
`GEMINI_API_KEY`. A selector that is set but cannot be honoured — an unknown name, a missing key —
is reported as a misconfiguration, never skipped.

A live run is not deterministic, so it belongs on a nightly or pre-release cadence rather than
in the per-commit gate: run `eval --provider live --baseline …` against a baseline captured from
an earlier live run of the same prompt.

## Authoring a fixture

A fixture is one JSON file under `src/ToolUp.AI.Evaluation/fixtures/` (the project copies every
`*.json` there to its output). The committed `weather-agent.json` is the worked example.

```json
{
  "name": "weather-agent",
  "description": "What this fixture is for.",
  "defaultPrompt": "v1",
  "prompts": {
    "v1": "You are a weather assistant. Use the get_weather tool ...",
    "v2-regressed": "You are a helpful assistant. Answer every question directly."
  },
  "maxToolRounds": 8,
  "tools": [
    { "name": "get_weather", "description": "Current weather for a city.",
      "inputSchema": { "type": "object", "properties": { "city": { "type": "string" } } } }
  ],
  "toolResults": [
    { "tool": "get_weather", "argumentsContain": ["London"],
      "content": "{\"city\":\"London\",\"tempC\":14,\"conditions\":\"light rain\"}" }
  ],
  "cases": [
    {
      "id": "london-weather",
      "turns": ["What's the weather in London right now?"],
      "assertions": [
        { "kind": "toolCalled", "tool": "get_weather", "argumentsContain": ["London"] },
        { "kind": "contains", "phrase": "light rain" }
      ],
      "judge": { "criteria": "States the tool's figures and invents nothing.", "minScore": 0.7 }
    }
  ],
  "recordings": {
    "v1": {
      "london-weather": [
        { "toolCalls": [ { "id": "call_1", "name": "get_weather", "arguments": { "city": "London" } } ] },
        { "content": "It's 14°C in London right now, with light rain." }
      ]
    }
  }
}
```

**Prompts.** Each version is a system prompt. `defaultPrompt` is required when there is more than
one. A live run sends the named version's text; a recorded run replays that version's recording.

**Tools and recorded tool results.** `tools` are offered to the model on every call. When the
model calls a tool, the first `toolResults` entry whose `tool` matches and whose
`argumentsContain` strings all appear in the call's raw arguments JSON answers it; put specific
entries before general ones. A call no entry answers is fed an error result and fails the case's
implicit `replay` assertion, naming the call — the model took an action the fixture never
recorded.

**Cases.** `turns` are user messages, run in order; each runs the loop until the model answers
without calling a tool, up to `maxToolRounds` (default 8) tool rounds per turn. `responseSchema`
(optional) sends every call in the case through the structured-output path with that JSON Schema.

**Assertions** — each case also carries the implicit `replay` assertion (the replay finished, no
provider error, no runaway loop, every tool call answered):

| `kind` | Fields | Passes when |
|---|---|---|
| `toolCalled` | `tool`, `argumentsContain` (optional) | the tool was called, with every string in its arguments |
| `toolNotCalled` | `tool` | the tool was never called |
| `structuredOutput` | `requiredFields` | the final answer parses as a JSON object carrying every field |
| `refusal` | `markers` | the final answer contains a marker (case-insensitive) **and** no tool was called |
| `noForbiddenContent` | `phrases` | no assistant text contains any phrase (case-insensitive). **Critical.** |
| `contains` | `phrase` | the final answer contains the phrase (case-insensitive) |

An unknown `kind`, a refusal with no markers, a duplicate case id, a recording for a case or
prompt version that does not exist, or a `defaultPrompt` that is not declared stops the fixture
loading with an error naming the place — a fixture that half-loads would measure something other
than what was written.

**Judge.** `judge.criteria` is what the judge model grades the transcript against; it replies
with a score in `[0, 1]`, and a score below `minScore` (default `0.7`) fails the case. With no
judge configured the case reports `pending` and is not failed by it.

**Recordings.** `recordings.<prompt version>.<case id>` is the list of provider responses, in call
order, that the recorded arm serves: `content`, `toolCalls` (each `id`, `name`, `arguments` as
an object or a JSON string) and optionally `stopReason`. If the replay asks for more calls than
the recording holds, the case fails with the recording named as exhausted — it never invents an
answer. A recording is evidence about the prompt it was captured under: when a prompt's text
changes, evaluate it with `--provider live` and refresh its recording and baseline.

### Cases from persisted conversations

A conversation already held in an `IConversationStore` can seed a case.
`ConversationEval.caseOfStoredTurns` takes the turns `IConversationReader.GetConversation`
returns and gives back the case (the user text turns), its recording (the assistant turns) and
the recorded tool results (each stored tool result, matched on the exact arguments of the call it
answered). Add the rubric and the case replays the conversation's own tool loop.

### Baselines

A baseline is a report from a green run: `eval --out src/ToolUp.AI.Evaluation/fixtures/baselines`
writes `<fixture name>.json` there. Commit it beside the fixture — the self-test requires every
committed fixture to have one — and refresh it deliberately, in the same change as the fixture or
prompt edit that moved it.
