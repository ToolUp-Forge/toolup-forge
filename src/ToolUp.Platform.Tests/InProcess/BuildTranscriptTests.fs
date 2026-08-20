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