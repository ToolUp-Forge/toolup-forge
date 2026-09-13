// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.ToolApprovalDialog

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AriaProp
open ToolUp.AI

// ─── Phase 503 — human-in-the-loop tool approval ─────────────────
//
// The browser half of the approval round trip. The agent loop is about to
// run a tool the deployment's policy declared consequential; the server
// has held the invocation and emitted a `ToolApprovalRequired` SSE event.
// This module renders the question — the tool, its arguments, and the
// deployment's own words about what will happen — and POSTs the answer to
// `/api/ai/tool-approval`, which either resumes the invocation or refuses
// it.
//
// **It is routed OUT OF BAND, exactly as `ClientToolInvoke` and
// `AIConsentRequired` are**, and for the same reason: a held server-side
// invocation waiting on the user is a transport concern, and routing it
// through one surface's Elmish model would tie the question to whichever
// surface happens to be mounted. `SSEClient` hands the event straight
// here; a module-level bridge holds it and a mounted component renders
// it — the `ConsentDialog` / `PromptAccessoryBridge` / `FastPathBridge`
// idiom this tier already uses.
//
// The dialog is deliberately a SEPARATE host from `ConsentDialog`,
// because the two ask different questions with different answers: consent
// is about a module and has a remember-me case, approval is about one
// call with the arguments on screen and deliberately has none.

let private log = Logger.forCategory "ai.tool-approval"

// ─── Bridge ──────────────────────────────────────────────────────

/// One held invocation, as the dialog renders it.
type PendingApprovalRequest = {
    /// The `AITask` the held turn belongs to. Carried for parity with the
    /// other suspended-dispatch events; the answer keys on `ApprovalId`.
    TaskId: Guid
    /// Correlates the answer with the held invocation.
    ApprovalId: Guid
    /// The conversation the held turn belongs to.
    ConversationId: Guid
    /// The tool about to run, by its canonical registry name.
    ToolName: string
    /// The tool's declaring module.
    SourceModule: string
    /// The deployment policy's one-line question. Rendered as the
    /// heading.
    Summary: string
    /// The deployment policy's account of the consequence. May be empty.
    Detail: string
    /// A short rendering of the model's own arguments — what the tool
    /// would be called WITH. Never the module's data: nothing has run.
    RedactedArgumentsPreview: string
}

// Sanctioned mutable globals — per-tab singletons with an effectively
// final lifetime, the same precedent as `ClientToolRuntime.registry`,
// `ConsentDialog`'s bridge, `FastPathBridge.resolver` and
// `NotificationClient.state`.
//
// A LIST rather than a single slot: a turn can dispatch several tool
// calls in one parallel batch, each held on its own decision. Dropping
// all but the newest would leave the others to time out silently, which
// is the failure the 90 s budget exists to make loud.
let mutable private pending: PendingApprovalRequest list = []
let mutable private listeners: (PendingApprovalRequest list -> unit) list = []

let private notifyListeners () =
    for listener in listeners do
        try
            listener pending
        with ex ->
            // A subscriber that throws must not stop its siblings being
            // told, or one broken surface silently blocks every approval
            // prompt in the tab.
            log.Error("tool-approval listener raised", Some ex)

/// Subscribe to the held-invocation list. Returns an unsubscribe thunk.
/// Called by the mounted dialog component; a consumer rendering its own
/// approval UI can use it too.
let subscribe (listener: PendingApprovalRequest list -> unit) : unit -> unit =
    listeners <- listener :: listeners
    listener pending

    fun () -> listeners <- listeners |> List.filter (fun l -> not (obj.ReferenceEquals(l, listener)))

/// Phase 503: handle an incoming `ToolApprovalRequired` SSE event. Called
/// by `SSEClient.subscribe`, not dispatched into Elmish — see the module
/// header for why.
///
/// Idempotent on `ApprovalId`: SSE delivery can repeat across a
/// reconnect, and a duplicated question would be a second dialog for one
/// held invocation.
let handleRequired
    (taskId: Guid)
    (approvalId: Guid)
    (conversationId: Guid)
    (toolName: string)
    (sourceModule: string)
    (summary: string)
    (detail: string)
    (redactedArgumentsPreview: string)
    : unit =
    if pending |> List.exists (fun p -> p.ApprovalId = approvalId) then
        ()
    else
        pending <-
            pending
            @ [
                {
                    TaskId = taskId
                    ApprovalId = approvalId
                    ConversationId = conversationId
                    ToolName = toolName
                    SourceModule = sourceModule
                    Summary = summary
                    Detail = detail
                    RedactedArgumentsPreview = redactedArgumentsPreview
                }
            ]

        notifyListeners ()

// ─── POST ────────────────────────────────────────────────────────

// `window.fetch` (not bare `fetch`) so this resolves the guarded global
// at call time — the CsrfClient request-guard wraps `window.fetch` to
// attach identity + `X-CSRF-Token`. Same shape as
// `ClientToolRuntime.postResult`; a captured binding here would be a
// latent bypass.
[<Emit("window.fetch($0, $1)")>]
let private fetch (url: string) (opts: obj) : JS.Promise<obj> = jsNative

/// POST the user's answer. A failed POST leaves the invocation held until
/// its own 90 s budget fires, which then REFUSES it — the safe direction,
/// and the reason this does not retry.
let private postDecision (approvalId: Guid) (decision: ToolApprovalDecision) : Async<unit> = async {
    let request: ToolApprovalDecisionRequest = {
        ApprovalId = approvalId
        Decision = ToolApprovalDecision.toToken decision
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
        do! fetch "/api/ai/tool-approval" opts |> Async.AwaitPromise |> Async.Ignore
    with ex ->
        log.Error("Failed to POST AI tool-approval decision", Some ex)
}

/// Answer one held invocation: drop it from the list, tell the
/// subscribers, and POST. The local drop happens FIRST so the dialog
/// closes on the click rather than on the round trip.
let answer (approvalId: Guid) (decision: ToolApprovalDecision) : unit =
    pending <- pending |> List.filter (fun p -> p.ApprovalId <> approvalId)
    notifyListeners ()
    postDecision approvalId decision |> Async.StartImmediate

// ─── Dialog ──────────────────────────────────────────────────────

let private requestSubtitle (request: PendingApprovalRequest) =
    if String.IsNullOrWhiteSpace request.SourceModule then
        request.ToolName
    else
        $"{request.ToolName} · {request.SourceModule}"

/// The approval modal. Renders the OLDEST held invocation — one at a
/// time, so a parallel tool batch asks its questions in the order they
/// were raised rather than stacking overlays.
///
/// Mount it once per tab. `ConversationPanel.View` does so for the SDK's
/// composed shell, in both its open and collapsed branches: a held
/// invocation belongs to the conversation, not to whether the user
/// happens to have the panel expanded.
///
/// **Two buttons and no third.** There is no "always allow this tool":
/// the user is approving arguments they can see, and a standing
/// allowance would authorise arguments nobody has read yet.
[<ReactComponent>]
let View () =
    let queue, setQueue = React.useState<PendingApprovalRequest list> []

    React.useEffectOnce (fun () ->
        let unsubscribe = subscribe setQueue
        FsReact.createDisposable (fun () -> unsubscribe ()))

    match queue with
    | [] -> Html.none
    | request :: _ ->
        Html.div [
            prop.className "fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4"
            prop.role "dialog"
            ariaProp.modal true
            prop.ariaLabel "AI action approval request"
            prop.children [
                Html.div [
                    prop.className "w-full max-w-md rounded-lg bg-white shadow-xl border border-amber-300"
                    prop.children [
                        Html.div [
                            prop.className "px-5 py-4 border-b border-gray-200"
                            prop.children [
                                Html.h2 [
                                    prop.className "text-sm font-semibold text-gray-800"
                                    prop.text (
                                        if String.IsNullOrWhiteSpace request.Summary then
                                            $"Approve running '{request.ToolName}'?"
                                        else
                                            request.Summary
                                    )
                                ]
                            ]
                        ]

                        Html.div [
                            prop.className "px-5 py-4 space-y-3"
                            prop.children [
                                if not (String.IsNullOrWhiteSpace request.Detail) then
                                    Html.p [ prop.className "text-sm text-gray-700"; prop.text request.Detail ]

                                Html.p [
                                    prop.className "text-xs text-gray-500 font-mono break-all"
                                    prop.text (requestSubtitle request)
                                ]

                                if not (String.IsNullOrWhiteSpace request.RedactedArgumentsPreview) then
                                    Html.pre [
                                        prop.className
                                            "text-xs text-gray-600 bg-gray-50 border border-gray-200 rounded p-2 whitespace-pre-wrap break-all max-h-32 overflow-y-auto"
                                        prop.text request.RedactedArgumentsPreview
                                    ]

                                if List.length queue > 1 then
                                    Html.p [
                                        prop.className "text-xs text-gray-500"
                                        prop.text
                                            $"{List.length queue - 1} further action(s) are waiting on your answer."
                                    ]
                            ]
                        ]

                        Html.div [
                            prop.className "px-5 py-4 border-t border-gray-200 flex flex-wrap justify-end gap-2"
                            prop.children [
                                Html.button [
                                    prop.className
                                        "px-3 py-1.5 text-sm rounded border border-gray-300 text-gray-700 hover:bg-gray-50"
                                    prop.text "Don't run it"
                                    prop.onClick (fun _ -> answer request.ApprovalId Rejected)
                                ]
                                Html.button [
                                    prop.className
                                        "px-3 py-1.5 text-sm rounded bg-amber-600 text-white hover:bg-amber-700"
                                    prop.text "Approve"
                                    prop.onClick (fun _ -> answer request.ApprovalId Approved)
                                ]
                            ]
                        ]
                    ]
                ]
            ]
        ]