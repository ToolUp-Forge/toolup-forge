// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open Feliz
open ToolUp.Elmish

// ─── Phase 110 — host-neutral client tree-hosting seam ────────────────
//
// A `ClientModule` view body is already an arbitrary `ReactElement`
// (`withFullWidthView` hosts any rendered tree), so an external typed
// Node-tree UI language renders into the SDK shell today. What was
// missing is the RUNTIME seam: the named bag of host capabilities a
// tree's typed actions route through so every hosting module doesn't
// hand-roll the same wiring. `ClientHostCapabilities` re-exposes four
// shipped concretes — nothing here is new behaviour:
//
//   - `Navigate`  → `NavigationRequest.request` (shell / sidebar router)
//   - `Call`      → `Cmd.OfRemoting` semantics (interceptor chain,
//                   correlation ids, error envelopes), executed against
//                   the module's Elmish dispatch
//   - `Notify`    → `NotificationClient.publishLocal` (`ToastCentre`
//                   picks it up on its existing subscription)
//   - `Dispatch`  → the module's Elmish dispatch (the MVU loop)
//
// The surface is renderer-neutral: any external tree language binds an
// adapter from its action vocabulary onto these four hooks. A pipeline
// that never constructs the bag pays nothing (GP 13) and is
// byte-for-byte unchanged (GP 11).

/// What a host-raised toast says and how loudly. Routed through the
/// platform notification stream as a `SystemMessage`, so the shipped
/// `ToastCentre` renders it with its standard level styling and
/// dismiss behaviour.
type ToastIntent = {
    Level: SystemMessageLevel
    Text: string
}

[<RequireQualifiedAccess>]
module ToastIntent =
    let info (text: string) : ToastIntent = {
        Level = SystemMessageLevel.Info
        Text = text
    }

    let warning (text: string) : ToastIntent = {
        Level = SystemMessageLevel.Warning
        Text = text
    }

    let error (text: string) : ToastIntent = {
        Level = SystemMessageLevel.Error
        Text = text
    }

/// The four host capabilities an external UI runtime needs against
/// ToolUp client substrate. An interface (not a record) so `Call` can
/// be generic per call site in the result type. Constructed via
/// `ClientHostCapabilities.create` from the values already threaded
/// into a `ClientModule` view — no new wiring, pure re-exposure.
type ClientHostCapabilities<'Msg> =
    /// Route the shell to a module (`"ModuleId"`) or a module page
    /// (`"ModuleId/route"`) — the same sidebar-id contract
    /// `NavigationRequest.request` documents.
    abstract Navigate: sidebarId: NavigationRequest.SidebarId -> unit

    /// Raise a toast through the platform notification stream.
    abstract Notify: intent: ToastIntent -> unit

    /// Dispatch a message into the module's Elmish `update` loop.
    abstract Dispatch: msg: 'Msg -> unit

    /// Execute a remoting-shaped async call, mapping the outcome into
    /// the module's `Msg` and dispatching it — `Cmd.OfRemoting.call`
    /// semantics (interceptor chain, error envelope) without the
    /// caller owning a `Cmd` value, which an external runtime is not
    /// shaped to thread back through `update`.
    abstract Call<'r> : call: Async<'r> * onSuccess: ('r -> 'Msg) * onError: (exn -> 'Msg) -> unit

[<RequireQualifiedAccess>]
module ClientHostCapabilities =

    /// Build the capability bag from a module view's dispatch. The
    /// other three hooks are module-level shipped concretes, so
    /// dispatch is the only per-module value required.
    let create (dispatch: 'Msg -> unit) : ClientHostCapabilities<'Msg> =
        { new ClientHostCapabilities<'Msg> with
            member _.Navigate sidebarId = NavigationRequest.request sidebarId

            member _.Notify intent =
                NotificationEnvelope.create
                    (UserSession.getUserId ())
                    (Notification.SystemMessage(intent.Level, intent.Text))
                |> NotificationClient.publishLocal

            member _.Dispatch msg = dispatch msg

            member _.Call(call, onSuccess, onError) =
                // Ride the OfRemoting effect (interceptors, error
                // envelope) and execute it immediately against the
                // module's dispatch — the imperative shape an external
                // runtime's action handler needs.
                Cmd.OfRemoting.call (fun () -> call) () onSuccess onError
                |> List.iter (fun effect -> effect dispatch)
        }

/// `ClientModule.withFullWidthView`-shaped builder step whose view also
/// receives the host-capability bag, so a runtime-hosted tree gets the
/// four hooks without the consumer re-deriving them. Lives in its own
/// module (not `ClientModule`) because the builder module is sealed at
/// its definition site earlier in the compile order; the pipeline shape
/// is identical:
///
/// ```
/// ClientModule.create spec
/// |> ClientHostView.withElementView (fun model dispatch host ->
///     MyTreeRuntime.render (view model) host)
/// |> ClientModule.register
/// ```
[<RequireQualifiedAccess>]
module ClientHostView =

    /// Single-page full-width view that additionally receives a
    /// `ClientHostCapabilities` constructed from the same dispatch the
    /// view already gets. Existing `withView` / `withFullWidthView` /
    /// `withPages` callers are untouched (GP 11).
    let withElementView
        (view: 'Model -> ('Msg -> unit) -> ClientHostCapabilities<'Msg> -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ClientModule.withFullWidthView
            (fun model dispatch -> view model dispatch (ClientHostCapabilities.create dispatch))
            m