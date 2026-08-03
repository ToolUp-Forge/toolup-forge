# Peer audience binding for contract hosts

**Ships in:** ToolUp.InterPlatform (Phase 309).

## What changes

[Phase 130](130-pkce-and-peer-audience.md) shipped the `aud` binding check in
`JwtPeerAuthProvider`, but it only fires when the validator was handed a
**non-empty** expected audience — and that value is `localIdentity.PeerId`, which
is `""` whenever `withLocalPeer` was not composed. A **host-only** deployment
(exposes contracts, never calls peers) is exactly the case documented as omitting
`withLocalPeer`.

That matters because `ValidatePeerToken` resolves the signing key from the
token's own `iss`. So a token peer X minted for receiver Y is accepted verbatim
by any other receiver Z that also trusts X — *unless* Z binds `aud = Z`. The
confused-deputy / cross-receiver-replay defence was therefore disabled on
precisely the contract-hosting deployments, with nothing said about it. The `aud`
claim was always minted; only the *check* was conditional.

Phase 309 makes that posture **visible**, and optionally fatal:

```fsharp
// The classification, derived from the composition record. Pure and total.
type PeerAudienceBinding =
    | AudienceBindingOff                          // PeerSubstrate = NoPeerSubstrate
    | AudienceBindingEnforced of receiverId: string  // a usable LocalPeer is composed
    | AudienceBindingMissing                      // hosts contracts, no LocalPeer — the defect
    | AudienceBindingIdle                         // no LocalPeer, nothing hosted

PeerServerApp.auditAudienceBinding   : PeerServerApp -> PeerAudienceBinding
PeerServerApp.enforceAudienceBinding : PeerServerApp -> unit   // called by `run`
PeerServerApp.withStrictAudienceBinding : PeerServerApp -> PeerServerApp
```

`PeerServerApp.run` calls `enforceAudienceBinding` before any peer registration.
On `AudienceBindingMissing` it logs one `Warn` through the resolved `ILogger`
naming the exposure and the lever; under `withStrictAudienceBinding` it
`failwith`s instead and the deployment does not start. Every other state is
silent.

A `LocalPeer` whose `PeerId` is blank counts as **absent**: `checkAudience`
short-circuits on an empty expected audience, so `withLocalPeer { PeerId = ""; … }`
binds nothing while looking composed.

`JwtPeerAuthProvider` is **behaviourally unchanged** — only XML-doc notes were
added. It stays a stateless, policy-free validator (GP 12 rule 4): it is handed an
expected audience and cannot tell a host-only deployment's deliberate posture from
a composition oversight. Only the compose root can, because only it can see the
hosted-contract set.

## Does this reject calls that previously succeeded?

**By default, no.** The default is warn-not-fail: an existing host-only deployment
starts and validates exactly as it did, with one extra startup `Warn` (GP 11).
Nothing is registered, nothing runs per request (GP 13).

**But the fix the advisory recommends does.** Composing `withLocalPeer` activates
the Phase 130 check, and from that moment **any inbound token whose `aud` is not
this receiver's peer id is refused with `PeerUnauthorized`** — including tokens
that a working counterparty is minting today against a stale or differently-cased
peer id, and any token minted before the counterparty knew your id. That is the
point of the phase, and it is the step that can break a live federation.

**And `withStrictAudienceBinding` refuses to START**, not to serve — a composition
defect becomes a failed deploy rather than a running deployment with the defence
off. Deliberately opt-in for the same reason.

## Rollout order

1. **Upgrade and read the log.** A host-only deployment now prints the advisory at
   startup. Nothing else changes; no counterparty is affected.
2. **Agree the receiver's peer id with every counterparty** and confirm each one is
   minting tokens addressed to it. `IssuePeerToken(caller, audience, user)` puts
   `audience.PeerId` in `aud`, so this is a question about each caller's
   `TargetPeer` configuration.
3. **Compose `withLocalPeer` on the receiver.** Binding is now in force. Roll this
   per receiver, not fleet-wide at once — a mis-addressed caller fails closed, and
   you want one counterparty's worth of blast radius while you find out.
4. **Add `withStrictAudienceBinding`** once every receiver in the estate is bound,
   so a future composition cannot silently regress to the unbound posture.

```fsharp
PeerServerApp.create ()
|> PeerServerApp.withConfig { ServerConfig.defaults with PeerSubstrate = EnabledPeerSubstrate }
|> PeerServerApp.withLocalPeer { PeerId = "receiver-z"; DisplayName = "Receiver Z" }  // step 3
|> PeerServerApp.withStrictAudienceBinding                                            // step 4
|> PeerServerApp.withContract (fun fusion -> JsonRpcPeerHost.contract<LedgerApi> "example.ledger" [ v1 ] fusion impl)
|> PeerServerApp.run
```

## Opt-out / rollback

- **Back out step 4** — drop `withStrictAudienceBinding`. The deployment starts
  again and the defect is an advisory once more.
- **Back out step 3** — drop `withLocalPeer`. Audience binding switches off and
  every previously-accepted token is accepted again. This is a real rollback lever
  for an incident, but it restores the cross-receiver exposure; treat it as
  temporary and finish step 2.
- **No opt-out is needed for the advisory itself.** It is one `Warn` line at
  startup, emitted only when the deployment is genuinely in the unbound-while-
  hosting state, and only when an `ILogger` is composed.

## Verification

`PeerServerApp.auditAudienceBinding` is pure — assert your own composition's
posture in your own test suite rather than reading logs:

```fsharp
Expect.equal
    (PeerServerApp.auditAudienceBinding myApp)
    (AudienceBindingEnforced "receiver-z")
    "this deployment binds inbound tokens to its own peer id"
```

## Public-API impact

Additive except for one entry: `PeerServerApp` gained a `StrictAudienceBinding`
field, so its compiler-generated constructor widened and the previous arity is
reported as removed by the API baseline. Source-compatible for every documented
construction path (`PeerServerApp.create ()` plus the `with*` chain); only a raw
record literal needs the extra field. Same shape as the Phase 590
`ConsumedContracts` append to the same record.

## See also

- [Phase 130 — PKCE enforcement + peer-JWT audience binding](130-pkce-and-peer-audience.md)
- [Phase 338 — peer JWT replay defence + call scoping](338-peer-jwt-replay-defence.md)
