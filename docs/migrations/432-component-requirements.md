# Phase 432 — component secret & config requirements manifest (consumer migration)

**What changes.** A composed component can now declare what it *needs* — the credentials it resolves from `ISecretStore` and the config knobs it binds — as a `ComponentId`-keyed manifest of **names and classes, never values**. An opt-in preflight then checks **presence** before the app serves: every required secret resolves, every required knob binds, and the misses aggregate into one typed startup report naming the component and the requirement. The failure mode it replaces is a missing credential surfacing mid-request, three layers down, as a 401 or a null reference in whichever component first reached for it.

**Scope.** Entirely additive and entirely opt-in. Nothing is registered unless a deployment builds a `RequirementsSignature` and folds `ComponentRequirementsPreflight.serviceRegistration` into its composition; an empty signature registers no validator at all, so `ServerApp.empty |> ServerApp.run` composes a byte-for-byte identical service collection (GP 11 / GP 13). No existing signature changed and **no record gained a field** — including `CompanionCapability` and `ComposableSlot`, where the requirement data is carried by a parallel `ComponentId`-keyed shape instead (the Phase 585 call: adding a field to an F# record is a breaking constructor change).

**Who is affected.** Only a deployment that opts in. The natural adopters are compositions with companions that read credentials at first use — an audit sink, a notification channel, a provider — where "the deploy came up green and then failed on the first request" is a known shape.

## New public surface

| Surface | Tier | Purpose |
|---|---|---|
| `SecretRequirement`, `SecretClass`, `RequirementNecessity` | `ToolUp.Platform.Core` (`Shared/ComponentRequirements.fs`, Fable-packed) | A credential a component needs: scope + key + class + required/optional + purpose. No field can hold a value. |
| `ConfigRequirement`, `ConfigKnobType`, `ConfigDefault` | `ToolUp.Platform.Core` | A config knob: path + type + default-or-required. |
| `ComponentRequirements`, `RequirementsSignature` | `ToolUp.Platform.Core` | One component's requirement set, and the `Map<ComponentId, _>` over all of them — the parallel of Phase 282's `CapabilitySignature`. |
| `ComponentRequirements.none` / `create` / `withSecret` / `withConfig` / `merge` / `signatureOf` / `resolve` / `mergeSignature` | `ToolUp.Platform.Core` | Construction + the signature algebra. `none` is the identity an undeclared component contributes. |
| `ComponentRequirementsPreflight.fromComponentConfig` / `fromComponentConfigs` / `derive` | `ToolUp.Platform.Server` (`Server/ComponentRequirements.fs`) | Derivation from the Phase 289 config binding, plus the merge of declared requirements on top. |
| `ComponentRequirementsPreflight.probeOf` / `SecretPresenceProbe` / `configDefects` / `secretDefects` / `check` | `ToolUp.Platform.Server` | The presence checks and the typed `RequirementDefect` report. |
| `ComponentConfigRequirementsValidator`, `ComponentSecretRequirementsValidator`, `serviceRegistration`, `serviceRegistrationWithStore` | `ToolUp.Platform.Server` | The two `IConfigValidator`s and the opt-in registration closure. |
| `ComposableSurface.slotRequirements`, `SlotRequirementSet` | `ToolUp.Platform.Server` | "Composing X requires secrets A, B" — answered *before* composing. |

## Derived where possible, declared only where it cannot be derived

A component that already binds config by id through a Phase 289 `ComponentConfig` section declares **nothing**: `fromComponentConfig` reads the section and derives one `ConfigRequirement` per declared key, so the requirement set cannot drift from what the component actually binds. Knob types are inferred from the declared default (`"3"` → `IntKnob`, `"false"` → `BoolKnob`, `"https://…"` → `UriKnob`); a key whose declared value is **blank** carries no default, so it derives as required and the deployment must bind it.

Secrets have no equivalent registration to read — a companion resolves them inside its own `create` — so those are the residual declared half, keyed by the same `ComponentId` the companion's Phase 282 capability is keyed by:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.ComponentRequirementsPreflight

let splunk = ComponentId.forCompanionImpl "IAuditSink" "SplunkHec"

let declared =
    ComponentRequirements.signatureOf [
        ComponentRequirements.create
            splunk
            [ SecretRequirement.required
                  ComponentRequirements.PlatformScope
                  "SPLUNK_HEC_TOKEN"
                  ApiKeySecret
                  "authenticates audit-event delivery to the collector" ]
            []
    ]

// Derivation first, declaration on top (declaration wins on a collision).
let requirements = derive declaredConfigSections declared
```

## Wiring it in

`serviceRegistration` returns the same `IServiceCollection -> IServiceCollection` closure `CompositionValidator.serviceRegistration` does, so it folds into the composition root's extension `ServiceConfig` hook the same way:

```fsharp
let register =
    ComponentRequirementsPreflight.serviceRegistrationWithStore
        secretStore              // the ISecretStore this deployment composed
        resolvedConfigSections   // ComponentConfigResolver.resolve output
        requirements
```

Pass the **resolved** sections (declared defaults with the id-scoped `TOOLUP_COMPONENT__*` overrides already merged) — that is the final bound state the component will read.

## What the failure looks like

```
Config preflight failed — startup aborted.
  [ERROR] component-secret-requirements: [secret] Component
  'companion:IAuditSink/SplunkHec' requires secret _platform/SPLUNK_HEC_TOKEN
  (api-key), which does not resolve in the composed ISecretStore. Purpose:
  authenticates audit-event delivery to the collector. The component cannot
  function without it, so startup is aborted rather than failing mid-request.
  Provision the key under that scope in whichever secret store this deployment
  composes.
```

Every miss is reported, not just the first. A **missing optional** requirement is a `Warning` — logged with the same detail, startup continues.

**No secret value appears anywhere in this path.** `SecretPresenceProbe` is `string -> string -> Async<bool>`: the value is tested for blankness inside the probe and never leaves it, no requirement type has a field a value could occupy, and every report string is built from `SecretRequirement.describe` (scope + key + class). The test pack asserts this with a planted sentinel value rather than trusting the convention.

## Which preflight class each half is in

The check splits along the Phase 585 boundary, because a validator is classified as a whole:

* **`component-config-requirements` — structural-class** (`IStructuralClassValidator`). A pure in-memory sweep over the resolved sections: no socket, microseconds. `ServerConfig.SkipPreflight` does **not** bypass it — booting with required knobs unbound is not what that lever is for.
* **`component-secret-requirements` — external-probe class** (deliberately unmarked, therefore skippable). It calls `ISecretStore.GetSecret`, and the composed store may be a remote vault that is slow or down — exactly the dependency outage `SkipPreflight` exists to ride through. It is not security-class either: it reads no value and enforces no auth invariant, so its bypass costs an early warning, never a hole.

## Enumerating requirements before composing

`ComposableSurface.slotRequirements` joins a signature onto the derived companion-slot list, so an authoring tool can render "composing `IAuditSink` requires `_platform/SPLUNK_HEC_TOKEN` (api-key)" without composing anything:

```fsharp
ComposableSurface.slotRequirements requirements
|> List.iter (fun slot ->
    printfn "%s: %s" slot.Interface (slot.Secrets |> List.map SecretRequirement.describe |> String.concat ", "))
```

An empty signature yields `[]`.

## Verification

1. `dotnet build ToolUp.Forge.sln` — clean.
2. `dotnet run --project Build.fsproj -- VerifyAll` — green.
3. Test pack: `InProcess/ComponentRequirementsTests.fs` covers the derivation + inference, declaration-wins-on-collision, the required-miss error (naming component + key + class + scope), the optional-miss warning, the present-but-blank case, the unbound-knob error (naming the id-scoped override), the two validators' Phase 585 classification, the `slotRequirements` projection, and the no-value-in-any-report property.

## Rollback

Remove the `serviceRegistration` fold from your composition root. Nothing else changes: the requirement types are inert data, and a deployment that never builds a signature never registers a validator.
