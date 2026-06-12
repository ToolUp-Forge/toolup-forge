module ToolUp.Platform.Tests.InProcess.ShareTokenAuthMiddlewareTests

open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

// ─── Phase 136 — share-link token-leak hardening ────────────────────
//
// A token-bearing request (?token= or X-Share-Token) carries the bearer
// secret, so the response must not leak it via Referer / be cached by
// an intermediary. The middleware stamps Referrer-Policy: no-referrer +
// Cache-Control: no-store on any token-bearing response.

let private invoke (setup: HttpContext -> unit) : HttpContext =
    let services = ServiceCollection()
    let sp = services.BuildServiceProvider()

    let ctx = DefaultHttpContext() :> HttpContext
    ctx.RequestServices <- sp
    ctx.Response.Body <- new System.IO.MemoryStream()
    setup ctx

    let next = RequestDelegate(fun _ -> System.Threading.Tasks.Task.CompletedTask)

    let mw = ShareTokenAuth.ShareTokenAuthMiddleware(next)
    mw.InvokeAsync(ctx) |> Async.AwaitTask |> Async.RunSynchronously
    ctx

[<Tests>]
let tests =
    testList "Phase 136 — share-link token-leak hardening" [
        test "token-bearing response carries Referrer-Policy: no-referrer + Cache-Control: no-store" {
            let ctx = invoke (fun c -> c.Request.QueryString <- QueryString("?token=abc123"))

            Expect.equal (string ctx.Response.Headers["Referrer-Policy"]) "no-referrer" "no-referrer stamped"
            Expect.equal (string ctx.Response.Headers["Cache-Control"]) "no-store" "no-store stamped"
        }

        test "non-token request is not stamped (pure pass-through)" {
            let ctx = invoke (fun _ -> ())

            Expect.isFalse (ctx.Response.Headers.ContainsKey "Referrer-Policy") "no stamping without a token"
        }

        test "a downstream-set Referrer-Policy is not overwritten (override wins)" {
            let ctx =
                invoke (fun c ->
                    c.Request.QueryString <- QueryString("?token=abc123")
                    c.Response.Headers["Referrer-Policy"] <- Microsoft.Extensions.Primitives.StringValues "origin")

            Expect.equal (string ctx.Response.Headers["Referrer-Policy"]) "origin" "pre-set value preserved"
        }
    ]