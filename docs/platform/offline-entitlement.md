# Offline entitlement verification — signed capability tokens

A deployment running inside somebody else's infrastructure cannot be gated by a
billing service it phones home to, because it may have no route to one. The
mechanism that works instead is the one TLS certificates and software licences
have always used: the party with the authority to grant signs a **statement of
what is granted, and for how long**, and the deployment verifies that statement
locally against a key it already holds.

`EntitlementToken` is that statement. It carries a capability set, a capacity
set, a validity window, and a holder id; it is verified against a **pinned**
public key with no network call in the path.

**Read this page for the guarantee, if nothing else:** no entitlement state — not
expiry, not a tampered token, not a wrong key, not the strictest posture a
deployment can declare — can withhold a customer's own data. That is enforced by
a type refusing to be constructed, not by a promise. See
[The data guarantee](#the-data-guarantee).

**It is a mechanism, not a licence.** forge imposes no entitlement of its own.
`EntitlementGovernance.none` governs nothing, a composition that never mentions
any of this registers nothing, and an unprovisioned deployment is fully unlocked
unless its operator declares otherwise (GP 11 / GP 13).

## Contents

- [The shape](#the-shape)
- [Capabilities are feature flags](#capabilities-are-feature-flags)
- [Capacity entitlements](#capacity-entitlements)
- [The lifecycle: active, grace, lapsed](#the-lifecycle-active-grace-lapsed)
- [The data guarantee](#the-data-guarantee)
- [Clock skew](#clock-skew)
- [Revocation](#revocation)
- [Refusals and what they mean](#refusals-and-what-they-mean)
- [The boot preflight](#the-boot-preflight)
- [Issuing a token](#issuing-a-token)
- [What an entitlement cannot do](#what-an-entitlement-cannot-do)

## The shape

Three types carry the whole model.

```fsharp
// What the issuer asserts, and signs.
let claims: EntitlementClaims = {
    HolderId = "deployment-7f2a"
    TokenId = "tok-0001"
    IssuedAt = System.DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero)
    NotBefore = System.DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero)
    ExpiresAt = System.DateTimeOffset(2026, 8, 30, 0, 0, 0, System.TimeSpan.Zero)
    Capabilities = Set.ofList [ "reporting.advanced-analytics"; "interplatform.peering" ]
    Capacities = [ { Dimension = "entitlement.seats"; Limit = 25L } ]
    GraceWindow = System.TimeSpan.FromDays 7.0
}

// The key this deployment will accept, by identity.
let pin: PinnedEntitlementKey = { KeyId = "issuer-2026-q3"; Algorithm = "ES256" }

// Which flag keys the entitlement is allowed to gate. Closed, declared.
let governance =
    EntitlementGovernance.declare [ "reporting.advanced-analytics"; "interplatform.peering" ]
```

`declare` returns a `Result` — see [The data guarantee](#the-data-guarantee) for
the one thing it refuses.

Verification arrives as the same `VerifyDetachedJws` function seam the signed
upgrade check uses (`byte[] -> string -> Async<Result<unit, string>>`), so the SDK
core carries no crypto stack (GP 1) and a deployment adapts its own verifier at
its own call site:

```fsharp skip=fragment
let verify: VerifyDetachedJws =
    fun bytes jws -> async {
        let signature = Convert.FromBase64String jws
        if publicKey.VerifyData(bytes, signature, HashAlgorithmName.SHA256) then
            return Ok()
        else
            return Error "ES256 signature did not verify over the presented claim bytes"
    }

let validation = EntitlementValidation.create pin verify governance
```

**The pin is enforced by that function's key material, not by the token.** A
token echoes the `KeyId` and `Algorithm` its issuer used, and those are compared
first — but only so a refusal can say *"signed with key `issuer-2025-q1`, this
host pins `issuer-2026-q3`"* instead of *"signature rejected"*. Treating a
self-asserted field as authority would be the same trust mistake as reading
configuration out of an unverified artefact. The verifier holds one key; that is
what makes the pin real.

## Capabilities are feature flags

Gated code checks a flag. It never sees a token, a claim set, a phase, or a
validity window.

```fsharp skip=fragment
// In a module. There is nothing entitlement-shaped here, and that is the point.
let! enabled = flags.IsEnabled "reporting.advanced-analytics" ctx
if enabled then renderAdvancedPanel () else renderUpgradePrompt ()
```

The composition root wires it once:

```fsharp skip=fragment
let! capped, budget, registerPreflight =
    EntitlementCompose.resolveAndCap validation presentedToken declaredFlags evaluator
```

`capped` is an ordinary `FlagEvaluator` with governed keys bounded by what the
entitlement grants. Resolution is `granted && whatever the scope walk said` — the
entitlement is a **ceiling**, never a value substitute, matching the `PremiumOnly`
flag-source precedent. A deployment that has switched a feature off for its own
reasons stays off whether or not it is entitled.

**Why a ceiling over the evaluator rather than an `IFlagSource`.** An
`IFlagSource` is consulted only when *no* in-process scope set the key, so a
single Platform-scope override would lift the entitlement entirely. An entitlement
a local admin toggle can switch off is not an entitlement. The ceiling composes
after the whole `User → Team → Platform → source → declared default` walk, so
there is no layer left that could override it — including `TryEvaluate`, so a
caller cannot route around `IsEnabled`.

Governed keys must be `Bool`. A governed `Variant` declaration is a composition
mismatch, not a gating question: it passes through uncapped and the preflight
warns by name, because silently coercing a variant to a boolean would pick an
option nobody declared.

## Capacity entitlements

Row, seat, and compute caps ride the same token and project to a typed read
surface:

```fsharp skip=fragment
match budget.Check "entitlement.seats" requestedSeats with
| Unbudgeted -> proceed ()                       // no limit declared — unbounded
| WithinBudget(limit, requested) -> proceed ()
| BudgetExceeded breach -> refuse breach          // the existing QuotaBreached shape
```

`BudgetExceeded` carries `QuotaBreached`, the same record the per-scope quota
policy returns, so a capacity entitlement and a quota breach report in one
vocabulary rather than two.

**The budget is read-only — there is no `Consume` and no counter.** Measuring
usage belongs to the usage log and enforcing it to the quota policy; an
entitlement keeping its own parallel tally would be a second answer to "how many
seats are in use", which is how the two drift and a customer gets measured
against the wrong one. This surface answers only *what the ceiling is*.

## The lifecycle: active, grace, lapsed

Verification answers one question — *is this statement authentic* — and an
expired statement is perfectly authentic. What changes at expiry is what the
statement **grants**, which is a separate, non-refusing axis:

| Phase | When | Governed capabilities | Capacity limits |
|---|---|---|---|
| `Unentitled` | no token, default posture | **all granted** | unbounded |
| `Active` | inside the window | as the token grants | as the token grants |
| `Grace` | past expiry, inside `GraceWindow` | **as the token grants — unreduced** | unchanged |
| `Lapsed` | past the grace window | reduced to `EntitlementFloor` | **unchanged** |

Two of those rows are deliberate and worth stating plainly.

**Grace is a full-capability state with a loud preflight, not a partial one.** A
reduction that begins quietly at expiry is discovered by users; a reduction
announced for days beforehand is discovered by the operator.

**A lapse does not zero the capacity limits.** Reduction acts through the
capability set and nowhere else — one gating model. A zero budget would present a
lapse as a *capacity breach*, which is a second and contradictory explanation for
one event.

## The data guarantee

`EntitlementFloor` holds the capabilities no entitlement state can withhold:

| Key | What it covers |
|---|---|
| `platform.data.read` | reading the deployment's own stored data |
| `platform.data.export` | exporting the deployment's own stored data |

`EntitlementGovernance.declare` **refuses** to govern either, naming the key and
the reason:

```fsharp
let refused = EntitlementGovernance.declare [ "platform.data.export" ]
// Error [ "'platform.data.export' is an EntitlementFloor capability and cannot be
//          governed by an entitlement. ..." ]
```

There is therefore **no representable configuration** — no token, no posture, no
lapse, no combination — under which a customer loses the ability to read or
export their own data. The guarantee holds for every downstream deployment
without any of them having to know it exists. `EntitlementGovernance.governs`
refuses a floor key at the read too, so a governance record assembled by some
future loader or deserialiser that bypassed `declare` still cannot reach the
floor.

A deployment can of course still turn export off through the ordinary
feature-flag scope walk, for its own reasons. What it cannot do is make that
switch answer to an entitlement.

**A mechanism that *can* hold data hostage and merely promises not to is a
different mechanism from one that cannot**, and the second is what a customer's
own security review can verify in an afternoon.

## Clock skew

`ClockSkewTolerance` is applied **in the holder's favour on both edges** of the
validity window: skew delays a lapse, and admits a token whose `NotBefore` is
barely in the future. A host with no reachable time peer drifts, and a drifting
clock must not manufacture an expiry.

An appliance that has already declared its drift does not restate it:

```fsharp skip=fragment
let validation =
    EntitlementValidation.create pin verify governance
    |> EntitlementValidation.withClockSkew (EntitlementValidation.skewFromApplianceProfile profile)
```

That bridge is a value read, not a dependency. **Entitlements are not
appliance-only** — the mechanism is generic offline licensing and behaves
identically on an ordinary networked deployment.

## Revocation

There is no CRL, no introspection call, and no revocation endpoint. On a host
that may have no route to anywhere, an unreachable revocation list fails either
open (useless) or closed (a lockout), and fetching one is exactly the phone-home
this mechanism exists to avoid.

**The validity window is the revocation mechanism.** The bound on how long a
withdrawn entitlement stays effective is precisely the token's lifetime, so that
lifetime is a declared, visible number:

```fsharp
let renewal: RenewalPolicy = {
    MaxTokenLifetime = System.TimeSpan.FromDays 90.0
    RenewalNotice = System.TimeSpan.FromDays 14.0
}
```

A presented token longer-lived than `MaxTokenLifetime` draws a preflight warning
naming both numbers and saying what it costs — never a refusal, because refusing
a valid token over a local policy preference would be a self-inflicted lockout.
Operationally: short tokens, renewed on a cadence, installed by the operator. A
token valid for a year is a token that cannot be revoked.

## Refusals and what they mean

Every refusal names what did not match. An operator who cannot tell a tampered
file from a wrong key from a clock problem has to escalate to make any progress,
and escalation is the dependency this mechanism exists to remove.

| Refusal | Means | Remedy |
|---|---|---|
| `KeyIdNotPinned` | signed with a key this host does not pin | update the pin, or obtain a token for this trust root |
| `AlgorithmNotPinned` | declares an algorithm this host does not pin | refused before any verification runs — algorithm substitution is an attack class, not a compatibility question |
| `SignatureRejected` | bytes do not verify | the claims were altered, the file is truncated, or the key material behind the pinned id does not match |
| `ClaimsUnparseable` | verified, but not well-formed claims | a format mismatch, not a trust problem |
| `ClaimsIncomplete` | a needed claim is absent, blank, or out of range | includes a negative capacity limit and a duplicate capacity dimension, both refused rather than resolved |
| `ValidityWindowInverted` | `NotBefore` is after `ExpiresAt` | a window that never opens; reissue |
| `NotYetValid` | the window has not opened, allowing for skew | a provisioning *ordering* problem, reported as itself rather than as an expiry |

Note what is **absent**: expiry. An expired token is authentic and resolves to a
phase, never to a refusal.

A refusal is not fatal either. `EntitlementValidation.resolveFailSafe` folds any
refusal into the same reduced state a lapse produces, and hands the refusal back
so the preflight can say exactly what happened. A deployment that cannot
establish its entitlement knows nothing about it, and the only fail-safe reading
of "I know nothing" is the floor — granting everything would make the mechanism
decorative, and refusing to boot would confiscate.

## The boot preflight

```fsharp skip=fragment
// Registering IS the opt-in. A deployment that gates nothing never calls it.
let services = registerPreflight services
```

The validator surfaces days-remaining, grace, lapse, the governance audit, and
the renewal advisory. It is **structural-class**, so `ServerConfig.SkipPreflight`
cannot silence it: an operator booting through a storage outage should not also
lose the line telling them their entitlement lapsed last week.

**It never returns `Error`.** A preflight `Error` aborts the boot, and a process
that will not start is the most complete way to withhold a customer's own data.
Every outcome is `Ok` or `Warning` — expiry, lapse, a tampered token, a wrong
key, an unparseable file, a clock problem, and a status source that throws. The
validator's job is to be loud, not to be fatal. That is asserted exhaustively in
the test pack across every refusal case and every phase, against a control
validator that *does* return `Error` so the assertion is known to be capable of
failing.

## Issuing a token

The entire issuing-side contract is one function. A party that can produce a
detached JWS over these bytes can issue tokens this SDK accepts, with no
dependency on it of any kind:

```fsharp skip=fragment
let signedBytes = EntitlementClaims.canonicalBytes claims
```

The canonical form is deterministic — fixed property order, capabilities sorted,
capacity grants sorted by dimension, timestamps normalised to UTC, no whitespace
— so an issuer and a verifier agree on a byte sequence without sharing a
serialiser. The UTC normalisation matters in practice: without it a token signed
on a host at `+01:00` fails verification on a host at UTC, for a reason no
operator could diagnose.

Assemble the token from the claims and the signature:

```fsharp skip=fragment
let token = EntitlementClaims.toToken keyId algorithm detachedJws claims
```

`toToken` canonicalises once, so the text the token carries is provably the text
that was signed. The token transports the claims as **text**, not as a
re-serialised record: parsing and re-canonicalising before verifying would make
every future serialisation change a silent signature break.

## What an entitlement cannot do

Stated plainly, because a customer's security review will ask:

- **It cannot withhold your data.** Reading and exporting are structurally
  ungovernable. See [The data guarantee](#the-data-guarantee).
- **It cannot stop the deployment starting.** The preflight has no `Error` path.
- **It cannot phone home.** No type in the verification path's transitive closure
  carries a `Uri`, a host name, or any other field a network call could be built
  from — checked by reflection in the test pack, falsified against a
  deliberately-networked control type. A fetch cannot be added without adding a
  field, and that walk would fail.
- **It cannot take the process down.** A verifier that raises is a rejection, not
  a crash: an entitlement check that can kill a process is a lockout mechanism
  with extra steps.
- **It cannot be imposed by this SDK.** An unconfigured deployment is fully
  unlocked, and the identity governance record governs nothing (GP 13).

## See also

- [`appliance-deployment.md`](appliance-deployment.md) — the in-situ deployment
  class this most often licenses, and the source of the `VerifyDetachedJws` seam
  and the declared clock-skew allowance.
- [`premium.md`](premium.md) — the operator-granted premium-tier substrate and
  the `PremiumOnly` flag source whose ceiling semantics this follows.
- [`composition-roots.md`](composition-roots.md) — where the wiring above goes,
  and the startup preflight aggregator the validator registers into.
