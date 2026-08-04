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

        // ── Phase 464 — per-call secret fetch (rotation without restart) ──
        //
        // The defect these cover: `verify` takes the secret BY VALUE, so a
        // caller that resolves it once at compose (which is what
        // `StripeConfig.WebhookSecret` is) keeps verifying against the
        // superseded value until the process restarts — and every genuine
        // event signed with the rotated secret fails as
        // `SignatureMismatch`, i.e. reported as a forgery rather than as
        // stale configuration.
        //
        // Each case is paired with a control so a green run is evidence
        // about the FETCHER rather than about verification in general.

        testCaseAsync "verifyWithFetcher picks up a rotated secret on the next call, with no reconstruction"
        <| async {
            let now = DateTimeOffset.UtcNow
            let body = """{"type":"invoice.paid","id":"evt_rotated"}"""

            let rotatedSecret = "whsec_rotated_32_byte_minimum_pad!"

            // A mutable "secret store" the deployment rotates underneath a
            // handler that is NOT rebuilt — the whole point.
            let stored = ref secret
            let fetcher () = async { return stored.Value }

            // Before the rotation: an event signed with the ORIGINAL secret
            // verifies.
            let originalHeader = signed now secret body

            match! WebhookSigner.verifyWithFetcherAt now fetcher body originalHeader with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok before rotation, got %A" e

            // CONTROL — the by-value `verifyWith`, closed over the original
            // secret exactly as a compose-time capture would be, REJECTS an
            // event signed with the rotated secret. This is the defect, and
            // without it the assertion below proves nothing.
            let rotatedHeader = signed now rotatedSecret body

            match WebhookSigner.verifyWith now secret body rotatedHeader with
            | Error SignatureMismatch -> ()
            | other ->
                failwithf
                    "control case is no longer a control — a by-value verify accepted the rotated-secret event (%A), so the fetcher assertion below would pass without any per-call fetch"
                    other

            // Rotate the store. The fetcher instance is unchanged; nothing
            // is reconstructed.
            stored.Value <- rotatedSecret

            match! WebhookSigner.verifyWithFetcherAt now fetcher body rotatedHeader with
            | Ok verified -> Expect.equal verified.Body body "the rotated-secret event verifies on the next call"
            | Error e -> failwithf "expected Ok after rotation via the fetcher, got %A" e

            // And the superseded secret stops verifying — a rotation that
            // left the old secret working would not be a rotation.
            match! WebhookSigner.verifyWithFetcherAt now fetcher body originalHeader with
            | Error SignatureMismatch -> ()
            | other -> failwithf "expected SignatureMismatch for the superseded secret, got %A" other
        }

        testCaseAsync "the fetcher is consulted on EVERY call, not memoised"
        <| async {
            // A fetcher called once and cached would look identical to a
            // working one in the case above if the rotation happened before
            // the first call. Count the invocations.
            let now = DateTimeOffset.UtcNow
            let body = "{}"
            let header = signed now secret body
            let calls = ref 0

            let fetcher () = async {
                calls.Value <- calls.Value + 1
                return secret
            }

            for _ in 1..3 do
                match! WebhookSigner.verifyWithFetcherAt now fetcher body header with
                | Ok _ -> ()
                | Error e -> failwithf "expected Ok, got %A" e

            Expect.equal calls.Value 3 "the secret is resolved once per verify, never cached inside the module"
        }

        testCaseAsync "a fetcher returning a blank or weak secret fails closed as SecretMissing"
        <| async {
            // The fail-closed strength gate must apply to the fetched value
            // too. An HMAC-SHA256 with an empty key is publicly computable,
            // so a store that has lost the key must never degrade into
            // verifying anything a caller sends.
            let now = DateTimeOffset.UtcNow
            let body = "{}"
            let header = signed now secret body

            for weak in [ ""; "short"; "still_too_short_for_32_bytes" ] do
                match! WebhookSigner.verifyWithFetcherAt now (fun () -> async { return weak }) body header with
                | Error SecretMissing -> ()
                | other -> failwithf "expected SecretMissing for a %d-byte fetched secret, got %A" weak.Length other

            // CONTROL — the same call with a strong fetched secret succeeds,
            // so the assertions above are about the STRENGTH GATE rather
            // than about the fetcher path being broken outright.
            match! WebhookSigner.verifyWithFetcherAt now (fun () -> async { return secret }) body header with
            | Ok _ -> ()
            | Error e -> failwithf "control failed — a strong fetched secret should verify, got %A" e
        }

        testCaseAsync "verifyWithFetcher (no `now`) uses UtcNow internally and keeps the drift window"
        <| async {
            let body = "{}"
            let fetcher () = async { return secret }

            // Fresh — inside the 5-minute window.
            let fresh = signed (DateTimeOffset.UtcNow.AddMinutes -1.0) secret body

            match! WebhookSigner.verifyWithFetcher fetcher body fresh with
            | Ok _ -> ()
            | Error e -> failwithf "expected Ok for a fresh timestamp, got %A" e

            // Stale — the fetcher overload must not have loosened the
            // freshness guard on its way to per-call resolution.
            let stale = signed (DateTimeOffset.UtcNow.AddMinutes -10.0) secret body

            match! WebhookSigner.verifyWithFetcher fetcher body stale with
            | Error(TimestampDrift _) -> ()
            | other -> failwithf "expected TimestampDrift for a 10-minute-old event, got %A" other
        }

        testCaseAsync "a throwing fetcher propagates — an unavailable secret store is not reported as a bad signature"
        <| async {
            // Swallowing this into `SecretMissing` would tell the operator a
            // transport outage was a signature problem. The caller classifies
            // it (usually a 503).
            let body = "{}"
            let header = signed DateTimeOffset.UtcNow secret body

            let fetcher () = async { return failwith "secret store unreachable" }

            let! outcome = WebhookSigner.verifyWithFetcher fetcher body header |> Async.Catch

            match outcome with
            | Choice2Of2 ex -> Expect.stringContains ex.Message "unreachable" "the store's own failure surfaces"
            | Choice1Of2 result ->
                failwithf "expected the fetcher's exception to propagate, got a verification result: %A" result
        }
    ]