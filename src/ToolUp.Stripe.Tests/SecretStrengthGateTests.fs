module ToolUp.Stripe.Tests.SecretStrengthGateTests

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Stripe.Webhook
open ToolUp.Stripe.TierToken
open ToolUp.Stripe.Server
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform

// Aliased, NOT opened: `ValidationResult.Ok`/`Error` would otherwise shadow
// `Result.Ok`/`Error` and break the `| Error …` matches on the webhook /
// tier / peer results.
module CV = ToolUp.Platform.ConfigValidation

// ─── Phase 332 — reject empty / weak HMAC secrets (billing + peer) ───
//
// A blank signing secret turns HMAC verification into a publicly-computable
// MAC: HMAC-SHA256(key="", …) is forgeable by anyone, so an unset
// `WebhookSecret` (or peer signing key) would let a forged event / token
// "verify". These tests pin the fail-closed guards across the three signing
// packages plus the boot-time StripeConfig validator, and assert a
// well-formed secret is unchanged (GP 11).

/// A secret comfortably over the 32-byte floor — the well-formed case.
let private strongSecret = "whsec_test_32_byte_minimum_padding"

/// Build a genuine `Stripe-Signature` header for `body` signed with
/// `secret` at `now` — the exact bytes `WebhookSigner` recomputes.
let private signHeader (now: DateTimeOffset) (secret: string) (body: string) : string =
    let ts = now.ToUnixTimeSeconds()
    let payload = sprintf "%d.%s" ts body
    use h = new HMACSHA256(Encoding.UTF8.GetBytes secret)

    let hex =
        Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes payload)).ToLowerInvariant()

    sprintf "t=%d,v1=%s" ts hex

/// Minimal in-memory `ISecretStore` for the peer-auth guard tests.
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    member _.Seed(scopeId, key, value) = store[(scopeId, key)] <- value

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Result.Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Result.Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private peerA: PeerIdentity = {
    PeerId = "peer-a"
    DisplayName = "Peer A"
}

let private peerB: PeerIdentity = {
    PeerId = "peer-b"
    DisplayName = "Peer B"
}

/// Seed a peer's signing key at the exact scope/key `JwtPeerAuthProvider`
/// reads on every issue / validate.
let private seedKey (store: InMemorySecretStore) (peerId: string) (key: string) =
    store.Seed("_platform", $"peers/{peerId}/signing-key", key)

/// Run the boot-time Stripe validator against a config, synchronously.
let private validateStripe (cfg: StripeConfig) : CV.ValidationResult =
    (StripeConfigValidator(cfg) :> CV.IConfigValidator).Validate()
    |> Async.RunSynchronously

[<Tests>]
let tests =
    testList "Phase332.SecretStrengthGate" [

        // ─── WebhookSigner — the forged-payment surface ──────────────
        testList "WebhookSigner" [
            test "empty secret NEVER verifies — returns SecretMissing, not Ok" {
                // The attacker's header is a *valid* HMAC for the empty key —
                // the guard must fire before the HMAC is even recomputed.
                let now = DateTimeOffset.UtcNow
                let body = """{"type":"checkout.session.completed","id":"evt_forged"}"""
                let forged = signHeader now "" body

                match WebhookSigner.verifyWith now "" body forged with
                | Error WebhookError.SecretMissing -> ()
                | other -> failwithf "empty secret must fail closed with SecretMissing, got %A" other
            }
            test "too-short (below 32 bytes) secret fails closed" {
                let now = DateTimeOffset.UtcNow
                let body = "{}"
                let shortSecret = "whsec_short" // 11 bytes
                let header = signHeader now shortSecret body

                match WebhookSigner.verifyWith now shortSecret body header with
                | Error WebhookError.SecretMissing -> ()
                | other -> failwithf "short secret must fail closed with SecretMissing, got %A" other
            }
            test "well-formed secret still verifies (GP 11)" {
                let now = DateTimeOffset.UtcNow
                let body = """{"type":"invoice.paid","id":"evt_ok"}"""
                let header = signHeader now strongSecret body

                match WebhookSigner.verifyWith now strongSecret body header with
                | Ok verified -> Expect.equal verified.Body body "body round-trip unchanged"
                | Error e -> failwithf "well-formed secret must verify, got %A" e
            }
        ]

        // ─── TierToken — mint / validate fail closed on weak keys ────
        testList "TierToken.Token" [
            test "empty secret: mint fails closed" {
                match Token.mint Tier.Personal 3600 DateTimeOffset.UtcNow [||] with
                | Error MintError.SecretMissing -> ()
                | other -> failwithf "empty mint secret must be SecretMissing, got %A" other
            }
            test "too-short secret: mint fails closed" {
                let short = Encoding.UTF8.GetBytes "too-short-key" // 13 bytes

                match Token.mint Tier.Personal 3600 DateTimeOffset.UtcNow short with
                | Error MintError.SecretMissing -> ()
                | other -> failwithf "short mint secret must be SecretMissing, got %A" other
            }
            test "too-short secret: validate fails closed" {
                let short = Encoding.UTF8.GetBytes "too-short-key"

                match Token.validate DateTimeOffset.UtcNow "a.b.c" short with
                | Error ValidateError.SecretMissing -> ()
                | other -> failwithf "short validate secret must be SecretMissing, got %A" other
            }
            test "32-byte secret round-trips mint → validate (GP 11)" {
                let secret = Encoding.UTF8.GetBytes "tier-cookie-secret-32-bytes-min!" // 32 bytes
                let now = DateTimeOffset.UtcNow

                match Token.mint Tier.Personal 3600 now secret with
                | Ok token ->
                    match Token.validate now token secret with
                    | Ok tier -> Expect.equal tier Tier.Personal "tier round-trip unchanged"
                    | Error e -> failwithf "well-formed validate must succeed, got %A" e
                | Error e -> failwithf "well-formed mint must succeed, got %A" e
            }
        ]

        // ─── StripeConfigValidator — loud startup refusal ────────────
        testList "StripeConfigValidator" [
            test "empty WebhookSecret refuses startup" {
                match
                    validateStripe {
                        WebhookSecret = ""
                        ApiKey = "sk_test_x"
                    }
                with
                | CV.ValidationResult.Error msg ->
                    Expect.stringContains msg "WebhookSecret" "names the offending secret"
                | other -> failwithf "empty WebhookSecret must Error, got %A" other
            }
            test "non-whsec-shaped WebhookSecret refuses startup" {
                // 32+ bytes but not a Stripe secret shape.
                match
                    validateStripe {
                        WebhookSecret = "sk_live_not_a_webhook_secret_value"
                        ApiKey = "sk"
                    }
                with
                | CV.ValidationResult.Error _ -> ()
                | other -> failwithf "malformed WebhookSecret must Error, got %A" other
            }
            test "well-formed whsec secret passes (GP 11)" {
                match
                    validateStripe {
                        WebhookSecret = strongSecret
                        ApiKey = "sk_test_x"
                    }
                with
                | CV.ValidationResult.Ok -> ()
                | other -> failwithf "well-formed WebhookSecret must pass, got %A" other
            }
            test "validator is security-class (survives SkipPreflight)" {
                let v = StripeConfigValidator({ WebhookSecret = ""; ApiKey = "" })
                Expect.isTrue (box v :? CV.ISecurityClassValidator) "must implement ISecurityClassValidator"
            }
        ]

        // ─── JwtPeerAuthProvider — per-call key read fails closed ────
        testList "JwtPeerAuthProvider" [
            test "empty peer signing key: IssuePeerToken fails closed" {
                let store = InMemorySecretStore()
                seedKey store peerA.PeerId ""
                let provider = JwtPeerAuthProvider(store) :> IPeerAuthProvider

                match
                    provider.IssuePeerToken(peerA, peerB, UserContext.Anonymous)
                    |> Async.RunSynchronously
                with
                | Error(PeerUnauthorized _) -> ()
                | other -> failwithf "empty key must fail closed with PeerUnauthorized, got %A" other
            }
            test "too-short peer signing key: ValidatePeerToken fails closed" {
                // Mint a genuine token under a strong key, then swap the stored
                // key for a too-short one: validation must refuse the read.
                let strongStore = InMemorySecretStore()
                seedKey strongStore peerA.PeerId "peer-a-signing-key-0123456789abcdef" // 35 bytes
                let issuer = JwtPeerAuthProvider(strongStore) :> IPeerAuthProvider

                let token =
                    match
                        issuer.IssuePeerToken(peerA, peerB, UserContext.Anonymous)
                        |> Async.RunSynchronously
                    with
                    | Ok t -> t
                    | Error e -> failwithf "setup: strong-key issue must succeed, got %A" e

                let weakStore = InMemorySecretStore()
                seedKey weakStore peerA.PeerId "short" // 5 bytes
                let validator = JwtPeerAuthProvider(weakStore) :> IPeerAuthProvider

                match validator.ValidatePeerToken token |> Async.RunSynchronously with
                | Error(PeerUnauthorized _) -> ()
                | other -> failwithf "short key must fail closed with PeerUnauthorized, got %A" other
            }
            test "well-formed peer key issues + validates (GP 11)" {
                let store = InMemorySecretStore()
                seedKey store peerA.PeerId "peer-a-signing-key-0123456789abcdef" // 35 bytes
                let provider = JwtPeerAuthProvider(store) :> IPeerAuthProvider

                let token =
                    match
                        provider.IssuePeerToken(peerA, peerB, UserContext.Anonymous)
                        |> Async.RunSynchronously
                    with
                    | Ok t -> t
                    | Error e -> failwithf "well-formed issue must succeed, got %A" e

                match provider.ValidatePeerToken token |> Async.RunSynchronously with
                | Ok principal -> Expect.equal principal.Caller.PeerId peerA.PeerId "caller round-trip unchanged"
                | Error e -> failwithf "well-formed validate must succeed, got %A" e
            }
        ]
    ]