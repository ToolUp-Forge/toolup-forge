# Phase 6j.B — Tier-3 fast-path triage (consumer migration)

**What changes.** Three things, and only the first is breaking.

1. `AIProviderCapabilities` gains two fields — `SupportsTriage: bool` and `TriageModelId: string option`. **Every construction site of that record must add them.** In-tree providers and test doubles are updated by this phase; an out-of-tree `IAIProvider` implementation that builds a capabilities record will stop compiling until it does the same.
2. A new opt-in seam, `IAIFieldRegistry` (`ToolUp.AI.Core`), through which the active module surface's settable fields reach the server.
3. A new opt-in composition helper, `AIServerApp.withFastPathTriage`, which turns the tier on.

**Nothing changes for a deployment that does not opt in.** No triage call, no event row, no metric emission; the agent loop reaches the provider exactly as it did before (GP 11 / GP 13). The only required action is the record widening in (1).

## What the tier does

An instruction that the client-side pattern tier missed — "set country to UK" — otherwise costs a full agent turn: 5–15 s and a frontier model's tokens. Tier 3 spends one cheap, tool-free, schema-constrained call (~500 ms) deciding whether the instruction maps onto exactly one *declared* field. On a hit it publishes a `Notification.ModuleAction(moduleId, "_ui.set-field", payloadJson)` — the existing action path, no new wire shape — appends a synthetic assistant recap so the next turn's history has no gap, and skips the loop. On anything else it falls through and the loop runs as usual.

**Fall-through is never an error to the user.** Unparseable output, an undeclared field id, confidence under the floor, a provider failure, a timeout: all resolve to "run the full agent loop", which is what would have happened anyway.

## Required: widen your capabilities record

```diff
 let capabilities: AIProviderCapabilities = {
     Streaming = true
     ToolUse = true
     Vision = false
     SupportsPromptCaching = false
+    SupportsTriage = false
+    TriageModelId = None
     ProviderName = "myvendor"
     Model = model
 }
```

`SupportsTriage = false` is the correct default and the value `AIProviderCapabilities.unknown` carries: the triage resolver treats it as a hard gate and never calls a provider that has not opted in.

Declare `true` only if your provider serves a small structured-output request cheaply and reliably. `TriageModelId` names the cheaper model your provider family would use for it — it is a **declaration a composition root reads**, not a dispatch instruction: `IAIProvider` has no per-call model override, so the resolver cannot re-point your provider at that id by itself.

## Optional: turn the tier on

Two pieces are needed, and both are yours to supply, because forge deliberately models neither the UI declaration surface nor the model routing:

```fsharp skip=fragment
// 1. Implement the seam over whatever your client-resident UI tier declares.
type MyFieldRegistry() =
    interface IAIFieldRegistry with
        member _.Describe(moduleId, page) = async {
            // Project your own declarations into the SDK's shape. Return
            // `None` for a surface you know nothing about — triage then
            // does not run for that turn.
            return None
        }

// 2. Compose. `withTriageProvider` is where a cheap model goes: read the
//    primary provider's `Capabilities.TriageModelId`, build a second
//    instance at that model, and pass it. Omit it and triage runs on the
//    turn's own provider — correct, but paying the frontier price for the
//    decision.
app
|> AIServerApp.withFastPathTriage (
    FastPathTriageConfig.create (MyFieldRegistry() :> IAIFieldRegistry)
    |> FastPathTriageConfig.withTriageProvider cheapProvider)
```

`FastPathTriageConfig` also carries `ConfidenceFloor` (default 0.85), `MaxInstructionChars` (240) and `TimeoutMs` (3000). The floor is high on purpose: a wrong hit changes the user's screen incorrectly *and* hides their request from the agent that could have handled it properly, whereas a wrong miss costs one cheap call.

## What you should measure before opting in

Triage trades a small per-instruction token cost for latency, and whether that trade pays depends on a number the SDK cannot compute for you: the share of your instruction traffic the declarative pattern tier already catches. `/dev/ai-fastpath` reports the answer for both tiers on one page — `TriageAttempts`, `TriageHits`, `TriageHitRate`, `TriageMeanLatencyMs`, and a per-outcome breakdown.

Read the breakdown, not just the hit rate. A healthy tier's misses are mostly `needs-full-agent` — triage correctly declining. A pile of `unparseable` or `unknown-field` means triage is failing rather than declining, and is costing a call per instruction to do it.

The same observations ride `IMetricsSink` as `toolup.ai.triage.attempts`, `toolup.ai.triage.outcomes` (tagged `outcome`) and `toolup.ai.triage.duration.ms`; the series are registered by `AIServerApp.create` whether or not you opt in, so turning the tier on mid-life needs no metrics re-wiring.

## Verification

- Your provider (or double) compiles against the widened record.
- Without `withFastPathTriage`, a chat turn hits the provider exactly once, as before.
- With it, an instruction naming a declared field resolves without a `SendMessage` call, and `/dev/ai-fastpath` shows one attempt with outcome `hit`.

## Rollback

Remove the `withFastPathTriage` call. The record widening stays — it is a compile-time contract change, not a behavioural one, and `SupportsTriage = false` restores the pre-6j.B path exactly.
