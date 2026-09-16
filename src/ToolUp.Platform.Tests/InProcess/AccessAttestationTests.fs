// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.AccessAttestationTests

// ─── Phase 557 — module-access attestation certificate ───────────────
//
// The certificate is the document a deployment hands a counterparty in
// answer to "prove no-one can be granted access to our data without our
// consent". Four properties carry it, matching 557.A–D:
//
//   1. **The payload is canonical.** Same state, same bytes — twice over
//      the same composition, and round-tripping through the reader. The
//      exact JSON is pinned as a literal so a serialiser change cannot
//      quietly re-canonicalise every certificate in the estate.
//   2. **Emit then verify round-trips, and every tamper is refused.** A
//      payload edit, a transplanted signature, a foreign key, a wrong
//      module, and a document whose predicate disagrees with its own
//      subject are five separate named refusals — none is a pass.
//   3. **Emission is deterministic given fixed state.** With Ed25519 the
//      whole envelope is byte-identical between two emissions, not just
//      the payload.
//   4. **A module with no policy yields "nothing to attest"**, and the
//      signer is never asked — an empty-but-signed claim is the one
//      output the phase forbids.
//
// **Non-vacuity.** The live approvals are resolved through the real
// `GrantConsentStore.resolveLive` over a real `InMemoryGrantConsentStore`
// with real HMAC-signed consent records, so the "alice is live, bob is
// revoked, carol is expired, dave never consented" fixture exercises the
// same judgement dispatch makes. The signature checks run the real
// `DsseEnvelopeSigning.verifySignature` over a public key resolved from
// the same secret store the signer provisioned into — the offline path a
// holder actually runs.

open System
open System.Collections.Concurrent
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.Platform.PermissionStore
open ToolUp.Platform.GrantConsentStore
open ToolUp.Platform.AccessAttestation
open ToolUp.ArtefactSigning

// ─── Fixtures: the deployment key ────────────────────────────────────

/// Minimal in-memory `ISecretStore` — the envelope signer provisions the
/// deployment key into it on first use, and the public key is read back
/// out of the same store through the Phase 40 signer.
type private InMemorySecretStore() =
    let store = ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private keyId = "deploy-v1"

/// The deployment's envelope signer plus the public key a holder would
/// fetch from `/_platform/signing-key/{keyId}`. Ed25519 so a signature is
/// deterministic and the whole-envelope determinism claim is testable.
let private deploymentKey () =
    let secrets = InMemorySecretStore() :> ISecretStore
    let audit = AuditLog.NoOpAuditLog() :> IAuditLog
    let byteSigner = DefaultArtefactSigner.createSystem secrets audit keyId Ed25519
    let publicKey = byteSigner.VerifyKey() |> Async.RunSynchronously
    let envelopeSigner = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519
    envelopeSigner, publicKey

/// A signer that fails the test if it is ever asked to sign — the
/// "nothing to attest" cases assert nothing reaches the key.
let private forbiddenSigner =
    { new IStatementEnvelopeSigner with
        member _.KeyId() = keyId

        member _.SignPreAuthenticated _ = async {
            return failtest "the signer must not be asked to sign when there is nothing to attest"
        }
    }

// ─── Fixtures: the composition state ─────────────────────────────────

let private party = PartyRef.create "acme-dpo"
let private counterparty = GrantPolicy.RequiresCounterpartyApproval party
let private moduleName = "SkuAnalysis"
let private otherModule = "Forecasting"
let private teamId = "team-a"
let private teamB = "team-b"

let private attestedAt = DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero)

let private macKeyBytes = Encoding.UTF8.GetBytes "a-shared-consent-signing-key-32b"
let private partyKeyId = "party-key-1"

let private keyring = [
    {
        Party = party
        KeyId = partyKeyId
        Material = ConsentHmacSha256(Convert.ToBase64String macKeyBytes)
    }
]

let private consentVerifier = verifierOver keyring

let private signed (draft: GrantConsentRecord) = {
    draft with
        Signature = ConsentSigning.signHmacSha256 macKeyBytes partyKeyId draft
}

let private record id (team: string) (subjectId: string) status supersedes expiresAt issuedAt =
    signed {
        ConsentId = id
        Subject = ConsentSubject.create team subjectId moduleName
        Party = party
        Status = status
        IssuedAtUtc = issuedAt
        ExpiresAtUtc = expiresAt
        Signature = {
            KeyId = partyKeyId
            DeclaredAlgorithm = "HmacSha256"
            Value = ""
            SignedAtUtc = issuedAt
        }
        Supersedes = supersedes
        RecordedBy = "admin-a"
    }

let private liveGrant = {
    State = GrantState.Active
    SatisfiedPolicy = counterparty
    Justification = "counterparty consent"
    ConsentedBy = None
}

/// A permission store over a fixed set of documents. The attestation
/// reads documents only; every write is refused so a test cannot
/// accidentally depend on one.
type private FixedPermissionStore(documents: Map<string, TeamPermissions>) =
    interface IPermissionStore with
        member _.GetTeamPermissions(teamId) = async {
            return documents |> Map.tryFind teamId |> Option.defaultValue TeamPermissions.empty
        }

        member _.SetTeamPermissions(_, _) = async { return Error "read-only fixture" }
        member _.GetEffectivePermissions(_, _) = async { return Map.empty }
        member _.SetMemberPermissions(_, _, _, _) = async { return Error "read-only fixture" }
        member _.SetTeamDefaults(_, _) = async { return Error "read-only fixture" }
        member _.GetModuleExposure(_) = async { return Map.empty }
        member _.SetModuleExposure(_, _, _) = async { return Error "read-only fixture" }

/// A permission store whose documents cannot be read.
type private BrokenPermissionStore() =
    interface IPermissionStore with
        member _.GetTeamPermissions(_) = async { return failwith "permission blob unreachable" }
        member _.SetTeamPermissions(_, _) = async { return Error "broken" }
        member _.GetEffectivePermissions(_, _) = async { return Map.empty }
        member _.SetMemberPermissions(_, _, _, _) = async { return Error "broken" }
        member _.SetTeamDefaults(_, _) = async { return Error "broken" }
        member _.GetModuleExposure(_) = async { return Map.empty }
        member _.SetModuleExposure(_, _, _) = async { return Error "broken" }

/// A consent registry that cannot be read.
type private BrokenConsentStore() =
    interface IGrantConsentStore with
        member _.Put _ = async { return Error "registry unreachable" }
        member _.TryGet(_, _) = async { return Error "registry unreachable" }
        member _.ListForSubject _ = async { return Error "registry unreachable" }

let private grantsOn (subjects: string list) : Map<string, Map<string, ModuleGrantRecord>> =
    subjects
    |> List.map (fun s -> s, Map.ofList [ moduleName, liveGrant ])
    |> Map.ofList

/// Team A: alice is live; bob's approval was revoked; carol's approval
/// expired an hour before the attestation; dave has a grant row and no
/// consent record at all; erin's grant is on a different module. Team B:
/// frank is live.
let private populatedSources (manifest: GrantAuthoritySurface option) =
    let consents = InMemoryGrantConsentStore() :> IGrantConsentStore
    let issued = attestedAt.AddDays(-30.0)

    let put r =
        consents.Put r |> Async.RunSynchronously |> ignore

    put (record "c-alice" teamId "alice" ConsentStatus.Approved None None issued)
    put (record "c-bob-1" teamId "bob" ConsentStatus.Approved None None issued)
    put (record "c-bob-2" teamId "bob" ConsentStatus.Revoked (Some "c-bob-1") None (issued.AddDays 1.0))

    put (record "c-carol" teamId "carol" ConsentStatus.Approved None (Some(attestedAt.AddHours(-1.0))) issued)

    put (record "c-frank" teamB "frank" ConsentStatus.Approved None (Some(attestedAt.AddDays 90.0)) issued)

    let teamADoc = {
        TeamPermissions.empty with
            Grants =
                grantsOn [ "alice"; "bob"; "carol"; "dave" ]
                |> Map.add "erin" (Map.ofList [ otherModule, liveGrant ])
    }

    let teamBDoc = {
        TeamPermissions.empty with
            Grants = grantsOn [ "frank" ]
    }

    {
        Registry = GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ moduleName, counterparty ]
        Permissions = FixedPermissionStore(Map.ofList [ teamId, teamADoc; teamB, teamBDoc ]) :> IPermissionStore
        Consents = Some consents
        ConsentVerifier = consentVerifier
        Manifest = manifest
    }

let private request = {
    ModuleName = moduleName
    TeamIds = [ teamB; teamId; teamId ]
    DeploymentSha = "3f2a9c1"
    AttestedAt = attestedAt
}

let private expectAttested (outcome: AttestationOutcome) =
    match outcome with
    | AttestationOutcome.Attested(envelope, payload) -> envelope, payload
    | other -> failtestf "expected an attested certificate, got %A" other

/// The exact predicate JSON the populated fixture must produce. A change
/// here is a change to every certificate's bytes and needs a compatibility
/// note, which is why it is a literal and not a derivation.
let private expectedCanonical =
    """{"format":"toolup.module-access-attestation/v1","module":"SkuAnalysis","declaredPolicy":"requires-counterparty-approval:acme-dpo","teamsExamined":["team-a","team-b"],"liveApprovals":[{"consentId":"c-alice","teamId":"team-a","subjectId":"alice","party":"acme-dpo","issuedAtUtc":"2026-08-17T12:00:00.0000000Z"},{"consentId":"c-frank","teamId":"team-b","subjectId":"frank","party":"acme-dpo","issuedAtUtc":"2026-08-17T12:00:00.0000000Z","expiresAtUtc":"2026-12-15T12:00:00.0000000Z"}],"deploymentSha":"3f2a9c1","attestedAtUtc":"2026-09-16T12:00:00.0000000Z"}"""

/// Re-encode an envelope with an edited statement, keeping the signature.
let private withPayloadEdited (edit: string -> string) (envelope: DsseEnvelope) =
    let statement = Convert.FromBase64String envelope.Payload |> Encoding.UTF8.GetString

    {
        envelope with
            Payload = edit statement |> Encoding.UTF8.GetBytes |> Convert.ToBase64String
    }

let private freshExpectation (moduleName: string option) = {
    ModuleName = moduleName
    Now = attestedAt.AddMinutes 10.0
    MaxAge = TimeSpan.FromHours 24.0
    ClockSkew = AttestationExpectation.DefaultClockSkew
}

// ─── Tests ───────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 557 — module-access attestation certificate" [

        // ── 1. The payload is canonical ──────────────────────────────
        testList "payload (557.A)" [
            test "the populated fixture composes to the pinned canonical bytes" {
                let sources = populatedSources None

                match compose sources request |> Async.RunSynchronously with
                | Ok(Some payload) ->
                    Expect.equal (canonicalJson payload) expectedCanonical "canonical predicate JSON"

                    Expect.equal
                        (payload.LiveApprovals |> List.map _.SubjectId)
                        [ "alice"; "frank" ]
                        "only the live, addressed, verified approvals — revoked, expired, unconsented and other-module grants are absent"

                    Expect.equal payload.TeamsExamined [ "team-a"; "team-b" ] "teams deduplicated and ordinal-sorted"
                    Expect.isNone payload.GrantAuthorityManifestSha256 "no surface supplied ⇒ no manifest member"
                | other -> failtestf "expected a payload, got %A" other
            }

            test "the canonical JSON round-trips through the reader byte for byte" {
                let sources = populatedSources None

                match compose sources request |> Async.RunSynchronously with
                | Ok(Some payload) ->
                    match readPayload (canonicalJson payload) with
                    | Ok read ->
                        Expect.equal read payload "payload record round-trips"
                        Expect.equal (canonicalJson read) (canonicalJson payload) "bytes round-trip"
                    | Error e -> failtestf "reader refused its own canonical form: %s" e
                | other -> failtestf "expected a payload, got %A" other
            }

            test "the manifest hash is present when a surface is supplied, and is the hash of its rendering" {
                let sources = populatedSources (Some GrantAuthoritySurface.empty)

                match compose sources request |> Async.RunSynchronously with
                | Ok(Some payload) ->
                    let expected =
                        GrantAuthoritySurface.render GrantAuthoritySurface.empty
                        |> Encoding.UTF8.GetBytes
                        |> DsseEnvelope.sha256Hex

                    Expect.equal payload.GrantAuthorityManifestSha256 (Some expected) "manifest hash"

                    Expect.stringContains
                        (canonicalJson payload)
                        "\"grantAuthorityManifestSha256\":\""
                        "member present in the canonical form"
                | other -> failtestf "expected a payload, got %A" other
            }

            test "the reader refuses a foreign format and a missing member rather than defaulting" {
                match readPayload """{"format":"something-else/v1"}""" with
                | Error e -> Expect.stringContains e "unsupported attestation format" "format refusal"
                | Ok p -> failtestf "read a foreign format as %A" p

                let missingSha = expectedCanonical.Replace(",\"deploymentSha\":\"3f2a9c1\"", "")

                match readPayload missingSha with
                | Error e -> Expect.stringContains e "deploymentSha" "names the missing member"
                | Ok p -> failtestf "defaulted a missing member: %A" p
            }
        ]

        // ── 2. Emit → verify, and every tamper refused ───────────────
        testList "emit and verify (557.B / 557.C)" [
            test "a certificate verifies offline against the public key alone and reads back its payload" {
                let signer, publicKey = deploymentKey ()
                let sources = populatedSources None

                let envelope, payload =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                let check = DsseEnvelopeSigning.verifySignature publicKey

                match verify check (freshExpectation (Some moduleName)) envelope with
                | Ok read -> Expect.equal read payload "the verified payload is the emitted one"
                | Error r -> failtestf "refused a genuine certificate: %s" (AttestationRefusal.describe r)

                // The wire form a holder actually receives.
                match verifyJson check (freshExpectation (Some moduleName)) (DsseEnvelope.toJson envelope) with
                | Ok read -> Expect.equal read payload "JSON form verifies"
                | Error r -> failtestf "refused the JSON form: %s" (AttestationRefusal.describe r)
            }

            test "editing an approval out of the payload is refused as a signature failure" {
                let signer, publicKey = deploymentKey ()

                let envelope, _ =
                    emit signer (populatedSources None) request
                    |> Async.RunSynchronously
                    |> expectAttested

                let tampered =
                    envelope |> withPayloadEdited (fun s -> s.Replace("\"alice\"", "\"mallory\""))

                match
                    verify (DsseEnvelopeSigning.verifySignature publicKey) (freshExpectation (Some moduleName)) tampered
                with
                | Error(AttestationRefusal.Envelope EnvelopeSignatureInvalid) -> ()
                | other -> failtestf "expected EnvelopeSignatureInvalid, got %A" other
            }

            test "a signature transplanted from a certificate about another module is refused" {
                let signer, publicKey = deploymentKey ()
                let sources = populatedSources None

                let envelope, _ =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                let otherSources = {
                    sources with
                        Registry =
                            GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [ otherModule, counterparty ]
                }

                let otherEnvelope, _ =
                    emit signer otherSources {
                        request with
                            ModuleName = otherModule
                    }
                    |> Async.RunSynchronously
                    |> expectAttested

                let transplanted = {
                    envelope with
                        Signatures = otherEnvelope.Signatures
                }

                match
                    verify
                        (DsseEnvelopeSigning.verifySignature publicKey)
                        (freshExpectation (Some moduleName))
                        transplanted
                with
                | Error(AttestationRefusal.Envelope EnvelopeSignatureInvalid) -> ()
                | other -> failtestf "expected EnvelopeSignatureInvalid, got %A" other
            }

            test "a certificate signed under a different deployment key is refused as unsigned for the holder's key" {
                let signer, _ = deploymentKey ()
                let _, foreignKey = deploymentKey ()

                let envelope, _ =
                    emit signer (populatedSources None) request
                    |> Async.RunSynchronously
                    |> expectAttested

                // Same key id, different key material: the holder's copy of
                // "deploy-v1" is not the one that signed. That is a signature
                // failure, not an unsigned envelope — the id matches.
                match
                    verify
                        (DsseEnvelopeSigning.verifySignature foreignKey)
                        (freshExpectation (Some moduleName))
                        envelope
                with
                | Error(AttestationRefusal.Envelope EnvelopeSignatureInvalid) -> ()
                | other -> failtestf "expected EnvelopeSignatureInvalid, got %A" other

                let renamed = { foreignKey with KeyId = "deploy-v2" }

                match
                    verify (DsseEnvelopeSigning.verifySignature renamed) (freshExpectation (Some moduleName)) envelope
                with
                | Error(AttestationRefusal.Envelope(EnvelopeUnsignedForKey "deploy-v2")) -> ()
                | other -> failtestf "expected EnvelopeUnsignedForKey, got %A" other
            }

            test "a genuine certificate about a different module is refused as a subject mismatch, after the signature" {
                let signer, publicKey = deploymentKey ()

                let envelope, _ =
                    emit signer (populatedSources None) request
                    |> Async.RunSynchronously
                    |> expectAttested

                match
                    verify
                        (DsseEnvelopeSigning.verifySignature publicKey)
                        (freshExpectation (Some otherModule))
                        envelope
                with
                | Error(AttestationRefusal.Envelope(EnvelopeSubjectMismatch(expected, _))) ->
                    Expect.equal expected otherModule "names the module the holder brought"
                | other -> failtestf "expected EnvelopeSubjectMismatch, got %A" other
            }

            test "a signed statement whose predicate names a different module than its subject is unreadable" {
                let signer, publicKey = deploymentKey ()
                let sources = populatedSources None

                let payload =
                    match compose sources request |> Async.RunSynchronously with
                    | Ok(Some p) -> p
                    | other -> failtestf "expected a payload, got %A" other

                // Assemble the statement by hand with the WRONG subject, and
                // sign it genuinely — a document that says two things.
                let envelope =
                    DsseEnvelope.sign signer [ subjectFor otherModule ] PredicateType (canonicalJson payload)
                    |> Async.RunSynchronously
                    |> function
                        | Ok e -> e
                        | Error e -> failtestf "could not sign: %s" e

                match verify (DsseEnvelopeSigning.verifySignature publicKey) (freshExpectation None) envelope with
                | Error(AttestationRefusal.Unreadable reason) ->
                    Expect.stringContains reason "subject" "names the disagreement"
                | other -> failtestf "expected Unreadable, got %A" other
            }

            test "a stale certificate and one from the future are refused on freshness, after the signature" {
                let signer, publicKey = deploymentKey ()

                let envelope, _ =
                    emit signer (populatedSources None) request
                    |> Async.RunSynchronously
                    |> expectAttested

                let check = DsseEnvelopeSigning.verifySignature publicKey

                let stale = {
                    freshExpectation (Some moduleName) with
                        Now = attestedAt.AddHours 25.0
                }

                match verify check stale envelope with
                | Error(AttestationRefusal.Stale(at, _, _)) -> Expect.equal at attestedAt "names the attested instant"
                | other -> failtestf "expected Stale, got %A" other

                let future = {
                    freshExpectation (Some moduleName) with
                        Now = attestedAt.AddMinutes(-10.0)
                }

                match verify check future envelope with
                | Error(AttestationRefusal.NotYetValid _) -> ()
                | other -> failtestf "expected NotYetValid, got %A" other

                let withinSkew = {
                    freshExpectation (Some moduleName) with
                        Now = attestedAt.AddMinutes(-2.0)
                }

                Expect.isOk (verify check withinSkew envelope) "two honest clocks a couple of minutes apart"
            }

            test "the freshness judgement on its own is total at the boundaries" {
                let payload =
                    match compose (populatedSources None) request |> Async.RunSynchronously with
                    | Ok(Some p) -> p
                    | other -> failtestf "expected a payload, got %A" other

                let at = {
                    freshExpectation (Some moduleName) with
                        Now = attestedAt.AddHours 24.0
                }

                Expect.isOk (checkFreshness at payload) "exactly at the window is still fresh"

                let edge = {
                    freshExpectation (Some moduleName) with
                        Now = attestedAt - AttestationExpectation.DefaultClockSkew
                }

                Expect.isOk (checkFreshness edge payload) "exactly at the skew is still valid"
            }
        ]

        // ── 3. Determinism given fixed state ─────────────────────────
        testList "determinism (557.D)" [
            test "two emissions over the same state are byte-identical envelopes" {
                let signer, _ = deploymentKey ()
                let sources = populatedSources (Some GrantAuthoritySurface.empty)

                let first, _ =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                let second, _ =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                Expect.equal second.Payload first.Payload "statement bytes"

                Expect.equal
                    (DsseEnvelope.toJson second)
                    (DsseEnvelope.toJson first)
                    "whole envelope (Ed25519 is deterministic)"
            }

            test "the team order in the request does not change the bytes" {
                let signer, _ = deploymentKey ()
                let sources = populatedSources None

                let forward, _ =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                let reversed, _ =
                    emit signer sources {
                        request with
                            TeamIds = [ teamId; teamB ]
                    }
                    |> Async.RunSynchronously
                    |> expectAttested

                Expect.equal reversed.Payload forward.Payload "statement bytes independent of request order"
            }
        ]

        // ── 4. Nothing to attest, and the failure arms ───────────────
        testList "nothing to attest and failures (557.D)" [
            test "a module with no declared policy is 'nothing to attest' and the signer is never asked" {
                let sources = {
                    populatedSources None with
                        Registry = GrantPolicyGuard.ModuleGrantPolicyRegistry.empty
                }

                match emit forbiddenSigner sources request |> Async.RunSynchronously with
                | AttestationOutcome.NothingToAttest reason ->
                    Expect.stringContains reason moduleName "names the module"
                | other -> failtestf "expected NothingToAttest, got %A" other
            }

            test "a module registered under a different name is 'nothing to attest' for the name asked about" {
                match
                    emit forbiddenSigner (populatedSources None) {
                        request with
                            ModuleName = otherModule
                    }
                    |> Async.RunSynchronously
                with
                | AttestationOutcome.NothingToAttest _ -> ()
                | other -> failtestf "expected NothingToAttest, got %A" other
            }

            test "an unreadable consent registry fails the attestation rather than attesting nobody" {
                let signer, _ = deploymentKey ()

                let sources = {
                    populatedSources None with
                        Consents = Some(BrokenConsentStore() :> IGrantConsentStore)
                }

                match emit signer sources request |> Async.RunSynchronously with
                | AttestationOutcome.AttestationFailed reason ->
                    Expect.stringContains reason "registry" "names the source"
                | other -> failtestf "expected AttestationFailed, got %A" other
            }

            test "an unreadable permission document fails the attestation" {
                let signer, _ = deploymentKey ()

                let sources = {
                    populatedSources None with
                        Permissions = BrokenPermissionStore() :> IPermissionStore
                }

                match emit signer sources request |> Async.RunSynchronously with
                | AttestationOutcome.AttestationFailed reason ->
                    Expect.stringContains reason "permission document" "names the source"
                | other -> failtestf "expected AttestationFailed, got %A" other
            }

            test "no composed consent registry attests the policy with no live approvals" {
                let signer, publicKey = deploymentKey ()

                let sources = {
                    populatedSources None with
                        Consents = None
                }

                let envelope, payload =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                Expect.isEmpty payload.LiveApprovals "nothing can be live without a registry"

                Expect.isOk
                    (verify
                        (DsseEnvelopeSigning.verifySignature publicKey)
                        (freshExpectation (Some moduleName))
                        envelope)
                    "still a genuine certificate"
            }

            test "a policy naming no counterparty attests the policy without touching the registry" {
                let signer, _ = deploymentKey ()

                let sources = {
                    populatedSources None with
                        Registry =
                            GrantPolicyGuard.ModuleGrantPolicyRegistry.ofDeclarations [
                                moduleName, GrantPolicy.RequiresAcknowledgement
                            ]
                        // A registry that would fail if read.
                        Consents = Some(BrokenConsentStore() :> IGrantConsentStore)
                }

                let _, payload =
                    emit signer sources request |> Async.RunSynchronously |> expectAttested

                Expect.equal payload.DeclaredPolicy "requires-acknowledgement" "the declared policy is attested"
                Expect.isEmpty payload.LiveApprovals "the 552 registry holds counterparty consent only"
            }
        ]
    ]