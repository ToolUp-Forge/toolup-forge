// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open Fuaran.Facts

// ─── Fact model ──────────────────────────────────────────────────────
//
// The Fact kernel is the published `Fuaran.Facts` package: `Fact`,
// `FactValue`, `MethodRef`, `Disclosure`, `SubjectRef`, `MetricRef`,
// `TemporalExtent`, `Evidence`, `Confidence`, the identity frame
// (`Fact.compute` / `computeFor`), the lineage key and digest, the value
// hash, `InputHash`, supersession and as-of visibility (`History`),
// `Freshness`, the codec (`FactWire`, `LegacyFact`) and the rendering
// standard (`FactRendering`). Forge uses those types and laws directly.
//
// This file keeps only what is forge's own: the string forms of a
// disclosure stance its audit trail and signed artefacts carry, the
// conservative floor its egress gates take, the declared-method evidence
// token, the lineage digest of a key held as parts, and the store codec
// (the specification's encoding written, the legacy saved shape read).

/// Rendering for a `Disclosure` — the canonical string form
/// (`Surfaceable` / `Internal` / `Restricted(policy)`) shared by the audit
/// trail and the provenance/evidence surfaces.
module Disclosure =
    let toString (d: Disclosure) : string =
        match d with
        | Surfaceable -> "Surfaceable"
        | Internal -> "Internal"
        | Restricted policyRef -> sprintf "Restricted(%s)" policyRef

    /// The exact inverse of `toString`. `None` for anything this build does
    /// not recognise — a stance it cannot read is never silently defaulted,
    /// because every default is either a widening (unsafe) or a narrowing
    /// that misreports what the source actually said. The caller refuses.
    ///
    /// Lives beside `toString` deliberately: a stance rendered into a
    /// signed artefact by one deployment is read back by another, and a
    /// parser written at the reading end would be a second definition of
    /// the wire shape, free to drift from the one that writes it.
    let tryParse (s: string) : Disclosure option =
        if isNull s then
            None
        else
            match s.Trim() with
            | "Surfaceable" -> Some Surfaceable
            | "Internal" -> Some Internal
            | t when
                t.StartsWith("Restricted(", StringComparison.Ordinal)
                && t.EndsWith(")", StringComparison.Ordinal)
                ->
                // A policy ref may itself contain parentheses; take
                // everything between the FIRST '(' and the LAST ')'.
                Some(Restricted(t.Substring(11, t.Length - 12)))
            | _ -> None

    /// The **conservative floor** (the meet) of two stances — the more
    /// restrictive of the two, under the Phase 525 egress predicate's own
    /// ordering:
    ///
    ///   `Internal`  ≤  `Restricted p`  ≤  `Surfaceable`
    ///
    /// `Internal` is the bottom because it is denied at every surface
    /// unconditionally, while a `Restricted` policy MAY permit some
    /// surface; `Surfaceable` is the top because it is permitted at every
    /// surface subject only to composition policy above.
    ///
    /// **Two `Restricted` stances under DIFFERENT policy refs meet at
    /// `Internal`**, not at either ref. They are incomparable: nothing
    /// tells this deployment that satisfying one policy satisfies the
    /// other, and picking either would assert a permission neither source
    /// granted. Collapsing to the bottom is the only answer that cannot
    /// widen — and because the choice is recorded wherever the meet is
    /// taken, it is a visible narrowing rather than a silent one.
    ///
    /// Commutative, associative, idempotent, with `Surfaceable` as the
    /// identity — so a boundary that imposes no ceiling of its own is
    /// byte-for-byte the stance it was handed (GP 11).
    let floor (a: Disclosure) (b: Disclosure) : Disclosure =
        match a, b with
        | Internal, _
        | _, Internal -> Internal
        | Surfaceable, other
        | other, Surfaceable -> other
        | Restricted p, Restricted q when p = q -> Restricted p
        | Restricted _, Restricted _ -> Internal


/// The lineage of a fact named by its parts rather than by a fact: what a
/// store looks a lineage up by before it holds any fact of it.
[<RequireQualifiedAccess>]
module FactLineage =

    let private probe (subject: SubjectRef) (metric: MetricRef) (period: TemporalExtent) (method: MethodRef) : Fact = {
        FactId = ""
        Subject = subject
        Metric = metric
        Value = Absent ""
        Period = period
        AsOf = period.From
        Method = method
        Evidence = {
            ResultRef = None
            InputHashes = []
            TriggerRef = None
        }
        Confidence = None
        Supersedes = None
        Disclosure = Internal
    }

    /// The lineage key (`Fact.lineageKey`) of the lineage the four parts
    /// name. The key reads only these four members of a fact, so the probe
    /// fact's other members are placeholders the rule never reads.
    let keyOf (subject: SubjectRef) (metric: MetricRef) (period: TemporalExtent) (method: MethodRef) : string =
        Fact.lineageKey (probe subject metric period method)

    /// The lineage digest (`Fact.lineageDigest`) of the lineage the four
    /// parts name: what a store indexes the lineage by.
    let digestOf (subject: SubjectRef) (metric: MetricRef) (period: TemporalExtent) (method: MethodRef) : string =
        Fact.lineageDigest (probe subject metric period method)

/// How a store reads back a fact it persisted. A store writes the
/// specification's encoding (`FactWire.writeFact`) and nothing else; a fact
/// persisted before forge adopted the kernel is in the legacy saved shape,
/// which `LegacyFact.read` reads and holds to every rule the wire reader
/// applies (its id is verified, never trusted). Stored facts are never
/// rewritten: a store converts a fact to the specification's encoding only
/// when it next writes one.
[<RequireQualifiedAccess>]
module FactStoreCodec =

    /// The fact a stored text holds — the specification's encoding, or the
    /// legacy saved shape — or the refusal of the reader whose shape the text
    /// is in.
    let read (text: string) : Result<Fact, Refusal> =
        match FactWire.readFactText text with
        | Ok fact -> Ok fact
        | Error wireRefusal ->
            match LegacyFact.read text with
            | Ok fact -> Ok fact
            | Error legacyRefusal ->
                if not (isNull text) && text.Contains "\"FactId\"" then
                    Error legacyRefusal
                else
                    Error wireRefusal

/// The method a derived table's run declares it was produced by (Phase
/// 1040): the derivation's stable id and the version of it that ran — as in
/// `dailytrade.derive` at `v1`. Where `Evidence.InputHashes` names WHAT a fact
/// was computed from, this names HOW.
type DeclaredMethod = {
    /// The method's stable id.
    MethodId: string
    /// The version of the method that ran.
    MethodVersion: string
}

/// The form a declared method takes in a fact's evidence (Phase 1040).
///
/// A run that declares a method stamps every fact it writes with one token,
/// `method:<id>@<version>`, among the fact's input hashes. That placement is
/// deliberate: it puts the method in the fact's content address — so a run by
/// another method mints a new fact rather than an idempotent skip — while
/// leaving it OUT of the lineage key (`Fact.lineageKey`), so the facts two
/// methods write for one table, subject and period are one lineage and the
/// later supersedes the earlier. The method is evidence, never identity. The
/// token is never in the `InputHash` form (it carries an `@`), so
/// `InputHash.named` never mistakes it for an input.
module DeclaredMethod =

    /// The prefix of a declared method's evidence token.
    [<Literal>]
    let TokenPrefix = "method:"

    let private wellFormed (part: string) =
        not (String.IsNullOrEmpty part)
        && part |> Seq.forall (fun c -> not (Char.IsWhiteSpace c || Char.IsControl c))

    /// A declared method from its id and version.
    let create (methodId: string) (methodVersion: string) : DeclaredMethod = {
        MethodId = methodId
        MethodVersion = methodVersion
    }

    /// Why a method cannot be declared, or `None` when it can: its id and
    /// version are non-empty, free of whitespace and control characters, and
    /// its id carries no `@` (the token's separator).
    let problem (m: DeclaredMethod) : string option =
        if isNull (box m) then
            Some "the method is null"
        elif not (wellFormed m.MethodId) || m.MethodId.Contains '@' then
            Some(
                sprintf
                    "method id '%s' must be non-empty, without whitespace, control characters or '@'"
                    (if isNull m.MethodId then "" else m.MethodId)
            )
        elif not (wellFormed m.MethodVersion) then
            Some(
                sprintf
                    "method version '%s' must be non-empty, without whitespace or control characters"
                    (if isNull m.MethodVersion then "" else m.MethodVersion)
            )
        else
            None

    /// The evidence token of a method: `method:<id>@<version>`.
    let token (m: DeclaredMethod) : string =
        sprintf "%s%s@%s" TokenPrefix m.MethodId m.MethodVersion

    /// The method a token names, or `None` for anything else.
    let tryParse (value: string) : DeclaredMethod option =
        if isNull value || not (value.StartsWith(TokenPrefix, StringComparison.Ordinal)) then
            None
        else
            let rest = value.Substring TokenPrefix.Length

            match rest.IndexOf '@' with
            | at when at > 0 ->
                let m = create (rest.Substring(0, at)) (rest.Substring(at + 1))
                if (problem m).IsNone then Some m else None
            | _ -> None

    /// The method a fact's evidence names, or `None` when it names none — a
    /// fact written by a run that declared no method.
    let ofEvidence (evidence: Evidence) : DeclaredMethod option =
        evidence.InputHashes |> List.tryPick tryParse

    /// The readable form a citation shows: `<id> <version>`.
    let describe (m: DeclaredMethod) : string =
        sprintf "%s %s" m.MethodId m.MethodVersion