module ToolUp.Platform.Tests.InProcess.InvariantRuleManifestTests

open Expecto
open ToolUp.Platform

// ─── Phase 294 — composition invariant rule-manifest (as data) ────────
//
// `CompositionValidator.ruleManifest` exposes the Phase 281 rule set as
// introspectable data (code + severity + description). These tests lock
// the manifest to the runtime check: every shipped rule appears in the
// manifest with a stable code, and the runtime check + the manifest cannot
// diverge — a rule reachable at runtime but absent from the manifest (or a
// manifest rule no input can trigger) fails the bijection test. Both derive
// from the same `rules` list, so the guarantee is structural; the test is
// the guard that a future edit keeps the crafted-fixture coverage honest.

/// A raw companion-impl entry with a caller-chosen Id — two impls in one
/// slot sharing a sub-id with distinct Ids trigger the slot-legality rule.
let private companionImpl (rawId: string) (slot: string) (subId: string) : ComponentEntry = {
    Id = ComponentId.create rawId
    Kind = CompanionComponent
    Label = slot
    Impl = Some subId
}

/// The stable code + a crafted (manifest, refs) input that triggers exactly
/// that rule — the fixture set that must stay in lock-step with `rules`.
let private ruleFixtures: (string * CompositionManifest * CompositionReferences) list = [
    "duplicate-component-id",
    CompositionManifest.build
        [
            CompositionManifest.moduleEntry ("A", ComponentId.ofModule "dup")
            CompositionManifest.moduleEntry ("B", ComponentId.ofModule "dup")
        ]
        []
        [] [] [],
    CompositionReferences.empty

    "companion-slot-legality",
    CompositionManifest.build
        []
        [
            companionImpl "companion:IAuditSink/s#a" "IAuditSink" "s"
            companionImpl "companion:IAuditSink/s#b" "IAuditSink" "s"
        ]
        [] [] [],
    CompositionReferences.empty

    "orphaned-tool-reference",
    CompositionManifest.build [ CompositionManifest.moduleEntry ("M", ComponentId.ofModule "M") ] [] [] [
        CompositionManifest.toolEntry "t"
    ] [],
    { ToolSources = [ "t", "Ghost" ] }
]

let tests =
    testList "InvariantRuleManifest" [

        // ── the manifest enumerates every rule with a stable code ─────
        testCase "ruleManifest exposes every rule with a unique, stable code"
        <| fun _ ->
            let codes = CompositionValidator.ruleManifest |> List.map _.Code

            Expect.isNonEmpty codes "the rule manifest is not empty"
            Expect.equal (List.distinct codes).Length codes.Length "every rule code is unique"

            // The stable, published codes. Changing this set is a deliberate
            // contract change (an external checker keys off these) — this
            // assertion forces the update to be conscious.
            Expect.equal
                (Set.ofList codes)
                (Set.ofList [
                    "duplicate-component-id"
                    "companion-slot-legality"
                    "orphaned-tool-reference"
                ])
                "the published rule codes are exactly the shipped invariant set"

        // ── every descriptor is well-formed ──────────────────────────
        testCase "every rule descriptor carries a non-empty description"
        <| fun _ ->
            for descriptor in CompositionValidator.ruleManifest do
                Expect.isFalse
                    (System.String.IsNullOrWhiteSpace descriptor.Description)
                    (sprintf "rule '%s' has a non-empty description" descriptor.Code)

        // ── runtime check and manifest cannot diverge (bijection) ────
        testCase "the runtime check and the manifest enumerate the same rule codes"
        <| fun _ ->
            let manifestCodes =
                CompositionValidator.ruleManifest |> List.map _.Code |> Set.ofList

            // Every code the runtime emits across the crafted fixtures — one
            // fixture per rule, each triggering exactly its rule.
            let emittedCodes =
                ruleFixtures
                |> List.collect (fun (_, manifest, refs) ->
                    CompositionValidator.checkWith refs manifest |> List.map _.RuleCode)
                |> Set.ofList

            // Bijection: no runtime code is absent from the manifest (a rule
            // added to the check without the manifest), and no manifest code
            // is unreachable (a manifest entry with no runtime rule / no
            // fixture). Both sides derive from `rules`, so a real divergence
            // is impossible — this fails first when the fixture set falls out
            // of step with a newly-added rule.
            Expect.equal emittedCodes manifestCodes "runtime-emitted codes are exactly the manifest codes"

        // ── each fixture triggers exactly its own rule ───────────────
        testCase "each crafted fixture triggers its named rule"
        <| fun _ ->
            for expectedCode, manifest, refs in ruleFixtures do
                let emitted =
                    CompositionValidator.checkWith refs manifest
                    |> List.map _.RuleCode
                    |> List.distinct

                Expect.contains emitted expectedCode (sprintf "fixture for '%s' triggers its rule" expectedCode)
    ]