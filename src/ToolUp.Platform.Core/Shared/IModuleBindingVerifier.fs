// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 165 — opt-in module-binding gate ─────────────────────────────
//
// A deployment may opt into refusing to load modules that are not *bound*
// to it. The gate is a second check beside the existing
// `ModuleFilter.matches` name filter in `ServerApp.addModule`: a module
// can carry a `ModuleBindingStamp`, and the deployment can configure an
// `IModuleBindingVerifier` that decides whether the stamp verifies under
// one of its trust anchors.
//
// This file is the tier-shared *contract* only — pure types + an abstract
// interface, no crypto. It ships in the Fable-packed source (it is
// BCL-pure), so a client-tier consumer that happens to reference these
// types compiles cleanly. The default verifier — which reuses the Phase 40
// detached-JWS verify primitives and an HMAC compare against a configured
// anchor set — lives server-side in the `ToolUp.ArtefactSigning` companion
// (it cannot live here: that companion references `ToolUp.Platform.Server`,
// so the crypto sits *above* Core, not in it).
//
// **Design intent (GP 13 / GP 9):** the SDK names no module and ships no
// keyring. A deployment's keyring, stamping policy, and key custody are a
// consumer concern; forge only verifies a presented stamp against the
// anchors the consumer hands the verifier. When neither a verifier nor a
// stamp is present, the gate is a single cheap branch and `addModule`
// behaves byte-for-byte as it did pre-165.

/// A binding stamp a module presents to the gate. The signed payload is
/// the module's identifier bytes, so a stamp minted for one module cannot
/// be replayed onto another — `addModule` recomputes the canonical bytes
/// from the *actual* module name being gated, never from a self-asserted
/// field on the stamp.
type ModuleBindingStamp =
    /// Asymmetric: a detached JWS (the Phase 40 `ArtefactSignature` shape —
    /// `base64url(header)..base64url(signature)`) over the canonical
    /// module-binding bytes. Verified against an asymmetric trust anchor
    /// (an ECDSA P-256 / Ed25519 public verify key). The JWS protected
    /// header carries the `alg` + `kid`.
    | JwsStamp of detachedJws: string
    /// Symmetric: a base64url-encoded HMAC-SHA256 tag over the canonical
    /// module-binding bytes, plus the anchor-key id the tag was produced
    /// under. Verified against a symmetric trust anchor (a shared MAC key).
    | MacStamp of keyId: string * tag: string

/// The decision the gate acts on for a single module.
type BindingOutcome =
    /// The module is admitted (no stamp under a permitting policy, or a
    /// stamp that verified under a configured anchor).
    | Allowed
    /// The module is refused; `reason` is a neutral diagnostic suitable
    /// for a startup log (it names no commercial concept).
    | Rejected of reason: string

/// Opt-in module-binding verifier. A deployment composes one (e.g.
/// `DefaultModuleBindingVerifier` from `ToolUp.ArtefactSigning`, built over
/// a value-typed set of trust anchors) and `ServerApp.addModule` consults
/// it as a second gate after the module-name filter.
///
/// **Verification rule (load-bearing):** an absent stamp is `Allowed`
/// (policy permitting); a *present* stamp MUST verify under some configured
/// anchor, else `Rejected`. A stamped module is therefore self-protecting:
/// on a deployment that lacks the matching anchor it fails closed.
///
/// `Verify` is synchronous — anchors carry their key material by value, so
/// no I/O (secret-store lookup) is involved; the work is a CPU-bound
/// signature / MAC check that fits `addModule`'s synchronous shape.
type IModuleBindingVerifier =
    /// Decide whether `moduleId` may load given the `stamp` it presents
    /// (`None` when the module carries no stamp).
    abstract Verify: moduleId: string * stamp: ModuleBindingStamp option -> BindingOutcome

// ─── Phase 170 — trust-anchor config surface (`fromEnv`) ────────────────
//
// A Core-pure *description* of the deployment's trust anchors, so one
// container image can be configured for module binding by environment
// alone (matching the Wave 22 runtime-config theme) rather than baking the
// anchor set at compile time. These types carry no key *material* — a
// symmetric anchor references its MAC key indirectly (resolved through
// `ISecretStore` at compose time, never read from a plaintext config),
// and an asymmetric anchor carries only its public verify key. The
// server-side resolver (in `ToolUp.ArtefactSigning`) turns these refs into
// the value-typed `ModuleBindingAnchor` set the `DefaultModuleBindingVerifier`
// is built over; an `IConfigValidator` fails startup closed if a configured
// symmetric anchor's secret does not resolve (a named gap, never a silent
// disable). BCL-pure (strings only) so it ships in the Fable-packed source.

/// A description of one trust anchor, resolved into a crypto-bearing
/// `ModuleBindingAnchor` at compose time.
type ModuleBindingAnchorRef =
    /// Symmetric MAC anchor. The shared HMAC-SHA256 key is *referenced*, not
    /// inlined: `secretScope` + `secretKey` are looked up through the
    /// deployment's `ISecretStore` (the value is a base64 key). `keyId` is
    /// matched against a `MacStamp`'s declared key id.
    | SymmetricAnchorRef of keyId: string * secretScope: string * secretKey: string
    /// Asymmetric public-verify anchor for a `JwsStamp`. `algorithm` is the
    /// case-name string (`"EcdsaP256"` / `"Ed25519"`); `publicKeyBase64` is
    /// the base64 public key (SubjectPublicKeyInfo DER for ECDSA, raw 32
    /// bytes for Ed25519 — the `StoredSigningKey.PublicKey` convention).
    | AsymmetricAnchorRef of keyId: string * algorithm: string * publicKeyBase64: string

/// The deployment's module-binding trust configuration. Default = no
/// anchors + `AllowUnbound = true`, i.e. binding is **off** and behaviour
/// is byte-for-byte the pre-binding pipeline (GP 13).
type ModuleBindingTrustConfig = {
    Anchors: ModuleBindingAnchorRef list
    /// Whether a module that presents *no* stamp is admitted. `true`
    /// (default) admits unstamped modules; `false` requires every module to
    /// be stamped and verified.
    AllowUnbound: bool
}

module ModuleBindingTrustConfig =
    let defaults = { Anchors = []; AllowUnbound = true }

// ─── Phase 215 — revocation list + transparency log ─────────────────────
//
// Sign+verify (165/166) proves a stamp was minted under a trusted anchor,
// but a *compromised* anchor key has no kill switch — every stamp it ever
// minted keeps verifying — and there is no append-only record of what the
// gate admitted. These two seams close that gap, both **off by default** so
// a deployment configuring neither is byte-for-byte unchanged (GP 13):
//
//   * `IBindingRevocationList` — consulted by the verifier *after* a stamp
//     verifies cryptographically and *before* it returns `Allowed`. A
//     revoked anchor (whole-key kill) or a revoked individual stamp denies
//     regardless of a valid signature. An empty / absent list admits
//     exactly as today.
//   * `IBindingTransparencyLog` — an append-only sink the verifier records
//     every admit/deny decision into, so "what did this deployment let
//     load, and when?" is answerable after the fact.
//
// Both are tier-shared contracts (BCL-pure, Fable-safe) living beside
// `IModuleBindingVerifier`. The crypto-free file format + parser + the
// `stampId` derivation + a file-backed transparency log live server-side in
// `ModuleBindingRevocation` (`ToolUp.Platform.Server`); the *signed*
// revocation-list loader (detached-JWS-verified, fail-closed) lives in
// `ToolUp.ArtefactSigning` beside the default verifier, since it reuses the
// Phase 40 JWS verify primitives.

/// An append-only record of one binding-gate decision, written to an
/// `IBindingTransparencyLog`. `AnchorId` / `StampId` are present only when a
/// stamp was presented and identified (an absent-stamp admit records
/// neither); `Reason` carries the neutral denial diagnostic on a `Rejected`
/// outcome.
type BindingDecision = {
    ModuleId: string
    /// The trust anchor that admitted the stamp (its key id), when a stamp
    /// verified; `None` for an absent-stamp admit or a stamp no anchor
    /// verified.
    AnchorId: string option
    /// A stable identifier for the presented stamp (see
    /// `ModuleBindingRevocation.stampId`); `None` when no stamp was present.
    StampId: string option
    /// `true` when the module was admitted, `false` when the gate denied it.
    Admitted: bool
    /// Neutral denial diagnostic on a deny; `None` on an admit.
    Reason: string option
    /// Wall-clock at the decision.
    TimestampUtc: System.DateTimeOffset
}

/// Opt-in revocation list the verifier consults after a stamp verifies
/// cryptographically and before it admits the module. A `true` from
/// `IsRevoked` denies a stamp that is otherwise valid — the kill switch a
/// compromised anchor key needs. The default (`BindingRevocationList.none`)
/// revokes nothing, so a deployment that configures no list admits exactly
/// as it did pre-215.
///
/// `IsRevoked` is synchronous and side-effect-free — the list is a
/// deploy-time artefact resolved into memory at compose time, so the check
/// fits the verifier's synchronous `Verify` shape with no I/O.
type IBindingRevocationList =
    /// Is the stamp identified by (`anchorId`, `stampId`) revoked? A list
    /// may revoke a whole anchor (every stamp under `anchorId`) or one
    /// specific `stampId`.
    abstract IsRevoked: anchorId: string * stampId: string -> bool

/// Opt-in append-only transparency log the verifier records every gate
/// decision into. The default (`BindingTransparencyLog.none`) records
/// nothing and costs nothing; a deployment opts in by composing a real
/// sink (e.g. the file-backed `FileBindingTransparencyLog` in
/// `ModuleBindingRevocation`, or a custom blob-backed implementation).
///
/// `Record` is `Async` so a real sink can append to a file / blob without
/// blocking on the I/O shape; the verifier awaits it at the (startup-time,
/// not hot-path) gate so a recorded decision is durable before the module
/// loads.
type IBindingTransparencyLog =
    /// Append one decision to the log.
    abstract Record: decision: BindingDecision -> Async<unit>

/// The no-op revocation list (revokes nothing) — the GP-13 default.
module BindingRevocationList =
    /// A revocation list that revokes nothing; the verifier admits exactly
    /// as it did pre-215.
    let none =
        { new IBindingRevocationList with
            member _.IsRevoked(_, _) = false
        }

/// The no-op transparency log (records nothing) — the GP-13 default.
module BindingTransparencyLog =
    /// A transparency log that records nothing and costs nothing.
    let none =
        { new IBindingTransparencyLog with
            member _.Record(_) = async { return () }
        }