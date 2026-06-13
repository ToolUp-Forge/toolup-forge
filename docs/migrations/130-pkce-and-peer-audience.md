# Migration — Phase 130: wire the inert auth controls (PKCE enforcement + peer-JWT audience binding)

**Status:** two breaking interface widens, both additive-at-runtime (GP 11). A non-PKCE OAuth flow and a peer deployment that composes no `LocalPeer` identity are byte-for-byte unchanged on the wire; the breaking part is **source-level** (interface method arity / constructor arity) and only touches consumers who implement `IOAuthCredentialFlow` or construct `JwtPeerAuthProvider` directly.

Two previously-inert security primitives are made load-bearing:

1. **PKCE (RFC 7636)** — the data-source OAuth connector flow generated a `code_verifier`, stashed it in the state entry, and then never used it: `BuildAuthorizeUrl` sent no `code_challenge` and `ExchangeCode` was never handed the verifier (`OAuthCrypto.codeChallengeFromVerifier` had zero call sites). The substrate now derives the S256 challenge and threads both ends for any flow that opts in.
2. **Peer-JWT audience** — `PeerJwt.encode` set `aud`, but `ValidatePeerToken` never read it. The receiver now binds `aud` to its own peer id when it has one.

## What changes

### 1. `IOAuthCredentialFlow` (`ToolUp.Platform.Server`)

| Member | Before | After |
|---|---|---|
| `SupportsPkce` | — (new) | `abstract SupportsPkce: bool` — the per-flow capability flag |
| `BuildAuthorizeUrl` | `ctx * state * redirectUri` | `ctx * state * redirectUri * pkce: PkceChallenge option` |
| `ExchangeCode` | `ctx * code * redirectUri` | `ctx * code * redirectUri * codeVerifier: string option` |

New type `PkceChallenge = { Challenge: string; Method: string }` (`Method` is always `"S256"`).

The substrate (`OAuthFlowHandler`) passes `Some` to a flow that declares `SupportsPkce = true` and `None` otherwise. A PKCE-declaring flow whose state entry carries no verifier **fails the exchange closed** — it never silently redeems the code without PKCE.

### 2. `JwtPeerAuthProvider` (`ToolUp.InterPlatform`)

Constructor gains an optional `?expectedAudience: string` (the receiver's own peer id). When non-empty, `ValidatePeerToken` adds a fixed-time `aud` check after signature/exp/nbf: a token addressed to a different peer (even under a shared issuer key) and a token with no `aud` are both rejected. `PeerCompose` threads `LocalPeer.PeerId`. A receiver that composed no `LocalPeer` keeps the pre-130 behaviour (audience unbound).

## Diff to apply (custom flow implementers)

```fsharp
// Before
member _.BuildAuthorizeUrl(ctx, state, redirectUri) = async { ... }
member _.ExchangeCode(ctx, code, redirectUri) = async { ... }

// After — non-PKCE provider (no behavioural change; just the new arity + flag)
member _.SupportsPkce = false
member _.BuildAuthorizeUrl(ctx, state, redirectUri, _pkce) = async { ... }   // ignore pkce
member _.ExchangeCode(ctx, code, redirectUri, _codeVerifier) = async { ... } // ignore verifier

// After — PKCE provider
member _.SupportsPkce = true
member _.BuildAuthorizeUrl(ctx, state, redirectUri, pkce) = async {
    let pkceQuery =
        match pkce with
        | Some c -> $"&code_challenge={c.Challenge}&code_challenge_method={c.Method}"
        | None -> ""   // substrate never sends None to a SupportsPkce flow; treat as a hard error if it matters
    // ... append pkceQuery to the authorize URL
}
member _.ExchangeCode(ctx, code, redirectUri, codeVerifier) = async {
    // include code_verifier on the token POST; a None here means an intercepted
    // code with no verifier — reject rather than redeem.
}
```

Peer deployments that construct the provider directly: `JwtPeerAuthProvider(secrets)` still compiles (audience binding off). To activate audience binding, compose `PeerServerApp.withLocalPeer` (the SDK does the threading for you) or pass the id explicitly: `JwtPeerAuthProvider(secrets, myPeerId)`.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj` — the `OAuth PKCE enforcement (Phase 130)` suite (RFC 7636 S256 vector + challenge-on-URL + verifier-gated exchange + verifier-less rejection), the `FakePkceOAuthFlow — IOAuthCredentialFlow contract` binding (the `SupportsPkce`-gated conformance case fires), and `JwtPeerAuthProvider — audience binding (Phase 130)` (cross-receiver replay rejected, correctly-addressed accepted, missing-`aud` fail-closed, unbound-receiver pre-130 behaviour) all pass.

## Rollback

Revert the two interface widens and the `JwtPeerAuthProvider` constructor param. The PKCE verifier returns to being stashed-but-unused; the peer `aud` claim returns to being set-but-unread. No persisted-state migration is involved (state entries already carried `CodeVerifier`; tokens already carried `aud`).
