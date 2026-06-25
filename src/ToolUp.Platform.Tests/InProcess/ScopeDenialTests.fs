module ToolUp.Platform.Tests.InProcess.ScopeDenialTests

open Expecto
open ToolUp.Platform
open ToolUp.Remoting.Client

// ─── Phase 227 (task #4) — typed scope-denial classification pack ────
//
// `ScopeDenial.ofException` maps a remoting `ProxyRequestException`
// carrying a `SurfaceEnforcementMiddleware` rejection body into a typed,
// client-recognisable result so a module's `ofError` handler can tell
// "no team yet" (recoverable → the no-active-team onboarding surface)
// apart from "forbidden" (no client-actionable next step) without
// scraping status codes / error-string fragments. The parser is pure F#
// (Fable.SimpleJson, not JS interop), so it is exercised here in the
// .NET test harness exactly as it runs under Fable.

/// Build a `ProxyRequestException` the way `Proxy.fs` does on a non-2xx
/// response — the body string is the wire envelope the server wrote.
let private proxyError (statusCode: int) (body: string) : exn =
    let response: HttpResponse = {
        StatusCode = statusCode
        ResponseBody = body
    }

    ProxyRequestException(response, sprintf "HTTP %d" statusCode, body) :> exn

let tests =
    testList "ScopeDenial" [

        test "403 team_required + select_team hint → NeedsActiveTeam" {
            let ex =
                proxyError 403 """{"error":"team_required","status":403,"hint":"select_team"}"""

            Expect.equal
                (ScopeDenial.ofException ex)
                (Some ScopeDenial.NeedsActiveTeam)
                "the signed-in no-active-team caller is the recoverable onboarding case"
        }

        test "401 authentication_required → NeedsAuthentication" {
            let ex = proxyError 401 """{"error":"authentication_required","status":401}"""

            Expect.equal (ScopeDenial.ofException ex) (Some ScopeDenial.NeedsAuthentication) "no credentials presented"
        }

        test "403 user_subject_not_admitted → Forbidden with the raw code" {
            let ex = proxyError 403 """{"error":"user_subject_not_admitted","status":403}"""

            Expect.equal
                (ScopeDenial.ofException ex)
                (Some(ScopeDenial.Forbidden "user_subject_not_admitted"))
                "authenticated but route closed — no client-actionable next step"
        }

        test "403 team_member_not_admitted → Forbidden" {
            let ex = proxyError 403 """{"error":"team_member_not_admitted","status":403}"""

            Expect.equal
                (ScopeDenial.ofException ex)
                (Some(ScopeDenial.Forbidden "team_member_not_admitted"))
                "every non-team/non-auth surface denial classifies as Forbidden"
        }

        test "hint alone (no team_required code) still classifies NeedsActiveTeam" {
            // Defensive: classify on the actionable hint even if the
            // server ever emits select_team under a different error code.
            let ex = proxyError 403 """{"error":"something_else","hint":"select_team"}"""

            Expect.equal
                (ScopeDenial.ofException ex)
                (Some ScopeDenial.NeedsActiveTeam)
                "the select_team hint is the actionable signal"
        }

        test "non-denial status code (500) → None" {
            let ex = proxyError 500 """{"error":"internal_server_error","status":500}"""

            Expect.equal (ScopeDenial.ofException ex) None "only 401/403 are scope denials"
        }

        test "404 → None (not a scope denial)" {
            let ex = proxyError 404 """{"error":"not_found","status":404}"""
            Expect.equal (ScopeDenial.ofException ex) None "404 is not a surface-enforcement denial"
        }

        test "403 with a non-JSON body → None (falls through to generic path)" {
            let ex = proxyError 403 "Forbidden"
            Expect.equal (ScopeDenial.ofException ex) None "unparseable body is not a recognisable envelope"
        }

        test "403 with JSON lacking an error field → None" {
            let ex = proxyError 403 """{"status":403}"""
            Expect.equal (ScopeDenial.ofException ex) None "no error code → not classifiable"
        }

        test "a plain exception (transport / timeout) → None" {
            Expect.equal
                (ScopeDenial.ofException (exn "network down"))
                None
                "only ProxyRequestException carries a rejection envelope"
        }

        test "ofResponseBody is callable directly for a pre-read body" {
            Expect.equal
                (ScopeDenial.ofResponseBody """{"error":"team_required","hint":"select_team"}""")
                (Some ScopeDenial.NeedsActiveTeam)
                "the body parser is exposed independently of the exception wrapper"
        }
    ]