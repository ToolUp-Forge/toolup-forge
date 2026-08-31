// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MessageCatalogContractTests

open Expecto
open ToolUp.Platform

// ─── Phase 444.E — client-shell localization substrate ────────────────
//
// What this pack does NOT test, and why that is the point: catalog
// COMPLETENESS. `MessageCatalog` is a record, so a translation missing a
// string is a missing field and the compiler names it — a runtime
// assertion over the same property could only ever be weaker, and would
// go stale the moment a field was added. The whole reason the substrate
// is a record rather than a key table is to move that check to compile
// time; re-asserting it here would suggest the compile-time property is
// not trusted.
//
// What is left over is everything the type system cannot state:
//
//   1. locale RESOLUTION — the precedence between the declared mode, the
//      team's `_platform.locale` and the browser preference, and the
//      fallback chain each mode ends in;
//   2. the team-default PLUMB-THROUGH — that the key read from the
//      `_platform` config map is the same key the server-side resolver
//      writes, and that a blank or absent value falls through rather
//      than resolving to an empty tag;
//   3. the OVERRIDE hook — that it is handed the resolved locale, that
//      returning its argument is the fallback to English, and that a
//      raising override degrades the language rather than the shell.
//
// All three are exercised on .NET rather than through Fable, which is
// exactly why `MessageCatalog.fs` carries no Feliz dependency.

let private catalogTests =
    testList "MessageCatalog: locale resolution" [

        test "FixedLocale returns its literal and consults nothing else" {
            // Both ambient sources are populated and both must be ignored:
            // a deployment that declared one locale for the whole app has
            // said the browser's opinion is not wanted.
            let resolved =
                MessageCatalog.resolveLocale (FixedLocale "de") (Some "fr") (Some "es")

            Expect.equal resolved "de" "FixedLocale must win over team and browser"
        }

        test "FixedLocale with a blank literal falls back to the built-in locale" {
            // Never resolve to "" — `new Intl.NumberFormat("")` throws a
            // RangeError, so a blank tag would take out the number
            // formatting of every view that touched it.
            let resolved = MessageCatalog.resolveLocale (FixedLocale "   ") None None
            Expect.equal resolved MessageCatalog.BuiltInLocale "blank must fall back, not pass through"
        }

        test "FixedLocale trims surrounding whitespace" {
            let resolved = MessageCatalog.resolveLocale (FixedLocale " fr-CA ") None None
            Expect.equal resolved "fr-CA" "the tag must reach Intl trimmed"
        }

        test "BrowserLocale prefers the browser and ignores the team default" {
            // The team value is deliberately supplied and deliberately
            // unused: `BrowserLocale` is the mode that says "ask the
            // visitor", and silently preferring a team setting would make
            // the two modes indistinguishable wherever a team had one.
            let resolved =
                MessageCatalog.resolveLocale (BrowserLocale "en") (Some "de") (Some "fr")

            Expect.equal resolved "fr" "BrowserLocale must read the browser"
        }

        test "BrowserLocale falls back to its declared fallback with no browser" {
            // This is every non-browser host — a prerender pass, a jsdom
            // harness, this test — so the fallback arm is the common case
            // rather than an edge.
            let resolved = MessageCatalog.resolveLocale (BrowserLocale "en-GB") None None
            Expect.equal resolved "en-GB" "no browser must reach the declared fallback"
        }

        test "BrowserLocale treats a blank browser value as absent" {
            let resolved = MessageCatalog.resolveLocale (BrowserLocale "en-GB") None (Some "  ")

            Expect.equal resolved "en-GB" "a blank navigator.language is not a locale"
        }

        test "TeamDefault prefers the team's configured locale" {
            let resolved =
                MessageCatalog.resolveLocale (TeamDefault "en") (Some "de") (Some "fr")

            Expect.equal resolved "de" "an explicit team setting must win over the browser"
        }

        test "TeamDefault falls through to the browser when the team has set nothing" {
            // A team that has configured no locale is not asking for
            // English — it is asking for nothing, and the visitor's own
            // preference is the better answer than the deployment's
            // fallback.
            let resolved = MessageCatalog.resolveLocale (TeamDefault "en") None (Some "fr")
            Expect.equal resolved "fr" "an unset team default must not shadow the browser"
        }

        test "TeamDefault reaches its declared fallback when neither source has a value" {
            let resolved = MessageCatalog.resolveLocale (TeamDefault "es") None None
            Expect.equal resolved "es" "both sources absent must reach the fallback"
        }

        test "every mode ends at the built-in locale rather than a blank tag" {
            // Resolution is total by construction; this pins that no
            // configuration of blanks can produce an empty string.
            let blanks = [ FixedLocale ""; BrowserLocale " "; TeamDefault "" ]

            for mode in blanks do
                let resolved = MessageCatalog.resolveLocale mode (Some "") (Some "   ")

                Expect.equal
                    resolved
                    MessageCatalog.BuiltInLocale
                    (sprintf "%A with blank inputs must reach the built-in locale" mode)
        }
    ]

let private teamPlumbThroughTests =
    testList "MessageCatalog: team-default plumb-through" [

        test "the team locale is read from the key the server-side resolver writes" {
            // The literal is asserted rather than only used, because the
            // whole value of sharing it is that ONE team setting drives
            // the SSR tier and the client shell. A silent rename here
            // would leave both tiers working and disagreeing.
            Expect.equal
                MessageCatalog.TeamLocaleConfigKey
                "_platform.locale"
                "the client must read the same _platform key the server LocaleResolver does"
        }

        test "teamLocaleOf lifts the configured value out of the platform-config map" {
            let platformConfig =
                Map.ofList [ "_platform.locale", "fr-CA"; "_platform.brandName", "Acme" ]

            Expect.equal
                (MessageCatalog.teamLocaleOf platformConfig)
                (Some "fr-CA")
                "the configured team locale must be found"
        }

        test "an absent key yields None rather than an empty tag" {
            Expect.isNone
                (MessageCatalog.teamLocaleOf (Map.ofList [ "_platform.brandName", "Acme" ]))
                "a team that configured nothing must fall through"
        }

        test "a blank configured value yields None rather than an empty tag" {
            // A team admin who cleared the field has unset it, not set it
            // to nothing; treating "" as a locale would resolve past the
            // browser to a tag Intl rejects.
            Expect.isNone
                (MessageCatalog.teamLocaleOf (Map.ofList [ "_platform.locale", "   " ]))
                "a cleared team locale must read as unset"
        }

        test "a configured team locale round-trips through resolution" {
            // End to end over the two functions the shell composes: map →
            // teamLocaleOf → resolveLocale.
            let platformConfig = Map.ofList [ "_platform.locale", "de" ]

            let resolved =
                MessageCatalog.resolveLocale (TeamDefault "en") (MessageCatalog.teamLocaleOf platformConfig) (Some "fr")

            Expect.equal resolved "de" "the team's configured locale must reach the catalog"
        }
    ]

// A minimal second language: enough fields to prove the mechanism, and
// deliberately NOT a full translation — a partial override is the shape
// the record-update syntax exists to make natural.
let private frenchOverride (catalog: MessageCatalog) : MessageCatalog =
    if catalog.Locale.StartsWith "fr" then
        {
            catalog with
                Shell = {
                    catalog.Shell with
                        SignOut = "Se déconnecter"
                        SelectTeam = "Choisir une équipe"
                        ResultsAvailableIn = fun m -> sprintf "Résultats disponibles dans %s" m
                }
        }
    else
        catalog

let private overrideTests =
    testList "MessageCatalog: consumer override" [

        test "no override returns the built-in catalog stamped with the locale" {
            let resolved = MessageCatalog.resolve "de" None
            Expect.equal resolved.Locale "de" "the resolved locale must be stamped on the catalog"
            Expect.equal resolved.Shell.SignOut "Sign out" "with no override the strings stay English"
        }

        test "the override is handed the resolved locale so one function can serve several" {
            // This is the mechanism that lets `MessageCatalogOverride` be
            // a single `MessageCatalog -> MessageCatalog` and still cover
            // more than one language.
            let mutable seen = ""

            let spy (c: MessageCatalog) =
                seen <- c.Locale
                c

            MessageCatalog.resolve "fr-CA" (Some spy) |> ignore
            Expect.equal seen "fr-CA" "the override must be able to see which language was asked for"
        }

        test "a covered language is translated" {
            let resolved = MessageCatalog.resolve "fr" (Some frenchOverride)
            Expect.equal resolved.Shell.SignOut "Se déconnecter" "the override's strings must win"
            Expect.equal resolved.Shell.SelectTeam "Choisir une équipe" "…for every field it set"
        }

        test "a partial translation keeps English for the fields it did not set" {
            // The record-update half of the design: an override supplies
            // what it has, and the rest is the built-in catalog. There is
            // no per-field lookup and no missing-key state.
            let resolved = MessageCatalog.resolve "fr" (Some frenchOverride)

            Expect.equal
                resolved.Shell.CreateTeam
                MessageCatalog.english.Shell.CreateTeam
                "an untranslated field must keep the built-in string"
        }

        test "a parameterised message survives translation with its substitution" {
            let resolved = MessageCatalog.resolve "fr" (Some frenchOverride)

            Expect.equal
                (resolved.Shell.ResultsAvailableIn "Ventes")
                "Résultats disponibles dans Ventes"
                "a function field must substitute in the translated sentence"
        }

        test "an uncovered language falls back to English via the identity function" {
            // The fallback chain is the override returning its argument —
            // not a per-field lookup. Pinning it here because that IS the
            // documented mechanism a consumer is told to rely on.
            let resolved = MessageCatalog.resolve "de" (Some frenchOverride)
            Expect.equal resolved.Shell.SignOut "Sign out" "an uncovered language must render English"
            Expect.equal resolved.Locale "de" "…while still reporting the locale that was asked for"
        }

        test "a raising override degrades the language, never the shell" {
            // A translation bug must not be able to blank the page. The
            // shell rendering English is a visibly-degraded state an
            // operator can diagnose; a thrown exception during render is
            // not.
            let exploding (_: MessageCatalog) : MessageCatalog = failwith "bad translation"

            let resolved = MessageCatalog.resolve "fr" (Some exploding)

            Expect.equal
                resolved.Shell.SignOut
                MessageCatalog.english.Shell.SignOut
                "a raising override must fall back to the built-in catalog"
        }

        // `MessageCatalog` carries `string -> string` fields, so it has no
        // structural equality and the two tests below compare
        // representative strings rather than whole records. That is not a
        // workaround for the type — it is the type working: a message with
        // a parameter is a FUNCTION precisely so the substitution points
        // are typed, and functions are not comparable.
        test "forLocale leaves the built-in catalog's strings untouched apart from the stamp" {
            let stamped = MessageCatalog.forLocale "fr"
            Expect.equal stamped.Locale "fr" "the stamp must be applied"

            Expect.equal stamped.Shell.SignOut MessageCatalog.english.Shell.SignOut "shell strings must be untouched"

            Expect.equal
                stamped.TeamManager.MyTeamsPanel
                MessageCatalog.english.TeamManager.MyTeamsPanel
                "module strings must be untouched"

            Expect.equal
                (stamped.Shell.ResultsAvailableIn "X")
                (MessageCatalog.english.Shell.ResultsAvailableIn "X")
                "parameterised messages must be untouched"
        }

        test "forLocale with a blank tag returns the built-in locale, unstamped" {
            let stamped = MessageCatalog.forLocale "  "

            Expect.equal
                stamped.Locale
                MessageCatalog.BuiltInLocale
                "a blank locale must not be stamped over the built-in one"
        }
    ]

let private switchSeamTests =
    testList "MessageCatalog: locale-switch seam" [

        test "a request reaches every subscriber and no subscriber is a no-op failure" {
            let mutable received = []
            let unsubscribe = LocaleRequest.subscribe (fun l -> received <- l :: received)

            LocaleRequest.request "fr"
            LocaleRequest.request "de"
            unsubscribe ()

            Expect.equal (List.rev received) [ "fr"; "de" ] "both requests must be delivered in order"
        }

        test "a request with no subscribers is a no-op rather than a failure" {
            // The documented contract, and the case in every harness that
            // has not mounted the shell — including this one.
            LocaleRequest.request "fr"
        }

        test "an unsubscribed callback stops receiving" {
            let mutable count = 0
            let unsubscribe = LocaleRequest.subscribe (fun _ -> count <- count + 1)
            LocaleRequest.request "fr"
            unsubscribe ()
            LocaleRequest.request "de"
            Expect.equal count 1 "the dispose thunk must actually remove the callback"
        }

        test "a raising subscriber does not stop delivery to the others" {
            // Same guarantee `NavigationRequest` gives: one companion's
            // bug must not silently sever another's subscription.
            let mutable reached = false
            let bad = LocaleRequest.subscribe (fun _ -> failwith "subscriber bug")
            let good = LocaleRequest.subscribe (fun _ -> reached <- true)

            LocaleRequest.request "fr"
            bad ()
            good ()

            Expect.isTrue reached "delivery must continue past a raising subscriber"
        }
    ]

let tests = catalogTests
let teamDefaultTests = teamPlumbThroughTests
let consumerOverrideTests = overrideTests
let localeSwitchTests = switchSeamTests