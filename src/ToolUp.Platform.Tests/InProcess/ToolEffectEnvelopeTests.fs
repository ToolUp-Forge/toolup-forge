// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ToolEffectEnvelopeTests

// ─── Phase 793 — the tool effect class, its policy and its envelope ──
//
// Three things this pack proves, each against the shape that would
// silently fail:
//
//   1. **The policy is one decision.** `ToolGate.decideDeclared` refuses
//      a tool whose declared classes exceed the ceiling, `ListAccessible`
//      drops it, and the agent loop's dispatch re-check refuses a forged
//      name for it with a typed `Denied` on the allowlist denial stream.
//      The executor of every deny-path tool `failtest`s if it is ever
//      invoked, so a gate that failed open is caught on the executor
//      rather than on a missing assertion.
//   2. **The envelope binds the body.** A tool declaring only `ReadFacts`
//      that reaches for an outbound request through `guardEgress`, or a
//      host capability through `guardInvoke`, gets a typed denial back,
//      the seam's action never runs, and the denial lands on
//      `_platform.ai.tool_allowlist_denial`. The same body under a
//      declaration that names the effect runs it.
//   3. **The verified profile makes the declaration mandatory**, and the
//      two Phase 503 / 489 keyings — approval by class, the external
//      principal ceiling — read the declaration rather than a name list.
//
// GP 11 is asserted explicitly: no policy composed, an undeclared tool,
// no envelope in force — every one is byte-for-byte the pre-793 path.

open System
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.McpHost

// ─── Fixtures ─────────────────────────────────────────────────────────

let private toolDef (name: string) (sourceModule: string) (effects: ToolEffectDeclaration) : AIToolDefinition = {
    Name = name
    Description = "Phase 793 test tool"
    Parameters = []
    SourceModule = sourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = effects
}

/// An executor that must never run. Every deny-path tool registers with
/// this body, so a gate that failed open is caught here rather than by a
/// missing assertion.
let private mustNotRun _ctx _argsJson : Async<string> = async {
    return failtest "tool executor must not be invoked when the tool's declared effects exceed the policy ceiling"
}

let private inert _ctx _argsJson : Async<string> = async { return "{}" }

let private registryUnder (policy: ToolPolicy) (tools: RegisteredTool list) =
    let registry = AIToolRegistry(policy)
    registry.RegisterAll tools
    registry

let private alice = AccessContext.unrestricted (AuthenticatedUser "alice")
let private everyGrantLive (_: string) = true

/// Minimal provider that emits ONE tool call on its first turn and ends
/// the conversation on the second. It ignores the tool list it is
/// handed, which is exactly the forged-name shape the dispatch re-check
/// exists for.
type private ForgingProvider(toolNameToCall: string) =
    let mutable callCount = 0

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "test-forging"
            Model = "test-forging-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            callCount <- callCount + 1

            if callCount = 1 then
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = toolNameToCall
                                Arguments = "{}"
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            else
                return
                    Ok {
                        Content = "done"
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// Background-shaped `HttpContext` with an event store, an unrestricted
/// caller and a scope — what `createBackgroundContext` carries forward.
let private buildContext (eventStore: IEventStore) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IEventStore>(eventStore) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "alice"

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = "alice"
            Container = "user-alice"
            Persist = true
        }

    ctx :> HttpContext

/// Run one forged-call turn against `registry` and return the tool-result
/// contents the model was handed plus the event store the audit landed
/// in.
let private runTurn (registry: AIToolRegistry) (toolName: string) : Async<string list * IEventStore> = async {
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let ctx = buildContext eventStore
    let events = ResizeArray<AIStreamEvent>()
    let onEvent (evt: AIStreamEvent) = lock events (fun () -> events.Add evt)

    let! _final =
        AIAgentEngine.runAgentLoop
            (ForgingProvider(toolName) :> IAIProvider)
            registry
            (ClientToolDispatch.ClientToolDispatchRegistry())
            ctx
            (Guid.NewGuid())
            (Guid.NewGuid())
            AISurface.FullPage
            None
            None
            CancellationToken.None
            [ AIProviderMessage.text "user" "do the thing" ]
            None
            onEvent

    let contents =
        lock events (fun () -> events.ToArray() |> Array.toList)
        |> List.choose (function
            | ToolCallCompleted(_, _, content) -> Some content
            | _ -> None)

    return contents, eventStore
}

let private denialsOn (eventStore: IEventStore) =
    eventStore.ReadBySource("alice", ToolEffectEnvelope.DenialAuditSource)

let private allowAll =
    { new IActionAuthorizer with
        member _.Authorize _ _ = async { return AuthorizationDecision.Allow }
    }

let private agent (granted: string list) : AgentPrincipal = {
    AccountId = "svc-exporter"
    DisplayName = "Nightly exporter"
    ScopeId = "team-1"
    TokenId = "tok-1"
    ExpiresAt = DateTimeOffset.UtcNow.AddDays 1.0
    GrantedTools = Set.ofList granted
}

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 793 - the tool effect envelope" [

        // ── 793.A — the declaration ──────────────────────────────────

        testCase "every in-tree tool declares its effects"
        <| fun () ->
            let definitions =
                (NarrativeTools.builtInTools @ PlatformAITools.builtIn)
                |> List.map _.Definition
                |> List.append [
                    ToolUp.Facts.FactQueryTool.definition
                    ToolUp.Facts.PopulationQueryTool.definition
                    ToolUp.Facts.CoverageTool.definition
                    SampleClientTool.Server.Compose.toolDefinition
                ]

            Expect.isGreaterThan (List.length definitions) 12 "the in-tree set should all be here"

            Expect.isEmpty
                (ToolEffectEnvelope.undeclaredTools definitions)
                "an in-tree tool with UndeclaredEffects would be refused by every verified deployment that composes it"

        testCase
            "EmitsActions on the older field is folded into the declaration, and an undeclared tool stays undeclared"
        <| fun () ->
            let declaration = [
                {
                    ModuleId = "Sales"
                    ActionKey = "apply"
                    Description = "test"
                    PayloadSchema = None
                }
            ]

            let declared = {
                toolDef "sales.apply" "Sales" ToolEffectDeclaration.readFacts with
                    EmitsActions = Some declaration
            }

            Expect.equal
                (AIToolEffects.declaredOf declared)
                (DeclaredEffects(Set.ofList [ ReadFacts; EmitsActions ]))
                "a tool declaring client actions carries the EmitsActions class whether or not it listed it"

            let undeclared = {
                toolDef "sales.apply" "Sales" UndeclaredEffects with
                    EmitsActions = Some declaration
            }

            Expect.equal
                (AIToolEffects.declaredOf undeclared)
                UndeclaredEffects
                "the field alone does not turn an undeclared tool into a declared one"

        testCase "the verified profile refuses an undeclared tool by name; the standard profile refuses nothing"
        <| fun () ->
            let tools = [
                toolDef "sales.read" "Sales" ToolEffectDeclaration.readFacts
                toolDef "sales.legacy" "Sales" UndeclaredEffects
                toolDef "hr.legacy" "HR" UndeclaredEffects
            ]

            match ToolEffectEnvelope.refuseUndeclared CompositionProfile.Verified tools with
            | Error(ToolEffectsUndeclared names) ->
                Expect.equal names [ "sales.legacy"; "hr.legacy" ] "the refusal names every undeclared tool"

                let described = CompositionProfileRefusal.describe (ToolEffectsUndeclared names)
                Expect.stringContains described "sales.legacy" "and the description carries the names"
                Expect.stringContains described "AIToolDefinition.Effects" "and the remedy"
            | other -> failtestf "expected ToolEffectsUndeclared, got %A" other

            Expect.equal
                (ToolEffectEnvelope.refuseUndeclared CompositionProfile.Standard tools)
                (Ok())
                "Standard leaves an undeclared tool to the policy (GP 11)"

            Expect.equal
                (ToolEffectEnvelope.refuseUndeclared CompositionProfile.Verified [ List.head tools ])
                (Ok())
                "and Verified is satisfied when every tool declares"

        // ── 793.C — one decision ─────────────────────────────────────

        testCase "the decision refuses what exceeds the ceiling, names it, and admits the rest"
        <| fun () ->
            let decide policy effects =
                ToolGate.decideDeclared (isToolSourcePermittedFor alice) everyGrantLive policy "Sales" effects

            Expect.equal
                (decide ToolPolicy.readOnly ToolEffectDeclaration.readFacts)
                ToolAdmitted
                "reads sit under read-only"

            Expect.equal
                (decide
                    ToolPolicy.readOnly
                    (ToolEffectDeclaration.declare [ ReadFacts; WriteState "Sales"; Egress "api.example.com" ]))
                (ToolRefusedEffects [ WriteState "Sales"; Egress "api.example.com" ])
                "every exceeding effect is named, reads are not"

            Expect.equal
                (decide ToolPolicy.readOnly UndeclaredEffects)
                ToolRefusedUndeclared
                "a bounded policy refuses an undeclared tool"

            Expect.equal
                (decide (ToolPolicy.permittingUndeclared ToolPolicy.readOnly) UndeclaredEffects)
                ToolAdmitted
                "unless it says otherwise"

            Expect.equal
                (decide ToolPolicy.unrestricted (ToolEffectDeclaration.declare [ Egress "anywhere"; Spend "x" ]))
                ToolAdmitted
                "and the unrestricted policy admits everything (GP 11)"

            Expect.equal (decide ToolPolicy.unrestricted UndeclaredEffects) ToolAdmitted "undeclared tools included"

        testCase
            "RBAC and grant liveness are decided before the ceiling, so each refusal names the gate that produced it"
        <| fun () ->
            let noRead = {
                alice with
                    ModulePermissions = Map.ofList [ "HR", [ ModulePermission.Read ] ]
            }

            let egress = ToolEffectDeclaration.declare [ Egress "api.example.com" ]

            Expect.equal
                (ToolGate.decideDeclared
                    (isToolSourcePermittedFor noRead)
                    everyGrantLive
                    ToolPolicy.readOnly
                    "Sales"
                    egress)
                (ToolRefusedSource "Sales")
                "no Read on the module is the first refusal"

            Expect.equal
                (ToolGate.decideDeclared
                    (isToolSourcePermittedFor alice)
                    (fun _ -> false)
                    ToolPolicy.readOnly
                    "Sales"
                    egress)
                (ToolRefusedGrant "Sales")
                "an inert grant is the second"

            Expect.equal
                (ToolGate.decideDeclared
                    (isToolSourcePermittedFor alice)
                    everyGrantLive
                    ToolPolicy.readOnly
                    "Sales"
                    egress)
                (ToolRefusedEffects [ Egress "api.example.com" ])
                "and the ceiling is the third"

        testCase
            "ListAccessible under a bounded policy drops the tool whose declaration exceeds it, and keeps registry order"
        <| fun () ->
            let registry =
                registryUnder ToolPolicy.readOnly [
                    createTool (toolDef "sales.read" "Sales" ToolEffectDeclaration.readFacts) inert
                    createTool
                        (toolDef
                            "sales.export"
                            "Sales"
                            (ToolEffectDeclaration.declare [ ReadFacts; Egress "api.example.com" ]))
                        mustNotRun
                    createTool (toolDef "sales.legacy" "Sales" UndeclaredEffects) mustNotRun
                    createTool (toolDef "sales.compute" "Sales" ToolEffectDeclaration.computeFacts) inert
                ]

            Expect.equal
                (registry.ListAccessible(alice, everyGrantLive) |> List.map _.Definition.Name)
                [ "sales.read"; "sales.compute" ]
                "the exporter and the undeclared tool are never described to the model"

            Expect.equal
                (registry.ListAccessible alice |> List.map _.Definition.Name)
                [ "sales.read"; "sales.compute" ]
                "through the one-argument overload too"

            let unrestricted = registryUnder ToolPolicy.unrestricted (registry.GetAll())

            Expect.equal
                (unrestricted.ListAccessible(alice, everyGrantLive) |> List.length)
                4
                "and a registry with no policy lists all four (GP 11)"

        testCaseAsync
            "a forged name for a tool outside the ceiling is refused at dispatch before Execute, and lands on the allowlist denial stream"
        <| async {
            let registry =
                registryUnder ToolPolicy.readOnly [
                    createTool
                        (toolDef
                            "sales.export"
                            "Sales"
                            (ToolEffectDeclaration.declare [ ReadFacts; Egress "api.example.com" ]))
                        mustNotRun
                ]

            let! contents, eventStore = runTurn registry "sales.export"

            let denied = contents |> List.filter (fun c -> c.Contains "was denied:")
            Expect.isNonEmpty denied "the dispatch re-check must surface a typed Denied tool-result to the model"

            Expect.stringContains
                (List.head denied)
                "egress(api.example.com)"
                "the refusal names the effect the ceiling does not admit"

            let! audits = denialsOn eventStore

            Expect.isNonEmpty
                audits
                "the refusal must land on _platform.ai.tool_allowlist_denial so /dev/ai-allowlist sees it"

            let evt = Seq.head audits
            Expect.equal evt.EventType ToolEffectEnvelope.DenialEventType "audit event type"
            Expect.stringContains evt.Payload "sales.export" "the audit payload names the tool"
        }

        testCaseAsync "an admitted tool's dispatch is unchanged - the gate refuses, it does not break"
        <| async {
            let registry =
                registryUnder ToolPolicy.readOnly [
                    createTool (toolDef "sales.read" "Sales" ToolEffectDeclaration.readFacts) (fun _ _ -> async {
                        return """{"rows":3}"""
                    })
                ]

            let! contents, eventStore = runTurn registry "sales.read"
            Expect.contains contents """{"rows":3}""" "the tool ran and its result reached the model"
            let! audits = denialsOn eventStore
            Expect.isEmpty audits "and nothing was denied"
        }

        // ── 793.B — the envelope ─────────────────────────────────────

        testCaseAsync
            "a tool declaring only ReadFacts cannot make an outbound call: the send never runs and the denial is audited"
        <| async {
            let mutable sends = 0

            let fetch _ctx _args = async {
                let! outcome =
                    ToolEffectEnvelope.guardEgress "api.example.com" (fun () -> async {
                        sends <- sends + 1
                        return """{"fetched":true}"""
                    })

                match outcome with
                | Ok body -> return body
                | Error denial -> return denial.Reason
            }

            let registry =
                registryUnder ToolPolicy.unrestricted [
                    createTool (toolDef "web.fetch" "Sales" ToolEffectDeclaration.readFacts) fetch
                ]

            let! contents, eventStore = runTurn registry "web.fetch"

            Expect.equal sends 0 "the outbound send must never run"

            Expect.isTrue
                (contents |> List.exists (fun c -> c.Contains "egress(api.example.com)"))
                "the body received the typed denial naming the destination"

            let! audits = denialsOn eventStore
            Expect.isNonEmpty audits "the denial must land on _platform.ai.tool_allowlist_denial"
            Expect.stringContains (Seq.head audits).Payload "web.fetch" "naming the tool"
        }

        testCaseAsync "the same body under a declaration that names the destination sends"
        <| async {
            let mutable sends = 0

            let fetch _ctx _args = async {
                let! outcome =
                    ToolEffectEnvelope.guardEgress "api.example.com" (fun () -> async {
                        sends <- sends + 1
                        return """{"fetched":true}"""
                    })

                match outcome with
                | Ok body -> return body
                | Error denial -> return denial.Reason
            }

            let registry =
                registryUnder ToolPolicy.unrestricted [
                    createTool
                        (toolDef
                            "web.fetch"
                            "Sales"
                            (ToolEffectDeclaration.declare [ ReadFacts; Egress "api.example.com" ]))
                        fetch
                ]

            let! contents, eventStore = runTurn registry "web.fetch"
            Expect.equal sends 1 "the declared egress ran once"
            Expect.contains contents """{"fetched":true}""" "and its result reached the model"
            let! audits = denialsOn eventStore
            Expect.isEmpty audits "nothing was denied"
        }

        testCaseAsync
            "a tool declaring only ReadFacts cannot invoke a host capability: the handler never runs and the registry is never reached"
        <| async {
            let mutable handled = 0
            let denials = ResizeArray<ToolEffectEnvelope.ToolEffectDenial>()

            let hostRegistry = HostCapabilityRegistry.create allowAll

            hostRegistry.Register (CapabilityId "send-mail") (fun _ _ -> async {
                handled <- handled + 1
                return Map.empty
            })

            let invoke () =
                ToolEffectEnvelope.guardInvoke
                    CompositionCapabilityGate.disabled
                    (ComponentId.create "sales")
                    CompanionCapability.identity
                    hostRegistry
                    (CapabilityId "send-mail")
                    Map.empty
                    alice

            let envelopeOf effects : ToolEffectEnvelope.ActiveEnvelope = {
                ToolName = "sales.notify"
                Effects = effects
                OnDenied = fun denial -> async { lock denials (fun () -> denials.Add denial) }
            }

            let! refused = ToolEffectEnvelope.runWithin (envelopeOf ToolEffectDeclaration.readFacts) (invoke ())

            match refused with
            | HostCapabilityOutcome.Denied reason ->
                Expect.stringContains reason "external(send-mail)" "the denial names the capability"
                Expect.stringContains reason "sales.notify" "and the tool"
            | other -> failtestf "expected a Denied outcome, got %A" other

            Expect.equal handled 0 "the handler must never run"
            Expect.equal denials.Count 1 "and the denial reached the observer exactly once"
            Expect.equal denials[0].Required (External "send-mail") "as a typed effect"

            let! completed =
                ToolEffectEnvelope.runWithin
                    (envelopeOf (ToolEffectDeclaration.declare [ ReadFacts; External "send-mail" ]))
                    (invoke ())

            match completed with
            | HostCapabilityOutcome.Completed _ -> ()
            | other -> failtestf "a declared capability must reach the registry, got %A" other

            Expect.equal handled 1 "the declared invocation ran once"
        }

        testCaseAsync "outside any envelope, and under an undeclared tool, every seam is permitted (GP 11)"
        <| async {
            Expect.isNone (ToolEffectEnvelope.current ()) "no envelope is in force on a fresh flow"

            let! outside = ToolEffectEnvelope.require (Egress "anywhere")
            Expect.equal outside (Ok()) "a seam consulted outside every tool passes"

            let undeclared: ToolEffectEnvelope.ActiveEnvelope = {
                ToolName = "legacy.tool"
                Effects = UndeclaredEffects
                OnDenied = fun _ -> async { return failtest "an undeclared tool has no envelope to deny" }
            }

            let! inside = ToolEffectEnvelope.runWithin undeclared (ToolEffectEnvelope.require (External "anything"))

            Expect.equal inside (Ok()) "an undeclared tool's body runs exactly as it did"
            Expect.isNone (ToolEffectEnvelope.current ()) "and the envelope is restored afterwards"
        }

        testCaseAsync "the envelope is restored after a body that throws"
        <| async {
            let envelope: ToolEffectEnvelope.ActiveEnvelope = {
                ToolName = "throwing.tool"
                Effects = ToolEffectDeclaration.readFacts
                OnDenied = fun _ -> async { return () }
            }

            let! outcome =
                ToolEffectEnvelope.runWithin envelope (async { return failwith "boom" })
                |> Async.Catch

            match outcome with
            | Choice2Of2 _ -> ()
            | Choice1Of2() -> failtest "the body should have thrown"

            Expect.isNone (ToolEffectEnvelope.current ()) "the previous (absent) envelope is back in force"
        }

        // ── 503 by class, 489 by class ───────────────────────────────

        testCase
            "approval is keyed on effect class: a WriteState tool is held under a policy naming the class, a read tool is not, an undeclared tool never"
        <| fun () ->
            let policy =
                ToolPolicy.unrestricted
                |> ToolPolicy.withApprovalFor [ ToolEffectClass.WriteState; ToolEffectClass.Spend ]

            match
                ToolApprovalDispatch.requirementByClass
                    policy
                    (toolDef "sales.update" "Sales" (ToolEffectDeclaration.declare [ ReadFacts; WriteState "Sales" ]))
            with
            | ApprovalRequired prompt ->
                Expect.stringContains prompt.Summary "sales.update" "the prompt names the tool"
                Expect.stringContains prompt.Detail "write-state" "and the class the deployment holds"
                Expect.stringContains prompt.Detail "write-state(Sales)" "and the declared effect"
            | ApprovalNotRequired -> failtest "a tool declaring a held class must be held"

            Expect.equal
                (ToolApprovalDispatch.requirementByClass
                    policy
                    (toolDef "sales.read" "Sales" ToolEffectDeclaration.readFacts))
                ApprovalNotRequired
                "a read tool falls through to the deployment's own policy"

            Expect.equal
                (ToolApprovalDispatch.requirementByClass policy (toolDef "sales.legacy" "Sales" UndeclaredEffects))
                ApprovalNotRequired
                "an undeclared tool has no class to key on"

            Expect.equal
                (ToolApprovalDispatch.requirementByClass
                    ToolPolicy.unrestricted
                    (toolDef "sales.update" "Sales" (ToolEffectDeclaration.declare [ WriteState "Sales" ])))
                ApprovalNotRequired
                "and a policy holding no class holds nothing (GP 11)"

        testCase
            "an external principal is gated by class: default-deny under a bounded policy, admitted to exactly the classes named"
        <| fun () ->
            let tools = [
                createTool (toolDef "sales.read" "Sales" ToolEffectDeclaration.readFacts) inert
                createTool (toolDef "sales.update" "Sales" (ToolEffectDeclaration.declare [ WriteState "Sales" ])) inert
            ]

            let granted = agent [ "sales.read"; "sales.update" ]

            let defaultDeny = registryUnder ToolPolicy.readOnly tools

            Expect.isEmpty
                (McpAuthorization.visibleTools defaultDeny alice everyGrantLive granted)
                "a bounded policy admits an external principal to no class it did not name"

            match McpAuthorization.classifyCall defaultDeny alice everyGrantLive granted "sales.read" with
            | Error(McpError.ToolDenied(name, reason)) ->
                Expect.equal name "sales.read" "the refusal names the tool"
                Expect.stringContains reason "by effect class" "and says it is the class ceiling, not the name grant"
            | other -> failtestf "expected ToolDenied, got %A" other

            let readOnlyAgents =
                registryUnder
                    (ToolPolicy.readOnly
                     |> ToolPolicy.withExternalPrincipalCeiling [ ToolEffectClass.ReadFacts ])
                    tools

            Expect.equal
                (McpAuthorization.visibleTools readOnlyAgents alice everyGrantLive granted
                 |> List.map _.Definition.Name)
                [ "sales.read" ]
                "naming a class admits exactly the tools within it"

            match McpAuthorization.classifyCall readOnlyAgents alice everyGrantLive granted "sales.update" with
            | Error(McpError.ToolDenied _) -> ()
            | other -> failtestf "the writer stays refused, got %A" other

            let unrestricted = registryUnder ToolPolicy.unrestricted tools

            Expect.equal
                (McpAuthorization.visibleTools unrestricted alice everyGrantLive granted
                 |> List.length)
                2
                "and a registry with no policy is the pre-793 name-only grant (GP 11)"
    ]