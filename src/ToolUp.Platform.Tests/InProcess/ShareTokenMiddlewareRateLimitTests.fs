module ToolUp.Platform.Tests.InProcess.ShareTokenMiddlewareRateLimitTests

open System
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 333 — share-token rate limit enforced in the middleware ──
//
// `ShareTokenAuthMiddleware` consults `IShareTokenRateLimiter.Admit`
// when the resolved claim carries a `RateLimit` and a limiter is
// composed, so the per-token rate limit a claim advertises applies on
// every claim-bearer route — not only consumers (Forms) that call
// `Admit` themselves. Denial is 429 + `Retry-After`; a claim without
// a `RateLimit`, or a deployment without a composed limiter, is
// byte-for-byte the prior pass-through (GP 13).

let private claimWith (rateLimit: ShareTokenRateLimit option) : ShareTokenClaim = {
    TokenId = "tok-1"
    ScopeId = "scope-1"
    ResourceKind = "tests.claim-bearer"
    ResourceId = "res-1"
    AttributedHandle = None
    IssuedBy = "tester"
    IssuedAt = DateTimeOffset.UtcNow
    ExpiresAt = DateTimeOffset.UtcNow.AddDays 1.0
    UseLimit = None
    UsedCount = 0
    Revoked = false
    RateLimit = rateLimit
}

/// Store fake: `Validate` always resolves the given claim. Only
/// `Validate` is on the middleware's path; the rest fail loudly.
type private FixedClaimStore(claim: ShareTokenClaim) =
    interface IShareTokenStore with
        member _.Issue _ = failwith "not on the middleware path"
        member _.Validate _ = async { return Ok claim }
        member _.MarkUsed(_, _) = failwith "not on the middleware path"
        member _.Revoke(_, _, _) = failwith "not on the middleware path"
        member _.ListByResource(_, _, _) = failwith "not on the middleware path"
        member _.ListByIssuer(_, _) = failwith "not on the middleware path"

/// Limiter fake: admits the first `budget` calls, denies the rest.
/// Counts every `Admit` so tests can assert the gate was (not) consulted.
type private BudgetLimiter(budget: int) =
    let mutable calls = 0
    member _.AdmitCalls = calls

    interface IShareTokenRateLimiter with
        member _.Admit(_, _, _) = async {
            calls <- calls + 1

            if calls <= budget then
                return Ok()
            else
                return Error ShareTokenError.RateLimited
        }

        member _.IsDistributed = false

/// One request through a fresh `HttpContext` against a shared service
/// provider. Returns the context + whether `next` ran.
let private invoke (sp: IServiceProvider) : HttpContext * bool =
    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Response.Body <- new System.IO.MemoryStream()
    ctx.Request.QueryString <- QueryString("?token=abc123")

    let mutable nextInvoked = false

    let next =
        RequestDelegate(fun _ ->
            nextInvoked <- true
            System.Threading.Tasks.Task.CompletedTask)

    let mw = ShareTokenAuth.ShareTokenAuthMiddleware(next)
    mw.InvokeAsync(ctx) |> Async.AwaitTask |> Async.RunSynchronously
    ctx, nextInvoked

let private buildServices (claim: ShareTokenClaim) (limiter: IShareTokenRateLimiter option) : IServiceProvider =
    let services = ServiceCollection()

    services.AddSingleton<IShareTokenStore>(FixedClaimStore claim :> IShareTokenStore)
    |> ignore

    match limiter with
    | Some l -> services.AddSingleton<IShareTokenRateLimiter> l |> ignore
    | None -> ()

    services.BuildServiceProvider() :> IServiceProvider

let private readBody (ctx: HttpContext) : string =
    ctx.Response.Body.Position <- 0L
    use reader = new System.IO.StreamReader(ctx.Response.Body)
    reader.ReadToEnd()

[<Tests>]
let tests =
    testList "Phase 333 — share-token rate limit enforced in the middleware" [
        test "rate-capped token is throttled with 429 + Retry-After once its budget is exhausted" {
            let rate = {
                MaxUses = 2
                Window = TimeSpan.FromSeconds 60.0
            }

            let limiter = BudgetLimiter 2

            let sp =
                buildServices (claimWith (Some rate)) (Some(limiter :> IShareTokenRateLimiter))

            // Two requests inside the budget pass through with the claim stashed.
            for _ in 1..2 do
                let ctx, nextInvoked = invoke sp
                Expect.isTrue nextInvoked "in-budget request continues the pipeline"

                Expect.isTrue
                    (ctx.Items.ContainsKey ShareTokenAuth.ShareTokenClaimItemsKey)
                    "in-budget request stashes the claim"

            // The third is denied: 429, Retry-After = the window, pipeline halted.
            let ctx, nextInvoked = invoke sp
            Expect.isFalse nextInvoked "rate-limited request never reaches the handler"
            Expect.equal ctx.Response.StatusCode 429 "429 Too Many Requests"

            Expect.equal
                (string ctx.Response.Headers["Retry-After"])
                "60"
                "Retry-After is the claim's window in seconds"

            Expect.isFalse
                (ctx.Items.ContainsKey ShareTokenAuth.ShareTokenClaimItemsKey)
                "rate-limited request does not stash the claim"

            Expect.stringContains (readBody ctx) "rate_limited" "body carries the rate_limited error code"
            Expect.equal limiter.AdmitCalls 3 "every rate-capped request consulted Admit"
        }

        test "token without a RateLimit never consults the limiter" {
            // A zero-budget limiter would deny anything it is asked about —
            // proving it is never asked.
            let limiter = BudgetLimiter 0
            let sp = buildServices (claimWith None) (Some(limiter :> IShareTokenRateLimiter))

            let ctx, nextInvoked = invoke sp
            Expect.isTrue nextInvoked "no RateLimit on the claim = pass-through"

            Expect.isTrue (ctx.Items.ContainsKey ShareTokenAuth.ShareTokenClaimItemsKey) "claim stashed as before"

            Expect.equal limiter.AdmitCalls 0 "Admit never consulted for a claim without a RateLimit"
        }

        test "no limiter composed → rate-capped token passes through unchanged" {
            let rate = {
                MaxUses = 1
                Window = TimeSpan.FromSeconds 60.0
            }

            let sp = buildServices (claimWith (Some rate)) None

            // More requests than the declared MaxUses — with no limiter
            // composed there is nothing to enforce it (GP 13 opt-in).
            for _ in 1..3 do
                let ctx, nextInvoked = invoke sp
                Expect.isTrue nextInvoked "no limiter composed = pass-through"
                Expect.equal ctx.Response.StatusCode 200 "response untouched"

                Expect.isTrue (ctx.Items.ContainsKey ShareTokenAuth.ShareTokenClaimItemsKey) "claim stashed as before"
        }
    ]