# Phase 289 — component-scoped configuration binding (`ComponentConfig`) (consumer migration)

**What changes.** Configuration becomes addressable, validated, and overridable **by
`ComponentId`** rather than by an ad-hoc global key. Two surfaces:

1. **`ComponentConfig`** (`ToolUp.Platform.Core`, Fable-safe) — a config section owned by one
   component (`{ Component: ComponentId; Values: Map<string,string> }`), plus the pure, deterministic
   id-scoped env-var-name derivation `ComponentConfig.envVarName : ComponentId -> string -> string`
   (`TOOLUP_COMPONENT__<canonical id>__<canonical key>`). The derivation folds a slot-prefixed id
   (`module:orders-service`) into a shell-legal token (`MODULE_ORDERS_SERVICE`), so declaration and
   resolution agree on both tiers.
2. **`ComponentConfigResolver`** (`ToolUp.Platform.Server`) — `resolve` merges a declared section with
   its id-scoped env overrides (only *declared* keys are overridable, keeping the surface
   typo-catchable); `overrideValidator` is an `IConfigValidator` that fails preflight when a
   `TOOLUP_COMPONENT__*` variable targets no known component + declared key, naming the offending
   variable.

**Scope.** Purely additive and zero-cost when unused (GP 11 + GP 13). A deployment that declares no
`ComponentConfig` and sets no `TOOLUP_COMPONENT__*` override behaves exactly as before — the global
`ServerConfig` path is untouched. The env accessors are injected, so `resolve` + the validator are
pure over an injected view of the environment (deterministic, testable, no global mutation).

## Adopting the binding

```fsharp
open ToolUp.Platform.ComponentConfigResolver

// 1. Declare a component's config section, keyed by its Phase 279 id.
let ordersCfg =
    ComponentConfig.create (ComponentId.ofModule "orders-service") [ "maxItems", "100"; "currency", "GBP" ]

// 2. Resolve — an id-scoped env override wins per declared key.
//    Set TOOLUP_COMPONENT__MODULE_ORDERS_SERVICE__MAXITEMS=250 to override just this component.
let resolved = resolve environmentReader ordersCfg

// 3. Fail preflight loudly on a typo'd override (wrong id / undeclared key).
let app = app |> ServerApp.withConfigValidator (overrideValidator [ ordersCfg ])
```

## Verification

- `InProcess/ComponentConfigTests.fs`: `envVarName` folds a slot-prefixed id + key into a legal
  token; an id-scoped override reaches exactly its component; an override for another id / an
  undeclared key does not leak; a no-override section resolves to itself (GP 11); a stray override
  fails preflight with an id-named message; a valid / absent override passes.

## Rollback

Stop declaring `ComponentConfig` sections + wiring `overrideValidator` — the global `ServerConfig`
path is unchanged. Or revert the Phase 289 forge commit; no persisted state is involved (overrides
are read-side only).
