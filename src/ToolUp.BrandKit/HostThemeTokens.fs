// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.BrandKit

// ─── Phase 269 — brandkit → hosted-tree theme-token bridge ────────────
//
// A Phase 110-hosted typed-tree resolves its own theme tokens with no
// bridge to the deployment's brand, so a hosted view ignores the tenant's
// palette entirely. `HostThemeTokens` closes that gap: a neutral projection
// that flows the deployment's brandkit primitive values (the `Tokens.fs`
// `--bk-*` custom-property set) + the per-tenant palette overrides
// (Phase 223) into ONE CSS-variable token bag a hosted renderer emits as
// `:root` variables — so a single deployment theme drives both Feliz modules
// and hosted-tree modules.
//
// **Neutral by construction.** The token bag is a plain `Map<string,string>`
// of CSS-variable name → value; it names no tree language and carries no
// forge-private vocabulary. The gated Layer-2 binding maps these tokens onto
// its renderer's theme record in its own staging location.
//
// **Scope-bound (GP 4).** The projection is a PURE per-call function over the
// values + overrides the caller supplies for one scope. Two tenants' bags are
// built by two independent calls over two immutable inputs, so a tenant's
// palette can never leak into another's — there is no shared mutable state to
// leak through.
//
// **Zero-cost when not composed (GP 13).** A pipeline that wires no theme
// bridge emits `HostThemeTokens.empty` — no variables, no `:root` block.
//
// **Decoupled from Core.** `HostThemeTokens` takes per-tenant palette
// overrides as a plain `(name, value) list` — exactly the shape
// `ToolUp.Platform.Branding.PaletteOverrides` already carries — so this
// package keeps its minimal `Giraffe.ViewEngine`-only dependency graph and
// never pulls `ToolUp.Platform.Core`.

/// A neutral bag of CSS custom-property assignments a hosted renderer emits
/// as `:root` variables. Keyed by variable name (e.g. `"--bk-accent"`,
/// `"--color-brand"`); values are the resolved brand values. Immutable — a
/// projection produces a new bag, never mutates one.
type HostThemeTokens = {
    /// CSS-variable name → resolved value. Empty = nothing to emit (GP 13).
    Variables: Map<string, string>
}

[<RequireQualifiedAccess>]
module HostThemeTokens =

    /// The empty token bag — what a deployment that wires no theme bridge
    /// projects. A hosted renderer emits no `:root` variables (GP 13).
    let empty: HostThemeTokens = { Variables = Map.empty }

    /// The canonical BrandKit primitive custom-property names (from
    /// `Tokens.fs`), in a stable order. The base of every projection — a
    /// deployment supplies values for the subset it themes; unset primitives
    /// are simply omitted so the renderer falls back to its own defaults.
    let brandKitVars: string list = [
        Tokens.FontDisplayVar
        Tokens.FontUiVar
        Tokens.FontMonoVar
        Tokens.InkVar
        Tokens.InkMuteVar
        Tokens.PaperVar
        Tokens.PanelVar
        Tokens.RuleVar
        Tokens.AccentVar
        Tokens.OnDarkTextVar
        Tokens.PositiveVar
        Tokens.PriorityVar
        Tokens.InfoVar
        Tokens.RadiusMdVar
        Tokens.RadiusLgVar
        Tokens.ShadowCardVar
    ]

    /// Project the base brandkit token values onto the canonical primitive
    /// set. Only a primitive with a non-blank supplied value is emitted, so a
    /// deployment that themes three tokens produces a three-entry bag (a
    /// hosted renderer falls back to its own defaults for the rest).
    let ofBrandKitValues (values: Map<string, string>) : HostThemeTokens =
        let vars =
            brandKitVars
            |> List.choose (fun name ->
                match values |> Map.tryFind name with
                | Some v when v.Trim() <> "" -> Some(name, v.Trim())
                | _ -> None)
            |> Map.ofList

        { Variables = vars }

    /// Layer the Phase 223 per-tenant palette over a base token bag. A
    /// palette override (`(cssVarName, value)`) WINS over any base value for
    /// the same variable, and adds palette-only variables (e.g.
    /// `--color-brand` / `--pos` / `--neg`) the base brandkit set doesn't
    /// carry. Scope-bound: the overrides are the caller's own resolved
    /// per-tenant list, so the result is a fresh immutable bag for that scope
    /// (GP 4). A blank value is ignored (never clobbers the base with an
    /// empty string).
    let withPaletteOverrides (overrides: (string * string) list) (tokens: HostThemeTokens) : HostThemeTokens =
        let merged =
            overrides
            |> List.fold
                (fun (acc: Map<string, string>) (name, value) ->
                    if not (System.String.IsNullOrWhiteSpace value) then
                        acc |> Map.add name (value.Trim())
                    else
                        acc)
                tokens.Variables

        { Variables = merged }

    /// The `--name: value;` declaration list for the bag, sorted by variable
    /// name for DETERMINISTIC output — the property order is stable across
    /// runs, so a Phase 197 visual-snapshot of a hosted view is byte-stable
    /// and a brandkit change to the view is caught by the existing snapshot
    /// gate. Empty bag → empty list.
    let toDeclarations (tokens: HostThemeTokens) : string list =
        tokens.Variables
        |> Map.toList
        |> List.sortBy fst
        |> List.map (fun (name, value) -> sprintf "%s: %s;" name value)

    /// Render the bag as a `:root { … }` CSS block a hosted renderer injects
    /// so its tree paints with the deployment's brand. Deterministic (sorted)
    /// for snapshot stability. An empty bag renders `""` — no block, nothing
    /// emitted (GP 13).
    let toRootCss (tokens: HostThemeTokens) : string =
        match toDeclarations tokens with
        | [] -> ""
        | decls -> sprintf ":root { %s }" (String.concat " " decls)