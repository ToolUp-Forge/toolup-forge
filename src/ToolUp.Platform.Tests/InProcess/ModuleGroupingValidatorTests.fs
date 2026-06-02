module ToolUp.Platform.Tests.InProcess.ModuleGroupingValidatorTests

open Expecto
open ToolUp.Elmish
open Feliz
open ToolUp.Platform

// ─── Phase 55 — withGroup preflight validator ─────────────────────────
//
// Drives `ModuleGroupingValidator.result` (the pure decision) against
// hand-built `ErasedModule` values. `Icon` and `View` involve
// Fable-only render machinery (Feliz `ReactElement`); we never invoke
// either through the test path, so `Unchecked.defaultof` for the icon
// and `None` for view/pageviews keep the constructions valid for
// .NET-side runtime without touching the render layer.

let private erasedModule (id: string) (name: string) (group: string option) : ErasedModule = {
    Definition = {
        Id = id
        Name = name
        Icon = Unchecked.defaultof<ReactElement>
        Pages = []
    }
    Init = fun _ -> box (), Cmd.none
    Update = fun _ state -> state, Cmd.none
    View = None
    PageViews = None
    NeedsData = None
    DataTypes = []
    ProvidesProcessedData = None
    ProvidesNarrative = None
    Config = None
    FeatureFlags = []
    Availability = Always
    Group = group
    ClientQueryHandlers = []
    ActionDecoder = None
    Visibility = Visibility.visibleToAll
}

[<Tests>]
let tests =
    testList "Phase 55 — ModuleGroupingValidator" [

        test "empty module list → Ok (degenerate, not the bug shape this validator targets)" {
            let r = ModuleGroupingValidator.result []
            Expect.equal r (Ok()) "empty input is not a Group defect"
        }

        test "single module with withGroup → Ok" {
            let r =
                ModuleGroupingValidator.result [ erasedModule "M1" "First" (Some "Workflow") ]

            Expect.equal r (Ok()) "any single Group declaration satisfies the check"
        }

        test "single module without withGroup → Error naming the module" {
            // The single-module bring-up shape: one consumer module,
            // no withGroup call. Sidebar renders title-less collapsed
            // `_other` and the operator sees an empty shell.
            let r = ModuleGroupingValidator.result [ erasedModule "Convert" "Convert" None ]

            match r with
            | Error msg ->
                Expect.stringContains msg "Convert" "names the offending module"

                Expect.stringContains msg "ClientModule.withGroup" "points at the fix-site API surface"

                Expect.stringContains msg "_other" "explains the failure mode"
            | other -> failtestf "expected Error, got %A" other
        }

        test "multiple modules, none grouped → Error" {
            let r =
                ModuleGroupingValidator.result [
                    erasedModule "A" "Alpha" None
                    erasedModule "B" "Beta" None
                    erasedModule "C" "Gamma" None
                ]

            match r with
            | Error msg ->
                Expect.stringContains msg "Alpha" "names the first ungrouped module"

                Expect.isFalse (msg.Contains "Beta") "only the first module is named (concise)"
            | other -> failtestf "expected Error, got %A" other
        }

        test "multiple modules, at least one grouped → Ok" {
            // A single `withGroup` somewhere produces the declared
            // section header the title-less-collapse failure mode
            // depends on the absence of. One is enough.
            let r =
                ModuleGroupingValidator.result [
                    erasedModule "A" "Alpha" None
                    erasedModule "B" "Beta" (Some "Workflow")
                    erasedModule "C" "Gamma" None
                ]

            Expect.equal r (Ok()) "any Group declaration satisfies the check"
        }

        test "every module grouped → Ok" {
            let r =
                ModuleGroupingValidator.result [
                    erasedModule "A" "Alpha" (Some "Sales")
                    erasedModule "B" "Beta" (Some "Sales")
                    erasedModule "C" "Gamma" (Some "Marketing")
                ]

            Expect.equal r (Ok()) "fully-grouped deployments pass"
        }

        test "validate wrapper raises on Error" {
            // The Phase 13a wrapper turns `Error` into `failwithf` so
            // `Client.boot` aborts before the empty-sidebar render.
            Expect.throws
                (fun () -> ModuleGroupingValidator.validate [ erasedModule "M1" "Solo" None ])
                "ungrouped single-module deployment must throw"
        }

        test "validate wrapper passes on Ok" {
            // Sanity: the wrapper must not throw on a satisfied
            // deployment, otherwise every consumer would break.
            ModuleGroupingValidator.validate [ erasedModule "M1" "Solo" (Some "Workflow") ]
        }
    ]