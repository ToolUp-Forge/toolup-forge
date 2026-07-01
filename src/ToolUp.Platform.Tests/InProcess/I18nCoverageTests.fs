module ToolUp.Platform.Tests.InProcess.I18nCoverageTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 179 — i18n coverage gate + pseudo-localisation ────────────
//
// Covers:
//   1. `I18nCoverage.audit` flags a key present in `en` but missing in a
//      required `fr`, and stays silent when the language-only fallback
//      (`en` → `en-GB`) covers the requirement.
//   2. The SDK's own `sdk.*` + `ApiError` keys are fully covered for
//      `en` + `fr` (regression-guards the Phase 12a seed).
//   3. `I18nCoverageMode = FailOnMissing` aborts (validator → `Error`) on
//      a synthetic gap; `WarnOnMissing` warns; `NoCoverageCheck` builds
//      no validator.
//   4. `PseudoLocale.transform` accents vowels, preserves `{placeholder}`
//      tokens, lengthens the string, and round-trips a vowel-less string
//      unchanged.

let private sample: Translations =
    Map.ofList [
        "greeting", Map.ofList [ LocaleCode.en, "Hello"; LocaleCode.fr, "Bonjour" ]
        "farewell", Map.ofList [ LocaleCode.en, "Goodbye" ] // fr missing
    ]

let private auditTests =
    testList "I18nCoverage.audit" [
        testCase "flags a key present in en but missing in required fr"
        <| fun _ ->
            let report = I18nCoverage.audit sample [ LocaleCode.en; LocaleCode.fr ]
            Expect.isFalse (I18nCoverage.CoverageReport.isComplete report) "farewell gap present"

            let gap = report.Gaps |> List.tryFind (fun g -> g.Key = "farewell")
            Expect.isSome gap "farewell reported"
            Expect.equal (gap |> Option.get).MissingLocales [ LocaleCode.fr ] "only fr missing"

        testCase "greeting (en + fr) is fully covered"
        <| fun _ ->
            let report = I18nCoverage.audit sample [ LocaleCode.en; LocaleCode.fr ]
            let gap = report.Gaps |> List.tryFind (fun g -> g.Key = "greeting")
            Expect.isNone gap "greeting has no gap"

        testCase "language-only fallback: en satisfies an en-GB requirement"
        <| fun _ ->
            let report = I18nCoverage.audit sample [ LocaleCode.enGB ]
            // "greeting" + "farewell" both have en → en-GB covered by
            // language-only fallback, so no gaps at all.
            Expect.isTrue (I18nCoverage.CoverageReport.isComplete report) "en covers en-GB"

        testCase "an entirely-absent required key reports every required locale"
        <| fun _ ->
            let report =
                I18nCoverage.auditKeys sample [ "absent.key" ] [ LocaleCode.en; LocaleCode.fr ]

            let gap = report.Gaps |> List.exactlyOne
            Expect.equal gap.Key "absent.key" "the absent key"
            Expect.equal gap.MissingLocales [ LocaleCode.en; LocaleCode.fr ] "both required locales missing"
    ]

let private sdkSeedCoverageTests =
    testList "I18nCoverage — SDK seed regression guard" [
        testCase "SDK sdk.* + ApiError keys are covered for en + fr"
        <| fun _ ->
            let report =
                I18nCoverage.auditKeys I18nDefaults.sdkTranslations I18nCoverage.sdkRequiredKeys [
                    LocaleCode.en
                    LocaleCode.fr
                ]

            Expect.isTrue
                (I18nCoverage.CoverageReport.isComplete report)
                $"SDK seed must cover en+fr; gaps: {I18nCoverage.CoverageReport.describe report}"

        testCase "sdkRequiredKeys includes every stock ApiError message key"
        <| fun _ ->
            let stock = [
                ErrorCode.NotAuthenticated
                ErrorCode.NotAuthorized
                ErrorCode.NotFound
                ErrorCode.Conflict
                ErrorCode.ValidationFailed
                ErrorCode.Internal
            ]

            for code in stock do
                let key = I18nDefaults.messageKeyFor code
                Expect.contains I18nCoverage.sdkRequiredKeys key $"required-key set contains {key}"
    ]

let private validatorTests =
    // A synthetic table with a deliberate fr gap on top of the SDK seed.
    let gappy: Translations =
        Translations.merge
            I18nDefaults.sdkTranslations
            (Map.ofList [ "app.title", Map.ofList [ LocaleCode.en, "Dashboard" ] ])

    let required = [ LocaleCode.en; LocaleCode.fr ]

    testList "I18nCoverage.validator" [
        testCase "NoCoverageCheck builds no validator"
        <| fun _ ->
            let v = I18nCoverage.validator gappy required NoCoverageCheck
            Expect.isNone v "no validator joins the preflight when off"

        testCase "FailOnMissing aborts (Error) on a synthetic gap, naming key + locale"
        <| fun _ ->
            let v = I18nCoverage.validator gappy required FailOnMissing |> Option.get
            let result = v.Validate() |> Async.RunSynchronously

            match result with
            | ValidationResult.Error msg ->
                Expect.stringContains msg "app.title" "names the missing key"
                Expect.stringContains msg "fr" "names the missing locale"
            | other -> failtestf "expected Error, got %A" other

        testCase "WarnOnMissing warns (does not abort) on the same gap"
        <| fun _ ->
            let v = I18nCoverage.validator gappy required WarnOnMissing |> Option.get
            let result = v.Validate() |> Async.RunSynchronously

            match result with
            | ValidationResult.Warning msg -> Expect.stringContains msg "app.title" "warning names the key"
            | other -> failtestf "expected Warning, got %A" other

        testCase "no gap → Ok (SDK seed audited against en only)"
        <| fun _ ->
            let v =
                I18nCoverage.validator I18nDefaults.sdkTranslations [ LocaleCode.en ] FailOnMissing
                |> Option.get

            let result = v.Validate() |> Async.RunSynchronously
            Expect.equal result ValidationResult.Ok "en-only SDK surface is complete"
    ]

let private pseudoLocaleTests =
    testList "PseudoLocale.transform" [
        testCase "accents vowels and lengthens the string"
        <| fun _ ->
            let out = PseudoLocale.transform "Save"
            Expect.stringContains out "á" "a accented"
            Expect.stringContains out "é" "e accented"
            Expect.isGreaterThan out.Length "Save".Length "padded longer"
            Expect.stringContains out "⟦" "opening marker"
            Expect.stringContains out "⟧" "closing marker"

        testCase "preserves {placeholder} tokens for later substitution"
        <| fun _ ->
            let template = "Validation failed: {reason}"
            let out = PseudoLocale.transform template
            Expect.stringContains out "{reason}" "placeholder token intact"
            // And substitution still lands after transformation.
            let rendered =
                ApiError.applyPlaceholders out (Map.ofList [ "reason", "email required" ])

            Expect.stringContains rendered "email required" "placeholder substituted post-transform"
            Expect.isFalse (rendered.Contains "{reason}") "no placeholder left"

        testCase "a vowel-less string round-trips unchanged"
        <| fun _ ->
            let vowelless = "XYZ-123"
            Expect.equal (PseudoLocale.transform vowelless) vowelless "nothing to accent → unchanged"

        testCase "isActive matches only the pseudo-locale"
        <| fun _ ->
            Expect.isTrue (PseudoLocale.isActive (LocaleCode "qps-ploc")) "qps-ploc is active"
            Expect.isFalse (PseudoLocale.isActive LocaleCode.en) "en is not the pseudo-locale"

        testCase "empty string round-trips unchanged"
        <| fun _ -> Expect.equal (PseudoLocale.transform "") "" "empty in, empty out"
    ]

let tests =
    testList "ToolUp.Platform I18n coverage + pseudo-locale (Phase 179)" [
        auditTests
        sdkSeedCoverageTests
        validatorTests
        pseudoLocaleTests
    ]