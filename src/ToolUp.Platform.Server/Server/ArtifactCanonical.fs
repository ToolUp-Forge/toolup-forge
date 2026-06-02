// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ArtifactCanonical

open System
open System.IO
open System.Text

// Phase 30a — canonical byte encoding of `ArtifactManifest` for signing
// + verification. Length-prefixed concatenation: every string field is
// emitted as `[4-byte big-endian UTF-8 byte count][UTF-8 bytes]`; lists
// emit `[4-byte big-endian item count][items...]`.
//
// Deterministic — the same `ArtifactManifest` always produces the same
// byte sequence regardless of host platform or list-ordering quirks.
// `Dependencies` is encoded in declared order (the hub is the source of
// truth for ordering); the verifier re-encodes the same list in the same
// order from the wire manifest.
//
// The signature is the Ed25519 output over `canonical(manifest) ||
// payload`. `Signature` is NOT part of the canonical form — `SignedArtifact`
// is the wrapper that carries it separately.

let private writeLengthPrefixedBytes (writer: BinaryWriter) (bytes: byte[]) =
    let length = bytes.Length
    // Big-endian 4-byte length prefix — portable across .NET hosts.
    writer.Write(byte ((length >>> 24) &&& 0xFF))
    writer.Write(byte ((length >>> 16) &&& 0xFF))
    writer.Write(byte ((length >>> 8) &&& 0xFF))
    writer.Write(byte (length &&& 0xFF))
    writer.Write(bytes)

let private writeString (writer: BinaryWriter) (s: string) =
    writeLengthPrefixedBytes writer (Encoding.UTF8.GetBytes(s))

let private writeSdkVersionRange (writer: BinaryWriter) (range: SdkVersionRange) =
    writeString writer range.MinInclusive
    writeString writer range.MaxExclusive

let private writeDependency (writer: BinaryWriter) (dep: ArtifactDependency) =
    writeString writer dep.PackageId
    writeSdkVersionRange writer dep.VersionRange

/// Canonical bytes of a manifest. Signed by `IArtifactSigner.Sign` and
/// re-derived by `IArtifactVerifier.Verify` before Ed25519 verification.
let encodeManifest (manifest: ArtifactManifest) : byte[] =
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream)
    writeString writer manifest.ModuleId
    writeString writer manifest.Version
    writeSdkVersionRange writer manifest.SdkVersionRange
    writeString writer (ContentHash.value manifest.CodeHash)
    writeString writer (ContentHash.value manifest.SchemaHash)
    let deps = manifest.Dependencies
    let depCount = deps.Length
    writer.Write(byte ((depCount >>> 24) &&& 0xFF))
    writer.Write(byte ((depCount >>> 16) &&& 0xFF))
    writer.Write(byte ((depCount >>> 8) &&& 0xFF))
    writer.Write(byte (depCount &&& 0xFF))

    for dep in deps do
        writeDependency writer dep

    writeString writer (PublisherKeyId.value manifest.PublisherKeyId)
    writer.Flush()
    stream.ToArray()

/// Canonical bytes the signature covers: `canonical(manifest) ||
/// payload`. Used by both signer and verifier so the contract stays
/// in one place.
let messageToSign (manifest: ArtifactManifest) (payload: byte[]) : byte[] =
    let manifestBytes = encodeManifest manifest
    let result = Array.zeroCreate (manifestBytes.Length + payload.Length)
    Buffer.BlockCopy(manifestBytes, 0, result, 0, manifestBytes.Length)
    Buffer.BlockCopy(payload, 0, result, manifestBytes.Length, payload.Length)
    result