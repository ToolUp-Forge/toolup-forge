# Peer robustness roundup — base64url 401 / profile no-downgrade / asymmetric key

**Ships in:** ToolUp.InterPlatform (Phase 343).

Three independent hardenings of the federation layer. Two are invisible to a
correctly-behaving deployment; **one can refuse a handshake that previously
succeeded** and is called out under [Rollout order](#rollout-order).

---

## 1. A malformed base64url signature is a 401, not a 500

### What changes

`PeerJwt.verifySignature` decoded the token's signature segment with
`Base64Url.decode`, which throws `FormatException` on anything outside the
base64url alphabet. Nothing caught it: `JwtCrypto.result` is a `Result`
computation expression, not an exception boundary, so the throw escaped
`IPeerAuthProvider.ValidatePeerToken` and `JsonRpcPeerHost` answered **500**
where every other bad credential answers 401.

The call was still refused — this was never an authentication bypass. What it
was is an **error oracle**: an unauthenticated caller could distinguish
"malformed encoding" from "wrong signing key" by status code alone, before
presenting any valid credential.

Two changes, at source and at the boundary:

- `verifySignature` now uses `Base64Url.tryDecode` and returns the **same**
  `Error "Invalid signature"` a well-formed-but-wrong signature returns. The
  HMAC is still computed before the decode is inspected, so the work done does
  not depend on whether the segment parses.
- `JsonRpcPeerHost` wraps every `ValidatePeerToken` call in a fail-closed
  backstop: an exception escaping *any* provider becomes `PeerUnauthorized`
  (401), never a 500. `OperationCanceledException` is deliberately excluded — a
  disconnected client is not a rejected credential.

### What you must do

**Nothing.** No API changed, no configuration moved, and a deployment whose
peers send well-formed tokens cannot observe the difference.

If you have monitoring that alerts on 5xx from `/peer/v1/*`, expect that signal
to go quiet and the corresponding 401 rate to rise by the same amount.

---

## 2. A non-2xx capability-profile fetch no longer downgrades

### What changes

**This is the behaviour change.** The Phase 18d handshake fetched a target
peer's `PeerProfile` from `GET /peer/v1/capabilities/profile`, and on **any**
non-2xx degraded to the bare `GET /peer/v1/capabilities` list mapped through
`PeerCapabilityNegotiation.fromCapabilityList`. That was written for peers
predating the profile route.

The problem is that the degraded profile carries **no method lifecycle at all**,
and the degrade triggers on a status code the *answering side* chooses. A
receiver that wanted a deprecation window ignored — or anything on the path able
to spoil one response — returned a 500, and the caller stopped asking about
method lifecycle entirely: a declared `Deprecated` became `MethodNotAdvertised`,
which the negotiation contract documents as "fall back to contract-version
negotiation", i.e. call it anyway. Nothing logged a downgrade, because from the
caller's point of view nothing failed.

A non-2xx is now a `PeerHandshakeError` naming the status. No profile is
synthesised, and the bare capability list is not even requested.

**404 is deliberately not special-cased.** "The route is missing" is exactly
what a masking response claims to be, so treating it as trusted would leave the
vector open under a different number.

**Why fail closed rather than "keep the last-known profile".** A cache needs an
invalidation story, and a stale cached profile is itself a way to miss a
deprecation — it trades a fetch-time masking vector for a
lifetime-of-the-cache one. `IPeerHandshake` is stateless between calls by
contract (GP 12 rule 4), and this keeps it that way.

### What you must do

Nothing, **unless** this deployment calls `IPeerHandshake.NegotiateMethod`
against a peer that does not serve `/peer/v1/capabilities/profile`. Those
negotiations now fail instead of silently degrading.

Preferred fix — upgrade the peer, which needs only that it compose
`PeerServerApp` at 18d or later (the route is mounted unconditionally by
`JsonRpcPeerHost.routes`).

Where that is not yet possible, restore the old behaviour explicitly:

```fsharp
PeerServerApp.create ()
|> PeerServerApp.withConfig config
|> PeerServerApp.withLocalPeer myIdentity
|> PeerServerApp.withLegacyProfileFallback   // accepts the downgrade, by name
|> PeerServerApp.run
```

Naming it is the whole value: a composition that accepts lifecycle masking now
says so in its own source, rather than inheriting it as a default nobody chose.

### Compatibility note

`PeerServerApp` gains a `LegacyProfileFallback: bool` field. As with Phase 309's
`StrictAudienceBinding`, this widens the record's compiler-generated constructor
— a **binary** break for any caller constructing `PeerServerApp` by full record
literal rather than through `PeerServerApp.create ()` + the `with*` pipeline
(which is the documented and only recommended shape, and is source-compatible).
The `api-baselines/InterPlatform.approved.txt` diff reports it as one removed
constructor plus the widened replacement.

---

## 3. Asymmetric (ES256 / RS256) peer auth as a companion

### What changes

`JwtPeerAuthProvider` signs and verifies with **one shared symmetric secret per
peer**, held by both ends. Two consequences, both fine for a strict 1:1 pairing
and poor for anything wider:

- **Compromise scope.** A receiver trusting N peers holds N live *minting* keys.
  Reading its `ISecretStore` yields the material to impersonate every one of
  them, against every other receiver that trusts them.
- **Attribution.** Both ends hold the same key, so a token proves only that
  *someone* holding it signed the payload. The receiver can mint tokens
  indistinguishable from the caller's own.

`AsymmetricPeerAuthProvider` is the GP 1 companion for topologies beyond 1:1.
The private key never leaves the deployment that owns it; receivers hold only
public keys, which are not secrets. Compromising a receiver forges nothing.

```fsharp
type PeerSignatureAlgorithm =
    | PeerEs256   // ECDSA P-256 / SHA-256 — the default choice
    | PeerRs256   // RSASSA-PKCS1-v1_5 / SHA-256, ≥ 2048-bit

// Same three shapes as the symmetric provider, as explicit overloads.
AsymmetricPeerAuthProvider(secrets, PeerEs256)
AsymmetricPeerAuthProvider(secrets, myPeerId, PeerEs256)                  // audience-bound
AsymmetricPeerAuthProvider(secrets, myPeerId, PeerEs256, tokenPolicy)     // + replay / scope policy
```

Everything else about the token is **literally the shared code path**: claim
set, `exp` / `nbf`, Phase 130 audience binding, Phase 338 `jti` replay defence
and `cid` contract binding, Phase 330 `uctx` handling, Phase 334 issuer
sanitisation. The two providers differ only in which secret they read, which
`alg` they accept, and how the signature is checked — so they cannot drift into
disagreeing about what a valid peer token is.

Exactly one algorithm is accepted per instance. A provider that picked its
verifier from the token's own `alg` header would be reintroducing algorithm
confusion; an HS256 token presented to an ES256 receiver is refused, and vice
versa.

BCL `ECDsa` / `RSA` only — no new dependency. This is the same primitive set
`OidcAuthProvider` already uses for asymmetric JWS, down to ES256's IEEE-P1363
fixed-field signature transport.

### What you must do

**Nothing** to keep the symmetric default: `PeerCompose` still registers
`JwtPeerAuthProvider`, byte-for-byte unchanged (GP 11), and a deployment that
never constructs the asymmetric type pays nothing (GP 13).

To adopt it, key material goes in `ISecretStore` under the reserved `_platform`
scope — deliberately **distinct key names** from the symmetric
`peers/{peerId}/signing-key`, so a deployment mid-migration can hold both and
"which provider is this store configured for" stays answerable:

| Key | Contents | Who holds it |
|---|---|---|
| `peers/{me}/signing-private-key` | PEM PKCS#8 private key | only the deployment it names |
| `peers/{peer}/signing-public-key` | PEM SPKI public key | every deployment that trusts that peer |

Generate a P-256 pair with the BCL:

```fsharp
use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)
let privatePem = ec.ExportPkcs8PrivateKeyPem()        // stays on the owning deployment
let publicPem = ec.ExportSubjectPublicKeyInfoPem()    // distribute to peers
```

Then register the provider ahead of the SDK default in your composition's
`ServiceConfig` (the peer branch registers `IPeerAuthProvider` with
`AddSingleton`, so an earlier registration from the base `ServerApp` wins).

An RSA key below 2048 bits is refused at mint time rather than producing a
signature with none of the strength the caller assumes — the same posture as the
symmetric provider's 32-byte minimum.

---

## Rollout order

Only item 2 is order-sensitive. Both halves of a peer pair are independent:

1. **Upgrade receivers first.** Item 1 and item 3 are receiver-side and change
   nothing a caller can observe; item 2 is *caller*-side, so upgrading a
   receiver never breaks a peer.
2. **Then upgrade callers**, having first confirmed each peer they negotiate
   methods against serves `/peer/v1/capabilities/profile`. Where one does not
   yet, compose `withLegacyProfileFallback` on that caller and remove it once
   the peer is upgraded.

A caller that only calls contracts (never `NegotiateMethod`) is unaffected by
item 2 in any order.

## See also

- [Phase 130 / peer audience binding](130-pkce-and-peer-audience.md)
- [Phase 309 — audience-binding enforcement](309-peer-audience-binding-enforcement.md)
- [Phase 330 — delegation verification](330-peer-delegation-verification.md)
- [Phase 338 — replay defence + call scoping](338-peer-jwt-replay-defence.md)
