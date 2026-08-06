module ToolUp.AI.Client.Tests.ModuleVisibilityAdminUITests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform
open ModuleVisibilityAdminUI

// ─── Module-visibility profile editor MVU tests ──────────────────────
//
// Drives `ModuleVisibilityAdminUI.update` through the editor's flow:
// init → the three loads → toggle / flip → save → clear, plus the two
// properties that are decisions rather than plumbing:
//
//   * a scope with NO stored profile seeds the IDENTITY rule (`Deny []`),
//     not `Allow []` — the two forms look identical and one of them hides
//     every governed module on the first Save;
//   * `ExcludedEntryIds` loaded from the stored profile are round-tripped
//     through a save. `SetProfile` replaces the whole document, so
//     dropping them would silently discard page-level narrowing this
//     slice does not edit.
//
// Fable-side (not .NET Expecto) for the same reason as
// `TenantLifecycleAdminUITests`: the module declares a module-level
// `let private visibilityApi = Api.makeProxy<IModuleVisibilityApi> …`,
// whose reflection-based proxy builder is shaped for Fable's runtime and
// raises under .NET reflection at static-init time.
//
// View rendering (Feliz, React `useState`, JSDOM) is not exercised here;
// the pure algebra (`ModuleVisibility.resolve` / `admitsModule`) is
// pinned .NET-side in `ModuleVisibilityContractTests.fs`.

let private registered = [ "finance"; "logistics"; "research" ]

let private storedProfile: ModuleVisibilityProfile = {
    Scope = FlagScope.Team "acme"
    Rule = ModuleVisibilityRule.Allow [ "finance"; "research" ]
    ExcludedEntryIds = [ "research/archive" ]
    Note = Some "finance family only"
}

/// Drive a model to the state the editor reaches once every load has
/// answered.
let private loaded (profile: ModuleVisibilityProfile option) =
    let m0, _ = init ()
    let m1, _ = update (RegisteredLoaded(Ok registered)) m0
    let m2, _ = update (ProfileLoaded(Ok profile)) m1
    m2

let tests =
    testList "ModuleVisibilityAdminUI" [
        testCase "init: nothing loaded, no banners, identity rule pending the fetch"
        <| fun _ ->
            let m0, _ = init ()
            Expect.equal m0.RegisteredModuleIds [] "init: no candidates yet"
            Expect.equal m0.StoredProfile None "init: no stored profile"
            Expect.equal m0.Resolved None "init: no resolution"
            Expect.isFalse m0.ResolvedLoaded "init: resolution not yet answered"
            Expect.isFalse m0.Loaded "init: profile not yet answered"
            Expect.isFalse m0.Busy "init: not busy"
            Expect.equal m0.Error None "init: no error banner"
            Expect.equal m0.Status None "init: no status banner"

        testCase "loading a stored profile seeds the editor from its rule, note and exclusions"
        <| fun _ ->
            let m = loaded (Some storedProfile)

            Expect.equal m.RegisteredModuleIds registered "candidates come from ListRegisteredModules"
            Expect.equal m.StoredProfile (Some storedProfile) "stored profile retained"
            Expect.isTrue m.Loaded "profile answered ⇒ editor may render"
            Expect.equal m.Kind RuleKind.Allow "an Allow rule seeds the Allow shape"
            Expect.equal m.Selection [ "finance"; "research" ] "selection is the rule's ids, in declared order"
            Expect.equal m.Note "finance family only" "note seeded from the profile"

            Expect.equal m.CarriedExcludedEntryIds [ "research/archive" ] "page-level exclusions carried, not dropped"

        testCase "a scope with no profile seeds the IDENTITY rule — an empty Deny, never an empty Allow"
        <| fun _ ->
            let m = loaded None

            // The whole point: `Deny []` removes nothing, so a first Save
            // is a deliberate no-op. `Allow []` selects nothing, and would
            // hide every governed module in one click.
            Expect.equal m.Kind RuleKind.Deny "fresh scope: deny shape"
            Expect.equal m.Selection [] "fresh scope: nothing named"
            Expect.equal m.Note "" "fresh scope: no note"
            Expect.equal m.CarriedExcludedEntryIds [] "fresh scope: no exclusions"
            Expect.equal m.StoredProfile None "fresh scope: nothing stored"

        testCase "toggling appends then removes, preserving the operator's declared order"
        <| fun _ ->
            let m0 = loaded None
            let m1, _ = update (ToggleModule "research") m0
            let m2, _ = update (ToggleModule "finance") m1
            Expect.equal m2.Selection [ "research"; "finance" ] "appended in the order they were named"

            let m3, _ = update (ToggleModule "research") m2
            Expect.equal m3.Selection [ "finance" ] "toggling again removes, leaving the rest in order"

        testCase "flipping Allow ⇄ Deny keeps the selection"
        <| fun _ ->
            let m0 = loaded (Some storedProfile)
            let m1, _ = update (SetKind RuleKind.Deny) m0

            Expect.equal m1.Kind RuleKind.Deny "shape flipped"

            Expect.equal
                m1.Selection
                [ "finance"; "research" ]
                "the same modules are named; only what naming them MEANS changed"

        testCase "save marks busy and clears stale banners; completion reloads"
        <| fun _ ->
            let m0 = loaded (Some storedProfile)

            let withStatus = {
                m0 with
                    Status = Some "stale"
                    Error = Some "stale"
            }

            let m1, _ = update (Save "  a fresh note  ") withStatus
            Expect.isTrue m1.Busy "save: busy while in flight"
            Expect.equal m1.Error None "save: stale error cleared"
            Expect.equal m1.Status None "save: stale status cleared"

            let m2, _ = update (SaveCompleted(Ok())) m1
            Expect.isFalse m2.Busy "save completed: busy cleared"
            Expect.equal m2.Status (Some "Profile saved.") "save completed: confirmation shown"
            Expect.equal m2.Error None "save completed: no error"

        testCase "save is refused before the profile has loaded, and while one is in flight"
        <| fun _ ->
            let m0, _ = init ()
            let m1, _ = update (Save "note") m0
            Expect.isFalse m1.Busy "unloaded: no save started"

            // A real save clears `Status`, so a Status that survives is
            // proof the guard fired rather than the arm running.
            let busy = {
                loaded None with
                    Busy = true
                    Status = Some "in flight"
            }

            let m2, _ = update (Save "note") busy
            Expect.equal m2.Status (Some "in flight") "already busy: the second save was refused, not run"

        testCase "the SetProfile payload round-trips exclusions the editor does not surface"
        <| fun _ ->
            let m0 = loaded (Some storedProfile)
            let input = profileInput "  a fresh note  " m0

            Expect.equal input.Rule (ModuleVisibilityRule.Allow [ "finance"; "research" ]) "payload: the edited rule"

            Expect.equal
                input.ExcludedEntryIds
                [ "research/archive" ]
                "payload: page-level exclusions survive a save made from this editor"

            Expect.equal input.Note (Some "a fresh note") "payload: note trimmed"

            // A blank note is absent, not an empty string — `Note` is
            // `string option` and "" would read as a note that says nothing.
            let blank = profileInput "   " m0
            Expect.equal blank.Note None "payload: a blank note is None"

            // Flipping the shape changes the rule and nothing else.
            let denied, _ = update (SetKind RuleKind.Deny) m0
            let denyInput = profileInput "" denied
            Expect.equal denyInput.Rule (ModuleVisibilityRule.Deny [ "finance"; "research" ]) "payload: deny shape"

            Expect.equal
                denyInput.ExcludedEntryIds
                [ "research/archive" ]
                "payload: exclusions unaffected by the rule shape"

        testCase "save failure surfaces the server message and releases the form"
        <| fun _ ->
            let m0 = loaded (Some storedProfile)
            let m1, _ = update (Save "note") m0
            let m2, _ = update (SaveCompleted(Error "team owner or admin required")) m1

            Expect.equal m2.Error (Some "team owner or admin required") "error: banner carries the server message"
            Expect.isFalse m2.Busy "error: form released"
            Expect.equal m2.Status None "error: no success confirmation"

            let m3, _ = update DismissError m2
            Expect.equal m3.Error None "dismiss: banner cleared"

        testCase "clear marks busy, then confirms; a failed clear releases the form"
        <| fun _ ->
            let m0 = loaded (Some storedProfile)
            let m1, _ = update Clear m0
            Expect.isTrue m1.Busy "clear: busy while in flight"

            let m2, _ = update (ClearCompleted(Ok())) m1
            Expect.isFalse m2.Busy "clear completed: busy cleared"
            Expect.isTrue m2.Status.IsSome "clear completed: confirmation shown"

            let m3, _ = update DismissStatus m2
            Expect.equal m3.Status None "dismiss: confirmation cleared"

            let m4, _ = update (ClearCompleted(Error "store unreachable")) m1
            Expect.equal m4.Error (Some "store unreachable") "clear error: banner carries the server message"
            Expect.isFalse m4.Busy "clear error: form released"

        testCase "the resolution loads independently of the profile and drives the per-module badge"
        <| fun _ ->
            let resolution: ModuleVisibilityResolution = {
                GovernedModuleIds = registered
                SelectedModuleIds = [ "finance" ]
                ExcludedEntryIds = []
                ContributingScopes = [ FlagScope.Platform; FlagScope.Team "acme" ]
            }

            let m0 = loaded (Some storedProfile)
            let m1, _ = update (ResolutionLoaded(Ok(Some resolution))) m0

            Expect.isTrue m1.ResolvedLoaded "resolution answered"
            Expect.equal m1.Resolved (Some resolution) "resolution retained"

            // The editor allows `research`, but an outer layer has already
            // removed it — precisely the "I allowed it and it still does
            // not appear" case the pane exists to answer.
            Expect.isTrue (List.contains "research" m1.Selection) "editor still names research"
            Expect.isTrue (ModuleVisibility.admitsModule resolution "finance") "finance survives the walk"
            Expect.isFalse (ModuleVisibility.admitsModule resolution "research") "research does not"

            // An `_sdk.` admin id is absent from the governed universe, so
            // it is admitted unconditionally — the lock-out guard.
            Expect.isTrue
                (ModuleVisibility.admitsModule resolution "_sdk.ModuleVisibilityAdmin")
                "the editor's own module is ungoverned and can never be hidden by a profile"

        testCase "an unconfigured deployment resolves to None without an error"
        <| fun _ ->
            let m0 = loaded None
            let m1, _ = update (ResolutionLoaded(Ok None)) m0

            Expect.isTrue m1.ResolvedLoaded "answered"
            Expect.equal m1.Resolved None "no layer declares a profile"
            Expect.equal m1.Error None "that is a valid answer, not a failure"

        testCase "a failed candidate fetch banners without blocking the editor"
        <| fun _ ->
            let m0, _ = init ()
            let m1, _ = update (RegisteredLoaded(Error "module visibility is not enabled")) m0

            Expect.equal m1.Error (Some "module visibility is not enabled") "banner carries the server message"
            Expect.equal m1.RegisteredModuleIds [] "no candidates"
    ]