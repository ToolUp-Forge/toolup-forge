module ToolUp.Platform.Tests.InProcess.HostThemeTokensTests

open Expecto
open ToolUp.BrandKit

// ─── Phase 269 — brandkit → hosted-tree theme-token bridge tests ──────
//
// Pins the projection the phase promises:
//   * base brandkit values project onto the canonical primitive set
//     (blank / absent primitives omitted);
//   * a per-tenant palette override WINS over the base and adds
//     palette-only variables, and the projection is scope-isolated
//     (GP 4 — two tenants' bags are independent);
//   * a not-composed pipeline emits nothing (GP 13);
//   * the `:root` rendering is deterministic (sorted) so a Phase 197
//     visual snapshot of a hosted view is byte-stable.

let tests =
    testList "HostThemeTokens (Phase 269)" [

        testCase "base brandkit values project onto the canonical primitive set"
        <| fun _ ->
            let values =
                Map [
                    Tokens.AccentVar, "#2563eb"
                    Tokens.InkVar, "#111827"
                    Tokens.PaperVar, "  " // blank — must be omitted
                    "--not-a-brandkit-var", "#000" // not a primitive — must be omitted
                ]

            let tokens = HostThemeTokens.ofBrandKitValues values

            Expect.equal (tokens.Variables |> Map.tryFind Tokens.AccentVar) (Some "#2563eb") "accent projected"
            Expect.equal (tokens.Variables |> Map.tryFind Tokens.InkVar) (Some "#111827") "ink projected"
            Expect.isNone (tokens.Variables |> Map.tryFind Tokens.PaperVar) "a blank primitive value is omitted"
            Expect.isNone (tokens.Variables |> Map.tryFind "--not-a-brandkit-var") "a non-primitive var is omitted"
            Expect.equal tokens.Variables.Count 2 "only the two non-blank primitives project"

        testCase "a per-tenant palette override wins over the base and adds palette-only vars"
        <| fun _ ->
            let baseTokens =
                HostThemeTokens.ofBrandKitValues (Map [ Tokens.AccentVar, "#000000" ])

            // Phase 223 shape: (cssVarName, hexValue) list. Overrides the base
            // --bk-accent AND adds the palette-only --color-brand / --pos.
            let overrides = [
                Tokens.AccentVar, "#6d28d9" // overrides the base accent
                "--color-brand", "#6d28d9"
                "--pos", "#16a34a"
            ]

            let themed = baseTokens |> HostThemeTokens.withPaletteOverrides overrides

            Expect.equal
                (themed.Variables |> Map.tryFind Tokens.AccentVar)
                (Some "#6d28d9")
                "palette override wins over base"

            Expect.equal (themed.Variables |> Map.tryFind "--color-brand") (Some "#6d28d9") "palette-only var added"
            Expect.equal (themed.Variables |> Map.tryFind "--pos") (Some "#16a34a") "second palette-only var added"

        testCase "the projection is scope-isolated — one tenant's palette never leaks into another's (GP 4)"
        <| fun _ ->
            let baseTokens =
                HostThemeTokens.ofBrandKitValues (Map [ Tokens.AccentVar, "#000000" ])

            let tenantA =
                baseTokens
                |> HostThemeTokens.withPaletteOverrides [ "--color-brand", "#aa0000" ]

            let tenantB =
                baseTokens
                |> HostThemeTokens.withPaletteOverrides [ "--color-brand", "#0000bb" ]

            Expect.equal
                (tenantA.Variables |> Map.tryFind "--color-brand")
                (Some "#aa0000")
                "tenant A keeps its own palette"

            Expect.equal
                (tenantB.Variables |> Map.tryFind "--color-brand")
                (Some "#0000bb")
                "tenant B keeps its own palette"
            // The shared base is untouched by either projection.
            Expect.isNone
                (baseTokens.Variables |> Map.tryFind "--color-brand")
                "the base bag is never mutated by a projection"

        testCase "a blank palette override never clobbers the base"
        <| fun _ ->
            let baseTokens =
                HostThemeTokens.ofBrandKitValues (Map [ Tokens.AccentVar, "#123456" ])

            let themed =
                baseTokens |> HostThemeTokens.withPaletteOverrides [ Tokens.AccentVar, "   " ]

            Expect.equal
                (themed.Variables |> Map.tryFind Tokens.AccentVar)
                (Some "#123456")
                "a blank override leaves the base value intact"

        testCase "not-composed = no tokens emitted (GP 13)"
        <| fun _ ->
            Expect.isTrue (Map.isEmpty HostThemeTokens.empty.Variables) "empty bag has no variables"
            Expect.equal (HostThemeTokens.toRootCss HostThemeTokens.empty) "" "empty bag renders no :root block"
            Expect.isEmpty (HostThemeTokens.toDeclarations HostThemeTokens.empty) "empty bag has no declarations"

        testCase "the :root rendering is deterministic (sorted) for snapshot stability (Phase 197)"
        <| fun _ ->
            // Insert in a deliberately unsorted order; the output is sorted.
            let tokens =
                HostThemeTokens.empty
                |> HostThemeTokens.withPaletteOverrides [
                    "--pos", "#16a34a"
                    "--color-brand", "#6d28d9"
                    "--bk-accent", "#2563eb"
                ]

            let css = HostThemeTokens.toRootCss tokens

            Expect.equal
                css
                ":root { --bk-accent: #2563eb; --color-brand: #6d28d9; --pos: #16a34a; }"
                "declarations render in a stable sorted order regardless of insertion order"

            // Same inputs → identical bytes (the snapshot invariant).
            let cssAgain = HostThemeTokens.toRootCss tokens
            Expect.equal cssAgain css "the projection is byte-stable across calls"
    ]