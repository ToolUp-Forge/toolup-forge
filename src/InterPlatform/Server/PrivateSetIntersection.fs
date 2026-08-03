// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Collections.Generic
open System.Security.Cryptography

// ─── Layer 5 — two-party private set intersection ────────────────────
//
// The Diffie-Hellman PSI protocol over `ICommutativeCipher`. Two
// deployments learn which identifiers they BOTH hold without either
// sending a raw identifier and without revealing a single non-matching
// element:
//
//   1. The initiator maps each of its elements to a group element and
//      applies its own key `ka`, producing `A₁`, and sends it.
//   2. The responder applies its key `kb` to every token of `A₁`, KEEPING
//      THE REQUEST ORDER, producing `A₂`; it blinds its own elements under
//      `kb`, SHUFFLES them, producing `B₁`; and returns both.
//   3. The initiator applies `ka` to `B₁`, producing `B₂`.
//   4. `A₂ ∩ B₂` — commutativity makes `[ka][kb]H(x)` equal `[kb][ka]H(x)`
//      exactly when the pre-images match, so equal doubly-encrypted tokens
//      are exactly the shared elements. The initiator maps a match back to
//      its own pre-image through `A₂`'s preserved order.
//
// The two order rules are the protocol, not an implementation detail.
// `A₂` must preserve the request order or the initiator cannot recover
// which of ITS elements matched. `B₁` must be shuffled or its position
// leaks the responder's element ordering, which for a sorted or
// insertion-ordered source is a partial ordering oracle over a set the
// initiator is not entitled to.
//
// **Output is one-sided by construction.** The initiator learns the
// intersection; the responder learns only the initiator's set SIZE. A
// deployment that wants both sides to learn it runs the protocol twice
// with the roles swapped — a deliberate choice, not an oversight: which
// party is entitled to the answer is exactly the kind of domain judgement
// this substrate does not make (GP 1).
//
// **What is NOT here** (GP 1, matching `ICleanRoomBroker` and
// `IPeerFanout`): no identifier semantics, no canonicalisation, no
// normalisation of case / whitespace / encoding, no opinion on what a
// match means. Elements are opaque `byte[]`; a caller that wants
// "alice@example.com" and "Alice@Example.COM" to match lower-cases them
// itself, because whether they SHOULD match is a domain question with
// different answers per data set and per regulator. No aggregation, no
// cardinality-only mode, no differential-privacy budget over the answer —
// a deployment needing those composes them over the returned set.
//
// **Set-size leakage is inherent to the construction**, not a defect
// introduced here: each side sees the other's token count. A deployment
// that treats set size as sensitive pads its input to a bucket boundary
// before calling — a caller-side decision, so it stays caller-side.
//
// Six portability rules (GP 12): every wire type is a plain immutable
// record of primitives (rule 1); the exchange is a caller-supplied
// closure, so the protocol holds no transport (rule 2 at the boundary the
// caller owns); failure is a typed `PsiError`, never a throw (rule 3); the
// implementation holds no state between calls (rule 4).

/// Why a PSI run could not complete. Failure as data (GP 12 rule 3).
type PsiError =
    /// A cipher operation failed — typically a counterparty token that is
    /// malformed, from a different backend, or outside the prime-order
    /// subgroup.
    | PsiCipher of error: CommutativeCipherError
    /// The exchange itself failed on the wire.
    | PsiTransport of error: PeerError
    /// The counterparty answered, but the answer does not satisfy the
    /// protocol: wrong session, wrong token count, or a malformed
    /// encoding.
    | PsiProtocol of message: string

/// The initiator's half of the exchange: its singly-blinded tokens, in
/// its own element order. Base64 so the payload is a plain JSON wire
/// shape a non-.NET counterparty can produce (the same posture the
/// JSON-RPC peer wire takes).
type PsiRequest = {
    /// Caller-assigned id correlating the request with its answer. Echoed
    /// back and checked — a mismatched session is a protocol error, not a
    /// silently-accepted answer to a different question.
    SessionId: string
    /// `A₁` — the initiator's elements under the initiator's key.
    Blinded: string list
}

/// The responder's half of the exchange.
type PsiResponse = {
    /// Echo of the request's `SessionId`.
    SessionId: string
    /// `B₁` — the responder's own elements under the responder's key,
    /// SHUFFLED. The shuffle is required (see the file header).
    PartnerBlinded: string list
    /// `A₂` — the request's tokens under the responder's key, in the
    /// REQUEST'S ORDER. The order is required (see the file header).
    Doubled: string list
}

/// The initiator's result. `MatchedTokens` are the opaque doubly-encrypted
/// tokens the two sides agreed on; `MatchedElements` are the initiator's
/// OWN pre-images for those tokens, in input order — the pre-image mapping
/// that makes the answer usable without the substrate knowing what an
/// element means.
type PsiOutcome = {
    MatchedTokens: string list
    MatchedElements: byte[] list
}

/// The canonical peer contract for the exchange. An ordinary record of
/// functions, the shape `JsonRpcPeerHost.contract<'TApi>` hosts and
/// `JsonRpcPeerClient.create<'TApi>` proxies — so the round trip runs over
/// the Phase 18 `IPeerClient` transport with no PSI-specific wire code.
/// Shipped as the interoperable default; a deployment with its own
/// contract passes any closure of the same shape instead.
type PsiApi = {
    Exchange: PsiRequest -> Async<PsiResponse>
}

/// The two-party PSI protocol. A seam rather than a module so a deployment
/// can substitute a different construction — a multi-round protocol over
/// `IRoundOrchestrator`, an OPRF with a different blinding discipline —
/// without changing its call sites.
type IPrivateSetIntersection =
    /// Run the initiator's side. `key` is this deployment's secret cipher
    /// key; `elements` are its opaque inputs; `exchange` is the wire — a
    /// closure the caller typically fills with a `JsonRpcPeerClient`
    /// proxy over `PsiApi`, which is what keeps the protocol free of any
    /// contract or transport knowledge (the discipline
    /// `IRoundOrchestrator.RunRounds` follows for the same reason).
    abstract Intersect:
        key: byte[] * elements: byte[] list * exchange: (PsiRequest -> Async<Result<PsiResponse, PeerError>>) ->
            Async<Result<PsiOutcome, PsiError>>

    /// Run the responder's side over a received request. Synchronous:
    /// pure CPU over already-materialised bytes with nothing to await, the
    /// same documented shape `ICleanRoomBroker.Enforce` takes.
    abstract Respond: key: byte[] * elements: byte[] list * request: PsiRequest -> Result<PsiResponse, PsiError>

/// Default two-party PSI over an `ICommutativeCipher`. Holds no state
/// between calls (GP 12 rule 4).
///
/// The shuffle arrives as an EXPLICIT OVERLOAD rather than an optional
/// argument: F# folds `?shuffle` into one widened constructor, which
/// erases the narrower signature from the emitted public surface — source
/// compatible, but a binary change the public-API baseline correctly reads
/// as a removal. Secondary constructors keep every shape additive (GP 11),
/// the discipline `BlobPeerJobResultStore` adopted at Phase 316 and
/// `DefaultRoundOrchestrator` at Phase 483.
type DefaultPrivateSetIntersection(cipher: ICommutativeCipher, shuffle: string list -> string list) =

    /// Cryptographic Fisher-Yates. `Random` would do for hiding an
    /// ordering from an honest-but-curious counterparty, but a predictable
    /// permutation is a predictable ordering, and the whole point of the
    /// shuffle is that the counterparty cannot reconstruct the source
    /// order.
    static let secureShuffle (items: string list) =
        let array = List.toArray items

        for i in array.Length - 1 .. -1 .. 1 do
            let j = RandomNumberGenerator.GetInt32(i + 1)
            let swap = array[i]
            array[i] <- array[j]
            array[j] <- swap

        List.ofArray array

    /// Bucket width for the match index. The index only ever holds a
    /// truncated prefix of a token that is already public on the wire; the
    /// decision that actually determines membership is the full-length
    /// constant-time compare below.
    static let bucketBytes = 8

    static let bucketOf (token: byte[]) =
        Convert.ToBase64String(token, 0, min bucketBytes token.Length)

    let encode (bytes: byte[]) = Convert.ToBase64String bytes

    let decode (token: string) =
        try
            Ok(Convert.FromBase64String token)
        with _ ->
            Error(PsiProtocol "a token in the counterparty's answer is not valid base64")

    /// Decode a whole list, failing the run on the first malformed token.
    /// Skipping one instead would report the intersection as SMALLER than
    /// it is — a wrong answer wearing a success, which is the one outcome
    /// a privacy primitive must never produce quietly.
    let decodeAll (tokens: string list) =
        let rec walk acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | token :: rest ->
                match decode token with
                | Error e -> Error e
                | Ok raw -> walk (raw :: acc) rest

        walk [] tokens

    /// Map each element onto the group and blind it under `key`, order
    /// preserved.
    let blindAll (key: byte[]) (elements: byte[] list) =
        let rec walk acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | element :: rest ->
                match cipher.Apply key (cipher.HashToPoint element) with
                | Error e -> Error(PsiCipher e)
                | Ok blinded -> walk (encode blinded :: acc) rest

        walk [] elements

    /// Apply `key` to every already-blinded token, order preserved.
    let applyAll (key: byte[]) (tokens: string list) =
        let rec walk acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | token :: rest ->
                match decode token with
                | Error e -> Error e
                | Ok raw ->
                    match cipher.Apply key raw with
                    | Error e -> Error(PsiCipher e)
                    | Ok doubled -> walk (encode doubled :: acc) rest

        walk [] tokens

    /// The cheapest shape: the cryptographic shuffle.
    new(cipher: ICommutativeCipher) = DefaultPrivateSetIntersection(cipher, secureShuffle)

    /// The default permutation, exposed so a caller supplying its own
    /// shuffle can fall back to it and so the conformance pack can pin a
    /// deterministic one without reaching into a private binding.
    static member SecureShuffle = secureShuffle

    interface IPrivateSetIntersection with
        member _.Intersect(key, elements, exchange) = async {
            match blindAll key elements with
            | Error e -> return Error e
            | Ok mine ->
                let request = {
                    SessionId = Guid.NewGuid().ToString "N"
                    Blinded = mine
                }

                let! answer = exchange request

                match answer with
                | Error transport -> return Error(PsiTransport transport)
                | Ok response ->
                    if response.SessionId <> request.SessionId then
                        return
                            Error(
                                PsiProtocol
                                    "the counterparty answered a different session — the response cannot be matched to this request"
                            )
                    elif List.length response.Doubled <> List.length mine then
                        return
                            Error(
                                PsiProtocol
                                    $"the counterparty returned {List.length response.Doubled} doubly-encrypted token(s) for {List.length mine} sent — the request order cannot be recovered"
                            )
                    else
                        match applyAll key response.PartnerBlinded with
                        | Error e -> return Error e
                        | Ok theirs ->
                            match decodeAll theirs, decodeAll response.Doubled with
                            | Error e, _
                            | _, Error e -> return Error e
                            | Ok theirsRaw, Ok mineRaw ->
                                // Index the counterparty's doubled tokens
                                // by a truncated prefix, then confirm the
                                // full token in constant time. The index
                                // is a lookup over wire-public bytes; the
                                // confirmation is the comparison whose
                                // OUTCOME is the private answer, and that
                                // is the one that must not be
                                // timing-distinguishable.
                                let index = Dictionary<string, ResizeArray<byte[]>>()

                                for raw in theirsRaw do
                                    let bucket = bucketOf raw

                                    match index.TryGetValue bucket with
                                    | true, existing -> existing.Add raw
                                    | false, _ -> index[bucket] <- ResizeArray [ raw ]

                                let matches =
                                    List.zip3 elements response.Doubled mineRaw
                                    |> List.choose (fun (element, token, raw) ->
                                        match index.TryGetValue(bucketOf raw) with
                                        | false, _ -> None
                                        | true, candidates ->
                                            if
                                                candidates
                                                |> Seq.exists (fun candidate ->
                                                    CommutativeCipher.bytesEqual candidate raw)
                                            then
                                                Some(element, token)
                                            else
                                                None)

                                return
                                    Ok {
                                        MatchedTokens = matches |> List.map snd
                                        MatchedElements = matches |> List.map fst
                                    }
        }

        member _.Respond(key, elements, request) =
            match applyAll key request.Blinded with
            | Error e -> Error e
            | Ok doubled ->
                match blindAll key elements with
                | Error e -> Error e
                | Ok mine ->
                    Ok {
                        SessionId = request.SessionId
                        PartnerBlinded = shuffle mine
                        Doubled = doubled
                    }

[<RequireQualifiedAccess>]
module PrivateSetIntersection =
    /// The default protocol over `cipher`, with the cryptographic shuffle.
    let create (cipher: ICommutativeCipher) : IPrivateSetIntersection =
        DefaultPrivateSetIntersection(cipher) :> IPrivateSetIntersection

    /// The default protocol with a caller-supplied permutation — for a
    /// deployment that shuffles through its own entropy source, and for
    /// tests that need a deterministic transcript.
    let createWith (cipher: ICommutativeCipher) (shuffle: string list -> string list) : IPrivateSetIntersection =
        DefaultPrivateSetIntersection(cipher, shuffle) :> IPrivateSetIntersection

    /// Adapt a `PsiApi` proxy — typically `JsonRpcPeerClient.create<PsiApi>`
    /// over an `IPeerClient` — into the exchange closure `Intersect`
    /// drives. A transport throw becomes a `PeerTransport`, so the
    /// protocol sees failure as data either way.
    let overContract (api: PsiApi) : PsiRequest -> Async<Result<PsiResponse, PeerError>> =
        fun request -> async {
            try
                let! response = api.Exchange request
                return Ok response
            with ex ->
                return Error(PeerTransport ex.Message)
        }