module ToolUp.Stripe.Webhook.Tests.WebhookSignerTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Stripe.Webhook

let private secret = "whsec_test_32_byte_minimum_padding"

let private signed (now: DateTimeOffset) (secret: string) (body: string) : string =
    let timestamp = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" timestamp body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    let sigBytes = h.ComputeHash(Encoding.UTF8.GetBytes payload)
    let sigHex = Convert.ToHexString(sigBytes).ToLowerInvariant()
    sprintf "t=%d,v1=%s" timestamp sigHex

[<Tests>]
let tests =
    testList "WebhookSigner" [
        test "valid signature returns Ok with the original body + timestamp" {
            let now = DateTimeOffset.UtcNow
            let body = """{"type":"customer.subscription.created","id":"evt_test"}"""
            let header = signed now secret body

            match WebhookSigner.verifyWith now secret body header with
            | Ok verified ->
                Expect.equal verified.Body body "body round-trip"
                Expect.equal verified.Timestamp (now.ToUnixTimeSeconds()) "timestamp round-trip"
            | Error e -> failwithf "expected Ok, got %A" e
        }
        test "tampered body fails with SignatureMismatch" {
            let now = DateTimeOffset.UtcNow
            let body = """{"id":"evt_test"}"""
            let header = signed now secret body

            // Header was signed for `body`; verify against a different body.
            let tampered = """{"id":"evt_pwned"}"""

            match WebhookSigner.verifyWith now secret tampered header with
            | Error SignatureMismatch -> ()
            | other -> failwithf "expected SignatureMismatch, got %A" other
        }
        test "malformed header (missing v1=) fails" {
            let now = DateTimeOffset.UtcNow
            let timestamp = now.ToUnixTimeSeconds()
            let bad = sprintf "t=%d" timestamp

            match WebhookSigner.verifyWith now secret "{}" bad with
            | Error MalformedHeader -> ()
            | other -> failwithf "expected MalformedHeader, got %A" other
        }
        test "malformed header (missing t=) fails" {
            let bad = "v1=deadbeef"

            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret "{}" bad with
            | Error MalformedHeader -> ()
            | other -> failwithf "expected MalformedHeader, got %A" other
        }
        test "malformed header (non-integer t=) fails" {
            let bad = "t=not-a-number,v1=deadbeef"

            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret "{}" bad with
            | Error MalformedHeader -> ()
            | other -> failwithf "expected MalformedHeader, got %A" other
        }
        test "empty header fails" {
            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret "{}" "" with
            | Error MalformedHeader -> ()
            | other -> failwithf "expected MalformedHeader, got %A" other
        }
        test "stale timestamp (>5 min in the past) fails with TimestampDrift" {
            // Header signed 10 minutes ago.
            let past = DateTimeOffset.UtcNow.AddMinutes(-10.0)
            let body = "{}"
            let header = signed past secret body

            // verifyWith uses `now` (= UtcNow). The drift is +600s.
            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret body header with
            | Error(TimestampDrift seconds) -> Expect.isGreaterThan seconds 300L "drift > 5 min"
            | other -> failwithf "expected TimestampDrift, got %A" other
        }
        test "future timestamp (>5 min in the future) fails with TimestampDrift" {
            let future = DateTimeOffset.UtcNow.AddMinutes(10.0)
            let body = "{}"
            let header = signed future secret body

            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret body header with
            | Error(TimestampDrift seconds) -> Expect.isLessThan seconds -300L "drift < -5 min"
            | other -> failwithf "expected TimestampDrift, got %A" other
        }
        test "fresh timestamp (within 5 min window) passes" {
            // 4 minutes in the past — inside the window.
            let recent = DateTimeOffset.UtcNow.AddMinutes(-4.0)
            let body = "{}"
            let header = signed recent secret body

            match WebhookSigner.verifyWith DateTimeOffset.UtcNow secret body header with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok, got %A" e
        }
        test "unknown event type does NOT error — body passed through opaquely" {
            // v0.1.0-alpha doesn't decode the payload; the typed
            // StripeEvent DU lands at Phase 04. So an "unknown" event
            // body is just returned in VerifiedEvent.Body.
            let now = DateTimeOffset.UtcNow
            let body = """{"type":"some.unknown.event","id":"evt_x"}"""
            let header = signed now secret body

            match WebhookSigner.verifyWith now secret body header with
            | Ok verified -> Expect.equal verified.Body body "unknown event body returned as-is"
            | Error e -> failwithf "expected Ok (no decode), got %A" e
        }
        test "verify (no `now`) uses UtcNow internally" {
            let now = DateTimeOffset.UtcNow
            let body = "{}"
            let header = signed now secret body

            match WebhookSigner.verify secret body header with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok via UtcNow-internal verify, got %A" e
        }
    ]