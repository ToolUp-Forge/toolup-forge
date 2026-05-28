module ToolUp.Platform.Tests.Contracts.IUserClaimsContract

open Expecto
open ToolUp.Platform

// ─── IUserClaims contract pack — Phase 62 ─────────────────────────
//
// Parametrised tests for any `IUserClaims` implementation. Each test
// asks the factory for a fresh provider so concurrent runs against
// shared substrate state cannot interfere.
//
// Coverage targets the **portable** surface — assertions every
// implementation must satisfy through the interface alone:
//   * `GetPremiumStatus` for an unknown user → `NotPremium` (the
//     anonymous / unprovisioned-user default; provider impls must
//     not throw on a userId they have never seen).
//   * `ListPremiumUsers` initial shape — `Ok []` for a fresh
//     provider; populated after a `GrantPremium` round-trip.
//   * Grant + read round-trip — premium status set via `GrantPremium`
//     surfaces on a subsequent `GetPremiumStatus` (persisting impls
//     only; the no-op default is exempt — see factory note).
//   * Revoke + read round-trip — granted status falls back to
//     `NotPremium` after revoke.
//   * Grantor / reason captured on the returned `PremiumStatus`.
//
// **Out of scope here** (live in per-impl tests, not the portable
// contract):
//   * Auth-provider-specific write-through paths (Clerk admin API,
//     OIDC custom-claim writes) — exercised by each provider's own
//     test pack against a mock HttpClient or test issuer.
//   * Eventual-consistency timing windows — implementation-specific
//     auth-context-refresh ceilings vary; the portable contract only
//     asserts read-after-grant within the same `IUserClaims`
//     instance.
//
// **Factory variants supported.** `tests` takes a `persists: bool`
// flag because the SDK's default `NoOpUserClaims` records the
// operator's intent (returning `Ok (Premium ...)` from `GrantPremium`)
// without writing through to any provider — a deliberate design
// choice so the audit trail captures grants even when no provider is
// wired. Production impls that write to a real provider set
// `persists = true` and exercise the full grant-read round-trip.

let private knownUser = "user-known"
let private unknownUser = "user-unknown"
let private grantor = "operator-alice"
let private reason = Some "premium upgrade — public-utility tier"

let private baseCases (factory: unit -> IUserClaims) = [
    testCaseAsync "GetPremiumStatus — unknown user returns NotPremium"
    <| async {
        let claims = factory ()
        let! status = claims.GetPremiumStatus unknownUser
        Expect.equal status NotPremium "an unknown user must not be reported as Premium"
    }

    testCaseAsync "GetPremiumStatus — idempotent across adjacent calls"
    <| async {
        let claims = factory ()
        let! first = claims.GetPremiumStatus knownUser
        let! second = claims.GetPremiumStatus knownUser
        Expect.equal first second "two adjacent reads must return the same status"
    }

    testCaseAsync "ListPremiumUsers — fresh provider returns Ok"
    <| async {
        let claims = factory ()
        let! result = claims.ListPremiumUsers()

        match result with
        | Ok _ -> ()
        | Error e -> failtestf "fresh provider must not surface a transient error: %s" e
    }

    testCaseAsync "GrantPremium — returns Ok carrying Premium with grantor + reason"
    <| async {
        let claims = factory ()
        let! result = claims.GrantPremium(knownUser, grantor, reason)

        match result with
        | Ok(Premium(_, capturedGrantor, capturedReason)) ->
            Expect.equal capturedGrantor grantor "returned PremiumStatus must carry the supplied grantor"
            Expect.equal capturedReason reason "returned PremiumStatus must carry the supplied reason"
        | Ok NotPremium -> failtest "GrantPremium must not return NotPremium on the success path"
        | Error e -> failtestf "GrantPremium must succeed for a known user: %s" e
    }

    testCaseAsync "RevokePremium — returns Ok for any user (idempotent)"
    <| async {
        let claims = factory ()
        // Revoke applied to a never-granted user must still return
        // Ok — revocation is idempotent so callers can invoke it
        // without first checking the current status.
        let! result = claims.RevokePremium(knownUser, grantor, reason)

        match result with
        | Ok() -> ()
        | Error e -> failtestf "RevokePremium must be idempotent: %s" e
    }

    testCaseAsync "Empty-string userId returns NotPremium (anonymous defensive default)"
    <| async {
        let claims = factory ()
        let! status = claims.GetPremiumStatus ""
        Expect.equal status NotPremium "blank userId must never surface as Premium"
    }
]

let private persistingCases (factory: unit -> IUserClaims) = [
    testCaseAsync "Grant + GetPremiumStatus round-trip — surfaces Premium"
    <| async {
        let claims = factory ()
        let! _ = claims.GrantPremium(knownUser, grantor, reason)
        let! status = claims.GetPremiumStatus knownUser

        match status with
        | Premium(_, capturedGrantor, capturedReason) ->
            Expect.equal capturedGrantor grantor "stored grantor matches"
            Expect.equal capturedReason reason "stored reason matches"
        | NotPremium ->
            failtest "GetPremiumStatus must surface Premium status set via GrantPremium (persisting impls only)"
    }

    testCaseAsync "Grant + Revoke + GetPremiumStatus round-trip — surfaces NotPremium"
    <| async {
        let claims = factory ()
        let! _ = claims.GrantPremium(knownUser, grantor, reason)
        let! _ = claims.RevokePremium(knownUser, grantor, reason)
        let! status = claims.GetPremiumStatus knownUser
        Expect.equal status NotPremium "revoked user reads as NotPremium"
    }

    testCaseAsync "Grant + ListPremiumUsers — granted user surfaces in the listing"
    <| async {
        let claims = factory ()
        let! _ = claims.GrantPremium(knownUser, grantor, reason)
        let! listed = claims.ListPremiumUsers()

        match listed with
        | Ok entries ->
            let isKnown (uid, _) = uid = knownUser
            Expect.isTrue (entries |> List.exists isKnown) "ListPremiumUsers must include a granted user"
        | Error e -> failtestf "ListPremiumUsers must not fail after a grant: %s" e
    }
]

let tests (name: string) (persists: bool) (factory: unit -> IUserClaims) =
    let cases =
        if persists then
            baseCases factory @ persistingCases factory
        else
            baseCases factory

    testList $"{name} — IUserClaims contract" cases