module ToolUp.Forms.Tests.InProcess.FormApiInHandlerGateTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Forms
open ToolUp.Forms.FormApi

// ─── Phase 627.E — `IFormApi`'s in-handler gate declarations ─────────
//
// `IFormApi` is the largest of the four records 627.E's out-of-scope
// finding named: sixteen `[<AllowAnonymous>]` methods, every one of them
// genuinely gated inside `FormApiHandler`, and every one of them sitting
// in `AuthorizationSurface.anonymousReachable` — the list whose entire
// value is that a genuine open door stands out in it. Sixteen
// non-findings in a list of that kind is how a real one hides.
//
// The declarations live in `FormsInHandlerGates`, beside the handler
// whose checks they describe. These cases pin that they are COMPLETE and
// that they name the right endpoints — a sweep that covered the wrong
// methods would move the headline count down just as convincingly.

let private componentId = ComponentId.create "toolup.forms"

let private surface () =
    AuthorizationSurface.ofApiRecord<IFormApi> componentId

let tests =
    testList "FormApi in-handler gates (627.E)" [

        test "all sixteen methods start in the anonymous-reachable headline" {
            // The premise. If `IFormApi` ever grows a real attribute gate
            // this number drops and the declarations below need pruning —
            // which is the right way round: a declaration can never
            // downgrade an entry that already carries a real gate, but a
            // stale one should still be noticed.
            Expect.equal
                (AuthorizationSurface.anonymousReachable (surface ()) |> List.length)
                16
                "every IFormApi method is [<AllowAnonymous>] at the dispatcher"
        }

        test "every method is declared, and the headline empties" {
            let before =
                AuthorizationSurface.anonymousReachable (surface ()) |> List.map _.Endpoint

            let resolved =
                surface ()
                |> AuthorizationSurface.resolveWithInHandlerGates (FormsInHandlerGates.formApi componentId)

            Expect.isEmpty
                (AuthorizationSurface.anonymousReachable resolved)
                "no IFormApi method is left overstating the anonymous surface"

            Expect.equal
                (AuthorizationSurface.gatedInHandler resolved |> List.map _.Endpoint)
                before
                "and the entries that moved are exactly the entries that were there — the check that a sweep did not cover the wrong methods"

            Expect.equal
                (AuthorizationSurface.anonymousAtAttributeLayer resolved |> List.length)
                16
                "the dispatcher-level audit question is unchanged — nothing here re-gated anything"
        }

        test "every declaration carries a rationale a reviewer can check" {
            let resolved =
                surface ()
                |> AuthorizationSurface.resolveWithInHandlerGates (FormsInHandlerGates.formApi componentId)

            for entry in AuthorizationSurface.gatedInHandler resolved do
                let token =
                    entry.Requires
                    |> List.tryFind (fun t -> t.StartsWith("gate:in-handler=", StringComparison.Ordinal))

                match token with
                | None -> failtestf "%s landed in gatedInHandler with no rationale token" entry.Endpoint
                | Some t ->
                    Expect.isGreaterThan
                        (t.Length - "gate:in-handler=".Length)
                        20
                        (sprintf
                            "%s's rationale is too short to say what the handler checks — a gate nobody can name is indistinguishable from a gate nobody wrote"
                            entry.Endpoint)
        }

        test "the declaration set names no endpoint the record does not carry" {
            // A stale declaration is inert by design, so it cannot break a
            // composition — which is exactly why it needs a test: a typo'd
            // endpoint would leave its method in the headline set and
            // nothing would say so.
            let endpoints = (surface ()).Exposed |> List.map _.Endpoint |> Set.ofList

            let strays =
                FormsInHandlerGates.formApi componentId
                |> List.map _.GatedEndpoint
                |> List.filter (endpoints.Contains >> not)

            Expect.isEmpty
                strays
                "a declaration names an IFormApi endpoint that does not exist — it would silently do nothing"
        }
    ]