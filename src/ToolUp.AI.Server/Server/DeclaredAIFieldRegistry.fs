// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

// ─── An `IAIFieldRegistry` a composition root fills ───────────────
//
// `IAIFieldRegistry` is the one place Tier-3 triage learns what a
// surface exposes, and the fields it describes are the SAME
// declarations the deterministic tier matches on (one source for the
// tiers). Until now the seam shipped with no implementation a
// deployment could fill: each had to write a registry of its own. This
// one is filled from data — `AIDeclaredSurface` values, one per
// module/page, each a list of `AIFieldDescriptor`s — so a composition
// root that knows its views' affordances states them once and hands
// them over:
//
//   let registry = DeclaredAIFieldRegistry(surfaces)
//   FastPathTriageConfig.create (registry :> IAIFieldRegistry)
//
// Keyed by module and page, compared case-insensitively (ids reach the
// registry from a route and an active-module signal, not from a
// compiler). A page that was never declared answers an EMPTY snapshot,
// never an exception and never another page's fields: triage then
// falls through to the full agent loop, which is the correct answer
// for a surface nothing is known about.
//
// Several declarations of one module/page (one per view on the page,
// say) are one surface: their fields concatenate in order, and a
// repeated field id keeps its first declaration.
//
// Immutable after construction and stateless between calls (GP 12).

/// A registry answering from a fixed set of declared surfaces.
type DeclaredAIFieldRegistry(surfaces: AIDeclaredSurface seq) =
    // Case-insensitive, whitespace-trimmed identity. `None` and a blank
    // page are the same page: the page-less surface.
    static let norm (s: string) : string =
        if isNull s then "" else s.Trim().ToUpperInvariant()

    static let keyOf (moduleId: string) (page: string option) : string * string =
        norm moduleId, page |> Option.map norm |> Option.defaultValue ""

    let declared: Map<string * string, AIFieldSnapshot> =
        surfaces
        |> Seq.filter (fun s -> not (obj.ReferenceEquals(s, null)))
        |> Seq.groupBy (fun s -> keyOf s.ModuleId s.Page)
        |> Seq.map (fun (key, group) ->
            let first = Seq.head group

            let fields =
                group
                |> Seq.collect (fun s -> s.Fields)
                |> Seq.distinctBy (fun f -> norm f.FieldId)
                |> List.ofSeq

            key,
            {
                AIFieldSnapshot.empty first.ModuleId with
                    Page = first.Page
                    Fields = fields
            })
        |> Map.ofSeq

    interface IAIFieldRegistry with
        member _.Describe(moduleId, page) = async {
            match Map.tryFind (keyOf moduleId page) declared with
            | Some snapshot -> return Some snapshot
            | None ->
                return
                    Some {
                        AIFieldSnapshot.empty moduleId with
                            Page = page
                    }
        }