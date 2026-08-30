// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 654 — the signed-shape separator registry ─────────────────
//
// Every signed shape in the SDK opens its canonical encoding with a
// **domain separator**, so a signature minted over one shape can never be
// replayed as another. The mechanism was right and the layer below it was
// careful — fields are length-prefixed (`{byteLen}:{value}`), and wire
// names for DU cases are written out rather than derived — but the
// separators themselves got none of that protection. They were
// hand-written string literals, one per module, with no mechanical
// relationship to anything, and **nothing could enumerate the set**.
//
// The 2026-08-18 rename wave is the evidence: renaming them took THREE
// passes, each finding separators the previous had missed, and each pass
// was a breaking wire change invalidating the same signed artefacts. The
// cost of not knowing the set was three invalidations where one would
// have done — and even after three passes the set was still incomplete.
// Building this registry found a SIXTH separator none of them reached
// (`PromotedArtifact`, below), because it lived inside a `sprintf` format
// string rather than a named binding. That is the argument for this file
// in one sentence: a grep finds what it is shaped to find, and an
// exhaustive match over a closed union finds everything.
//
// ── What this file is, and what it is not ──
//
// A signature is over **bytes**. The F# type system already makes these
// shapes distinct at compile time, and that distinctness is precisely
// what is erased at the byte boundary: the verifier receives a byte
// string, not a typed value. The separator IS the type tag in the
// encoding. So this file does not replace the mechanism — it makes the
// SET knowable, and it makes the FORMAT a construction rather than a
// convention five authors each remembered differently.
//
// ── Why typed parts rather than a table of strings ──
//
// Read as opaque strings, the six separators disagreed with each other:
// five ended `/1` and one ended `.v1`, and namespace depth ran from four
// segments to two. A registry storing them verbatim would have made an
// inconsistent scheme permanent and dignified it as a design. Storing the
// PARTS instead buys three things a lookup table cannot:
//
//   * **Format correctness.** There is one renderer, so the version
//     suffix cannot be spelled two ways. `Version` is an `int`, so a bump
//     is arithmetic rather than string surgery.
//   * **A sound collision check.** Segments exclude `.` and `/` (see
//     `isSegment`), and a version is digits, so `render` is INJECTIVE: a
//     rendered separator decomposes back to exactly one `(vendor, path,
//     version)`. Two shapes therefore collide in bytes if and only if
//     they collide in parts. Without that, "the strings differ" would be
//     a happy accident of how they were written rather than a property.
//   * **A knowable set.** `SignedShape` is closed and `parts` is matched
//     exhaustively with **no catch-all arm**, so adding a signed shape
//     without a separator is a COMPILE ERROR rather than an omission
//     nobody can see. That is the whole point of the phase.
//
// ── What is deliberately NOT derived ──
//
// The vendor and path segments are written out as literal data, never
// produced from an F# identifier (no `nameof`, no case-name reflection).
// This is the same rule `TemplateCanonical.shapeName` states and for the
// same reason: renaming a case in F# source must not be able to silently
// invalidate every signature already minted across a federation. A typed
// registry that derived its strings from case names would have traded one
// invisible coupling for a worse one.
//
// ── What enforcement is, honestly ──
//
// Well-formedness is asserted by `SignedShapeSeparatorTests`, which
// derives its cases by reflecting over `SignedShape`'s union cases rather
// than listing them — so a new shape inherits format validation, the
// collision check and a demand for a pinned digest automatically, and
// fails until someone supplies the pin. Injectivity of `render` holds by
// construction GIVEN well-formedness; well-formedness itself is a test
// over a closed, compile-time-constant set rather than a type-system
// guarantee. A smart constructor returning `Result` would move that into
// the types at the cost of unwrapping a `Result` at every constant
// definition, which buys nothing over an exhaustive test of a finite set.
//
// ── Cost, and Fable ──
//
// Pure strings, lists and a DU: no crypto, no BCL beyond the primitives,
// no reflection. The file ships in the Fable-packed source with the rest
// of `Shared/`, and a client that never mentions a `SignedShape` pays
// nothing (GP 11 / GP 13).

/// A domain separator as typed parts, rendered by
/// `SignedShapeSeparator.render` into the text that opens a canonical
/// encoding: `{vendor}.{path joined by '.'}/{version}`.
///
/// Every field is literal data written out by hand. Nothing here is
/// derived from an F# identifier — see the file header.
type SignedShapeSeparator = {
    /// The owning vendor namespace, e.g. `fuaran` or `toolup`. One
    /// segment, subject to `SignedShapeSeparator.isSegment`.
    Vendor: string
    /// The path beneath the vendor, outermost segment first. Non-empty;
    /// depth varies legitimately between shapes (a federation shape is
    /// several segments deep, a single ToolUp protocol is one), and
    /// uniform depth is not a goal.
    Path: string list
    /// The separator's version, `1` upwards. **Bumping this is a
    /// breaking wire change** — it invalidates every signature already
    /// minted over the shape, which is exactly what it is for.
    Version: int
}

/// Every signed shape in the SDK. **Closed on purpose**: `parts` matches
/// it exhaustively with no catch-all, so a new case cannot compile until
/// its separator lands.
///
/// `[<RequireQualifiedAccess>]` because two of these names are also live
/// type names in `ToolUp.InterPlatform` (`CleanRoomTemplate`,
/// `ActivationAuthorisation`); qualifying keeps the call sites in those
/// files unambiguous to a reader as well as to the compiler.
[<RequireQualifiedAccess>]
type SignedShape =
    /// A clean-room template's canonical encoding, whose digest IS the
    /// template version an approval binds to (Phase 480).
    | CleanRoomTemplate
    /// A bilateral template-approval lifecycle record (Phase 480).
    | CleanRoomApprovalRecord
    /// A governed cohort-activation authorisation (Phase 490).
    | ActivationAuthorisation
    /// One outbound signal-feed emission's idempotency key (Phase 491).
    | SignalFeedDelivery
    /// The acceptance signature over a promoted model artifact
    /// (`ModelPromotionSigningInput`).
    ///
    /// **This is the one the three rename passes never found**, because
    /// it is not a named binding: it was embedded inside a `sprintf`
    /// format string, so every sweep looking for a standalone separator
    /// literal walked straight past it. It is in this registry precisely
    /// so the next sweep is a match expression rather than a grep.
    | PromotedArtifact
    /// A worker's signed terminal outcome, carried on the
    /// `X-ToolUp-Worker-Signature` header (Phase 320-era signing).
    | WorkerSignedOutcome
    /// A countersignature SUBJECT hashed by the generic registry's own
    /// encoder (Phase 676) — the case where a domain hands the registry
    /// canonical bytes rather than a hash it computed under its own
    /// separator. The kind tag is inside the hashed bytes, so the same
    /// content registered under two kinds cannot produce one hash.
    | CountersignatureSubject
    /// An N-party countersignature lifecycle record (Phase 676). The
    /// generic successor to `CleanRoomApprovalRecord`, which keeps its
    /// own separator unchanged — a bilateral approval already minted
    /// must keep verifying.
    | CountersignatureRecord

[<RequireQualifiedAccess>]
module SignedShapeSeparator =

    /// Is `value` a well-formed segment — non-empty, lowercase
    /// alphanumeric with interior hyphens, no leading or trailing hyphen?
    ///
    /// **The exclusions are the load-bearing part.** A segment can hold
    /// neither `.` nor `/`, which are the two characters `render` uses as
    /// structure. That is what makes the rendering injective: `["a.b";
    /// "c"]` and `["a"; "b.c"]` could otherwise render identically, and
    /// the collision check downstream would be asserting nothing.
    let isSegment (value: string) : bool =
        not (System.String.IsNullOrEmpty value)
        && value[0] <> '-'
        && value[value.Length - 1] <> '-'
        && value
           |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')

    /// The separator's wire text: `{vendor}.{path}/{version}`.
    ///
    /// Total and allocation-light. It does NOT validate — `validate` is
    /// the check, run over the whole closed set by the test pack, because
    /// every value reaching here is a compile-time constant.
    let render (separator: SignedShapeSeparator) : string =
        System.String.Concat(
            separator.Vendor,
            ".",
            System.String.Join(".", separator.Path),
            "/",
            string separator.Version
        )

    /// Why `separator` is not well formed, or `Ok`.
    ///
    /// Every clause protects the injectivity `render` claims, so a
    /// failure here is not a tidiness complaint — it is the collision
    /// guarantee ceasing to hold.
    let validate (separator: SignedShapeSeparator) : Result<unit, string> =
        if not (isSegment separator.Vendor) then
            Error $"vendor '%s{separator.Vendor}' is not a well-formed segment"
        elif List.isEmpty separator.Path then
            Error "path is empty — a separator must name something beneath its vendor"
        elif separator.Path |> List.exists (isSegment >> not) then
            let bad = separator.Path |> List.filter (isSegment >> not) |> String.concat ", "

            Error $"path segment(s) '%s{bad}' are not well-formed segments (no '.' or '/')"
        elif separator.Version < 1 then
            Error $"version %d{separator.Version} is below 1"
        else
            Ok()

[<RequireQualifiedAccess>]
module SignedShape =

    /// Every shape, in declaration order. Convenience for enumeration;
    /// the test pack pins it against the union's cases by reflection, so
    /// it cannot silently go stale when a case is added.
    let all: SignedShape list = [
        SignedShape.CleanRoomTemplate
        SignedShape.CleanRoomApprovalRecord
        SignedShape.ActivationAuthorisation
        SignedShape.SignalFeedDelivery
        SignedShape.PromotedArtifact
        SignedShape.WorkerSignedOutcome
        SignedShape.CountersignatureSubject
        SignedShape.CountersignatureRecord
    ]

    /// The typed parts of a shape's domain separator.
    ///
    /// **Exhaustive, with no catch-all arm — deliberately.** Adding a
    /// case to `SignedShape` breaks this match until its separator is
    /// written, which is the mechanism this phase exists to install.
    /// Every string below is literal data; none is derived from the case
    /// name beside it (file header, "What is deliberately NOT derived").
    let parts (shape: SignedShape) : SignedShapeSeparator =
        match shape with
        | SignedShape.CleanRoomTemplate -> {
            Vendor = "fuaran"
            Path = [ "federation"; "cleanroom"; "template" ]
            Version = 1
          }
        | SignedShape.CleanRoomApprovalRecord -> {
            Vendor = "fuaran"
            Path = [ "federation"; "cleanroom"; "approval" ]
            Version = 1
          }
        | SignedShape.ActivationAuthorisation -> {
            Vendor = "fuaran"
            Path = [ "federation"; "activation"; "authorisation" ]
            Version = 1
          }
        | SignedShape.SignalFeedDelivery -> {
            Vendor = "fuaran"
            Path = [ "federation"; "signalfeed"; "delivery" ]
            Version = 1
          }
        | SignedShape.PromotedArtifact -> {
            Vendor = "fuaran"
            Path = [ "federation"; "promoted-artifact" ]
            Version = 1
          }
        // Branded `toolup`, by operator decision (2026-08-18) and not by
        // oversight: this names a ToolUp-specific protocol whose header is
        // literally `X-ToolUp-Worker-Signature`, it ships in a ToolUp
        // repo, and the branding is accurate rather than a leak. Only its
        // VERSION SUFFIX moved into the common scheme (it read
        // `toolup.signed-outcome.v1` before Phase 654) — the brand and the
        // path are untouched.
        | SignedShape.WorkerSignedOutcome -> {
            Vendor = "toolup"
            Path = [ "signed-outcome" ]
            Version = 1
          }
        // Branded `toolup` for the same reason `WorkerSignedOutcome`
        // is, and not by oversight: the countersignature registry is a
        // generic platform mechanism in `ToolUp.Platform.Server`, not a
        // federation wire shape. The four federation-branded separators
        // above name one specific cross-deployment protocol; these two
        // name a substrate any subject kind can be registered under.
        | SignedShape.CountersignatureSubject -> {
            Vendor = "toolup"
            Path = [ "countersignature"; "subject" ]
            Version = 1
          }
        | SignedShape.CountersignatureRecord -> {
            Vendor = "toolup"
            Path = [ "countersignature"; "record" ]
            Version = 1
          }

    /// The wire text opening this shape's canonical encoding. **This is
    /// the only sanctioned way to obtain a domain separator** — a module
    /// that writes one out again as a local literal has re-created the
    /// defect Phase 654 closed.
    let separator (shape: SignedShape) : string =
        SignedShapeSeparator.render (parts shape)