# Phase 554 — the grant-authority facet of the authorization-surface manifest

**Status:** net-new, opt-in, purely additive. No existing type, function, or default changed; nothing
is registered into DI by this phase at all. A deployment that does not call anything below composes
byte-for-byte what it did before and pays nothing at runtime (GP 11 / GP 13). **No consumer action is
required to upgrade.**

## Why

Phase 438 enumerates what a composition *exposes*; Phase 688 what each component *reaches*. A
counterparty deciding whether to let their data into a deployment asks a third question, and it sits
one level above both: **who can hand out access to this module, by which path, and what must be true
before they can?**

Phase 551 gave a module a voice on the second half — a declared `GrantPolicy` narrowing the
preconditions on being granted at all. What nobody had was a way to *read* the resulting authority
structure without following the write paths through the source. "The only grant paths to our module
demand our co-signature" was a sentence in a contract, checked by a reviewer with a code search and
re-checked from scratch whenever either side changed.

This facet makes it derived data. Per module carrying a declared policy: the policy, the **principal
classes** that can still write a grant against it, the write paths still open, and what each of those
demands.

## What it derives from

| Half | Read from | Kept honest by |
|---|---|---|
| the module | `ServerModule.GrantPolicy` (Phase 551) | derivation — a module that declares a policy appears with no change to `AuthorizationSurface.fs`, and one that declares nothing appears nowhere |
| the paths | the SDK's own `GrantAuthoritySurface.platformWritePaths` table | a drift guard that reflects over the real `IPermissionStore` and the real grant entry points and **fails on a member the table does not classify** |

The path half is a **declaration**, and deliberately so: which principal class reaches a store member
is a fact about the admin surfaces and the composition, not about any registration, so nothing can
derive it. The precedent is already in the file — `InHandlerGateDeclaration` (Phase 627.E), and seams
1 and 2 of the exposed surface, which read declarations too. What keeps a declaration honest is that
something independent checks it. Here that is the guard, and it exists because the only way a
meta-authority manifest can be *dangerous* is by under-reporting who can grant.

**Live consent state is deliberately absent.** Phase 552's `IGrantConsentStore` knows which
counterparty approvals exist right now; this is static composition truth — what the composition
*permits*, not what has been agreed. Folding a store read in here would make the manifest a query
against a moving target and quietly turn a composition-time artefact into a runtime one.

## Reading it

```fsharp
let facet = GrantAuthoritySurface.ofModules modules

GrantAuthoritySurface.entryFor "Payroll" facet        // one module's meta-authority
GrantAuthoritySurface.principalsOf "Payroll" facet    // who can grant it
GrantAuthoritySurface.counterpartyModules facet       // every module that named a party
GrantAuthoritySurface.render facet                    // the review artifact
```

`render` is byte-stable: modules in name order, paths and preconditions sorted within each entry, no
timestamp and no machine identity. Two runs over the same composition produce identical text, so a
difference in the artifact is a difference in the composition. `toWire` / `ofWire` round-trip exactly,
for a golden file or an external reviewer's tool.

### The five principal classes

* `team-owner` — a team-scoped administrator (`TeamRoles.canManageMembers`, i.e. `Owner` or `Admin`).
* `platform-admin` — bypasses the team-role gates server-side.
* `service-account` — composed server-side code with no interactive caller at the moment of the write.
* `grantee-subject` — **the grantee themselves.** Reachable only where a policy records a grant
  pending the subject's acceptance, and the class a review is most likely to miss.
* `counterparty` — the party a `RequiresCounterpartyApproval` arm names. Their verified consent record
  is an input to the write, so they are in the authority chain rather than observing it.

### Which paths stay open

Computed from the shipped Phase 551 / 552 guard semantics, arm for arm, never hand-listed per policy:

| Declared policy | Open paths |
|---|---|
| `AdminDiscretion` | every path that writes a grant at all (the module is absent from the facet — nothing was narrowed) |
| `RequiresAcknowledgement` | `PermissionGrants.grantModuleAccess`, `IPermissionStore.SetTeamPermissions` |
| `RequiresSubjectConsent` | those two, plus `PermissionGrants.acceptGrant` — the grantee's own act |
| `RequiresCounterpartyApproval` | `GrantConsentStore.grantWithCounterpartyApproval`, `IPermissionStore.SetTeamPermissions` |

`IPermissionStore.SetMemberPermissions` and `SetTeamDefaults` are closed against every declared arm:
neither has anywhere to carry an acknowledgement, which is why Phase 551 refuses them by construction
rather than by a check someone has to remember. `SetModuleExposure` is classified as conferring no
module authority at all — classified rather than omitted, so a *new* store member cannot pass by being
overlooked.

## The counterparty review workflow — what to check before signing

The facet exists to be read by someone who is not the deployment's author. In order:

1. **Find your modules.** `GrantAuthoritySurface.counterpartyModules facet` lists every module that
   named a party, with the party. If your party reference is not there, the declaration you were
   promised was not made — the module is grantable at administrative discretion.
2. **Read the party reference literally.** `PartyRef` is trimmed but **not** case-folded — the SDK
   never interprets it. `Acme-DPO` and `acme-dpo` are different parties, and a module naming the wrong
   one is refused for you and open to nobody.
3. **Check the open path list, not the policy alone.** The policy is what was declared; the open paths
   are what it *leaves reachable*. A `requires-counterparty-approval` module whose open paths include
   anything you do not recognise is the finding.
4. **Check the principal list for `grantee-subject`.** Under subject consent the grantee completes the
   authority. That is correct and intended — but if you believed only administrators could act, this is
   where that belief is corrected.
5. **Diff it, do not read it once.** Persist `toWire` beside your other baselines. A module that
   *loses* its declared policy, or gains an open path, is a change in who can grant — and a facet read
   once at contract time proves nothing about the deployment running next quarter.

What the facet does **not** prove: that a live consent record exists (that is Phase 552's registry, and
deliberately not here), and that no path exists outside the ones enumerated. The second is what the
drift guard defends: it fails on any `IPermissionStore` member or grant entry point the table has not
classified, so an unenumerated path cannot arrive quietly. It cannot see a consumer's own store
implementation reached without going through the composed seams.

## Verification

1. `dotnet build ToolUp.Forge.sln`
2. `dotnet run --project Build.fsproj -- VerifyAll`
3. For this facet alone: run the built Platform pack dll with
   `--filter "ToolUp.Platform.Tests.AuthorizationSurface"` (note Expecto joins the path with `.`, and a
   filter matching nothing exits 0 — read the count).

## Rollback

Stop calling `GrantAuthoritySurface.ofModules`. Nothing else in a composition depends on it; the facet
registers nothing, allocates nothing until asked, and reads no store.
