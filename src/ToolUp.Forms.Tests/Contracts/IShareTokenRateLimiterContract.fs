module ToolUp.Forms.Tests.Contracts.IShareTokenRateLimiterContract

open System
open Expecto
open ToolUp.Platform

// ─── IShareTokenRateLimiter contract pack ────────────────────────────
//
// Framework-agnostic test pack: factory builds a fresh
// `IShareTokenRateLimiter` per test. Tests cover the three rate-limit
// invariants any implementation must satisfy:
//
//   1. Admission inside the window's budget succeeds.
//   2. Admission beyond the window's budget returns
//      `Error ShareTokenError.RateLimited`.
//   3. Per-(scope, token) isolation — admissions against one
//      `(scopeId, tokenId)` pair do not deplete another's budget.
//
// Distributed companions (Redis-backed limiter, `IRateLimitStore`-
// backed limiter) bind the same pack against their own factory and
// prove portability against the same conformance bar as the in-memory
// default. Six-rule audit compliance is structural — see
// `IShareTokenRateLimiter.fs` header.

type RateLimiterFactory = unit -> IShareTokenRateLimiter

let private mkRate maxUses windowSeconds : ShareTokenRateLimit = {
    MaxUses = maxUses
    Window = TimeSpan.FromSeconds(float windowSeconds)
}

let tests (label: string) (factory: RateLimiterFactory) =
    testList (sprintf "IShareTokenRateLimiter contract — %s" label) [

        testAsync "Admit inside budget returns Ok" {
            let limiter = factory ()
            let rate = mkRate 3 60
            let! r = limiter.Admit("scope-a", "tok-1", rate)
            Expect.isOk r "first admission inside budget"
        }

        testAsync "Admit at exact budget boundary still returns Ok" {
            let limiter = factory ()
            let rate = mkRate 3 60

            for i in 1..3 do
                let! r = limiter.Admit("scope-a", "tok-1", rate)
                Expect.isOk r (sprintf "admission %d/3 inside budget" i)
        }

        testAsync "Admit beyond budget returns Error RateLimited" {
            let limiter = factory ()
            let rate = mkRate 3 60

            for _ in 1..3 do
                let! _ = limiter.Admit("scope-a", "tok-1", rate)
                ()

            let! r = limiter.Admit("scope-a", "tok-1", rate)

            match r with
            | Error ShareTokenError.RateLimited -> ()
            | other -> failwithf "expected Error RateLimited, got %A" other
        }

        testAsync "Different tokens have independent windows" {
            let limiter = factory ()
            let rate = mkRate 2 60

            // Exhaust tok-1's window.
            let! _ = limiter.Admit("scope-a", "tok-1", rate)
            let! _ = limiter.Admit("scope-a", "tok-1", rate)
            let! exhausted = limiter.Admit("scope-a", "tok-1", rate)

            match exhausted with
            | Error ShareTokenError.RateLimited -> ()
            | other -> failwithf "expected tok-1 to be exhausted, got %A" other

            // tok-2 in the same scope is unaffected.
            let! r = limiter.Admit("scope-a", "tok-2", rate)
            Expect.isOk r "different tokenId has its own window"
        }

        testAsync "Different scopes for the same tokenId have independent windows" {
            let limiter = factory ()
            let rate = mkRate 1 60

            let! _ = limiter.Admit("scope-a", "tok-shared", rate)
            let! exhausted = limiter.Admit("scope-a", "tok-shared", rate)

            match exhausted with
            | Error ShareTokenError.RateLimited -> ()
            | other -> failwithf "expected scope-a/tok-shared exhausted, got %A" other

            let! r = limiter.Admit("scope-b", "tok-shared", rate)
            Expect.isOk r "different scopeId has its own window for the same tokenId"
        }

        testAsync "Window slides forward — old admissions age out" {
            let limiter = factory ()
            // 1-second window is the lower bound documented on
            // `validateRateLimit`. Test sleeps slightly beyond it to
            // verify the sliding behaviour without keeping the test
            // suite slow.
            let rate = mkRate 2 1

            let! _ = limiter.Admit("scope-a", "tok-window", rate)
            let! _ = limiter.Admit("scope-a", "tok-window", rate)
            let! exhausted = limiter.Admit("scope-a", "tok-window", rate)

            match exhausted with
            | Error ShareTokenError.RateLimited -> ()
            | other -> failwithf "expected exhausted, got %A" other

            do! Async.Sleep 1100
            let! r = limiter.Admit("scope-a", "tok-window", rate)
            Expect.isOk r "admission resumes after the window slides"
        }
    ]