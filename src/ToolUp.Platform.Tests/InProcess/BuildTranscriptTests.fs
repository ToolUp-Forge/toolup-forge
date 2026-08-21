module ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

open System
open System.IO
open System.Security.Cryptography
open System.Text
open Expecto
open ToolUp.Platform

// ─── Build transcript + sealed deploy record ─────────────────────────
//
// Two properties carry this substrate, and both are probed in both
// directions rather than only the direction that would pass:
//
//   * SAME inputs ⇒ SAME digest. Probed with the dependency closure
//     shuffled and duplicated, because resolution order is an artefact
//     of the resolver and must not reach the digest.
//   * DIFFERENT inputs ⇒ DIFFERENT digest. Probed field by field, one
//     perturbation at a time, because a canonical form that silently
//     dropped a field would still pass the first property perfectly.
//
// The second is the one worth insisting on. A determinism test that
// only ever hashes equal inputs cannot distinguish a correct canonical
// form from `fun _ -> "constant"`.

// ─── A stub sealer ───────────────────────────────────────────────────
//
// Keyed-hash rather than a real signature: the deploy plane's contract
// with a sealer is "bytes in, opaque token out, refuse a token that
// does not match", and a stub that honours it exercises the substrate
// without dragging a crypto surface into this pack. The signing
// companion's own pack covers the real signer over the same seam.

type StubSealer(secret: string, ?scheme: string) =
    let scheme = defaultArg scheme "test.stub.v1"

    let tag (bytes: byte[]) =
        use mac = new HMACSHA256(Encoding.UTF8.GetBytes secret)
        Convert.ToHexString(mac.ComputeHash bytes).ToLowerInvariant()

    interface IDeployRecordSealer with
        member _.Scheme() = scheme

        member _.Seal(canonicalBytes: byte[]) = async {
            return
                Ok {
                    Scheme = scheme
                    KeyId = "stub-key"
                    Claim = "stub"
                    Token = tag canonicalBytes
                    SealedAt = DateTimeOffset.UnixEpoch
                }
        }

        member _.VerifySeal(canonicalBytes: byte[], seal: DeployRecordSeal) = async {
            if seal.Scheme <> scheme then
                return Error $"seal scheme '{seal.Scheme}' is not '{scheme}'"
            elif seal.Token = tag canonicalBytes then
                return Ok()
            else
                return Error "seal does not match the supplied bytes"
        }

// ─── Fixtures ────────────────────────────────────────────────────────

let private toolchain: BuildToolchain = {
    Name = "example-sdk"
    Version = "10.0.203"
}

let private dependencies: BuildDependency list = [
    {
        Id = "Alpha.Package"
        Version = "1.2.3"
        ContentDigest = "aa11"
    }
    {
        Id = "Beta.Package"
        Version = "0.9.0"
        ContentDigest = "bb22"
    }
    {
        Id = "Gamma.Package"
        Version = "4.0.0"
        ContentDigest = "cc33"
    }
]

let private entryPoint: BuildEntryPoint = {
    Path = "src/Program.fs"
    ContentDigest = "dd44"
}

let private transcript = BuildTranscript.create toolchain dependencies entryPoint

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Example"
            Slug = "example"
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
        }
        Secrets = [
            {
                Name = "DB_URL"
                Source = "vault://kv/db"
            }
        ]
        Modules = [
            {
                PackageId = "ToolUp.AI"
                Version = "0.5.0"
            }
        ]
}

let private digestOf = DeployRecords.transcriptDigest

// ─── Transcript determinism ──────────────────────────────────────────

let transcriptDeterminismTests =
    testList "Phase 656 — build transcript is deterministic" [

        test "same inputs produce the same canonical form and digest" {
            let again = BuildTranscript.create toolchain dependencies entryPoint

            Expect.equal
                (BuildTranscript.canonicalForm again)
                (BuildTranscript.canonicalForm transcript)
                "identical inputs must canonicalise identically"

            Expect.equal (digestOf again) (digestOf transcript) "identical inputs must digest identically"
        }

        test "dependency order does not reach the digest" {
            let shuffled =
                BuildTranscript.create toolchain (dependencies |> List.rev) entryPoint

            Expect.equal
                (digestOf shuffled)
                (digestOf transcript)
                "the closure is a set — resolver order is not a fact about the build"
        }

        test "a duplicated dependency does not reach the digest" {
            let duplicated =
                BuildTranscript.create toolchain (dependencies @ [ List.head dependencies ]) entryPoint

            Expect.equal (digestOf duplicated) (digestOf transcript) "the closure is de-duplicated"
        }

        test "the canonical dependency order is sorted and de-duplicated" {
            let noisy =
                BuildTranscript.create toolchain (dependencies |> List.rev |> List.append dependencies) entryPoint

            let canonical = BuildTranscript.canonicalDependencies noisy

            Expect.equal canonical.Length 3 "duplicates collapse"

            Expect.equal
                (canonical |> List.map _.Id)
                [ "Alpha.Package"; "Beta.Package"; "Gamma.Package" ]
                "canonical order is ordinal by id"
        }

        test "an empty transcript digests stably" {
            Expect.equal
                (digestOf BuildTranscript.empty)
                (digestOf BuildTranscript.empty)
                "the empty transcript is a value like any other"
        }
    ]

// ─── Transcript sensitivity (the other direction) ────────────────────

let transcriptSensitivityTests =
    let perturbations: (string * BuildTranscript) list = [
        "toolchain name", BuildTranscript.create { toolchain with Name = "other-sdk" } dependencies entryPoint
        "toolchain version", BuildTranscript.create { toolchain with Version = "10.0.204" } dependencies entryPoint
        "dependency id",
        BuildTranscript.create
            toolchain
            ({
                dependencies[0] with
                    Id = "Alpha.Package.Extra"
             }
             :: List.tail dependencies)
            entryPoint
        "dependency version",
        BuildTranscript.create
            toolchain
            ({
                dependencies[0] with
                    Version = "1.2.4"
             }
             :: List.tail dependencies)
            entryPoint
        "dependency content digest",
        BuildTranscript.create
            toolchain
            ({
                dependencies[0] with
                    ContentDigest = "aa12"
             }
             :: List.tail dependencies)
            entryPoint
        "a dependency removed", BuildTranscript.create toolchain (List.tail dependencies) entryPoint
        "entry-point path",
        BuildTranscript.create toolchain dependencies {
            entryPoint with
                Path = "src/Other.fs"
        }
        "entry-point content digest",
        BuildTranscript.create toolchain dependencies {
            entryPoint with
                ContentDigest = "dd45"
        }
    ]

    testList "Phase 656 — every recorded input reaches the digest" [
        for (label, perturbed) in perturbations do
            test $"changing the {label} changes the digest" {
                Expect.notEqual
                    (digestOf perturbed)
                    (digestOf transcript)
                    $"a canonical form that ignored the {label} would pass every equality probe and still be wrong"
            }

        test "the framing is injective across field boundaries" {
            // Two transcripts whose fields concatenate to the same text
            // but split differently. Without length prefixes these
            // would frame identically; with them they cannot.
            let a = BuildTranscript.create { Name = "ab"; Version = "c" } [] entryPoint
            let b = BuildTranscript.create { Name = "a"; Version = "bc" } [] entryPoint

            Expect.notEqual (digestOf a) (digestOf b) "length framing must prevent a field boundary being re-cut"
        }
    ]

// ─── Provenance + record canonical form ──────────────────────────────

let provenanceTests =
    testList "Phase 656 — deploy provenance" [

        test "none records nothing and is the identity" {
            Expect.isTrue (DeployProvenance.isEmpty DeployProvenance.none) "none is empty"
            Expect.isEmpty DeployProvenance.none.ArtifactDigests "no artifacts"
            Expect.isNone DeployProvenance.none.TranscriptDigest "no transcript digest"
            Expect.isNone DeployProvenance.none.UpstreamProvenanceDigest "no upstream digest"
        }

        test "artifact order does not reach the canonical form" {
            let artifacts = [
                { Path = "a.dll"; ContentDigest = "11" }
                { Path = "b.dll"; ContentDigest = "22" }
            ]

            let forward = DeployProvenance.none |> DeployProvenance.withArtifacts artifacts

            let backward =
                DeployProvenance.none |> DeployProvenance.withArtifacts (List.rev artifacts)

            Expect.equal
                (DeployProvenance.canonicalForm backward)
                (DeployProvenance.canonicalForm forward)
                "the artifact set is a set"
        }

        test "an absent optional slot is distinguishable from an empty one" {
            let absent = DeployProvenance.none

            let empty =
                DeployProvenance.none |> DeployProvenance.withUpstreamProvenanceDigest ""

            Expect.notEqual
                (DeployProvenance.canonicalForm empty)
                (DeployProvenance.canonicalForm absent)
                "None and Some \"\" must not canonicalise alike"
        }

        test "the opaque upstream slot reaches the canonical form" {
            let filled =
                DeployProvenance.none
                |> DeployProvenance.withUpstreamProvenanceDigest "opaque-value"

            let other =
                DeployProvenance.none
                |> DeployProvenance.withUpstreamProvenanceDigest "other-opaque-value"

            Expect.notEqual
                (DeployProvenance.canonicalForm filled)
                (DeployProvenance.canonicalForm other)
                "the platform never interprets the slot, but it does cover it"
        }

        test "a null provenance from an older persisted record coerces to none" {
            // What the platform's JSON path hands back for a field that
            // did not exist when the record was written. A null F# list
            // throws on the first list operation, so the read path
            // coerces rather than trusting the deserialiser.
            let fromOlderStore = Unchecked.defaultof<DeployProvenance>
            let coerced = DeployProvenance.coerce fromOlderStore

            Expect.isTrue (DeployProvenance.isEmpty coerced) "a null provenance reads as nothing recorded"
        }

        test "a null artifact list inside a provenance coerces to empty" {
            let partial: DeployProvenance = {
                ArtifactDigests = Unchecked.defaultof<DeployArtifactDigest list>
                TranscriptDigest = None
                UpstreamProvenanceDigest = None
            }

            let coerced = DeployProvenance.coerce partial

            Expect.isEmpty coerced.ArtifactDigests "a null list becomes an empty one"
        }
    ]

let deployRecordCanonicalFormTests =
    let baseRecord =
        DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

    let perturbations: (string * DeployRecord) list = [
        "the deploy id",
        {
            baseRecord with
                DeployId = "deploy-2"
        }
        "the tenant id",
        {
            baseRecord with
                TenantId = "tenant-2"
        }
        "the build id", { baseRecord with BuildId = "build-2" }
        "the app slug",
        {
            baseRecord with
                Manifest = {
                    manifest with
                        App = { manifest.App with Slug = "example-2" }
                }
        }
        "a secret source",
        {
            baseRecord with
                Manifest = {
                    manifest with
                        Secrets = [
                            {
                                Name = "DB_URL"
                                Source = "vault://kv/other"
                            }
                        ]
                }
        }
        "a pinned module version",
        {
            baseRecord with
                Manifest = {
                    manifest with
                        Modules = [
                            {
                                PackageId = "ToolUp.AI"
                                Version = "0.6.0"
                            }
                        ]
                }
        }
        "the recorded transcript digest",
        {
            baseRecord with
                Provenance =
                    DeployProvenance.none
                    |> DeployProvenance.withTranscriptDigest (digestOf transcript)
        }
        "the opaque upstream slot",
        {
            baseRecord with
                Provenance =
                    DeployProvenance.none
                    |> DeployProvenance.withUpstreamProvenanceDigest "opaque-value"
        }
    ]

    testList "Phase 656 — the deploy record's canonical form covers the whole record" [
        test "the same record canonicalises identically" {
            let again =
                DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

            Expect.equal
                (DeployRecord.canonicalForm again)
                (DeployRecord.canonicalForm baseRecord)
                "identical records canonicalise identically"
        }

        test "manifest authoring order does not reach the canonical form" {
            let twoSecrets = [
                { Name = "A_URL"; Source = "vault://a" }
                { Name = "B_URL"; Source = "vault://b" }
            ]

            let forward = {
                baseRecord with
                    Manifest = { manifest with Secrets = twoSecrets }
            }

            let backward = {
                baseRecord with
                    Manifest = {
                        manifest with
                            Secrets = List.rev twoSecrets
                    }
            }

            Expect.equal
                (DeployRecord.canonicalForm backward)
                (DeployRecord.canonicalForm forward)
                "list sections are sorted before framing"
        }

        for (label, perturbed) in perturbations do
            test $"changing {label} changes the canonical form" {
                Expect.notEqual
                    (DeployRecord.canonicalForm perturbed)
                    (DeployRecord.canonicalForm baseRecord)
                    $"a canonical form that ignored {label} would let it be rewritten under a valid seal"
            }
    ]

// ─── Verification ────────────────────────────────────────────────────

let private withTempTree (body: string -> 'a) : 'a =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-deployrecord-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore

    try
        body root
    finally
        try
            Directory.Delete(root, true)
        with _ ->
            ()

let deployRecordVerificationTests =
    testList "Phase 656 — sealed deploy-record verification" [

        test "a sealed record over intact files and its transcript verifies" {
            withTempTree (fun root ->
                File.WriteAllText(Path.Combine(root, "app.dll"), "the application")
                File.WriteAllText(Path.Combine(root, "config.json"), "{}")

                let provenance =
                    DeployProvenance.none
                    |> DeployProvenance.withArtifacts (DeployRecords.artifactsUnder root)
                    |> DeployProvenance.withTranscriptDigest (digestOf transcript)

                let record = DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

                let sealer = StubSealer "secret" :> IDeployRecordSealer

                let outcome =
                    async {
                        let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

                        let signedRecord = {
                            Record = record
                            Seal = Result.defaultValue Unchecked.defaultof<DeployRecordSeal> seal
                        }

                        return!
                            DeployRecords.verify sealer (DeployRecords.locateUnder root) (Some transcript) signedRecord
                    }
                    |> Async.RunSynchronously

                Expect.isOk outcome "an untampered deployment verifies")
        }

        test "tampering with a deployed artifact fails verification NAMING the file" {
            withTempTree (fun root ->
                File.WriteAllText(Path.Combine(root, "app.dll"), "the application")
                File.WriteAllText(Path.Combine(root, "config.json"), "{}")

                let provenance =
                    DeployProvenance.none
                    |> DeployProvenance.withArtifacts (DeployRecords.artifactsUnder root)

                let record = DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

                let sealer = StubSealer "secret" :> IDeployRecordSealer

                let signedRecord =
                    async {
                        let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

                        return {
                            Record = record
                            Seal = Result.defaultValue Unchecked.defaultof<DeployRecordSeal> seal
                        }
                    }
                    |> Async.RunSynchronously

                // Perturb ONE deployed file, after sealing.
                File.WriteAllText(Path.Combine(root, "config.json"), "{\"tampered\":true}")

                let outcome =
                    DeployRecords.verify sealer (DeployRecords.locateUnder root) None signedRecord
                    |> Async.RunSynchronously

                match outcome with
                | Ok() -> failtest "a tampered artifact must not verify"
                | Error failures ->
                    let named =
                        failures
                        |> List.choose (function
                            | DeployRecords.ArtifactDigestMismatch(path, _, _) -> Some path
                            | _ -> None)

                    Expect.equal named [ "config.json" ] "the failure names the file that changed"

                    Expect.isFalse
                        (failures
                         |> List.exists (function
                             | DeployRecords.SealRejected _ -> true
                             | _ -> false))
                        "the seal itself is untouched — only the file changed")
        }

        test "a missing deployed artifact fails verification naming it" {
            withTempTree (fun root ->
                File.WriteAllText(Path.Combine(root, "app.dll"), "the application")

                let provenance =
                    DeployProvenance.none
                    |> DeployProvenance.withArtifacts (DeployRecords.artifactsUnder root)

                File.Delete(Path.Combine(root, "app.dll"))

                match DeployRecords.verifyArtifacts (DeployRecords.locateUnder root) provenance with
                | Ok() -> failtest "a missing artifact must not verify"
                | Error failures -> Expect.equal failures [ DeployRecords.ArtifactMissing "app.dll" ] "names the file")
        }

        test "editing the record after sealing breaks the seal" {
            let record =
                DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

            let sealer = StubSealer "secret" :> IDeployRecordSealer

            let outcome =
                async {
                    let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

                    let edited = {
                        record with
                            Provenance =
                                DeployProvenance.none |> DeployProvenance.withUpstreamProvenanceDigest "planted"
                    }

                    return!
                        DeployRecords.verifySeal sealer {
                            Record = edited
                            Seal = Result.defaultValue Unchecked.defaultof<DeployRecordSeal> seal
                        }
                }
                |> Async.RunSynchronously

            match outcome with
            | Ok() -> failtest "an edited record must not verify against its old seal"
            | Error failures ->
                Expect.isTrue
                    (failures
                     |> List.exists (function
                         | DeployRecords.SealRejected _ -> true
                         | _ -> false))
                    "the seal is rejected"
        }

        test "a seal from another scheme is refused, not mis-parsed" {
            let record =
                DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

            let foreign = StubSealer("secret", "other.scheme.v1") :> IDeployRecordSealer
            let ours = StubSealer "secret" :> IDeployRecordSealer

            let outcome =
                async {
                    let! seal = foreign.Seal(DeployRecords.canonicalBytes record)

                    return!
                        DeployRecords.verifySeal ours {
                            Record = record
                            Seal = Result.defaultValue Unchecked.defaultof<DeployRecordSeal> seal
                        }
                }
                |> Async.RunSynchronously

            match outcome with
            | Ok() -> failtest "a foreign scheme must not verify"
            | Error failures ->
                Expect.isTrue
                    (failures
                     |> List.exists (function
                         | DeployRecords.SealSchemeMismatch _ -> true
                         | _ -> false))
                    "the scheme mismatch is reported as itself, not as a rejected signature"
        }

        test "a transcript digest that does not match the supplied transcript is reported" {
            let provenance =
                DeployProvenance.none
                |> DeployProvenance.withTranscriptDigest (digestOf BuildTranscript.empty)

            match DeployRecords.verifyTranscript transcript provenance with
            | Ok() -> failtest "a mismatched transcript digest must not verify"
            | Error failures ->
                Expect.isTrue
                    (failures
                     |> List.exists (function
                         | DeployRecords.TranscriptDigestMismatch _ -> true
                         | _ -> false))
                    "the mismatch is reported"
        }

        test "supplying a transcript against a record that recorded none is reported" {
            match DeployRecords.verifyTranscript transcript DeployProvenance.none with
            | Ok() -> failtest "there is no digest to check against"
            | Error failures -> Expect.equal failures [ DeployRecords.TranscriptNotRecorded ] "reported as itself"
        }

        test "a provenance recording no artifacts claims nothing and passes" {
            // Not a vacuous green being smuggled in: a provenance that
            // records no artifacts makes no claim about any file, and a
            // check that invented a failure would be measuring the
            // absence of a claim rather than a broken one.
            withTempTree (fun root ->
                Expect.isOk
                    (DeployRecords.verifyArtifacts (DeployRecords.locateUnder root) DeployProvenance.none)
                    "no recorded artifacts, no artifact claims")
        }
    ]
// ═══ Phase 659 — the dependency-closure upstream-provenance join ═════
//
// The closure's canonical form is probed in both directions, like the
// transcript's. Beyond that, the three legs the join adds each get
// their own falsifier: capture reads the restore's OWN output (and
// errors on a missing or malformed file rather than reporting an empty
// closure); the attest seam is exercised provider-absent (honestly
// unattested — the pre-join behaviour) and against a stub ledger
// covering each answer shape; and the closure's digest is bound into
// the sealed record — perturb the closure and the seal refuses.

// ─── A stub upstream release provider ────────────────────────────────
//
// Answers from a fixed coverage map, or fails every resolve when
// constructed with a failure reason. The seam's contract is "one
// answer per (id, version), failure as data", and a stub honouring it
// exercises the join without a ledger.

type StubReleaseProvider(coverage: Map<string * string, UpstreamReleaseCoverage>, ?failWith: string) =
    interface IUpstreamReleaseProvider with
        member _.Ledger() = "test.ledger"

        member _.Resolve(packageId, version) = async {
            match failWith with
            | Some reason -> return Error reason
            | None ->
                return
                    coverage
                    |> Map.tryFind (packageId, version)
                    |> Option.defaultValue UpstreamReleaseCoverage.NotTracked
                    |> Ok
        }

// ─── Closure fixtures ────────────────────────────────────────────────

let private closureEntries: DependencyClosureEntry list = [
    DependencyClosure.unattestedEntry "Alpha.Package" "1.2.3" "https://feed.example/v3/index.json" "aa11"
    DependencyClosure.unattestedEntry "Beta.Package" "0.9.0" "https://feed.example/v3/index.json" "bb22"
    DependencyClosure.unattestedEntry "Gamma.Package" "4.0.0" "" "cc33"
]

let private closure = DependencyClosure.create closureEntries

let private closureDigestOf = DeployRecords.closureDigest

let private attested reference (entry: DependencyClosureEntry) = {
    entry with
        Attestation = AttestedBy reference
}

// ─── Closure canonical form (both directions) ────────────────────────

let closureCanonicalFormTests =
    let entry = List.head closureEntries

    let withReason reason = {
        entry with
            Attestation = Unattested reason
    }

    let perturbations: (string * DependencyClosure) list = [
        "an entry id",
        DependencyClosure.create (
            {
                entry with
                    Id = "Alpha.Package.Extra"
            }
            :: List.tail closureEntries
        )
        "an entry version", DependencyClosure.create ({ entry with Version = "1.2.4" } :: List.tail closureEntries)
        "an entry source",
        DependencyClosure.create (
            {
                entry with
                    Source = "https://other.example/v3/index.json"
            }
            :: List.tail closureEntries
        )
        "an entry content digest",
        DependencyClosure.create ({ entry with ContentDigest = "aa12" } :: List.tail closureEntries)
        "an entry removed", DependencyClosure.create (List.tail closureEntries)
        "an attestation (unattested to attested)",
        DependencyClosure.create (
            attested { OpId = "act-42"; ActDigest = "ff00" } entry
            :: List.tail closureEntries
        )
        "an attested op id",
        DependencyClosure.create (attested { OpId = "act-43"; ActDigest = "" } entry :: List.tail closureEntries)
        "an attested act digest",
        DependencyClosure.create (attested { OpId = ""; ActDigest = "ff01" } entry :: List.tail closureEntries)
    ]

    testList "Phase 659 — dependency-closure canonical form" [

        test "same entries produce the same canonical form and digest" {
            let again = DependencyClosure.create closureEntries

            Expect.equal
                (DependencyClosure.canonicalForm again)
                (DependencyClosure.canonicalForm closure)
                "identical closures must canonicalise identically"

            Expect.equal (closureDigestOf again) (closureDigestOf closure) "identical closures must digest identically"
        }

        test "entry order does not reach the digest" {
            let shuffled = DependencyClosure.create (List.rev closureEntries)

            Expect.equal
                (closureDigestOf shuffled)
                (closureDigestOf closure)
                "the closure is a set — resolver order is not a fact about the build"
        }

        test "a duplicated entry does not reach the digest" {
            let duplicated =
                DependencyClosure.create (closureEntries @ [ List.head closureEntries ])

            Expect.equal (closureDigestOf duplicated) (closureDigestOf closure) "the closure is de-duplicated"
        }

        for (label, perturbed) in perturbations do
            test $"changing {label} changes the digest" {
                Expect.notEqual
                    (closureDigestOf perturbed)
                    (closureDigestOf closure)
                    $"a canonical form that ignored {label} would let it be rewritten under a valid seal"
            }

        test "every unattested reason digests distinctly" {
            let reasons = [
                ProviderAbsent
                ExternalPackage
                NoCoveringAct
                ResolutionFailed "the ledger timed out"
            ]

            let digests =
                reasons
                |> List.map (fun reason -> closureDigestOf (DependencyClosure.create [ withReason reason ]))

            Expect.equal
                (List.distinct digests).Length
                reasons.Length
                "reasons that canonicalised alike would be indistinguishable once sealed"
        }

        test "a resolution-failure reason reaches the digest" {
            let a = DependencyClosure.create [ withReason (ResolutionFailed "timeout") ]

            let b = DependencyClosure.create [ withReason (ResolutionFailed "unreachable") ]

            Expect.notEqual (closureDigestOf a) (closureDigestOf b) "the failure's own reason is part of the record"
        }

        test "the framing is injective across entry field boundaries" {
            let a =
                DependencyClosure.create [ DependencyClosure.unattestedEntry "ab" "c" "" "" ]

            let b =
                DependencyClosure.create [ DependencyClosure.unattestedEntry "a" "bc" "" "" ]

            Expect.notEqual
                (closureDigestOf a)
                (closureDigestOf b)
                "length framing must prevent a field boundary being re-cut"
        }

        test "the closure projects into the transcript's dependency shape verbatim" {
            let projected = DependencyClosure.toBuildDependencies closure

            Expect.equal
                projected
                [
                    {
                        Id = "Alpha.Package"
                        Version = "1.2.3"
                        ContentDigest = "aa11"
                    }
                    {
                        Id = "Beta.Package"
                        Version = "0.9.0"
                        ContentDigest = "bb22"
                    }
                    {
                        Id = "Gamma.Package"
                        Version = "4.0.0"
                        ContentDigest = "cc33"
                    }
                ]
                "one observation, two projections — the transcript records the same resolved set"
        }
    ]

// ─── The attest seam ─────────────────────────────────────────────────

let closureAttestationTests =
    testList "Phase 659 — closure attestation through the provider seam" [

        test "attesting with no provider is the identity on a captured closure" {
            let after = DependencyClosure.attest None closure |> Async.RunSynchronously

            Expect.equal
                (DependencyClosure.canonicalForm after)
                (DependencyClosure.canonicalForm closure)
                "capture already records provider-absent; asking nobody changes nothing"
        }

        test "attesting with no provider re-states every entry as provider-absent" {
            let previouslyAttested =
                DependencyClosure.create [ attested { OpId = "act-42"; ActDigest = "ff00" } (List.head closureEntries) ]

            let after =
                DependencyClosure.attest None previouslyAttested |> Async.RunSynchronously

            Expect.all
                (after.Entries |> List.map _.Attestation)
                (fun attestation -> attestation = Unattested ProviderAbsent)
                "an attestation pass that asked nobody must say so, whatever the closure said before"
        }

        test "a provider's three answer shapes map to the three attestations" {
            let provider =
                StubReleaseProvider(
                    Map [
                        ("Alpha.Package", "1.2.3"),
                        UpstreamReleaseCoverage.Covered { OpId = "act-42"; ActDigest = "ff00" }
                        ("Beta.Package", "0.9.0"), UpstreamReleaseCoverage.NotCovered
                    // Gamma.Package deliberately absent — the stub answers NotTracked.
                    ]
                )

            let after =
                DependencyClosure.attest (Some(provider :> IUpstreamReleaseProvider)) closure
                |> Async.RunSynchronously

            Expect.equal
                (after.Entries |> List.map (fun e -> e.Id, e.Attestation))
                [
                    "Alpha.Package", AttestedBy { OpId = "act-42"; ActDigest = "ff00" }
                    "Beta.Package", Unattested NoCoveringAct
                    "Gamma.Package", Unattested ExternalPackage
                ]
                "covered means attested by reference; tracked-but-uncovered and untracked are distinct reasons"
        }

        test "attestation replaces nothing but the attestation" {
            let provider =
                StubReleaseProvider(
                    Map [
                        ("Alpha.Package", "1.2.3"),
                        UpstreamReleaseCoverage.Covered { OpId = "act-42"; ActDigest = "ff00" }
                    ]
                )

            let after =
                DependencyClosure.attest (Some(provider :> IUpstreamReleaseProvider)) closure
                |> Async.RunSynchronously

            Expect.equal
                (after.Entries |> List.map (fun e -> e.Id, e.Version, e.Source, e.ContentDigest))
                (closure.Entries
                 |> List.map (fun e -> e.Id, e.Version, e.Source, e.ContentDigest))
                "the observed facts are capture's; attestation must not rewrite them"
        }

        test "a failing provider is recorded on the entry, not thrown and not dropped" {
            let provider = StubReleaseProvider(Map.empty, failWith = "the ledger timed out")

            let after =
                DependencyClosure.attest (Some(provider :> IUpstreamReleaseProvider)) closure
                |> Async.RunSynchronously

            Expect.equal after.Entries.Length closure.Entries.Length "no entry is dropped"

            Expect.all
                (after.Entries |> List.map _.Attestation)
                (fun attestation -> attestation = Unattested(ResolutionFailed "the ledger timed out"))
                "the failure's own reason lands on each entry"
        }

        test "every attestation renders distinguishably" {
            let rendered = [
                ClosureAttestation.describe (AttestedBy { OpId = "act-42"; ActDigest = "ff00" })
                ClosureAttestation.describe (Unattested ProviderAbsent)
                ClosureAttestation.describe (Unattested ExternalPackage)
                ClosureAttestation.describe (Unattested NoCoveringAct)
                ClosureAttestation.describe (Unattested(ResolutionFailed "timeout"))
            ]

            Expect.equal
                (List.distinct rendered).Length
                rendered.Length
                "reasons that render alike are not distinguishable"

            Expect.stringContains
                (ClosureAttestation.describe (AttestedBy { OpId = "act-42"; ActDigest = "ff00" }))
                "act-42"
                "the reference names the act it stands on"
        }
    ]

// ─── Capture from the restore's own output ───────────────────────────

let closureCaptureTests =
    testList "Phase 659 — closure capture reads the restore's own output" [

        test "the resolved closure is observed from the assets output" {
            withTempTree (fun root ->
                // The restore's own layout: an assets file naming the
                // resolved packages, and per-package origin metadata
                // beside each extracted package.
                let packagesDir = Path.Combine(root, "packages")
                let alphaDir = Path.Combine(packagesDir, "alpha.package", "1.2.3")
                Directory.CreateDirectory alphaDir |> ignore

                Directory.CreateDirectory(Path.Combine(packagesDir, "beta.package", "0.9.0"))
                |> ignore

                File.WriteAllText(
                    Path.Combine(alphaDir, ".nupkg.metadata"),
                    "{\"version\": 2, \"contentHash\": \"unused\", \"source\": \"https://feed.example/v3/index.json\"}"
                )

                let hashBytes = [| 0x01uy; 0x02uy; 0xabuy; 0xcduy |]
                let hashBase64 = Convert.ToBase64String hashBytes

                let assetsPath = Path.Combine(root, "project.assets.json")

                File.WriteAllText(
                    assetsPath,
                    sprintf
                        """{
  "version": 3,
  "libraries": {
    "Alpha.Package/1.2.3": { "type": "package", "sha512": "%s", "path": "alpha.package/1.2.3" },
    "Beta.Package/0.9.0": { "type": "package", "sha512": "", "path": "beta.package/0.9.0" },
    "My.Project/1.0.0": { "type": "project" }
  },
  "packageFolders": { "%s": {} }
}"""
                        hashBase64
                        (packagesDir.Replace("\\", "/"))
                )

                match RestoreClosures.readAssetsFile assetsPath with
                | Error reason -> failtest $"capture must read the restore's output: {reason}"
                | Ok captured ->
                    Expect.equal
                        (captured.Entries
                         |> List.map (fun e -> e.Id, e.Version, e.Source, e.ContentDigest))
                        [
                            "Alpha.Package", "1.2.3", "https://feed.example/v3/index.json", "0102abcd"
                            "Beta.Package", "0.9.0", "", ""
                        ]
                        ("packages observed with the restore's own source and hash; the origin-less package "
                         + "records an EMPTY source, never a guessed one; the project reference is not a package")

                    Expect.all
                        (captured.Entries |> List.map _.Attestation)
                        (fun attestation -> attestation = Unattested ProviderAbsent)
                        "capture records what was resolved; attestation is a separate act")
        }

        test "a missing assets file is an error naming it, never an empty closure" {
            withTempTree (fun root ->
                let missing = Path.Combine(root, "project.assets.json")

                match RestoreClosures.readAssetsFile missing with
                | Ok _ -> failtest "an absent restore output must not read as 'no dependencies'"
                | Error reason -> Expect.stringContains reason "project.assets.json" "the error names the file")
        }

        test "a malformed assets file is an error, never an empty closure" {
            withTempTree (fun root ->
                let assetsPath = Path.Combine(root, "project.assets.json")
                File.WriteAllText(assetsPath, "{ not json")

                match RestoreClosures.readAssetsFile assetsPath with
                | Ok _ -> failtest "an unreadable restore output must not read as 'no dependencies'"
                | Error reason -> Expect.stringContains reason "unreadable" "the error says what went wrong")
        }
    ]

// ─── Binding the closure into the sealed record ──────────────────────

let closureBindingTests =
    testList "Phase 659 — the closure joins the sealed record" [

        test "binding a closure is exactly a digest in the slot that already existed" {
            let bound = DeployProvenance.none |> DeployRecords.withClosure closure

            let bySlot =
                DeployProvenance.none
                |> DeployProvenance.withUpstreamProvenanceDigest (closureDigestOf closure)

            Expect.equal
                (DeployProvenance.canonicalForm bound)
                (DeployProvenance.canonicalForm bySlot)
                "no new record shape — a provenance that binds no closure is byte-for-byte the prior shape"
        }

        test "the record's closure verifies against the closure it was bound to" {
            let provenance = DeployProvenance.none |> DeployRecords.withClosure closure

            Expect.isOk (DeployRecords.verifyClosure closure provenance) "the join question answers yes"
        }

        test "a record that bound no closure reports it, never a pass" {
            match DeployRecords.verifyClosure closure DeployProvenance.none with
            | Ok() -> failtest "there is no digest to check against"
            | Error failures -> Expect.equal failures [ DeployRecords.ClosureNotRecorded ] "reported as itself"
        }

        test "a different closure fails the join naming both digests" {
            let provenance = DeployProvenance.none |> DeployRecords.withClosure closure

            let drifted =
                DependencyClosure.create (
                    {
                        List.head closureEntries with
                            Version = "1.2.4"
                    }
                    :: List.tail closureEntries
                )

            match DeployRecords.verifyClosure drifted provenance with
            | Ok() -> failtest "a drifted closure must not verify"
            | Error failures ->
                Expect.equal
                    failures
                    [
                        DeployRecords.ClosureDigestMismatch(closureDigestOf closure, closureDigestOf drifted)
                    ]
                    "the mismatch carries what the record says and what the supplied closure digests to"
        }

        test "perturbing the bound closure breaks the seal" {
            let record =
                DeployRecord.create
                    "deploy-1"
                    "tenant-1"
                    "build-1"
                    manifest
                    (DeployProvenance.none |> DeployRecords.withClosure closure)

            let sealer = StubSealer "secret" :> IDeployRecordSealer

            let outcome =
                async {
                    let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

                    // The substitution: same record, the closure swapped
                    // for one that resolved a different version.
                    let drifted =
                        DependencyClosure.create (
                            {
                                List.head closureEntries with
                                    Version = "1.2.4"
                            }
                            :: List.tail closureEntries
                        )

                    let substituted = {
                        record with
                            Provenance = DeployProvenance.none |> DeployRecords.withClosure drifted
                    }

                    return!
                        DeployRecords.verifySeal sealer {
                            Record = substituted
                            Seal = Result.defaultValue Unchecked.defaultof<DeployRecordSeal> seal
                        }
                }
                |> Async.RunSynchronously

            match outcome with
            | Ok() -> failtest "a deploy whose build resolved a different closure must be a different record"
            | Error failures ->
                Expect.isTrue
                    (failures
                     |> List.exists (function
                         | DeployRecords.SealRejected _ -> true
                         | _ -> false))
                    "the seal refuses the substitution"
        }
    ]