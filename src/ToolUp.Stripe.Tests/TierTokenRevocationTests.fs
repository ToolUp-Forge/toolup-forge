module ToolUp.Stripe.Tests.TierTokenRevocationTests

open System
open System.Text
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Stripe.TierToken

// ─── Phase 340 — tier-token revocation + cookie-edge hardening ───────
//
// Before this phase the tier cookie was a stateless signed claim whose
// only bound was `exp`: a cancelled, charged-back or leaked token kept
// granting its tier until it naturally expired, and nothing could
// withdraw it. Revocation is a per-subject epoch stamped inside the
// signature and re-checked server-side on every resolve.
//
// The three properties this pack pins, in the order they matter:
//
//   1. **A bumped epoch stops the tier before `exp`** — the feature.
//   2. **A revocable token cannot be resolved by a path that does not
//      check the epoch** — the failure mode that would silently undo (1).
//      `Token.validate` refuses it rather than degrading to the tier.
//   3. **Legacy three-part tokens are untouched** — GP 11. Every legacy
//      assertion below is a regression guard, not a new capability.
//
// Plus the two cookie edges surfaced alongside: `clear` now mirrors the
// issue-path attributes (an unmirrored clear can leave the real cookie
// live), and the insecure-cookie downgrade is inert on a production-shaped
// host.

let private secret = Encoding.UTF8.GetBytes "tier-revocation-secret-32-bytes!!"

let private now = DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero)

/// An `EpochLookup` over a fixed table — `None` for any subject the table
/// does not name, which is the "deleted user" shape.
let private lookupFrom (table: (string * int64) list) : EpochLookup =
    fun subject -> async { return table |> List.tryFind (fun (s, _) -> s = subject) |> Option.map snd }

let private mintFor tier subject epoch lifetime =
    match Token.mintFor tier subject epoch lifetime now secret with
    | Ok t -> t
    | Error e -> failtestf "mintFor failed: %A" e

let private mintLegacy tier lifetime =
    match Token.mint tier lifetime now secret with
    | Ok t -> t
    | Error e -> failtestf "mint failed: %A" e

let private resolve lookup token =
    Token.validateWithEpoch (lookupFrom lookup) now token secret
    |> Async.RunSynchronously

/// Split a token into its parts, replace one, and re-join — used to prove
/// each new field is genuinely inside the signed payload.
let private tamper (index: int) (replacement: string) (token: string) =
    let parts = token.Split('.')
    parts[index] <- replacement
    String.Join(".", parts)

// ─── Cookie helpers ──────────────────────────────────────────────────

let private cookieConfig name envVar : CookieConfig = {
    CookieName = name
    InsecureCookiesEnvVar = envVar
}

let private ctxOn (host: string) =
    let ctx = DefaultHttpContext()
    ctx.Request.Host <- HostString host
    ctx

/// The `Set-Cookie` header the response carries, as one string.
let private setCookieHeader (ctx: HttpContext) =
    let values = ctx.Response.Headers["Set-Cookie"]
    String.Join(" | ", values.ToArray() |> Array.map (fun v -> if isNull v then "" else v))

[<Tests>]
let tests =
    testList "Phase 340 — tier-token revocation" [

        // ─── 1. The feature ──────────────────────────────────────────

        test "a revocable token resolves its tier while the epoch matches" {
            let token = mintFor Tier.Pro "user-1" 7L 3600

            match resolve [ "user-1", 7L ] token with
            | Ok tier -> Expect.equal tier Tier.Pro "current epoch grants the tier"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "bumping the subject's epoch revokes the token BEFORE exp" {
            // The whole point: same token, same clock, still well inside
            // its hour-long lifetime — and it stops granting the tier
            // purely because the server-side epoch moved.
            let token = mintFor Tier.Pro "user-1" 7L 3600

            Expect.equal (resolve [ "user-1", 7L ] token) (Ok Tier.Pro) "live before the bump"

            match resolve [ "user-1", 8L ] token with
            | Error Revoked -> ()
            | other -> failtestf "expected Revoked after the epoch bump, got %A" other
        }

        test "revocation is per-subject — bumping one subject leaves another alone" {
            let mine = mintFor Tier.Personal "user-1" 3L 3600
            let theirs = mintFor Tier.Personal "user-2" 3L 3600
            let table = [ "user-1", 4L; "user-2", 3L ]

            Expect.equal (resolve table mine) (Error Revoked) "bumped subject is revoked"
            Expect.equal (resolve table theirs) (Ok Tier.Personal) "untouched subject is unaffected"
        }

        test "a token stamped ABOVE the current epoch still resolves (monotonic, not equality)" {
            // A token minted at epoch N remains valid at every epoch ≤ N.
            // Equality would revoke every outstanding token the moment a
            // replica lagged, which is an availability bug wearing a
            // security hat.
            let token = mintFor Tier.Teacher "user-1" 9L 3600
            Expect.equal (resolve [ "user-1", 5L ] token) (Ok Tier.Teacher) "stamped above current is live"
        }

        test "an unknown subject fails closed with SubjectUnknown" {
            // A deleted account and a subject the lookup has never heard
            // of are indistinguishable, so the safe reading is the strict
            // one — a deleted user must not keep a paid tier.
            let token = mintFor Tier.Enterprise "ghost" 1L 3600

            match resolve [ "user-1", 1L ] token with
            | Error SubjectUnknown -> ()
            | other -> failtestf "expected SubjectUnknown, got %A" other
        }

        test "an expired revocable token is Expired, and the lookup is never consulted" {
            // Ordering guard: expiry is cheap and local, revocation is a
            // remote read. An expired token must not cost a lookup.
            let consulted = ref 0

            let counting: EpochLookup =
                fun _ -> async {
                    incr consulted
                    return Some 0L
                }

            let token =
                match Token.mintFor Tier.Pro "user-1" 0L 1 (now.AddHours -2.0) secret with
                | Ok t -> t
                | Error e -> failtestf "mintFor failed: %A" e

            match Token.validateWithEpoch counting now token secret |> Async.RunSynchronously with
            | Error Expired -> ()
            | other -> failtestf "expected Expired, got %A" other

            Expect.equal consulted.Value 0 "no revocation lookup for an already-expired token"
        }

        // ─── 2. The failure mode that would undo the feature ─────────

        test "Token.validate REFUSES a revocable token rather than granting an unchecked tier" {
            // If the revocation-unaware path quietly returned the tier, a
            // deployment could mint revocable tokens, resolve them through
            // the old function, and have a revocation feature that revokes
            // nothing — green, and wrong.
            let token = mintFor Tier.Enterprise "user-1" 2L 3600

            match Token.validate now token secret with
            | Error RevocationCheckRequired -> ()
            | other -> failtestf "expected RevocationCheckRequired, got %A" other
        }

        test "Cookie.resolveFromRequest yields None for a revocable cookie (same fail-closed shape)" {
            let config = cookieConfig "tier" None
            let ctx = ctxOn "app.example.com"

            ctx.Request.Headers.Cookie <-
                Microsoft.Extensions.Primitives.StringValues("tier=" + mintFor Tier.Pro "user-1" 1L 3600)

            Expect.isNone
                (Cookie.resolveFromRequest config ctx now secret)
                "the epoch-unaware resolve must not grant a tier it cannot check"
        }

        test "the epoch field is inside the signed payload — editing it is a SignatureMismatch" {
            let token = mintFor Tier.Pro "user-1" 7L 3600
            // Index 3 is the epoch in {tier}.{exp}.{subject}.{epoch}.{sig}.
            let forged = tamper 3 "999" token

            match resolve [ "user-1", 8L ] forged with
            | Error SignatureMismatch -> ()
            | other -> failtestf "expected SignatureMismatch on a forged epoch, got %A" other
        }

        test "the subject field is inside the signed payload — swapping it is a SignatureMismatch" {
            // Otherwise a revoked user could point their token at somebody
            // else's un-bumped epoch.
            let token = mintFor Tier.Pro "user-1" 7L 3600

            let otherSubject =
                Convert.ToBase64String(Encoding.UTF8.GetBytes "user-2").Replace('+', '-').Replace('/', '_').TrimEnd('=')

            let forged = tamper 2 otherSubject token

            match resolve [ "user-1", 8L; "user-2", 7L ] forged with
            | Error SignatureMismatch -> ()
            | other -> failtestf "expected SignatureMismatch on a swapped subject, got %A" other
        }

        test "a truncated revocable token (epoch fields dropped) does not degrade to a legacy token" {
            // Dropping the last three parts leaves {tier}.{exp} — the
            // shape a legacy token has minus its signature. It must be
            // rejected, never re-read as an unrevocable grant.
            let token = mintFor Tier.Pro "user-1" 7L 3600
            let parts = token.Split('.')
            let truncated = String.Join(".", parts[0..1])

            match resolve [ "user-1", 8L ] truncated with
            | Error MalformedToken -> ()
            | other -> failtestf "expected MalformedToken, got %A" other
        }

        // ─── 3. Legacy tokens are untouched (GP 11) ──────────────────

        test "a legacy three-part token still validates through Token.validate" {
            let token = mintLegacy Tier.Teacher 3600

            match Token.validate now token secret with
            | Ok tier -> Expect.equal tier Tier.Teacher "legacy round-trip unchanged"
            | Error e -> failtestf "legacy token must still validate: %A" e
        }

        test "a legacy token passes through validateWithEpoch and never consults the lookup" {
            // This is what lets a deployment move its resolve path first
            // and start minting revocable tokens afterwards, with no flag
            // day and no window where live cookies stop working.
            let consulted = ref 0

            let counting: EpochLookup =
                fun _ -> async {
                    incr consulted
                    return None
                }

            let token = mintLegacy Tier.Personal 3600

            match Token.validateWithEpoch counting now token secret |> Async.RunSynchronously with
            | Ok tier -> Expect.equal tier Tier.Personal "legacy tier resolves"
            | Error e -> failtestf "expected Ok, got %A" e

            // Note the lookup returns None (= SubjectUnknown) for anything
            // it is asked about, so a legacy token that DID reach it would
            // fail — the count assertion is belt to that braces.
            Expect.equal consulted.Value 0 "a subject-less token has nothing to look up"
        }

        // ─── mintFor argument gates ──────────────────────────────────

        test "mintFor rejects a blank subject" {
            for blank in [ ""; "   " ] do
                match Token.mintFor Tier.Pro blank 1L 3600 now secret with
                | Error InvalidSubject -> ()
                | other -> failtestf "expected InvalidSubject for %A, got %A" blank other
        }

        test "mintFor rejects a negative epoch" {
            match Token.mintFor Tier.Pro "user-1" -1L 3600 now secret with
            | Error InvalidEpoch -> ()
            | other -> failtestf "expected InvalidEpoch, got %A" other
        }

        test "mintFor inherits the Phase 332 secret-strength floor and the lifetime gate" {
            match Token.mintFor Tier.Pro "user-1" 1L 3600 now (Encoding.UTF8.GetBytes "short") with
            | Error MintError.SecretMissing -> ()
            | other -> failtestf "expected SecretMissing for a sub-32-byte key, got %A" other

            match Token.mintFor Tier.Pro "user-1" 1L 0 now secret with
            | Error InvalidLifetime -> ()
            | other -> failtestf "expected InvalidLifetime, got %A" other
        }

        test "a subject containing dots survives the round-trip (base64-url field encoding)" {
            // The reason the subject is encoded rather than interpolated
            // raw: an email-shaped subject would otherwise shift every
            // field boundary after it.
            let subject = "alice.smith@example.com"
            let token = mintFor Tier.Personal subject 4L 3600

            Expect.equal (resolve [ subject, 4L ] token) (Ok Tier.Personal) "dotted subject resolves"
            Expect.equal (resolve [ subject, 5L ] token) (Error Revoked) "and is still revocable"
        }

        test "Token.inspect surfaces the claims without deciding revocation" {
            let token = mintFor Tier.Pro "user-1" 7L 3600

            match Token.inspect now token secret with
            | Ok claims ->
                Expect.equal claims.Tier Tier.Pro "tier"
                Expect.equal claims.Subject (Some "user-1") "subject decoded"
                Expect.equal claims.Epoch (Some 7L) "epoch parsed"
                Expect.equal claims.ExpiresAt (now.AddSeconds 3600.0) "exp reconstructed"
            | Error e -> failtestf "expected Ok, got %A" e

            match Token.inspect now (mintLegacy Tier.Free 3600) secret with
            | Ok claims ->
                Expect.isNone claims.Subject "legacy token carries no subject"
                Expect.isNone claims.Epoch "legacy token carries no epoch"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "the recommended maximum cookie lifetime is documented as 24 hours" {
            Expect.equal Token.RecommendedMaxLifetimeSeconds 86400 "24h ceiling"
        }
    ]

[<Tests>]
let cookieEdgeTests =
    testList "Phase 340 — tier-cookie edge hardening" [

        test "Cookie.clear mirrors the issue-path attributes" {
            // An unmirrored clear is not merely untidy: a browser matches
            // a replacement on name + domain + path and can reject or
            // partition one whose security attributes disagree, so the
            // cookie the signout was supposed to remove survives it.
            let config = cookieConfig "tier" None

            let issued =
                let ctx = ctxOn "app.example.com"
                Cookie.issue config ctx Tier.Pro 3600 secret |> ignore
                setCookieHeader ctx

            let cleared =
                let ctx = ctxOn "app.example.com"
                Cookie.clear config ctx
                setCookieHeader ctx

            for attribute in [ "httponly"; "secure"; "samesite=lax"; "path=/" ] do
                Expect.stringContains (issued.ToLowerInvariant()) attribute (sprintf "issue sets %s" attribute)
                Expect.stringContains (cleared.ToLowerInvariant()) attribute (sprintf "clear mirrors %s" attribute)

            Expect.stringContains cleared "tier=;" "clear empties the value"
            Expect.stringContains (cleared.ToLowerInvariant()) "expires=" "clear back-dates the cookie"
        }

        test "the insecure-cookie downgrade is INERT on a production-shaped host" {
            // The env var alone used to be enough, so an env var set once
            // in a shared compose file (or baked into a promoted image)
            // shipped a non-Secure tier cookie over the public internet.
            let varName = "TOOLUP_TEST_INSECURE_COOKIES_340"
            Environment.SetEnvironmentVariable(varName, "1")

            try
                let config = cookieConfig "tier" (Some varName)
                let ctx = ctxOn "app.example.com"

                Expect.isFalse (Cookie.insecureDowngradeApplies config ctx) "downgrade refused on a public host"

                Cookie.issue config ctx Tier.Pro 3600 secret |> ignore

                Expect.stringContains
                    ((setCookieHeader ctx).ToLowerInvariant())
                    "secure"
                    "cookie stays Secure despite the flag"
            finally
                Environment.SetEnvironmentVariable(varName, null)
        }

        test "the insecure-cookie downgrade still works on localhost (the intended use)" {
            let varName = "TOOLUP_TEST_INSECURE_COOKIES_340_LOCAL"
            Environment.SetEnvironmentVariable(varName, "1")

            try
                let config = cookieConfig "tier" (Some varName)
                let ctx = ctxOn "localhost:8080"

                Expect.isTrue (Cookie.insecureDowngradeApplies config ctx) "downgrade honoured for local dev"

                Cookie.issue config ctx Tier.Pro 3600 secret |> ignore

                Expect.isFalse
                    ((setCookieHeader ctx).ToLowerInvariant().Contains "secure")
                    "Secure dropped for plain-HTTP local dev"
            finally
                Environment.SetEnvironmentVariable(varName, null)
        }

        test "the downgrade needs the env var too — a local host alone is not enough" {
            let config = cookieConfig "tier" (Some "TOOLUP_TEST_INSECURE_COOKIES_340_UNSET")
            Expect.isFalse (Cookie.insecureDowngradeApplies config (ctxOn "localhost")) "env var unset ⇒ no downgrade"

            let noVar = cookieConfig "tier" None
            Expect.isFalse (Cookie.insecureDowngradeApplies noVar (ctxOn "localhost")) "no env var configured ⇒ never"
        }

        test "isNonProductionHost classifies the host table as intended" {
            let nonProduction = [
                "localhost"
                "localhost:8080"
                "127.0.0.1"
                "127.0.0.1:5001"
                "[::1]"
                "[::1]:5001"
                "0.0.0.0"
                "app.localhost"
                "printer.local"
                "myapp.test"
                "svc.internal"
                // Single-label container / service names have no public TLD.
                "web"
                "stripe-api:3000"
            ]

            let production = [
                "app.example.com"
                "example.com"
                "preview.example.com:443"
                "tenant.example.co.uk"
                // Deliberately included: a host merely CONTAINING a dev
                // token is not a dev host.
                "localhost.example.com"
                "not-local.example.com"
                ""
            ]

            for host in nonProduction do
                Expect.isTrue (Cookie.isNonProductionHost host) (sprintf "%s is non-production" host)

            for host in production do
                Expect.isFalse (Cookie.isNonProductionHost host) (sprintf "%s is production-shaped" host)

            Expect.isFalse (Cookie.isNonProductionHost null) "a missing Host header is never a downgrade"
        }

        test "Cookie.issueFor mints a revocable cookie that resolveFromRequestWithEpoch can revoke" {
            // End-to-end over the cookie surface, not just the token one.
            let config = cookieConfig "tier" None
            let issuing = ctxOn "app.example.com"

            match Cookie.issueFor config issuing Tier.Pro "user-1" 7L 3600 secret with
            | Ok() -> ()
            | Error e -> failtestf "issueFor failed: %A" e

            let token =
                let header = setCookieHeader issuing
                let afterName = header.Substring(header.IndexOf "tier=" + 5)
                afterName.Substring(0, afterName.IndexOf ';')

            let reading = ctxOn "app.example.com"
            reading.Request.Headers.Cookie <- Microsoft.Extensions.Primitives.StringValues("tier=" + token)

            let resolveWith table =
                Cookie.resolveFromRequestWithEpoch config reading DateTimeOffset.UtcNow secret (lookupFrom table)
                |> Async.RunSynchronously

            Expect.equal (resolveWith [ "user-1", 7L ]) (Some Tier.Pro) "live at the stamped epoch"
            Expect.isNone (resolveWith [ "user-1", 8L ]) "revoked once the epoch is bumped"
            Expect.isNone (resolveWith []) "unknown subject fails closed"
        }
    ]