module ToolUp.Platform.Tests.InProcess.WebhookSubstrateTests

open System
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Webhooks
open ToolUp.Webhooks.Server

// ─── Phase 238 — generic inbound-webhook receiver substrate ──────────
//
// Proves the scheme-driven verifier and the registry/dedup contracts:
//   (1) a valid Stripe-style (t=,v1=) signature verifies;
//   (2) a valid GitHub-style (sha256= over body) signature verifies +
//       extracts the dedup event id;
//   (3) a tampered body / wrong secret → SignatureMismatch (fail-closed);
//   (4) an out-of-window timestamp → TimestampDrift;
//   (5) a missing/malformed signature header → MissingSignature/MalformedHeader;
//   (6) the registry rejects duplicate kinds;
//   (7) the in-memory dedup store claims once, rejects the redelivery.

let private hexHmac (secret: string) (payload: string) : string =
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)
    Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

/// A fixed clock inside the Stripe-style freshness window for `ts`.
let private clockAt (ts: int64) =
    DateTimeOffset.FromUnixTimeSeconds(ts + 10L)

let private headerMap (pairs: (string * string) list) : string -> string option =
    let m = Map.ofList pairs
    fun k -> m.TryFind k

let tests =
    testList "WebhookSubstrate (Phase 238)" [
        test "stripe-style valid signature verifies" {
            let secret = "whsec_test"
            let body = """{"id":"evt_1","type":"x"}"""
            let ts = 1_700_000_000L
            let signature = hexHmac secret (sprintf "%d.%s" ts body)
            let header = headerMap [ "Stripe-Signature", sprintf "t=%d,v1=%s" ts signature ]

            let result =
                WebhookVerifier.verifyWith (clockAt ts) WebhookScheme.stripeStyle "billing" secret body header

            match result with
            | Ok v ->
                Expect.equal v.Kind "billing" "kind preserved"
                Expect.equal v.Timestamp (Some ts) "timestamp parsed"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "github-style valid signature verifies + extracts event id" {
            let secret = "ghsecret"
            let body = """{"action":"opened"}"""
            let signature = "sha256=" + hexHmac secret body

            let header =
                headerMap [ "X-Hub-Signature-256", signature; "X-GitHub-Delivery", "delivery-42" ]

            let result =
                WebhookVerifier.verifyWith DateTimeOffset.UtcNow WebhookScheme.gitHubStyle "push" secret body header

            match result with
            | Ok v -> Expect.equal v.EventId (Some "delivery-42") "event id from header"
            | Error e -> failtestf "expected Ok, got %A" e
        }

        test "tampered body fails closed with SignatureMismatch" {
            let secret = "ghsecret"
            let body = """{"action":"opened"}"""
            let signature = "sha256=" + hexHmac secret body
            let header = headerMap [ "X-Hub-Signature-256", signature ]

            let result =
                WebhookVerifier.verify WebhookScheme.gitHubStyle "push" secret "TAMPERED" header

            Expect.equal result (Error SignatureMismatch) "tampered body rejected"
        }

        test "stale stripe timestamp → TimestampDrift" {
            let secret = "whsec_test"
            let body = "{}"
            let ts = 1_700_000_000L
            let signature = hexHmac secret (sprintf "%d.%s" ts body)
            let header = headerMap [ "Stripe-Signature", sprintf "t=%d,v1=%s" ts signature ]
            // Clock 10 minutes after ts — outside the 5-minute window.
            let lateClock = DateTimeOffset.FromUnixTimeSeconds(ts + 600L)

            let result =
                WebhookVerifier.verifyWith lateClock WebhookScheme.stripeStyle "billing" secret body header

            match result with
            | Error(TimestampDrift _) -> ()
            | other -> failtestf "expected TimestampDrift, got %A" other
        }

        test "missing signature header → MissingSignature" {
            let result =
                WebhookVerifier.verify WebhookScheme.gitHubStyle "push" "s" "{}" (headerMap [])

            Expect.equal result (Error MissingSignature) "absent header rejected"
        }

        test "malformed stripe header → MalformedHeader" {
            let header = headerMap [ "Stripe-Signature", "garbage-no-elements" ]

            let result =
                WebhookVerifier.verify WebhookScheme.stripeStyle "billing" "s" "{}" header

            Expect.equal result (Error MalformedHeader) "unparseable header rejected"
        }

        test "registry rejects duplicate kinds" {
            let mk kind =
                { new IInboundWebhookHandler with
                    member _.Kind = kind
                    member _.Handle _ = async { return Acknowledged }
                }

            match WebhookRegistry.tryOfList [ mk "a"; mk "a" ] with
            | Error _ -> ()
            | Ok _ -> failtest "expected duplicate-kind rejection"
        }

        test "in-memory dedup claims once then rejects redelivery" {
            let store = InMemoryWebhookDedupStore() :> IWebhookDedupStore
            let first = store.TryClaim("k", "evt_1") |> Async.RunSynchronously
            let second = store.TryClaim("k", "evt_1") |> Async.RunSynchronously
            Expect.isTrue first "first claim succeeds"
            Expect.isFalse second "redelivery rejected"
        }
    ]