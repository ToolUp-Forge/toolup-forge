module ToolUp.Platform.Tests.AI.AISurfaceCapabilityTests

open System
open System.Text
open Microsoft.AspNetCore.Http
open Expecto
open ToolUp.AI

// ─── Phase 6g.F — AISurface trust model ──────────────────────────
//
// `TrustClient` (default) takes the client-supplied
// `AIMessageRequest.Surface` at face value — byte-for-byte the
// pre-6g.F behaviour. `DeriveFromCookie key` derives the authoritative
// surface from a signed capability cookie, so a client lying about
// `Surface = FullPage` from a side-panel context is demoted server-side.

let private key = Encoding.UTF8.GetBytes "test-signing-key-0123456789abcdef"
let private otherKey = Encoding.UTF8.GetBytes "a-completely-different-key-xyzzy!"
let private now = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)

let private mintFullPage (lifetime: int) (issuedAt: DateTimeOffset) (signingKey: byte[]) =
    AISurfaceCapability.mintToken AISurfaceCapability.FullPageClaim lifetime issuedAt signingKey

let tests =
    testList "AISurfaceCapability" [
        // ── TrustClient (default) preserves today's behaviour ──
        test "TrustClient trusts a claimed FullPage" {
            let surface, warn = AISurfaceCapability.resolveSurface TrustClient now None FullPage
            Expect.equal surface FullPage "claimed FullPage is trusted"
            Expect.isNone warn "TrustClient never reports a mismatch"
        }

        test "TrustClient trusts a claimed SidePanel" {
            let surface, warn =
                AISurfaceCapability.resolveSurface TrustClient now None SidePanel

            Expect.equal surface SidePanel "claimed SidePanel is trusted"
            Expect.isNone warn "TrustClient never reports a mismatch"
        }

        test "TrustClient ignores any capability cookie present" {
            let cookie = mintFullPage 3600 now key

            let surface, warn =
                AISurfaceCapability.resolveSurface TrustClient now (Some cookie) SidePanel

            Expect.equal surface SidePanel "TrustClient does not consult the cookie"
            Expect.isNone warn "no mismatch under TrustClient"
        }

        // ── The acceptance case: a lying client is demoted ──
        test "DeriveFromCookie demotes a client claiming FullPage with no cookie" {
            let surface, warn =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now None FullPage

            Expect.equal surface SidePanel "no corroborating cookie => demoted to SidePanel"
            Expect.isSome warn "claimed FullPage vs derived SidePanel is warned"
        }

        test "DeriveFromCookie grants FullPage with a valid signed cookie" {
            let cookie = mintFullPage 3600 now key

            let surface, warn =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now (Some cookie) FullPage

            Expect.equal surface FullPage "valid fullpage cookie grants FullPage"
            Expect.isNone warn "claimed FullPage agrees with derived FullPage"
        }

        test "DeriveFromCookie with a claimed SidePanel + no cookie agrees" {
            let surface, warn =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now None SidePanel

            Expect.equal surface SidePanel "derived SidePanel"
            Expect.isNone warn "claimed SidePanel agrees with derived SidePanel"
        }

        test "DeriveFromCookie demotes on an expired cookie" {
            let issued = now.AddHours(-2.0)
            let cookie = mintFullPage 60 issued key // 60s lifetime, validated 2h later

            let surface, _ =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now (Some cookie) FullPage

            Expect.equal surface SidePanel "expired cookie => demoted"
        }

        test "DeriveFromCookie demotes a cookie signed with a different key" {
            let cookie = mintFullPage 3600 now otherKey

            let surface, _ =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now (Some cookie) FullPage

            Expect.equal surface SidePanel "signature from a different key is rejected"
        }

        test "DeriveFromCookie demotes a tampered token" {
            let cookie = mintFullPage 3600 now key
            let tampered = cookie + "x"

            let surface, _ =
                AISurfaceCapability.resolveSurface (DeriveFromCookie key) now (Some tampered) FullPage

            Expect.equal surface SidePanel "a mutated token fails signature validation"
        }

        // ── Token primitives ──
        test "validateToken round-trips a freshly minted token" {
            let cookie = mintFullPage 3600 now key
            let claim = AISurfaceCapability.validateToken now key cookie
            Expect.equal claim (Some AISurfaceCapability.FullPageClaim) "valid token yields its claim"
        }

        test "validateToken rejects an empty signing key" {
            let cookie = mintFullPage 3600 now key
            let claim = AISurfaceCapability.validateToken now [||] cookie
            Expect.isNone claim "empty key never validates (mis-wired config never grants)"
        }

        // ── HttpContext adapters (the path the handler uses) ──
        test "readCookie reads the capability cookie off a request" {
            let ctx = DefaultHttpContext()
            ctx.Request.Headers.Append("Cookie", $"{AISurfaceCapability.CookieName}=abc123")
            Expect.equal (AISurfaceCapability.readCookie ctx) (Some "abc123") "cookie value is read"
        }

        test "readCookie returns None when the cookie is absent" {
            let ctx = DefaultHttpContext()
            Expect.isNone (AISurfaceCapability.readCookie ctx) "no cookie => None"
        }

        test "resolveSurfaceFromRequest grants FullPage when the request carries a valid cookie" {
            let ctx = DefaultHttpContext()
            let cookie = mintFullPage 3600 now key
            ctx.Request.Headers.Append("Cookie", $"{AISurfaceCapability.CookieName}={cookie}")

            let surface, warn =
                AISurfaceCapability.resolveSurfaceFromRequest (DeriveFromCookie key) now ctx FullPage

            Expect.equal surface FullPage "valid request cookie grants FullPage"
            Expect.isNone warn "no mismatch"
        }

        test "resolveSurfaceFromRequest demotes a lying request with no cookie" {
            let ctx = DefaultHttpContext() // no Cookie header

            let surface, warn =
                AISurfaceCapability.resolveSurfaceFromRequest (DeriveFromCookie key) now ctx FullPage

            Expect.equal surface SidePanel "no cookie on the request => demoted"
            Expect.isSome warn "lying client is warned"
        }

        test "issueFullPageCookie emits a HttpOnly/Secure/SameSite=Strict Set-Cookie" {
            let ctx = DefaultHttpContext()
            AISurfaceCapability.issueFullPageCookie 3600 now key ctx
            let setCookie = ctx.Response.Headers["Set-Cookie"].ToString().ToLowerInvariant()
            Expect.stringContains setCookie AISurfaceCapability.CookieName "cookie name present"
            Expect.stringContains setCookie "httponly" "HttpOnly attribute present"
            Expect.stringContains setCookie "secure" "Secure attribute present"
            Expect.stringContains setCookie "samesite=strict" "SameSite=Strict attribute present"
        }

        test "an issued cookie validates back to the FullPage claim" {
            // Round-trip: issue → read the token out of the Set-Cookie value → validate.
            let ctx = DefaultHttpContext()
            AISurfaceCapability.issueFullPageCookie 3600 now key ctx
            let setCookie = ctx.Response.Headers["Set-Cookie"].ToString()
            // "toolup-ai-surface=<token>; max-age=...; path=/; ..."
            let token =
                setCookie.Substring(AISurfaceCapability.CookieName.Length + 1).Split(';')[0]

            Expect.equal
                (AISurfaceCapability.validateToken now key token)
                (Some AISurfaceCapability.FullPageClaim)
                "the issued cookie validates back to fullpage"
        }
    ]