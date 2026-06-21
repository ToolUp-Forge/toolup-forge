// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Resolved per-team app-chrome branding (Phase 5e). The shell renders
/// the app name + logo from this record and applies the favicon +
/// primary colour to the document. Values are sourced from the
/// prefetched `_platform` config map (the four `ConfigKeys.BrandingKeys`
/// fields), each falling back to the composition root's `ClientConfig`
/// default when the active team has set no override — so a single-tenant
/// deployment, or a team that customises nothing, renders byte-for-byte
/// as before.
type Branding = {
    AppName: string
    PrimaryColor: string
    LogoUrl: string
    FaviconUrl: string
    /// Phase 223 — the team's EXPLICIT, hex-validated palette overrides as
    /// `(cssVariableName, hexValue)` pairs (e.g. `("--color-brand", "#6d28d9")`).
    /// Only colours the team actually set appear here; absent ones are omitted
    /// so the deployment's base theme shows through (the shell removes any
    /// `:root` property not in this list on team switch). Allow-list is colours
    /// only — there is no field through which a team could set a font or a
    /// component shape.
    PaletteOverrides: (string * string) list
}

module Branding =
    /// Neutral primary-colour default. `ClientConfig` carries no brand
    /// colour today, so the composition root supplies this as the
    /// `PrimaryColor` fallback. Exposed so the client provider's
    /// out-of-context fallback and the shell's default record agree.
    [<Literal>]
    let DefaultPrimaryColor = "#2563eb"

    /// `true` for a `#RGB` / `#RRGGBB` hex colour. Hand-rolled rather
    /// than via `System.Uri`/regex so it compiles identically under
    /// Fable and .NET (Phase 9c platform-parity).
    let internal isHexColour (value: string) : bool =
        let isHexDigit c =
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')

        value.StartsWith "#"
        && (value.Length = 4 || value.Length = 7)
        && value.[1..] |> Seq.forall isHexDigit

    /// Phase 223 — the allow-listed per-team palette: each `_platform` config
    /// key paired with the `:root` CSS custom property it drives. Colours only;
    /// `PrimaryColor` drives both the shell brand token and the legacy
    /// `--brand-primary` (Phase 5e back-compat). The client-toolkit theming
    /// tokens (`--pos` / `--neg`) and shell tokens (`--color-brand` /
    /// `-brand-dark` / `-sidebar`) are exactly the surface this re-skins.
    let paletteVarMap: (string * string) list = [
        ConfigKeys.BrandingKeys.PrimaryColor, "--color-brand"
        ConfigKeys.BrandingKeys.PrimaryColor, "--brand-primary"
        ConfigKeys.BrandingKeys.BrandDarkColor, "--color-brand-dark"
        ConfigKeys.BrandingKeys.SidebarColor, "--color-sidebar"
        ConfigKeys.BrandingKeys.PosColor, "--pos"
        ConfigKeys.BrandingKeys.NegColor, "--neg"
    ]

    /// Every `:root` property the per-team palette may set — the shell sets the
    /// overridden ones and removes the rest on each team switch.
    let paletteCssVars: string list = paletteVarMap |> List.map snd |> List.distinct

    /// The team's explicit, hex-validated palette overrides (Phase 223). A key
    /// contributes only when present, non-blank AND a valid hex colour, so a
    /// malformed or absent value leaves the deployment's base theme untouched
    /// (never clobbers it with a default).
    let paletteOverrides (config: Map<string, string>) : (string * string) list =
        paletteVarMap
        |> List.choose (fun (key, cssVar) ->
            match config |> Map.tryFind key with
            | Some v when v.Trim() <> "" && isHexColour (v.Trim()) -> Some(cssVar, v.Trim())
            | _ -> None)

    /// Resolve effective branding from a `_platform` config map against
    /// a default record. A field is taken from config only when present
    /// AND non-blank (a stored empty string means "use the deployment
    /// default", matching the schema's blank `DefaultJson`). `PrimaryColor`
    /// is additionally validated as a hex colour — a malformed stored
    /// value degrades to the default rather than emitting an invalid CSS
    /// custom property.
    let resolve (defaults: Branding) (config: Map<string, string>) : Branding =
        let pick key fallback =
            match config |> Map.tryFind key with
            | Some v when v.Trim() <> "" -> v.Trim()
            | _ -> fallback

        let pickColour key fallback =
            let v = pick key fallback
            if isHexColour v then v else fallback

        {
            AppName = pick ConfigKeys.BrandingKeys.AppName defaults.AppName
            PrimaryColor = pickColour ConfigKeys.BrandingKeys.PrimaryColor defaults.PrimaryColor
            LogoUrl = pick ConfigKeys.BrandingKeys.LogoUrl defaults.LogoUrl
            FaviconUrl = pick ConfigKeys.BrandingKeys.FaviconUrl defaults.FaviconUrl
            PaletteOverrides = paletteOverrides config
        }