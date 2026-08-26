module ToolUp.Platform.Tests.InProcess.EvidenceBundleExportTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Secrets
open ToolUp.ArtefactSigning

// ─── The joined evidence bundle as a signed statement ────────────────
//
// Four properties carry this substrate, and each is probed in both
// directions rather than only the direction that would pass:
//
//   * SAME chain ⇒ SAME content id — and the observer and the clock,
//     which are carried but deliberately not addressed by, must not
//     reach it.
//   * DIFFERENT chain ⇒ DIFFERENT content id, probed field by field,
//     one perturbation at a time. A canonical form that silently
//     dropped a field would pass the first property perfectly, so the
//     second is the one worth insisting on: a determinism test that
//     only ever hashes equal inputs cannot distinguish a correct
//     canonical form from `fun _ -> "constant"`.
//   * The document is READABLE without a verifier, and the claim
//     boundary is present on a CLEAN bundle, not only a broken one.
//   * The nested-attestation ruling is written into the document, and a
//     document declaring any other ruling is refused by name.
//
// **What is checked with real crypto and what is not.** The signature
// round-trip runs against the shipped signing companion over a real
// Ed25519 and a real ECDSA P-256 key. The STRUCTURAL verifier is probed
// with an injected digest as well, because it is the one that must run
// on a host with no cryptography package at all — a test that only ever
// reached it through this host's SHA-256 would not have exercised the
// property that makes it portable.
//
// **The stock-tooling check is a separate, out-of-band step**, recorded
// in this phase's migration note: an unmodified cosign binary verifying
// a fixture written by these tests. It is not run from inside this pack,
// because adding an external binary to the suite would make a green
// depend on a toolchain a fresh checkout does not have.

/// Minimal in-memory `ISecretStore` — signing keys are auto-provisioned
/// into it on first use.
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

let private keyId = "bundle-v1"
let private observer = "deployment-under-test"
let private observedAt = DateTime(2026, 8, 26, 11, 30, 0, DateTimeKind.Utc)

// ─── Fixture chains ──────────────────────────────────────────────────
//
// The seal references below are the half of the fixture the transcode
// ruling is about: each is the digest of an artefact that already
// carries somebody else's signature, and the bundle must carry it
// exactly as the walk recorded it.

let private deploySealReference = "deploy-2026-08-26-a91f"

let private packManifestDigest =
    "6b3d1f0c9a4e7b2d5f8c1a0e3b6d9f2c5a8e1b4d7f0c3a6e9b2d5f8c1a4e7b0d"

let private ledgerPosition = "4218"

let private linked (reference: string) (detail: string) =
    EvidenceHopOutcome.bare (EvidenceLink.Linked(reference, detail))

/// A chain where every hop resolves — the CLEAN case. It is the one the
/// claim-boundary test uses, because a caveat that appears only on
/// failures is a caveat nobody reads.
let private cleanLinks: EvidenceChainLinks = {
    UpstreamWorkRecord = linked "work-8823" "the deploy's sources are covered by change work record work-8823"
    BuildTranscript = linked "3f9a" "the deploy record names transcript 3f9a, which is the transcript in hand"
    DependencyClosure = {
        Link = EvidenceLink.Linked("c710", "the deploy record binds closure c710 — 214 resolved entries")
        Findings = [ "example.pkg 1.4.0 — no upstream attestation recorded" ]
    }
    DeployRecord =
        linked
            deploySealReference
            "the 'ecdsa-p256' seal minted under key deploy-v2 covers this record's canonical bytes"
    BootVerification = linked deploySealReference "the running composition is the sealed one"
    EvidencePack =
        linked packManifestDigest "a signed manifest pins 9 content-addressed segment(s) of this deployment's evidence"
    LedgerPosition = {
        Link = EvidenceLink.Linked(ledgerPosition, "this deployment's evidence chains to ledger position 4218")
        Findings = [ "chain head '0d4c'" ]
    }
}

/// A chain carrying one break and one absence — the ordinary state of a
/// partially-composed deployment that has broken something.
let private mixedLinks: EvidenceChainLinks = {
    cleanLinks with
        DependencyClosure = EvidenceHopOutcome.bare (EvidenceLink.LinkAbsent "no dependency closure is composed")
        LedgerPosition =
            EvidenceHopOutcome.bare (EvidenceLink.LinkBroken("4218", "the ledger is composed and could not be read"))
}

let private chainOf (links: EvidenceChainLinks) : EvidenceChain =
    let hops = EvidenceChain.hops links

    {
        SchemaVersion = EvidenceChain.SchemaVersion
        Actor = "assessor@example.test"
        WalkedAt = DateTime(2026, 8, 26, 11, 29, 0, DateTimeKind.Utc)
        Hops = hops
        Outcome = EvidenceChain.outcomeOf hops
        VerdictDigest = DefaultEvidenceChainWalker.VerdictDigest hops
    }

let private cleanChain = chainOf cleanLinks
let private mixedChain = chainOf mixedLinks

let private cleanBundle =
    EvidenceBundleExport.bundleOf observer observedAt cleanChain

let private mixedBundle =
    EvidenceBundleExport.bundleOf observer observedAt mixedChain

/// Re-address a hand-perturbed bundle, so a perturbation test probes the
/// canonical form rather than the arithmetic of an id nobody recomputed.
let private readdress (bundle: EvidenceBundle) : EvidenceBundle = {
    bundle with
        ContentId = EvidenceBundleExport.digest (EvidenceBundle.canonicalForm bundle)
}

// ─── A — the bundle projection is deterministic ──────────────────────

let bundleDeterminismTests =
    testList "Phase 714 — the bundle projection is content-addressed" [

        test "same chain produces the same canonical form and content id" {
            let again = EvidenceBundleExport.bundleOf observer observedAt cleanChain

            Expect.equal
                (EvidenceBundle.canonicalForm again)
                (EvidenceBundle.canonicalForm cleanBundle)
                "identical inputs must canonicalise identically"

            Expect.equal again.ContentId cleanBundle.ContentId "identical inputs must address identically"
        }

        test "the observer and the observation time do not reach the content id" {
            let elsewhere =
                EvidenceBundleExport.bundleOf "some-other-deployment" (observedAt.AddDays 40.0) cleanChain

            Expect.equal
                elsewhere.ContentId
                cleanBundle.ContentId
                "the id names the record set, so an unchanged deployment bundles to the same id however far apart the walks were taken"

            Expect.notEqual
                elsewhere.Observer
                cleanBundle.Observer
                "and the observer is still CARRIED — excluded from the id is not the same as absent from the document"
        }

        test "a different chain produces a different content id" {
            Expect.notEqual
                mixedBundle.ContentId
                cleanBundle.ContentId
                "a chain with a break and an absence is not the same record set as a clean one"
        }

        test "each field of the canonical form reaches the content id" {
            // One perturbation at a time. A canonical form that dropped
            // any of these would pass every same-input test above.
            let perturbations: (string * EvidenceBundle) list = [
                "a hop's link label",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.mapi (fun i hop ->
                                        if i = 0 then
                                            {
                                                hop with
                                                    Link = EvidenceLink.LinkAbsent "nothing joins these"
                                            }
                                        else
                                            hop)
                        }
                }
                "a hop's reference",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.map (fun hop ->
                                        if hop.Id = EvidenceChain.EvidencePackHop then
                                            {
                                                hop with
                                                    Link =
                                                        EvidenceLink.Linked(
                                                            "tampered-digest",
                                                            EvidenceLink.detail hop.Link
                                                        )
                                            }
                                        else
                                            hop)
                        }
                }
                "a hop's detail",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.map (fun hop ->
                                        if hop.Id = EvidenceChain.DeployRecordHop then
                                            {
                                                hop with
                                                    Link =
                                                        EvidenceLink.Linked(
                                                            EvidenceLink.reference hop.Link,
                                                            "a rather more flattering account of the same seal"
                                                        )
                                            }
                                        else
                                            hop)
                        }
                }
                "the chain's own verdict digest",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                VerdictDigest = String.replicate 64 "a"
                        }
                }
                "the nested-attestation ruling",
                {
                    cleanBundle with
                        NestedAttestationDisposition = "re-signed"
                }
                "a not-proved statement",
                {
                    cleanBundle with
                        NotProved =
                            cleanBundle.NotProved
                            |> List.map (fun s ->
                                if s.Id = "records-not-truth" then
                                    {
                                        s with
                                            Statement = "nothing to see here"
                                    }
                                else
                                    s)
                }
                "a qualifier",
                EvidenceBundleExport.bundleWith
                    [
                        {
                            Id = "enumeration-completeness"
                            Verdict = "bounded"
                            Detail = "the closure enumeration was capped"
                        }
                    ]
                    observer
                    observedAt
                    cleanChain
            ]

            for label, perturbed in perturbations do
                let readdressed = readdress perturbed

                Expect.notEqual
                    readdressed.ContentId
                    cleanBundle.ContentId
                    $"changing {label} must change the content id"
        }

        test "a qualifier appends to the canonical form rather than shifting it" {
            // The extensibility property a later phase's typed verdict
            // depends on: everything before the qualifier block must be
            // byte-identical, so a reader diffing two canonical forms
            // across the upgrade can tell a growth from a re-statement.
            let qualified =
                EvidenceBundleExport.bundleWith
                    [
                        {
                            Id = "enumeration-completeness"
                            Verdict = "complete"
                            Detail = "every hop's enumeration was taken whole"
                        }
                    ]
                    observer
                    observedAt
                    cleanChain

            let baseForm = EvidenceBundle.canonicalForm cleanBundle
            let grownForm = EvidenceBundle.canonicalForm qualified

            Expect.stringStarts grownForm baseForm "adding a qualifier must append lines and move nothing before them"

            Expect.isGreaterThan grownForm.Length baseForm.Length "and it must actually add something"
        }
    ]

// ─── B — statement wrapping, and the crypto-free format half ─────────

let statementWrappingTests =
    testList "Phase 714 — the bundle wraps as an in-toto statement" [

        test "the statement is in-toto v1 under the bundle's own predicate type" {
            let statement = JsonNode.Parse(EvidenceBundleExport.statementJson cleanBundle)

            Expect.equal
                (statement["_type"].GetValue<string>())
                DsseEnvelope.StatementType
                "the statement declares in-toto Statement v1"

            Expect.equal
                (statement["predicateType"].GetValue<string>())
                EvidenceBundleExport.PredicateType
                "and the bundle's own predicate type"
        }

        test "the bundle predicate type is distinct from the certificate types" {
            // A verifier keys on `predicateType` to decide what shape it
            // is about to read; two shapes under one URI would make that
            // key meaningless.
            Expect.notEqual
                EvidenceBundleExport.PredicateType
                ToolUp.Facts.CertificateEnvelope.PredicateType
                "a bundle is a different claim from a grounding certificate"

            Expect.notEqual
                EvidenceBundleExport.PredicateType
                ToolUp.Facts.CertificateEnvelope.AttestedPredicateType
                "and from an attested grounding certificate"
        }

        test "the statement's subject digest is the bundle's content id" {
            let statement = JsonNode.Parse(EvidenceBundleExport.statementJson cleanBundle)
            let subject = statement["subject"].AsArray()

            Expect.equal subject.Count 1 "one subject — the bundle addresses one record set"

            let entry = subject.Item 0
            let digestSet = entry["digest"]
            let published = digestSet["sha256"].GetValue<string>()

            Expect.equal
                published
                cleanBundle.ContentId
                "the subject digest is the content id, so a holder claim-checks against an id they already have"
        }

        test "the content id is the sha256 of the canonical bytes a stock tool would hash" {
            let bytes = EvidenceBundleExport.canonicalBytes cleanBundle

            Expect.equal
                (DsseEnvelope.sha256Hex bytes)
                cleanBundle.ContentId
                "the blob a stock verifier hashes must be the artefact the subject names"
        }

        test "a consumer can read the bundle's shape with no verifier and no key" {
            // The format half is crypto-free by design: this reads the
            // payload with nothing but base64 and a JSON parser.
            let envelope: DsseEnvelope = {
                PayloadType = DsseEnvelope.InTotoPayloadType
                Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(EvidenceBundleExport.statementJson cleanBundle))
                Signatures = []
            }

            match DsseEnvelope.readStatement envelope with
            | Error verdict -> failtestf "an unsigned bundle statement must still be readable: %A" verdict
            | Ok statement ->
                match EvidenceBundleExport.readBundle statement.PredicateJson with
                | Error verdict -> failtestf "the predicate must read back as a bundle: %A" verdict
                | Ok read ->
                    Expect.equal read.ContentId cleanBundle.ContentId "the content id survives the round trip"
                    Expect.equal (List.length read.Chain.Hops) 7 "and so does the full hop list"
        }
    ]

// ─── C — the transcode-vs-re-sign ruling, pinned ─────────────────────

let nestedAttestationRulingTests =
    testList "Phase 714 — nested attestations are carried verbatim" [

        test "the ruling is written into the document, not known about the producer" {
            Expect.equal
                cleanBundle.NestedAttestationDisposition
                EvidenceBundle.CarriedVerbatim
                "every bundle declares how it treated inner signatures"

            let predicate = JsonNode.Parse(EvidenceBundleExport.predicateJson cleanBundle)

            Expect.equal
                (predicate["NestedAttestationDisposition"].GetValue<string>())
                EvidenceBundle.CarriedVerbatim
                "and the declaration is on the wire, where a verifier reads it"
        }

        test "an inner attestation's reference rides byte-identically into the bundle" {
            // Transcode, concretely: the deploy seal reference, the
            // signed pack manifest digest and the ledger position are
            // each the identity of an artefact somebody else signed, and
            // the bundle carries them exactly as the walk recorded them.
            let referenceFor hopId =
                cleanBundle.Chain.Hops
                |> List.find (fun hop -> hop.Id = hopId)
                |> _.Link
                |> EvidenceLink.reference

            Expect.equal
                (referenceFor EvidenceChain.DeployRecordHop)
                deploySealReference
                "the sealed deploy record's reference is unchanged"

            Expect.equal
                (referenceFor EvidenceChain.EvidencePackHop)
                packManifestDigest
                "the signed pack manifest digest is unchanged"

            Expect.equal
                (referenceFor EvidenceChain.LedgerPositionHop)
                ledgerPosition
                "the signed ledger head's position is unchanged"

            let predicateText = EvidenceBundleExport.predicateJson cleanBundle

            for reference in [ deploySealReference; packManifestDigest; ledgerPosition ] do
                Expect.stringContains
                    predicateText
                    reference
                    "and each reaches the wire verbatim rather than being re-derived"
        }

        test "the export adds exactly one signature and re-asserts nothing" {
            async {
                let secrets = InMemorySecretStore() :> ISecretStore
                let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

                match! EvidenceBundleExport.export signer cleanBundle with
                | Error e -> return failtestf "export must succeed: %s" e
                | Ok envelope ->
                    Expect.equal
                        (List.length envelope.Signatures)
                        1
                        "one outer signature — the inner attestations are carried, so there is nothing a second could say"

                    Expect.equal envelope.Signatures.Head.KeyId keyId "under the deployment's own key id"
            }
            |> Async.RunSynchronously
        }

        test "a document declaring another ruling is refused by name" {
            // The pin. If the estate ever adopts re-signing, it is a new
            // disposition and a new verifier leg — never a silent change
            // of meaning under the same document shape.
            let reSigned =
                readdress {
                    cleanBundle with
                        NestedAttestationDisposition = "re-signed"
                }

            match EvidenceBundleExport.verifyBundle reSigned with
            | BundleIntegrity.Intact ->
                failtest "a bundle claiming a disposition this verifier cannot check must not pass"
            | BundleIntegrity.BrokenAt(position, reason) ->
                Expect.equal position "bundle/nestedAttestationDisposition" "the refusal names the field"
                Expect.stringContains reason "re-signed" "and quotes what it read"
        }
    ]

// ─── D — the pure verifier ───────────────────────────────────────────

let pureVerifierTests =
    testList "Phase 714 — the verifier is pure and needs no platform" [

        test "a well-formed bundle is Intact" {
            Expect.equal
                (EvidenceBundleExport.verifyBundle cleanBundle)
                BundleIntegrity.Intact
                "a clean bundle verifies"

            Expect.equal
                (EvidenceBundleExport.verifyBundle mixedBundle)
                BundleIntegrity.Intact
                "and so does a bundle whose CHAIN carries a break — the bundle's integrity is not the deployment's verdict"
        }

        test "the verifier runs against an injected digest, with no cryptography" {
            // The property that makes it portable: `verifyWith` takes the
            // hash as an argument, so a host with no crypto package
            // reaches the same answer. Probed with a deliberately
            // non-cryptographic digest, re-addressing the bundle under
            // it, so the check exercises the injection rather than
            // agreeing with the shipped SHA-256 by accident.
            let toyDigest (canonical: string) =
                canonical
                |> Seq.fold (fun acc c -> (acc * 131 + int c) % 1000000007) 17
                |> string

            let underToy =
                let chainDigest = toyDigest (EvidenceChain.canonicalForm cleanChain.Hops)

                let rebased = {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                VerdictDigest = chainDigest
                        }
                }

                {
                    rebased with
                        ContentId = toyDigest (EvidenceBundle.canonicalForm rebased)
                }

            Expect.equal
                (EvidenceBundle.verifyWith toyDigest underToy)
                BundleIntegrity.Intact
                "the verifier's only cryptographic dependency is the function it was handed"
        }

        test "every tamper is reported at the position it broke" {
            let cases: (string * EvidenceBundle * string) list = [
                "a hop altered after the walk",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.map (fun hop ->
                                        if hop.Id = EvidenceChain.EvidencePackHop then
                                            {
                                                hop with
                                                    Link = EvidenceLink.Linked("swapped", EvidenceLink.detail hop.Link)
                                            }
                                        else
                                            hop)
                        }
                },
                "bundle/chain/verdictDigest"

                "a hop dropped from the walk",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops = cleanBundle.Chain.Hops |> List.filter (fun hop -> hop.Ordinal <> 3)
                        }
                },
                "bundle/chain/hops"

                "a hop renumbered",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.map (fun hop -> if hop.Ordinal = 2 then { hop with Ordinal = 9 } else hop)
                        }
                },
                "bundle/chain/hops[1]"

                "a hop re-ordered",
                {
                    cleanBundle with
                        Chain = {
                            cleanBundle.Chain with
                                Hops =
                                    cleanBundle.Chain.Hops
                                    |> List.rev
                                    |> List.mapi (fun i hop -> { hop with Ordinal = i + 1 })
                        }
                },
                "bundle/chain/hops[0]"

                "a flattered outcome",
                {
                    cleanBundle with
                        Chain = {
                            mixedBundle.Chain with
                                Outcome = EvidenceChainOutcome.ChainComplete
                        }
                },
                "bundle/chain/outcome"

                "a stripped claim boundary", { cleanBundle with NotProved = [] }, "bundle/notProved"

                "a schema this verifier cannot read",
                {
                    cleanBundle with
                        SchemaVersion = EvidenceBundle.SchemaVersion + 1
                },
                "bundle/schemaVersion"
            ]

            for label, tampered, expectedPosition in cases do
                // Re-addressed, so each case probes the property it names
                // rather than tripping the content-id check first. The
                // content id's own check is the case below.
                let readdressed = readdress tampered

                match EvidenceBundle.verifyWith EvidenceBundleExport.digest readdressed with
                | BundleIntegrity.Intact -> failtestf "%s must not verify" label
                | BundleIntegrity.BrokenAt(position, _) ->
                    Expect.equal position expectedPosition $"{label} must be reported at its own position"
        }

        test "a bundle whose content id was not recomputed breaks at the id" {
            let restated = {
                cleanBundle with
                    Observer = "a different deployment claiming this walk"
                    NotProved =
                        cleanBundle.NotProved
                        |> List.map (fun s ->
                            if s.Id = "uncomposed-substrate-is-silent" then
                                {
                                    s with
                                        Statement = "everything here is fine"
                                }
                            else
                                s)
            }

            match EvidenceBundleExport.verifyBundle restated with
            | BundleIntegrity.Intact -> failtest "a restated bundle must not verify under its old id"
            | BundleIntegrity.BrokenAt(position, _) ->
                Expect.equal position "bundle/contentId" "the id is the last line of defence and it names itself"
        }
    ]

// ─── E — the claim boundary is on every bundle ───────────────────────

let claimBoundaryTests =
    testList "Phase 714 — the claim boundary is data on every bundle" [

        test "a clean bundle carries the not-proved statements" {
            Expect.equal cleanChain.Outcome EvidenceChainOutcome.ChainComplete "the fixture really is the clean case"

            Expect.isNonEmpty cleanBundle.NotProved "a caveat that appears only on failures is a caveat nobody reads"

            let ids = cleanBundle.NotProved |> List.map _.Id

            for expected in
                [
                    "records-not-truth"
                    "work-quality-not-claimed"
                    "uncomposed-substrate-is-silent"
                    "code-never-composed-behaved"
                    "signature-binds-the-document"
                ] do
                Expect.contains ids expected "the boundary names each bound it stands on"
        }

        test "narrowing is never closing" {
            let bare = chainOf (EvidenceChain.allAbsent "nothing is composed here")
            let bareBundle = EvidenceBundleExport.bundleOf observer observedAt bare

            let narrowingOf (bundle: EvidenceBundle) =
                bundle.NotProved
                |> List.find (fun s -> s.Id = "records-not-truth")
                |> _.Narrowing

            Expect.isNone (narrowingOf bareBundle) "an unlinked chain narrows nothing"
            Expect.isSome (narrowingOf cleanBundle) "a complete chain names what its joins do cover"

            Expect.equal
                (List.length bareBundle.NotProved)
                (List.length cleanBundle.NotProved)
                "and the statement count is the same either way — a resolved join shrinks a bound, it never removes one"
        }

        test "the rendered bundle prints the boundary on a pass" {
            let text = EvidenceBundle.render cleanBundle

            Expect.stringContains text "What this bundle does NOT prove" "the rendering carries it too"
            Expect.stringContains text "carried-verbatim" "and states the nested-attestation ruling"
        }

        test "the verify command prints the boundary and exits zero on a pass" {
            async {
                let secrets = InMemorySecretStore() :> ISecretStore
                let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

                match! EvidenceBundleExport.export signer cleanBundle with
                | Error e -> return failtestf "export must succeed: %s" e
                | Ok envelope ->
                    let run = EvidenceBundleExport.verifyCommand (DsseEnvelope.toJson envelope)

                    Expect.equal run.ExitCode 0 "an intact bundle exits zero"
                    Expect.equal run.Integrity BundleIntegrity.Intact "and reports intact"

                    Expect.stringContains
                        run.Report
                        "It says nothing about who signed it"
                        "a structural pass must not be readable as a signature pass"

                    Expect.stringContains
                        run.Report
                        "What this bundle does NOT prove"
                        "and the boundary prints on a PASS, which is the only run anybody reads"
            }
            |> Async.RunSynchronously
        }
    ]

// ─── The signed round trip, offline ──────────────────────────────────

let signedRoundTripTests =
    testList "Phase 714 — a bundle verifies offline against a key alone" [

        for algorithm, name in [ Ed25519, "Ed25519"; EcdsaP256, "ECDSA P-256" ] do
            test $"a bundle exported under {name} verifies and reads back" {
                async {
                    let secrets = InMemorySecretStore() :> ISecretStore
                    let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId algorithm

                    let audit = AuditLog.NoOpAuditLog() :> IAuditLog

                    let artefactSigner =
                        DefaultArtefactSigner.createSystem secrets audit keyId algorithm

                    let! publicKey = artefactSigner.VerifyKey()

                    match! EvidenceBundleExport.export signer cleanBundle with
                    | Error e -> return failtestf "export must succeed: %s" e
                    | Ok envelope ->
                        let expectation = EvidenceBundleExport.expectation (Some cleanBundle.ContentId)

                        match DsseEnvelopeSigning.verify publicKey expectation envelope with
                        | Error verdict -> return failtestf "offline verification must pass: %A" verdict
                        | Ok predicate ->
                            match EvidenceBundleExport.readBundle predicate with
                            | Error verdict -> return failtestf "the verified predicate must read: %A" verdict
                            | Ok read ->
                                Expect.equal read.ContentId cleanBundle.ContentId "the same bundle comes back"

                                Expect.equal
                                    (EvidenceBundleExport.verifyBundle read)
                                    BundleIntegrity.Intact
                                    "and it is structurally intact after a serialisation round trip"
                }
                |> Async.RunSynchronously
            }

        test "a payload altered after signing fails the signature check" {
            async {
                let secrets = InMemorySecretStore() :> ISecretStore
                let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId Ed25519

                let audit = AuditLog.NoOpAuditLog() :> IAuditLog
                let artefactSigner = DefaultArtefactSigner.createSystem secrets audit keyId Ed25519
                let! publicKey = artefactSigner.VerifyKey()

                match! EvidenceBundleExport.export signer cleanBundle with
                | Error e -> return failtestf "export must succeed: %s" e
                | Ok envelope ->
                    let tamperedBundle = readdress { mixedBundle with Observer = observer }

                    let tampered = {
                        envelope with
                            Payload =
                                Convert.ToBase64String(
                                    Encoding.UTF8.GetBytes(EvidenceBundleExport.statementJson tamperedBundle)
                                )
                    }

                    let expectation = EvidenceBundleExport.expectation None

                    match DsseEnvelopeSigning.verify publicKey expectation tampered with
                    | Ok _ -> return failtest "a re-bodied envelope must not verify"
                    | Error verdict ->
                        Expect.equal
                            verdict
                            EnvelopeSignatureInvalid
                            "the signature covers the PAE, so swapping the payload breaks it"

                        // …and the structural verifier still passes on the
                        // swapped document, which is the honest split: it
                        // says the document is self-consistent, never that
                        // it is yours.
                        Expect.equal
                            (EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson tampered))
                            BundleIntegrity.Intact
                            "structural integrity and authorship are different questions and only the second needs a key"
            }
            |> Async.RunSynchronously
        }

        test "a document of another predicate type is refused as that type" {
            let statement =
                DsseEnvelope.statementJson
                    [ EvidenceBundleExport.subjectFor cleanBundle ]
                    "https://example.test/attestations/something-else/v1"
                    "{}"

            let envelope: DsseEnvelope = {
                PayloadType = DsseEnvelope.InTotoPayloadType
                Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes statement)
                Signatures = []
            }

            match EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson envelope) with
            | BundleIntegrity.Intact -> failtest "a statement of another shape must not read as a bundle"
            | BundleIntegrity.BrokenAt(position, reason) ->
                Expect.equal position "document/predicateType" "the reader names what it is holding"
                Expect.stringContains reason "something-else" "and quotes the type it read"
        }

        test "a document whose subject names a different bundle is refused at the subject" {
            let statement =
                DsseEnvelope.statementJson
                    [
                        {
                            Name = EvidenceBundle.SubjectName
                            Digest = [ "sha256", mixedBundle.ContentId ]
                        }
                    ]
                    EvidenceBundleExport.PredicateType
                    (EvidenceBundleExport.predicateJson cleanBundle)

            let envelope: DsseEnvelope = {
                PayloadType = DsseEnvelope.InTotoPayloadType
                Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes statement)
                Signatures = []
            }

            match EvidenceBundleExport.verifyDocument (DsseEnvelope.toJson envelope) with
            | BundleIntegrity.Intact -> failtest "a statement about a different bundle must not pass"
            | BundleIntegrity.BrokenAt(position, _) ->
                Expect.equal position "document/subject" "the mismatch is reported as a mismatch, not as corruption"
        }
    ]

// ─── The fixture the out-of-band stock-tooling check consumes ────────

let fixtureEmissionTests =
    testList "Phase 714 — the stock-tooling fixture" [

        test "the fixture writes an envelope, its canonical blob and a public key" {
            // Written to a temp directory the test cleans up, UNLESS
            // `TOOLUP_BUNDLE_FIXTURE_DIR` names one to keep. That switch
            // is how the out-of-band stock-tooling check gets its input
            // reproducibly: anyone can re-emit the exact three files an
            // unmodified cosign-class verifier is pointed at, without a
            // bespoke emitter that could drift from what ships.
            //
            // The test itself exists to keep the fixture SHAPE honest —
            // an envelope a stock verifier can consume, a blob whose
            // digest is the subject it claim-checks, and the public key.
            async {
                let secrets = InMemorySecretStore() :> ISecretStore
                let signer = DsseEnvelopeSigning.fromSecretStore secrets keyId EcdsaP256

                let audit = AuditLog.NoOpAuditLog() :> IAuditLog

                let artefactSigner =
                    DefaultArtefactSigner.createSystem secrets audit keyId EcdsaP256

                let! publicKey = artefactSigner.VerifyKey()

                match! EvidenceBundleExport.export signer cleanBundle with
                | Error e -> return failtestf "export must succeed: %s" e
                | Ok envelope ->
                    let kept =
                        match Environment.GetEnvironmentVariable "TOOLUP_BUNDLE_FIXTURE_DIR" with
                        | null -> None
                        | path when String.IsNullOrWhiteSpace path -> None
                        | path -> Some path

                    let dir =
                        match kept with
                        | Some path -> path
                        | None -> Path.Combine(Path.GetTempPath(), $"toolup-bundle-fixture-{Guid.NewGuid():N}")

                    Directory.CreateDirectory dir |> ignore

                    try
                        let envelopePath = Path.Combine(dir, "bundle.dsse.json")
                        let blobPath = Path.Combine(dir, "bundle.canonical.txt")
                        let keyPath = Path.Combine(dir, "bundle.pub.pem")

                        File.WriteAllText(envelopePath, DsseEnvelope.toJson envelope)
                        File.WriteAllBytes(blobPath, EvidenceBundleExport.canonicalBytes cleanBundle)
                        File.WriteAllText(keyPath, publicKey.Pem)

                        Expect.equal
                            (DsseEnvelope.sha256Hex (File.ReadAllBytes blobPath))
                            cleanBundle.ContentId
                            "the blob on disk digests to the subject a stock verifier will claim-check"

                        Expect.equal
                            (EvidenceBundleExport.verifyCommand (File.ReadAllText envelopePath)).ExitCode
                            0
                            "and the file on disk verifies structurally, which is what a cold run repeats"

                        // The verdict text a cold run must reproduce
                        // byte for byte, written beside the fixture so
                        // the comparison is a diff rather than a
                        // recollection.
                        match kept with
                        | Some _ ->
                            File.WriteAllText(
                                Path.Combine(dir, "bundle.warm-verdict.txt"),
                                (EvidenceBundleExport.verifyCommand (File.ReadAllText envelopePath)).Report
                            )
                        | None -> ()
                    finally
                        match kept with
                        | Some _ -> ()
                        | None ->
                            try
                                Directory.Delete(dir, true)
                            with _ ->
                                ()
            }
            |> Async.RunSynchronously
        }
    ]