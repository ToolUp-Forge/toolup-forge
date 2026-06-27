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