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

open Expecto
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
    testList "Phase 1052 — ServerApp contributions and builder order" [
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
    ]