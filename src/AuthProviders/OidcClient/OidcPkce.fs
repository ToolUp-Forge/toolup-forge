// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AuthProviders.Oidc.OidcPkce

open Fable.Core

// ─── PKCE (RFC 7636) via WebCrypto ───────────────────────────────────
//
// The OIDC Authorization Code flow requires PKCE in public-client
// deployments (browser SPAs) to prevent authorization-code interception
// attacks. We generate:
//   1. `code_verifier`  — 32 random bytes, base64url-encoded
//   2. `code_challenge` — SHA-256 of the verifier, base64url-encoded
//
// The verifier is stashed in `sessionStorage` before the authorize
// redirect and retrieved on the callback. `sessionStorage` (not
// `localStorage`) because the verifier must be tab-scoped — a user
// with two half-completed sign-ins in two tabs must not cross them.
//
// We reach for WebCrypto (`crypto.subtle.digest`, `crypto.getRandomValues`)
// rather than the BCL's `System.Security.Cryptography` because Fable
// can't compile BCL crypto to browser JS. WebCrypto is universally
// available on HTTPS and localhost (Secure Contexts), which matches
// OIDC's deployment assumptions anyway.

/// Generate a cryptographically random `code_verifier` — 32 bytes of
/// entropy, base64url-encoded to ~43 characters. Synchronous because
/// `crypto.getRandomValues` is sync; SHA-256 is the async step.
[<Emit("(() => { const a = new Uint8Array(32); crypto.getRandomValues(a); return btoa(String.fromCharCode.apply(null, a)).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, ''); })()")>]
let generateCodeVerifier () : string = jsNative

/// SHA-256 the verifier, then base64url-encode the digest. Async
/// because `crypto.subtle.digest` returns a Promise; callers should
/// `Async.AwaitPromise` or consume as a Promise directly.
[<Emit("(async () => { const data = new TextEncoder().encode($0); const hash = await crypto.subtle.digest('SHA-256', data); const bytes = new Uint8Array(hash); return btoa(String.fromCharCode.apply(null, bytes)).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, ''); })()")>]
let generateCodeChallenge (verifier: string) : JS.Promise<string> = jsNative

/// Generate a short (16-byte) random `state` parameter for CSRF
/// protection on the authorize redirect. Stashed alongside the
/// verifier, checked on callback.
[<Emit("(() => { const a = new Uint8Array(16); crypto.getRandomValues(a); return btoa(String.fromCharCode.apply(null, a)).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, ''); })()")>]
let generateState () : string = jsNative