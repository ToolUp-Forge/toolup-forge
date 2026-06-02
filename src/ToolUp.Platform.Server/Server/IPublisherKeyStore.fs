// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Phase 30a — edge-side trust anchor for module artefact verification.
/// Holds Ed25519 public-key bytes keyed by `PublisherKeyId`; populated
/// out-of-band by the deployment operator (CLI / admin endpoint /
/// configuration sync).
///
/// **Default implementation** — `BlobBackedPublisherKeyStore` (writes to
/// `_platform/trusted-publishers/{keyId}.pub` via `IBlobStorage`).
/// Cluster-portable: the blob storage backend is whatever the deployment
/// composes (filesystem / Azure / S3 / GCS), and every node reads the
/// same trust set on each call.
///
/// **Empty-by-default (GP 13).** A fresh deployment starts with zero
/// trusted publishers — `TryGetPublicKey` returns `None` for every key
/// id, so `IArtifactVerifier.Verify` refuses everything until the
/// operator explicitly adds keys.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — `PublisherKeyId` is a string newtype; keys
///    are returned as `byte[]`.
/// 2. *Async at every boundary* — every member returns `Async<_>`.
/// 3. *Retry as data* — implementation failures throw; callers wrap as
///    they see fit.
/// 4. *Stateless handlers between invocations* — each call resolves
///    against the backing store; no in-memory cache survives between
///    calls. Implementations may add a TTL cache in front, but the
///    contract is "next call re-reads".
/// 5. *No cross-shard ordering* — key reads are independent.
/// 6. *Precision at the lower bound* — no timing / clock contract.
type IPublisherKeyStore =
    /// Add (or replace) the trusted public key for `keyId`. `publicKey`
    /// is the raw 32-byte Ed25519 public key. Implementations persist
    /// durably before returning.
    abstract AddTrustedKey: keyId: PublisherKeyId * publicKey: byte[] -> Async<unit>

    /// Remove the trusted public key for `keyId`. Idempotent — removing
    /// an unknown id returns successfully. Subsequent
    /// `IArtifactVerifier.Verify` calls naming the removed key id will
    /// refuse with `"untrusted publisher"`.
    abstract RemoveTrustedKey: keyId: PublisherKeyId -> Async<unit>

    /// Resolve the trusted public key for `keyId`. Returns `None` when
    /// the key id is not in the trusted set — the verifier treats this
    /// as `"untrusted publisher"`.
    abstract TryGetPublicKey: keyId: PublisherKeyId -> Async<byte[] option>

    /// List every trusted publisher key id. Used by the admin UI to
    /// display the current trust set; not used in the verification hot
    /// path.
    abstract ListTrustedKeyIds: unit -> Async<PublisherKeyId list>