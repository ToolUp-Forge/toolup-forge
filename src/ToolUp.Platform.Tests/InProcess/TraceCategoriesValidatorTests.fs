module ToolUp.Platform.Tests.InProcess.TraceCategoriesValidatorTests

open System.Text.Json
open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.TraceCategoriesValidator
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 9m.C (Gap 11) — the value-level trace-category guard ───────
//
// `TOOLUP_TRACE_CATEGORIES` carries a whitelist matched by exact string
// membership, so a misspelt value is silent at every layer: the key name
// is registered (Phase 695's guard is correctly quiet), the logger emits
// nothing under the value, and the operator reads that silence as an
// idle subsystem. What is asserted here, in the order the phase asks:
//
//   * a typo'd value warns, naming the unmatched value AND the canonical
//     list — the acceptance sentence's `webhoks,jobs` shape;
//   * an all-known list returns `Ok`;
//   * an empty configured list returns `Ok`;
//   * an empty registry returns `Ok` — the GP 13 arm: an emission site
//     that declares nothing must not turn the validator red;
//   * a case-only difference is reported as exactly that, because the
//     whitelist is ordinal at emission time so the value really does
//     emit nothing;
//   * the registry itself is additive, ordinal-sorted and idempotent;
//   * the `/dev/inspect` panel renders under its contract name with a
//     per-category enabled marker.
//
// The pure `evaluate` is the subject wherever it can be: it takes both
// sides as plain values, so nearly all of the behaviour is testable with
// no process-global state at all. The one list that must touch the real
// registry is sequenced and restores what it found.

let private canonical = [ "ai.agent"; "jobs"; "webhooks" ]

let private message (result: ValidationResult) =
    match result with
    | Ok -> failtest "expected a finding, but the guard returned Ok"
    | Warning m -> m
    | Error m -> m

let tests =
    testList "TraceCategoriesValidator" [

        // ── the acceptance sentence, verbatim ─────────────────────────
        testCase "a typo'd value warns, naming the unmatched value and the canonical list"
        <| fun _ ->
            let result = evaluate canonical (Set.ofList [ "webhoks"; "jobs" ])

            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning, got %s" (ValidationResult.status other)

            let m = message result
            Expect.stringContains m "webhoks" "the unmatched value is named"
            Expect.stringContains m "ai.agent" "the canonical list is named"
            Expect.stringContains m "webhooks" "the canonical list carries the correctly-spelt sibling"
            Expect.stringContains m "TOOLUP_TRACE_CATEGORIES" "the key that carries the value is named"

            Expect.stringContains m "names 1 value(s)" "only the unmatched value is counted — `jobs` is known"

        testCase "a wholly-known list returns Ok"
        <| fun _ ->
            let result = evaluate canonical (Set.ofList [ "ai.agent"; "webhooks" ])
            Expect.equal result Ok "every configured value matches a declared category"

        testCase "an empty configured list returns Ok"
        <| fun _ ->
            // The default posture: no Trace output was asked for, so
            // there is nothing to be wrong about.
            Expect.equal (evaluate canonical Set.empty) Ok "nothing configured"

        testCase "an empty registry returns Ok even with values configured (GP 13)"
        <| fun _ ->
            // The fail-open arm. Registration is opt-in and additive: a
            // deployment whose emission sites declare nothing has no
            // canonical list to be measured against, and reporting it
            // would be a finding about the SDK's adoption rather than
            // about this deployment.
            Expect.equal (evaluate [] (Set.ofList [ "webhoks"; "anything" ])) Ok "nothing declared, nothing reported"

        testCase "a case-only difference is reported as a case difference, not as an unknown name"
        <| fun _ ->
            let m = message (evaluate canonical (Set.ofList [ "AI.Agent" ]))
            Expect.stringContains m "AI.Agent" "the configured value is named"
            Expect.stringContains m "apart from case" "the finding says the difference is case, not spelling"
            Expect.stringContains m "ai.agent" "the correctly-cased category is named"

        testCase "every unmatched value is listed, not just the first"
        <| fun _ ->
            let m = message (evaluate canonical (Set.ofList [ "webhoks"; "jbos"; "ai.agent" ]))
            Expect.stringContains m "webhoks" "first typo listed"
            Expect.stringContains m "jbos" "second typo listed"

        // ── the IConfigValidator wrapper ──────────────────────────────
        testCase "the validator carries the stable registration name"
        <| fun _ ->
            let v = TraceCategoriesValidator(Set.empty, fun () -> canonical) :> IConfigValidator

            Expect.equal v.Name "trace-categories" "stable identity key"

        testAsync "the validator reads the registry through the thunk at Validate() time, not at construction" {
            // Construction order must not matter: compose builds the
            // validator and registers categories in an order no
            // validator should have to depend on.
            let mutable declared = []

            let v =
                TraceCategoriesValidator(Set.ofList [ "late" ], (fun () -> declared)) :> IConfigValidator

            // Empty registry at construction — and still empty here.
            let! before = v.Validate()
            Expect.equal before Ok "empty registry is Ok"

            declared <- [ "early" ]
            let! after = v.Validate()

            match after with
            | Warning m ->
                Expect.stringContains m "late" "the late registration is what the second call measured against"
            | other -> failtestf "expected Warning after registration, got %s" (ValidationResult.status other)
        }

        testAsync "the validator never returns Error — a misspelt trace filter is not a boot refusal" {
            let v =
                TraceCategoriesValidator(Set.ofList [ "nonsense"; "more-nonsense" ], (fun () -> canonical))
                :> IConfigValidator

            let! result = v.Validate()

            match result with
            | Error m -> failtestf "the guard must never refuse a boot, but returned Error: %s" m
            | Warning _
            | Ok -> ()
        }

        // ── the /dev/inspect panel ────────────────────────────────────
        testAsync "the contributor renders under the panel name 'Trace categories' with an enabled marker" {
            let contrib =
                TraceCategoriesContributor(Set.ofList [ "ai.agent"; "webhoks" ], (fun () -> canonical))
                :> IDevDiagnosticsContributor

            let! (panelName, payload) = contrib.Contribute()

            Expect.equal panelName "Trace categories" "panel name matches contract"
            Expect.isNotNull payload "payload non-null"

            // Same posture as the SSE-trace panel's test: assert over the
            // serialised shape rather than reflecting into an anonymous
            // record, so the test pins what the operator actually reads.
            let json = JsonSerializer.Serialize(payload, FableConverters.shared)
            Expect.stringContains json "Categories" "shape includes the declared list"
            Expect.stringContains json "Enabled" "each category carries an enabled marker"
            Expect.stringContains json "Unmatched" "configured-but-undeclared values are surfaced too"
            Expect.stringContains json "ai.agent" "a declared category appears"
            Expect.stringContains json "webhoks" "the unmatched configured value appears"
            Expect.stringContains json "RegisteredCount" "the summary block is present"
        }

        // ── the registry itself (process-global — sequenced) ──────────
        testSequenced
        <| testList "Logger trace-category registry" [
            testCase "registration is additive, trimmed, idempotent and ordinal-sorted"
            <| fun _ ->
                let saved = Logger.registeredCategories ()

                try
                    Logger.clearRegisteredCategories ()
                    Expect.isEmpty (Logger.registeredCategories ()) "cleared"

                    Logger.registerCategory "zeta"
                    Logger.registerCategory "alpha"
                    Logger.registerCategory "  alpha  " // trimmed → same entry
                    Logger.registerCategory "alpha" // idempotent
                    Logger.registerCategory "" // ignored
                    Logger.registerCategory "   " // ignored
                    Logger.registerCategory null // ignored

                    Expect.equal
                        (Logger.registeredCategories ())
                        [ "alpha"; "zeta" ]
                        "sorted, de-duplicated, whitespace-only declarations ignored"
                finally
                    // Restore whatever a concurrently-composed server had
                    // declared — this list is sequenced, but the registry
                    // is process-global and not ours to empty.
                    Logger.clearRegisteredCategories ()
                    saved |> List.iter Logger.registerCategory

            testCase "the production validator shape measures the live registry"
            <| fun _ ->
                let saved = Logger.registeredCategories ()

                try
                    Logger.clearRegisteredCategories ()
                    Logger.registerCategory "ai.agent"

                    let config = {
                        ServerConfig.defaults with
                            TraceCategories = Set.ofList [ "ai.agnet" ]
                    }

                    let result = validator config |> _.Validate() |> Async.RunSynchronously

                    let m = message result
                    Expect.stringContains m "ai.agnet" "the configured typo is named"
                    Expect.stringContains m "ai.agent" "the live registry supplied the canonical list"
                finally
                    Logger.clearRegisteredCategories ()
                    saved |> List.iter Logger.registerCategory
        ]
    ]