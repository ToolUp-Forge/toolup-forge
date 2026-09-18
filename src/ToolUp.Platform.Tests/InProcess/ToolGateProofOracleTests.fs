// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ToolGateProofOracleTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.AI

// ─── Phase 793 — the proved gate as oracle ───────────────────────────
//
// `proofs/ToolGate.fst` models `ToolGate.decideDeclared`,
// `AIToolRegistry.ListAccessible` and the dispatch-site re-check clause
// for clause, and proves four lemmas over them: the list is sound
// against the policy, the list IS the decision, dispatch is never wider
// than the list, and egress never escapes a ceiling that does not name
// it. `proofs/check.ps1` extracts that model to F# and byte-compares the
// result against the committed `proofs/oracle/ToolGate.fs`, which this
// pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** Every arm below is differential:
// a tool, a policy, a caller and a grant predicate go through production
// and through the extracted model, and the two must agree on the
// VERDICT, the LISTED SET and the DISPATCH answer at once. The tools are
// every in-tree definition this pack can reach — the narrative and
// cross-module built-ins, the three fact tools, the sample client tool —
// plus generated tools with generated declarations, because an in-tree
// tool says the gate is right on the effects someone declared.
//
// **The go-red case is committed, and it is the egress-blind gate.** An
// oracle that drops `Egress` from a declaration before deciding — the
// tidy-looking mistake of measuring a tool by what it reads rather than
// where it reaches — must be CAUGHT by the comparison. A differential
// that has never been shown to fail agrees with whatever it is shown.

// ─── The bridge ──────────────────────────────────────────────────────
//
// Case for case, and short on purpose: a defect here would make the
// comparison compare the wrong thing.

let private toModelEffect (e: ToolEffect) : ToolGate.tool_effect =
    match e with
    | ReadFacts -> ToolGate.ReadFacts
    | ComputeFacts -> ToolGate.ComputeFacts
    | ReadContent -> ToolGate.ReadContent
    | WriteState scope -> ToolGate.WriteState scope
    | Egress destination -> ToolGate.Egress destination
    | Spend budgetClass -> ToolGate.Spend budgetClass
    | External capabilityId -> ToolGate.External capabilityId
    | EmitsActions -> ToolGate.EmitsActions

let private ofModelEffect (e: ToolGate.tool_effect) : ToolEffect =
    match e with
    | ToolGate.ReadFacts -> ReadFacts
    | ToolGate.ComputeFacts -> ComputeFacts
    | ToolGate.ReadContent -> ReadContent
    | ToolGate.WriteState scope -> WriteState scope
    | ToolGate.Egress destination -> Egress destination
    | ToolGate.Spend budgetClass -> Spend budgetClass
    | ToolGate.External capabilityId -> External capabilityId
    | ToolGate.EmitsActions -> EmitsActions

let private toModelClass (c: ToolEffectClass) : ToolGate.effect_class =
    match c with
    | ToolEffectClass.ReadFacts -> ToolGate.CReadFacts
    | ToolEffectClass.ComputeFacts -> ToolGate.CComputeFacts
    | ToolEffectClass.ReadContent -> ToolGate.CReadContent
    | ToolEffectClass.WriteState -> ToolGate.CWriteState
    | ToolEffectClass.Egress -> ToolGate.CEgress
    | ToolEffectClass.Spend -> ToolGate.CSpend
    | ToolEffectClass.External -> ToolGate.CExternal
    | ToolEffectClass.EmitsActions -> ToolGate.CEmitsActions

/// A declared set rides as its sorted list — the order `Set.toList`
/// gives production's `exceeding`, so the two refusals list the same
/// effects in the same order.
let private toModelDeclaration (d: ToolEffectDeclaration) : ToolGate.declaration =
    match d with
    | UndeclaredEffects -> ToolGate.Undeclared
    | DeclaredEffects effects -> ToolGate.Declared(effects |> Set.toList |> List.map toModelEffect)

let private toModelPolicy (p: AIToolRegistry.ToolPolicy) : ToolGate.policy = {
    ToolGate.ceiling =
        match p.Ceiling with
        | AIToolRegistry.UnboundedCeiling -> ToolGate.Unbounded
        | AIToolRegistry.BoundedCeiling classes -> ToolGate.Bounded(classes |> Set.toList |> List.map toModelClass)
    ToolGate.permit_undeclared = p.PermitUndeclared
}

let private ofModelVerdict (v: ToolGate.verdict) : AIToolRegistry.ToolGateVerdict =
    match v with
    | ToolGate.Admitted -> AIToolRegistry.ToolAdmitted
    | ToolGate.RefusedSource sourceModule -> AIToolRegistry.ToolRefusedSource sourceModule
    | ToolGate.RefusedGrant sourceModule -> AIToolRegistry.ToolRefusedGrant sourceModule
    | ToolGate.RefusedUndeclared -> AIToolRegistry.ToolRefusedUndeclared
    | ToolGate.RefusedEffects over -> AIToolRegistry.ToolRefusedEffects(over |> List.map ofModelEffect)

/// A registered tool as the gate reads it: the authored name, the
/// provider alias `FindByName` also matches, the source module, and the
/// declaration with `EmitsActions` folded in — the same fold production
/// applies.
let private toModelTool (t: AIToolRegistry.RegisteredTool) : ToolGate.tool = {
    ToolGate.name = t.Definition.Name
    ToolGate.alias = t.ProviderDef.Name
    ToolGate.source_module = t.Definition.SourceModule
    ToolGate.effects = toModelDeclaration (AIToolEffects.declaredOf t.Definition)
}

// ─── The population ──────────────────────────────────────────────────

let private inert _ctx _args : Async<string> = async { return "{}" }

/// Every in-tree tool definition this pack can reach, as registered
/// tools. The knowledge-base pair and the algorithm family are outside:
/// the first are private to their compose function, the second are
/// built per algorithm from a catalog this pack does not reference.
let private inTreeTools: AIToolRegistry.RegisteredTool list =
    NarrativeTools.builtInTools
    @ PlatformAITools.builtIn
    @ [
        AIToolRegistry.createTool ToolUp.Facts.FactQueryTool.definition inert
        AIToolRegistry.createTool ToolUp.Facts.PopulationQueryTool.definition inert
        AIToolRegistry.createTool ToolUp.Facts.CoverageTool.definition inert
        AIToolRegistry.createTool SampleClientTool.Server.Compose.toolDefinition inert
    ]

let private pick (random: Random) (xs: 'a list) : 'a = xs[random.Next(List.length xs)]

let private sourceModules = [
    "_platform.ai"
    "ToolUp.Platform"
    "_facts"
    "Sales"
    "MoodJournal"
    "Inventory"
]

let private effectPool = [
    ReadFacts
    ComputeFacts
    ReadContent
    WriteState "narratives"
    WriteState "Sales"
    Egress "api.example.com"
    Egress "hooks.example.net"
    Spend "model-call"
    External "send-mail"
    External "open-ticket"
    EmitsActions
]

let private toolDef (name: string) (sourceModule: string) (effects: ToolEffectDeclaration) : AIToolDefinition = {
    Name = name
    Description = "Phase 793 generated tool"
    Parameters = []
    SourceModule = sourceModule
    EmitsActions = None
    Location = ServerResident
    Surface = Both
    IsLiveInterface = false
    ResultBudget = DefaultResultBudget
    Effects = effects
}

/// Generated tools: names with a dot (so alias and name differ), sources
/// from the pool, declarations that are undeclared, empty, or one to
/// four effects from the pool — Egress among them often enough that the
/// go-red case has plenty to catch.
let private generatedTools: AIToolRegistry.RegisteredTool list =
    let random = Random 793

    List.init 60 (fun n ->
        let declaration =
            match random.Next 5 with
            | 0 -> UndeclaredEffects
            | 1 -> DeclaredEffects Set.empty
            | _ -> DeclaredEffects(Set.ofList (List.init (random.Next(1, 5)) (fun _ -> pick random effectPool)))

        let def = toolDef (sprintf "gen.tool_%d" n) (pick random sourceModules) declaration

        // Every fourth tool also declares client actions on the older
        // field, so the fold of `EmitsActions` is exercised.
        let def =
            if n % 4 = 0 then
                {
                    def with
                        EmitsActions =
                            Some [
                                {
                                    ModuleId = def.SourceModule
                                    ActionKey = "apply"
                                    Description = "generated"
                                    PayloadSchema = None
                                }
                            ]
                }
            else
                def

        AIToolRegistry.createTool def inert)

let private classPool = ToolEffectClass.all

/// Generated policies: the two shipped constants, bounded ceilings over
/// random class subsets with and without undeclared tools admitted, and
/// the external-principal view of each — so `forExternalPrincipal` is a
/// policy the model sees like any other.
let private generatedPolicies: (string * AIToolRegistry.ToolPolicy) list =
    let random = Random 7930

    let bounded =
        List.init 12 (fun n ->
            let classes = classPool |> List.filter (fun _ -> random.Next 2 = 0)

            let policy =
                AIToolRegistry.ToolPolicy.bounded classes
                |> (fun p ->
                    if random.Next 3 = 0 then
                        AIToolRegistry.ToolPolicy.permittingUndeclared p
                    else
                        p)
                |> (fun p ->
                    if random.Next 2 = 0 then
                        AIToolRegistry.ToolPolicy.withExternalPrincipalCeiling
                            (classPool |> List.filter (fun _ -> random.Next 3 = 0))
                            p
                    else
                        p)

            sprintf "bounded #%d" n, policy)

    let named = [
        "unrestricted", AIToolRegistry.ToolPolicy.unrestricted
        "read-only", AIToolRegistry.ToolPolicy.readOnly
    ]

    let all = named @ bounded

    all
    @ (all
       |> List.map (fun (name, p) ->
           sprintf "%s (external principal)" name, AIToolRegistry.ToolPolicy.forExternalPrincipal p))

/// One caller and one grant predicate, named. The caller is a permission
/// map over the source pool with the three permission levels; the grant
/// predicate is a random per-module answer, defaulting to live.
type private Caller = {
    Name: string
    Access: AccessContext
    GrantLive: string -> bool
}

let private generatedCallers: Caller list =
    let random = Random 79300

    let permissions = [
        [ ModulePermission.Read ]
        [ ModulePermission.Write ]
        [ ModulePermission.Admin ]
        [ ModulePermission.Read; ModulePermission.Write ]
    ]

    let unrestricted = {
        Name = "unrestricted caller, every grant live"
        Access = AccessContext.unrestricted (AuthenticatedUser "alice")
        GrantLive = fun _ -> true
    }

    let generated =
        List.init 10 (fun n ->
            let perms =
                sourceModules
                |> List.filter (fun _ -> random.Next 2 = 0)
                |> List.map (fun m -> m, pick random permissions)

            let inert = sourceModules |> List.filter (fun _ -> random.Next 4 = 0) |> Set.ofList

            {
                Name = sprintf "caller #%d" n
                Access = {
                    AccessContext.unrestricted (AuthenticatedUser(sprintf "user-%d" n)) with
                        ModulePermissions = Map.ofList perms
                }
                GrantLive = fun m -> not (Set.contains m inert)
            })

    unrestricted :: generated

// ─── Running the two, and comparing ──────────────────────────────────

let private productionVerdict
    (caller: Caller)
    (policy: AIToolRegistry.ToolPolicy)
    (tool: AIToolRegistry.RegisteredTool)
    : AIToolRegistry.ToolGateVerdict =
    AIToolRegistry.ToolGate.decide caller.Access caller.GrantLive policy tool.Definition

let private modelVerdict
    (caller: Caller)
    (policy: AIToolRegistry.ToolPolicy)
    (tool: AIToolRegistry.RegisteredTool)
    : AIToolRegistry.ToolGateVerdict =
    let modelled = toModelTool tool

    ToolGate.decide
        (AIToolRegistry.isToolSourcePermittedFor caller.Access)
        caller.GrantLive
        (toModelPolicy policy)
        modelled.source_module
        modelled.effects
    |> ofModelVerdict

/// **The go-red oracle.** Production's own decision, handed a
/// declaration with every `Egress` removed. Right on every tool that
/// does not egress; wrong on exactly the ones the ceiling exists to
/// stop.
let private egressBlindVerdict
    (caller: Caller)
    (policy: AIToolRegistry.ToolPolicy)
    (tool: AIToolRegistry.RegisteredTool)
    : AIToolRegistry.ToolGateVerdict =
    let blinded =
        match AIToolEffects.declaredOf tool.Definition with
        | DeclaredEffects effects ->
            DeclaredEffects(
                effects
                |> Set.filter (fun e ->
                    match e with
                    | Egress _ -> false
                    | _ -> true)
            )
        | undeclared -> undeclared

    AIToolRegistry.ToolGate.decideDeclared
        (AIToolRegistry.isToolSourcePermittedFor caller.Access)
        caller.GrantLive
        policy
        tool.Definition.SourceModule
        blinded

/// Never throws. A throw from either side is a finding, not a crash.
let private verdictDisagreements
    (oracle: Caller -> AIToolRegistry.ToolPolicy -> AIToolRegistry.RegisteredTool -> AIToolRegistry.ToolGateVerdict)
    (tools: AIToolRegistry.RegisteredTool list)
    : string list =
    [
        for caller in generatedCallers do
            for policyName, policy in generatedPolicies do
                for tool in tools do
                    let production =
                        try
                            Choice1Of2(productionVerdict caller policy tool)
                        with ex ->
                            Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

                    let model =
                        try
                            Choice1Of2(oracle caller policy tool)
                        with ex ->
                            Choice2Of2(sprintf "THREW %s: %s" (ex.GetType().Name) ex.Message)

                    match production, model with
                    | Choice1Of2 p, Choice1Of2 m when p = m -> ()
                    | _ ->
                        let show =
                            function
                            | Choice1Of2 v -> sprintf "%A" v
                            | Choice2Of2 text -> text

                        yield
                            sprintf
                                "%s / %s / %s\n    production: %s\n    model:      %s"
                                caller.Name
                                policyName
                                tool.Definition.Name
                                (show production)
                                (show model)
    ]

let private registryUnder (policy: AIToolRegistry.ToolPolicy) (tools: AIToolRegistry.RegisteredTool list) =
    let registry = AIToolRegistry.AIToolRegistry(policy)
    registry.RegisterAll tools
    registry

let private allTools () = inTreeTools @ generatedTools

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 793 - the proved gate as oracle" [

        testCase "the covered set is not vacuous"
        <| fun () ->
            // Every arm below is a `List.isEmpty` over a computed
            // population, which an empty population satisfies trivially -
            // so the population is asserted first.
            Expect.isGreaterThan
                (List.length inTreeTools)
                12
                "the in-tree built-ins, fact tools and sample should all be here"

            Expect.isGreaterThan (List.length generatedTools) 40 "and a generated population beside them"
            Expect.isGreaterThan (List.length generatedPolicies) 20 "over a generated policy set"
            Expect.isGreaterThan (List.length generatedCallers) 8 "and a generated caller set"

            Expect.isTrue
                (generatedTools
                 |> List.exists (fun t ->
                     match t.Definition.Effects with
                     | DeclaredEffects effects ->
                         effects
                         |> Set.exists (fun e ->
                             match e with
                             | Egress _ -> true
                             | _ -> false)
                     | UndeclaredEffects -> false))
                "at least one generated tool must declare Egress, or the go-red case has nothing to catch"

            Expect.isTrue
                (generatedPolicies
                 |> List.exists (fun (_, p) ->
                     match p.Ceiling with
                     | AIToolRegistry.BoundedCeiling classes -> not (Set.contains ToolEffectClass.Egress classes)
                     | AIToolRegistry.UnboundedCeiling -> false))
                "and at least one bounded policy must exclude the egress class"

        testCase
            "the extracted decision agrees with production over every in-tree tool, policy, caller and grant predicate"
        <| fun () ->
            let found = verdictDisagreements modelVerdict inTreeTools

            Expect.isEmpty
                found
                (sprintf
                    "the proved gate and the shipped one must agree on the verdict:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted decision agrees with production over generated tools with generated declarations"
        <| fun () ->
            let found = verdictDisagreements modelVerdict generatedTools

            Expect.isEmpty
                found
                (sprintf
                    "the proved gate and the shipped one must agree over the generated population:\n  %s"
                    (String.concat "\n  " found))

        testCase
            "the extracted list agrees with ListAccessible - the same tools, in the same order - under every policy and caller"
        <| fun () ->
            let tools = allTools ()
            let modelled = tools |> List.map toModelTool

            let found = [
                for caller in generatedCallers do
                    for policyName, policy in generatedPolicies do
                        let production =
                            (registryUnder policy tools).ListAccessible(caller.Access, caller.GrantLive)
                            |> List.map _.Definition.Name

                        let model =
                            ToolGate.list_accessible
                                (AIToolRegistry.isToolSourcePermittedFor caller.Access)
                                caller.GrantLive
                                (toModelPolicy policy)
                                modelled
                            |> List.map _.name

                        if production <> model then
                            yield
                                sprintf
                                    "%s / %s\n    production: %A\n    model:      %A"
                                    caller.Name
                                    policyName
                                    production
                                    model
            ]

            Expect.isEmpty
                found
                (sprintf "the proved list and the shipped one must agree:\n  %s" (String.concat "\n  " found))

        testCase
            "the extracted dispatch check agrees with FindByName-then-decide, by authored name, by alias, and for an unknown name"
        <| fun () ->
            let tools = allTools ()
            let modelled = tools |> List.map toModelTool

            let names =
                (tools |> List.map _.Definition.Name)
                @ (tools |> List.map _.ProviderDef.Name)
                @ [ "no.such.tool"; "" ]

            let found = [
                for caller in generatedCallers do
                    for policyName, policy in generatedPolicies do
                        let registry = registryUnder policy tools

                        for name in names do
                            let production =
                                match registry.FindByName name with
                                | None -> false
                                | Some tool -> AIToolRegistry.ToolGate.admits (productionVerdict caller policy tool)

                            let model =
                                ToolGate.dispatch_admits
                                    (AIToolRegistry.isToolSourcePermittedFor caller.Access)
                                    caller.GrantLive
                                    (toModelPolicy policy)
                                    modelled
                                    name

                            if production <> model then
                                yield
                                    sprintf
                                        "%s / %s / '%s': production %b, model %b"
                                        caller.Name
                                        policyName
                                        name
                                        production
                                        model
            ]

            Expect.isEmpty
                found
                (sprintf "the proved dispatch check and the shipped one must agree:\n  %s" (String.concat "\n  " found))

        testCase "the differential CATCHES a gate that ignores Egress - the go-red case"
        <| fun () ->
            let found = verdictDisagreements egressBlindVerdict (allTools ())

            Expect.isNonEmpty
                found
                "a gate that drops Egress before deciding must be caught: if this passes, the comparison is agreeing \
                 with whatever it is shown and every other arm in this pack is worthless"

        testCase "and egress under a read-only ceiling is what it catches"
        <| fun () ->
            // The mechanism, pinned rather than left to the population:
            // one tool declaring only `Egress`, one read-only policy, an
            // unrestricted caller. Production and the honest oracle refuse
            // naming the egress; the blind oracle admits.
            let tool =
                AIToolRegistry.createTool
                    (toolDef "web.fetch" "Sales" (DeclaredEffects(Set.ofList [ Egress "api.example.com" ])))
                    inert

            let caller = List.head generatedCallers
            let policy = AIToolRegistry.ToolPolicy.readOnly

            Expect.equal
                (productionVerdict caller policy tool)
                (AIToolRegistry.ToolRefusedEffects [ Egress "api.example.com" ])
                "production refuses, naming the egress"

            Expect.equal
                (modelVerdict caller policy tool)
                (AIToolRegistry.ToolRefusedEffects [ Egress "api.example.com" ])
                "the honest oracle agrees - this is `egress_never_escapes` running"

            Expect.equal
                (egressBlindVerdict caller policy tool)
                AIToolRegistry.ToolAdmitted
                "the blind oracle admits it, which is the defect the comparison exists to see"

            Expect.isNonEmpty
                (verdictDisagreements egressBlindVerdict [ tool ])
                "and the comparison reports exactly that"

        testCase "the model never throws, on any tool, under any policy, caller or grant predicate"
        <| fun () ->
            // The lemmas' operational face: every definition in the model
            // is total, but the extraction and the bridge are trusted
            // rather than proved, and this is the arm that would catch
            // either turning a total function into an exception.
            let threw = [
                for caller in generatedCallers do
                    for policyName, policy in generatedPolicies do
                        for tool in allTools () do
                            try
                                modelVerdict caller policy tool |> ignore
                            with ex ->
                                yield
                                    sprintf
                                        "%s / %s / %s: %s %s"
                                        caller.Name
                                        policyName
                                        tool.Definition.Name
                                        (ex.GetType().Name)
                                        ex.Message
            ]

            Expect.isEmpty threw (sprintf "the extracted model threw:\n  %s" (String.concat "\n  " threw))
    ]