// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System.Text
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
    let blobNameFor (peerId: string) = $"peers/{peerId}.json"

    let tryDecode (bytes: byte[]) =
        try
            Some(JsonRpc.deserialize<TargetPeer> (Encoding.UTF8.GetString bytes))
        with _ ->
            None

    interface IPeerRegistry with
        member _.Resolve(peerId: string) = async {
            let! result = blobs.Download(container, blobNameFor peerId)

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
            let payload = Encoding.UTF8.GetBytes(JsonRpc.serialize target)
            let! result = blobs.Upload(container, blobNameFor target.Peer.PeerId, payload)

            return
                match result with
                | Ok _ -> Ok()
                | Error message -> Error(PeerTransport message)
        }

        member _.Remove(peerId: string) = async {
            let! _ = blobs.Delete(container, blobNameFor peerId)
            return ()
        }