// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Generic

// ─── Phase 266 — extensible host-capability registry (authorizer-gated) ──
//
// Phase 110 shipped a fixed FOUR host capabilities a hosted typed-tree
// routes its actions through (`Navigate` / `Call` / `Notify` / `Dispatch`).
// Everything else a tree language needs — clipboard-write, file-read,
// set-state, and any consumer-registered capability — is today delegated to
// the *tree language's own* browser runtime, which bypasses forge substrate
// AND the Phase 113 action authorizer. That is the seam this closes.
//
// `IHostCapabilityRegistry` is a neutral, additive registry of NAMED
// capabilities a host wires once and a tree invokes as
// `Invoke(capabilityId, args)`. **Every invoke is gated by
// `IActionAuthorizer`** (default-deny when no rule matches) so a hosted
// action can no longer reach a side-effect without passing the tenant/tier
// gate the rest of the platform enforces by construction. An unregistered
// id denies; an empty registry denies everything; a pipeline that registers
// nothing pays nothing (GP 13).
//
// **Six portability rules** (GP 12):
//   1. Identity by value — `CapabilityId` wraps a string; `HostCapabilityArgs`
//      is a `Map<string,string>`; `AccessContext` is a record of values. ✓
//   2. Async at every boundary — `Invoke` and every handler return `Async<_>`. ✓
//   3. Retry/supervision as data — N/A (a point invoke; a distributed impl
//      wraps its own policy around `Register`/`Invoke`). ✓
//   4. Stateless handlers — a handler receives all state (args + ctx) per
//      call; registration is a compose-time concern, invocation holds no
//      per-call state between invokes. ✓
//   5. No cross-call ordering promises — each `Invoke` is a point decision. ✓
//   6. Precision — N/A (no timing primitive). ✓
//
// The registry is renderer-neutral: a capability id is an opaque string the
// host owns, and args cross the boundary as a stringly-typed prop bag — the
// same erasure seam the `NarrativeElement.Component` renderer uses — so no
// tree-language payload type reaches forge.

/// Identity-by-value name of a host capability (GP 12 rule 1). An opaque
/// string the host owns; forge never interprets it beyond routing.
type CapabilityId =
    | CapabilityId of string

    /// The underlying string id.
    member this.Value =
        let (CapabilityId s) = this
        s

[<RequireQualifiedAccess>]
module CapabilityId =
    /// The underlying string id.
    let value (CapabilityId s) : string = s

    /// Wrap a raw string as a `CapabilityId`.
    let ofString (s: string) : CapabilityId = CapabilityId s

/// Args a tree passes to an invoked capability, as data (GP 12 rule 1). A
/// neutral, stringly-typed prop bag — no typed tree-language payload crosses
/// the host boundary. Reserved keys are namespaced under `toolup.` (see
/// `HostCapabilityRegistry.TargetScopeArg`).
type HostCapabilityArgs = Map<string, string>

/// Outcome of an `Invoke`. `Completed` carries the handler's neutral result
/// bag; `Denied` carries the reason. EVERY non-allow path — unauthorized,
/// unregistered id, empty registry — is a `Denied` (default-deny), never an
/// exception.
[<RequireQualifiedAccess>]
type HostCapabilityOutcome =
    /// The authorizer granted the action and a registered handler ran.
    | Completed of result: HostCapabilityArgs
    /// The action was refused (authorizer deny, unregistered id, or an
    /// empty registry). `reason` is the authorizer's or the registry's
    /// human-readable explanation.
    | Denied of reason: string

/// A registered capability handler. Stateless between invocations (GP 12
/// rule 4): receives its args + the caller's `AccessContext` per call and
/// holds no per-call state. Async at the boundary (rule 2). The handler runs
/// ONLY after the authorizer has granted the action, so it never re-checks
/// authorization — it does the side-effect and returns a neutral result bag.
type HostCapabilityHandler = HostCapabilityArgs -> AccessContext -> Async<HostCapabilityArgs>

/// Neutral, additive registry of named host capabilities a hosted typed-tree
/// can request BEYOND the fixed four (Phase 110). A host wires it once at
/// compose time (`Register`); a tree invokes a capability by id (`Invoke`),
/// and every invoke routes through the Phase 113 `IActionAuthorizer`
/// default-deny gate. Absent registration is NOT "allow" — an empty registry
/// denies every id.
type IHostCapabilityRegistry =
    /// Register a handler under a capability id. Compose-time; the last
    /// registration for an id wins. Registering nothing leaves the registry
    /// deny-all (GP 13).
    abstract Register: capability: CapabilityId -> handler: HostCapabilityHandler -> unit

    /// Invoke a named capability on behalf of the caller. Maps the capability
    /// to a neutral `ActionDescriptor`, routes it through the authorizer
    /// (default-deny), and runs the registered handler ONLY on `Allow` AND
    /// when a handler is registered for the id — otherwise returns
    /// `HostCapabilityOutcome.Denied`.
    abstract Invoke:
        capability: CapabilityId -> args: HostCapabilityArgs -> ctx: AccessContext -> Async<HostCapabilityOutcome>

[<RequireQualifiedAccess>]
module HostCapabilityRegistry =

    /// The `ActionDescriptor.Kind` every capability invoke maps to. A
    /// deployment's `ActionPolicy` gates invokes with rules on this kind
    /// (`{ Kind = "invoke"; Target = "<capabilityId>"; ... }`).
    [<Literal>]
    let InvokeActionKind = "invoke"

    /// Reserved `HostCapabilityArgs` key naming the scope an invoke targets.
    /// Absent ⇒ the action targets the caller's OWN resolved scope
    /// (`ctx.TeamId`); present ⇒ that scope, and the authorizer denies a
    /// cross-scope target structurally (GP 4).
    [<Literal>]
    let TargetScopeArg = "toolup.targetScope"

    /// Map a capability + args + caller context to the neutral
    /// `ActionDescriptor` the Phase 113 authorizer gates. `Kind = "invoke"`,
    /// `Target` = the capability id, `Scope` = the reserved target-scope arg
    /// when present else the caller's own scope.
    let toActionDescriptor
        (capability: CapabilityId)
        (args: HostCapabilityArgs)
        (ctx: AccessContext)
        : ActionDescriptor =
        {
            Kind = InvokeActionKind
            Target = CapabilityId.value capability
            Scope = args |> Map.tryFind TargetScopeArg |> Option.orElseWith (fun () -> ctx.TeamId)
        }

    /// Build the shipped authorizer-gated registry over an `IActionAuthorizer`.
    /// Every `Invoke` maps to an `ActionDescriptor` and routes through
    /// `authorizer` (default-deny); a handler runs ONLY on `Allow` AND when a
    /// capability is registered for the id. An empty registry (nothing
    /// registered) denies everything (GP 13 — zero cost, deny-all).
    ///
    /// Registration is a compose-time concern (single-threaded startup), so
    /// the backing map is a plain `Dictionary` with no runtime lock — the
    /// same shape the SDK's other name-keyed registration surfaces use.
    let create (authorizer: IActionAuthorizer) : IHostCapabilityRegistry =
        let handlers = Dictionary<string, HostCapabilityHandler>()

        { new IHostCapabilityRegistry with
            member _.Register capability handler =
                handlers[CapabilityId.value capability] <- handler

            member _.Invoke capability args ctx = async {
                let descriptor = toActionDescriptor capability args ctx
                let! decision = authorizer.Authorize descriptor ctx

                match decision with
                | AuthorizationDecision.Deny reason -> return HostCapabilityOutcome.Denied reason
                | AuthorizationDecision.Allow ->
                    match handlers.TryGetValue(CapabilityId.value capability) with
                    | true, handler ->
                        let! result = handler args ctx
                        return HostCapabilityOutcome.Completed result
                    | false, _ ->
                        return
                            HostCapabilityOutcome.Denied(
                                sprintf
                                    "no capability registered for id '%s' (default-deny)"
                                    (CapabilityId.value capability)
                            )
            }
        }

/// Forge-public built-in capabilities. Each is OPT-IN — a host registers it
/// explicitly, so a registry that never registers it denies the id
/// (default-deny). Shipping them here means a hosted tree reaches
/// clipboard-write / file-read through forge substrate + the authorizer,
/// not a tree-language runtime back door. The raw side-effect (browser
/// clipboard / file read) is INJECTED by the host tier so this module stays
/// neutral and Fable-safe; only the args/result shaping is shipped.
[<RequireQualifiedAccess>]
module HostCapability =

    /// Args key carrying the text a `ClipboardWrite` invoke writes.
    [<Literal>]
    let ClipboardTextArg = "text"

    /// Args key carrying the path/id a `FileRead` invoke reads; result key
    /// carrying the read content.
    [<Literal>]
    let FilePathArg = "path"

    /// Result key carrying the content a `FileRead` invoke returns.
    [<Literal>]
    let FileContentArg = "content"

    /// Built-in: write text to the host clipboard. Invoke with
    /// `Map [ "text", "<value>" ]`.
    let ClipboardWrite: CapabilityId = CapabilityId "host.clipboard.write"

    /// Built-in: read a named host file / resource. Invoke with
    /// `Map [ "path", "<id>" ]`; the result carries `"content"`.
    let FileRead: CapabilityId = CapabilityId "host.file.read"

    /// Register the clipboard-write built-in, delegating the actual write to
    /// `writeText` (the host tier supplies the browser effect; this module
    /// stays neutral + Fable-safe). Opt-in — a registry without this
    /// registered denies `ClipboardWrite` (GP 13).
    let registerClipboardWrite (writeText: string -> Async<unit>) (registry: IHostCapabilityRegistry) : unit =
        registry.Register ClipboardWrite (fun args _ctx -> async {
            let text = args |> Map.tryFind ClipboardTextArg |> Option.defaultValue ""
            do! writeText text
            return Map.empty
        })

    /// Register the file-read built-in, delegating the actual read to
    /// `readFile`. Opt-in — a registry without this registered denies
    /// `FileRead` (GP 13).
    let registerFileRead (readFile: string -> Async<string>) (registry: IHostCapabilityRegistry) : unit =
        registry.Register FileRead (fun args _ctx -> async {
            let path = args |> Map.tryFind FilePathArg |> Option.defaultValue ""
            let! content = readFile path
            return Map [ FileContentArg, content ]
        })