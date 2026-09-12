// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The pure baseline comparer shared by the public-API approval gate and
/// the release-time SemVer-bump check.
///
/// **Extracted by Phase 260, unchanged.** Every line below was Phase
/// 175's, as hardened by Phase 618 (both directions fail) and left
/// exception-free by Phase 258 (an `[<Obsolete>]` marker is an ADDED
/// line, so it classifies additive with no special case). The move is
/// behaviour-preserving; `PublicApiApproval` opens this module and its
/// callers are unchanged.
///
/// **Why it is its own file.** Phase 260 computes the release bump from
/// the same set difference, and its module (`SemVerBump.fs`, repo root)
/// is compiled into `Build.fsproj` and into the Build test pack — neither
/// of which can reference `MetadataLoadContext`, so neither can compile
/// `PublicApiApproval.fs`. The alternative was a second tokeniser, and a
/// second tokeniser here is not a style question: `isCompilerVersionDependent`
/// below excludes a token whose presence depends on which SDK feature
/// band built the DLL, and a copy that forgot it would read an ordinary
/// band difference as ELEVEN removed members — i.e. would demand a
/// breaking bump for a change nobody made. That exact skew reddened CI on
/// 2026-08-19. One tokeniser, three readers.
///
/// BCL-only and free of both Expecto and FAKE, so it can be source-linked
/// wherever the rule is needed.
module ToolUp.Platform.Tests.Contracts.SurfaceDiff

open System

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
let private isCompilerVersionDependent (l: string) =
    l.EndsWith "..ctor(System.Runtime.Serialization.SerializationInfo, System.Runtime.Serialization.StreamingContext)"

let private significantLines (text: string) =
    text.Replace("\r\n", "\n").Split('\n')
    |> Array.map _.TrimEnd()
    |> Array.filter (fun l -> l <> "" && not (l.StartsWith "#") && not (isCompilerVersionDependent l))

/// Significant tokens of `candidates` that `present` does not carry.
/// Both directions of the comparison are this same set difference — which
/// is why neither arm can drift from the other in how it tokenises.
let private missingFrom (present: string) (candidates: string) : string list =
    let presentSet = significantLines present |> Set.ofArray

    significantLines candidates
    |> Array.filter (fun l -> not (presentSet.Contains l))
    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
    |> List.ofArray

/// Tokens present in `baseline` but absent from `current` — i.e. removed,
/// renamed, or retyped public members. A non-empty result is a BREAKING
/// diff.
let removedMembers (baseline: string) (current: string) : string list = missingFrom current baseline

/// Tokens present in `current` but absent from `baseline` — i.e. new public
/// types, members, or overloads. Non-breaking, but UNFOLDED: the baseline
/// must be regenerated and committed with the change (Phase 618).
let addedMembers (baseline: string) (current: string) : string list = missingFrom baseline current

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

let compareSurface (baseline: string) (current: string) : SurfaceDrift = {
    Removed = removedMembers baseline current
    Added = addedMembers baseline current
}