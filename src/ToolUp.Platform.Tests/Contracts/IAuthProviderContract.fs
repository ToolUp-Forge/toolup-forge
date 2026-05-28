// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IAuthProviderContract

// ─── IAuthProvider conformance pack ──────────────────────────────────
//
// A reusable Expecto list asserting the *uniform* invariants every
// compliant `IAuthProvider` must uphold, per the contract documented on
// the interface in `IAuthProvider.fs`:
//
//   • `ValidateRequest` is STRICT — valid credential → `Ok user`
//     (non-anonymous); missing / expired / invalid → `Error _`.
//   • `GetUser` is LENIENT — valid credential → resolved user; any
//     failure (missing / expired / invalid) → `AuthenticatedUser.anonymous`.
//
// This pack deliberately covers only the contract that is uniform
// across providers. It does NOT replace the per-provider pinning tests
// in `AuthProviderTests.fs`: `HeaderAuthProvider` is dev-only and
// intentionally diverges (its `ValidateRequest` is lenient), so it is
// not run through this pack — only providers that honour the documented
// strict/lenient split (OIDC, StaticJwt) are.
//
// The pack is transport-agnostic: it takes request-context factories as
// `unit -> RequestContext` (post-11.C.5 Tier 3) so the caller decides
// how a credential is presented (bearer header, cookie, …); the wrap
// from `HttpContext` happens at the caller via
// `ToolUp.Platform.RequestContextBuilder.ofHttpContext`. The pack
// itself carries no ASP.NET Core or Kestrel dependency.

open Expecto
open ToolUp.Platform.Auth

/// Everything the pack needs to exercise one provider. `ValidCtx` /
/// `ExpiredCtx` / `EmptyCtx` are thunks (re-evaluated per case) so a
/// fresh `HttpContext` is produced for every assertion rather than a
/// shared mutable one.
type AuthProviderContractFixture = {
    /// Human label, suffixed onto the test-list name.
    Name: string
    Provider: IAuthProvider
    /// A request carrying a currently-valid credential the provider
    /// must accept, plus the `UserId` it should resolve to.
    ValidCtx: unit -> RequestContext
    ExpectedUserId: string
    /// A request carrying an expired (or otherwise invalid) credential.
    ExpiredCtx: unit -> RequestContext
    /// A request with no credential at all.
    EmptyCtx: unit -> RequestContext
}

/// The conformance list for one provider fixture. Compose into the
/// caller's suite like any other Expecto `Test`.
let tests (f: AuthProviderContractFixture) : Test =
    testList $"IAuthProvider contract — {f.Name}" [
        testCaseAsync "ValidateRequest: valid credential → Ok non-anonymous user"
        <| async {
            match! f.Provider.ValidateRequest(f.ValidCtx()) with
            | Error e -> failtestf "expected Ok for a valid credential; got Error: %s" e
            | Ok u ->
                Expect.equal u.UserId f.ExpectedUserId "UserId resolved from the valid credential"
                Expect.isFalse (AuthenticatedUser.isAnonymous u) "a validated user must not be anonymous"
        }

        testCaseAsync "ValidateRequest: missing credential → Error (strict)"
        <| async {
            match! f.Provider.ValidateRequest(f.EmptyCtx()) with
            | Ok _ -> failtest "ValidateRequest must reject a request with no credential"
            | Error _ -> ()
        }

        testCaseAsync "ValidateRequest: expired/invalid credential → Error (strict)"
        <| async {
            match! f.Provider.ValidateRequest(f.ExpiredCtx()) with
            | Ok _ -> failtest "ValidateRequest must reject an expired/invalid credential"
            | Error _ -> ()
        }

        testCaseAsync "GetUser: missing credential → anonymous (lenient)"
        <| async {
            let! u = f.Provider.GetUser(f.EmptyCtx())
            Expect.isTrue (AuthenticatedUser.isAnonymous u) "no credential → anonymous"
        }

        testCaseAsync "GetUser: valid credential → resolved user"
        <| async {
            let! u = f.Provider.GetUser(f.ValidCtx())
            Expect.equal u.UserId f.ExpectedUserId "UserId resolved from the valid credential"
        }

        testCaseAsync "GetUser: expired/invalid credential → anonymous (lenient)"
        <| async {
            let! u = f.Provider.GetUser(f.ExpiredCtx())
            Expect.isTrue (AuthenticatedUser.isAnonymous u) "invalid credential → anonymous (lenient)"
        }
    ]