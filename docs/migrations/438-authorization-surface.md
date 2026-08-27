# Migration — Phase 438 authorization-surface manifest (`AuthorizationSurface`)

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed; nothing is registered into DI by this phase at all. A deployment that does not call anything below composes byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is required to upgrade.**

## Why

`CompositionManifest` (Phase 280) says *what* was composed, `EventTopology` (431) *who talks to whom*, `DataFootprint` (433) *what is stored*. None of them answers the question a security review opens with: **what does this application expose, and what does each entry require?** That answer existed only in source — spread across route registrations, API-record attributes, tool declarations and job triggers — so "which endpoints are reachable anonymously?" was a code-reading exercise repeated by hand every review.

`AuthorizationSurface` makes it a value. Per `ComponentId`: every externally reachable entry with its authorization requirement and a resolved default-deny classification. The **anonymous-reachable set** is the headline; an addition to it is a CI failure, not a pentest finding.

## What it derives from (zero per-module effort)

| Seam | Read from | Attributed to |
|---|---|---|
| route sub-tree | `ServerModule.RoutePrefixes` × `DefaultSurfaceRequirement` | the module's explicit `ComponentId`, else its `Name`-derived one |
| exact route override | `ServerModule.RouteSurfaceRequirements` | same |
| AI-drivable tool | `ServerModule.AITools` | same |
| event-triggered handler | `ServerModule.JobHandlers` where `Trigger = OnEvent` | same |
| remoting endpoint | the API record TYPE, through the dispatcher's own Phase 69d `AuthClassifier` | the `ComponentId` you pass |

A module that adds a route, a tool, or an `OnEvent` job surfaces with **no change to `AuthorizationSurface.fs`**. The remoting half reads the *same normalised classification the dispatcher evaluates per request*, from both attribute families (`ToolUp.Remoting.Server.*` and the tier-shared `ToolUp.Platform.*` mirrors) — so the manifest and the enforcement cannot drift.

**What it does not guess at.** A module's `Handlers` are Giraffe closures; their routes are not enumerable, so the route surface is the *declared* prefixes / overrides. A remoting endpoint needs its record type because `ServerApp` accumulates built handlers, not contracts. An undeclared route is invisible here — and equally invisible to the Phase 66 registry, so it runs under the strict global fallback rather than under nothing.

## Reading it

```fsharp
let surface = AuthorizationSurface.ofModules modules                 // routes / tools / event handlers
let api     = AuthorizationSurface.ofApiRecord<IReportsApi> componentId   // remoting endpoints
let whole   = AuthorizationSurface.mergeAll [ surface; api ]

AuthorizationSurface.anonymousReachable whole      // the headline set
AuthorizationSurface.defaultDenied whole
AuthorizationSurface.explicitlyRequired whole
AuthorizationSurface.ofComponent componentId whole // per-component attribution
AuthorizationSurface.ofKind ExposedRoute whole
```

### The three classifications

* `ExplicitRequirement` — the registration declares a gate (a role, a claim, a tenant binding, an admit set moved off the strict `SurfaceRequirement.userOrTeam` fallback, or a matched action-policy rule).
* `InheritedDefaultDeny` — nothing was declared, so the fail-closed floor applies. **Not a defect** — the correct posture for most surfaces, said out loud so "no declaration" is never mistaken for "no gate" in either direction.
* `AnonymousReachable` — an unauthenticated caller reaches it.

### Folding in a Phase 113 action policy (optional)

```fsharp
let resolved = AuthorizationSurface.resolveWithPolicy policy whole
```

Only entries still at the default-deny floor are refined; an entry that already declares a gate is never overwritten by a policy that does not run in front of it. A rule granting `Unrestricted` resolves to `AnonymousReachable` — that is what "grant unconditionally" means, and it promotes `ActionAuthorizer.allowAll`'s dev-only warning into a headline diff entry.

## The golden-file gate (438.C)

A fourth sidecar baseline beside the composition / topology / footprint ones — `composition-baselines/authorization-surface-baseline.json`, keyed by `ComponentId` rather than grown as a field on a shipped record (growing an F# record breaks its constructor). Approved by the same flag:

```powershell
$env:TOOLUP_APPROVE_COMPOSITION = "1"
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
$env:TOOLUP_APPROVE_COMPOSITION = $null
```

`AuthorizationSurface.diff` is keyed by `(Component, ExposedKind, Endpoint)`, so re-ordering registrations diffs to empty, and `severity` classifies the result:

* **`CriticalAuthorizationDrift`** — a new anonymous-reachable entry, or **any weakening**.
* **`ReviewableAuthorizationDrift`** — anything else that moved (an added guarded surface, a removal, a strengthening).

"Weaker" is defined once, on two axes that move in **opposite** directions: an *admit* set (`subject:<Kind>`) is weaker when it grows; a *demand* set (`role:` / `claim:` / `tenant` / `permission:`) is weaker when it shrinks. A swapped requirement — neither subset nor superset — counts as weakened, deliberately: it is not provably at least as strong as what it replaced.

## The facets beside it

The manifest has grown two sibling projections rather than fields — the rule this file records above, that growing a shipped F# record breaks its constructor. All three join on `ComponentId`.

* **Outbound authority (Phase 688)** — what each component *reaches*: `SeamAuthoritySurface`, in [`688-seam-authority-grants.md`](688-seam-authority-grants.md).
* **Grant authority (Phase 554)** — *who can hand out access to a module, by which path, and what must be true first*: `GrantAuthoritySurface`, in [`554-grant-authority-facet.md`](554-grant-authority-facet.md). Per module carrying a Phase 551 `GrantPolicy`: the policy, the principal classes that can still write a grant against it, the write paths still open, and what each demands — plus the counterparty review workflow ("what to check before signing"). Static composition truth; live consent state is deliberately not folded in.

## Consumer action

None required. To adopt: derive the surface where you already build the composition manifest, and — if you want the gate — persist `AuthorizationSurface.toWire` beside your other baselines and compare through `diff`. `ofWire` round-trips exactly, and an unrecognised persisted classification reads back as `InheritedDefaultDeny` (never as a fabricated headline finding, never as a claimed gate).
