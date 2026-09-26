// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IAIProviderModelOverrideContract

// ─── IAIProviderModelOverride conformance pack (Phase 661) ────────
//
// The laws every per-call model override must satisfy, whether it is a
// shipped connector, a decorator over one, or an external implementer:
//
//   • `options.Model = None` serves the call on the configured
//     `Capabilities.Model`, reports `ConfiguredModel`, and returns the
//     response the plain `SendMessage` returns — the override path with
//     nothing to override is the plain path.
//   • an id the implementation can serve is honoured on BOTH paths and
//     reported as `OverrideHonoured id`; `ModelOverrideOutcome.served`
//     names it.
//   • an id it cannot serve is a FALLBACK, never a failure: the call
//     succeeds on the configured model and reports `OverrideFellBack`
//     naming the requested id, the model that served, and a reason.
//   • a blank id is a fallback too — no call is ever issued on an empty
//     model id.
//
// Bound by the in-memory reference implementation and by each shipped
// decorator wrapped over it (see `AIProviderModelOverrideTests`). The
// shipped connectors cannot bind offline — every send is a vendor HTTP
// round-trip — so their family checks are pinned in
// `ToolUp.AIProviders.Tests` instead, and the live half rides the
// env-gated per-provider packs.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI

/// What one binding hands the pack: a provider carrying the
/// implementation under test, and two ids the pack drives it with.
type ModelOverrideSubject = {
    /// The provider under test — the implementation itself, or a
    /// decorator over one. Must implement `IAIProviderModelOverride`.
    Provider: IAIProvider
    /// An id the implementation SERVES on an honoured override. Must
    /// differ from `Provider.Capabilities.Model`.
    Servable: string
    /// An id the implementation cannot serve — it must fall back.
    Unservable: string
}

let private messages = [ AIProviderMessage.text "user" "Say hello in three words or fewer." ]

let private schema =
    """{"type":"object","properties":{"greeting":{"type":"string"}}}"""

let private okCall (label: string) (r: Result<AIProviderCallResponse, AIProviderError>) : AIProviderCallResponse =
    match r with
    | Ok c -> c
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let private okPlain (label: string) (r: Result<AIProviderResponse, AIProviderError>) : AIProviderResponse =
    match r with
    | Ok c -> c
    | Error e -> failtestf "%s: expected Ok, got %A" label e

/// The pack. `name` labels the binding in the Expecto report; `factory`
/// builds a fresh subject per case.
let tests (name: string) (factory: unit -> ModelOverrideSubject) : Test =
    testList $"IAIProviderModelOverride contract — %s{name}" [
        testCase "the subject implements IAIProviderModelOverride"
        <| fun _ ->
            let subject = factory ()

            Expect.isTrue
                (match subject.Provider with
                 | :? IAIProviderModelOverride -> true
                 | _ -> false)
                "a binding of this pack must carry the override — a provider without it is served by the fallback, which is not what is under test here"

            Expect.notEqual
                subject.Servable
                subject.Provider.Capabilities.Model
                "Servable must differ from the configured model"

        testCaseAsync
            "no override: the configured model serves, reported as configured, and the response is the plain send's"
        <| async {
            let subject = factory ()
            let provider = subject.Provider
            let configured = provider.Capabilities.Model

            let! plain = provider.SendMessage(messages, [], Some "sys", None, RetryPolicy.defaults)

            let! viaOptions =
                provider.SendMessageWith(
                    AIProviderCallOptions.none,
                    messages,
                    [],
                    Some "sys",
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall "none" viaOptions
            Expect.equal call.Model (ConfiguredModel configured) "configured"
            Expect.equal (ModelOverrideOutcome.served call.Model) configured "served accessor"
            Expect.equal call.Response (okPlain "plain" plain) "the plain path's response"

            let! structuredPlain =
                provider.SendStructuredMessage(messages, [], Some "sys", schema, RetryPolicy.defaults)

            let! structuredViaOptions =
                provider.SendStructuredMessageWith(
                    AIProviderCallOptions.none,
                    messages,
                    [],
                    Some "sys",
                    schema,
                    RetryPolicy.defaults
                )

            let structuredCall = okCall "none/structured" structuredViaOptions
            Expect.equal structuredCall.Model (ConfiguredModel configured) "configured on the structured path"

            Expect.equal
                structuredCall.Response
                (okPlain "plain/structured" structuredPlain)
                "the plain structured response"
        }

        testCaseAsync "a servable id is honoured on both paths"
        <| async {
            let subject = factory ()
            let options = AIProviderCallOptions.forModel subject.Servable

            let! r = subject.Provider.SendMessageWith(options, messages, [], None, None, RetryPolicy.defaults)
            let call = okCall "servable" r
            Expect.equal call.Model (OverrideHonoured subject.Servable) "honoured"
            Expect.equal (ModelOverrideOutcome.served call.Model) subject.Servable "served accessor"
            Expect.isFalse (ModelOverrideOutcome.fellBack call.Model) "not a fallback"

            let! rs =
                subject.Provider.SendStructuredMessageWith(options, messages, [], None, schema, RetryPolicy.defaults)

            Expect.equal
                (okCall "servable/structured" rs).Model
                (OverrideHonoured subject.Servable)
                "honoured on the structured path"
        }

        testCaseAsync "an unservable id falls back to the configured model and says so — the call succeeds"
        <| async {
            let subject = factory ()
            let configured = subject.Provider.Capabilities.Model
            let options = AIProviderCallOptions.forModel subject.Unservable

            let check (label: string) (r: Result<AIProviderCallResponse, AIProviderError>) =
                let call = okCall label r

                match call.Model with
                | OverrideFellBack(requested, served, reason) ->
                    Expect.equal requested subject.Unservable $"{label}: names the requested id"
                    Expect.equal served configured $"{label}: the configured model served"
                    Expect.isNotEmpty reason $"{label}: with a reason"
                | other -> failtestf "%s: expected OverrideFellBack, got %A" label other

                Expect.isTrue (ModelOverrideOutcome.fellBack call.Model) $"{label}: fellBack"
                Expect.equal (ModelOverrideOutcome.served call.Model) configured $"{label}: served accessor"

            let! r = subject.Provider.SendMessageWith(options, messages, [], None, None, RetryPolicy.defaults)
            check "unservable" r

            let! rs =
                subject.Provider.SendStructuredMessageWith(options, messages, [], None, schema, RetryPolicy.defaults)

            check "unservable/structured" rs
        }

        testCaseAsync "a blank id is a fallback, never a call on an empty model"
        <| async {
            let subject = factory ()
            let configured = subject.Provider.Capabilities.Model

            let! r =
                subject.Provider.SendMessageWith(
                    AIProviderCallOptions.forModel "  ",
                    messages,
                    [],
                    None,
                    None,
                    RetryPolicy.defaults
                )

            let call = okCall "blank" r

            match call.Model with
            | OverrideFellBack(_, served, _) -> Expect.equal served configured "the configured model served"
            | other -> failtestf "expected OverrideFellBack, got %A" other
        }
    ]