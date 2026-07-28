module ToolUp.Platform.Tests.InProcess.PreflightRuleClassTests

open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ConfigValidatorAggregator

// ─── Phase 585 — security-classified preflight rule classes ───────────
//
// `ServerConfig.SkipPreflight` exists so an emergency boot can ride past
// an external probe whose dependency is down (a storage sentinel, OIDC
// discovery, an SMTP connect). Before this phase the composition
// validator's identity / integrity rules rode the same switch, so the
// operator who set it to survive a storage outage also, silently, booted
// with `duplicate-component-id` / `companion-slot-legality` /
// `orphaned-tool-reference` switched off.
//
// The split is marker-derived, never a name set: a validator carrying
// `IStructuralClassValidator` (or `ISecurityClassValidator`) always runs;
// everything unmarked is external-probe class and stays skippable. These
// tests pin both halves — the structural check survives `SkipPreflight`,
// the external probe does not — plus the manifest projection an external
// checker reads to learn which rules are unconditional.

/// The composed surface a deployment gets wrong: two modules resolving to
/// the same `ComponentId`. Every introspection / telemetry-correlation
/// surface keys on that id, so the collision is a real defect no outage
/// explains.
let private duplicateIdManifest =
    CompositionManifest.build
        [
            CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "shared")
            CompositionManifest.moduleEntry ("Inventory", ComponentId.ofModule "shared")
        ]
        []
        [] [] []

let private wellFormedManifest =
    CompositionManifest.build
        [
            CompositionManifest.moduleEntry ("Orders", ComponentId.ofModule "Orders")
            CompositionManifest.moduleEntry ("Inventory", ComponentId.ofModule "Inventory")
        ]
        []
        [] [] []

/// Stands in for the class `SkipPreflight` was designed to bypass: an
/// unmarked probe that reaches a dependency and reports whatever the
/// caller wants it to.
let private storageSentinelProbe (result: ValidationResult) =
    { new IConfigValidator with
        member _.Name = "storage-sentinel"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return result }
    }

/// The registration shape a real composition root produces: the
/// first-party composition validator(s) via `serviceRegistration`, plus a
/// companion probe.
let private services (manifest: CompositionManifest) (probeResult: ValidationResult) =
    let sc = ServiceCollection()

    CompositionValidator.serviceRegistration manifest CompositionReferences.empty sc
    |> ignore

    sc.AddSingleton<IConfigValidator>(storageSentinelProbe probeResult) |> ignore
    sc

let private sentinelDown: ValidationResult =
    Error "sentinel write failed: blob endpoint unreachable"

let tests =
    testList "PreflightRuleClass" [

        // ── the phase's acceptance shape, both halves in one run ──────
        testCase "SkipPreflight = true — a duplicate ComponentId still aborts, the storage sentinel is skipped"
        <| fun _ ->
            try
                validate (services duplicateIdManifest sentinelDown) None true |> ignore

                Expect.isTrue
                    false
                    "expected ConfigPreflightFailedException — the structural identity rule must not be bypassable"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains
                    ex.Message
                    "duplicate-component-id"
                    "the structural rule that fired is named in the abort summary"

                Expect.isFalse
                    (ex.Message.Contains "storage-sentinel")
                    "the external probe was skipped, not run — SkipPreflight still does the job it exists for"

        testCase "SkipPreflight = true — a well-formed composition boots, external probe still skipped"
        <| fun _ ->
            // The emergency boot must still succeed: running the structural
            // rules unconditionally only costs a boot when the composition
            // is genuinely malformed.
            let outcomes = validate (services wellFormedManifest sentinelDown) None true
            let ran = outcomes |> List.map _.Name |> List.sort

            Expect.equal
                ran
                [ CompositionValidator.ValidatorName ]
                "only the structural composition validator ran; the sentinel probe was skipped"

            Expect.all
                outcomes
                (fun o -> ValidationResult.status o.Result = "Ok")
                "a well-formed composition passes the structural rules"

        // ── GP 11: the default path is exactly what it was ────────────
        testCase "SkipPreflight = false — the external probe runs and its Error still aborts (default path)"
        <| fun _ ->
            try
                validate (services wellFormedManifest sentinelDown) None false |> ignore
                Expect.isTrue false "expected the external probe's Error to abort on the default path"
            with :? ConfigPreflightFailedException as ex ->
                Expect.stringContains
                    ex.Message
                    "storage-sentinel"
                    "on the default path the external probe runs and its Error still aborts startup"

        testCase "SkipPreflight = false — the class split adds no registration"
        <| fun _ ->
            let outcomes = validate (services wellFormedManifest Ok) None false
            let ran = outcomes |> List.map _.Name |> List.sort

            Expect.equal
                ran
                [ CompositionValidator.ValidatorName; "storage-sentinel" ]
                "one composition validator (structural) plus the probe — the empty external-probe class registers nothing"

        // ── the classification is marker-derived, not name-derived ────
        testCase "the composition well-formedness validator carries IStructuralClassValidator"
        <| fun _ ->
            let v =
                CompositionValidator.CompositionWellFormednessValidator(wellFormedManifest, CompositionReferences.empty)
                :> IConfigValidator

            Expect.equal (classify v) StructuralClass "the composition validator classifies as structural"
            Expect.isFalse (alwaysRuns ExternalProbeClass) "the external-probe class is the only skippable one"
            Expect.isTrue (alwaysRuns StructuralClass) "structural-class validators are unconditional"
            Expect.isTrue (alwaysRuns SecurityClass) "security-class validators stay unconditional"

        testCase "an unmarked probe is external-probe class (GP 11 — no impl-site change)"
        <| fun _ ->
            Expect.equal
                (classify (storageSentinelProbe Ok))
                ExternalProbeClass
                "absence of a marker is the skippable classification, so pre-585 validators are unaffected"

        // ── the manifest exports each rule's class ────────────────────
        testCase "classifiedRuleManifest covers exactly the Phase 294 manifest, with a class per rule"
        <| fun _ ->
            let classified = CompositionValidator.classifiedRuleManifest

            Expect.equal
                (classified |> List.map _.Code)
                (CompositionValidator.ruleManifest |> List.map _.Code)
                "the classified manifest enumerates the same rules, in the same order, as ruleManifest"

            for descriptor in CompositionValidator.ruleManifest do
                let entry = classified |> List.find (fun c -> c.Code = descriptor.Code)
                Expect.equal entry.Severity descriptor.Severity "severity matches the Phase 294 descriptor"
                Expect.equal entry.Description descriptor.Description "description matches the Phase 294 descriptor"

        testCase "every shipped rule is structural — and says so via tryRuleClass"
        <| fun _ ->
            // If a rule ever lands in the external-probe class this fails,
            // which is the point: making a composition invariant skippable
            // must be a conscious edit, not a by-product.
            for entry in CompositionValidator.classifiedRuleManifest do
                Expect.equal entry.Class StructuralRule (sprintf "rule '%s' is a pure in-process invariant" entry.Code)

                Expect.equal
                    (CompositionValidator.tryRuleClass entry.Code)
                    (Some entry.Class)
                    (sprintf "tryRuleClass agrees with the manifest for '%s'" entry.Code)

            Expect.isNone
                (CompositionValidator.tryRuleClass "no-such-rule")
                "an unknown code classifies as None, never as a guessed class"

        testCase "no rule escapes classification — rules is exactly the two class lists"
        <| fun _ ->
            Expect.equal
                (CompositionValidator.rules |> List.map _.Code)
                ((CompositionValidator.structuralRules @ CompositionValidator.externalProbeRules)
                 |> List.map _.Code)
                "every declared rule sits in exactly one class list; `rules` is their concatenation"

            Expect.equal
                (CompositionValidator.checkClassWith StructuralRule CompositionReferences.empty duplicateIdManifest
                 |> List.map _.RuleCode)
                (CompositionValidator.check duplicateIdManifest |> List.map _.RuleCode)
                "with no external-probe rules shipped, the structural class evaluates the whole rule set"
    ]