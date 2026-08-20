// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DeployRecords

open System
open System.IO
open System.Security.Cryptography
open System.Text
open ToolUp.Platform

// ─── Deploy-record digests + verification ────────────────────────────
//
// The server-side half of the build-transcript substrate: the hash over
// the canonical forms defined in `ToolUp.Platform.Core`, and the
// verification walk an external checker runs.
//
// **Why the split.** Canonical forms are pure string building and live
// in Core so producer and verifier can never disagree about what was
// covered — including a Fable-compiled surface that wants to render a
// transcript. Hashing needs `System.Security.Cryptography`, which does
// not compile under Fable, so the digest lives here. The rule is the
// repo's standing one for crypto-addressed types: shared shape in Core,
// crypto in Server.
//
// **What verification answers.** Exactly three questions, in this
// order, and no others:
//
//   1. *Is this record sealed?* — the seal covers the record's
//      canonical bytes, checked by the deployment's own sealer.
//   2. *Do its artifact digests match these files?* — every recorded
//      artifact is present and hashes to the digest recorded for it.
//      A mismatch names the file.
//   3. *Does its transcript digest match this transcript?* — the digest
//      recorded in the record is the digest of the transcript supplied.
//
// It answers no question about the OPAQUE upstream-provenance slot
// beyond "it was not edited after sealing", because the substrate does
// not know what fills that slot and will not pretend to.
//
// Every check accumulates: a call reports every failure it found, not
// the first. An operator holding a tampered deployment wants the whole
// list, and a checker that stops at the first mismatch invites a
// second run to discover a second one.

// ─── Digests ─────────────────────────────────────────────────────────

/// Lowercase-hex SHA-256 of arbitrary bytes.
let digestBytes (bytes: byte[]) : string =
    Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()

/// Lowercase-hex SHA-256 of a file's contents, streamed rather than
/// buffered — a deployed artifact can be arbitrarily large, and a
/// verifier that loads one into memory to hash it is a verifier that
/// falls over on the deployment that most needs checking.
let digestFile (path: string) : string =
    use stream = File.OpenRead path
    Convert.ToHexString(SHA256.HashData stream).ToLowerInvariant()

/// Lowercase-hex SHA-256 of a canonical form's UTF-8 bytes.
let digestCanonicalForm (canonicalForm: string) : string =
    canonicalForm |> Encoding.UTF8.GetBytes |> digestBytes

/// The content address of a build transcript: the digest of its
/// canonical form.
///
/// Deterministic both ways, and worth stating explicitly because it is
/// the property everything else rests on. Same inputs ⇒ same digest:
/// the canonical form sorts and de-duplicates the dependency closure
/// and length-frames every field, so nothing about the producing
/// machine reaches the bytes. Different inputs ⇒ different digest: the
/// framing is injective over field sequences, so no two distinct
/// transcripts canonicalise to the same text.
let transcriptDigest (transcript: BuildTranscript) : string =
    transcript |> BuildTranscript.canonicalForm |> digestCanonicalForm

/// The exact bytes a deploy record's seal is taken over.
let canonicalBytes (record: DeployRecord) : byte[] =
    record |> DeployRecord.canonicalForm |> Encoding.UTF8.GetBytes

// ─── Recording artifacts ─────────────────────────────────────────────

/// Locate a deployment-relative artifact path on disk. `None` means the
/// artifact is absent — which verification reports, never absorbs.
type ArtifactLocator = string -> string option

/// The obvious locator: artifact paths are relative to one directory.
///
/// Recorded paths always use `/` as the separator (they are recorded
/// once and checked on whatever host runs the checker), so the
/// separator is translated here rather than baked into the record.
let locateUnder (root: string) : ArtifactLocator =
    fun relativePath ->
        let native = relativePath.Replace('/', Path.DirectorySeparatorChar)
        let full = Path.Combine(root, native)

        if File.Exists full then Some full else None

/// Record every file under `root` as a `DeployArtifactDigest`, with
/// deployment-relative, `/`-separated paths.
///
/// Enumeration order is deliberately not relied on: the canonical form
/// sorts, so a file system that walks in a different order produces the
/// same record.
let artifactsUnder (root: string) : DeployArtifactDigest list =
    let full = Path.GetFullPath root

    Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)
    |> Seq.map (fun file -> {
        Path = Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/')
        ContentDigest = digestFile file
    })
    |> List.ofSeq

// ─── Verification ────────────────────────────────────────────────────

/// One way a sealed deploy record failed verification.
type DeployRecordVerificationFailure =
    /// The seal was minted under a scheme the supplied sealer does not
    /// own. Reported rather than attempted: a verifier that guesses at a
    /// token it does not understand can only guess wrong.
    | SealSchemeMismatch of expected: string * found: string
    /// The seal does not cover the record's canonical bytes. Carries
    /// the sealer's own reason — the substrate does not interpret it.
    | SealRejected of reason: string
    /// A recorded artifact is not present where the locator looked.
    | ArtifactMissing of path: string
    /// A recorded artifact is present but its bytes hash to something
    /// else. **The file is named** — that is the finding an operator
    /// acts on.
    | ArtifactDigestMismatch of path: string * recorded: string * observed: string
    /// A transcript was supplied to check against, but the record
    /// recorded no transcript digest to check it with.
    | TranscriptNotRecorded
    /// The record's transcript digest is not the digest of the
    /// transcript supplied.
    | TranscriptDigestMismatch of recorded: string * computed: string

[<RequireQualifiedAccess>]
module DeployRecordVerificationFailure =
    let describe =
        function
        | SealSchemeMismatch(expected, found) ->
            $"seal scheme mismatch: verifier owns '{expected}', seal was minted as '{found}'"
        | SealRejected reason -> $"seal does not cover this record: {reason}"
        | ArtifactMissing path -> $"recorded artifact is missing: {path}"
        | ArtifactDigestMismatch(path, recorded, observed) ->
            $"artifact '{path}' does not match its recorded digest: recorded {recorded}, observed {observed}"
        | TranscriptNotRecorded -> "a transcript was supplied but the record carries no transcript digest"
        | TranscriptDigestMismatch(recorded, computed) ->
            $"transcript digest mismatch: record carries {recorded}, supplied transcript digests to {computed}"

/// Check every recorded artifact against the bytes on disk.
///
/// Reports every mismatch and every absence, each naming its file. A
/// provenance recording no artifacts passes trivially — it claims
/// nothing about any file, and a check that invented a failure there
/// would be measuring the absence of a claim rather than a broken one.
let verifyArtifacts
    (locate: ArtifactLocator)
    (provenance: DeployProvenance)
    : Result<unit, DeployRecordVerificationFailure list> =
    let failures =
        provenance
        |> DeployProvenance.coerce
        |> DeployProvenance.canonicalArtifacts
        |> List.choose (fun artifact ->
            match locate artifact.Path with
            | None -> Some(ArtifactMissing artifact.Path)
            | Some full ->
                let observed = digestFile full

                if String.Equals(observed, artifact.ContentDigest, StringComparison.OrdinalIgnoreCase) then
                    None
                else
                    Some(ArtifactDigestMismatch(artifact.Path, artifact.ContentDigest, observed)))

    if List.isEmpty failures then Ok() else Error failures

/// Check the record's transcript digest against a transcript the
/// checker holds.
let verifyTranscript
    (transcript: BuildTranscript)
    (provenance: DeployProvenance)
    : Result<unit, DeployRecordVerificationFailure list> =
    let computed = transcriptDigest transcript

    match (DeployProvenance.coerce provenance).TranscriptDigest with
    | None -> Error [ TranscriptNotRecorded ]
    | Some recorded when String.Equals(recorded, computed, StringComparison.OrdinalIgnoreCase) -> Ok()
    | Some recorded -> Error [ TranscriptDigestMismatch(recorded, computed) ]

/// Check the seal covers the record's canonical bytes.
let verifySeal
    (sealer: IDeployRecordSealer)
    (signedRecord: SealedDeployRecord)
    : Async<Result<unit, DeployRecordVerificationFailure list>> =
    async {
        let scheme = sealer.Scheme()

        if signedRecord.Seal.Scheme <> scheme then
            return Error [ SealSchemeMismatch(scheme, signedRecord.Seal.Scheme) ]
        else
            let bytes = canonicalBytes (DeployRecord.coerce signedRecord.Record)

            match! sealer.VerifySeal(bytes, signedRecord.Seal) with
            | Ok() -> return Ok()
            | Error reason -> return Error [ SealRejected reason ]
    }

/// The verification function an external checker calls.
///
/// Answers the three questions in the module header — *this record is
/// sealed*, *its artifact digests match these files*, *its transcript
/// digest matches this transcript* — and accumulates every failure
/// found rather than stopping at the first.
///
/// `transcript` is optional because a checker legitimately may not hold
/// the transcript: verifying the seal and the deployed files is useful
/// on its own. Supplying `None` SKIPS the transcript question; it does
/// not answer it in the affirmative, and a caller reporting "verified"
/// should say which questions it asked.
let verify
    (sealer: IDeployRecordSealer)
    (locate: ArtifactLocator)
    (transcript: BuildTranscript option)
    (signedRecord: SealedDeployRecord)
    : Async<Result<unit, DeployRecordVerificationFailure list>> =
    async {
        let record = DeployRecord.coerce signedRecord.Record

        let! sealResult = verifySeal sealer { signedRecord with Record = record }

        let collect result =
            match result with
            | Ok() -> []
            | Error failures -> failures

        let transcriptFailures =
            match transcript with
            | None -> []
            | Some t -> collect (verifyTranscript t record.Provenance)

        let failures =
            collect sealResult
            @ collect (verifyArtifacts locate record.Provenance)
            @ transcriptFailures

        return if List.isEmpty failures then Ok() else Error failures
    }