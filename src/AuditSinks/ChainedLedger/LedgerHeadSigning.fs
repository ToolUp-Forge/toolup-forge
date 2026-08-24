// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AuditSinks.LedgerHeadSigning

open ToolUp.Platform
open ToolUp.Platform.AuditSinks.ChainedLedger

// ─── Bridging the ledger's local seam onto a composed key story ─────────
//
// `ILedgerHeadSigner` was declared deliberately generic and deliberately
// local: the ledger needs a key id, an algorithm name and
// bytes-to-signature, and asking for more would have coupled this package
// to whichever key-management substrate existed when it was written. That
// judgement stands, and this module does not revisit it — the seam is
// unchanged, the unsigned default is unchanged, and a deployment that has
// implemented the seam itself keeps working exactly as it did (GP 11).
//
// What it adds is the missing alternative to implementing it yourself.
// `IKeyedByteSigner` (in `ToolUp.Platform.Server`, which this package
// already references) is the same three facts, stated once for every
// recording substrate that needs them, and the signing companion fills it
// from the deployment's composed signer. Bridging one onto the other is
// the twenty lines below — no new package dependency here, no crypto here,
// and no key material required by anything that does not want it (GP 1,
// GP 13).
//
// **This is a pure re-shaping, and that is the whole of it.** Nothing here
// interprets a scheme, frames a message, or decides what a signature
// claims; the seam on the far side owns all of that. Keeping the bridge
// empty is what lets the ledger stay honest about the one thing it does
// know — that it recorded a signature, under a named key, produced by a
// scheme it does not itself understand.

/// Adapt a keyed byte signer onto the ledger's head-signing seam.
///
/// The signer's SCHEME becomes the head's recorded `Algorithm`. That field
/// exists so "a verifier can refuse rather than guess", which is exactly a
/// scheme name's job — and a scheme string says strictly more than a bare
/// primitive name, because it also fixes how the signed message was
/// framed. A verifier that does not recognise it refuses, which is the
/// behaviour the field was put there for.
let ofKeyedSigner (signer: IKeyedByteSigner) : ILedgerHeadSigner =
    { new ILedgerHeadSigner with
        member _.KeyId = signer.KeyId()

        member _.Algorithm = signer.Scheme()

        member _.Sign(headBytes: byte[]) = signer.Sign headBytes
    }

/// Adapt a keyed byte verifier onto the ledger's head-verification seam.
///
/// The two `Result<bool, string>` contracts already agree case for case —
/// valid, well-formed-but-wrong, and cannot-be-accepted-and-here-is-why —
/// so the mapping is the identity. The ledger renders the third case as
/// `HeadSignatureUnverifiable`, which carries the reason and never reads
/// as a pass.
let verifierOfKeyed (verifier: IKeyedByteVerifier) : ILedgerHeadVerifier =
    { new ILedgerHeadVerifier with
        member _.Verify(keyId: string, algorithm: string, headBytes: byte[], signature: byte[]) =
            verifier.Verify(keyId, algorithm, headBytes, signature)
    }