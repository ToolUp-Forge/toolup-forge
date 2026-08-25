module ToolUp.Platform.Tests.InProcess.UnknownConfigKeyValidatorTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigKeys
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.UnknownConfigKeyValidator

// ─── Phase 695 — the unknown-config-key preflight guard ───────────────
//
// The name-level guard: a set `TOOLUP_*` variable that names no registry
// entry is read by nothing, so a deployment can believe it supplied a
// value it did not. What is asserted here, in the order the phase asks:
//
//   * an unrecognised name warns and names its nearest registered key;
//   * the two declared prefixes and the tooling class stay silent, as do
//     registered keys and every non-`TOOLUP_` variable in the environment;
//   * strict mode escalates the identical finding to a refusal, and reads
//     through the resolution seam so a manifest can declare it;
//   * an environment with nothing unrecognised in it returns `Ok`
//     (GP 11 — a deployment that has not tripped the guard sees no change).
//
// The pure `evaluate` is the subject wherever possible: it takes the
// environment as a list, so most of the behaviour is testable with no
// process-global state at all. The two cases that must exercise the real
// wiring — the production enumerator and the strict-mode read — restore
// what they touch on the way out.

/// Run `body` with one env var temporarily set, restoring the prior value.
let private withEnv (name: string) (value: string option) (body: unit -> unit) =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

/// Run `body` with a manifest supplying `values`, then return the process
/// to the no-manifest state.
let private withManifest (values: (string * string) list) (body: unit -> unit) =
    try
        ConfigResolution.install {
            Path = "test://manifest"
            Hash = "0000000000000000000000000000000000000000000000000000000000000000"
            Values = Map.ofList values
            PendingKeys = []
            Profile = None
        }

        body ()
    finally
        ConfigResolution.clear ()

let private message (result: ValidationResult) =
    match result with
    | Ok -> failtest "expected a finding, but the guard returned Ok"
    | Warning m -> m
    | Error m -> m

/// A registered key, taken from the registry rather than written down, so
/// this pack cannot outlive a rename.
let private aRegisteredKey = Names.authMode

let tests =
    testList "UnknownConfigKeyValidator" [
        testCase "an unrecognised TOOLUP_ variable warns and names its nearest registered key"
        <| fun _ ->
            let result = evaluate false [ "TOOLUP_AUTH_MOD" ]

            match result with
            | Warning m ->
                Expect.stringContains m "TOOLUP_AUTH_MOD" "the warning must name the variable that is set"

                Expect.stringContains
                    m
                    (sprintf "did you mean %s?" Names.authMode)
                    "the warning must suggest the nearest registered key — naming the typo without the intent leaves the operator to search the reference"
            | other -> failtestf "expected a Warning, got %A" other

        testCase "a name too far from every registered key is reported without a guessed suggestion"
        <| fun _ ->
            // The suggestion is a convenience, not an assertion. Offering
            // the "nearest" key for a name nothing resembles would dress a
            // guess as an answer, and an operator who followed it would be
            // sent to the wrong key.
            let m =
                evaluate false [ "TOOLUP_ZZZZ_COMPLETELY_UNRELATED_VARIABLE_NAME" ] |> message

            Expect.stringContains m "TOOLUP_ZZZZ_COMPLETELY_UNRELATED_VARIABLE_NAME" "the name must still be reported"

            Expect.isFalse
                (m.Contains "did you mean")
                "no suggestion should be offered for a name with no near neighbour"

        testCase "a case-only difference is reported as such, not as a near miss"
        <| fun _ ->
            // Environment variable names are case-sensitive on Linux, so
            // `toolup_auth_mode` there is genuinely read by nothing.
            // "did you mean TOOLUP_AUTH_MODE?" would understate a name that
            // is already correct apart from its case.
            for miscased in [ "toolup_auth_mode"; "TOOLUP_AUTH_mode" ] do
                let m = evaluate false [ miscased ] |> message

                Expect.stringContains m miscased "the miscased name must be reported"

                Expect.stringContains
                    m
                    "differs only in case"
                    "a case-only mismatch deserves its own, more precise message"

                Expect.stringContains m Names.authMode "the correctly-cased key must be named"

        testCase "registered keys, the two declared prefixes and tooling keys are all silent"
        <| fun _ ->
            let names = [
                // Registered — the definition of "known".
                aRegisteredKey
                Names.replicaCount
                Names.strictConfig
                // The two declared prefixes: registered under the prefix,
                // suffix supplied at runtime, so the full name is
                // unknowable here.
                Names.componentConfigPrefix + "Billing__Endpoint"
                Names.externalComputeHttpPrefix + "BASE_URL"
                // Tooling: read by the build / test run / analyzer, never
                // by a running server. A developer box carries these.
                Names.approveApi
                Names.emitSbom
                Names.testArgs
                Names.regenConfigReference
                // Not a config key at all.
                "PATH"
                "HOME"
                "DOTNET_ROOT"
            ]

            Expect.equal
                (evaluate false names)
                Ok
                "nothing in this environment is unaccounted for, so the guard must be silent"

        testCase "every tooling key is excluded, derived from the category rather than a second list"
        <| fun _ ->
            // The classification is derived from `ToolingCategory`, so a
            // descriptor added to that section is excluded with no second
            // edit. Quantifying over the whole class is what pins that.
            Expect.isNonEmpty toolingKeys "the tooling class must not be empty — an empty set would pass this vacuously"

            let reported = toolingKeys |> Set.toList |> unrecognisedNames

            Expect.isEmpty reported "no key in the tooling category may be reported by the guard"

            Expect.all (Set.toList toolingKeys) isToolingKey "isToolingKey must agree with the set it is derived from"

        testCase "an environment with no TOOLUP_ variables at all returns Ok (GP 11)"
        <| fun _ ->
            Expect.equal (evaluate false []) Ok "an empty environment is not a finding"
            Expect.equal (evaluate false [ "PATH"; "HOME" ]) Ok "non-TOOLUP variables are outside the guard's scope"

        testCase "strict mode escalates the identical finding to a startup refusal"
        <| fun _ ->
            let warned = evaluate false [ "TOOLUP_AUTH_MOD" ]
            let refused = evaluate true [ "TOOLUP_AUTH_MOD" ]

            match warned, refused with
            | Warning _, Error m ->
                Expect.stringContains m "TOOLUP_AUTH_MOD" "the refusal must name the variable"

                Expect.stringContains
                    m
                    Names.strictConfig
                    "the refusal must name the key that caused it, so the operator can find the lever they pulled"
            | w, r -> failtestf "expected Warning then Error, got %A then %A" w r

        testCase "strict mode refuses nothing when there is nothing to report"
        <| fun _ ->
            // The escalation changes the grade of a finding, never whether
            // there is one. A curated environment boots under strict mode.
            Expect.equal (evaluate true [ aRegisteredKey; "PATH" ]) Ok "strict mode must not invent a finding"

        testCase "strict mode is off by default and reads through the resolution seam"
        <| fun _ ->
            withEnv Names.strictConfig None (fun () ->
                Expect.isFalse (strictModeEnabled ()) "an unset key must leave the guard at warning grade"

                // Manifest-bindable, so a deployment can declare the
                // escalation in the same reviewable file as everything else
                // it declares.
                withManifest [ Names.strictConfig, "1" ] (fun () ->
                    Expect.isTrue (strictModeEnabled ()) "a manifest-declared 1 must enable strict mode"))

        testCase "strict mode accepts only the canonical truthy spellings"
        <| fun _ ->
            for truthy in [ "1"; "true"; "TRUE"; "yes"; "on"; " true " ] do
                withEnv Names.strictConfig (Some truthy) (fun () ->
                    Expect.isTrue (strictModeEnabled ()) (sprintf "%s must enable strict mode" truthy))

            for falsy in [ "0"; "false"; "no"; "off"; "maybe" ] do
                withEnv Names.strictConfig (Some falsy) (fun () ->
                    Expect.isFalse (strictModeEnabled ()) (sprintf "%s must not enable strict mode" falsy))

        testCase "the strict-mode key is registered, manifest-bindable and not itself a finding"
        <| fun _ ->
            // The coverage test would catch a missing descriptor, but not
            // that the guard excludes its own lever — which it must, or
            // turning it on would be the first thing it reported.
            Expect.isTrue
                (all |> List.exists (fun k -> k.EnvVar = Names.strictConfig))
                "TOOLUP_STRICT_CONFIG must carry a descriptor"

            Expect.isTrue
                (isManifestBindable Names.strictConfig)
                "TOOLUP_STRICT_CONFIG reads through the resolution seam, so it must be declared bindable"

            Expect.isEmpty (unrecognisedNames [ Names.strictConfig ]) "the guard must not report its own lever"

        testCase "findings are de-duplicated and sorted, so the message is stable across runs"
        <| fun _ ->
            let m =
                evaluate false [
                    "TOOLUP_ZEBRA_ZEBRA_ZEBRA"
                    "TOOLUP_ALPHA_ALPHA_ALPHA"
                    "TOOLUP_ZEBRA_ZEBRA_ZEBRA"
                ]
                |> message

            Expect.stringContains m "2 environment variable(s)" "the repeated name must be counted once"

            let alphaAt = m.IndexOf("TOOLUP_ALPHA_ALPHA_ALPHA", StringComparison.Ordinal)
            let zebraAt = m.IndexOf("TOOLUP_ZEBRA_ZEBRA_ZEBRA", StringComparison.Ordinal)

            Expect.isLessThan alphaAt zebraAt "names must be sorted, so two runs of one environment read identically"

        testCase "an empty-valued variable is treated as unset, matching every reader in the SDK"
        <| fun _ ->
            // Blanking a variable is how several orchestrators disable one.
            // A reader already sees that as unset, so the guard must not
            // report a name whose value nothing would have read anyway.
            withEnv "TOOLUP_DELIBERATELY_BLANKED" (Some "") (fun () ->
                let reported = environmentNameEnumerator () |> unrecognisedNames

                Expect.isFalse
                    (List.contains "TOOLUP_DELIBERATELY_BLANKED" reported)
                    "an empty-valued variable is unset by the SDK's own convention")

        testCase "the composed validator reports a real environment variable through the real enumerator"
        <| fun _ ->
            // The one end-to-end case: the pure core is covered above, but
            // a guard wired to an enumerator that reads nothing would pass
            // every one of those and report nothing in production.
            let v = validator ()

            Expect.equal v.Name "unknown-config-key" "the registration name is the validator's identity key"

            withEnv "TOOLUP_AUTH_MOD" (Some "oidc") (fun () ->
                let m = v.Validate() |> Async.RunSynchronously |> message

                Expect.stringContains m "TOOLUP_AUTH_MOD" "the composed validator must see the process environment")

        testCase "the edit distance is the plain algorithm, so a suggestion is never an artefact of the metric"
        <| fun _ ->
            Expect.equal (editDistance "" "") 0 "two empty strings are identical"
            Expect.equal (editDistance "abc" "abc") 0 "identical strings are distance 0"
            Expect.equal (editDistance "abc" "") 3 "deleting every character costs its length"
            Expect.equal (editDistance "" "abc") 3 "inserting every character costs its length"
            Expect.equal (editDistance "kitten" "sitting") 3 "the textbook case, so a regression here is unmistakable"
            Expect.equal (editDistance "TOOLUP_AUTH_MOD" Names.authMode) 1 "the motivating typo is one insertion away"
    ]