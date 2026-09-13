// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.ConsentDialog

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AriaProp
open ToolUp.AI

// ─── Phase 36.D — cross-module read consent ──────────────────────
//
// The browser half of the consent round trip. A `_platform.ai.*` tool is
// about to read from a module the user has not approved in this
// conversation; the server has suspended it and emitted an
// `AIConsentRequired` SSE event. This module renders the question and
// POSTs the answer to `/api/ai/consent`, which resumes the read.
//
// **It is routed OUT OF BAND, exactly as `ClientToolInvoke` is.** The
// suspend/resume of an agent-loop tool call is a transport concern, not a
// UI-state concern, and routing it through Elmish would put the question
// in one surface's model — so a user chatting in the full-page assistant
// while the side panel is collapsed, or vice versa, would answer a dialog
// the other surface owns. `SSEClient` hands the event straight here; a
// module-level bridge holds the pending request and a mounted component
// renders it, which is the `PromptAccessoryBridge` / `FastPathBridge`
// idiom this tier already uses.

let private log = Logger.forCategory "ai.consent"

// ─── Bridge ──────────────────────────────────────────────────────

/// One pending question, as the dialog renders it.
type PendingConsentRequest = {
    TaskId: Guid
    ConsentId: Guid
    ConversationId: Guid
    /// The `_platform.ai.*` tool that is about to read.
    ToolName: string
    /// The module it is about to read FROM — the subject of the question.
    TargetModule: string
    /// What it intends to read, when the tool has a discriminator for it
    /// (a query key, a result type, an entity type). `""` otherwise.
    IntendedQueryKey: string
    /// A short rendering of the model's own arguments. Never the module's
    /// data — the read has not happened.
    RedactedPayloadPreview: string
}

// Sanctioned mutable globals — per-tab singletons with an effectively
// final lifetime, the same precedent as `ClientToolRuntime.registry`,
// `FastPathBridge.resolver` and `NotificationClient.state`.
//
// A LIST rather than a single slot: a turn can dispatch several
// `_platform.ai.*` calls in one parallel batch, each suspending on its own
// module. Dropping all but the newest would leave the others to time out
// silently, which is the failure the 90 s budget exists to make loud.
let mutable private pending: PendingConsentRequest list = []
let mutable private listeners: (PendingConsentRequest list -> unit) list = []

let private notifyListeners () =
    for listener in listeners do
        try
            listener pending
        with ex ->
            // A subscriber that throws must not stop its siblings being
            // told, or one broken surface silently blocks every consent
            // prompt in the tab.
            log.Error("consent listener raised", Some ex)

/// Subscribe to the pending-question list. Returns an unsubscribe thunk.
/// Called by the mounted dialog component; a consumer rendering its own
/// consent UI can use it too.
let subscribe (listener: PendingConsentRequest list -> unit) : unit -> unit =
    listeners <- listener :: listeners
    listener pending

    fun () -> listeners <- listeners |> List.filter (fun l -> not (obj.ReferenceEquals(l, listener)))

/// Phase 36.D: handle an incoming `AIConsentRequired` SSE event. Called by
/// `SSEClient.subscribe`, not dispatched into Elmish — see the module
/// header for why.
///
/// Idempotent on `ConsentId`: SSE delivery can repeat across a reconnect,
/// and a duplicated question would be a second dialog for one suspended
/// read.
let handleRequired
    (taskId: Guid)
    (consentId: Guid)
    (conversationId: Guid)
    (toolName: string)
    (targetModule: string)
    (intendedQueryKey: string)
    (redactedPayloadPreview: string)
    : unit =
    if pending |> List.exists (fun p -> p.ConsentId = consentId) then
        ()
    else
        pending <-
            pending
            @ [
                {
                    TaskId = taskId
                    ConsentId = consentId
                    ConversationId = conversationId
                    ToolName = toolName
                    TargetModule = targetModule
                    IntendedQueryKey = intendedQueryKey
                    RedactedPayloadPreview = redactedPayloadPreview
                }
            ]

        notifyListeners ()

// ─── POST ────────────────────────────────────────────────────────

// `window.fetch` (not bare `fetch`) so this resolves the guarded global at
// call time — the CsrfClient request-guard wraps `window.fetch` to attach
// identity + `X-CSRF-Token`. Same shape as `ClientToolRuntime.postResult`;
// a captured binding here would be a latent bypass.
[<Emit("window.fetch($0, $1)")>]
let private fetch (url: string) (opts: obj) : JS.Promise<obj> = jsNative

/// POST the user's answer. A failed POST leaves the server-side read
/// suspended until its own 90 s budget fires, which then refuses the read
/// — the safe direction, and the reason this does not retry.
let private postDecision (consentId: Guid) (decision: AllowDecision) : Async<unit> = async {
    let request: AIConsentDecisionRequest = {
        ConsentId = consentId
        Decision = AllowDecision.toToken decision
    }

    let opts =
        createObj [
            "method" ==> "POST"
            "headers"
            ==> createObj [
                "Content-Type" ==> "application/json"
                "X-User-Id" ==> UserSession.getUserId ()
            ]
            "body" ==> Json.serialize request
        ]

    try
        do! fetch "/api/ai/consent" opts |> Async.AwaitPromise |> Async.Ignore
    with ex ->
        log.Error("Failed to POST AI consent decision", Some ex)
}

/// Answer one pending question: drop it from the list, tell the
/// subscribers, and POST. The local drop happens FIRST so the dialog
/// closes on the click rather than on the round trip.
let answer (consentId: Guid) (decision: AllowDecision) : unit =
    pending <- pending |> List.filter (fun p -> p.ConsentId <> consentId)
    notifyListeners ()
    postDecision consentId decision |> Async.StartImmediate

// ─── Dialog ──────────────────────────────────────────────────────

let private requestSummary (request: PendingConsentRequest) =
    if String.IsNullOrWhiteSpace request.IntendedQueryKey then
        request.ToolName
    else
        $"{request.ToolName} · {request.IntendedQueryKey}"

/// The consent modal. Renders the OLDEST pending question — one at a
/// time, so a parallel tool batch asks its questions in the order they
/// were raised rather than stacking overlays.
///
/// Mount it once per tab. `ConversationPanel.View` does so for the SDK's
/// composed shell, in both its open and collapsed branches: the question
/// belongs to the conversation, not to whether the panel happens to be
/// expanded, and a user whose panel is closed still has a suspended read
/// waiting on them.
[<ReactComponent>]
let View () =
    let queue, setQueue = React.useState<PendingConsentRequest list> []

    React.useEffectOnce (fun () ->
        let unsubscribe = subscribe setQueue
        FsReact.createDisposable (fun () -> unsubscribe ()))

    match queue with
    | [] -> Html.none
    | request :: _ ->
        Html.div [
            prop.className "fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4"
            prop.role "dialog"
            // `ariaProp.modal` / `prop.ariaLabel` rather than raw
            // `prop.custom` — the DOM attr-custom audit ratchet
            // (`DomAttrCustomAuditTests`) keeps every `aria-*` site on one
            // seam, so a future React tightening of its attribute
            // normaliser is one edit rather than a silent regression
            // across every call site.
            ariaProp.modal true
            prop.ariaLabel "AI data access request"
            prop.children [
                Html.div [
                    prop.className "w-full max-w-md rounded-lg bg-white shadow-xl border border-gray-200"
                    prop.children [
                        Html.div [
                            prop.className "px-5 py-4 border-b border-gray-200"
                            prop.children [
                                Html.h2 [
                                    prop.className "text-sm font-semibold text-gray-800"
                                    prop.text "Allow the assistant to read this module?"
                                ]
                            ]
                        ]

                        Html.div [
                            prop.className "px-5 py-4 space-y-3"
                            prop.children [
                                Html.p [
                                    prop.className "text-sm text-gray-700"
                                    prop.children [
                                        Html.text "The assistant is about to read from "
                                        Html.span [
                                            prop.className "font-semibold text-gray-900"
                                            prop.text request.TargetModule
                                        ]
                                        Html.text " to answer this."
                                    ]
                                ]

                                Html.p [
                                    prop.className "text-xs text-gray-500 font-mono break-all"
                                    prop.text (requestSummary request)
                                ]

                                if not (String.IsNullOrWhiteSpace request.RedactedPayloadPreview) then
                                    Html.pre [
                                        prop.className
                                            "text-xs text-gray-600 bg-gray-50 border border-gray-200 rounded p-2 whitespace-pre-wrap break-all max-h-32 overflow-y-auto"
                                        prop.text request.RedactedPayloadPreview
                                    ]

                                if List.length queue > 1 then
                                    Html.p [
                                        prop.className "text-xs text-gray-500"
                                        prop.text
                                            $"{List.length queue - 1} further request(s) are waiting on your answer."
                                    ]
                            ]
                        ]

                        Html.div [
                            prop.className "px-5 py-4 border-t border-gray-200 flex flex-wrap justify-end gap-2"
                            prop.children [
                                Html.button [
                                    prop.className
                                        "px-3 py-1.5 text-sm rounded border border-gray-300 text-gray-700 hover:bg-gray-50"
                                    prop.text "Deny"
                                    prop.onClick (fun _ -> answer request.ConsentId Denied)
                                ]
                                Html.button [
                                    prop.className
                                        "px-3 py-1.5 text-sm rounded border border-violet-300 text-violet-700 hover:bg-violet-50"
                                    prop.text "Allow once"
                                    prop.onClick (fun _ -> answer request.ConsentId AllowOnce)
                                ]
                                Html.button [
                                    prop.className
                                        "px-3 py-1.5 text-sm rounded bg-violet-600 text-white hover:bg-violet-700"
                                    prop.text "Allow for this conversation"
                                    prop.onClick (fun _ -> answer request.ConsentId AllowForConversation)
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]