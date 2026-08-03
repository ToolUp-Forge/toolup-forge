// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System.Text
open ToolUp.Platform.Auth
open ToolUp.Platform.BlobStorage

// ─── Layer 4 — default peer directory ────────────────────────────────
//
// `BlobPeerRegistry` is the default `IPeerRegistry`: one JSON document
// per peer at `_platform` / `peers/{peerId}.json`, persisted through
// `IBlobStorage`. The directory survives a restart and is editable by an
// admin surface. Identity is always carried by value (GP 12 rule 1) — a
// `TargetPeer` record round-trips through the universal converter set,
// never a live connection. Stateless between calls (GP 12 rule 4): every
// method reads through to the blob store; no in-memory authority is held.

/// `IBlobStorage`-backed peer directory. Reads / writes one
/// `TargetPeer` document per peer under the reserved `_platform`
/// container at `peers/{peerId}.json`.
type BlobPeerRegistry(blobs: IBlobStorage) =
    let container = "_platform"
    let prefix = "peers/"

    /// Phase 334 — `peerId` is interpolated into a BLOB NAME, which
    /// `LocalFileStorage` resolves as a path under its root and the cloud
    /// backends accept as a key. A traversal-shaped, NUL-bearing or
    /// Windows-reserved id would therefore address a document outside the
    /// peer directory — read on `Resolve`, overwritten on `Register`,
    /// deleted on `Remove`. It is checked against the same platform
    /// `IdentitySanitiser` policy that guards the peer `iss` claim in
    /// `JwtPeerAuthProvider` and `X-Peer-Name` in
    /// `PeerBearerAuthMiddleware`, so the federated-identity boundaries
    /// share one rule set rather than three dialects.
    ///
    /// `None` is "this id may not become a blob name". Every caller
    /// converts it to that method's own miss (`Resolve` → `None`,
    /// `Register` → `Error`, `Remove` → no-op) rather than throwing —
    /// the registry's contract is that a bad id is a miss, not an
    /// exception. A well-formed id passes through unchanged, so an
    /// existing directory resolves byte-for-byte as before (GP 11).
    let blobNameFor (peerId: string) =
        match IdentitySanitiser.sanitiseScopeId peerId with
        | Result.Ok clean -> Some $"peers/{clean}.json"
        | Result.Error _ -> None

    let tryDecode (bytes: byte[]) =
        try
            Some(JsonRpc.deserialize<TargetPeer> (Encoding.UTF8.GetString bytes))
        with _ ->
            None

    interface IPeerRegistry with
        member _.Resolve(peerId: string) = async {
            match blobNameFor peerId with
            | None -> return None
            | Some name ->
                let! result = blobs.Download(container, name)

                return
                    match result with
                    | Ok bytes -> tryDecode bytes
                    | Error _ -> None
        }

        member _.List() = async {
            let! names = blobs.List(container, prefix)

            let! peers =
                names
                |> List.map (fun name -> async {
                    let! r = blobs.Download(container, name)

                    return
                        match r with
                        | Ok bytes -> tryDecode bytes
                        | Error _ -> None
                })
                |> Async.Parallel

            return peers |> Array.choose id |> List.ofArray
        }

        member _.Register(target: TargetPeer) = async {
            match blobNameFor target.Peer.PeerId with
            | None ->
                // `PeerTransport` is the channel `Register` already
                // documents for "the entry was not persisted" — the
                // registry has no validation case of its own, and adding
                // one to `PeerError` would break every exhaustive match
                // on it for a refusal that is, from the caller's side,
                // exactly "this did not get written".
                return
                    Error(
                        PeerTransport
                            "Refusing to register a peer whose id is not a well-formed identifier — it would become a blob name outside the peer directory"
                    )
            | Some name ->
                let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize target)
                let! result = blobs.Upload(container, name, payload)

                return
                    match result with
                    | Ok _ -> Ok()
                    | Error message -> Error(PeerTransport message)
        }

        member _.Remove(peerId: string) = async {
            // `Remove` is documented idempotent — removing an unknown peer
            // is a no-op — and an id that can never have been registered
            // under this registry is exactly that. Silently doing nothing
            // is the right answer, and far better than resolving the
            // traversal and deleting whatever it lands on.
            match blobNameFor peerId with
            | None -> return ()
            | Some name ->
                let! _ = blobs.Delete(container, name)
                return ()
        }