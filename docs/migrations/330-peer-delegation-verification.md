# Verify peer delegation assertions before dispatch

**Ships in:** ToolUp.InterPlatform (Phase 330).

> **Read this before upgrading a receiver.** This change **can reject calls that
> previously succeeded**, and unlike [Phase 338](338-peer-jwt-replay-defence.md)
> and [Phase 309](309-peer-audience-binding-enforcement.md) it is **on by
> default with no opt-out flag**. It is a correctness fix, not a policy: the
> pre-330 behaviour had no delegation security at all, so shipping an off switch
> would be shipping the hole. What it costs you is stated in full below.

## What changes

`ValidatePeerToken` authenticates the **calling peer**. The end-user identity it
returns — the `uctx` claim — rode inside *that peer's own signed payload*, so the
outer signature proves who sent the assertion and nothing whatever about whether
it is true. On the `Delegated` case the caller is asserting *"I am acting for
user U, and peer P authorised me to"*, and the only thing separating a genuine
buyer→broker→seller delegation from an invented one is
`DelegatedAssertion.Signature`.

That signature has been verifiable since Phase 18 — `IPeerAuthProvider.
VerifyDelegation` checks an HMAC over the canonical `{Subject}|{chain}` byte
string against the *last* peer in the chain's trust anchor. It simply had **no
call site on the dispatch path**. So any peer holding a valid signing key could
send:

```fsharp
// A perfectly valid outer token — this peer really is who it says it is …
Delegated {
    Subject = "admin@victim"        // … asserting an originator it invented
    DelegationChain = [ "origin" ]
    Signature = "anything"          // never checked
}
```

and the receiver would rebuild its call context from `admin@victim`. Two fixes:

**1. `JsonRpcPeerHost` contract dispatch now verifies a `Delegated` originator
before rebuilding the call context**, and refuses `PeerUnauthorized` (HTTP 401)
when it does not verify. The check runs *before the request body is read*, so a
refused call never reaches `IPlatformPeer.Handle` and no audit row attributes
work to an originator the receiver could not authenticate. `Anonymous` and
`Direct` short-circuit without touching the auth provider — a non-delegating
deployment runs exactly the code it ran before and pays nothing (GP 11 / GP 13).

**2. `JwtPeerAuthProvider.ValidatePeerToken` now rejects a malformed `uctx`
claim** instead of silently degrading it to `Anonymous`. Both malformed shapes
are covered: a `uctx` that is not a JSON string, and a string that will not
deserialise back to a `UserContext`. An **absent** `uctx` is still `Anonymous` —
nothing was asserted, so nothing is being ignored.

The pure `VerifyDelegation` implementation is **unchanged** (constant-time HMAC,
empty-chain reject, per-call signing-key read). The wiring lives at the host
seam so the provider stays a stateless, policy-free validator (GP 12 rule 4).
There is no public-API change in this phase: the enforcement is inside the
existing handler and the claim reader is a private helper.

## What can now be rejected that previously succeeded

Four shapes, all of them 401 with a `PeerUnauthorized` body naming the reason.
Only a deployment that **receives `Delegated` assertions** is affected at all.

| Rejection message | Cause | What to do |
|---|---|---|
| `Delegation signature verification failed` | The assertion was not signed with the delegating peer's key over the canonical string — including a placeholder signature that used to be accepted because nothing looked at it. | Have the delegating peer sign properly (below). |
| `No signing key registered for delegating peer 'X'` | The receiver holds no trust anchor for the **last peer in the chain**. Previously irrelevant; now required. | Seed `peers/X/signing-key` on the receiver. |
| `Signing key for delegating peer 'X' is empty or below the 32-byte minimum` | A placeholder or blank trust anchor. | Replace with real key material ≥ 32 bytes. |
| `Delegation chain is empty` | A `Delegated` assertion with `DelegationChain = []` — there is no delegator to verify against. | Populate the chain, or send `Direct` / `Anonymous`. |

Plus, on any deployment: `Malformed 'uctx' claim …` when an inbound token asserts
an end-user context the receiver cannot read. In practice this means the two ends
disagree about the wire shape of `UserContext` — which was previously invisible,
because the call proceeded as anonymous and the disagreement was swallowed.

## Signing a delegation correctly

The canonical byte string is `{Subject}|{chain joined by ">"}`, HMAC-SHA256 with
the **last** chain member's signing key (the immediate delegating peer — the same
`peers/{id}/signing-key` secret it signs its bearer tokens with), base64url,
unpadded:

```fsharp
// Issued by the peer that is delegating — for chain [ "origin"; "broker" ]
// this is BROKER's key, not origin's.
let canonical = $"""{subject}|{String.concat ">" chain}"""
use hmac = new HMACSHA256(Encoding.UTF8.GetBytes delegatingPeerSigningKey)

let assertion = {
    Subject = subject
    DelegationChain = chain
    Signature = Base64Url.encode (hmac.ComputeHash(Encoding.UTF8.GetBytes canonical))
}
```

The receiver reads `peers/{last-chain-member}/signing-key` from its own
`ISecretStore` on every verify (no cache — a rotated key flows through
immediately), so the key must be shared out of band exactly as bearer keys are.

## Rollout order

The initiator side is unchanged, so there is no "both halves move together"
problem — but the *receiver* is where the refusals happen, so audit before you
upgrade it, not after.

1. **Find out whether you delegate at all.** Grep your initiator composition for
   `Delegated`. If nothing constructs one — the overwhelmingly common case, since
   1:1 peer calls carry `Anonymous` or `Direct` — you are unaffected and can
   upgrade with no further work.
2. **For each delegating peer, confirm it signs.** A signature that is a
   placeholder, an empty string, or copied from another assertion will now be
   refused. Use the snippet above.
3. **On each receiver, seed a trust anchor for every peer that appears LAST in a
   chain you accept.** This is the step most likely to be missing, because before
   this phase the receiver never looked the key up.
4. **Upgrade the receiver, then watch for `PeerUnauthorized` on `/peer/v1/*`.**
   The `PeerCallCompleted` audit row is deliberately *not* emitted for a refused
   delegation (the call never dispatched), so the signal is the 401 itself.

## Rollback

There is no feature flag. If step 4 surfaces refusals you cannot resolve
immediately, pin `ToolUp.InterPlatform` to the previous version while you fix the
signing or trust-anchor gap — and treat that window as one in which any peer with
a valid signing key can name any originator, because that is precisely what the
pinned version permits.

## Not in scope

**The chain's last member is not bound to the authenticated caller.** A peer may
present a chain ending in a *different* peer, provided that peer's signature
verifies. In the intended buyer→broker→seller topology the two coincide, so
binding them would be a further tightening with a real compatibility cost; it is
deliberately left for a follow-on rather than folded in here.

**The capability and job-poll routes are unchanged.** Neither acts on the
end-user identity — they read only the validated `Caller` — so there is nothing
for a delegation check to protect there.

## See also

- [`src/InterPlatform/TECHNICAL_GUIDE.md`](../../src/InterPlatform/TECHNICAL_GUIDE.md) — the delegation section and the canonical assertion form.
- [Phase 309 migration](309-peer-audience-binding-enforcement.md) — audience binding, the other confused-deputy leg.
- [Phase 338 migration](338-peer-jwt-replay-defence.md) — replay defence + call scoping on the same token.
