# Migration — Phase 434 composition scale-readiness planner (`ScaleReadiness`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed, and **no new config knob was added**. A deployment that calls nothing below composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

Phase 282 made each companion's deployment posture a typed value — `CompanionCapability.Readiness` is `DistributedReady` or `DevOnly` — but nothing **joined** those declarations across a composition. So the operator's actual question ("can this app run 3 instances, or serverless?") was answered by reading N file headers and holding the result in their head, and the three shipped answers to it (`JobSchedulerInstanceValidator`, `ShareTokenRateLimiterDistributionValidator`, `AICancellationDispatchInstanceValidator`) each hard-code one companion, written by hand when someone noticed.

`ScaleReadiness` derives the same answer from whatever the composition declared: a per-component finding, a whole-composition verdict, and the companion swap that would lift each limitation.

## The join (434.A)

```fsharp
let report = ScaleReadiness.assessDeclared declarations (ServerApp.compositionManifest app)

report.Verdict                              // ComponentScale
report.Findings                             // per-ComponentId attribution
ScaleReadiness.limitingFindings report      // just the ones constraining the verdict
```

Per component, `ComponentScale` is one of:

| Case | Means |
|---|---|
| `MultiInstanceSafe` | N instances, no conditions. The meet **identity**, and what an *undeclared* component contributes |
| `MultiInstanceWith of ComponentId list` | safe only while the named distributed companions are also composed — carries the ones that are **not** |
| `SingleInstanceOnly` | declared `DevOnly`. **Absorbing** — one makes the whole composition single-instance-only |

The composition verdict is the **meet** of the parts: associative, commutative, idempotent, so it does not depend on compose order. This is the Phase 296 capability join read from the other end — there the bottom was harmless and one bad part contaminated upward; here the top is harmless and one bad part contaminates downward.

`ScaleReadiness.assess manifest` is the base case over `ScaleDeclarations.empty`: every component resolves to `CompanionCapability.identity`, so the report is all-`MultiInstanceSafe`. A composition that declares nothing has asserted nothing to the contrary.

## The declared half

A `ScaleDeclarations` sidecar keyed by `ComponentId` — beside `CapabilitySignature` (282), `RequirementsSignature` (432) and `FootprintSignature` (433), never a new field on a shipped record:

```fsharp
let declarations = {
    // Reuse the Phase 296 signature you may already declare.
    Capabilities =
        Map.ofList [
            ComponentId.forCompanionSlot "IJobScheduler", CompanionCapability.devOnlyEffecting
            ComponentId.forCompanionSlot "IBlobStorage", CompanionCapability.distributedEffecting
        ]

    // Optional: "safe across instances only alongside these".
    Prerequisites =
        Map.ofList [
            ComponentId.forCompanionSlot "IShareTokenStore",
            [ ComponentId.forCompanionSlot "IRateLimiter" ]
        ]
}
```

A prerequisite counts as satisfied only when it is **composed** *and* itself declared `DistributedReady` — present-but-dev-only cannot coordinate across instances. Resolution is deliberately one level deep, not transitive.

## Unblock suggestions (434.B)

```fsharp
ScaleReadiness.unblockLines report |> List.iter (printfn "%s")
```

One line per limiting finding, naming the swap from the Phase 293 `ComposableSurface` vocabulary: the slot's interface, its `ComponentId`, whether it is single- or multi-impl (a `MultiImpl` slot needs the dev-only implementation **removed** as well as a distributed one added), and the substrate its `create` typically receives. A **report line, never an auto-swap** — the vocabulary enumerates slots, not the packages that fill them, so a suggestion never invents a package name. A component the vocabulary knows no slot for (a module, a data type, a tool) still appears, saying that no swap can be named rather than going unmentioned.

## Preflight gate (434.C, opt-in)

**The intent knob already existed.** The gate reads the two `ServerConfig` fields that already declare topology — `ReplicaCount` (default `1`) and `ServerlessHost` (default `KestrelHost`):

```fsharp
// Fold into the composition root's ServiceConfig hook, exactly as the
// Phase 281 / 431 / 432 / 433 registrations are folded:
let services =
    ScaleReadinessPreflight.serviceRegistrationForConfig config declarations manifest services
```

| Code | Severity | Fires when |
|---|---|---|
| `scale-readiness-intent-unsatisfiable` | `DefectError` | `ReplicaCount > 1` or `ServerlessHost = ServerlessHost`, and the verdict is not `MultiInstanceSafe` |

Exported in the Phase 294 `ruleManifest` and the Phase 585 `classifiedRuleManifest`; **structural-class**, so `SkipPreflight` does not bypass it — an emergency boot should not silently start N instances of a composition that cannot serve N. `DefectError` rather than a warning because this is not a staged-work shape that resolves itself: the operator declared a topology and the composition provably cannot run in it.

**Nothing is registered on the default topology** — `ReplicaCount <= 1` with `KestrelHost` composes a byte-identical service collection, and the report is not even built (GP 13). A single-instance intent is satisfied by *every* verdict, including `SingleInstanceOnly`: an in-memory composition on one instance is the normal development shape, not a defect. The gate engages only where the deployment has already said it wants more.

## Verification

```powershell
dotnet build ToolUp.Forge.sln
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list ScaleReadiness
```

32 cases: the all-in-memory composition reporting `SingleInstanceOnly` with per-component attributions, the swap flipping the verdict, the meet semilattice laws, the prerequisite middle case, the unblock suggestions against a pinned vocabulary, and the gate registering nothing until the topology is declared.

## Rollback

Delete the declarations and the `serviceRegistrationForConfig` call. Nothing else reads the report; no shipped default depends on it, and the three per-companion instance validators are untouched and continue to fire on their own terms.
