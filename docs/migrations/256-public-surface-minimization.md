# Phase 256 — Public-API surface minimization (shrink-before-freeze)

**Stability impact:** breaking (46 public tokens across nine assemblies are now assembly-private).
**Consumer action required:** none for every first-party consumer measured — see [Consumer adoption](#consumer-adoption).

## What changed

Six symbol families that were public only because F# defaults to public are now `internal`. Every
one was measured to have no caller outside its own assembly — in this repo, in `samples/`,
`templates/` and `docs-snippets/`, and in the 46 first-party consumer trees under `Applications/`,
`cookbook-apps/` and `Modules/` — before it was hidden.

| Package | Symbol (baseline tokens removed) | Why it was never contract |
|---|---|---|
| `ToolUp.KnowledgeBase.Server` | `KnowledgeBase.ServerJsonHelpers` (4) | KB.Server's own JSON glue; every `open` is inside the assembly. |
| `ToolUp.Platform.Client` | `ToolUp.Remoting.Client.InternalUtilities` (10) | Browser-binary helpers for the proxy/HTTP layer beside it; nothing a consumer composes reaches it. |
| `ToolUp.Platform.Server` | `ToolUp.Remoting.Giraffe.GiraffeUtil` (7) | The adapter's build/dispatch plumbing behind `Remoting.buildHttpHandler`; tests see it through the existing `InternalsVisibleTo`. |
| `ToolUp.RAG.Server` | `ToolUp.RAG.CitationNormaliserImpl` (6, incl. `RollingCitationCounters`) | Its own header calls it the RAG-private bridge; `RAGCompose` is the only caller. |
| `ToolUp.ContentAuthoring` | `ToolUp.ContentAuthoring.ContentAdminApiImpl` (2) | The compose root is the only production caller. A new `InternalsVisibleTo(ToolUp.Platform.Tests)` keeps the authorization tests constructing it directly. |
| `ToolUp.Cli`, `ToolUp.Companions.Isolation`, `ToolUp.RAG.StaticCorpus.Build`, `ToolUp.Remoting.Generator` | the `Program` entry module (2 each) | An executable's entry point is not a contract. |

The change per file is one keyword — `module X` → `module internal X` — plus the one `<InternalsVisibleTo>`
line in `ToolUp.ContentAuthoring.fsproj`. The nine baselines were regenerated with the **scoped** switch
(`TOOLUP_APPROVE_API=<names>`, Phase 175), so no other baseline moved; `api-baselines/doc-coverage.approved.txt`
carries the nine recounted floors.

**Version notes.** Removing a public token is a breaking change under the SemVer-on-`0.x` policy and
`VerifySemVerBump` (Phase 260) will classify it as such at the next release. `<Version>` was deliberately
not moved here — 0.23.0 is frozen for release — so the operator classes the cut.

## How the surface was triaged, and what the measurement found

The shard assumed the surface was largely incidental — "helpers, DTO internals, plumbing" — and that an
access-modifier sweep would shrink it materially. That premise was **measured before implementing against
it**, and it does not hold at the scale the shard imagined:

- `dev-scripts/api-surface-triage.ps1` takes every **top-level** type in the 169 committed baselines
  (5,050 at the start of the phase; 10,377 rendered type headers including nested `Tags` / `Module`
  companions) and checks whether its simple name occurs as an identifier in any *other* in-repo
  project, in the in-tree samples/templates/snippets, or in any external first-party consumer tree.
  Reference detection is by identifier token, deliberately conservative (a false "referenced" keeps a
  symbol public; a false "unreferenced" would be the dangerous error).
- **4,111 of 5,050 (81%) are referenced from another package in this repo.** Forge is a federation of
  ~170 packages, and F# has no visibility between `public` and assembly-private beyond
  `InternalsVisibleTo`, so a symbol a sibling package consumes *must* be public. What the shard would
  read as plumbing — `ToolUp.Platform.Compose*` (16 modules), the handler factories, the
  `RemotingHelpers` — is the cross-package seam the companion tier is built on (20+ companion
  packages call into it).
- **783 are referenced from an external first-party consumer** and are contract by observation.
- **898 are referenced nowhere the script can see** — the candidate set. A name-pattern pass over it
  (`Impl`, `Helpers`, `Internal`, `Util`, entry-point `Program`) flagged 17; reading each produced the
  six families above and eleven that are contract despite the name (next section). The remaining
  ~880 candidates are domain types — a `Msg` the shell dispatches, an options record set through a
  builder, a seam shipped ahead of its first implementor, a record a consumer only ever *receives* —
  and none of them can be hidden by a mechanical rule. They are recorded, by assembly, in
  [`docs/reference/public-surface-triage.md`](../reference/public-surface-triage.md) (generated;
  re-run the script) so a domain owner can judge them one package at a time.

So the sweep this phase asked for is the small residue above, and the shrink the shard wanted at
freeze time comes from the packaging restructure under way (Phase 347's shared-types split and its
siblings), not from access modifiers. Phase 626's
`DeadCodeReport` was consulted and, by construction, says nothing here: it covers `let private` /
`let internal` only, so a public-and-uncalled symbol is exactly what it cannot see.

### Name-flagged, read, and kept — with the reason

| Symbol | Kept because |
|---|---|
| `ToolUp.Platform.RemotingHelpers` | Five companion packages call it. |
| `ToolUp.AI.ToolHelpers` | Five external consumers (the `toolup-module-*` modules) call it. |
| `ToolUp.PublicRendering.StructuredDataHelpers`, `PublicContentApiImpl` | `samples/PublicSite`, two external consumers, and the SSR docs cite them. |
| `ToolUp.Platform.OAuth1aCredentialUIHelpers` | The documented credential-form seam for OAuth 1.0a connector authors — no first implementor yet, which is the shape a reference count cannot judge. |
| `ToolUp.Platform.Tracing.ActivityContextHelpers` | The technical guide names `tryParseTraceparent` as the contract `INotificationChannel` companions re-join traces through. |
| `ToolUp.Elmish.{HMR,React}.Program`, `ToolUp.Elmish.Sub.Internal` | Vendored Elmish public API; upstream keeps them public for the same reason. |
| `ToolUp.Remoting.Json.SystemTextJson.*Converter` / `*Factory` (30 types) | Zero external references, but they are the STJ converter set a consumer could register piecemeal on its own `JsonSerializerOptions`. Hiding them is a decision about a serialization-extensibility seam, escalated to the operator rather than taken here. |

## The parked Tier-4 renames (2026-05-24 API audit) — all five decided

The audit's Tier 2/3 items shipped as Phase 11.C.5 (see [11-C-5-public-api-stability-cluster.md](11-C-5-public-api-stability-cluster.md)).
Its "would-be-nice, accept-the-1.0-bump" Tier 4 list was never committed to this repo — the repo was
re-cut on 2026-05-28 — so the five items are reproduced here in full and this table is now their record.
Each, measured against today's surface:

| # | Item | Decision | Rationale |
|---|---|---|---|
| 1 | `KnowledgeApi` / `AISettingsApi` / `AIAssistantApi` lack the `I` prefix | **deferred-to-2.0** | The convention is mixed across the whole surface: 33 Remoting API records carry `I…Api`, 48 do not — `TeamApi`, `JobApi`, `PlatformAdminApi`, `PermissionApi` in the core among them, beside `IPresenceApi` and `ISessionApi` in the same assembly. Renaming the three named would unify nothing; renaming all 48 is a source break for every consumer's `Api.makeProxy<…>` call at a frozen release. The 2.0 line decides the convention once and applies it everywhere. |
| 2 | `AIServerApp.Base` / `RAGServerApp.AI` accessor leak | **deferred-to-2.0** | The `.Base` field is now the composition ladder's shape, adopted since by `McpServerApp`, `RAGServerApp` and every other compose root that wraps a base app. Hiding it means a class-based ladder — a redesign of every `*ServerApp` record, not a rename. |
| 3 | Acronym drift (`SplunkHec` vs `SplunkHEC`) | **resolved — not a defect** | No `SplunkHEC` spelling exists on the surface; `SplunkHec`, `Cef`, `Ldap`, `Oidc`, `Scim`, `Csrf`, `Jwt` all follow the .NET rule (3+ letter acronyms Pascal-cased). The only all-caps runs left are product names carried as namespaces (`ToolUp.RAG`, `ToolUp.Graph.AGE`, vendored `ToolUp.Elmish.HMR`) and the `ToolUp.Platform.SSE` module. The namespaces stay by decision; the one module is folded into item 4's line. |
| 4 | `ServerConfig` field-shape newtypes | **deferred-to-2.0** | `ServerConfig` has 141 public fields; newtyping them retypes the record constructor and every `{ ServerConfig.defaults with … }` in every consumer. A wholesale record redesign belongs to a major. |
| 5 | Builder convention (`empty` vs `create`, `fromConfig` vs `create`) | **deferred-to-2.0** | `create` is the convention by weight (471 sites) against `init` 34, `empty` 14, `fromConfig` 3; 27 modules expose two shapes. Unifying is a consumer-wide rename sweep of the minority forms — a 2.0 act. New builders use `create`. |

Nothing is left undecided; a 2.0 planning pass reads this table rather than the audit.

## Verification

```powershell
# the six families are gone from the surface and nothing else moved
git diff de2f3021 -- api-baselines | Select-String '^[-+][^-+]'
# the approval pack compares clean with the switch UNSET (approve mode passes trivially)
dotnet build ToolUp.Forge.sln --nologo
dotnet src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter "ToolUp.Platform.Tests.Phase 175"
# re-derive the triage
pwsh ./dev-scripts/api-surface-triage.ps1 -Out triage.csv -Markdown docs/reference/public-surface-triage.md `
    -ConsumerRoots ../../Applications,../cookbook-apps,../../Modules
```

## Rollback

Revert the phase's commits; the baselines travel with them. Nothing was deleted, so re-exposing one
family with intent is the one-keyword change back plus a scoped regen of its baseline.

## Consumer adoption

For every measured consumer the answer is "nothing to do": none referenced any of the six families.
A consumer outside the workspace that did would fail to compile with FS0039 on the name; the remedy
is the public entry point beside it — `Remoting.buildHttpHandler` for `GiraffeUtil`, `FableConverters`
for the KB JSON helpers, `ContentAdminCompose` for `ContentAdminApiImpl`, `RAGServerApp` for the
citation bridge — or, for a genuine need, re-expose the symbol with intent and a baseline regen.
The workspace `SDK-ADOPTION.md` matrix is generated; each consumer records its own stance by flipping its
`sdk-adoption.json` record to `n-a` (no referenced symbol) on re-pin.
