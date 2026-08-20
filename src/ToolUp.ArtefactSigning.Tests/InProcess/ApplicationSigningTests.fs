// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.ArtefactSigning.Tests.InProcess.ApplicationSigningTests

open System.Text
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning
open ToolUp.ArtefactSigning.Tests.Support.InMemoryStores

// ─── The application signing seam ──────────────────────────────────────
//
// The conformance pack certifies what a PROVIDER must do. This pack
// covers the seam around it: that an application composition can inject
// a signer and use it, that composing one leaves the existing
// byte-level signing path untouched, and that the framing and ledger the
// seam rests on behave as their contracts claim.

let private utf8 (s: string) = Encoding.UTF8.GetBytes s

let private newProvider () =
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = InMemoryAuditLog()

    let provider =
        ApplicationSigning.inProcess secrets audit "app-key-v1" EcdsaP256 "system"

    secrets, audit, provider

[<Tests>]
let tests =
    testList "Application signing — seam" [

        // The phase's headline acceptance: an application composes a
        // signer and signs and verifies its own payload through it.
        testCaseAsync "an application composition can inject a signer and round-trip a payload"
        <| async {
            let _, _, provider = newProvider ()
            let services = ServiceCollection()
            ApplicationSigning.registerProvider services provider |> ignore
            use sp = services.BuildServiceProvider()

            let signer = sp.GetRequiredService<IApplicationSigner>()
            let payload = utf8 """{"invoice":"INV-1001","total":42.5}"""

            match! signer.SignPayload("invoice.issued", payload) with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok envelope ->
                match! signer.VerifyPayload("invoice.issued", payload, envelope) with
                | Ok() -> ()
                | Error e -> failtestf "Round-trip must verify; got %s" (PayloadVerificationError.describe e)
        }

        testCaseAsync "the composed provider also exposes its byte-level pieces"
        <| async {
            let _, _, provider = newProvider ()
            let services = ServiceCollection()
            ApplicationSigning.registerProvider services provider |> ignore
            use sp = services.BuildServiceProvider()

            // The publish path holds `IArtefactSigner`; application code
            // holds `IApplicationSigner`. Both resolve from one compose.
            let byteSigner = sp.GetRequiredService<IArtefactSigner>()
            let verifier = sp.GetRequiredService<IArtefactVerifier>()
            sp.GetRequiredService<ISigningKeyLedger>() |> ignore

            let artefact = utf8 "an ordinary published artefact"

            match! byteSigner.Sign artefact with
            | Error e -> failtestf "byte-level sign must still work; got %s" (SigningError.describe e)
            | Ok signature ->
                match! verifier.Verify(artefact, signature) with
                | Ok() -> ()
                | Error e -> failtestf "byte-level verify must still work; got %s" (VerificationError.describe e)
        }

        // GP 11 in the composition: registering an application signer
        // must not displace a signer the deployment already composed for
        // its publish pipeline.
        testCase "registration never displaces an already-composed signer"
        <| fun _ ->
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = InMemoryAuditLog()

            let existing =
                DefaultArtefactSigner.createSystem secrets audit "publish-key" Ed25519

            let services = ServiceCollection()
            services.AddSingleton<IArtefactSigner>(existing) |> ignore

            let provider =
                ApplicationSigning.inProcess secrets audit "app-key-v1" EcdsaP256 "system"

            ApplicationSigning.registerProvider services provider |> ignore
            use sp = services.BuildServiceProvider()

            Expect.equal
                (sp.GetRequiredService<IArtefactSigner>().KeyId())
                "publish-key"
                "the pre-existing publish signer must survive an application-signer registration"

        // The framing is what makes purpose and level claims rather than
        // labels. Length prefixes, not delimiters, so no two distinct
        // (purpose, payload) pairs can frame to the same bytes.
        testCase "the canonical framing separates purpose from payload unambiguously"
        <| fun _ ->
            // Without length prefixes these two collide: the first
            // purpose absorbs the separator the second one splits on.
            let a = ApplicationPayload.canonicalBytes "a|b" Attribution (utf8 "c")
            let b = ApplicationPayload.canonicalBytes "a" Attribution (utf8 "b|c")
            Expect.notEqual a b "distinct purpose/payload pairs must never frame to identical bytes"

            let c = ApplicationPayload.canonicalBytes "p" Attribution (utf8 "x")
            let d = ApplicationPayload.canonicalBytes "p" IsolatedSigner (utf8 "x")
            Expect.notEqual c d "the attestation level must change the framed bytes"

            let framed = ApplicationPayload.canonicalBytes "p" Attribution (utf8 "x")
            let text = Encoding.UTF8.GetString framed
            Expect.stringStarts text ApplicationPayload.FramingVersion "the framing must be version-tagged"

        testCase "attestation levels round-trip through their stable names"
        <| fun _ ->
            for level in [ Attribution; IsolatedSigner; Reserved "hardware-quoted" ] do
                Expect.equal
                    (level |> AttestationLevel.name |> AttestationLevel.parse)
                    level
                    $"%A{level} must round-trip through its name"

            // A level from a newer producer must not make an envelope
            // unreadable — it reads as reserved, which the type's doc
            // comment defines as unverified rather than trusted.
            match AttestationLevel.parse "some-future-level" with
            | Reserved label -> Expect.equal label "some-future-level" "an unknown level is retained verbatim"
            | other -> failtestf "an unknown level must parse as Reserved; got %A" other

        testCaseAsync "the ledger persists lifecycle events across instances"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let writer = SecretStoreSigningKeyLedger.create secrets

            do! ApplicationSigning.activate writer "system" "k1" |> Async.Ignore
            do! ApplicationSigning.retire writer "operator-a" "k1" |> Async.Ignore

            do!
                ApplicationSigning.revoke writer "operator-b" "k2" "suspected disclosure"
                |> Async.Ignore

            // A separate instance over the same store — the ledger is
            // persisted data, not in-memory state.
            let reader = SecretStoreSigningKeyLedger.create secrets
            let! history = reader.History()

            let k1 =
                history
                |> SigningKeyHistory.tryFind "k1"
                |> Option.defaultWith (fun () -> failtest "k1 must be recorded")

            let k2 =
                history
                |> SigningKeyHistory.tryFind "k2"
                |> Option.defaultWith (fun () -> failtest "k2 must be recorded")

            Expect.equal k1.State RetiredKey "k1 folds to retired"
            Expect.equal (k1.Events |> List.length) 2 "k1 keeps both of its events — the ledger is append-only"

            match k2.State with
            | RevokedKey(_, reason) -> Expect.equal reason "suspected disclosure" "k2 retains its revocation reason"
            | other -> failtestf "k2 must fold to revoked; got %A" other

            Expect.equal
                (k2.Events |> List.map _.Actor)
                [ "operator-b" ]
                "every event carries the actor that recorded it"
        }

        // Revocation is terminal: recording an activation afterwards must
        // not resurrect the key, whatever order the events arrive in.
        testCase "revocation is terminal regardless of event order"
        <| fun _ ->
            let at = System.DateTimeOffset.UtcNow

            let events = [
                {
                    KeyId = "k"
                    Kind = SigningKeyEventKind.Revoked
                    At = at
                    Actor = "operator"
                    Reason = Some "compromised"
                }
                {
                    KeyId = "k"
                    Kind = SigningKeyEventKind.Activated
                    At = at.AddMinutes 5.0
                    Actor = "system"
                    Reason = None
                }
            ]

            match SigningKeyHistory.ofEvents events |> SigningKeyHistory.tryFind "k" with
            | Some entry ->
                match entry.State with
                | RevokedKey(_, reason) ->
                    Expect.equal reason "compromised" "a later activation must not un-revoke a key"
                | other -> failtestf "expected the key to stay revoked; got %A" other
            | None -> failtest "the key must be recorded"

        testCaseAsync "activation recording is idempotent across restarts"
        <| async {
            let _, _, provider = newProvider ()
            let! _ = ApplicationSigning.createActivated "system" provider
            let! signer = ApplicationSigning.createActivated "system" provider
            let! history = signer.KeyHistory()

            match history |> SigningKeyHistory.tryFind (signer.ActiveKeyId()) with
            | Some entry ->
                Expect.equal
                    (entry.Events
                     |> List.filter (fun e -> e.Kind = SigningKeyEventKind.Activated)
                     |> List.length)
                    1
                    "composing twice must not accumulate duplicate activations"
            | None -> failtest "the active key's activation must be recorded"
        }

        // A deployment that composes no ledger keeps the behaviour it had
        // before this surface existed: signatures are judged on bytes.
        testCaseAsync "a provider with no ledger verifies on the signature alone"
        <| async {
            let secrets = InMemorySecretStore() :> ISecretStore
            let audit = InMemoryAuditLog()

            let provider =
                ApplicationSigning.inProcess secrets audit "app-key-v1" EcdsaP256 "system"
                |> ApplicationSigning.withLedger (EmptySigningKeyLedger.create ())

            let signer = ApplicationSigning.create provider
            let payload = utf8 "unledgered"

            match! signer.SignPayload("some.purpose", payload) with
            | Error e -> failtestf "Sign must succeed; got %s" (SigningError.describe e)
            | Ok envelope ->
                match! signer.VerifyPayload("some.purpose", payload, envelope) with
                | Ok() -> ()
                | Error e ->
                    failtestf
                        "an unledgered signature must verify on its bytes; got %s"
                        (PayloadVerificationError.describe e)

                let! history = signer.KeyHistory()
                Expect.isEmpty history.Entries "an empty ledger records nothing"
        }
    ]