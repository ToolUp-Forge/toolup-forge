// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.ArtefactSigning

/// The record of a deployment's signing-key lifecycle, as data.
///
/// Rotation and revocation are ordinarily done by swapping key material:
/// a new file appears, an old one is deleted, and the deployment's trust
/// posture changes with no record of who changed it or why. That has two
/// costs a signing story cannot absorb. Deleting the old key breaks every
/// signature already made under it, so rotation becomes a
/// re-sign-everything event. And distrusting a key looks identical to
/// losing it, so a relying party shown a failure cannot tell "this
/// signature was forged" from "we tidied up a key file".
///
/// This ledger separates the two. Key MATERIAL stays where the signer put
/// it, so an old signature keeps verifying after rotation. Key TRUST is
/// an append-only sequence of attributable `SigningKeyEvent`s, so a
/// revocation is a recorded decision with an actor and a reason, and the
/// current state of any key is folded from that record rather than
/// inferred from what happens to be on disk.
///
/// **Six portability rules (GP 12).**
/// 1. *Identity by value* — key ids are strings, events are records; no
///    live handles cross the boundary.
/// 2. *Async at every boundary* — both members return `Async<_>`.
/// 3. *Retry as data* — a write failure is `Result.Error` carrying a
///    reason, never an exception or a callback.
/// 4. *Stateless between invocations* — an implementation caches
///    nothing; `History` re-reads.
/// 5. *No cross-shard ordering* — events carry their own timestamps and
///    fold order-independently (revocation is terminal regardless of
///    arrival order), so a distributed store needs no global sequence.
/// 6. *Precision at the lower bound* — event timestamps are wall-clock
///    records of a decision, never used as an ordering primitive finer
///    than a second.
type ISigningKeyLedger =
    /// Append one lifecycle event. Append-only by contract: an
    /// implementation never rewrites or removes a recorded event, so the
    /// history of a key is auditable rather than merely current.
    abstract Record: event: SigningKeyEvent -> Async<Result<unit, string>>

    /// The whole recorded history, folded per key. An empty history is a
    /// legitimate state — a deployment that has recorded nothing has
    /// revoked nothing, and its signatures verify on their bytes alone.
    abstract History: unit -> Async<SigningKeyHistory>