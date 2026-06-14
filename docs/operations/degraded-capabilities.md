# Degraded capabilities — the "boots fine but a capability is down" signal

Some platform capabilities are wired **best-effort** at startup: the
composition root tries to enable them, but a failure must not crash the
deployment (the rest of the server is fine without them). Before this was
observable, such a failure disappeared into a swallowed exception — the
server booted green while a capability was silently down until the next
process restart.

The **degraded-capability registry** closes that gap. A best-effort site
that fails registers a named entry; a later success clears it. The set is
surfaced on three read surfaces so operators never have to reconstruct it
from logs:

| Surface | Where | Gate |
|---|---|---|
| `/health` | A `degraded` array on the JSON payload | Unauthenticated — **emitted only when non-empty** |
| `/dev/inspect` | A `Degraded capabilities` panel | `EnableDevEndpoints` |
| Health Monitor admin UI | A card on the **Live health** tab | `PlatformRole.PlatformAdmin` |

Each entry carries: `capability` (stable id), `degradedSince`, `reason`
(what failed), `impact` (what is broken/unsafe while degraded), and
`remediation` (what to do).

## Zero footprint when healthy

On a healthy deployment the registry is empty, the `/health` payload is
**byte-for-byte identical** to before (no `degraded` key is emitted), and
the admin/dev panels render nothing. The cost is paid only when something
is actually degraded.

## The operator contract

**Alert on a non-empty degraded set.** The deployment can be `Healthy`
on `/health` (the readiness status is unchanged — a degraded capability
does not trip `/ready` to 503 by itself) while a security- or
correctness-relevant capability is down. The named entries answer
"what exactly is degraded?" without log archaeology.

Suggested alerting: poll `/health`, parse the optional `degraded` array,
and page when its length is `> 0`. Because the array is omitted entirely
when empty, "key present" is itself the alert condition.

Note the registry is **per-process**: an entry records that *this
instance* failed to wire something at *this boot*. Across a fleet, the
degraded instance shows the entry while a cleanly-wired instance shows an
empty set — so alert per-instance, not just on an aggregate.

## Shipped registrations

| Capability id | Registered by | Impact while degraded |
|---|---|---|
| `crypto-shred-cache-eviction` | Per-scope encryption-key resolver failing to subscribe to the distributed notification channel at compose | A destroyed (crypto-shredded) encryption key keeps decrypting on every *other* silo until that silo restarts — cross-silo cache eviction is not happening. Single-silo deployments are unaffected. |

A successful (re)subscribe on a later boot clears the entry. The
preflight validator covers the *misconfiguration* case (distributed
resolver + in-process channel); this registry covers a *failed subscribe
on a correctly-configured channel*.

## Registering a capability from your own best-effort wiring

Resolve the registry from DI and register on failure / clear on success:

```fsharp
match services.GetService(typeof<DegradedCapabilities.DegradedCapabilityRegistry>) with
| :? DegradedCapabilities.DegradedCapabilityRegistry as degraded ->
    try
        wireMyBestEffortThing ()
        degraded.Clear "my-capability-id"
    with ex ->
        logger.Error("[MyThing] best-effort wire failed", Some ex)

        degraded.Register {
            Capability = "my-capability-id"
            DegradedSince = System.DateTimeOffset.UtcNow
            Reason = sprintf "wire failed: %s" ex.Message
            Impact = "what stops working while this is down"
            Remediation = "what an operator should do to restore it"
        }
| _ -> ()
```

Register **only** for failures that leave a real capability down — not
for expected, high-volume runtime conditions (e.g. a write to an
already-disconnected SSE client), which should stay silently handled.
