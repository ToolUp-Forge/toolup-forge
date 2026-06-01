// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.SampleClientToolDispatchTests

// ─── Phase 46.B — Sample companion round-trip + 2nd-impl binding ─────
//
// Two responsibilities in one file:
//
//   1. Compose-shape checks — `Compose.register` appends the sample's
//      `_sample.calc` tool to `AIServerApp.Base.AITools`;
//      `Compose.registerWithPolicy` additionally wires the
//      operator-supplied `IClientToolAuthorizer` into the composition
//      root's `ServiceConfig`.
//
//   2. Binds Phase 46.A's `IClientToolDispatchContract` against the
//      sample's authorizer + a calculator simulator that runs
//      `CalcOps.compute` (shared with the real Fable handler in
//      `ToolUp.AI.SampleClientTool.Client.SampleHandler`). This is
//      the second binding subject the dispatch substrate needs
//      (`SyntheticClientToolAuthorizer` and the `DenyOnlyAuthorizer`
//      were the first two synthetic bindings; the sample is the
//      first non-synthetic).
//
// The Fable client handler can't be invoked from a .NET Expecto runner,
// but the test exercises the same JSON round-trip via the simulator
// reading the same `CalcRequest` / `CalcResponse` shapes the real
// handler does. So a regression in the shared `CalcOps.compute` or the
// wire shape is caught by either tier's test.

open Expecto
open Microsoft.Extensions.DependencyInjection
open Newtonsoft.Json
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Server
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.AI.SampleClientTool
open ToolUp.AI.SampleClientTool.Server
open ToolUp.Platform.Tests.Contracts

// ─── Stub provider factory + profile (compose-only; never invoked) ───

type private StubProviderFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error(ProviderResolutionError.NoProviderConfigured) }

        member _.TryResolveByLabel(_, _) = async { return Error(ProviderResolutionError.NoProviderConfigured) }

type private StubProviderProfile() =
    interface IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }

let private buildBaseApp () : AIServerApp =
    let factory = StubProviderFactory() :> IAIProviderFactory
    let profile = StubProviderProfile() :> IProviderProfile
    AIServerApp.create factory profile

// ─── Authorizer for the contract-pack binding ────────────────────────

/// Denies one specific tool name, allows everything else. The sample
/// itself doesn't ship an authorizer (zero-state companion); the
/// contract pack needs both an Allow path and a Deny path, so the
/// test supplies a minimal policy authorizer in the same shape a
/// real companion's operator would build.
type private SampleTestAuthorizer(denyTool: string) =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, _argsJson, _activeModule, _activePage) =
            if toolName = denyTool then
                Deny $"sample-test policy denied '{toolName}'"
            else
                Allow

// ─── Calculator simulator (mimics the Fable SampleHandler) ───────────

/// Mirror of `ToolUp.AI.SampleClientTool.Client.SampleHandler.handler`
/// for the .NET-side test. Parses `CalcRequest` from the agent loop's
/// SSE-emitted argsJson, runs the shared `CalcOps.compute`, and
/// returns a `CalcResponse` JSON. Newtonsoft serialises the records
/// in PascalCase — matches what the agent loop ships to the model.
let private calcSimulator (evt: AIStreamEvent) : string option =
    match evt with
    | ClientToolInvoke(_, _, _, argsJson, _, _) ->
        let request = JsonConvert.DeserializeObject<CalcRequest>(argsJson)
        let response = CalcOps.compute request
        Some(JsonConvert.SerializeObject response)
    | _ -> None

// ─── Tests ───────────────────────────────────────────────────────────

let private composeShapeTests =
    testList "Phase 46.B — sample compose shape" [
        test "register appends _sample.calc with Location = ClientResident" {
            let app = buildBaseApp () |> Compose.register
            let toolNames = app.Base.AITools |> List.map (fun (def, _) -> def.Name)
            Expect.contains toolNames SampleCalcToolName "sample tool must land in AITools"

            let def =
                app.Base.AITools
                |> List.tryFind (fun (d, _) -> d.Name = SampleCalcToolName)
                |> Option.map fst

            match def with
            | None -> failtest "sample tool definition not found"
            | Some d ->
                Expect.equal
                    d.Location
                    ClientResident
                    "Location must be ClientResident — the substrate dispatches via SSE, not server-side execution"
        }

        test "registerWithPolicy installs the authorizer into the composition's ServiceConfig" {
            let authorizer = SampleTestAuthorizer("denied.tool") :> IClientToolAuthorizer

            let app = buildBaseApp () |> Compose.registerWithPolicy authorizer

            // The ServiceConfig delegate is the load-bearing seam —
            // the agent loop resolves IClientToolAuthorizer through it
            // at request time. Build the DI container the same way
            // `composeWithAI` would and confirm the resolution lands
            // on our authorizer.
            let services = ServiceCollection() :> IServiceCollection

            match app.Base.Extensions.ServiceConfig with
            | None -> failtest "Compose.registerWithPolicy must install a ServiceConfig delegate"
            | Some configure -> configure services |> ignore

            let provider = services.BuildServiceProvider()
            let resolved = provider.GetService<IClientToolAuthorizer>()
            Expect.isNotNull (box resolved) "authorizer must resolve from DI"

            // Sanity-check the resolved authorizer is the one we
            // installed — denying its declared tool name proves the
            // wiring isn't picking up some default seam-absent shim.
            let decision = resolved.Authorize("denied.tool", "{}", Some "AnyModule", None)

            match decision with
            | Allow -> failtest "resolved authorizer must enforce its policy"
            | Deny _ -> ()
        }
    ]

/// Phase 46.A's `IClientToolDispatchContract` bound to the sample's
/// authorizer + calculator simulator. This is the GP 12 second
/// in-tree implementation — Phase 46 + 46.A's synthetic stubs were the
/// first two; this is the first non-synthetic binding, paired with the
/// real Fable handler that ships in `ToolUp.AI.SampleClientTool.Client`.
let private samplePackBinding =
    IClientToolDispatchContract.tests {
        Name = "ToolUp.AI.SampleClientTool"
        Authorizer = SampleTestAuthorizer("denied.tool") :> IClientToolAuthorizer
        AllowedToolName = SampleCalcToolName
        DeniedToolName = "denied.tool"
        Simulator = calcSimulator
    }

let tests =
    testList "Phase 46.B — ToolUp.AI.SampleClientTool reference companion" [ composeShapeTests; samplePackBinding ]