# Upgrading from 0.x to 1.0

**Who this is for.** A consumer on any `0.x` release who wants to be on `1.0.0` and is not going to
walk every per-release migration entry on the way. This page is the consolidated list of every
**breaking rename** and every **internalization** the `0.x` line accumulated, in the order to apply
them, with the mechanical parts handed to the Phase 183 codemod. The per-release detail — every
member added, changed and removed between any two releases — is [`CHANGELOG.md`](../../CHANGELOG.md),
generated from the same public-API baselines the approval gate decides each commit with; this page
says what to *do*, that one says what *moved*.

**What 1.0 means.** From `1.0.0` the ordinary SemVer table applies: a breaking change is a major, an
addition a minor. Under the `0.x` policy a break was a MINOR bump, so a consumer that pinned a minor
and read no migration entries may be carrying several of the changes below already-broken and not
yet compiled against. Do the audit in §1 first; it is cheap and it tells you which sections apply.

The precondition for the 1.0 tag itself is the readiness scorecard
([`docs/reference/v1-readiness.md`](../reference/v1-readiness.md), Phase 257); its `undecided-renames`
row reads the decision table in §5 below, so a rename left undecided blocks the tag rather than
surprising a consumer after it.

## 1. Find out where you are

```powershell
git grep -n "ToolUpSdkVersion\|ToolUp\.Platform\.Core" -- Directory.Packages.props   # the pin you hold
git grep -ln "Fable\.Remoting\|open Elmish\|PlatformMode\|Accept.*InAuthenticatedMode" -- '*.fs' '*.fsx'   # §2 sites
git grep -ln "EntraExternalId\|ServerJsonHelpers\|InternalUtilities\|GiraffeUtil\|CitationNormaliserImpl\|ContentAdminApiImpl" -- '*.fs' '*.fsproj'   # §3–§4 sites
```

Then run the codemod in check mode from your Build project; it lists every rename it will apply and
every site it wants a human to look at, and writes nothing:

```powershell
dotnet run -- Codemod src --check
```

## 2. The renames — mechanical, via the codemod (Phases 73 / 66 / 11.C.5)

Three rename clusters shipped during `0.x` as prose migration entries. Every one that is
deterministic — 1:1, context-free — is applied by
[`dotnet run -- Codemod <dir>`](183-consumer-codemod-analyzer-breaking-renames.md), which rewrites
the lines in place, Fantomas-formats what it touched, and prints the sites it deliberately did not
guess at. Run it once per consumer tree; a second run is a no-op.

| Cluster | What renames | Applied by the codemod | Read first |
|---|---|---|---|
| **Phase 73** — the in-tree forks are named as forks | `Fable.Remoting.{Server,Client,Json,Giraffe,MsgPack}` → `ToolUp.Remoting.*`; `Elmish` / `Elmish.React` / `Elmish.HMR` → `ToolUp.Elmish` / `.React` / `.HMR` (opens and fully-qualified names) | yes — `73-remoting-namespace`, `73-elmish-open`, `73-elmish-qualified` | [73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md](73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md) |
| **Phase 66** — `PlatformMode` → `Surfaces` | `ServerConfig.Mode` / `ClientConfig.Mode` → `.Surfaces`; the five `Accept*InAuthenticatedMode` flags → `Accept*WhenAuthRequired`; `TOOLUP_PLATFORM_MODE` → `TOOLUP_PLATFORM_SURFACES` (and the Vite define); `BundleConstants.platformMode` → `.platformSurfaces`; `UserSession.getMode ()` → `.getSubjectKind ()` | yes — the six `66-*` rules | [0.X.0-platform-mode-to-surfaces.md](0.X.0-platform-mode-to-surfaces.md) |
| **Phase 11.C.5** — public-API stability cluster | `AuditReplicatorRetryPolicy` → `RetryPolicy`; `NotificationKind.SinkKind.Email` / `.Sms` → `SinkKind.Email` / `.Sms`; the five Tier-2 package ids (`ToolUp.Platform.AuditSinks.*`, `ToolUp.Platform.NotificationChannels.*`, `ToolUp.Platform.Metrics.OpenTelemetry`, `ToolUp.Storage.Azure`, `ToolUp.AuthProviders.OidcClient`) → their current ids, **project files only** | yes — `11c5-audit-retry-policy`, `11c5-sink-kind`, the five `11c5-package-*` | [11-C-5-public-api-stability-cluster.md](11-C-5-public-api-stability-cluster.md) |

**What the codemod reports and does not rewrite** — each site is listed per file with its line and
the migration section to read, and the bytes are left alone:

- Phase 66: any `match ctx.Mode with` (the arm mapping onto `Subject` is per site), other `.Mode`
  reads, a `PlatformMode` type reference, `withAnonymousRoute` / `AnonymousRoutePrefixes` (per-route
  `SurfaceRequirement` now), an `IAuditSink` implementation (`SchemaVersion` + `AuditEnvelope list`),
  `UserSession.configure`, `DevDefaultUserId` (deleted), `AuthEnforcementMiddleware` (retired),
  `RateLimit = Some | None` (no longer an option).
- Phase 11.C.5: an `IAuthProvider` implementation and any `:?> HttpContext` cast (T3.1
  `RequestContext`), `MaxRetries` / `BackoffMs` (T3.2 — `MaxAttempts` counts the first attempt),
  `NotificationKind.SinkKind.Push` or a `Kind = "…"` string (T3.3 — choose the `PushVariant`),
  `ClientConfig.create` (T3.4 — trailing `ClientHandlerRegistry`), `createWithModel` /
  `createWithBatchSize` (T3.5 — `secretStore` first).
- Phase 73: a `Fable.Remoting.X` the fork does not carry (`AspNetCore`, `DotnetClient`, …), an
  `Elmish.X` the runtime does not carry (`Navigation`, `UrlParser`, `Debug`, …), and
  `FableJsonConverter` (retired with the System.Text.Json migration — use
  `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()`).

Work that list by hand against the three entries, then `dotnet build` and
`dotnet fable -o output --noCache` per client project: an `open` the codemod could not classify
surfaces as an unresolved namespace, which is the intended failure.

## 3. The internalizations — nothing to do unless a name stops resolving (Phase 256)

Six symbol families that were public only because F# defaults to public are assembly-private since
`0.23.0`. Every one was measured to have no caller outside its own assembly — in this repository,
its samples, templates and doc snippets, and forty-six first-party consumer trees — before it was
hidden, so for a measured consumer the answer is "nothing to do". A consumer outside that set that
did reach one fails to compile with **FS0039** on the name; the remedy is the public entry point
beside it:

| Package | Now internal | Use instead |
|---|---|---|
| `ToolUp.KnowledgeBase.Server` | `KnowledgeBase.ServerJsonHelpers` | `ToolUp.Remoting.Json.SystemTextJson.FableConverters` |
| `ToolUp.Platform.Client` | `ToolUp.Remoting.Client.InternalUtilities` | the proxy you already hold from `Api.makeProxy` |
| `ToolUp.Platform.Server` | `ToolUp.Remoting.Giraffe.GiraffeUtil` | `Remoting.buildHttpHandler` |
| `ToolUp.RAG.Server` | `ToolUp.RAG.CitationNormaliserImpl` (incl. `RollingCitationCounters`) | `RAGServerApp` composes it |
| `ToolUp.ContentAuthoring` | `ToolUp.ContentAuthoring.ContentAdminApiImpl` | `ContentAdminCompose` |
| `ToolUp.Cli`, `ToolUp.Companions.Isolation`, `ToolUp.RAG.StaticCorpus.Build`, `ToolUp.Remoting.Generator` | the `Program` entry module | the executable's command line |

Detail, the triage that found them, and the eleven name-flagged symbols that were *kept* with their
reasons: [256-public-surface-minimization.md](256-public-surface-minimization.md). A genuine need for
one of the six is a request to re-expose it with intent — one keyword and a scoped baseline regen —
not a reason to copy it.

## 4. Packages that no longer ship — drop the reference before raising the pin

A withdrawn package is the one break a version number cannot express: raising `<ToolUpSdkVersion>`
with the `PackageReference` still present fails at **restore** (`NU1101`), not at compile.

| Withdrawn | In | Replacement |
|---|---|---|
| `ToolUp.AuthProviders.EntraExternalId`, `ToolUp.AuthProviders.EntraExternalId.Client` | `0.23.0` | the OIDC provider against the same External ID tenant and app registration — [0.23.0-entra-external-id-removal.md](0.23.0-entra-external-id-removal.md) (deprecated since [0.4.0](0.4.0-entra-external-id-deprecation.md)) |

Every withdrawal is also a one-line `package withdrawn` entry under **Removed** in the CHANGELOG
section of the release that made it, so a later one will be found there rather than here.

## 5. Deprecations — what 1.0 removes (all of them), and what it carries (none)

Every `[<Obsolete>]` notice on the `0.x` surface names its replacement and its removal target, and the
Phase 258 gate holds that sentence to a format. Those targeting "a future major" were decided **at**
the 1.0 cut, and the decision was to remove every one of them rather than carry any into 1.x — the
`openDeprecations` allowance in [`v1-readiness.json`](../../v1-readiness.json) stays at zero, and the
`open-deprecations` scorecard row reads `0 open` by removal. The seven are gone from `0.23.0`
([Phase 815](815-remove-open-deprecations.md), which carries the codemod rules that apply the
mechanical half):

| Removed member | Use instead |
|---|---|
| `ToolUp.Elmish.Program.withConsoleTrace` | `Program.withTrace` with a callback over `Program.safeMsgRepr`, or `ClientConfig.EnableElmishConsoleTrace` under the SDK shell |
| `ToolUp.Elmish.Program.withErrorHandler` | `Program.withErrorReporter (fun ctx -> onError (ctx.Message, ctx.Exception))` (structured `ErrorContext`) |
| `ToolUp.Platform.AgGrid` / `ToolUp.Platform.AgChart` compat modules (`ToolUp.Platform.Client`) | `open Feliz.AgGrid` / `open Feliz.AgCharts` — the bindings are standalone packages since Phase 344 |
| `ThemeClass` (both the compat module's and `Feliz.AgGrid`'s) | the Theming API: `AgGrid.theme Theme.themeBalham` (a `*Dark` class is `|> Theme.withPart Theme.colorSchemeDark`) |
| `ToolUp.Platform.RemotingHelpers.makePermissionGuardedApi` | `ServerModule.withGuardedApi` with per-method `[<RequiresRole>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` attributes — [69d-authorization-metadata.md](69d-authorization-metadata.md) |

The authoritative list is the set of `(obsolete)` marker lines in `api-baselines/`, which the
scorecard counts — empty on this tree. A deprecation marked after this page was written is decided
the same way at the next major.

**Renames deliberately NOT in 1.0.** The five parked Tier-4 renames from the 2026-05-24 API audit
are decided in [Phase 256's table](256-public-surface-minimization.md#the-parked-tier-4-renames-2026-05-24-api-audit--all-five-decided):
four are **deferred to 2.0** (the `I…Api` prefix convention, the `*ServerApp.Base` accessor,
`ServerConfig` field newtypes, the `create` / `empty` / `fromConfig` builder convention) and one was
resolved as not a defect. A consumer need not prepare for any of them on the 1.0 line.

## 6. Record widenings — the class that breaks a full-literal construction

Across `0.x` several shared records gained fields (`ServerConfig`, `ClientConfig`,
`SubjectResolutionRequest`, the InterPlatform `PeerJobFusion` / `PeerServerApp` records, among
others). A consumer that constructs such a record with a **full literal** stops compiling with
**FS0764** (a field is missing); one that uses the `{ X.defaults with … }` / `X.create` shape is
unaffected, which is why every entry recommends that shape. The CHANGELOG lists each widening as a
constructor line under **Changed** in the release that made it, `before → after`, so the field that
appeared is named there. The remedy is the same every time: build against the release you are moving
to, and for each FS0764 either switch to the builder or add the field with its documented default.

## 7. Verify

```powershell
dotnet run -- Codemod src --check          # exits 0, "no rewrites pending"
dotnet build <consumer>.sln                # clean — the internalizations and widenings surface here
dotnet fable -o output --noCache           # per client project — the Phase 73 opens surface here
dotnet fantomas --check src                # the codemod formatted only what it rewrote
```

Then walk the CHANGELOG sections between the release you left and `1.0.0`, reading **Removed** and
**Changed** for the packages you reference — the two lists are what stops compiling, and each one is
complete in count and links the `git diff` that shows every member past the listing cap.

## Rollback

Reverting the pin is the rollback for every change here; the codemod's rewrites are plain edits
(`git checkout -- <the rewritten files>`) and it holds no state. A partial upgrade — codemod applied,
pin not yet raised — compiles against the old release only if that release already carried the new
names; the CHANGELOG section for it says whether it did.

## See also

- [`CHANGELOG.md`](../../CHANGELOG.md) — every release's Added / Changed / Removed, generated.
- [262-changelog-upgrade-guide-generator.md](262-changelog-upgrade-guide-generator.md) — how the
  CHANGELOG is generated, what it reads, and how to regenerate it.
- [183-consumer-codemod-analyzer-breaking-renames.md](183-consumer-codemod-analyzer-breaking-renames.md) —
  the codemod's rule tables and the golden-file pack that decides them.
- [257-v1-readiness-scorecard.md](257-v1-readiness-scorecard.md) — the six preconditions for the tag.
- [`docs/platform/deprecation-policy.md`](../platform/deprecation-policy.md) — window, notice format,
  removal-only-at-a-major.
