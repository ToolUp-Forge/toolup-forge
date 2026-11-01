// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ServerAppOrderTests

// ─── Phase 1052 — what the builders accumulate survives the builder order ─
//
// A companion's DI registrations are a `Contribution` on `ServerApp`, and a
// deployment's own are added with `ServerApp.addServiceConfig`. Both land on
// `ServerApp.Contributions`, which `run` folds after the consumer's
// `Extensions.ServiceConfig` in the order added — the one fixed point these
// tests read through `ServerApp.serviceConfig`. Pinned here:
//
//   1. `ServiceConfig.append` is the one fold, and it composes in order.
//   2. Two contributions from two owners register both, in the order added,
//      and keep their owners.
//   3. `addServiceConfig` then `addContribution`, and the reverse, register
//      both in the order added.
//   4. A trailing `withExtensions` (the builder that SETS the record) keeps
//      both kinds — and keeps a platform builder's registration
//      (`withCspContributor`), which used to be discarded by it.
//   5. A builder that writes `ServerConfig` records an override `run` folds
//      once, so `withConfig` on either side of it keeps the write; the
//      middleware builders contribute, so `withExtensions` keeps them.
//   6. The contribution plan runs providers before requirers, keeps the
//      order added otherwise, and refuses an unmet requirement or a cycle
//      naming both ends.

open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform

type private MarkerA() = class end
type private MarkerB() = class end
type private MarkerC() = class end

let private register<'T when 'T: not struct> (instance: 'T) (services: IServiceCollection) =
    services.AddSingleton<'T>(instance)

/// The service types the app's registrations add, in order.
let private registered (app: ServerApp) : System.Type list =
    let services = ServiceCollection()

    match ServerApp.serviceConfig app with
    | Some configure -> configure services |> ignore
    | None -> ()

    services |> Seq.map _.ServiceType |> List.ofSeq

let private ownerA = ComponentId.forCompanionSlot "test.a"
let private ownerB = ComponentId.forCompanionSlot "test.b"

let tests =
    testList "ServerApp contributions, overrides and builder order" [
        testCase "ServiceConfig.append starts a chain and appends in order"
        <| fun () ->
            let chain =
                None
                |> ServiceConfig.append (register (MarkerA()))
                |> ServiceConfig.append (register (MarkerB()))

            let services = ServiceCollection()

            match chain with
            | Some configure -> configure services |> ignore
            | None -> failtest "append onto None must start a chain"

            Expect.equal
                (services |> Seq.map _.ServiceType |> List.ofSeq)
                [ typeof<MarkerA>; typeof<MarkerB> ]
                "the earlier registration runs first"

        testCase "an empty app contributes nothing"
        <| fun () ->
            Expect.isNone (ServerApp.serviceConfig ServerApp.empty) "no contribution, no consumer config"
            Expect.isEmpty ServerApp.empty.Contributions "no contribution"

        testCase "two contributions from two owners register both, in the order added"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.addContribution (Contribution.service ownerA (register (MarkerA())))
                |> ServerApp.addContribution (Contribution.service ownerB (register (MarkerB())))

            Expect.equal (registered app) [ typeof<MarkerA>; typeof<MarkerB> ] "both, in the order added"

            Expect.equal (app.Contributions |> List.map _.Owner) [ ownerA; ownerB ] "each contribution keeps its owner"

        testCase "addServiceConfig then addContribution registers both, in that order"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.addServiceConfig (register (MarkerA()))
                |> ServerApp.addContribution (Contribution.service ownerB (register (MarkerB())))

            Expect.equal (registered app) [ typeof<MarkerA>; typeof<MarkerB> ] "both, in the order added"

        testCase "addContribution then addServiceConfig registers both, in that order"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.addContribution (Contribution.service ownerB (register (MarkerB())))
                |> ServerApp.addServiceConfig (register (MarkerA()))

            Expect.equal (registered app) [ typeof<MarkerB>; typeof<MarkerA> ] "both, in the order added"

        testCase "a trailing withExtensions keeps both, after the consumer's own ServiceConfig"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.addServiceConfig (register (MarkerA()))
                |> ServerApp.addContribution (Contribution.service ownerB (register (MarkerB())))
                |> ServerApp.withExtensions {
                    ComposeExtensions.empty with
                        ServiceConfig = Some(register (MarkerC()))
                }

            Expect.equal
                (registered app)
                [ typeof<MarkerC>; typeof<MarkerA>; typeof<MarkerB> ]
                "the consumer's record is set, the contributions survive and run after it"

        testCase "a platform builder's registration survives a later withExtensions"
        <| fun () ->
            let contributor =
                { new ICspContributor with
                    member _.RequiredSources = []
                }

            let app =
                ServerApp.empty
                |> ServerApp.withCspContributor contributor
                |> ServerApp.withExtensions ComposeExtensions.empty

            Expect.equal (registered app) [ typeof<ICspContributor> ] "withCspContributor is not discarded"

        testCase "withConfig after the config-writing builders keeps what they wrote"
        <| fun () ->
            let categories =
                ServerModule.create "Reports"
                |> ServerModule.withNotificationCategories [ NotificationCategory.create "reports.summary" "Summaries" ]

            let config = {
                ServerConfig.defaults with
                    PeerRoutePrefixes = [ "/api/peer/consumer" ]
            }

            let builders (app: ServerApp) =
                app
                |> ServerApp.withPeerRoutePrefix "/api/peer/echo"
                |> ServerApp.withSeedData EnabledSeedData
                |> ServerApp.withAuditFallbackDirectory "audit-fallback"
                |> ServerApp.addModule categories

            let configFirst =
                ServerApp.empty
                |> ServerApp.withConfig config
                |> builders
                |> ServerApp.effectiveConfig

            let configLast =
                ServerApp.empty
                |> builders
                |> ServerApp.withConfig config
                |> ServerApp.effectiveConfig

            for effective in [ configFirst; configLast ] do
                Expect.equal
                    effective.PeerRoutePrefixes
                    [ "/api/peer/consumer"; "/api/peer/echo" ]
                    "the consumer's prefix, then the builder's"

                Expect.equal effective.SeedData EnabledSeedData "the set-one-field builder survives"
                Expect.equal effective.AuditFallbackDirectory (Some "audit-fallback") "and this one"

                Expect.equal
                    (effective.NotificationCategories |> List.map _.Id)
                    [ "reports.summary" ]
                    "the module's declarations survive"

                Expect.equal effective.ModuleNames [ "Reports" ] "the module is composed"

        testCase "withConfig holds only the consumer's value; the builders' writes are overrides"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.withSeedData EnabledSeedData
                |> ServerApp.withConfig ServerConfig.defaults

            Expect.equal app.Config.SeedData ServerConfig.defaults.SeedData "Config is what withConfig set"
            Expect.equal (app.Overrides |> List.map _.Field) [ "SeedData" ] "the write is recorded by field"

        testCase "a middleware builder survives a later withExtensions, after the consumer's own"
        <| fun () ->
            let consumers: IApplicationBuilder -> IApplicationBuilder = fun b -> b
            let pre: IApplicationBuilder -> IApplicationBuilder = fun b -> b

            let app =
                ServerApp.empty
                |> ServerApp.withPreMiddleware pre
                |> ServerApp.withPostMiddleware pre
                |> ServerApp.withExtensions {
                    ComposeExtensions.empty with
                        PreMiddleware = [ consumers ]
                }

            let composed = ServerApp.composedExtensions app
            Expect.hasLength composed.PreMiddleware 2 "the consumer's thunk, then the builder's"
            Expect.isTrue (obj.ReferenceEquals(composed.PreMiddleware[0], consumers)) "the consumer's first"
            Expect.isTrue (obj.ReferenceEquals(composed.PreMiddleware[1], pre)) "the builder's after it"
            Expect.hasLength composed.PostMiddleware 1 "the post thunk too"

        testCase "a plan with no seams keeps the order added"
        <| fun () ->
            let plan = [
                Contribution.service ownerA (register (MarkerA()))
                Contribution.service ownerB (register (MarkerB()))
            ]

            match ContributionPlan.order plan with
            | Ok ordered -> Expect.equal (ordered |> List.map _.Owner) [ ownerA; ownerB ] "unchanged"
            | Error refusal -> failtestf "refused: %s" (ContributionRefusal.describe refusal)

        testCase "a provider runs before the contribution that requires its seam"
        <| fun () ->
            let seam = SeamId "IMarker"

            let app =
                ServerApp.empty
                |> ServerApp.addContribution (
                    Contribution.service ownerA (register (MarkerA()))
                    |> Contribution.requires [ seam ]
                )
                |> ServerApp.addContribution (
                    Contribution.service ownerB (register (MarkerB()))
                    |> Contribution.provides [ seam ]
                )

            Expect.equal (registered app) [ typeof<MarkerB>; typeof<MarkerA> ] "the provider first"
            Expect.equal (app.Contributions |> List.map _.Owner) [ ownerA; ownerB ] "the record keeps the order added"

        testCase "an unmet requirement refuses, naming the requirer and the seam"
        <| fun () ->
            let plan = [
                Contribution.service ownerA (register (MarkerA()))
                |> Contribution.requires [ SeamId "IAbsent" ]
            ]

            match ContributionPlan.order plan with
            | Error(UnmetRequirement(requirer, seam)) ->
                Expect.equal requirer ownerA "the requirer"
                Expect.equal seam (SeamId "IAbsent") "the seam"

                let message = ContributionRefusal.describe (UnmetRequirement(requirer, seam))
                Expect.stringContains message (ComponentId.value ownerA) "the message names the requirer"
                Expect.stringContains message "IAbsent" "and the seam"
            | other -> failtestf "expected an unmet requirement, got %A" (Result.map List.length other)

        testCase "a cycle refuses, naming both ends of every link"
        <| fun () ->
            let ownerC = ComponentId.forCompanionSlot "test.c"
            let nameA = ComponentId.value ownerA
            let nameB = ComponentId.value ownerB

            let plan = [
                Contribution.service ownerC (register (MarkerC()))
                Contribution.service ownerA (register (MarkerA()))
                |> Contribution.requires [ SeamId "IB" ]
                |> Contribution.provides [ SeamId "IA" ]
                Contribution.service ownerB (register (MarkerB()))
                |> Contribution.requires [ SeamId "IA" ]
                |> Contribution.provides [ SeamId "IB" ]
            ]

            match ContributionPlan.order plan with
            | Error(RequirementCycle edges) ->
                Expect.equal
                    (edges |> List.map fst |> Set.ofList)
                    (Set.ofList [ ownerA; ownerB ])
                    "the loop, and only it"

                let message = ContributionRefusal.describe (RequirementCycle edges)

                Expect.stringContains
                    message
                    (sprintf "'%s' requires 'IB' (provided by '%s')" nameA nameB)
                    "a link names both ends"

                Expect.stringContains
                    message
                    (sprintf "'%s' requires 'IA' (provided by '%s')" nameB nameA)
                    "and so does the other"
            | other -> failtestf "expected a cycle, got %A" (Result.map List.length other)

        testCase "a refused plan refuses the composition"
        <| fun () ->
            let app =
                ServerApp.empty
                |> ServerApp.addContribution (
                    Contribution.service ownerA (register (MarkerA()))
                    |> Contribution.requires [ SeamId "IAbsent" ]
                )

            Expect.throws (fun () -> ServerApp.serviceConfig app |> ignore) "serviceConfig refuses"

        testCase "a hosted contribution is admitted against the configuration set after it"
        <| fun () ->
            let unit =
                BackgroundUnit.create "order-probe" SharedWork (fun _ -> Hosting.atStart ignore)

            let app =
                ServerApp.empty
                |> ServerApp.addContribution (Contribution.hosted ownerA unit)
                |> ServerApp.withConfig {
                    ServerConfig.defaults with
                        ProcessProfile = WebOnly
                }

            let services = ServiceCollection()

            match ServerApp.serviceConfig app with
            | Some configure -> configure services |> ignore
            | None -> failtest "the hosted contribution registers its record"

            Hosting.admit services (ServerApp.effectiveConfig app) |> ignore

            Expect.hasLength (Hosting.records services) 1 "the unit is recorded"

            Expect.isFalse
                (services
                 |> Seq.exists (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>))
                "WebOnly does not admit shared work, though withConfig came after the unit"
    ]