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
// different answers per data set and per regulator. No payload semantics
// either (Phase 479 below): an aggregated value is opaque bytes the
// caller encodes and decodes, and no differential-privacy budget is taken
// over the answer.
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
//
// ─── Phase 479 — release modes, and what each one leaks ──────────────
//
// The protocol above releases the intersection SET. Many clean-room
// questions want strictly less ("how big is the overlap?") or something
// beside it ("total value across the overlap") and would be answered
// wrongly by computing the set and then discarding it — because the party
// that computes the set has already learned it. `PsiMode` therefore
// selects a distinct protocol PATH, not a post-filter, and the three
// paths have three different leakage profiles. Stated plainly, because a
// privacy primitive whose leakage is implied rather than written down is
// one nobody can review:
//
//   * `Members` — 18f, unchanged, byte for byte. `A₂` echoes the request
//     order, so the initiator recovers WHICH of its elements matched. The
//     responder learns the initiator's set size.
//
//   * `CardinalityOnly` — the responder SHUFFLES `A₂` as well as `B₁`, so
//     the initiator can count equal tokens but cannot map a match back to
//     one of its own elements; and the initiator shuffles `A₁`, which it
//     could not do in `Members` (it needed the echo) and which hides its
//     own element ordering from the responder for the same reason `B₁`'s
//     shuffle hides the responder's. Released: one integer. The responder
//     still learns the initiator's set size.
//
//   * `AggregatePayload` — as `CardinalityOnly`, plus per-element opaque
//     payload ciphertexts riding positionally alongside `B₁` under the
//     SAME permutation, combined over the intersection through an
//     injected mechanism. Released: the intersection size and one opaque
//     aggregate. See `IPsiAggregator` for the leak this mode cannot
//     remove and the default-deny that stops a deployment walking into
//     it unreviewed.
//
// **Who enforces what, and why that lands where it does.** The shuffles
// that bound `CardinalityOnly` are applied by the RESPONDER and protect
// the RESPONDER's data from the initiator — so the party the control
// protects is the party that applies it, which is the only arrangement
// that survives a dishonest counterparty. A responder that "cheats" by
// preserving `A₂`'s order gives away nothing of its own; it merely tells
// the initiator more about the initiator's own set, which is why nothing
// here tries to make the shuffle verifiable by the far end. Conversely
// the initiator cannot be prevented from keeping what it legitimately
// receives, which is exactly why the modes differ in what is SENT rather
// than in what is discarded on arrival.
//
// **What no protocol path fixes: differencing across runs.** An initiator
// that runs `CardinalityOnly` with `S` and again with `S ∪ {x}` reads
// `x`'s membership off the difference of two counts, and a near-singleton
// input turns an "aggregate" into a point lookup. That is a property of
// the FUNCTIONALITY, not of this implementation, so it is answered by
// policy, in two places:
//
//   * `PsiResponderPolicy.MinInputCohort` — a floor on the initiator's
//     set size, checked by the responder against `|A₁|`, which it can see
//     and which no initiator can forge downward. This is what closes the
//     singleton probe, and it closes it responder-side.
//   * `PsiReleaseGate` — the existing `ICleanRoomBroker` / `PrivacyGate`
//     floor (Phases 18b / 311) applied to the released count or
//     aggregate, so a sub-k overlap is withheld with an audit reason
//     rather than released.
//
// The CUMULATIVE half — bounding how many differencing questions a
// counterparty may ask at all — is `IPrivacyBudgetLedger` (Phase 190),
// and it is deliberately NOT wired here. That ledger's two-phase
// reserve/settle hangs off receiver-side dispatch (`CleanRoomGate`), and
// a PSI run's release happens initiator-side where there is no inbound
// call to meter. The composition point is a peer contract that hosts
// `PsiModeApi` behind a gated clean-room template: the ledger meters the
// EXCHANGE the responder serves, and this file's gate meters the ANSWER
// the initiator releases. Two halves, two seams, neither reimplemented
// here.

/// Why an aggregate combine could not be performed. Failure as data
/// (GP 12 rule 3), the same posture `CommutativeCipherError` takes for
/// the cipher seam.
type PsiAggregateError =
    /// The two ciphertexts are not the same width for this mechanism.
    | AggregateWidthMismatch
    /// A ciphertext is not a well-formed encoding for this mechanism.
    | AggregateMalformed

/// Which answer a run releases. Negotiated rather than declared: the
/// initiator names the mode it requires, the responder either serves that
/// mode or refuses the run, and the initiator refuses an answer echoing a
/// different mode — so there is no silent downgrade to a more-revealing
/// path (nor a silent upgrade to one the responder never agreed to).
///
/// Each case's leakage profile is in the file header. They are not
/// orderable: `AggregatePayload` reveals the count that `CardinalityOnly`
/// reveals, but `Members` reveals membership that neither of the others
/// does.
type PsiMode =
    /// Phase 18f: the intersection SET, recovered against the initiator's
    /// own pre-images.
    | Members
    /// The intersection SIZE and nothing else.
    | CardinalityOnly
    /// The intersection size plus one opaque aggregate over the matched
    /// elements' payloads.
    | AggregatePayload

/// Why a PSI run could not complete. Failure as data (GP 12 rule 3).
type PsiError =
    /// A cipher operation failed — typically a counterparty token that is
    /// malformed, from a different backend, or outside the prime-order
    /// subgroup.
    | PsiCipher of error: CommutativeCipherError
    /// The exchange itself failed on the wire.
    | PsiTransport of error: PeerError
    /// The counterparty answered, but the answer does not satisfy the
    /// protocol: wrong session, wrong token count, wrong mode, or a
    /// malformed encoding.
    | PsiProtocol of message: string
    /// The aggregate mechanism could not combine two payload ciphertexts.
    | PsiAggregation of error: PsiAggregateError
    /// The run was refused LOCALLY, before anything was sent: the
    /// composition cannot serve the requested mode (no aggregate
    /// mechanism bound, or one whose payload space the aggregating party
    /// can read without the deployment having said so). Distinct from
    /// `PsiProtocol` because no counterparty was involved and nothing
    /// reached the wire.
    | PsiConfiguration of message: string

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

// ── Phase 479 — the mode protocol ────────────────────────────────────
//
// A parallel wire shape rather than extra fields on `PsiRequest` /
// `PsiResponse`. That is a deliberate GP 11 reading applied at the WIRE
// rather than only at the API: a Phase 18f counterparty and a Phase 479
// counterparty exchange byte-identical `Members` traffic, which matters
// more in a federation protocol than anywhere else, because the two ends
// version independently and neither can make the other upgrade. The
// records also carry fields — a negotiated mode, aligned payloads — that
// have no meaning on the `Members` path and would be dead weight there.

/// One of the responder's elements together with the opaque payload that
/// travels with it in `AggregatePayload` mode. `Payload` is uninterpreted
/// by the substrate — the caller encodes it and the caller decodes the
/// aggregate (GP 1, the same line the element bytes already draw).
type PsiPayloadElement = { Element: byte[]; Payload: byte[] }

/// The initiator's half of a mode exchange.
type PsiModeRequest = {
    /// Caller-assigned id correlating the request with its answer.
    SessionId: string
    /// The mode this run REQUIRES. The responder serves it or refuses;
    /// there is no negotiation down.
    Mode: PsiMode
    /// `A₁` — the initiator's elements under the initiator's key. In the
    /// initiator's element order for `Members` (the echo has to be
    /// mappable back); SHUFFLED for the other modes, where it need not be
    /// and therefore should not be.
    Blinded: string list
}

/// The responder's half of a mode exchange.
type PsiModeResponse = {
    /// Echo of the request's `SessionId`.
    SessionId: string
    /// Echo of the request's `Mode`. A mismatch fails the run — an answer
    /// computed under a different mode is an answer to a different
    /// question, whichever direction it moved.
    Mode: PsiMode
    /// `B₁` — the responder's own elements under the responder's key,
    /// SHUFFLED in every mode.
    PartnerBlinded: string list
    /// `A₂` — the request's tokens under the responder's key. In the
    /// REQUEST'S ORDER for `Members`; SHUFFLED for the other modes, which
    /// is precisely what reduces the initiator's answer from membership
    /// to a count.
    Doubled: string list
    /// Opaque payload ciphertexts, positionally aligned with
    /// `PartnerBlinded` under the SAME permutation. Empty except in
    /// `AggregatePayload` mode.
    Payloads: string list
}

/// The canonical peer contract for a mode exchange — the `PsiApi`
/// equivalent, hosted and proxied the same way.
type PsiModeApi = {
    ExchangeMode: PsiModeRequest -> Async<PsiModeResponse>
}

/// The pluggable aggregation mechanism (479.B). Payload ciphertexts are
/// opaque `byte[]`; the substrate only ever combines them, never reads
/// one, so the payload's meaning stays entirely the caller's.
///
/// **`Combine` must be associative AND commutative.** The intersection is
/// a SET arriving through a shuffle, so the substrate combines it in an
/// order nobody controls; a mechanism sensitive to that order would
/// return a different aggregate per run for the same answer. Additive
/// homomorphic ciphertext addition satisfies both, which is the mechanism
/// this seam is shaped for.
type IPsiAggregator =
    /// The aggregate of an empty intersection.
    abstract Zero: byte[]

    /// Combine two opaque payload ciphertexts.
    abstract Combine: left: byte[] -> right: byte[] -> Result<byte[], PsiAggregateError>

    /// Whether the payload space is opaque to the party performing the
    /// combine — `true` for a real additive-homomorphic context whose
    /// decryption key the aggregating party does not hold, `false` for a
    /// mechanism that operates on values it could equally well read.
    ///
    /// **This is the mode's central caveat, and it is declared rather
    /// than inferred so a composition can assert on data** (the shape
    /// `IPrivacyBudgetLedger.IsDurable` and `IPeerReplayGuard.IsDistributed`
    /// already take). Aggregation happens initiator-side, because only the
    /// initiator can compute the intersection — so the initiator
    /// necessarily receives every responder payload and learns which of
    /// them fell in the overlap. If those payloads are readable to it,
    /// `AggregatePayload` is not an aggregate release at all: it is
    /// per-element disclosure of the responder's values, which is a
    /// strictly worse leak than the number it was asked for. A concealing
    /// mechanism is what makes the mode's name true, and a run with a
    /// non-concealing one is REFUSED unless the composition says
    /// otherwise (`PsiRunOptions.AllowRevealingAggregator`) — the same
    /// fail-loudly-rather-than-silently-complete posture the reference
    /// cipher's magic tag takes.
    abstract IsConcealing: bool

/// **Reference mechanism. NOT FOR PRODUCTION USE** — `IsConcealing` is
/// `false` and says so.
///
/// Addition of fixed-width big-endian unsigned integers modulo `2^(8·w)`:
/// the commutative group an additive-homomorphic ciphertext space models,
/// with the concealment removed. It ships for the same reason
/// `InMemoryCommutativeCipher` does — so the contract above it can be
/// exercised with nothing but this assembly — and it carries the same
/// warning for a sharper reason: here the missing property is not
/// performance or side-channel hardening but the encryption itself.
type InMemoryPsiAggregator(width: int) =

    /// 128-bit accumulator — wide enough that a realistic sum of caller
    /// values does not wrap into a wrong answer that looks like a right
    /// one.
    static let defaultWidth = 16

    do
        if width <= 0 then
            invalidArg "width" "the aggregate ciphertext width must be positive"

    new() = InMemoryPsiAggregator defaultWidth

    /// The encoded width, so a caller can size its payloads without
    /// re-deriving the constant.
    member _.Width = width

    interface IPsiAggregator with
        member _.IsConcealing = false

        member _.Zero = Array.zeroCreate width

        member _.Combine left right =
            if isNull left || isNull right then
                Error AggregateMalformed
            elif left.Length <> width || right.Length <> width then
                Error AggregateWidthMismatch
            else
                let sum = Array.zeroCreate<byte> width
                let mutable carry = 0

                for i in width - 1 .. -1 .. 0 do
                    let total = int left[i] + int right[i] + carry
                    sum[i] <- byte (total &&& 0xFF)
                    carry <- total >>> 8

                // The final carry is DROPPED, which is what "modulo
                // 2^(8·w)" means: wrapping keeps the group closed, and a
                // closed group is what makes the combine associative
                // whichever order the shuffle hands the intersection over
                // in. A caller whose values can overflow the width has
                // chosen the width wrongly.
                Ok sum

/// What the responder will serve. Declared per responder rather than
/// inferred per request, because "which questions may this counterparty
/// ask of my data" is exactly the judgement a mode negotiation exists to
/// make explicit.
type PsiResponderPolicy = {
    /// The modes this responder answers. A request for any other mode is
    /// refused outright — including a request for a LESS revealing mode,
    /// since serving a question the deployment never reviewed is the
    /// thing being avoided, not merely serving a revealing one.
    AcceptedModes: Set<PsiMode>
    /// The smallest initiator set this responder will answer, checked
    /// against `|A₁|`.
    ///
    /// This is the singleton-probe floor, and it is the one differencing
    /// defence available to the party that needs it: the responder can
    /// SEE the initiator's token count and no initiator can understate
    /// it. `0` disables the floor. The substrate ships no default number
    /// — what size is safe depends on the data and the regulator (GP 1),
    /// so `PsiResponderPolicy.create` takes it rather than assuming one.
    MinInputCohort: int
}

/// The optional release gate (479.C): the existing clean-room floor
/// (Phases 18b / 311) applied to a released count or aggregate, so a
/// sub-k overlap is withheld with an audit reason instead of released.
///
/// It is a control the RELEASING deployment applies to its own outbound
/// answer — the same thing `ICleanRoomBroker` already is, and the same
/// thing it is not: it is not a defence against a dishonest initiator,
/// which is the responder's `PsiResponderPolicy` and its choice to
/// participate at all.
///
/// `Members` runs are deliberately NOT gateable here: no `OutputShape`
/// describes "the intersection set", and mis-declaring one as `Count`
/// would let a k-floor read as satisfied over an answer it never
/// examined. A deployment that wants a floor on a `Members` run withholds
/// the MODE instead, via `AcceptedModes`.
type PsiReleaseGate = {
    Broker: ICleanRoomBroker
    Template: CleanRoomTemplate
    /// The template method name the release is attributed to, for the
    /// broker's surface enforcement and the audit trail.
    MethodName: string
    /// A caller-requested gate, composed with the template floor. May
    /// only tighten it (`PrivacyGate.compose`).
    Requested: PrivacyGate option
}

/// Everything a run needs beyond its key and elements. A record rather
/// than constructor parameters so the protocol object stays a stateless
/// singleton whose existing constructors are untouched (GP 11), and so a
/// composition can carry its posture as a value it can log and audit.
type PsiRunOptions = {
    Mode: PsiMode
    /// The aggregate mechanism. Required for `AggregatePayload`, unused
    /// otherwise.
    Aggregator: IPsiAggregator option
    /// Opt in to aggregating through a mechanism whose payloads the
    /// aggregating party can read. Default `false`, and the default is
    /// the point — see `IPsiAggregator.IsConcealing`.
    AllowRevealingAggregator: bool
    /// The optional clean-room floor on the released answer.
    Release: PsiReleaseGate option
}

/// What a mode run released.
type PsiModeOutcome =
    /// `Members` — the Phase 18f answer, unchanged.
    | MembersReleased of outcome: PsiOutcome
    /// `CardinalityOnly` — the size of the intersection, as a set.
    | CardinalityReleased of matched: int
    /// `AggregatePayload` — the intersection size and the opaque combined
    /// payload. Forge never reads `aggregate`; the caller decodes it with
    /// whatever it encoded the payloads under.
    | AggregateReleased of matched: int * aggregate: byte[]
    /// The release gate withheld the answer. A decision, not a failure —
    /// the run completed correctly and the floor bound, which is exactly
    /// what a withheld clean-room answer is (`GateDecision.Withheld`).
    | ReleaseWithheld of reason: string

/// The mode protocol. A second seam beside `IPrivateSetIntersection`
/// rather than extra members on it, so a deployment that substituted its
/// own `Members` construction is not forced to grow two more.
type IPrivateSetIntersectionModes =
    /// Run the initiator's side under `options`.
    abstract IntersectAs:
        options: PsiRunOptions *
        key: byte[] *
        elements: byte[] list *
        exchange: (PsiModeRequest -> Async<Result<PsiModeResponse, PeerError>>) ->
            Async<Result<PsiModeOutcome, PsiError>>

    /// Run the responder's side for a mode carrying no payloads. An
    /// `AggregatePayload` request is refused here rather than answered
    /// with empty payloads — a responder with no values to aggregate has
    /// not agreed to that question.
    abstract RespondAs:
        key: byte[] * elements: byte[] list * policy: PsiResponderPolicy * request: PsiModeRequest ->
            Result<PsiModeResponse, PsiError>

    /// Run the responder's side with a payload per element. Serves every
    /// mode; the payloads are ignored except in `AggregatePayload`.
    abstract RespondWithPayloads:
        key: byte[] * elements: PsiPayloadElement list * policy: PsiResponderPolicy * request: PsiModeRequest ->
            Result<PsiModeResponse, PsiError>

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

    /// Index doubly-encrypted tokens by a truncated prefix. The index is a
    /// lookup over bytes that are already public on the wire; the decision
    /// that actually determines membership is `indexContains`'s
    /// full-length constant-time compare.
    let buildIndex (raws: byte[] list) =
        let index = Dictionary<string, ResizeArray<byte[]>>()

        for raw in raws do
            let bucket = bucketOf raw

            match index.TryGetValue bucket with
            | true, existing -> existing.Add raw
            | false, _ -> index[bucket] <- ResizeArray [ raw ]

        index

    /// Membership against a `buildIndex` index. The bucket narrows the
    /// candidate set; the confirmation is the comparison whose OUTCOME is
    /// the private answer, and that is the one that must not be
    /// timing-distinguishable.
    ///
    /// Shared by every mode deliberately: two release paths that decided
    /// membership by two pieces of code could drift, and a cardinality
    /// that disagreed with the member set it is supposed to be the size of
    /// would be the hardest possible defect to notice.
    let indexContains (index: Dictionary<string, ResizeArray<byte[]>>) (raw: byte[]) =
        match index.TryGetValue(bucketOf raw) with
        | false, _ -> false
        | true, candidates ->
            candidates
            |> Seq.exists (fun candidate -> CommutativeCipher.bytesEqual candidate raw)

    /// Draw a permutation of `count` positions from the injected shuffle.
    ///
    /// The mode protocols need ONE permutation carried across TWO aligned
    /// lists (a responder's blinded tokens and their payloads), which a
    /// `string list -> string list` shuffle cannot express directly — so
    /// the seam is asked to permute the position labels instead, and the
    /// answer is checked to BE a permutation. A caller-supplied shuffle
    /// that dropped, duplicated or invented a position would otherwise
    /// silently drop payloads or mis-pair them with elements, which is a
    /// wrong aggregate wearing a success.
    let permutation (count: int) =
        if count = 0 then
            Ok [||]
        else
            let drawn = shuffle [ for i in 0 .. count - 1 -> string i ]

            let parsed =
                drawn
                |> List.choose (fun label ->
                    match Int32.TryParse label with
                    | true, value when value >= 0 && value < count -> Some value
                    | _ -> None)
                |> List.toArray

            if parsed.Length <> count || (Array.distinct parsed).Length <> count then
                Error(
                    PsiProtocol
                        "the supplied shuffle did not return a permutation of its input — the mode protocols carry two aligned lists under one permutation and cannot proceed without one"
                )
            else
                Ok parsed

    /// Apply a permutation drawn by `permutation` to a list of the same
    /// length.
    let reorder (perm: int[]) (items: 'a list) =
        let source = List.toArray items
        [ for index in perm -> source[index] ]

    /// Apply the optional clean-room floor (479.C) to a released cohort.
    let gateRelease (release: PsiReleaseGate option) (shape: OutputShape) (matched: int) (outcome: PsiModeOutcome) =
        match release with
        | None -> outcome
        | Some gate ->
            let result: CohortResult = {
                Shape = shape
                Cells = [
                    {
                        Label = "intersection"
                        Count = matched
                        Value = None
                    }
                ]
            }

            match gate.Broker.Enforce(gate.Template, gate.MethodName, gate.Requested, result) with
            | Withheld reason -> ReleaseWithheld reason
            | NoisedRelease _ ->
                // Phase 481 — a noised cardinality is not a cardinality
                // this protocol can carry. The whole point of the branch
                // below is that the released number must equal the
                // intersection that was computed; a calibrated draw makes
                // that false by construction, and reporting it as an
                // exact intersection size would be a wrong answer wearing
                // a success. A deployment wanting a noised PSI cardinality
                // asks for it as a gated aggregate, where the mechanism is
                // composed and audited.
                ReleaseWithheld
                    $"the clean-room broker for template '{gate.Template.TemplateId}' returned a noised release; a private-set-intersection cardinality is exact or it is withheld"
            | Released(cleared, suppressed) ->
                // A release is only carried through when the broker
                // cleared the WHOLE cohort. A suppressed cell would leave
                // the cleared cohort smaller than the intersection that
                // was actually computed, and releasing that smaller number
                // as "the intersection size" is a wrong answer wearing a
                // success — the outcome this file refuses to produce
                // quietly for a malformed token, refused here for a
                // suppressed cell.
                let cohort = cleared.Cells |> List.sumBy _.Count

                if List.isEmpty suppressed && cohort = matched then
                    outcome
                else
                    ReleaseWithheld
                        $"the clean-room gate for template '{gate.Template.TemplateId}' suppressed part of the released cohort; a partial cardinality is not a smaller intersection, so the whole answer is withheld"

    /// The responder's side of the mode protocol. `elements` carry a
    /// payload; the non-aggregate modes never read it.
    let respondMode
        (key: byte[])
        (elements: PsiPayloadElement list)
        (policy: PsiResponderPolicy)
        (request: PsiModeRequest)
        =
        // Negotiation first, and it is a refusal rather than a downgrade:
        // answering a mode the deployment never reviewed is the thing
        // being prevented, so an unaccepted request produces no tokens at
        // all rather than tokens computed under some other mode.
        if not (Set.contains request.Mode policy.AcceptedModes) then
            Error(
                PsiProtocol
                    $"the responder does not accept PSI mode %A{request.Mode}; the run is refused rather than answered under a different mode"
            )
        elif List.length request.Blinded < policy.MinInputCohort then
            // The singleton-probe floor. Checked against the token count
            // the responder can see, which is the only quantity about the
            // initiator's set the initiator cannot understate.
            Error(
                PsiProtocol
                    $"the initiator's set carries {List.length request.Blinded} element(s), below the responder's minimum input cohort of {policy.MinInputCohort}"
            )
        elif
            request.Mode = AggregatePayload
            && elements |> List.exists (fun element -> isNull element.Payload)
        then
            Error(
                PsiConfiguration
                    "an AggregatePayload responder was given an element with no payload; a missing payload would silently drop that element from every aggregate"
            )
        else
            match applyAll key request.Blinded with
            | Error e -> Error e
            | Ok doubled ->
                match blindAll key (elements |> List.map _.Element) with
                | Error e -> Error e
                | Ok mine ->
                    match request.Mode with
                    | Members ->
                        // Phase 18f, unchanged: the echo keeps the request
                        // order so the initiator can map a match back to
                        // its own pre-image.
                        Ok {
                            SessionId = request.SessionId
                            Mode = Members
                            PartnerBlinded = shuffle mine
                            Doubled = doubled
                            Payloads = []
                        }
                    | CardinalityOnly
                    | AggregatePayload ->
                        // Both transcripts permute. `Doubled`'s shuffle is
                        // what turns the initiator's answer from
                        // membership into a count, and it is applied here,
                        // by the party it protects.
                        match permutation (List.length doubled), permutation (List.length mine) with
                        | Error e, _
                        | _, Error e -> Error e
                        | Ok echoOrder, Ok ownOrder ->
                            let payloads =
                                match request.Mode with
                                | AggregatePayload ->
                                    // The SAME permutation as
                                    // `PartnerBlinded`: the alignment is
                                    // the only thing that keeps a payload
                                    // attached to its element, and a
                                    // second independent shuffle would
                                    // pair every value with the wrong id.
                                    elements |> List.map (fun element -> encode element.Payload) |> reorder ownOrder
                                | _ -> []

                            Ok {
                                SessionId = request.SessionId
                                Mode = request.Mode
                                PartnerBlinded = reorder ownOrder mine
                                Doubled = reorder echoOrder doubled
                                Payloads = payloads
                            }

    /// Combine an already-decoded payload set through the injected
    /// mechanism, left to right. Order is immaterial by contract — the
    /// mechanism is required to be associative and commutative — which is
    /// what makes it safe to fold a set that arrived through a shuffle.
    let combineAll (aggregator: IPsiAggregator) (payloads: byte[] list) =
        let rec walk (acc: byte[]) remaining =
            match remaining with
            | [] -> Ok acc
            | payload :: rest ->
                match aggregator.Combine acc payload with
                | Error e -> Error(PsiAggregation e)
                | Ok next -> walk next rest

        walk aggregator.Zero payloads

    /// Refuse a run the local composition cannot serve, BEFORE anything
    /// reaches the wire. A run that exchanged tokens and then discovered
    /// it had no mechanism would have already told the counterparty its
    /// set size for nothing.
    let preflight (options: PsiRunOptions) =
        match options.Mode with
        | Members
        | CardinalityOnly -> Ok()
        | AggregatePayload ->
            match options.Aggregator with
            | None ->
                Error(
                    PsiConfiguration
                        "AggregatePayload needs an IPsiAggregator; no aggregate mechanism is bound to this run"
                )
            | Some aggregator when not aggregator.IsConcealing && not options.AllowRevealingAggregator ->
                Error(
                    PsiConfiguration
                        "the bound IPsiAggregator declares IsConcealing = false: the aggregating party can read the counterparty's individual payloads, which is per-element disclosure rather than an aggregate release. Bind an additive-homomorphic mechanism whose key this party does not hold, or set PsiRunOptions.AllowRevealingAggregator once the deployment has reviewed that leak."
                )
            | Some _ -> Ok()

    /// The initiator's side of the mode protocol.
    let intersectAs
        (options: PsiRunOptions)
        (key: byte[])
        (elements: byte[] list)
        (exchange: PsiModeRequest -> Async<Result<PsiModeResponse, PeerError>>)
        =
        async {
            match preflight options with
            | Error e -> return Error e
            | Ok() ->
                let blinded =
                    match blindAll key elements with
                    | Error e -> Error e
                    | Ok mine ->
                        match options.Mode with
                        // `Members` must send its own element order — the
                        // echo is mapped back through it.
                        | Members -> Ok mine
                        // The other modes need no mapping back, so they
                        // can afford the shuffle `Members` cannot: it
                        // hides the initiator's element ordering from the
                        // responder, for the reason `B₁`'s shuffle hides
                        // the responder's from the initiator.
                        | CardinalityOnly
                        | AggregatePayload ->
                            permutation (List.length mine) |> Result.map (fun order -> reorder order mine)

                match blinded with
                | Error e -> return Error e
                | Ok sent ->
                    let request = {
                        SessionId = Guid.NewGuid().ToString "N"
                        Mode = options.Mode
                        Blinded = sent
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
                        elif response.Mode <> request.Mode then
                            // The other half of the negotiation. An answer
                            // computed under a different mode answers a
                            // different question, and accepting one would
                            // be the silent downgrade the mode exists to
                            // rule out.
                            return
                                Error(
                                    PsiProtocol
                                        $"the counterparty answered in mode %A{response.Mode} for a request in mode %A{request.Mode}; a mode is not negotiable downward"
                                )
                        elif List.length response.Doubled <> List.length sent then
                            return
                                Error(
                                    PsiProtocol
                                        $"the counterparty returned {List.length response.Doubled} doubly-encrypted token(s) for {List.length sent} sent — the answer is short of the set it was asked about"
                                )
                        else
                            match applyAll key response.PartnerBlinded with
                            | Error e -> return Error e
                            | Ok theirs ->
                                match decodeAll theirs, decodeAll response.Doubled with
                                | Error e, _
                                | _, Error e -> return Error e
                                | Ok theirsRaw, Ok mineRaw ->
                                    match options.Mode with
                                    | Members ->
                                        let index = buildIndex theirsRaw

                                        let matches =
                                            List.zip3 elements response.Doubled mineRaw
                                            |> List.choose (fun (element, token, raw) ->
                                                if indexContains index raw then
                                                    Some(element, token)
                                                else
                                                    None)

                                        return
                                            Ok(
                                                MembersReleased {
                                                    MatchedTokens = matches |> List.map snd
                                                    MatchedElements = matches |> List.map fst
                                                }
                                            )
                                    | CardinalityOnly ->
                                        // `response.Doubled` arrived
                                        // permuted, so there is nothing to
                                        // map back and nothing here tries
                                        // to: the answer is a count over
                                        // DISTINCT tokens, which is the
                                        // size of a set intersection
                                        // whatever multiplicity either
                                        // caller's input carried.
                                        let index = buildIndex theirsRaw

                                        let matched =
                                            List.zip response.Doubled mineRaw
                                            |> List.distinctBy fst
                                            |> List.filter (snd >> indexContains index)
                                            |> List.length

                                        return
                                            Ok(gateRelease options.Release Count matched (CardinalityReleased matched))
                                    | AggregatePayload ->
                                        if List.length response.Payloads <> List.length response.PartnerBlinded then
                                            return
                                                Error(
                                                    PsiProtocol
                                                        $"the counterparty returned {List.length response.Payloads} payload(s) for {List.length response.PartnerBlinded} blinded element(s) — the alignment that keeps a value attached to its element is broken"
                                                )
                                        else
                                            let index = buildIndex mineRaw

                                            let matched =
                                                List.zip3 theirs theirsRaw response.Payloads
                                                |> List.filter (fun (_, raw, _) -> indexContains index raw)
                                                |> List.distinctBy (fun (token, _, _) -> token)

                                            match decodeAll (matched |> List.map (fun (_, _, payload) -> payload)) with
                                            | Error e -> return Error e
                                            | Ok payloads ->
                                                match options.Aggregator with
                                                | None ->
                                                    // Unreachable: `preflight` refuses
                                                    // this run before the exchange.
                                                    return
                                                        Error(
                                                            PsiConfiguration
                                                                "AggregatePayload needs an IPsiAggregator; no aggregate mechanism is bound to this run"
                                                        )
                                                | Some aggregator ->
                                                    match combineAll aggregator payloads with
                                                    | Error e -> return Error e
                                                    | Ok aggregate ->
                                                        let count = List.length matched

                                                        return
                                                            Ok(
                                                                gateRelease
                                                                    options.Release
                                                                    Aggregate
                                                                    count
                                                                    (AggregateReleased(count, aggregate))
                                                            )
        }

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
                // Annotated because `PsiModeRequest` (Phase 479) carries a
                // superset of these labels, so bare inference would resolve
                // to the later type and fail on the absent `Mode`.
                let request: PsiRequest = {
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
                                let index = buildIndex theirsRaw

                                let matches =
                                    List.zip3 elements response.Doubled mineRaw
                                    |> List.choose (fun (element, token, raw) ->
                                        if indexContains index raw then
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
                    let response: PsiResponse = {
                        SessionId = request.SessionId
                        PartnerBlinded = shuffle mine
                        Doubled = doubled
                    }

                    Ok response

    interface IPrivateSetIntersectionModes with
        member _.IntersectAs(options, key, elements, exchange) =
            intersectAs options key elements exchange

        member _.RespondAs(key, elements, policy, request) =
            if request.Mode = AggregatePayload then
                // Refused rather than answered with empty payloads: a
                // responder holding no values has not agreed to an
                // aggregate question, and an aggregate over absent
                // payloads is an answer nobody asked for.
                Error(
                    PsiProtocol
                        "this responder was asked for AggregatePayload but carries no payloads; use RespondWithPayloads or decline the mode"
                )
            else
                respondMode
                    key
                    (elements
                     |> List.map (fun element -> {
                         Element = element
                         Payload = Array.empty
                     }))
                    policy
                    request

        member _.RespondWithPayloads(key, elements, policy, request) = respondMode key elements policy request

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

    /// The Phase 479 mode protocol over `cipher`, with the cryptographic
    /// shuffle. The same object serves both seams — a deployment holding
    /// one instance can run either.
    let createModes (cipher: ICommutativeCipher) : IPrivateSetIntersectionModes =
        DefaultPrivateSetIntersection(cipher) :> IPrivateSetIntersectionModes

    /// The mode protocol with a caller-supplied permutation. Note the
    /// permutation is drawn TWICE per non-`Members` response (once for
    /// each transcript) and once by the initiator, so a deterministic
    /// shuffle pins the whole run — which is what the conformance pack
    /// needs and what a production deployment must not want.
    let createModesWith
        (cipher: ICommutativeCipher)
        (shuffle: string list -> string list)
        : IPrivateSetIntersectionModes =
        DefaultPrivateSetIntersection(cipher, shuffle) :> IPrivateSetIntersectionModes

    /// Adapt a `PsiModeApi` proxy into the exchange closure `IntersectAs`
    /// drives — the `overContract` equivalent for the mode protocol.
    let overModeContract (api: PsiModeApi) : PsiModeRequest -> Async<Result<PsiModeResponse, PeerError>> =
        fun request -> async {
            try
                let! response = api.ExchangeMode request
                return Ok response
            with ex ->
                return Error(PeerTransport ex.Message)
        }

[<RequireQualifiedAccess>]
module PsiPayloadElement =
    /// An element carrying no payload — the shape the non-aggregate modes
    /// use.
    let ofElement (element: byte[]) : PsiPayloadElement = {
        Element = element
        Payload = Array.empty
    }

    /// An element and the opaque payload that travels with it.
    let create (element: byte[]) (payload: byte[]) : PsiPayloadElement = { Element = element; Payload = payload }

[<RequireQualifiedAccess>]
module PsiResponderPolicy =
    /// Declare what this responder serves. Both arguments are required
    /// because both are policy the substrate has no basis to assume: which
    /// questions this counterparty may ask, and how small a set it may ask
    /// them about (GP 1).
    let create (modes: PsiMode seq) (minInputCohort: int) : PsiResponderPolicy =
        if minInputCohort < 0 then
            invalidArg "minInputCohort" "the minimum input cohort cannot be negative"

        {
            AcceptedModes = Set.ofSeq modes
            MinInputCohort = minInputCohort
        }

    /// Phase 18f's posture expressed as a policy: `Members` only, no input
    /// floor. Provided so a deployment can state the old behaviour rather
    /// than arrive at it by omission.
    let membersOnly: PsiResponderPolicy = create [ Members ] 0

    /// Raise (or lower) the singleton-probe floor.
    let withMinInputCohort (minInputCohort: int) (policy: PsiResponderPolicy) : PsiResponderPolicy = {
        policy with
            MinInputCohort = minInputCohort
    }

[<RequireQualifiedAccess>]
module PsiRunOptions =
    /// A run in `mode` with no aggregate mechanism and no release gate.
    /// `AggregatePayload` is refused in this shape — bind a mechanism with
    /// `withAggregator`.
    let create (mode: PsiMode) : PsiRunOptions = {
        Mode = mode
        Aggregator = None
        AllowRevealingAggregator = false
        Release = None
    }

    /// Bind the aggregate mechanism.
    let withAggregator (aggregator: IPsiAggregator) (options: PsiRunOptions) : PsiRunOptions = {
        options with
            Aggregator = Some aggregator
    }

    /// Accept a mechanism whose payloads the aggregating party can read.
    /// Read `IPsiAggregator.IsConcealing` before reaching for this — it
    /// converts an aggregate release into per-element disclosure of the
    /// counterparty's values.
    let allowingRevealingAggregator (options: PsiRunOptions) : PsiRunOptions = {
        options with
            AllowRevealingAggregator = true
    }

    /// Apply a clean-room floor to the released answer (479.C).
    let withRelease (gate: PsiReleaseGate) (options: PsiRunOptions) : PsiRunOptions = {
        options with
            Release = Some gate
    }