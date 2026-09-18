<!--
SPDX-License-Identifier: Apache-2.0
Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)
-->

# Phase 793 — the tool effect class

## What changes

`AIToolDefinition` (`ToolUp.Platform.Core`) gains a required field, `Effects: ToolEffectDeclaration`,
declaring what a tool's body may **do**: a set of `ToolEffect` values — `ReadFacts`, `ComputeFacts`,
`ReadContent`, `WriteState of scope`, `Egress of destination`, `Spend of budgetClass`,
`External of capabilityId`, `EmitsActions` — or `UndeclaredEffects`. F# records require every field,
so **every full `AIToolDefinition` literal stops compiling until it names one**; the compiler lists
each site. Nothing else about the record moves.

Three things read the declaration, and none of them changes a deployment that composes nothing new:

- **The gate.** `AIToolRegistry.ListAccessible` and the agent loop's dispatch re-check now share one
  pure decision, `ToolGate.decide`, over the caller, the registry's `ToolPolicy` and the declaration.
  A registry built with `AIToolRegistry()` runs under `ToolPolicy.unrestricted` — every class
  admitted, undeclared tools admitted — which is the pre-793 list and the pre-793 refusals, byte for
  byte.
- **The envelope.** A server-resident body runs inside its declaration. A host-capability invocation
  routed through `ToolEffectEnvelope.guardInvoke`, an outbound request through `guardEgress`, or a
  scoped write through `guardWrite` is refused unless the matching effect was declared; the refusal
  comes back as a value and lands on `_platform.ai.tool_allowlist_denial`. An undeclared tool has no
  envelope and runs as it did.
- **Composition.** `AIServerApp.withToolEffects profile policy` composes a policy; under
  `CompositionProfile.Verified` it refuses, at compose, any registered tool — built-in or module —
  that declares nothing (`CompositionProfileRefusal.ToolEffectsUndeclared`).

`ToolPolicy.RequireApproval` keys the Phase 503 approval hold on effect class, ahead of the
deployment's own `IToolApprovalPolicy`; `ToolPolicy.ExternalPrincipalCeiling` gates MCP agents
(Phase 489) by class, default-deny under a bounded policy.

## Copy-pasteable diff

Every tool literal:

```diff
     IsLiveInterface = false
     ResultBudget = DefaultResultBudget
+    Effects = ToolEffectDeclaration.readFacts
 }
```

Pick the declaration that matches the body: `ToolEffectDeclaration.readFacts` / `.readContent` /
`.computeFacts` for the common single-class tools, or
`ToolEffectDeclaration.declare [ ReadFacts; WriteState "orders"; Egress "api.example.com" ]` for
anything more. A tool you have not yet audited can declare `UndeclaredEffects`; it keeps working
under the default policy and is what the verified profile will name.

A body that reaches outside the process, through the seam that binds it:

```diff
-    let! body = client.GetStringAsync uri |> Async.AwaitTask
+    match! ToolEffectEnvelope.guardEgress "api.example.com" (fun () -> client.GetStringAsync uri |> Async.AwaitTask) with
+    | Ok body -> …
+    | Error denial -> return denial.Reason
```

Composing a policy (optional — nothing below is required to adopt the field):

```fsharp
AIServerApp.create aiProviderFactory providerProfile
|> AIServerApp.withToolEffects
    CompositionProfile.Verified
    (AIToolRegistry.ToolPolicy.readOnly
     |> AIToolRegistry.ToolPolicy.withApprovalFor [ ToolEffectClass.WriteState; ToolEffectClass.Spend ]
     |> AIToolRegistry.ToolPolicy.withExternalPrincipalCeiling [ ToolEffectClass.ReadFacts ])
```

## Verification

1. `dotnet build` — the compiler names every literal still missing `Effects`.
2. With no policy composed, the per-turn tool list and every refusal are unchanged; the Phase 793
   envelope pack (`ToolEffectEnvelopeTests`) pins this as GP 11.
3. With a bounded policy, `ListAccessible` drops any tool declaring a class outside the ceiling, and a
   forged call for one is refused with a typed `Denied` naming the effect, audited on the allowlist
   denial stream.
4. Under `CompositionProfile.Verified`, startup fails naming every undeclared tool; add its
   declaration and restart.

## Rollback

Set every literal's `Effects = UndeclaredEffects` and remove any `withToolEffects` call: the gate
admits undeclared tools under the default policy and no envelope is stamped, so behaviour is the
pre-793 one. The field itself cannot be removed without the compile error returning, and it is
additive by design — a tool that declares nothing is admitted everywhere a declaration is not
mandatory.
