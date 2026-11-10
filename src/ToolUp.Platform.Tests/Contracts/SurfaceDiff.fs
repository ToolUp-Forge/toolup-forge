// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The one grammar of a rendered public-API baseline, and the comparer
/// built on it.
///
/// `PublicApiApproval.renderSurface` writes the text; `parse` is the one
/// reader of it. Every consumer of the baseline text reads it here: the
/// approval gate (`compareSurface`), the release bump (`SemVerBump.fs`), the
/// generated changelog (`Changelog.fs`) and the documentation gates
/// (`DocGates.fs`, which hold doc blocks and source comments to the surface
/// the baselines render). A second parser would be a second idea of what the
/// surface is, and the two would disagree exactly where a rename made the
/// answer matter. `dev-scripts/merge-baselines.ps1` is the one reader that is
/// not F#; it is held to this grammar by the merge-driver round-trip check.
///
/// BCL-only and free of both Expecto and FAKE, so it is source-linked into
/// the root build driver and the Build test pack, neither of which can
/// reference `MetadataLoadContext` and so neither can compile
/// `PublicApiApproval.fs`.
module ToolUp.Platform.Tests.Contracts.SurfaceDiff

open System
open System.Text.RegularExpressions

// The F# compiler stopped emitting the legacy `(SerializationInfo,
// StreamingContext)` constructor on exception declarations in SDK
// 10.0.400 (BinaryFormatter retirement), so whether an assembly carries
// that member depends on which SDK feature band built it — global.json
// rolls forward across bands, and 2026-08-19 the hosted runners moved
// to 10.0.400 while dev machines still carried 10.0.300: eleven
// assemblies went red in CI on a surface no source change touched. The
// gate must be insensitive to which side of that compiler change built
// the DLLs, so the token is excluded from BOTH sides of the comparison
// (it sits in `significantLines`, the one tokeniser every direction
// shares). Baselines regenerated under either band stay green; the
// stale lines fall out of the approved files at the next regen.
//
// Public since Phase 489's gate fix, for the one reason a predicate like
// this ever goes public: Phase 261's doc-coverage ratchet walks the SAME
// `MetadataLoadContext` surface and must exclude the SAME subjects, and a
// second copy of this string literal is exactly the drift the header
// above argues against. It is a predicate over a rendered surface TOKEN,
// which is what both readers hold.
let isCompilerVersionDependent (l: string) =
    l.EndsWith "..ctor(System.Runtime.Serialization.SerializationInfo, System.Runtime.Serialization.StreamingContext)"

/// One member line under a rendered type.
type SurfaceMember = {
    /// The rendered text up to its first `(` or space: `send`, `.ctor`,
    /// `Body`. Generic arity is kept (`ask`2`).
    Name: string
    /// The line after `<type>.`, exactly as rendered.
    Rendered: string
    /// The line is the deprecation MARKER beside a member (`…  (obsolete)`),
    /// not a member of its own.
    Obsolete: bool
}

/// One type a baseline renders, with the member lines that hang off it.
type SurfaceType = {
    /// The full name; a nested type keeps its `+`.
    Full: string
    /// `class`, `interface`, `struct`, `enum` or `delegate`.
    Kind: string
    /// Every significant line starting `<Full>.`, in file order.
    Members: SurfaceMember list
}

/// A baseline's text, read once.
type Surface = {
    /// The tokens a comparison sees: non-blank, non-comment lines, minus the
    /// band-dependent constructor (`isCompilerVersionDependent`).
    Lines: string array
    /// The type headers (`<full> (<kind>)`), in file order.
    Types: SurfaceType list
    /// Types the renderer could not enumerate
    /// (`<full>  # <members unavailable: …>`). Unknown, never empty.
    Unreadable: Set<string>
}

let private typeHeader =
    Regex(@"^(\S+) \((class|interface|struct|enum|delegate)\)$", RegexOptions.Compiled)

let private unreadableLine =
    Regex(@"^(\S+)\s+# <members unavailable", RegexOptions.Compiled)

/// Read a baseline's text: its tokens, its types and the member lines under
/// each, and the types it could not enumerate. A line belongs to EVERY type
/// whose full name followed by `.` begins it, which is how a member line is
/// attributed whatever order the renderer sorted it into.
let parse (text: string) : Surface =
    let lines =
        text.Replace("\r\n", "\n").Split('\n')
        |> Array.map _.TrimEnd()
        |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && not (isCompilerVersionDependent l))

    let headers =
        lines
        |> Array.choose (fun l ->
            let m = typeHeader.Match l

            if m.Success then
                Some(m.Groups[1].Value, m.Groups[2].Value)
            else
                None)

    let known = headers |> Array.map fst |> Set.ofArray

    let byOwner =
        lines
        |> Seq.collect (fun l -> seq {
            for i in 1 .. l.Length - 1 do
                if l[i] = '.' && known.Contains(l.Substring(0, i)) then
                    let rendered = l.Substring(i + 1)
                    let cut = rendered.IndexOfAny [| '('; ' ' |]

                    yield
                        l.Substring(0, i),
                        {
                            Name = (if cut >= 0 then rendered.Substring(0, cut) else rendered)
                            Rendered = rendered
                            Obsolete = rendered.EndsWith "  (obsolete)"
                        }
        })
        |> Seq.groupBy fst
        |> Seq.map (fun (owner, ms) -> owner, ms |> Seq.map snd |> List.ofSeq)
        |> Map.ofSeq

    {
        Lines = lines
        Types =
            headers
            |> Array.map (fun (full, kind) -> {
                Full = full
                Kind = kind
                Members = byOwner.TryFind full |> Option.defaultValue []
            })
            |> List.ofArray
        Unreadable =
            lines
            |> Array.choose (fun l ->
                let m = unreadableLine.Match l
                if m.Success then Some m.Groups[1].Value else None)
            |> Set.ofArray
    }

/// The tokens a comparison sees (`Surface.Lines`). Public because it is the
/// answer to "does this baseline text carry any tracked surface at all",
/// which the documentation-coverage gate asks of a baseline before grading it.
let significantLines (text: string) = (parse text).Lines

/// Significant tokens of `candidates` that `present` does not carry.
/// Both directions of the comparison are this same set difference — which
/// is why neither arm can drift from the other in how it tokenises.
let private missingFrom (present: Surface) (candidates: Surface) : string list =
    let presentSet = present.Lines |> Set.ofArray

    candidates.Lines
    |> Array.filter (fun l -> not (presentSet.Contains l))
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofArray

/// Tokens present in `baseline` but absent from `current` — i.e. removed,
/// renamed, or retyped public members. A non-empty result is a BREAKING
/// diff.
let removedMembers (baseline: string) (current: string) : string list =
    missingFrom (parse current) (parse baseline)

/// Tokens present in `current` but absent from `baseline` — i.e. new public
/// types, members, or overloads. Non-breaking, but UNFOLDED: the baseline
/// must be regenerated and committed with the change (Phase 618).
let addedMembers (baseline: string) (current: string) : string list =
    missingFrom (parse baseline) (parse current)

/// The two-directional comparison. `Removed` is breaking; `Added` is
/// non-breaking but unfolded. Both fail the gate — see the drift-policy
/// note in this file's header.
type SurfaceDrift = {
    Removed: string list
    Added: string list
}

[<RequireQualifiedAccess>]
module SurfaceDrift =
    let isClean (d: SurfaceDrift) =
        List.isEmpty d.Removed && List.isEmpty d.Added

let compareSurface (baseline: string) (current: string) : SurfaceDrift =
    let b = parse baseline
    let c = parse current

    {
        Removed = missingFrom c b
        Added = missingFrom b c
    }