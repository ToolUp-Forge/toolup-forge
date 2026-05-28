module ToolUp.Platform.Tests.InProcess.I18nInfrastructureTests

open Expecto
open ToolUp.Platform

// ─── Phase 12a — I18n Infrastructure ─────────────────────────────────
//
// Cover the substrate primitives:
//   1. `Translations.tryLookup` resolution order (exact → language → fallback → None)
//   2. `Translations.merge` collision semantics (right-hand wins)
//   3. SDK seed translations populate English + French for every shipped key
//   4. `ApiError.applyPlaceholders` + `renderMessage`
//   5. `DefaultLocaleResolver` resolution-order recipe (user > team > Accept-Language > fallback)
//   6. `parseAcceptLanguage` splits a comma-separated header and trims q-weights

let private resolverTests =
    let baseContext: AccessContext =
        AccessContext.unrestricted (Subject.AnonymousSession "alice")

    let resolver: ILocaleResolver =
        LocaleResolver.DefaultLocaleResolver(I18nDefaults.defaultFallback) :> _

    testList "DefaultLocaleResolver — resolution order" [
        testCase "1. team config wins over Accept-Language"
        <| fun _ ->
            let cfg = Map.ofList [ "_platform.locale", "fr" ]
            let resolved = resolver.Resolve(cfg, baseContext, Some "en-GB")
            Expect.equal resolved LocaleCode.fr "team locale"

        testCase "2. user override wins over team config"
        <| fun _ ->
            let cfg =
                Map.ofList [ "_platform.locale", "fr"; "_platform.userLocale.alice", "de" ]

            let resolved = resolver.Resolve(cfg, baseContext, Some "en-GB")
            Expect.equal resolved LocaleCode.de "user override"

        testCase "3. Accept-Language used when no team / user config"
        <| fun _ ->
            let resolved =
                resolver.Resolve(Map.empty, baseContext, Some "fr-CA,fr;q=0.9,en;q=0.5")

            Expect.equal resolved LocaleCode.frCA "first Accept-Language tag"

        testCase "4. fallback used when no signals"
        <| fun _ ->
            let resolved = resolver.Resolve(Map.empty, baseContext, None)
            Expect.equal resolved I18nDefaults.defaultFallback "fallback"

        testCase "5. blank team value treated as unset"
        <| fun _ ->
            let cfg = Map.ofList [ "_platform.locale", "   " ]
            let resolved = resolver.Resolve(cfg, baseContext, Some "es-ES")
            Expect.equal (LocaleCode.value resolved) "es-ES" "Accept-Language wins"

        testCase "parseAcceptLanguage strips q-weights"
        <| fun _ ->
            let parsed = LocaleResolver.parseAcceptLanguage "en-GB,en;q=0.9, fr;q=0.5"
            Expect.equal parsed.Length 3 "three tags"
            Expect.equal parsed[0] LocaleCode.enGB "first tag"
            Expect.equal parsed[1] LocaleCode.en "second tag"
            Expect.equal parsed[2] LocaleCode.fr "third tag"

        testCase "parseAcceptLanguage handles whitespace + empty"
        <| fun _ ->
            Expect.equal (LocaleResolver.parseAcceptLanguage "") [] "empty"
            Expect.equal (LocaleResolver.parseAcceptLanguage "   ") [] "whitespace"
    ]

let private translationsTests =
    let sample: Translations =
        Map.ofList [
            "greeting", Map.ofList [ LocaleCode.en, "Hello"; LocaleCode.fr, "Bonjour" ]
            "farewell", Map.ofList [ LocaleCode.en, "Goodbye" ]
        ]

    testList "Translations — lookup + merge" [
        testCase "exact locale match"
        <| fun _ ->
            let result = Translations.tryLookup sample LocaleCode.fr None "greeting"
            Expect.equal result (Some "Bonjour") "fr greeting"

        testCase "language-only fallback (en-GB → en)"
        <| fun _ ->
            let result =
                Translations.tryLookup sample (LocaleCode.create "en-GB") None "greeting"

            Expect.equal result (Some "Hello") "en fallback for en-GB"

        testCase "explicit fallback locale"
        <| fun _ ->
            let result =
                Translations.tryLookup sample LocaleCode.de (Some LocaleCode.en) "farewell"

            Expect.equal result (Some "Goodbye") "explicit en fallback"

        testCase "missing key returns None"
        <| fun _ ->
            let result = Translations.tryLookup sample LocaleCode.en None "absent"

            Expect.equal result None "missing key"

        testCase "lookupOrKey returns key on miss"
        <| fun _ ->
            let result = Translations.lookupOrKey sample LocaleCode.en None "absent"
            Expect.equal result "absent" "key verbatim"

        testCase "merge — right-hand wins on per-locale collision"
        <| fun _ ->
            let overlay: Translations =
                Map.ofList [ "greeting", Map.ofList [ LocaleCode.en, "Hi" ] ]

            let merged = Translations.merge sample overlay
            let en = Translations.tryLookup merged LocaleCode.en None "greeting"
            let fr = Translations.tryLookup merged LocaleCode.fr None "greeting"
            Expect.equal en (Some "Hi") "overlay en wins"
            Expect.equal fr (Some "Bonjour") "fr preserved from base"
    ]

let private sdkSeedTests =
    testList "I18nDefaults — SDK seed translations" [
        testCase "every shipped key has English + French entries"
        <| fun _ ->
            for KeyValue(key, perLocale) in I18nDefaults.sdkTranslations do
                let hasEn =
                    perLocale
                    |> Map.toSeq
                    |> Seq.exists (fun (l, _) -> LocaleCode.equals l LocaleCode.en)

                let hasFr =
                    perLocale
                    |> Map.toSeq
                    |> Seq.exists (fun (l, _) -> LocaleCode.equals l LocaleCode.fr)

                Expect.isTrue hasEn $"key '{key}' missing English entry"
                Expect.isTrue hasFr $"key '{key}' missing French entry"

        testCase "messageKeyFor covers every stock ErrorCode"
        <| fun _ ->
            let cases = [
                ErrorCode.NotAuthenticated
                ErrorCode.NotAuthorized
                ErrorCode.NotFound
                ErrorCode.Conflict
                ErrorCode.ValidationFailed
                ErrorCode.Internal
                ErrorCode.Module("Sample", "rule.broken")
            ]

            for code in cases do
                let key = I18nDefaults.messageKeyFor code
                Expect.isFalse (System.String.IsNullOrWhiteSpace key) "non-blank key"
    ]

let private apiErrorTests =
    testList "ApiError — envelope + renderMessage" [
        testCase "applyPlaceholders substitutes named placeholders"
        <| fun _ ->
            let template = "Validation failed: {reason} (field: {field})"

            let details = Map.ofList [ "reason", "required"; "field", "email" ]

            let rendered = ApiError.applyPlaceholders template details
            Expect.equal rendered "Validation failed: required (field: email)" "substituted"

        testCase "renderMessage resolves through translations"
        <| fun _ ->
            let err =
                ApiError.create ErrorCode.ValidationFailed "sdk.error.validationFailed"
                |> ApiError.withDetail "reason" "email is required"

            let rendered =
                ApiError.renderMessage I18nDefaults.sdkTranslations LocaleCode.en None err

            Expect.stringContains rendered "email is required" "placeholder applied"

        testCase "renderMessage falls back to key on missing translation"
        <| fun _ ->
            let err =
                ApiError.create (ErrorCode.Module("X", "y")) "module.x.y.message"
                |> ApiError.withDetail "n" "42"

            let rendered =
                ApiError.renderMessage I18nDefaults.sdkTranslations LocaleCode.en None err

            Expect.equal rendered "module.x.y.message" "key verbatim, no placeholders to apply"
    ]

let tests =
    testList "ToolUp.Platform I18n (Phase 12a)" [ resolverTests; translationsTests; sdkSeedTests; apiErrorTests ]