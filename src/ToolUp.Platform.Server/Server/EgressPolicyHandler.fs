// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Generic
open System.Net.Http
open System.Threading
open System.Threading.Tasks

// ─── Phase 772 — server-side egress policy: the enforcing half ───────
//
// `EgressPolicyTypes.fs` (Core) holds the decision; this file holds
// everything that makes the decision bind: the `DelegatingHandler` that
// asks it before every outbound request and refuses with a typed,
// audited denial; the ambient calling-component carrier; the
// process-wide installation seam a composition root sets its policy on;
// and `PlatformHttpClient`, the one construction path every in-tree
// client is migrated onto.
//
// **Why a process-wide install rather than a container registration.**
// The in-tree clients this seam must cover are built from module-level
// `lazy (new HttpClient())` values inside companion projects — the AI
// providers, the auth validators, the notification sinks — at a point
// where no `IServiceProvider` exists and none is reachable (the Phase 70
// factory builds a fresh provider per `Resolve`; the config validators
// run before the container is built). A seam those clients can reach has
// to be ambient, exactly as `ConfigResolution.install` is for the
// configuration layer. The named `IHttpClientFactory` client registered
// by `ComposeRuntimeServices.registerPlatformHttpClient` consults the
// SAME installed binding, so a DI consumer and a static one answer to
// one policy rather than two.
//
// Compile position: after `DeploymentVerificationReport.fs` (this file
// PRODUCES the report's `EgressIntegrity`) and before
// `Compose/ComposeRuntimeServices.fs` (which registers the handler) —
// the same tier-order constraint `SeamAuthorityEnforcement.fs` records
// for its own report projection.

/// An outbound call the egress policy refused. Raised by the handler
/// BEFORE any socket opens, so a refused request never leaves the
/// process; carries the decision's three coordinates and the policy's
/// reason. The message is the reason, which names the ORIGIN and never
/// the URL.
type EgressDeniedException
    (component: ComponentId, destination: EgressDestination, surface: EgressSurface, reason: string) =
    inherit Exception(reason)

    /// The component the call was attributed to.
    member _.Component = component
    /// The refused destination — origin only.
    member _.Destination = destination
    /// The surface the client was created for.
    member _.Surface = surface
    /// The policy's own sentence.
    member _.Reason = reason

/// The ambient calling-component carrier — the same `AsyncLocal` shape
/// `FactPurposeContext` uses for the claimed purpose. A module's handler
/// claims its `ComponentId` for the async chain it runs on; the egress
/// handler reads it at decision time. Unclaimed chains — platform-owned
/// work, a companion resolving its own client — are attributed to
/// `EgressPolicy.platformComponent`, so a decision always has a subject.
[<RequireQualifiedAccess>]
module EgressComponentContext =

    let private claimed = AsyncLocal<ComponentId option>()

    /// The component the current async chain claims, if any.
    let tryCurrent () : ComponentId option =
        match claimed.Value with
        | Some c -> Some c
        | _ -> None

    /// The component outbound calls on the current chain are attributed
    /// to — the claimed one, or the platform.
    let current () : ComponentId =
        tryCurrent () |> Option.defaultValue EgressPolicy.platformComponent

    /// Claim a component for the current async chain (flows into child
    /// asyncs, never into siblings or parents).
    let claim (componentId: ComponentId) : unit = claimed.Value <- Some componentId

    /// Clear the current chain's claim.
    let clear () : unit = claimed.Value <- None

    /// Run `body` with `componentId` claimed, restoring the prior claim
    /// afterwards — the shape a module handler wraps its outbound work in.
    let runAs (componentId: ComponentId) (body: unit -> Async<'T>) : Async<'T> = async {
        let prior = claimed.Value
        claimed.Value <- Some componentId

        try
            return! body ()
        finally
            claimed.Value <- prior
    }

/// Phase 796 — the ambient payload-label carrier. The same `AsyncLocal`
/// shape `EgressComponentContext` above uses, and for the same reason: an
/// `HttpRequestMessage` has nowhere to carry a disclosure label, and the
/// surfaces that KNOW a payload's lineage (a report render, a
/// notification dispatch, a webhook emission, a fact export) are several
/// frames above the handler that decides.
///
/// A surface that has computed a label claims it for the async chain its
/// outbound work runs on — `runLabelled`, the shape a handler wraps its
/// emission in — and the egress handler reads it at decision time. An
/// unclaimed chain reads `Unlabelled`, which is exactly true of it:
/// nobody computed a lineage for whatever is about to leave. Under the
/// permit-all default and under an advisory grant signature that costs
/// nothing (GP 11); under a profile that makes declaration mandatory it
/// is the refusal `EgressPolicy.requireClearedLabel` raises.
[<RequireQualifiedAccess>]
module EgressLabelContext =

    let private claimed = AsyncLocal<EgressLabel option>()

    /// The label the current async chain claims, if any.
    let tryCurrent () : EgressLabel option =
        match claimed.Value with
        | Some label -> Some label
        | _ -> None

    /// The label outbound calls on the current chain carry — the claimed
    /// one, or `Unlabelled` for a chain nobody labelled.
    let current () : EgressLabel =
        tryCurrent () |> Option.defaultValue EgressLabel.unlabelled

    /// Claim a label for the current async chain (flows into child
    /// asyncs, never into siblings or parents).
    let claim (label: EgressLabel) : unit = claimed.Value <- Some label

    /// Clear the current chain's claim, returning it to `Unlabelled`.
    let clear () : unit = claimed.Value <- None

    /// Claim the JOIN of `label` with whatever the chain already carries,
    /// so a payload assembled from several labelled sources ends up
    /// carrying all of them. `Unlabelled` is absorbing in that join
    /// (`EgressLabel.join` says why), so folding in an uncomputed source
    /// makes the whole chain uncomputed rather than silently dropping it.
    let joinIn (label: EgressLabel) : unit =
        claimed.Value <- Some(EgressLabel.join (current ()) label)

    /// Run `body` with `label` claimed, restoring the prior claim
    /// afterwards — the shape an emitting surface wraps its outbound work
    /// in.
    let runLabelled (label: EgressLabel) (body: unit -> Async<'T>) : Async<'T> = async {
        let prior = claimed.Value
        claimed.Value <- Some label

        try
            return! body ()
        finally
            claimed.Value <- prior
    }

/// The installed policy and everything a denial leaves behind: the
/// bounded since-boot ledger the deployment verification report reads,
/// and the `EgressDenied` audit row.
[<RequireQualifiedAccess>]
module EgressEnforcement =

    /// Scope every `EgressDenied` row is recorded under — the reserved
    /// cross-tenant platform scope, as the field-classification egress
    /// gate uses. A denial happens outside any request's config scope;
    /// the payload's component carries attribution.
    [<Literal>]
    let AuditScope = "_platform"

    /// How many refusals the in-process ledger retains. The since-boot
    /// COUNT is kept separately and never truncates, so a long-running
    /// deployment cannot present as quiet by outliving its own ledger.
    [<Literal>]
    let DenialRetention = 200

    // Module-level mutable state, justified: the binding must be
    // reachable from clients built with no container in hand (header
    // note), and a denial ledger is by nature process-wide. All writes
    // go through `gate`; reads of `binding` are a single reference read.
    let private gate = obj ()
    let mutable private binding: EgressPolicyBinding = EgressPolicy.permitAllBinding
    let mutable private auditLog: IAuditLog option = None
    let private denials = Queue<EgressDenialRecord>()
    let mutable private denialsSinceBoot = 0

    /// Install the policy every platform client consults from now on. A
    /// composition root calls this once, before or during composition,
    /// with `EgressPolicy.bind`'s result; nothing installed means the
    /// permit-all default (GP 11). Later installs replace earlier ones —
    /// the last word is the composition's.
    let install (policyBinding: EgressPolicyBinding) : unit =
        lock gate (fun () -> binding <- policyBinding)

    /// The binding in force.
    let current () : EgressPolicyBinding = binding

    /// Bind the audit log denials are recorded to. Called by
    /// `ComposeRuntimeServices.registerPlatformHttpClient` with the
    /// composition's `IAuditLog`; until then a denial is ledgered and
    /// raised but not audited.
    let bindAudit (log: IAuditLog) : unit =
        lock gate (fun () -> auditLog <- Some log)

    /// Every refusal since boot, retained or not.
    let denialCount () : int = lock gate (fun () -> denialsSinceBoot)

    /// The retained refusals, oldest first.
    let recentDenials () : EgressDenialRecord list =
        lock gate (fun () -> denials |> List.ofSeq)

    /// Return to the uninstalled state: permit-all, no audit log, an empty
    /// ledger. A test seam — a process that composed once has no reason
    /// to call it.
    let reset () : unit =
        lock gate (fun () ->
            binding <- EgressPolicy.permitAllBinding
            auditLog <- None
            denials.Clear()
            denialsSinceBoot <- 0)

    let private ledger (record: EgressDenialRecord) : IAuditLog option =
        lock gate (fun () ->
            denials.Enqueue record
            denialsSinceBoot <- denialsSinceBoot + 1

            while denials.Count > DenialRetention do
                denials.Dequeue() |> ignore

            auditLog)

    /// Ask the installed policy about one outbound call on behalf of the
    /// ambient component, and on a deny leave the trail: one ledger entry,
    /// one `EgressDenied` audit row (best-effort, per `IAuditLog.Record`'s
    /// contract), and the typed exception for the caller to raise. Nothing
    /// here opens a connection.
    let check (destination: EgressDestination) (surface: EgressSurface) : Async<Result<unit, EgressDeniedException>> = async {
        let componentId = EgressComponentContext.current ()
        let label = EgressLabelContext.current ()

        match binding.Policy.Decide(componentId, destination, surface, label) with
        | EgressVerdict.Permit -> return Ok()
        | EgressVerdict.Deny reason ->
            let origin = EgressDestination.origin destination
            let surfaceLabel = EgressSurface.label surface

            let log =
                ledger {
                    DeniedComponent = componentId
                    DeniedOrigin = origin
                    DeniedSurface = surfaceLabel
                    DeniedLabel = EgressLabel.render label
                    DeniedReason = reason
                }

            match log with
            | None -> ()
            | Some log ->
                try
                    do!
                        log.Record(
                            AuditScope,
                            AuditEvent.EgressDenied {
                                Component = ComponentId.value componentId
                                Origin = origin
                                Surface = surfaceLabel
                                Reason = reason
                            }
                        )
                with _ ->
                    // Best-effort by contract; the denial itself still
                    // stands and is still ledgered.
                    ()

            return Error(EgressDeniedException(componentId, destination, surface, reason))
    }

    /// Project the installed binding and the ledger onto the deployment
    /// verification report's tier-neutral mirror. `profileLabel` is
    /// `CompositionProfile.label` — passed as a value because the report
    /// compiles before the profile and this file compiles between them.
    /// Everything else is DERIVED from what is installed: a root cannot
    /// overstate its coverage by passing a flattering number.
    let deploymentVerificationEvidence (profileLabel: string) : EgressIntegrity =
        let installed = current ()

        {
            EgressProfile = profileLabel
            EgressPosture = installed.Posture
            EgressComponents =
                installed.Grants
                |> Map.toList
                |> List.map (fun (componentId, grant) -> {
                    EgressComponent = componentId
                    EgressDeclaredGrant = grant
                })
                |> List.sortWith (fun a b ->
                    String.CompareOrdinal(ComponentId.value a.EgressComponent, ComponentId.value b.EgressComponent))
            EgressDenials = recentDenials ()
            EgressDenialsSinceBoot = denialCount ()
            EgressLabelVocabulary = DisclosurePolicyRefSnapshot.Version
        }

/// The handler in front of every platform-issued client. Consults the
/// installed policy with the ambient component and the surface it was
/// constructed for; a permit forwards the request untouched (byte-for-
/// byte — no header, no rewrite), a deny raises `EgressDeniedException`
/// before the inner handler is reached, so the request never leaves the
/// process and no partial response exists.
///
/// A request whose URI is not absolute is forwarded as-is: `HttpClient`
/// has already applied `BaseAddress` by the time a handler sees it, so
/// the only way to arrive here relative is with no base at all, and the
/// inner handler's own refusal of that is the behaviour a caller had
/// before this handler existed.
type EgressPolicyHandler(surface: EgressSurface) =
    inherit DelegatingHandler()

    /// The `EgressSurface.Other` handler — for the named
    /// `IHttpClientFactory` client and any caller with no better label.
    /// An explicit secondary constructor rather than an optional
    /// argument, which the public-API gate would read as a removal.
    new() = EgressPolicyHandler(EgressSurface.Other)

    /// The surface every request through this handler is attributed to.
    member _.Surface = surface

    static member private DestinationOf(request: HttpRequestMessage) : EgressDestination option =
        match request.RequestUri with
        | null -> None
        | uri when not uri.IsAbsoluteUri -> None
        | uri -> EgressDestination.ofParts uri.Scheme uri.Host (if uri.IsDefaultPort then None else Some uri.Port)

    // `base` cannot be named inside the task builder's closure, so the
    // forward is a member of its own.
    member private _.Forward
        (request: HttpRequestMessage, cancellationToken: CancellationToken)
        : Task<HttpResponseMessage> =
        base.SendAsync(request, cancellationToken)

    override this.SendAsync
        (request: HttpRequestMessage, cancellationToken: CancellationToken)
        : Task<HttpResponseMessage> =
        match EgressPolicyHandler.DestinationOf request with
        | None -> this.Forward(request, cancellationToken)
        | Some destination -> task {
            let! verdict = EgressEnforcement.check destination surface |> Async.StartAsTask

            match verdict with
            | Ok() -> return! this.Forward(request, cancellationToken)
            | Error denied -> return raise denied
          }

    override this.Send(request: HttpRequestMessage, cancellationToken: CancellationToken) : HttpResponseMessage =
        match EgressPolicyHandler.DestinationOf request with
        | None -> base.Send(request, cancellationToken)
        | Some destination ->
            match EgressEnforcement.check destination surface |> Async.RunSynchronously with
            | Ok() -> base.Send(request, cancellationToken)
            | Error denied -> raise denied

/// The platform client factory — the ONE construction path for an
/// outbound `HttpClient` in this codebase. Every `create` puts an
/// `EgressPolicyHandler` in front of exactly the primary handler
/// `new HttpClient()` would have used, so under the permit-all default a
/// client from here behaves byte-for-byte as a hand-constructed one: same
/// connection pool per client, same defaults, one extra pass-through hop.
///
/// The honest limit, stated where a reader will meet it: a client
/// constructed by hand anywhere else never meets the handler. In this
/// tree that is a documented hatch; making it a compile-time finding is
/// Phase 776's analyser rule.
[<RequireQualifiedAccess>]
module PlatformHttpClient =

    /// The named `IHttpClientFactory` client `registerPlatformHttpClient`
    /// registers — `factory.CreateClient PlatformHttpClient.Name` from any
    /// DI consumer, attributed to the `ModuleHandler` surface.
    [<Literal>]
    let Name = "toolup-platform"

    /// Put the egress handler in front of a caller-supplied primary
    /// handler — for a client that needs its own transport (a Docker
    /// socket, a redirect-refusing `HttpClientHandler`, a test stub).
    let wrap (surface: EgressSurface) (inner: HttpMessageHandler) : HttpMessageHandler =
        new EgressPolicyHandler(surface, InnerHandler = inner) :> HttpMessageHandler

    /// The handler chain `create` uses: the egress handler over a fresh
    /// `HttpClientHandler` — the primary `new HttpClient()` constructs.
    let handler (surface: EgressSurface) : HttpMessageHandler = wrap surface (new HttpClientHandler())

    /// A client equivalent to `new HttpClient()` with the egress handler
    /// in front. Set `BaseAddress` / `Timeout` on the result exactly as
    /// before.
    let create (surface: EgressSurface) : HttpClient = new HttpClient(handler surface)

    /// A client equivalent to `new HttpClient(inner)` with the egress
    /// handler in front of `inner`.
    let createWith (surface: EgressSurface) (inner: HttpMessageHandler) : HttpClient =
        new HttpClient(wrap surface inner)