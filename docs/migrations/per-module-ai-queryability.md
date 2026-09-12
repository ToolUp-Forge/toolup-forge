# Migration — per-module AI-queryability opt-in (Phase 36.C)

**Consumer-facing. This is a behaviour change, not an additive feature.** After upgrading, the
built-in cross-module AI tool family (`_platform.ai.*`, Phase 36.B) reaches **no module** until a
module declares that it wants to be reachable. If your deployment relies on the AI assistant reading
across modules, it stops doing so until you add one line per module.

## What changes

A module now declares whether its data is on the cross-module AI surface:

```fsharp skip=fragment
ServerModule.create "MoodJournal"
|> ServerModule.withAIExposure ModuleAIExposure.Queryable
|> ServerModule.withGuardedApi moodJournalApi
```

`ModuleAIExposure` has two cases, `NotQueryable` (the default, applied when a module declares
nothing) and `Queryable`. The declaration is keyed by the module's `Name` — the same axis
`AccessContext.ModulePermissions` and the Phase 551 grant policy use.

The six `_platform.ai.*` tools honour it as follows:

| Tool | Behaviour for a module that has NOT opted in |
|---|---|
| `list_accessible_modules` | still lists the module, with `queryable: false` |
| `list_data_types` | omits every data type whose producers are all non-queryable |
| `query_module` | `{"error":"UnqueryableModule"}` — the module's handler is not invoked |
| `query_entity` | `{"error":"UnqueryableModule"}` when every catalogued producer of the entity type is non-queryable |
| `list_results` | `{"error":"UnqueryableModule"}` |
| `get_latest_result` | `{"error":"UnqueryableModule"}` |

`list_accessible_modules` annotates rather than filters on purpose: the model can then tell the user
"that data lives in MoodJournal, open it there" instead of inferring the module is absent and
inventing around the gap.

## Why the default is OFF

Every other SDK feature defaults to its prior behaviour (GP 11). This one deliberately does not, and
the trade is the point of the phase: a user installing a third-party module must not have that
module's data become AI-readable **as a side effect of installing it**. The module author makes that
choice; the deployment can revoke it; nobody acquires it by inaction. Same posture as
`withEncryptedBlobStorage` and `withTransactionalSink`.

The cost is paid once, at upgrade, and it is visible — the assistant says `UnqueryableModule` and
names the module — rather than silent. The alternative default would have been silent in the other
direction, and permanently.

## What you have to do

1. **Decide, per module, whether its data belongs on the AI surface.** This is a data-governance
   decision, not a build fix. A module holding diary entries, HR records or anything a user would be
   surprised to see quoted back by an assistant is a legitimate `NotQueryable`.
2. **Add `|> ServerModule.withAIExposure ModuleAIExposure.Queryable`** to each module you want
   reachable, in your composition root. Nothing else changes — no config key, no DI registration, no
   migration of stored data.
3. **Re-run your AI smoke tests.** The failure mode to look for is an assistant that used to answer a
   cross-module question and now says a module is unqueryable.

```diff
  ServerModule.create "MoodJournal"
+ |> ServerModule.withAIExposure ModuleAIExposure.Queryable
  |> ServerModule.withDataTypes [ moodDataType ]
  |> ServerModule.withGuardedApi moodJournalApi

  ServerModule.create "Payroll"
+ // deliberately NOT AI-queryable — see docs/migrations/per-module-ai-queryability.md
  |> ServerModule.withGuardedApi payrollApi
```

A deployment that composes no AI at all, or that never used the `_platform.ai.*` family, needs to do
nothing: with no module declaring an exposure, no registry is composed and the only cost is one
failed `GetService` per tool invocation that never happens.

## Verification

- `_platform.ai.list_accessible_modules` reports `queryable: true` for exactly the modules you
  declared, and `false` for the rest.
- `_platform.ai.list_data_types` no longer lists data types produced only by non-queryable modules.
- `_platform.ai.query_module` against a non-queryable module returns `UnqueryableModule` and the
  module's query handler is not invoked.
- Stripping the `withAIExposure` call from a module reverses all three.

## Rollback

Remove the `withAIExposure` calls to return to a fully-closed AI surface, or pin the previous SDK
version to return to the pre-36.C behaviour where RBAC and the grant gate were the only gates.
Nothing is persisted, so rollback is a recompose.

## Design notes worth knowing

**`UnqueryableModule` is distinct from `PermissionDenied`, and is reported after it.** Distinct,
because no grant fixes it: a model told "denied" re-plans around a door that could open, and this one
cannot without a deployment change. Reported after, because a caller who may not read the module at
all must learn nothing from the refusal about which modules the deployment exposed. So the order at
every reach site is: RBAC → grant liveness (Phase 730) → AI exposure; the first two render
identically as `PermissionDenied`, and only the third is distinguishable.

**The declaration narrows only.** `withAIExposure` composes like `withGrantPolicy`: a composition
root may take a module from `Queryable` to `NotQueryable`, and a call that would widen an explicit
`NotQueryable` fails at compose time naming the module. A silently-widened exposure at composition is
exactly the accidental exposure this phase exists to prevent, and the composition root is the last
place it can be caught for free.

**It is a separate value from `GrantPolicy`, deliberately.** The grant model answers "is THIS
SUBJECT's grant on the module live" — a per-request lifecycle fact. This answers "is the module on
the AI surface for ANYONE" — a compose-time declaration with no subject in it. A module can be
granted, `Active` and consented and still be deliberately outside the AI surface. They are composed
into one predicate at the tool sites (`AIToolRegistry.moduleAIQueryGate` beside `moduleGrantGate`),
which is where the two questions meet; folding the second into `ModuleGrantPolicyRegistry` would have
changed what `isEmpty` means at eight call sites that ask neither question.

**Known limit — `query_entity` attribution.** An entity type is attributed to a module through the
data catalogue's producers, because `EntityRegistration` carries no module attribution (entities are
registered app-level by `ServerApp.withEntity`). An entity type with **no catalogued producer** has no
module to gate on and is passed through unchanged. Refusing it was the other candidate and is worse:
it would block an opted-in module's own entities whenever it happens not to also declare a `DataType`,
with a remedy unrelated to the opt-in. Closing the gap needs module attribution on
`EntityRegistration`, which is a substrate change of its own.

**Six-rule portability audit (GP 12): clean, and no new portable interface.** The phase adds two
value types (`ModuleAIExposure`, a two-case DU; `ModuleAIExposureRegistry`, a record over a
`Set<string>`) and one module-builder function. No new infrastructure interface is introduced, so
rules 1–6 have nothing to bind: identity is by module-name `string` (rule 1); the declaration is
compose-time data with no call boundary to make async (rule 2); there is no retry, supervision,
handler state, sharding or timing surface (rules 3–6). The registry is snapshotted at composition and
read-only thereafter, matching `ModuleGrantPolicyRegistry`. No framework-specific serialisation
attribute is added, and both types live in `ToolUp.Platform.Core` with no server or vendor
dependency.
