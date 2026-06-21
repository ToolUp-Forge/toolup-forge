module ToolUp.Platform.Tests.InProcess.BrandingTests

open Expecto
open ToolUp.Platform

/// Phase 5e — per-tenant branding. Pins the two pure pieces the feature
/// rests on: `Branding.resolve` (the client-side blank-fallback + hex-
/// validation logic) and `PlatformSchema.mergeBrandingSchema` (the
/// server-side merge onto the reserved `_platform` schema).

let private defaults: Branding = {
    AppName = "Default App"
    PrimaryColor = "#000000"
    LogoUrl = "default-logo.png"
    FaviconUrl = "default-fav.png"
}

/// A minimal app-supplied `_platform` entry carrying one field, used to
/// exercise the merge-into-existing branch.
let private platformWith (fields: ConfigFieldSchema list) : ModuleConfigEntry = {
    ModuleKey = ConfigKeys.PlatformModuleKey
    DisplayName = "Platform Defaults"
    Schema = { Fields = fields }
}

let private field (key: string) : ConfigFieldSchema = {
    Key = key
    DisplayName = key
    Description = None
    Kind = ConfigFieldKind.String(Some 60)
    Required = false
    DefaultJson = "\"\""
}

let private brandingKeys (entry: ModuleConfigEntry) : Set<string> =
    entry.Schema.Fields |> List.map _.Key |> Set.ofList

let private platformEntry (entries: ModuleConfigEntry list) : ModuleConfigEntry =
    entries |> List.find (fun e -> e.ModuleKey = ConfigKeys.PlatformModuleKey)

let tests =
    testList "Phase 5e — per-tenant branding" [

        // ─── Branding.resolve — fallback ──────────────────────────

        testCase "empty config falls back to every default"
        <| fun _ ->
            let resolved = Branding.resolve defaults Map.empty
            Expect.equal resolved defaults "no overrides → defaults verbatim"

        testCase "a present non-blank field overrides; others stay default"
        <| fun _ ->
            let resolved =
                Branding.resolve defaults (Map.ofList [ ConfigKeys.BrandingKeys.AppName, "Acme" ])

            Expect.equal resolved.AppName "Acme" "appName taken from config"
            Expect.equal resolved.LogoUrl defaults.LogoUrl "logoUrl stays default"
            Expect.equal resolved.FaviconUrl defaults.FaviconUrl "faviconUrl stays default"

        testCase "blank / whitespace value falls back to the default"
        <| fun _ ->
            let resolved =
                Branding.resolve
                    defaults
                    (Map.ofList [ ConfigKeys.BrandingKeys.AppName, ""; ConfigKeys.BrandingKeys.LogoUrl, "   " ])

            Expect.equal resolved.AppName defaults.AppName "blank appName → default"
            Expect.equal resolved.LogoUrl defaults.LogoUrl "whitespace logoUrl → default"

        testCase "surrounding whitespace is trimmed from a kept value"
        <| fun _ ->
            let resolved =
                Branding.resolve defaults (Map.ofList [ ConfigKeys.BrandingKeys.AppName, "  Acme  " ])

            Expect.equal resolved.AppName "Acme" "value trimmed"

        // ─── Branding.resolve — primaryColor hex validation ──────

        testCase "valid #RRGGBB and #RGB primary colours are accepted"
        <| fun _ ->
            let six =
                Branding.resolve defaults (Map.ofList [ ConfigKeys.BrandingKeys.PrimaryColor, "#aabbcc" ])

            let three =
                Branding.resolve defaults (Map.ofList [ ConfigKeys.BrandingKeys.PrimaryColor, "#0Af" ])

            Expect.equal six.PrimaryColor "#aabbcc" "#RRGGBB accepted"
            Expect.equal three.PrimaryColor "#0Af" "#RGB accepted (case-insensitive)"

        testCase "malformed primary colour degrades to the default"
        <| fun _ ->
            let cases = [ "blue"; "2563eb"; "#xyz"; "#12345"; "#1234567"; "#" ]

            for raw in cases do
                let resolved =
                    Branding.resolve defaults (Map.ofList [ ConfigKeys.BrandingKeys.PrimaryColor, raw ])

                Expect.equal resolved.PrimaryColor defaults.PrimaryColor (sprintf "%s → default" raw)

        // ─── mergeBrandingSchema ──────────────────────────────────

        testCase "merge with no _platform entry prepends a branding-only one"
        <| fun _ ->
            let merged = PlatformSchema.mergeBrandingSchema []
            let platform = platformEntry merged
            let keys = brandingKeys platform

            Expect.isTrue (Set.contains ConfigKeys.BrandingKeys.AppName keys) "appName present"
            Expect.isTrue (Set.contains ConfigKeys.BrandingKeys.PrimaryColor keys) "primaryColor present"
            Expect.isTrue (Set.contains ConfigKeys.BrandingKeys.LogoUrl keys) "logoUrl present"
            Expect.isTrue (Set.contains ConfigKeys.BrandingKeys.FaviconUrl keys) "faviconUrl present"

        testCase "merge appends branding fields onto an existing _platform entry"
        <| fun _ ->
            let merged =
                PlatformSchema.mergeBrandingSchema [ platformWith [ field "currencySymbol" ] ]

            let keys = brandingKeys (platformEntry merged)

            Expect.isTrue (Set.contains "currencySymbol" keys) "existing field retained"
            Expect.equal (Set.count keys) 5 "currencySymbol + four branding fields"

        testCase "app-declared field of the same key wins (no duplicate)"
        <| fun _ ->
            let merged =
                PlatformSchema.mergeBrandingSchema [ platformWith [ field ConfigKeys.BrandingKeys.AppName ] ]

            let appNameCount =
                (platformEntry merged).Schema.Fields
                |> List.filter (fun f -> f.Key = ConfigKeys.BrandingKeys.AppName)
                |> List.length

            Expect.equal appNameCount 1 "appName not duplicated by the merge"
    ]