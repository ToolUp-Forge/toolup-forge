# Phase 727 — Attribute recognition beyond the auth classifier (consumer migration)

**What changes.** [Phase 335](335-qualified-auth-attribute-matching.md) moved the dispatcher's
*authorisation* classifier from bare simple-name matching to **CLR type identity**. Four other
attribute families riding the same dispatch path still matched by simple name. Three of them now
match by identity as well, and refuse composition on a name collision:

| Family | Attributes | Recognition after 727 |
|---|---|---|
| Audit emission | `[<Audit "…">]` | CLR identity + startup refusal on a name collision |
| PII-safe audit payload | `[<PiiSafe>]` | CLR identity + startup refusal on a name collision |
| Rate limiting | `[<RateLimit(n, w)>]` | CLR identity + startup refusal on a name collision |
| Idempotency | `[<Idempotent>]` | CLR identity + startup refusal on a name collision |
| **Validation** | `[<MinLength>]`, `[<Range>]`, … | **unchanged — simple name, deliberately** |

The two sanctioned families are the same two 335 named: `ToolUp.Remoting.Server.*` (server-tier) and
the tier-shared `ToolUp.Platform.*` mirrors in `ToolUp.Platform.Core` that Fable-compiled API records
carry.

The `Streaming.fs` diagnostic that refuses a streaming method carrying pre-flight attributes the SSE
short-circuit cannot enforce now agrees with the classifier about what counts as a marker, and picks
up a case it previously saw through neither path (below).

**Scope.** Server-side recognition only. No wire change, no route change, no data migration. Version:
minor bump under the SemVer-on-`0.x` policy.

## Am I affected?

Almost certainly not. If every marker on your records came from `open ToolUp.Remoting.Server` or
`open ToolUp.Platform` (or was written fully-qualified), recognition is byte-for-byte what it was —
GP 11. You are affected only if a record field carries an attribute whose **type name** is one of

```
AuditAttribute   PiiSafeAttribute   RateLimitAttribute   IdempotentAttribute
```

and whose type comes from somewhere other than those two families. The likely cause is an `open`
ordering accident: your own attribute, or one from another package, shadowing the sanctioned marker at
the point the record is declared. That compiled fine before and quietly meant something other than
what it looked like.

## Why each family, and why validation is not among them

The point of the phase was the assessment, not a mechanical extension — the four families differ in
what a forgery actually buys, and one of them is better left alone. The reasoning lives beside the code
(the module headers in `Audit.fs`, `RateLimit.fs`, `Idempotency.fs` and `Validation.fs`); in summary:

- **`[<PiiSafe>]` — the sharpest, and the only data-exposure case.** The predicate returning true is
  what *stops* a field being redacted: the value goes into the emitted audit row verbatim, and audit
  rows are replicated by every composed `IAuditSink` — straight out of the deployment's trust
  boundary. The attribute's documented contract is "fail-safe: forgetting it keeps PII out of audit
  rows", and simple-name matching meant a name the consumer never intended as ours could satisfy it.
- **`[<Audit>]`, `[<RateLimit>]`, `[<Idempotent>]` — availability and correctness.** A forgery that is
  *honoured* adds an unintended audit row, budget, or memoisation. That is the milder direction. The
  sharper one is the reverse, and it is why identity matching alone was not the whole fix: a consumer
  whose own attribute was being picked up by accident would, under a silent tightening, lose the guard
  with nothing anywhere saying so. **The startup refusal is the load-bearing half.**
- **Validation keeps simple-name matching, deliberately.** A validation attribute can only *add* a
  constraint — the family has no "skip validation" marker and violations accumulate rather than
  subtract — so a forgery cannot grant access, expose a value, or suppress a sibling validator; it can
  only produce extra, visible `400`s. Meanwhile its marker names (`MinLength`, `MaxLength`, `Range`,
  `Regex`, `Email`) collide with `System.ComponentModel.DataAnnotations`, which legitimately sits on
  the same consumer DTOs for EF Core column mapping and MVC model binding. A refusal there would break
  a correct deployment for no defect, and identity matching *without* a refusal would be a silent
  fail-open. So the status quo is the right answer, and this paragraph is why — recorded so the next
  sweep does not reach the opposite conclusion from consistency alone.

`ToolUp.Remoting.Analyzers/Recognition.fs` is deliberately untouched: it is a compile-time analyzer
over **source** names, with no CLR types to inspect. Its absence from this sweep is a decision, not an
oversight.

## The error you will see

```
ToolUp.Remoting refused to start: API record 'BillingApi' has 1 attribute(s) whose name matches a
dispatch pre-flight marker but which are NOT one of the two sanctioned families:
[Charge [idempotency] carries 'Contoso.Web.IdempotentAttribute, Contoso.Web, Version=1.0.0.0, ...'].
Only ToolUp.Remoting.Server.* (server-tier) and ToolUp.Platform.* (tier-shared mirror) attributes
arm rate limiting, idempotency, audit emission or PII-safe payload inclusion; an attribute of the
same name from any other namespace or assembly is refused rather than honoured, because a name
collision must never silently decide whether a guard is armed — in either direction. Replace it with
the sanctioned attribute of the same name, or rename your own attribute.
```

It fires at composition time (`Api.make` / `Remoting.buildHttpHandler`), before the first request.
Unlike 335's auth refusal it is **not** gated on an auth-context resolver being composed — these three
families arm independently of the auth system.

A `[<PiiSafe>]` collision names the method *and* the input-record field, because the marker sits on the
input record rather than on the API record:

```
[Read input field Email [PII-safe audit payload] carries 'Contoso.Web.PiiSafeAttribute, ...']
```

Only the input records of methods that are genuinely audited are scanned, one level deep — matching
exactly the reach of the payload builder that consults the marker.

## The fix

**If the guard was meant to apply**, qualify the sanctioned attribute:

```diff
-[<Idempotent>]                        // resolved to YOUR IdempotentAttribute
+[<ToolUp.Platform.Idempotent>]        // the tier-shared mirror
 Charge: ChargeRequest -> Async<Receipt>
```

or fix the `open` ordering so the sanctioned family wins, and confirm by reading the diagnostic
disappear rather than by assuming.

**Read a `[<PiiSafe>]` finding as an incident, not a rename.** A field that has been reaching the audit
payload un-redacted because of a name collision has been leaving the deployment in every replicated
audit row, whatever the record's author intended. Check what actually shipped to your sinks before you
make the refusal go away.

**If your attribute is unrelated** and merely shares a name, rename it — the engine cannot tell the two
apart, which is the whole finding:

```diff
-type PiiSafeAttribute() =             // collides with the marker
+type PiiReviewedAttribute() =
     inherit Attribute()
```

**If you deliberately declared your own type into `namespace ToolUp.Remoting.Server`**, note that
identity matching pins the *assembly* too, so it is still foreign — the same intentional rule 335 set.

## What did not change

- Both sanctioned families classify identically to pre-727 for audit, PII-safety, rate limiting and
  idempotency, including the mirror-to-server-tier budget normalisation (GP 11).
- Validation recognition is untouched in both directions.
- The streaming refusal is never weakened: a forged marker on a streaming method still flags the
  method, now rendered as a name collision rather than as a guard this SDK would have honoured. The one
  behavioural gain is a real defect closed — a tier-shared `[<ToolUp.Platform.RateLimit>]` on a
  streaming method matched neither the old type test nor any name arm, so the adapter started and the
  SSE short-circuit dropped the declared budget silently, on exactly the record family (Fable-compiled
  Core) that carries the mirror.
- `Audit`, `RateLimit`, `Idempotency`, `Streaming` and the new `MarkerFamily` mechanism are all
  internal; no public API surface moved.

## See also

- [`335-qualified-auth-attribute-matching.md`](335-qualified-auth-attribute-matching.md) — the auth
  classifier this extends, and the source of the mechanism.
- [`69h-audit-annotation-sweep.md`](69h-audit-annotation-sweep.md) — the audit family.
- [`69g-rate-limit-attribution.md`](69g-rate-limit-attribution.md) — the rate-limit family.
- [`69f-idempotency-keys.md`](69f-idempotency-keys.md) — the idempotency family.
- [`69e-typed-validation.md`](69e-typed-validation.md) — the family that keeps simple-name matching.
