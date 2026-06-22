module ToolUp.AI.Client.Tests.ConsentBannerTests

open System
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform
open ToolUp.Platform.Consent

// ─── Phase 159 — ConsentBanner MVU (Fable-tier) ─────────────────────
//
// Drives the pure `ConsentBanner` MVU core (`init` / `update` /
// `toConsentState`) under the Fable NodeTest harness. The banner lives
// in `ToolUp.Platform.Client` and is reached transitively through the
// AI.Client project reference. View rendering (the Feliz component, the
// `NoOpConsentProvider` short-circuit) is not exercised here — the
// harness drives pure update functions only.

let private fixedNow = DateTimeOffset(2026, 6, 22, 0, 0, 0, TimeSpan.Zero)

let private managed = ConsentBanner.defaultManagedCategories

let private grantingState (categories: ConsentCategory list) : ConsentState = {
    Granted = Set.ofList (Necessary :: categories)
    Denied = Set.empty
    LastUpdatedAt = fixedNow
    ConsentVersion = 3
}

let tests =
    testList "ConsentBanner (Phase 159)" [

        testCase "init seeds pending toggles from the current granted set"
        <| fun () ->
            let model = ConsentBanner.init managed (grantingState [ Analytics ])

            Expect.isTrue (Map.find Analytics model.Pending) "a granted category starts toggled on"
            Expect.isFalse (Map.find Marketing model.Pending) "a non-granted category starts off"
            Expect.equal model.Version 3 "consent version carries from the seed state"
            Expect.isFalse model.Dismissed "banner starts visible"

        testCase "ToggleCategory flips a single category"
        <| fun () ->
            let m0 = ConsentBanner.init managed ConsentState.initial

            let m1, _ =
                ConsentBanner.update fixedNow (ConsentBanner.ToggleCategory Analytics) m0

            Expect.isTrue (Map.find Analytics m1.Pending) "toggled category flips on"
            Expect.isFalse (Map.find Marketing m1.Pending) "other categories are untouched"

        testCase "AcceptAll turns every managed category on; RejectAll turns them off"
        <| fun () ->
            let m0 = ConsentBanner.init managed ConsentState.initial

            let accepted, _ = ConsentBanner.update fixedNow ConsentBanner.AcceptAll m0

            for c in managed do
                Expect.isTrue (Map.find c accepted.Pending) "AcceptAll grants every managed category"

            let rejected, _ = ConsentBanner.update fixedNow ConsentBanner.RejectAll accepted

            for c in managed do
                Expect.isFalse (Map.find c rejected.Pending) "RejectAll denies every managed category"

        testCase "toConsentState always grants Necessary and partitions managed categories"
        <| fun () ->
            let m0 = ConsentBanner.init managed ConsentState.initial

            let toggled, _ =
                ConsentBanner.update fixedNow (ConsentBanner.ToggleCategory Analytics) m0

            let state = ConsentBanner.toConsentState fixedNow toggled

            Expect.isTrue (Set.contains Necessary state.Granted) "Necessary is always granted"
            Expect.isTrue (Set.contains Analytics state.Granted) "an on category lands in Granted"
            Expect.isTrue (Set.contains Marketing state.Denied) "an off category lands in Denied"
            Expect.isFalse (Set.contains Necessary state.Denied) "Necessary never lands in Denied"
            Expect.equal state.ConsentVersion 1 "version carries from the seed (initial = 1)"

        testCase "SavePreferences dismisses the banner and commits the projected state"
        <| fun () ->
            let mutable committed: ConsentState option = None
            ConsentBanner.onCommit (fun state -> committed <- Some state)

            let m0 = ConsentBanner.init managed ConsentState.initial
            let accepted, _ = ConsentBanner.update fixedNow ConsentBanner.AcceptAll m0

            let saved, cmd =
                ConsentBanner.update fixedNow ConsentBanner.SavePreferences accepted

            Expect.isTrue saved.Dismissed "SavePreferences dismisses the banner"

            // Run the Cmd's fire-and-forget commit effect (dispatch is a
            // no-op for these subscriptions).
            for sub in cmd do
                sub (fun _ -> ())

            match committed with
            | Some state ->
                Expect.isTrue (Set.contains Analytics state.Granted) "commit carries the accept-all decision"
                Expect.isTrue (Set.contains Necessary state.Granted) "commit always grants Necessary"
            | None -> failwith "Save did not fire the commit sink"

            // Reset the module-level sink so test order cannot leak state.
            ConsentBanner.onCommit ignore
    ]