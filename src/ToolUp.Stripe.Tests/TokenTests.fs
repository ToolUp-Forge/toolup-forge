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
        test "Tier.rank is strictly increasing across all six tiers" {
            Expect.isLessThan (Tier.rank Tier.Anonymous) (Tier.rank Tier.Free) "Anonymous < Free"
            Expect.isLessThan (Tier.rank Tier.Free) (Tier.rank Tier.Personal) "Free < Personal"
            Expect.isLessThan (Tier.rank Tier.Personal) (Tier.rank Tier.Teacher) "Personal < Teacher"
            Expect.isLessThan (Tier.rank Tier.Teacher) (Tier.rank Tier.Pro) "Teacher < Pro"
            Expect.isLessThan (Tier.rank Tier.Pro) (Tier.rank Tier.Enterprise) "Pro < Enterprise"
        }
        test "existing four ranks are numerically unchanged (stored-claim stability)" {
            // These exact integers are baked into already-stored claims;
            // changing them would silently reinterpret old tokens.
            Expect.equal (Tier.rank Tier.Anonymous) 0 "Anonymous = 0"
            Expect.equal (Tier.rank Tier.Free) 1 "Free = 1"
            Expect.equal (Tier.rank Tier.Personal) 2 "Personal = 2"
            Expect.equal (Tier.rank Tier.Teacher) 3 "Teacher = 3"
            Expect.equal (Tier.rank Tier.Pro) 4 "Pro = 4"
            Expect.equal (Tier.rank Tier.Enterprise) 5 "Enterprise = 5"
        }
        test "TierGate.tierAtLeast respects the dominance ordering across new tiers" {
            Expect.isTrue (TierGate.tierAtLeast Tier.Personal Tier.Teacher) "Teacher ≥ Personal"
            Expect.isFalse (TierGate.tierAtLeast Tier.Personal Tier.Free) "Free < Personal"
            Expect.isTrue (TierGate.tierAtLeast Tier.Pro Tier.Enterprise) "Enterprise ≥ Pro"
            Expect.isTrue (TierGate.tierAtLeast Tier.Teacher Tier.Pro) "Pro ≥ Teacher"
            Expect.isFalse (TierGate.tierAtLeast Tier.Enterprise Tier.Pro) "Pro < Enterprise"
            Expect.isFalse (TierGate.tierAtLeast Tier.Pro Tier.Teacher) "Teacher < Pro"
        }
        test "Pro and Enterprise round-trip through mint / validate" {
            let now = DateTimeOffset.UtcNow

            for tier in [ Tier.Pro; Tier.Enterprise ] do
                let token =
                    match Token.mint tier 3600 now secret with
                    | Ok s -> s
                    | Error e -> failwithf "mint %A failed: %A" tier e

                match Token.validate now token secret with
                | Ok parsed -> Expect.equal parsed tier (sprintf "%A round-trip" tier)
                | Error e -> failwithf "validate %A failed: %A" tier e
        }
        test "a token minted at the old four-tier surface still validates unchanged" {
            // Construct a Teacher token exactly as the pre-Phase-144
            // surface would have: claim "teacher", same HMAC payload
            // shape. It must still validate to Tier.Teacher.
            let exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600L
            let payload = sprintf "teacher.%d" exp

            use h = new System.Security.Cryptography.HMACSHA256(secret)
            let bytes = h.ComputeHash(Encoding.UTF8.GetBytes payload)

            let sigB64 =
                Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')

            let legacyToken = sprintf "%s.%s" payload sigB64

            match Token.validate DateTimeOffset.UtcNow legacyToken secret with
            | Ok tier -> Expect.equal tier Tier.Teacher "legacy four-tier token validates unchanged"
            | Error e -> failwithf "legacy token failed to validate: %A" e
        }
        test "Tier.toClaim / tryParse round-trip for the new tiers" {
            Expect.equal (Tier.toClaim Tier.Pro) "pro" "Pro claim"
            Expect.equal (Tier.toClaim Tier.Enterprise) "enterprise" "Enterprise claim"
            Expect.equal (Tier.tryParse "pro") Tier.Pro "parse pro"
            Expect.equal (Tier.tryParse "ENTERPRISE") Tier.Enterprise "parse enterprise case-insensitive"
        }
    ]