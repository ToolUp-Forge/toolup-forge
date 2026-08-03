// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

// ─── Layer 4 — peer authentication ───────────────────────────────────
//
// `IPeerAuthProvider` is the trust boundary between two deployments. The
// initiating side calls `IssuePeerToken` to mint a bearer token vouching
// for its identity (and, optionally, the end user it is acting for); the
// receiving side calls `ValidatePeerToken` to authenticate the caller
// before any contract dispatch. `VerifyDelegation` checks the signature
// chain on a `Delegated` assertion that arrived through an intermediary.
//
// **Fail closed (silent-insecure-default lens).** Every validation
// method returns `Result<_, PeerError>`; a malformed, expired, or
// unverifiable credential is `Error (PeerUnauthorized …)`, never a
// silently-accepted call. The default implementation
// (`JwtPeerAuthProvider`) uses constant-time comparison and rejects
// `alg: none`, missing `exp`, and future `nbf` — there is no "auth
// disabled" mode that lets an unauthenticated peer through.
//
// **Audience binding (Phase 130).** When the receiver has declared its
// own identity (`PeerServerApp.withLocalPeer`), `ValidatePeerToken`
// additionally binds the token's `aud` claim to that identity: a token
// minted *for a different peer* that happens to share the issuer's
// signing key is rejected, and a token with no `aud` is rejected fail-
// closed. This closes the confused-deputy / cross-receiver-replay hole
// in any topology where more than one receiver trusts the same issuer
// key. A receiver that composed no `LocalPeer` identity cannot bind
// audience and keeps the pre-130 behaviour (signature + exp + nbf only).
//
// **Delegation is verified separately, and verifying it is mandatory
// (Phase 330).** `ValidatePeerToken` authenticates the *calling peer*.
// The end-user identity it returns arrived inside that peer's own signed
// payload, so on the `Delegated` case the caller is asserting "I am
// acting for user U, and peer P authorised me to" — a claim the outer
// signature says nothing about. The only thing separating a genuine
// multi-hop delegation from an invented one is
// `DelegatedAssertion.Signature`, checked by `VerifyDelegation` against
// the delegating peer's own trust anchor. **A receiver that acts on a
// `Delegated` principal without calling `VerifyDelegation` first has no
// delegation security at all** — any peer holding a valid signing key
// can name any subject. The split is deliberate rather than folded into
// `ValidatePeerToken`: the provider stays a stateless, policy-free
// validator (GP 12 rule 4), and the *host* seam owns the enforcement,
// where the call context is rebuilt. `JsonRpcPeerHost` does exactly that
// on the contract-dispatch path; a bespoke host built on this interface
// must do the same.
//
// Six portability rules (GP 12):
//   1. Identity by value — tokens are strings; identities are records.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry / supervision as data — failure is `PeerError`; no
//      callback leaks framework semantics across the boundary.
//   4. Stateless between calls — validation reads the signing key from
//      its injected `ISecretStore` on every call (so a rotated key
//      flows through immediately); no per-call state is retained.
//   5. No cross-shard ordering — token issue / validate are independent.
//   6. Precision at the lower bound — `exp` / `nbf` are second-precision
//      Unix timestamps, the JWT standard's lower bound.

/// The authenticated result of `ValidatePeerToken`: which peer the
/// token vouches for, and the end-user identity (if any) it carries.
/// Server-side only — produced by the auth provider, consumed by the
/// host before it rebuilds the `PeerCallContext`.
type PeerPrincipal = {
    /// The calling deployment the validated token authenticates.
    Caller: PeerIdentity
    /// The end-user identity the caller vouches for, or `Anonymous`.
    User: UserContext
}

type IPeerAuthProvider =
    /// Mint a bearer token vouching for `caller`, scoped to `audience`,
    /// optionally carrying the end-user identity `user`. Returns the
    /// serialised token, or `Error` if the signing material is
    /// unavailable.
    abstract IssuePeerToken:
        caller: PeerIdentity * audience: PeerIdentity * user: UserContext -> Async<Result<string, PeerError>>

    /// Authenticate an inbound bearer token. Returns the `PeerPrincipal`
    /// the token vouches for on success; `Error (PeerUnauthorized …)`
    /// for any malformed / expired / not-yet-valid / bad-signature token.
    /// When the receiver has declared its own identity, the token's `aud`
    /// claim is bound to it: a token addressed to a different peer (even
    /// under a shared issuer key) and a token with no `aud` are both
    /// rejected (Phase 130). Fails closed: there is no path that returns
    /// a principal for an unverified token — and an end-user context the
    /// token asserts but that will not deserialise is a rejection too,
    /// never a silent downgrade to `Anonymous` (Phase 330).
    ///
    /// The returned `User` is the caller's *assertion*, authenticated
    /// only as far as "this peer sent it". A `Delegated` case is NOT
    /// verified here — pass it to `VerifyDelegation` before acting on it.
    abstract ValidatePeerToken: token: string -> Async<Result<PeerPrincipal, PeerError>>

    /// Verify the signature chain on a `Delegated` assertion that
    /// arrived through one or more intermediary peers. Checks the
    /// immediate delegating peer's signature over the assertion payload
    /// against that peer's registered trust anchor. `Ok ()` authorises
    /// the delegated identity; `Error (PeerUnauthorized …)` rejects it.
    ///
    /// **Mandatory before dispatch (Phase 330).** Every receiver path
    /// that would act on a `Delegated` principal — rebuild a call
    /// context from it, authorise against its `Subject`, audit it as the
    /// originator — MUST call this first and fail closed on `Error`.
    /// Skipping it makes the assertion self-asserted, which is the
    /// confused-deputy vector the signature exists to close.
    abstract VerifyDelegation: assertion: DelegatedAssertion -> Async<Result<unit, PeerError>>