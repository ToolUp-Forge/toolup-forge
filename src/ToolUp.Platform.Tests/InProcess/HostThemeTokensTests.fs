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
//     visual snapshot of a hosted view is byte-stable;
//   * a bridged brand token also sets the Fuaran-UI `--fuaran-*` tokens it
//     answers, and `client-styling/fuaran-bridge.css` bridges the same shell
//     tokens as `HostThemeTokens.fuaranBridge`.

open System.IO
open System.Reflection
open System.Text.RegularExpressions

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

            Expect.equal
                (tokens.Variables |> Map.toList |> List.map fst |> Set.ofList)
                (set [
                    Tokens.AccentVar
                    Tokens.InkVar
                    "--fuaran-tone-brand-fg"
                    "--fuaran-tone-default-fg"
                ])
                "only the two non-blank primitives project, each with the tree token it answers"

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
                ":root { --bk-accent: #2563eb; --color-brand: #6d28d9; --fuaran-tone-brand-fg: #2563eb; --fuaran-tone-success-fg: #16a34a; --pos: #16a34a; }"
                "declarations render in a stable sorted order regardless of insertion order"

            // Same inputs → identical bytes (the snapshot invariant).
            let cssAgain = HostThemeTokens.toRootCss tokens
            Expect.equal cssAgain css "the projection is byte-stable across calls"

        testCase "a tenant palette that sets the --bk-* tokens also sets the tree tokens they answer"
        <| fun _ ->
            let themed =
                HostThemeTokens.ofBrandKitValues (Map [ Tokens.AccentVar, "#000000"; Tokens.RadiusMdVar, "2px" ])
                |> HostThemeTokens.withPaletteOverrides [ Tokens.AccentVar, "#6d28d9"; Tokens.PaperVar, "#fafafa" ]

            let value name = themed.Variables |> Map.tryFind name

            Expect.equal
                (value "--fuaran-tone-brand-fg")
                (Some "#6d28d9")
                "the palette's accent reaches the tree's brand"

            Expect.equal
                (value "--fuaran-tone-default-bg")
                (Some "#fafafa")
                "the palette's paper reaches the tree's surface"

            Expect.equal (value "--fuaran-radius-sm") (Some "2px") "the base radius reaches the tree's small radius"
            Expect.equal (value "--fuaran-radius-md") (Some "2px") "and its medium radius"

        testCase "a --fuaran-* override named by the palette wins over one bridged from it"
        <| fun _ ->
            let themed =
                HostThemeTokens.empty
                |> HostThemeTokens.withPaletteOverrides [
                    "--fuaran-tone-brand-fg", "#111111"
                    "--color-brand", "#6d28d9"
                ]

            Expect.equal
                (themed.Variables |> Map.tryFind "--fuaran-tone-brand-fg")
                (Some "#111111")
                "the explicit tree token is not replaced by the bridged one"

        testCase "every bridge row maps a brand or shell token onto --fuaran-* tokens"
        <| fun _ ->
            for source, targets in HostThemeTokens.fuaranBridge do
                Expect.isNonEmpty targets $"{source} bridges at least one token"

                for target in targets do
                    Expect.stringStarts target "--fuaran-" $"{source} bridges onto a renderer token, not {target}"

            let bkSources =
                HostThemeTokens.fuaranBridge
                |> List.map fst
                |> List.filter (fun s -> s.StartsWith "--bk-")

            for source in bkSources do
                Expect.contains HostThemeTokens.brandKitVars source $"{source} is a BrandKit primitive"

        testCase "client-styling/fuaran-bridge.css declares exactly the table's shell rows"
        <| fun _ ->
            // bin/<Config>/net10.0/<pack>.dll -> up five is the repository root.
            let repoRoot =
                Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "..",
                        "..",
                        "..",
                        "..",
                        ".."
                    )
                )

            let bridgeFile = Path.Combine(repoRoot, "client-styling/fuaran-bridge.css")

            let css = File.ReadAllText bridgeFile

            // The plain `--fuaran-x: var(--shell);` lines; the derived lines
            // (color-mix, calc, another --fuaran-* token) are the file's own.
            let declared =
                Regex.Matches(css, @"(--fuaran-[a-z0-9-]+)\s*:\s*var\((--[a-z0-9-]+)\)\s*;")
                |> Seq.map (fun m -> m.Groups[2].Value, m.Groups[1].Value)
                |> Seq.filter (fun (source, _) -> not (source.StartsWith "--fuaran-"))
                |> Set.ofSeq

            let table =
                HostThemeTokens.fuaranBridge
                |> List.filter (fun (source, _) -> not (source.StartsWith "--bk-"))
                |> List.collect (fun (source, targets) -> targets |> List.map (fun t -> source, t))
                |> Set.ofList

            Expect.isNonEmpty declared $"the bridge file declares shell bridges: {bridgeFile}"
            Expect.equal declared table "the stylesheet and HostThemeTokens.fuaranBridge bridge the same shell tokens"
    ]