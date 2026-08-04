# Appliance deployment — a single-tenant container running in situ

An **appliance** is a deployment of this SDK that runs inside somebody else's
infrastructure, on their data, and is operated remotely by whoever supplied it.
The customer's security team can point at the container. The supplying party
cannot.

Every other deployment posture in this SDK describes *where* a process runs
(`ServerlessHost`, `ProcessProfile`) or *how many* of it there are
(`ReplicaCount`). None of them describes a deployment that **cannot reach the
party operating it**, and that single constraint changes four things at once:

| Constraint | What it forces | Surface |
|---|---|---|
| No outbound reach at boot | Nothing on the startup path may depend on a remote call | `ApplianceProfile`, `ApplianceBootPosture` |
| Builds arrive as files, not pulls | Authenticity must be established from the artefact itself, before the flip | `ApplianceUpgrade` |
| Health must get out; content must not | An outbound channel that structurally cannot carry data | `OperationalTelemetryDiode` |
| Support without possessing the data | The operator generates, inspects, and forwards; nobody pulls | `ApplianceSupportBundle` |

**It is a profile, not a default.** `ApplianceProfile.identity` is
`ConnectedAppliance`, every registration helper is a no-op under it, and there is
no new `ServerConfig` field. A deployment that never mentions any of this composes
a byte-for-byte identical `services` (GP 11 / GP 13).

## Contents

- [The posture](#the-posture)
- [Offline-tolerant boot](#offline-tolerant-boot)
- [Clock skew](#clock-skew)
- [The upgrade runbook](#the-upgrade-runbook)
- [The telemetry diode](#the-telemetry-diode)
- [The redacted support bundle](#the-redacted-support-bundle)
- [What the supplying party can and cannot see](#what-the-supplying-party-can-and-cannot-see)

## The posture

```fsharp
// The identity — an ordinary networked deployment. Every helper is a no-op.
let connected = ApplianceProfile.identity

// A declared-offline appliance with the conventional five-minute clock allowance.
let appliance = ApplianceProfile.offline

// …or with a drift the operator has actually measured.
let drifting = ApplianceProfile.offlineWithSkew (System.TimeSpan.FromMinutes 15.0)
```

`DeclaredOffline` is a **declaration, not a probe**. Detecting "am I offline?" at
startup would require reaching for something, which is the behaviour being
eliminated — and a probe that fails is indistinguishable from a probe that is
slow, so the appliance would either block on it or guess. The operator knows the
answer and says it.

## Offline-tolerant boot

The problem is the Phase 9m `IConfigValidator` preflight. A storage sentinel, an
OIDC discovery fetch, an SMTP connect — each returns `Error` when it cannot reach
its dependency, and any `Error` aborts startup. On an appliance with no outbound
network, that abort is describing the deployment's own declared topology rather
than a fault.

**The fix decorates the validator instances; it does not touch the aggregator.**

```fsharp skip=fragment
// Call this LAST, after every companion has registered its validators — the
// same ordering constraint ConfigValidatorAggregator.validate carries.
services
|> ApplianceBootPosture.offlineTolerantRegistration profile
|> ApplianceBootPosture.serviceRegistration profile requirementsSignature manifest
```

`offlineTolerantRegistration` rewrites every **external-probe-class** validator
into an `OfflineTolerantValidator`, which downgrades an `Error` to a `Warning`
naming the posture that downgraded it. The original message survives, so the
operator still learns what could not be reached.

**A security-class or structural-class validator is never decorated.** The class
is read from the validator itself via `ConfigValidatorAggregator.classify`, which
type-tests the `ISecurityClassValidator` / `IStructuralClassValidator` markers, so
a guard authored tomorrow is excluded without anyone remembering to exclude it.
Being offline is a reason a sentinel cannot answer; it is not a reason to boot with
a CSRF hole or with colliding component ids.

**Three ways a probe fails on an unreachable network, and all three are caught.**
Returning `Error` is only the tidiest:

1. It **returns `Error`** — pattern-matched.
2. It **throws**, which is what a real socket does. The aggregator catches throws
   itself and converts them to `Error`, so a decorator that only matched the
   returned value would let the *most likely* failure mode straight through to an
   abort.
3. It **hangs** — a TCP connect to an unroutable address commonly does. Here the
   decorator cannot rely on the aggregator's timeout, because F# async
   *cancellation* is not an exception and does not pass through a `try`/`with`:
   the aggregator's token would fire, the outcome would be
   `Error("validator exceeded timeout")`, and the boot would abort. So the
   decorator imposes its own bounded wait via `Async.StartChild` (whose expiry
   *is* a catchable `TimeoutException`) and reports a slightly longer `Timeout`
   upward, clamped below the aggregator's 10-second global budget.

### The endpoint checklist

`ApplianceBootPosture` also registers a structural-class validator that joins the
Phase 432 requirements manifest against the composed manifest and reports every
**URI-typed config knob** a composed component declares.

This is a **warning**, not a boot refusal, and the distinction is load-bearing: a
URI knob on an offline appliance is usually correct — the database, the object
store and the identity provider all live in the same customer network, and every
one of them is a URI the deployment must supply. What the rule can honestly say
is "these are the endpoints that must resolve from inside the container, and
nobody has confirmed they do". Failing boot on it would refuse every real
appliance.

Rule code: `appliance-offline-external-endpoint`, severity `DefectWarning`, class
`StructuralRule`. It exports in the same `CompositionRuleDescriptor` /
`ClassifiedCompositionRule` vocabulary as every other rule family, so an external
pre-build checker reads one vocabulary across all of them.

### Cold boot with no network

A composition under `DeclaredOffline` boots with every external probe failing —
returning, throwing, or hanging. The test pack proves this by running the **real**
aggregator over a rewritten `IServiceCollection`, with a connected-posture control
in the same list confirming that the same composition *does* abort without the
posture. Without the control, "it booted" would prove nothing.

## Clock skew

An air-gapped appliance frequently has no NTP peer it is allowed to reach, so its
clock drifts. Every default freshness window in this SDK — notably the Stripe
webhook verifier's five minutes and the JWT peer layer's — was chosen for a
deployment that syncs.

```fsharp
let appliance = ApplianceProfile.offlineWithSkew (System.TimeSpan.FromMinutes 15.0)

// Widen an existing freshness window rather than hand-editing a constant.
let webhookWindow =
    ApplianceProfile.widenWindow appliance (System.TimeSpan.FromMinutes 5.0) // → 20 minutes
```

`ApplianceProfile.withinSkew` is **symmetric** — it compares the absolute
difference — because an appliance clock runs behind as easily as ahead, and a
one-sided allowance would reject half the drift it was added to absorb.

`ApplianceProfile.identity` carries `TimeSpan.Zero`, i.e. exactly today's
behaviour. A negative declared tolerance is clamped to zero rather than inverting
the comparison.

## The upgrade runbook

A build arrives as a file. Its authenticity is established from the artefact
itself against its Phase 182 provenance — the artefact digest, the SBOM digest,
and the detached JWS.

### Wiring the verifier

Verification is a **structural function seam**, not an interface over a crypto
stack — the same GP 1 decoupling the Phase 182 SBOM emitter uses on the emitting
side. The SDK core carries no signing implementation; a deployment that has
composed an artefact verifier adapts it at its own call site:

```fsharp skip=fragment
let verify : VerifyDetachedJws =
    fun bytes jws -> async {
        let signature = { KeyId = keyId; Algorithm = alg; SignedAt = signedAt; DetachedJws = jws }
        let! result = verifier.Verify(bytes, signature)
        return result |> Result.mapError VerificationError.describe
    }
```

### On start

```fsharp skip=fragment
// Calling this IS the opt-in — a deployment that does not verify its own
// artefact registers nothing (GP 13).
services |> ApplianceUpgrade.serviceRegistration verify runningArtefactSource
```

The registered validator is **security-class**, so `ServerConfig.SkipPreflight`
does **not** bypass it. An emergency boot taken to ride out a dependency outage is
a legitimate operator choice; booting an artefact that does not match the
provenance it shipped with is not the same kind of decision, and one lever must
not cover both.

An artefact whose provenance cannot be *resolved* — no sidecar, an unreadable
mount — is a **failed** verification, not a skipped one. An appliance that cannot
locate its own provenance has proved nothing about itself.

### Both digests are verified, and the second is not redundant

The artefact digest answers *"are these the bytes that were signed"*. The SBOM
digest answers *"is this the dependency set that was reviewed"*. An appliance that
verified only the artefact would accept a rebuild whose accompanying SBOM had been
swapped for a cleaner one — and the SBOM is the artefact the customer's own
supply-chain policy reads.

Digests are compared **case-insensitively**: refusing an artefact over the case of
a hex digit would be refusing for the wrong reason, and the operator would have no
way to tell.

### Refusal names the mismatch

The acceptance criterion is not "refused" but "refused with the provenance
mismatch named". An operator who cannot tell a corrupted download from a
wrong-version file from a revoked key has to escalate to the supplying party to
make any progress — which is the dependency an appliance exists to avoid.

`verifyArtefact` returns **every** mismatch, not the first, so one run gives the
whole picture:

```
Artefact provenance verification REFUSED for ToolUp.Platform.Server 0.9.4:
  • artefact digest mismatch: provenance declares sha256 4f3a…, the artefact bytes hash to 91c7…
  • SBOM digest mismatch: provenance declares sha256 be22…, the SBOM bytes hash to 0d15…
  • detached-JWS signature rejected: no verification key for key id: appliance-2026-01
The running build does not match the provenance it shipped with. Do not flip to it; return to the previously verified artefact.
```

A verifier that **throws** is a refusal, never a fall-through to `Ok`.

### The staging check: verify → migrate-preview → flip

```fsharp skip=fragment
let! report = ApplianceUpgrade.stage verify probes previousVerified candidate
printfn "%s" (ApplianceUpgrade.describeStaging report)

if UpgradeStage.mayFlip report.Stage then
    // The flip is a container-runtime action taken by the operator.
    ()
```

| Stage | Meaning |
|---|---|
| `ProvenanceRefused` | Verification failed. Nothing else ran. |
| `MigrationBlocked` | Verified, but the incoming version needs something this appliance has not been given. |
| `ReadyToFlip` | Verified and fully provisioned. |

**Provenance is checked before the migration preview, deliberately.** The preview
reads the candidate's declared Phase 432 requirements, and reading declarations
out of an artefact whose authenticity is unproven is the same trust mistake in a
smaller frame.

**The migrate preview is pure and offline.** It asks two injected probes whether
each required name is *present* — `MigrationProbes.SecretPresent` and
`.ConfigBound` both return `bool`. Neither returns a value. That mirrors the Phase
432 constraint that a requirement type has no field a secret's value could occupy;
the preview must not reintroduce one through the back door of a probe signature.

Only `RequiredRequirement` secrets and default-less knobs are reported. An
*optional* credential's absence degrades a component, which is a legitimate
configuration and not a blocker for a flip.

```
Provenance VERIFIED for ToolUp.Platform.Server 0.9.5, but the incoming version needs 2 requirement(s) this appliance has not been given:
  • companion:IBlobStorage requires _platform/STORAGE_KEY (api-key) — authenticates to the object store
  • companion:IBlobStorage requires endpoint (uri) — the object-store endpoint
Provision these, re-run the staging check, then flip.
Rollback target: ToolUp.Platform.Server 0.9.4 (previously verified).
```

### Rollback

**Rollback is the previously verified artefact, and nothing else.** The staging
report carries it as `RollbackTo`.

`None` means there is no verified predecessor — a first install, or an appliance
whose running build was never verified — and in that state a flip is **one-way**.
The report says so explicitly, so the operator learns it before the flip rather
than during an incident:

```
Rollback target: NONE — there is no previously verified artefact, so this flip is one-way. Verify the running build first if you need a rollback path.
```

**Nothing in this SDK flips anything.** `stage` produces a report and a verdict;
the switch is a container-runtime action taken by whoever operates the appliance,
deliberately outside this SDK's reach. An upgrade path that could flip itself
would be a remote-callback startup dependency wearing a different hat.

## The telemetry diode

The supplying party needs to know whether the appliance is healthy: which version
is running, whether preflight passed, roughly how much work it is doing. That is a
legitimate need and a dangerous channel — every "send us some diagnostics" pipe
ever built has, sooner or later, carried a row of customer data out with it,
because the pipe's payload was a string and a string will hold anything.

**The diode closes that structurally rather than by policy.**

### The closed schema

`OperationalTelemetryFrame` has **no `string` field anywhere in its transitive
closure**. Every value is an integer, a boolean, or a case of a closed DU whose
cases are all nullary.

```json
{
  "schema": 1,
  "version": { "major": 0, "minor": 9, "patch": 4 },
  "uptimeSeconds": 3600,
  "atUnixSeconds": 1780000000,
  "health": [
    { "subsystem": "storage", "state": "degraded" },
    { "subsystem": "platform", "state": "healthy" }
  ],
  "preflight": [
    { "class": "external-probe-class", "outcome": "warning", "validators": 3 }
  ],
  "counters": [
    { "counter": "requests-served", "value": 41233 },
    { "counter": "upgrade-refusals", "value": 1 }
  ]
}
```

Every string you see there is a **wire token from a closed vocabulary declared in
the SDK's own source** — `DiodeSubsystem`, `DiodeHealthState`,
`DiodeValidatorClass`, `DiodePreflightOutcome`, `DiodeCounter`. None of them is a
value a deployment chose.

Three consequences worth stating, because each is a place the obvious design
leaks:

- **Health reports by SUBSYSTEM, not by `ComponentId`.** A component id is a
  string, and it is not obviously content until a deployment names one after a
  customer.
- **Preflight reports by validator CLASS, not by name.** A validator's `Name` is
  deployment-chosen, and the documented convention for a multi-instance validator
  suffixes the instance — `"oidc-auth (https://login.northwind.example)"` is an
  internal hostname. The outcome also carries **no message**;
  `ValidationResult.Warning` and `.Error` both carry text that routinely quotes a
  connection string or a sentinel blob name.
- **Version is three integers.** This is the one field where a string looks
  unavoidable and is not. Pre-release and build metadata are dropped rather than
  carried: the operating party needs to know which release is running, not which
  CI run produced it.

This is verified by **reflecting over the type's transitive closure** in the test
pack, so a future field that would open the channel fails the build rather than
passing review. The same walk is falsified against a deliberately-open control
record in the same test list — a closure check that had stopped matching anything
would otherwise report closure just as happily.

The rendering is written field-by-field through `Utf8JsonWriter` rather than
serialised reflectively, for the same reason the schema is closed: a reflective
serialiser writes whatever the type happens to carry, so a future field would ship
silently. Two appliances in the same state produce **byte-identical** payloads.

### Why closed rather than redacted

The support bundle redacts: it takes arbitrary content and masks what it
recognises. Redaction is the right tool when the payload is genuinely open and an
operator inspects the result before it leaves.

It is the wrong tool for an automated outbound channel, where nobody reads each
frame and an unrecognised field name means content ships. A closed schema inverts
the default: a field that does not exist cannot leak.

### Consent

**Default off, and off means nothing is sent — not an empty request.**

```fsharp
// The default and the identity. `transmit` does not invoke the outbound
// function at all, so no connection is opened and no name is resolved.
let withheld = DiodeWithheld

// Consent is per-section, not a boolean: agreeing to report health states is
// not thereby agreeing to report throughput.
let granted =
    DiodeGranted {
        GrantedAtUnixSeconds = 1779000000L
        Sections = [ HealthSection ]
    }
```

The header — schema, version, uptime, timestamp — always rides when consent
exists at all. It is the irreducible "alive, on this build", and it contains no
deployment-chosen value. A grant covering no sections transmits the header only.

### The outbound seam and the local journal

```fsharp skip=fragment
let! outcome = OperationalTelemetryDiode.transmit consent journal send now frame
```

`send` is a `DiodeTransmit = string -> Async<Result<unit, string>>` — a structural
function, not an interface over a transport (GP 1). Whatever ships the payload (an
HTTPS POST, a file drop the operator forwards by hand, a message on a queue the
customer already runs) is the deployment's choice.

**Every frame is journalled locally**, including the suppressed ones, through
`IDiodeTransmissionLog`. The journal holds the **exact payload bytes that left** —
an operator audits the payload, not a description of it. A suppressed frame
journals `Payload = None`, which is the difference between *"we sent this and it
was empty"* and *"we sent nothing"*, and an operator auditing the channel needs to
tell those apart. `OperationalTelemetryDiode.bytesTransmitted` derives the total
from the journal rather than a separate counter, so it cannot disagree with the
record of what left.

The shipped `DiodeTransmissionJournal` is a bounded in-memory ring (500 frames by
default) so a long-running appliance cannot journal itself out of memory. An
appliance wanting the journal to survive a restart wires an
`IDiodeTransmissionLog` over its own `IEventStore` / `IAuditLog`.

A delivery failure — or a transport that throws — is journalled locally as
`DiodeFailed` and never transmitted. A telemetry channel must never be what takes
an appliance down.

## The redacted support bundle

The Phase 9n `/dev/bundle` archive is already redacted, against a four-suffix
credential allowlist (`apikey` / `token` / `secret` / `password`), and that is
right for its purpose: an operator pulls it from their **own** deployment for
their **own** support ticket, and its file header calls the redaction
"defence-in-depth".

An appliance inverts the trust direction. The party who would read the bundle is
not the party who owns the data, so redaction stops being defence-in-depth and
becomes the load-bearing guarantee — and a four-suffix credential list is not a
guarantee about *content*.

### The vocabulary is the deployment's own classifications

```fsharp skip=fragment
let vocabulary = ApplianceSupportBundle.vocabularyOf classifications
let masked = ApplianceSupportBundle.mask vocabulary sections

// A non-empty result means DO NOT FORWARD the bundle.
match ApplianceSupportBundle.survivingContentFields vocabulary masked with
| [] -> ()
| surviving -> failwithf "%d content-bearing field(s) survived masking" surviving.Length
```

The redaction list is the **Phase 41 / 188 field-classification vocabulary**:
every field whose `ClassificationLevel` is anything other than `Public`. The
deployment has already said which of its fields carry personal, financial and
regulated data, and a support bundle using a *different* definition of "sensitive"
than the entity store's own gate would be wrong in one direction or the other.

Two details that matter in practice:

- **`Confidential` is masked here, though the access gate admits it.**
  `ClassificationLevel.isSensitive` returns `false` for `Confidential` because any
  authenticated caller may read it. That judgement is about callers *inside* the
  deployment; a support bundle leaves it. Commercially confidential material is
  exactly what an in-situ customer is protecting, so the bundle masks it and the
  access gate does not, and the two are not in conflict.
- **A dotted field path masks under both spellings.** `Profile.HomeAddress`
  registers both `profile.homeaddress` and `homeaddress`: a nested JSON property
  carries only the leaf, while a flattened log line may carry the dotted path, and
  masking one spelling would leave the same value exposed under the other.

The Phase 9n suffix allowlist is kept as a **floor**, because it catches
credential-shaped property names in surfaces that have no entity classification at
all — a config tree, a dependency graph, a validator message. A deployment
declaring no classifications gets `ApplianceSupportBundle.floorOnly`, which is
still stricter than nothing.

### Section shapes

| Shape | Walk |
|---|---|
| `JsonSection` | Property-name walk over the parsed document |
| `JsonLinesSection` | The same walk per line, so one bad line does not mask the whole log |
| `Opaque` | Masked **wholesale** |

**Content that does not parse is masked wholesale, not passed through.** An
unparseable section is precisely the case where nothing is known about what it
contains, and an appliance bundle does not forward content it cannot walk.

A masked value is replaced by its length — `<masked:length=15>` — not a fixed
token. "This field was present and 15 characters long" is genuinely diagnostic (it
distinguishes an empty column from a populated one, which is often the whole
question) and a length is not the content. Same convention as the 9n bundle, so an
operator reading both sees one shape.

### The operator generates, inspects, and forwards

There is deliberately **no route, no endpoint, and no scheduled emission** here —
only pure functions over section content the operator has already collected.

"The supplying party never pulls" is therefore not a policy statement about this
module; it is the **absence of any mechanism that could**. The only outbound
channel an appliance has is the diode, and the diode's schema has no string field,
so a bundle cannot ride it.

## What the supplying party can and cannot see

The whole point of the posture, stated plainly enough to hand to a customer's
security review.

### Can see — only with consent, and only via the diode

| | |
|---|---|
| Which release is running | three integers; pre-release and build metadata dropped |
| Process uptime | seconds |
| Per-**subsystem** health | one of `healthy` / `degraded` / `unhealthy`, from a closed list of eight subsystems |
| Per-**validator-class** preflight outcome | one of `ok` / `warning` / `error`, plus a count. **No message.** |
| Coarse counters | from a closed list of seven; each a number |

Every one of those is off by default, gated per section, and journalled locally
with the exact bytes.

### Cannot see — structurally, not by policy

| | Why not |
|---|---|
| Any row, record, field value, or document | The diode schema has no `string` field anywhere in its transitive closure |
| Any customer, user, tenant, or team name | Same reason; health is reported by subsystem, not by `ComponentId` |
| Any hostname, endpoint, connection string, or blob name | Preflight is reported by validator class, and outcomes carry no message |
| Any credential | No requirement or preview type in this file has a field a secret's value could occupy; the migrate-preview probes return `bool` |
| Any log line, stack trace, or error text | Not in the schema |
| A support bundle | The bundle is text; the diode cannot carry text |

### Requires the operator to act, every time

| | |
|---|---|
| A support bundle | The operator generates it, inspects it, and forwards it. There is no endpoint to pull it from. |
| An upgrade | The operator runs the staging check and performs the flip. Nothing here flips itself. |
| Turning the diode on | Consent is explicit and per-section, and withheld is the default. |

## See also

- [`portability-rules.md`](portability-rules.md) — the six rules
  `IDiodeTransmissionLog` and the seams here are audited against.
- [`security.md`](security.md) — the platform security posture the appliance
  profile narrows.
- [`data-subject-requests.md`](data-subject-requests.md) — the other consumer of
  the Phase 41 / 188 classification vocabulary the support bundle masks against.
- [`events.md`](events.md) — `IEventStore` / `IAuditLog`, for an appliance wanting
  the diode journal to survive a restart.
- [`provenance-chain.md`](provenance-chain.md) — the wider provenance story the
  artefact verification sits inside.
