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

// ─── Phase 266 — additive Invoke hook for capabilities beyond the four ──
//
// The four `ClientHostCapabilities` are fixed. A tree language that needs a
// capability BEYOND them — clipboard-write, file-read, a consumer-registered
// capability — routes it through `ClientHostInvoke`, whose every call is
// gated by the Phase 266 `IHostCapabilityRegistry` (an `IActionAuthorizer`
// default-deny gate). This is a SEPARATE surface from `ClientHostCapabilities`
// (whose four-member shape every existing binding implements as an object
// expression) so the existing bag + every `withElementView` caller compile
// byte-for-byte unchanged (GP 11). A pipeline that never constructs a
// registry pays nothing (GP 13).

/// The additive Invoke hook a hosted tree uses for named host capabilities
/// beyond the fixed four. A thin, renderer-neutral view over the composed
/// `IHostCapabilityRegistry` — every `Invoke` routes through the registry's
/// authorizer (default-deny); an unregistered id or an unauthorized call
/// yields `HostCapabilityOutcome.Denied`.
type ClientHostInvoke =
    abstract Invoke:
        capability: CapabilityId -> args: HostCapabilityArgs -> ctx: AccessContext -> Async<HostCapabilityOutcome>

[<RequireQualifiedAccess>]
module ClientHostInvoke =

    /// Build the Invoke hook from a composed registry. The bridge is a pure
    /// passthrough — the registry owns the authorizer gate — so a hosted tree
    /// gets the same default-deny semantics the rest of the platform enforces.
    let create (registry: IHostCapabilityRegistry) : ClientHostInvoke =
        { new ClientHostInvoke with
            member _.Invoke capability args ctx = registry.Invoke capability args ctx
        }

[<RequireQualifiedAccess>]
module ClientHostInvokeView =

    /// `ClientHostView.withElementView`-shaped builder whose view ALSO
    /// receives a `ClientHostInvoke` constructed from the composed registry,
    /// so a hosted tree gets the four built-in hooks PLUS the authorizer-gated
    /// `Invoke` for capabilities beyond them. Existing `withView` /
    /// `withFullWidthView` / `withPages` / `withElementView` callers are
    /// untouched (GP 11):
    ///
    /// ```
    /// ClientModule.create spec
    /// |> ClientHostInvokeView.withInvokableElementView registry (fun model dispatch host invoke ->
    ///     MyTreeRuntime.render (view model) host invoke)
    /// |> ClientModule.register
    /// ```
    let withInvokableElementView
        (registry: IHostCapabilityRegistry)
        (view: 'Model -> ('Msg -> unit) -> ClientHostCapabilities<'Msg> -> ClientHostInvoke -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        ClientModule.withFullWidthView
            (fun model dispatch ->
                view model dispatch (ClientHostCapabilities.create dispatch) (ClientHostInvoke.create registry))
            m

// ─── Phase 270 — hosted-tree capability/version negotiation gate ──────
//
// The host runtime (Phase 110) and an external tree language version
// independently; nothing checked compatibility, so a tree that needs a newer
// capability (Phase 266's `Invoke`, or a binding shape the host predates)
// rendered BROKEN — a cryptic partial render with a console warning. This is
// a lightweight handshake: the host advertises a `HostCapabilitySet`
// version + supported ids, the tree declares its minimum required set, and a
// mismatch raises a STRUCTURED error at mount naming the missing capability —
// not a console warning after a half-render. It complements Phase 203's
// STRUCTURAL (SSR↔CSR) parity check with a CAPABILITY compatibility check.

/// A version + the set of capability ids in scope. For the HOST: the version
/// it advertises + the capability ids it implements (the four built-ins plus
/// any Phase 266 registered ids). For a TREE's required set: the minimum
/// version + the ids it needs. Neutral — capability ids are opaque strings.
type HostCapabilitySet = {
    /// Host-capability protocol version. A tree that requires a higher version
    /// than the host advertises fails negotiation.
    Version: int
    /// The capability ids in the set (host: implemented; tree: required).
    Capabilities: Set<string>
}

/// Why a capability negotiation failed. Structured — a mount-time error names
/// the gap, never a post-render console warning.
[<RequireQualifiedAccess>]
type HostCapabilityMismatch =
    /// The tree requires `required` but the host advertises `hostVersion`
    /// (below the minimum the tree needs).
    | VersionTooLow of required: int * hostVersion: int
    /// The tree requires capability ids the host does not implement. Sorted
    /// for a stable, greppable message.
    | MissingCapabilities of missing: string list

[<RequireQualifiedAccess>]
module HostCapabilityMismatch =
    /// Human-readable description of the gap — what a mount-time error state
    /// renders and what the Phase 268 telemetry sink records.
    let describe (mismatch: HostCapabilityMismatch) : string =
        match mismatch with
        | HostCapabilityMismatch.VersionTooLow(required, hostVersion) ->
            sprintf "hosted tree requires host-capability version %d but the host advertises %d" required hostVersion
        | HostCapabilityMismatch.MissingCapabilities missing ->
            sprintf "host does not implement required capabilities: %s" (String.concat ", " missing)

[<RequireQualifiedAccess>]
module HostCapabilitySet =

    /// The four Phase 110 built-in capability ids (the `ActionDescriptor.Kind`
    /// vocabulary). The default required set a hosted view declares when it
    /// names none — always satisfied by any host, so an existing
    /// `withElementView` caller is byte-for-byte unchanged (GP 11).
    let builtInCapabilities: Set<string> =
        Set.ofList [ "navigate"; "call"; "notify"; "dispatch" ]

    /// The version the current host protocol advertises. Bump when the
    /// host-capability surface gains a breaking shape.
    [<Literal>]
    let CurrentVersion = 1

    /// The default required set — the four built-ins at the current version. A
    /// hosted view that declares nothing negotiates this and always mounts.
    let defaultRequired: HostCapabilitySet = {
        Version = CurrentVersion
        Capabilities = builtInCapabilities
    }

    /// A host set advertising `CurrentVersion` + the four built-ins + the given
    /// Phase 266 registered capability ids.
    let host (registered: CapabilityId seq) : HostCapabilitySet = {
        Version = CurrentVersion
        Capabilities =
            registered
            |> Seq.map CapabilityId.value
            |> Set.ofSeq
            |> Set.union builtInCapabilities
    }

    /// A tree's required set: the minimum version + the capability ids it
    /// needs (the four built-ins are always implicitly required).
    let requires (version: int) (capabilities: CapabilityId seq) : HostCapabilitySet = {
        Version = version
        Capabilities =
            capabilities
            |> Seq.map CapabilityId.value
            |> Set.ofSeq
            |> Set.union builtInCapabilities
    }

[<RequireQualifiedAccess>]
module HostCapabilityNegotiation =

    /// Check a tree's `required` set against the `host`'s advertised set.
    /// Returns `Ok ()` when the host meets or exceeds the required version AND
    /// implements every required capability; otherwise a structured
    /// `HostCapabilityMismatch` naming the gap (never a thrown console
    /// warning). Called at mount.
    let negotiate (required: HostCapabilitySet) (host: HostCapabilitySet) : Result<unit, HostCapabilityMismatch> =
        if host.Version < required.Version then
            Error(HostCapabilityMismatch.VersionTooLow(required.Version, host.Version))
        else
            let missing = Set.difference required.Capabilities host.Capabilities

            if Set.isEmpty missing then
                Ok()
            else
                Error(HostCapabilityMismatch.MissingCapabilities(missing |> Set.toList |> List.sort))

[<RequireQualifiedAccess>]
module ClientHostNegotiatedView =

    /// The structured mount-time error element (not a console warning) a
    /// hosted view renders when negotiation fails.
    let private mismatchElement (mismatch: HostCapabilityMismatch) : ReactElement =
        Html.div [
            prop.className "toolup-host-capability-mismatch"
            prop.children [
                Html.strong [ prop.text "Hosted view unavailable" ]
                Html.p [ prop.text (HostCapabilityMismatch.describe mismatch) ]
            ]
        ]

    /// `ClientHostView.withElementView` with a mount-time capability handshake.
    /// Negotiates the tree's `required` set against the `host`'s advertised set
    /// once at mount: on success the tree renders as usual; on a mismatch it
    /// renders a STRUCTURED error state (never a silent partial render) and
    /// reports the gap through `onMismatch` (wire the Phase 268 render-failure
    /// telemetry sink here; pass `ignore` when none is composed). Existing
    /// `withElementView` callers that declare no required set stay on the
    /// four-built-in default and always mount (GP 11).
    let withNegotiatedElementView
        (required: HostCapabilitySet)
        (host: HostCapabilitySet)
        (onMismatch: HostCapabilityMismatch -> unit)
        (view: 'Model -> ('Msg -> unit) -> ClientHostCapabilities<'Msg> -> ReactElement)
        (m: ClientModule<'Model, 'Msg>)
        : ClientModule<'Model, 'Msg> =
        match HostCapabilityNegotiation.negotiate required host with
        | Ok() -> ClientHostView.withElementView view m
        | Error mismatch ->
            onMismatch mismatch
            ClientModule.withFullWidthView (fun _ _ -> mismatchElement mismatch) m