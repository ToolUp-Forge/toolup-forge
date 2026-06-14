module ToolUp.Stripe.TierToken.Tests.TokenTests

open System
open System.Text
open Expecto
open ToolUp.Stripe.TierToken

let private secret = Encoding.UTF8.GetBytes "test-secret-32-bytes-min-padding"
let private emptySecret: byte[] = [||]

[<Tests>]
let tests =
    testList "Token" [
        test "mint then validate round-trips the tier" {
            let now = DateTimeOffset.UtcNow

            let token =
                match Token.mint Tier.Personal 3600 now secret with
                | Ok s -> s
                | Error e -> failwithf "expected mint to succeed, got %A" e

            match Token.validate now token secret with
            | Ok tier -> Expect.equal tier Tier.Personal "round-trip"
            | Error e -> failwithf "validate failed: %A" e
        }
        test "expired token is rejected" {
            // Mint at 2h in the past with a 1-second lifetime → expired.
            let past = DateTimeOffset.UtcNow.AddHours(-2.0)

            let token =
                match Token.mint Tier.Personal 1 past secret with
                | Ok s -> s
                | Error e -> failwithf "expected mint to succeed, got %A" e

            match Token.validate DateTimeOffset.UtcNow token secret with
            | Error Expired -> ()
            | other -> failwithf "expected Expired, got %A" other
        }
        test "tampered tier is rejected" {
            let now = DateTimeOffset.UtcNow

            let token =
                match Token.mint Tier.Personal 3600 now secret with
                | Ok s -> s
                | Error e -> failwithf "mint failed: %A" e

            let tampered =
                let parts = token.Split('.')
                sprintf "teacher.%s.%s" parts.[1] parts.[2]

            match Token.validate now tampered secret with
            | Error SignatureMismatch -> ()
            | other -> failwithf "expected SignatureMismatch, got %A" other
        }
        test "tampered signature is rejected" {
            let now = DateTimeOffset.UtcNow

            let token =
                match Token.mint Tier.Personal 3600 now secret with
                | Ok s -> s
                | Error e -> failwithf "mint failed: %A" e

            let parts = token.Split('.')
            let tampered = sprintf "%s.%s.AAAA-deadbeef-AAAA" parts.[0] parts.[1]

            match Token.validate now tampered secret with
            | Error SignatureMismatch -> ()
            | other -> failwithf "expected SignatureMismatch, got %A" other
        }
        test "malformed token is rejected" {
            match Token.validate DateTimeOffset.UtcNow "no-dots-here" secret with
            | Error MalformedToken -> ()
            | other -> failwithf "expected MalformedToken, got %A" other
        }
        test "missing mint secret returns SecretMissing" {
            let now = DateTimeOffset.UtcNow

            match Token.mint Tier.Personal 3600 now emptySecret with
            | Error MintError.SecretMissing -> ()
            | other -> failwithf "expected MintError.SecretMissing, got %A" other
        }
        test "invalid lifetime returns InvalidLifetime" {
            let now = DateTimeOffset.UtcNow

            match Token.mint Tier.Personal 0 now secret with
            | Error InvalidLifetime -> ()
            | other -> failwithf "expected InvalidLifetime, got %A" other
        }
        test "missing validate secret returns SecretMissing" {
            let now = DateTimeOffset.UtcNow

            let token =
                match Token.mint Tier.Personal 3600 now secret with
                | Ok s -> s
                | Error e -> failwithf "mint failed: %A" e

            match Token.validate now token emptySecret with
            | Error ValidateError.SecretMissing -> ()
            | other -> failwithf "expected ValidateError.SecretMissing, got %A" other
        }
        test "unknown tier claim is rejected" {
            // Construct a token whose signature is valid but whose
            // tier claim is something the parser doesn't recognise.
            let now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L
            let payload = sprintf "wizard.%d" now

            use h = new System.Security.Cryptography.HMACSHA256(secret)
            let bytes = h.ComputeHash(Encoding.UTF8.GetBytes payload)

            let sigB64 =
                Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

            let token = sprintf "%s.%s" payload sigB64

            match Token.validate DateTimeOffset.UtcNow token secret with
            | Error UnknownTier -> ()
            | other -> failwithf "expected UnknownTier, got %A" other
        }
        test "Tier.rank orders the v0 superset" {
            Expect.isLessThan (Tier.rank Tier.Anonymous) (Tier.rank Tier.Free) "Anonymous < Free"
            Expect.isLessThan (Tier.rank Tier.Free) (Tier.rank Tier.Personal) "Free < Personal"
            Expect.isLessThan (Tier.rank Tier.Personal) (Tier.rank Tier.Teacher) "Personal < Teacher"
        }
        test "TierGate.tierAtLeast respects the dominance ordering" {
            Expect.isTrue (TierGate.tierAtLeast Tier.Personal Tier.Teacher) "Teacher ≥ Personal"
            Expect.isFalse (TierGate.tierAtLeast Tier.Personal Tier.Free) "Free < Personal"
        }
    ]