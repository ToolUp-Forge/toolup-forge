# Phase 1051 — composition and configuration defects

**What changes for you: a mistyped protective union token refuses the boot, a storage fault in a boot-time
bootstrap refuses the boot, and two sealed knobs render their case name.** No public member was removed or
retyped; one helper was deprecated (`ComposeTeamRuntime.runBootstrapTeam`, replaced by
`ComposeTeamRuntime.bootstrapTeam`). A default `AllInOne` deployment registers, mounts and seals what it did.

## 1. A typo on a protective union key refuses the boot

Phase 868 made a malformed value on a protective boolean stop startup. The keys whose value selects a union case
were left on the lenient path: an unrecognised token logged a warning and kept the configured value, so
`TOOLUP_SECURITY_HARDENING=stric` booted unhardened and `TOOLUP_REQUIRE_DIRECTORY_PROOF_FOR_DIRECT_ADD=ture`
booted with no proof. Every union key now takes the same direction rule:

| Direction | What a malformed value does | Keys |
|---|---|---|
| **Protective** — a typo could leave a protection or a behaviour the operator asked for switched off | **Stops startup**, naming the key, the value as written and the accepted tokens | every union key except the one below — the full list, with its accepted tokens, is the table in `DEPLOYMENT.md` ("Union keys — which ones refuse a typo") |
| **Permissive** — every token the environment can select only switches something off | Warns naming the accepted tokens, and the configured value stands | `TOOLUP_DATA_SUBJECT_REQUESTS` (the environment can only disable DSR, so keeping it on is the safe failure) |

Values are now trimmed before matching, and a whitespace-only value counts as unset, as 868 made the booleans
do. The accepted tokens are unchanged, so a deployment whose environment holds documented values is unaffected.

**Upgrading:** before rolling out, check every `TOOLUP_*` union value against the accepted tokens in the
`DEPLOYMENT.md` table (or in `docs/reference/config-reference.md`, which lists the same choices). A value
that used to warn — look for `not recognised. Valid: … Using the configured value.` in the startup log —
now stops the boot with `is not a recognised value. Expected one of: …`. Correct the token or unset the
variable.

An unrecognised token on a `TOOLUP_ACCEPT_*` escape hatch still reads as `false` (the safe direction), and is
now also a `Warning` row in the startup preflight, `escape-hatch-token`, naming the key and the value.

## 2. A storage fault in a boot-time bootstrap refuses the boot

Three boot-time reads used to catch every exception, log it and carry on: the platform-admin bootstrap
(`TOOLUP_INITIAL_PLATFORM_ADMIN`), the runtime-config store's load, and the initial-team bootstrap
(`TOOLUP_INITIAL_TEAM_NAME`). A storage that threw on those reads started the deployment with no platform
admin, a stub runtime-config store whose `Set` returned an error, or no team — and no refusal.

Each now records the fault as `ComposeAuth.BootstrapRefused` (the store, the remedy, the cause) and registers a
structural-class preflight row that returns `Error`, so the boot is refused at preflight and `SkipPreflight`
cannot skip it. The refusal rides the preflight rather than being raised where the bootstrap ran because the
bootstrap runs first: when the storage is down altogether, the storage validator's diagnosis is still the
first row of the refusal.

A failure the bootstrap itself reports — no Owner configured for the initial team, a refused dev-admin
fallback — is unchanged: logged, and the boot continues. A runtime fault after boot is unchanged too.

**If you call `ComposeTeamRuntime.runBootstrapTeam` yourself** (the SDK's `compose` no longer does), move to
`ComposeTeamRuntime.bootstrapTeam services …`. The deprecated helper has no service collection to carry the
fault to, so it raises `BootstrapRefused` where the bootstrap ran. It is removed in 1.0.

## 3. Two sealed knobs render their case name

The composition manifest sealed `JobScheduler` and `DataSubjectRequests` as `string` on the union — the `%A`
rendering, payload included — so a field added to `QuartzConfig` or `DataSubjectRequestConfig` would have
changed the sealed bytes of every deployment on that case. Both knobs now render the case name only
(`CompositionManifest.caseKnob`):

| Knob | Case | Sealed value before | Sealed value now |
|---|---|---|---|
| `JobScheduler` | `NoJobScheduler`, `InProcessJobScheduler` | the case name | **unchanged** |
| `JobScheduler` | `QuartzJobScheduler of QuartzConfig` | `QuartzJobScheduler` + the config record | `QuartzJobScheduler` |
| `DataSubjectRequests` | `Disabled` | `Disabled` | **unchanged** |
| `DataSubjectRequests` | `Enabled of DataSubjectRequestConfig` | `Enabled` + the config record | `Enabled` |

A binding sealed before this phase on a payload case still compares: `BootVerificationPreflight.compare` reads
the old rendering — the case name followed by whitespace and the payload — as the case it names. No schema
version was spent on it, because the old rendering identifies itself and a version bump would have moved the
canonical bytes of every default deployment. The payload is no longer covered by the seal; re-seal when
convenient and the old values disappear.

## 4. Every hosted service meets the profile gate or says why

Every `IHostedService` registration either consults `ProcessProfileGate.shouldRegisterBackgroundService` or is
registered through `ProcessProfileGate.addExempt` (or beside a `ProcessProfileGate.exempt`), which records a
`HostedServiceExemption` — the service and the reason the matrix does not govern it — in the service
collection. `HostedServiceGateTests` holds the registrations to that.

**No service's profile placement changed.** Each of the sites the redesign pass found ungated was examined, and
each registers on every profile for a reason that holds, now written down:

| Service | Reason it registers on every profile |
|---|---|
| Backup / restore-drill jobs, data-object orphan sweep, AI conversation retention, KB retention sweep, calendar sync, reactive fact recomputation, report rendering | scheduler registrations: each hands handlers and triggers to `IJobScheduler`, whose own `JobSchedulerSubsystem` gate decides where the work runs |
| Model-fit, DSR export / erasure, tenant-lifecycle, fact-publication, grounded-narrative and narrative-regenerate handler registrations; fact coherence scheduling | the same: handler registrations, placed by the scheduler's gate (the startup registrations now share one object expression, `ProcessProfileGate.atStart`) |
| RAG ingestion and re-embedding | the in-process ingestion queue is drained only by the process that accepted the upload, so gating it off a profile would strand every job that profile's HTTP tier enqueued |
| RAG ingestion recovery sweep | it settles the in-process jobs a restart lost, so it runs wherever ingestion runs |
| RAG retrieval-store flush | it flushes the stores this process opened at its own stop |
| AI chat worker | an agent turn is bound to the process that accepted the request |
| IndexNow startup submissions | the web tier is the one tier certain to compose the site |

`ProcessProfileGate`'s documentation used to say `DispatcherOnly` runs no RAG ingestion; it ran it. The
documentation now says what the code does: RAG ingestion is not a case of the matrix, for the reason above.
Placing RAG ingestion by queue durability is a later phase's work.

## Builder order (`withConfig`, `withExtensions`) — not in this release

The redesign pass found that `ServerApp.withConfig` and `ServerApp.withExtensions` replace what earlier builders
accumulated into `Config` and `Extensions`. The fix needs an `Overrides` record on `ServerApp`, which widens the
record — a breaking change to the draft slot this release rides — so it moves to the composition-as-
contributions phase. Until then, call `withConfig` and `withExtensions` **before** the builders that write into
`Config` or fold into `Extensions.ServiceConfig`.
