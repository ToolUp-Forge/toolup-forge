// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ToolEffectEnvelope

open System.Threading
open ToolUp.Platform

// ─── Phase 793 — the effect envelope a running tool body is held to ──
//
// `ToolGate.decide` says whether a tool may RUN. This module says what
// it may DO while it runs: the tool's declared `ToolEffectDeclaration`
// is stamped as the ambient envelope around `tool.Execute`, and the two
// seams through which a body reaches outside the process — a host
// capability invocation (`IHostCapabilityRegistry`, Phase 110/266/300)
// and an outbound request — consult it before acting. A body that
// declared `ReadFacts` and reaches for `External "send-mail"` or
// `Egress "api.example.com"` is refused with a typed denial the caller
// gets back as a value, and the denial lands on the SAME audit stream
// the Phase 45 allowlist denials use, so `/dev/ai-allowlist`, the
// `IAIDenialRollupProbe` panel and the sustained-denial rate monitor
// all see it with no second reader.
//
// **Ambient, because tool calls run in parallel on one context.** The
// agent loop dispatches a turn's tool calls through `Async.Parallel`
// over ONE background `HttpContext`, so a stamp on `HttpContext.Items`
// would race between siblings. The envelope rides an `AsyncLocal`
// instead (GP 7 — request-scoped context flows along the async chain,
// not through every signature): a value set inside one parallel branch
// is visible to that branch's continuations and to nothing else, and
// the previous value is restored when the body completes.
//
// **The honest boundary, stated rather than implied.** The envelope
// binds what goes through the SDK's seams. An executor that constructs
// its own `HttpClient` and calls it directly is outside every check
// here, exactly as an executor that opens a socket is outside the
// Phase 300 capability gate; nothing short of process-level isolation
// closes that, and this phase does not claim to. What IS claimed, and
// tested, is that a body reaching the world through `guardInvoke` or
// `guardEgress` cannot exceed its declaration — and the verified
// composition profile makes the declaration mandatory, so under it
// every in-tree tool is bound.
//
// **Undeclared means no envelope (GP 11).** A pre-793 tool carries
// `UndeclaredEffects`; whether it may run at all is `ToolPolicy`'s
// decision (`PermitUndeclared`), and once admitted it runs as it always
// did. The envelope refuses only what a DECLARED tool did not declare.

/// A refused effect: which tool reached for what, what it had declared,
/// and a readable reason. Returned to the caller as a value and handed
/// to the envelope's observer, so a denial is never silent.
type ToolEffectDenial = {
    /// The tool whose body attempted the effect.
    ToolName: string
    /// The effect the attempted operation required.
    Required: ToolEffect
    /// What the tool declared.
    Declared: ToolEffectDeclaration
    /// Human-readable explanation naming the tool, the effect and the
    /// declaration.
    Reason: string
}

/// The envelope in force while a tool body runs.
type ActiveEnvelope = {
    /// The running tool.
    ToolName: string
    /// Its declaration, `EmitsActions` folded in.
    Effects: ToolEffectDeclaration
    /// Where a denial is reported — the agent loop supplies the
    /// allowlist-denial audit writer. Best-effort and awaited before the
    /// denial is returned; never throws into the body.
    OnDenied: ToolEffectDenial -> Async<unit>
}

/// The reserved audit source every envelope denial is written under —
/// the Phase 45 allowlist-denial stream, so the `/dev/ai-allowlist`
/// rollup reads it. The agent loop's `OnDenied` writes there; this
/// constant is what a second writer must use to land on the same
/// surface.
[<Literal>]
let DenialAuditSource = "_platform.ai.tool_allowlist_denial"

/// The event type on that stream.
[<Literal>]
let DenialEventType = "ToolAllowlistDenied"

let private ambient = AsyncLocal<ActiveEnvelope option>()

/// The envelope in force on the current async flow, if a tool body is
/// running. `None` outside every tool — a capability invocation from a
/// request handler, a job, or a test that composed no tool.
let current () : ActiveEnvelope option =
    match box ambient.Value with
    | null -> None
    | _ -> ambient.Value

/// Whether `required` sits within `declared`. An undeclared tool has no
/// envelope and is permitted everything, per GP 11; a declared tool is
/// permitted exactly the effects it listed, payload included — `Egress
/// "a"` does not license `Egress "b"`.
let permits (declared: ToolEffectDeclaration) (required: ToolEffect) : bool =
    match declared with
    | UndeclaredEffects -> true
    | DeclaredEffects effects -> Set.contains required effects

let private reason (toolName: string) (declared: ToolEffectDeclaration) (required: ToolEffect) : string =
    $"tool effect envelope: tool '{toolName}' attempted {ToolEffect.describe required}, which is outside its declared effects ({ToolEffectDeclaration.describe declared}). Declare the effect on the tool's AIToolDefinition.Effects to permit it, or the access stays refused (default-deny)."

/// Run `body` inside `envelope`, restoring whatever was in force before
/// when it completes — normally or by exception.
let runWithin (envelope: ActiveEnvelope) (body: Async<'T>) : Async<'T> = async {
    let previous = current ()
    ambient.Value <- Some envelope

    try
        return! body
    finally
        ambient.Value <- previous
}

/// **The choke point.** Require `effect` of the envelope in force.
/// `Ok` when no envelope is active or the running tool declared it;
/// otherwise the typed denial, already reported to the envelope's
/// observer. Every seam below is this function plus the seam's own call.
let require (effect: ToolEffect) : Async<Result<unit, ToolEffectDenial>> = async {
    match current () with
    | None -> return Ok()
    | Some envelope ->
        if permits envelope.Effects effect then
            return Ok()
        else
            let denial = {
                ToolName = envelope.ToolName
                Required = effect
                Declared = envelope.Effects
                Reason = reason envelope.ToolName envelope.Effects effect
            }

            try
                do! envelope.OnDenied denial
            with _ ->
                ()

            return Error denial
}

/// The host-capability seam under the envelope. A capability invocation
/// from a tool body now clears THREE gates in order: the tool's declared
/// effect envelope (this — `External capabilityId` must be declared),
/// the Phase 300 composition effect envelope, then the registry's own
/// Phase 266 default-deny authorizer, exactly as
/// `CompositionCapabilityGate.guardInvoke` orders the last two. The
/// registry is never reached when the envelope refuses.
let guardInvoke
    (gate: ICompositionCapabilityGate)
    (owner: ComponentId)
    (required: CompanionCapability)
    (registry: IHostCapabilityRegistry)
    (capability: CapabilityId)
    (args: HostCapabilityArgs)
    (ctx: AccessContext)
    : Async<HostCapabilityOutcome> =
    async {
        match! require (External(CapabilityId.value capability)) with
        | Error denial -> return HostCapabilityOutcome.Denied denial.Reason
        | Ok() -> return! CompositionCapabilityGate.guardInvoke gate owner required registry capability args ctx
    }

/// The outbound seam under the envelope. `send` runs only when the tool
/// in force declared `Egress destination`; otherwise the typed denial
/// comes back and nothing was sent. A body that makes outbound calls
/// wraps each one: `ToolEffectEnvelope.guardEgress "api.example.com"
/// (fun () -> client.GetStringAsync uri |> Async.AwaitTask)`.
let guardEgress (destination: string) (send: unit -> Async<'T>) : Async<Result<'T, ToolEffectDenial>> = async {
    match! require (Egress destination) with
    | Error denial -> return Error denial
    | Ok() ->
        let! sent = send ()
        return Ok sent
}

/// The state-write seam under the envelope, for a body that writes to a
/// named scope through its own store: `write` runs only when the tool
/// declared `WriteState scope`.
let guardWrite (scope: string) (write: unit -> Async<'T>) : Async<Result<'T, ToolEffectDenial>> = async {
    match! require (WriteState scope) with
    | Error denial -> return Error denial
    | Ok() ->
        let! written = write ()
        return Ok written
}

// ─── Composition under the verified profile ──────────────────────────

/// What a deployment declares when it composes the tool effect policy:
/// the profile the declaration is mandatory under, and the policy the
/// registry lists and dispatches under.
type ToolEffectComposition = {
    /// `Verified` refuses any registered tool that declares no effects;
    /// `Standard` admits it under the policy's `PermitUndeclared`.
    Profile: CompositionProfile
    /// The ceiling, the class-keyed approval set and the external
    /// principal ceiling.
    Policy: AIToolRegistry.ToolPolicy
}

/// The registered tools that declare nothing, by name — what the
/// verified profile refuses and names.
let undeclaredTools (tools: AIToolDefinition list) : string list =
    tools
    |> List.choose (fun def ->
        match def.Effects with
        | UndeclaredEffects -> Some def.Name
        | DeclaredEffects _ -> None)

/// Refuse a composition whose tools cannot satisfy the profile: under
/// `Verified`, every registered tool must declare its effects, because a
/// mandatory envelope with nothing to check against would admit
/// everything while presenting as enforcement. `Standard` refuses
/// nothing here — the policy's `PermitUndeclared` decides per tool at
/// list and dispatch time.
let refuseUndeclared
    (profile: CompositionProfile)
    (tools: AIToolDefinition list)
    : Result<unit, CompositionProfileRefusal> =
    match profile, undeclaredTools tools with
    | CompositionProfile.Standard, _
    | CompositionProfile.Verified, [] -> Ok()
    | CompositionProfile.Verified, undeclared -> Error(ToolEffectsUndeclared undeclared)