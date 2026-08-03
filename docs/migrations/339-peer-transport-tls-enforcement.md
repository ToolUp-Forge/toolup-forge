# Peer transport TLS enforcement

**Ships in:** ToolUp.InterPlatform (Phase 339).

**This can refuse a peer that previously worked.** If any peer in your
federation is configured with an `http://` `BaseUrl` on a non-loopback host,
every call to it now fails until you either move that peer to `https://` or opt
out explicitly. Read [Rollout order](#rollout-order) before upgrading.

`http://localhost` (and `127.0.0.1`, `[::1]`) is **accepted unchanged, with no
opt-out** — the dev inner loop and local compose setups are unaffected.

---

## What changes

Every outbound peer leg mints a fresh HS256 bearer from `IPeerAuthProvider` and
puts it in an `Authorization` header. Until this phase, all four of them built
their request URL from `target.BaseUrl` **verbatim, with no scheme check**:

| Leg | Where |
|---|---|
| Contract invoke | `HttpPeerClient.Invoke` |
| Long-running job poll | `HttpPeerClient.PollJob` |
| Capability handshake | `PeerCompose`'s `GET /peer/v1/capabilities` |
| Capability-profile fetch | `PeerRemoteProfile.fetch` |

So an `http://` peer put a token that vouches for the **whole deployment** on
the path in the clear. A peer token is not a session cookie: one observation is
peer impersonation against every receiver that trusts the same issuer, until the
signing key rotates.

The accept rule is the one `isAcceptableKeyFetchUrl` already applies to OIDC
JWKS fetches, deliberately — **https anywhere, http to a loopback host**:

```fsharp
PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults "https://peer.example.com"  // true
PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults "http://localhost:13001"    // true
PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults "http://peer.example.com"   // false
```

A URL the substrate cannot classify (relative, empty, `ftp://`) is refused too —
a string it cannot parse is a string it can promise nothing about.

### Where the gate sits

Four layers, mirroring the three `isAcceptableKeyFetchUrl` uses on the OIDC
side:

1. **At the top of `Invoke` / `PollJob`**, *before* `IssuePeerToken`. A refused
   call never mints a token at all, so there is nothing to leak or replay.
2. **Inside `HttpPeerClient.send`**, the single choke point every request passes
   through, against the **full** request URL. This is what makes "no peer
   request leaves this transport in the clear" a property of the transport
   rather than of each method remembering to call the gate.
3. **At both handshake fetches**, on the same before-the-token terms. The
   capability fetch is usually the *first* call made to a newly configured peer,
   so it is where a cleartext `BaseUrl` surfaces earliest.
4. **At `BlobPeerRegistry.Register`**, so the peer directory will not *record* a
   peer the transport would never call.

`BlobPeerRegistry.Resolve` / `List` are deliberately **not** gated. A directory
written before this phase may hold a cleartext peer, and making that entry
vanish on read would be far harder to diagnose than a call that refuses and
names the URL. The read path is byte-for-byte pre-339.

### What this is not

It is a check on **where a token may be sent**, not a replacement for anything
the platform does once it is sent there. Certificate-chain validation, hostname
verification and revocation stay entirely with `HttpClient`'s handler; nothing
in this phase disables them, relaxes them, or adds a knob that does. A
deployment that needs a private trust anchor installs it in the host's
certificate store, where it is auditable.

## What you must do

**If every peer you call is `https://` or loopback: nothing.** No API you call
changed shape, and behaviour is identical.

**If you have a non-loopback `http://` peer**, pick one, in this order of
preference:

1. **Move the peer to `https://`.** The right answer, and usually the only work
   is a certificate and a `BaseUrl` edit on both sides of the pair.
2. **If the peer is genuinely local, address it as `http://localhost:PORT`**
   rather than by a machine name. Loopback needs no opt-out.
3. **Opt out explicitly**, for the one shape it is written for — a peer
   reachable only over cleartext where the path is already trusted by other
   means (a private link, a service mesh terminating TLS at a sidecar):

   ```fsharp
   app
   |> PeerServerApp.withInsecurePeerTransport
   ```

   The opt-out is loud on purpose: the deployment logs one `Warn` per start
   naming the posture and the lever that drops it.

**Ordering hazard with the opt-out.** `withInsecurePeerTransport` sets a field
on `TransportPolicy`, and `withTransportPolicy` replaces that record whole — so
this **silently re-enables enforcement**:

```fsharp
// WRONG — the opt-out is discarded.
app
|> PeerServerApp.withInsecurePeerTransport
|> PeerServerApp.withTransportPolicy (PeerTransportPolicy.defaults |> PeerTransportPolicy.withCallTimeout t)
```

Either call `withTransportPolicy` **first**, or build the policy whole:

```fsharp
app
|> PeerServerApp.withTransportPolicy (
    PeerTransportPolicy.defaults
    |> PeerTransportPolicy.withCallTimeout t
    |> PeerTransportPolicy.allowInsecureTransport
)
```

### Reading the refusal

A refused call returns `PeerTransport` carrying a stable prefix, classified with
`PeerTransportSecurity.isRefusal` — the same convention Phase 312's timeout uses,
and for the same reason (a new `PeerError` case would break every exhaustive
match in every consumer for a distinction that never crosses the wire). A
handshake refusal is `HandshakeRejected` carrying the same message. The message
names the URL, what to change it to, that loopback needs no change, and the
opt-out.

```fsharp
match! peerClient.Invoke(target, "reach", "Query", payload) with
| Error e when PeerTransportSecurity.isRefusal e -> // cleartext BaseUrl — config defect
| Error e when PeerTransportOutcome.isTimeout e   -> // Phase 312 deadline
| Error e                                          -> // the peer's own failure
| Ok result                                        -> ()
```

## Source-compatibility notes

Two signatures widened. Both are additive at every call site that uses the
supported shapes:

- **`PeerTransportPolicy` gained `AllowInsecureTransport: bool`.** Code that
  builds a policy from `PeerTransportPolicy.defaults` / `unbounded` /
  `withCallTimeout` is unaffected. Code that constructs the record *literally*
  (`{ CallTimeout = ... }`) must add the field — prefer the module helpers.
- **`PeerRemoteProfile.fetch` takes a `PeerTransportPolicy`** as its second
  argument, after the `HttpClient`. Only a deployment that drives the profile
  fetch by hand is affected; the composed handshake threads it through itself.
- **`BlobPeerRegistry` gained a two-argument constructor.** The one-argument
  `BlobPeerRegistry(blobs)` survives as a secondary constructor on
  `PeerTransportPolicy.defaults` — an explicit overload rather than an optional
  argument, per the Phase 312 discipline.

## Rollout order

The check is **entirely caller-side**. A receiver observes nothing, so the two
halves of a peer pair are independent and there is no coordinated cutover.

1. **Inventory first, before upgrading anything.** List every `TargetPeer` your
   deployment calls — composed targets *and* the blob-backed peer directory,
   which is where a cleartext peer is easiest to miss:

   ```fsharp
   let! peers = registry.List()

   peers
   |> List.filter (fun p ->
       not (PeerTransportSecurity.isAcceptablePeerUrl PeerTransportPolicy.defaults p.BaseUrl))
   ```

   Anything that comes back is a peer that will refuse after the upgrade.

2. **Fix or opt out for each one**, per [What you must do](#what-you-must-do).
   Do this in the same change as the upgrade, not after it.

3. **Upgrade.** Receivers can be upgraded in any order, at any time.

4. **Retire the opt-out** as each cleartext peer moves to `https://`. The
   startup `Warn` is the reminder.

## See also

- [Phase 312 — transport timeout + cancellation](312-peer-transport-timeout-cancellation.md)
- [Phase 309 — audience-binding enforcement](309-peer-audience-binding-enforcement.md)
- [Phase 343 — peer robustness roundup](343-peer-robustness-roundup.md)
