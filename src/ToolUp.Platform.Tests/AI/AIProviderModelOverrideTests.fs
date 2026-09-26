// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.AIProviderModelOverrideTests

// ─── Phase 661 — per-call model override on `IAIProvider` ──────────
//
// Three bars.
//
// The RESOLUTION bar is `ModelOverrideOutcome.resolve` and the
// `ModelIdFamily` vocabulary: the one rule every connector applies to
// decide which model serves a call and how it reports that. Pure, so
// tested directly.
//
// The DISPATCH bar is the `SendMessageWith` / `SendStructuredMessageWith`
// extensions on `IAIProvider`: a provider that implements
// `IAIProviderModelOverride` is served natively; one that does not is
// served on its configured model with the fallback in the response
// metadata — and the request it sees is exactly the plain send's.
//
// The DECORATOR bar: the shipped metering and quota decorators forward
// the override path with their own rule applied, so a composed
// deployment cannot lose the override to a wrapper — and the metering
// decorator bills the model that SERVED, not the one configured.

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Usage
open ToolUp.AI
open ToolUp.Platform.Tests.Contracts

// ─── Fakes ───────────────────────────────────────────────────────

let private capsFor (providerName: string) (model: string) = {
    AIProviderCapabilities.unknown with
        ProviderName = providerName
        Model = model
}

let private usage: TokenUsage = {
    PromptTokens = 1000
    CachedPromptTokens = 0
    OutputTokens = 200
    CacheCreationTokens = None
}

let private okResponse (content: string) : AIProviderResponse = {
    Content = content
    ToolCalls = []
    StopReason = "end_turn"
    Usage = Some usage
}

let private userMessage (text: string) = AIProviderMessage.text "user" text

/// An `IAIProvider` with NO per-call override — the shape every
/// pre-661 connector, decorator and test double has. It echoes the
/// system prompt it was handed so a test can prove the fallback path
/// changed nothing about the request.
type private PlainProvider(providerName: string, model: string) =
    let mutable sendCalls = 0
    let mutable structuredCalls = 0
    member _.SendCalls = sendCalls
    member _.StructuredCalls = structuredCalls
    member val LastSystemPrompt: string option = None with get, set

    interface IAIProvider with
        member _.Capabilities = capsFor providerName model

        member this.SendMessage(_messages, _tools, systemPrompt, _onStream, _retryPolicy) = async {
            sendCalls <- sendCalls + 1
            this.LastSystemPrompt <- systemPrompt
            return Ok(okResponse "plain")
        }

        member this.SendStructuredMessage(_messages, _tools, systemPrompt, _schema, _retryPolicy) = async {
            structuredCalls <- structuredCalls + 1
            this.LastSystemPrompt <- systemPrompt
            return Ok(okResponse "{}")
        }

/// An `IAIProvider` that ALSO implements the optional override, the
/// way the shipped connectors do: it resolves the requested model
/// through `ModelOverrideOutcome.resolve` with a family check and
/// reports the outcome. It records the options it was handed.
type private NativeProvider(providerName: string, model: string, canServe: string -> bool) =
    let mutable sendCalls = 0
    let mutable nativeCalls = 0
    member _.SendCalls = sendCalls
    member _.NativeCalls = nativeCalls
    member val LastOptions: AIProviderCallOptions option = None with get, set
    member val LastSystemPrompt: string option = None with get, set

    member private this.Serve(options: AIProviderCallOptions) =
        nativeCalls <- nativeCalls + 1
        this.LastOptions <- Some options

        let served, outcome =
            ModelOverrideOutcome.resolve canServe "wrong family" model options

        served, outcome

    interface IAIProvider with
        member _.Capabilities = capsFor providerName model

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            sendCalls <- sendCalls + 1
            return Ok(okResponse "plain")
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            sendCalls <- sendCalls + 1
            return Ok(okResponse "{}")
        }

    // Like the shipped connectors: a call that resolves to the configured
    // model IS the plain send; one that resolves elsewhere is served
    // "there" (here: a response naming the model, standing in for the
    // sibling instance a connector builds).
    interface IAIProviderModelOverride with
        member this.SendMessageWith(options, messages, tools, systemPrompt, onStream, retryPolicy) =
            let served, outcome = this.Serve options
            this.LastSystemPrompt <- systemPrompt

            if served = model then
                (this :> IAIProvider).SendMessage(messages, tools, systemPrompt, onStream, retryPolicy)
                |> AIProviderCallResponse.attach outcome
            else
                async {
                    return
                        Ok {
                            Response = okResponse ("served-on:" + served)
                            Model = outcome
                        }
                }

        member this.SendStructuredMessageWith(options, messages, tools, systemPrompt, schema, retryPolicy) =
            let served, outcome = this.Serve options
            this.LastSystemPrompt <- systemPrompt

            if served = model then
                (this :> IAIProvider).SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
                |> AIProviderCallResponse.attach outcome
            else
                async {
                    return
                        Ok {
                            Response = okResponse ("{\"served\":\"" + served + "\"}")
                            Model = outcome
                        }
                }

let private factoryOver (provider: IAIProvider) =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ctx = async { return Ok provider }
        member _.TryResolveByLabel(_ctx, _label) = async { return Ok provider }
        member _.BuildPlatform(_providerId, _apiKey, _model) = Some provider
    }

let private emptyProviderProfile =
    { new IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

type private CollectingUsageLog() =
    let records = ConcurrentBag<UsageRecord>()
    member _.Records = records |> List.ofSeq

    interface IUsageLog with
        member _.Record record = async { records.Add record }
        member _.Query(_scopeId, _resourceKind, _range) = async { return [] }
        member _.Aggregate(_scopeId, _grouping) = async { return Map.empty }

let private quotaPolicy (refuse: bool) =
    { new ITeamQuotaPolicy with
        member _.CheckJobConcurrency(_scopeId, _currentInFlight) = async { return Ok() }

        member _.CheckTokenBudget(scopeId, kind, _requested) = async {
            if refuse then
                return
                    Error {
                        Kind = kind
                        Limit = 10M
                        Requested = 11M
                        ScopeId = scopeId
                    }
            else
                return Ok()
        }
    }

let private scopeId = "team-1"
let private alice = "alice"

let private resolveThrough (factory: IAIProviderFactory) =
    async {
        let ctx = AccessContext.unrestricted (TeamMember(alice, scopeId))
        let! resolved = factory.Resolve ctx

        match resolved with
        | Error e -> return failwithf "provider resolution failed: %A" e
        | Ok provider -> return provider
    }
    |> Async.RunSynchronously

let private okCall (r: Result<AIProviderCallResponse, AIProviderError>) : AIProviderCallResponse =
    match r with
    | Ok c -> c
    | Error e -> failwithf "expected Ok, got %A" e

let private isAcme (id: string) = id.StartsWith "acme"

// ─── Resolution ──────────────────────────────────────────────────

let private resolutionTests =
    testList "ModelOverrideOutcome.resolve — the one rule" [
        testCase "no override ⇒ the configured model, reported as configured"
        <| fun _ ->
            let served, outcome =
                ModelOverrideOutcome.resolve isAcme "wrong family" "acme-1" AIProviderCallOptions.none

            Expect.equal served "acme-1" "configured model serves"
            Expect.equal outcome (ConfiguredModel "acme-1") "and says so"
            Expect.isFalse (ModelOverrideOutcome.fellBack outcome) "not a fallback"
            Expect.equal (ModelOverrideOutcome.route outcome) "configured" "route tag"

        testCase "a servable id ⇒ that id, honoured"
        <| fun _ ->
            let served, outcome =
                ModelOverrideOutcome.resolve isAcme "wrong family" "acme-1" (AIProviderCallOptions.forModel "acme-mini")

            Expect.equal served "acme-mini" "the override serves"
            Expect.equal outcome (OverrideHonoured "acme-mini") "reported as honoured"
            Expect.equal (ModelOverrideOutcome.served outcome) "acme-mini" "served accessor"
            Expect.equal (ModelOverrideOutcome.route outcome) "override" "route tag"

        testCase "the configured id itself is trivially honoured"
        <| fun _ ->
            let _, outcome =
                ModelOverrideOutcome.resolve isAcme "wrong family" "acme-1" (AIProviderCallOptions.forModel "acme-1")

            Expect.equal outcome (OverrideHonoured "acme-1") "honoured, not a fallback"

        testCase "an unservable id ⇒ the configured model, with the connector's reason"
        <| fun _ ->
            let served, outcome =
                ModelOverrideOutcome.resolve isAcme "wrong family" "acme-1" (AIProviderCallOptions.forModel "claude-x")

            Expect.equal served "acme-1" "falls back to the configured model"
            Expect.equal outcome (OverrideFellBack("claude-x", "acme-1", "wrong family")) "and names the reason"
            Expect.isTrue (ModelOverrideOutcome.fellBack outcome) "a fallback"
            Expect.equal (ModelOverrideOutcome.route outcome) "override-fallback" "route tag"
            Expect.stringContains (ModelOverrideOutcome.describe outcome) "claude-x" "describe names the request"

        testCase "a blank id ⇒ fallback, never a call on an empty model"
        <| fun _ ->
            let served, outcome =
                ModelOverrideOutcome.resolve isAcme "wrong family" "acme-1" (AIProviderCallOptions.forModel "   ")

            Expect.equal served "acme-1" "configured model"

            match outcome with
            | OverrideFellBack(_, "acme-1", reason) -> Expect.stringContains reason "blank" "says why"
            | other -> failtestf "expected a fallback, got %A" other
    ]

let private familyTests =
    testList "ModelIdFamily — the vendor vocabulary" [
        testCase "Anthropic ids"
        <| fun _ ->
            Expect.isTrue (ModelIdFamily.isAnthropic "claude-haiku-4-5-20251001") "claude-*"
            Expect.isTrue (ModelIdFamily.isAnthropic " Claude-Opus ") "case- and whitespace-insensitive"
            Expect.isFalse (ModelIdFamily.isAnthropic "gpt-4o-mini") "not gpt"
            Expect.isFalse (ModelIdFamily.isAnthropic "") "not blank"

        testCase "Google ids, with or without the models/ prefix"
        <| fun _ ->
            Expect.isTrue (ModelIdFamily.isGoogle "models/gemini-2.5-flash") "prefixed"
            Expect.isTrue (ModelIdFamily.isGoogle "gemini-2.5-pro") "bare"
            Expect.isTrue (ModelIdFamily.isGoogle "gemma-3-27b-it") "gemma"
            Expect.isFalse (ModelIdFamily.isGoogle "claude-haiku-4-5") "not claude"

        testCase "OpenAI-compatible is everything that is neither"
        <| fun _ ->
            Expect.isTrue (ModelIdFamily.isOpenAICompatible "gpt-4o-mini") "gpt"
            Expect.isTrue (ModelIdFamily.isOpenAICompatible "o1-mini") "o-series"
            Expect.isTrue (ModelIdFamily.isOpenAICompatible "my-azure-deployment") "a deployment name"
            Expect.isFalse (ModelIdFamily.isOpenAICompatible "claude-haiku-4-5") "not claude"
            Expect.isFalse (ModelIdFamily.isOpenAICompatible "models/gemini-2.5-flash") "not gemini"
            Expect.isFalse (ModelIdFamily.isOpenAICompatible "") "not blank"
    ]

// ─── Dispatch ────────────────────────────────────────────────────

let private dispatchTests =
    testList "SendMessageWith on IAIProvider" [
        testCaseAsync "a provider implementing the override is served natively"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)
            let provider = native :> IAIProvider

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    Some "sys",
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall r
            Expect.equal native.NativeCalls 1 "the native path ran"
            Expect.equal native.SendCalls 0 "and the plain send did not"
            Expect.equal native.LastOptions (Some(AIProviderCallOptions.forModel "acme-mini")) "with the options"
            Expect.equal call.Model (OverrideHonoured "acme-mini") "honoured"
            Expect.equal call.Response.Content "served-on:acme-mini" "the cheap model served"
        }

        testCaseAsync "a native provider that cannot serve the id falls back and says so — the call succeeds"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)
            let provider = native :> IAIProvider

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "claude-haiku",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall r
            Expect.equal call.Model (OverrideFellBack("claude-haiku", "acme-1", "wrong family")) "reported"
            Expect.equal call.Response.Content "plain" "served on the configured model — the plain send"
        }

        testCaseAsync
            "a provider WITHOUT the override is served on its configured model, and the request is the plain send's"
        <| async {
            let plain = PlainProvider("acme", "acme-1")
            let provider = plain :> IAIProvider

            let! direct = provider.SendMessage([ userMessage "hi" ], [], Some "sys", None, RetryPolicy.defaults)

            let! viaOptions =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    Some "sys",
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall viaOptions
            Expect.equal plain.SendCalls 2 "both went through the plain send — one call each"
            Expect.equal plain.LastSystemPrompt (Some "sys") "the system prompt reached it untouched"
            Expect.equal (Ok call.Response) direct "the response is the plain send's"

            match call.Model with
            | OverrideFellBack("acme-mini", "acme-1", reason) ->
                Expect.stringContains reason "IAIProviderModelOverride" "the reason names the missing capability"
            | other -> failtestf "expected a fallback, got %A" other
        }

        testCaseAsync "no override on a provider without the capability reads as configured — absent-override parity"
        <| async {
            let plain = PlainProvider("acme", "acme-1")
            let provider = plain :> IAIProvider

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.none,
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall r
            Expect.equal call.Model (ConfiguredModel "acme-1") "configured, not a fallback"
            Expect.equal plain.SendCalls 1 "one plain send"
        }

        testCaseAsync "the structured path dispatches the same way"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)
            let plain = PlainProvider("acme", "acme-1")

            let! viaNative =
                (native :> IAIProvider)
                    .SendStructuredMessageWith(
                        AIProviderCallOptions.forModel "acme-mini",
                        [ userMessage "hi" ],
                        [],
                        Some "sys",
                        "{}",
                        RetryPolicy.defaults
                    )

            let! viaPlain =
                (plain :> IAIProvider)
                    .SendStructuredMessageWith(
                        AIProviderCallOptions.forModel "acme-mini",
                        [ userMessage "hi" ],
                        [],
                        Some "sys",
                        "{}",
                        RetryPolicy.defaults
                    )

            Expect.equal (okCall viaNative).Model (OverrideHonoured "acme-mini") "native: honoured"
            Expect.equal native.NativeCalls 1 "native path"
            Expect.equal plain.StructuredCalls 1 "plain: the structured send ran"
            Expect.isTrue (ModelOverrideOutcome.fellBack (okCall viaPlain).Model) "plain: reported as a fallback"
            Expect.equal (okCall viaPlain).Response.Content "{}" "plain: the structured response"
        }

        testCaseAsync "the ModelInput-typed entry renders once and forwards the options"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)

            let input =
                ModelInput.ofSystemPrompt "test" (Some "rendered-system") [ userMessage "set country to UK" ]

            let! r =
                (native :> IAIProvider)
                    .SendStructuredMessageWith(
                        AIProviderCallOptions.forModel "acme-mini",
                        input,
                        [],
                        "{}",
                        RetryPolicy.defaults
                    )

            Expect.equal (okCall r).Model (OverrideHonoured "acme-mini") "the options reached the provider"
            Expect.equal native.LastSystemPrompt (Some "rendered-system") "and the rendered system prompt did too"
        }
    ]

// ─── Decorators ──────────────────────────────────────────────────

let private cheapPrice = ModelPrice.simple 0.10M 0.50M
let private frontierPrice = ModelPrice.simple 3.00M 15.00M

let private rateCard =
    ModelPriceTable.ofList "USD" [ "acme", "acme-1", frontierPrice; "acme", "acme-mini", cheapPrice ]

let private decoratorTests =
    testList "the shipped decorators forward the override" [
        testCaseAsync "metering bills the model that SERVED an honoured override"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)
            let log = CollectingUsageLog()

            let factory =
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver native,
                    log,
                    emptyProviderProfile,
                    Some rateCard
                )
                :> IAIProviderFactory

            let provider = resolveThrough factory

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            Expect.equal (okCall r).Model (OverrideHonoured "acme-mini") "the override survived the wrapper"
            let records = log.Records
            Expect.hasLength records 3 "two token records + the cost true-up"

            for record in records do
                Expect.equal (Map.find "model" record.Metadata) "acme-mini" "billed to the served model"

            let cost = records |> List.find (fun r -> r.ResourceKind = AISpendResourceKind.cost)
            Expect.equal (Map.find "price_key" cost.Metadata) "acme/acme-mini" "priced at the cheap rate"
            // 1000 input @ 0.10/M + 200 output @ 0.50/M
            Expect.equal cost.Quantity 0.0002M "the cheap tier's price, not the frontier's"
        }

        testCaseAsync "metering bills the configured model when the override fell back"
        <| async {
            let plain = PlainProvider("acme", "acme-1")
            let log = CollectingUsageLog()

            let factory =
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver plain,
                    log,
                    emptyProviderProfile,
                    Some rateCard
                )
                :> IAIProviderFactory

            let provider = resolveThrough factory

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            Expect.isTrue (ModelOverrideOutcome.fellBack (okCall r).Model) "fell back"

            for record in log.Records do
                Expect.equal (Map.find "model" record.Metadata) "acme-1" "billed to what actually served"
        }

        testCaseAsync "the AI-side quota gate refuses the override path before any provider call"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)

            let factory =
                AIProviderUsageMiddleware.QuotaEnforcingProviderFactory(factoryOver native, quotaPolicy true)
                :> IAIProviderFactory

            let provider = resolveThrough factory

            let! r =
                provider.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            match r with
            | Error(PermanentClient(429, _)) -> ()
            | other -> failtestf "expected a 429 refusal, got %A" other

            Expect.equal native.NativeCalls 0 "the provider was never invoked"
            Expect.equal native.SendCalls 0 "on either path"
        }

        testCaseAsync "the AI-side quota gate forwards the override when the budget allows"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)

            let factory =
                AIProviderUsageMiddleware.QuotaEnforcingProviderFactory(factoryOver native, quotaPolicy false)
                :> IAIProviderFactory

            let provider = resolveThrough factory

            let! r =
                provider.SendStructuredMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    "{}",
                    RetryPolicy.defaults
                )

            Expect.equal (okCall r).Model (OverrideHonoured "acme-mini") "honoured through the wrapper"
            Expect.equal native.LastOptions (Some(AIProviderCallOptions.forModel "acme-mini")) "the options arrived"
        }

        testCaseAsync "the Platform-side quota-gated provider applies its estimate to the override path"
        <| async {
            let native = NativeProvider("acme", "acme-1", isAcme)

            let refused =
                TeamQuotaPolicy.QuotaGatedAIProvider(native, scopeId, quotaPolicy true) :> IAIProvider

            let! r =
                refused.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            match r with
            | Error(PermanentClient(429, _)) -> ()
            | other -> failtestf "expected a 429 refusal, got %A" other

            Expect.equal native.NativeCalls 0 "never invoked"

            let allowed =
                TeamQuotaPolicy.QuotaGatedAIProvider(native, scopeId, quotaPolicy false) :> IAIProvider

            let! r2 =
                allowed.SendMessageWith(
                    AIProviderCallOptions.forModel "acme-mini",
                    [ userMessage "hi" ],
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            Expect.equal (okCall r2).Model (OverrideHonoured "acme-mini") "forwarded when allowed"
        }
    ]

// ─── The conformance pack, bound per implementation ──────────────
//
// `IAIProviderModelOverride` is a replaceable seam (Phase 259: a public
// interface with two or more production implementations), so it carries
// a pack. Bound here by the in-memory reference and by each shipped
// decorator over it — the decorators are the production implementations
// that CAN run offline; the connectors' family checks are pinned in
// `ToolUp.AIProviders.Tests`.

let private referenceSubject () : IAIProviderModelOverrideContract.ModelOverrideSubject = {
    Provider = NativeProvider("acme", "acme-1", isAcme) :> IAIProvider
    Servable = "acme-mini"
    Unservable = "claude-haiku"
}

let private wrapped (wrap: IAIProvider -> IAIProvider) () : IAIProviderModelOverrideContract.ModelOverrideSubject =
    let reference = referenceSubject ()

    {
        reference with
            Provider = wrap reference.Provider
    }

let private contractBindings =
    testList "conformance pack bindings" [
        IAIProviderModelOverrideContract.tests "in-memory reference" referenceSubject

        IAIProviderModelOverrideContract.tests
            "QuotaGatedAIProvider (Platform.Server)"
            (wrapped (fun inner -> TeamQuotaPolicy.QuotaGatedAIProvider(inner, scopeId, quotaPolicy false)))

        IAIProviderModelOverrideContract.tests
            "QuotaEnforcingProvider (AI.Server)"
            (wrapped (fun inner ->
                AIProviderUsageMiddleware.QuotaEnforcingProviderFactory(factoryOver inner, quotaPolicy false)
                :> IAIProviderFactory
                |> resolveThrough))

        IAIProviderModelOverrideContract.tests
            "MeteringProvider (AI.Server)"
            (wrapped (fun inner ->
                AIProviderUsageMiddleware.MeteringProviderFactory(
                    factoryOver inner,
                    CollectingUsageLog(),
                    emptyProviderProfile,
                    Some rateCard
                )
                :> IAIProviderFactory
                |> resolveThrough))
    ]

let tests =
    testList "Phase 661 — per-call model override" [
        resolutionTests
        familyTests
        dispatchTests
        decoratorTests
        contractBindings
    ]