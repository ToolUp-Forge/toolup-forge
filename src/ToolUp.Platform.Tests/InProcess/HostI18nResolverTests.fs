module ToolUp.Platform.Tests.InProcess.HostI18nResolverTests

open System.IO
open Expecto
open ToolUp.Platform

// ─── Phase 275 — hosted-tree i18n resolution seam ──────────────────────
//
// The resolver a hosted tree resolves localized-string bindings against.
// Four proofs:
//   1. A key + placeholders resolves per locale (with language-only
//      fallback, via the Phase 179 Translations substrate).
//   2. A missing key is FLAGGED (observable via the miss sink) and returns
//      the key — never a silent blank (GP 2).
//   3. Pseudolocalisation applies to a hosted tree (the `qps-ploc` audit).
//   4. OSS grep-guard.

let private fr = LocaleCode.fr
let private en = LocaleCode.en

// A tiny translation table: a greeting key with an {name} placeholder in
// two locales, and an English-only key (to exercise fallback).
let private translations: Translations =
    Map.ofList [
        "host.greeting", Map.ofList [ en, "Hello, {name}!"; fr, "Bonjour, {name} !" ]
        "host.enOnly", Map.ofList [ en, "English only" ]
    ]

let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

// ─── 1. Key + placeholder resolves per locale ─────────────────────────

let private resolutionTests =
    testList "Phase 275 — key + placeholder resolves per locale" [
        testCase "resolves the localized template with placeholders substituted"
        <| fun _ ->
            let r = HostI18nResolver.ofTranslations translations en

            Expect.equal
                (r.Resolve "host.greeting" (Map.ofList [ "name", "Ada" ]) en)
                "Hello, Ada!"
                "English template + placeholder"

            Expect.equal
                (r.Resolve "host.greeting" (Map.ofList [ "name", "Ada" ]) fr)
                "Bonjour, Ada !"
                "French template + placeholder"

        testCase "falls back to a language/fallback registration"
        <| fun _ ->
            let r = HostI18nResolver.ofTranslations translations en
            // fr has no host.enOnly; fallback en supplies it.
            Expect.equal (r.Resolve "host.enOnly" Map.empty fr) "English only" "fallback locale supplies the string"
    ]

// ─── 2. Missing key is flagged, not blanked ───────────────────────────

let private missTests =
    testList "Phase 275 — missing key is observable (GP 2)" [
        testCase "an unregistered key returns the key AND is recorded (never blank)"
        <| fun _ ->
            let r = HostI18nResolver.MissRecordingHostI18nResolver(translations, en)
            let out = (r :> IHostI18nResolver).Resolve "host.unknown" Map.empty en

            Expect.equal out "host.unknown" "the miss returns the key verbatim — visible, not a blank"
            Expect.isNonEmpty out "never a silent blank"
            Expect.equal r.Misses [ "host.unknown", en ] "the miss feeds the Phase 179 coverage signal"

        testCase "the miss sink fires on the resolve"
        <| fun _ ->
            let recorded = ResizeArray<string>()
            let r = HostI18nResolver.create translations en (fun key _ -> recorded.Add key)

            r.Resolve "host.greeting" (Map.ofList [ "name", "x" ]) en |> ignore
            r.Resolve "host.absent" Map.empty en |> ignore

            Expect.equal (List.ofSeq recorded) [ "host.absent" ] "only the unresolved key fires the sink"
    ]

// ─── 3. Pseudolocalisation passthrough ────────────────────────────────

let private pseudoTests =
    testList "Phase 275 — pseudolocalisation passthrough" [
        testCase "a hosted tree participates in the qps-ploc audit"
        <| fun _ ->
            let r = HostI18nResolver.ofTranslations translations en
            // Under the pseudo-locale, resolution falls through the fallback
            // to the English template, which is then accented + bracketed.
            let out = r.Resolve "host.enOnly" Map.empty PseudoLocale.code

            Expect.notEqual out "English only" "the pseudo-locale transforms the resolved string"
            Expect.stringContains out "⟦" "the pseudo-loc opening marker is present"
            Expect.stringContains out "⟧" "the pseudo-loc closing marker is present"

        testCase "placeholders still substitute after pseudo-localisation"
        <| fun _ ->
            let r = HostI18nResolver.ofTranslations translations en
            let out = r.Resolve "host.greeting" (Map.ofList [ "name", "Ada" ]) PseudoLocale.code

            Expect.stringContains out "Ada" "the {name} placeholder survives the pseudo-loc transform"
    ]

// ─── 4. OSS grep-guard ────────────────────────────────────────────────

let private ossTests =
    testList "Phase 275 — OSS boundary" [
        testCase "the resolver source carries no banned OSS vocabulary"
        <| fun _ ->
            let path =
                Path.Combine(repoRoot (), "src", "ToolUp.Platform.Client", "Client", "HostI18nResolver.fs")

            Expect.isTrue (File.Exists path) (sprintf "expected the seam file at %s" path)
            let contents = (File.ReadAllText path).ToLowerInvariant()
            Expect.isFalse (contents.Contains "fuaran") "the resolver must name no private layer (GP 1)"
    ]

let tests =
    testList "HostI18nResolver (Phase 275)" [ resolutionTests; missTests; pseudoTests; ossTests ]