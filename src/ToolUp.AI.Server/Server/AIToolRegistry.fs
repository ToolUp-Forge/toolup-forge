module ToolUp.AI.AIToolRegistry

open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

/// A registered tool: metadata + executable function.
/// The Execute function receives JSON arguments and HttpContext
/// (for scope resolution / DI access), returns a JSON result.
type RegisteredTool = {
    /// Shared metadata visible to both server and client
    Definition: AIToolDefinition
    /// Tool definition in the format the AI provider expects
    ProviderDef: AIProviderToolDef
    /// Execute the tool: HttpContext -> JSON args -> Async<JSON result>
    Execute: HttpContext -> string -> Async<string>
}

/// Sanitize a tool name to match AI provider naming constraints.
/// Claude requires ^[a-zA-Z0-9_-]{1,128}$
let private sanitizeToolName (name: string) = name.Replace(".", "_")

/// JSON-escape a string value so it can be embedded inside a
/// double-quoted JSON string literal. Required because `toProviderDef`
/// hand-builds the `InputSchema` JSON via string interpolation; a raw
/// `"` or `\` in any tool's `Name` / `Type` / `Description` would
/// otherwise terminate the string early and produce malformed JSON
/// that the AI provider's `JsonDocument.Parse` rejects at request
/// time. Order matters: backslashes first so they don't double-escape
/// the replacements that follow.
let private jsonEscape (s: string) : string =
    s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

/// Phase 6g.A: surface gate. Returns true when the tool's `Surface`
/// declaration permits the current request's surface. The agent loop
/// applies this filter when constructing the per-turn tool list passed
/// to the provider, so the model only sees tools that make sense for
/// the user's chat surface.
let isToolVisibleOnSurface (surface: AISurface) (def: AIToolDefinition) : bool =
    match def.Surface, surface with
    | Both, _
    | SidePanelOnly, SidePanel
    | FullPageOnly, FullPage -> true
    | SidePanelOnly, FullPage
    | FullPageOnly, SidePanel -> false

// ─── Phase 36.A — per-module RBAC over AI tool dispatch ──────────────
//
// A module tool is an executable the model can reach on the caller's
// behalf. Until this phase the agent loop's per-turn tool list was
// filtered by SURFACE only, and the dispatch site looked a tool up by
// name and ran it — so a user holding no `Read` on a module could still
// have that module's tools invoked, simply by the model deciding to call
// one. That is a permission hole with the module's own RBAC gate sitting
// one layer below, unreached (GP 4: isolation is enforced structurally,
// not by "the module will check").
//
// Three properties the design is built around:
//
//   * **Filtered at LIST time, re-checked at DISPATCH time.** The model
//     never sees an inaccessible tool, so it never plans around one and
//     never has to be told "no" — cleaner than dispatch-time refusal
//     alone, which teaches the model that a tool exists. The dispatch
//     re-check is defence in depth for the forged-name case (a model
//     hallucinating a name it was never shown, or a replayed history).
//
//   * **Empty permission map stays unrestricted (GP 11).** RBAC is
//     opt-in per deployment; `AccessContext.hasPermission` already
//     returns `true` on an empty map, so a deployment that has never
//     configured module permissions is byte-for-byte unchanged.
//
//   * **SDK-reserved tool sources are exempt, because they are not
//     modules.** `_platform.ai.*` (Phase 36.B) enforces RBAC INTERNALLY
//     per requested TARGET module — gating it on its own `SourceModule`
//     would ask whether the caller may read a module called
//     `"_platform.ai"`, which no permission map names, so the whole
//     cross-module family would vanish the moment any deployment
//     configured RBAC. See `isSdkReservedToolSource`.

/// Phase 36.A — is this tool's `SourceModule` an SDK-reserved namespace
/// rather than a consumer module?
///
/// Two reserved namespaces, both owned by the SDK and neither ever a key
/// in a deployment's `ModulePermissions`:
///
///   * `_`-prefixed — the SDK's own tool-source convention:
///     `_platform.ai` (36.B cross-module family), `_sdk.FeatureFlags`,
///     `_sdk.ModuleVisibility`, `_algorithms`, `_facts`, `_scheduling`.
///   * `ToolUp.`-prefixed — the SDK package namespace: `ToolUp.Platform`
///     (the narrative tools), `ToolUp.KnowledgeBase`.
///
/// The phase specification named only `_platform.*` and `_sdk.*`; those
/// two prefixes cover four of the SDK's built-in tool families and miss
/// five, and a literal reading would have hidden the narrative, KB,
/// algorithm, fact and scheduling tools from every RBAC-configured
/// deployment. The generalisation is to the namespaces the SDK reserves,
/// which is the property that actually justifies the exemption.
///
/// A consumer module named inside a reserved namespace would inherit the
/// exemption. Module names are compose-time declarations by the
/// deployment author (never user input), and such a name already
/// collides with SDK identifiers — but the exemption is not absolute:
/// `isToolPermittedFor` honours an explicit permission-map entry for a
/// reserved source, so a deployment always retains the ability to gate
/// one deliberately.
let isSdkReservedToolSource (sourceModule: string) : bool =
    not (System.String.IsNullOrWhiteSpace sourceModule)
    && (sourceModule.StartsWith("_", System.StringComparison.Ordinal)
        || sourceModule.StartsWith("ToolUp.", System.StringComparison.Ordinal))

/// Phase 36.A — may this caller reach a tool sourced from this module?
///
/// The single predicate behind the list filter, the agent-loop dispatch
/// re-check, the `/api/ai/tool-result` completion gate and the
/// `GetAvailableTools` listing, so the four surfaces cannot drift apart.
///
/// Pure — `AccessContext` + `string` in, `bool` out; no framework type,
/// no I/O, no ambient state (six-rule audit: identity by value, nothing
/// to await, nothing retained between calls).
let isToolSourcePermittedFor (access: AccessContext) (sourceModule: string) : bool =
    if
        isSdkReservedToolSource sourceModule
        && not (access.ModulePermissions.ContainsKey sourceModule)
    then
        // A reserved source the deployment has not named: not a module,
        // so there is nothing to check here. Its own internal per-target
        // RBAC is the gate.
        true
    else
        AccessContext.hasPermission sourceModule ModulePermission.Read access

/// Phase 36.A — `isToolSourcePermittedFor` over a tool declaration.
let isToolPermittedFor (access: AccessContext) (def: AIToolDefinition) : bool =
    isToolSourcePermittedFor access def.SourceModule

/// Phase 36.A — reconstruct the caller's `AccessContext` from the
/// per-request items.
///
/// **Why items rather than DI, and why this is load-bearing.** Tool
/// dispatch runs inside the agent loop's *background* `HttpContext`
/// (`AIAssistantHandler.createBackgroundContext`), which carries its own
/// DI scope. The DI `AccessContext` factory reads the live request via
/// `IHttpContextAccessor`, which is null/unreliable on that background
/// flow — resolving it there would silently yield an *unrestricted*
/// anonymous context and this whole gate would pass everything. The
/// background context copies `ToolUp.StorageScope` / `ToolUp.UserId` /
/// `ToolUp.ModulePermissions` forward, so reading them back is the
/// RBAC-correct path (GP 4), and it works unchanged on a real request
/// context because the same middleware populates the same items.
///
/// Mirrors `PlatformAITools.reconstructAccess` deliberately — same
/// items, same fallbacks. `ModulePermissions` is the only field this
/// gate reads; the rest are populated for the audit trail's subject
/// attribution.
let reconstructAccessContext (ctx: HttpContext) : AccessContext =
    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let scopeOpt =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> Some s
        | _ -> None

    let teamId =
        match scopeOpt with
        | Some s when s.Container.StartsWith "team-" -> Some s.ScopeId
        | _ -> None

    let modulePermissions =
        match ctx.Items.TryGetValue "ToolUp.ModulePermissions" with
        | true, (:? Map<string, ModulePermission list> as perms) -> perms
        | _ -> Map.empty

    let moduleExposure =
        match ctx.Items.TryGetValue "ToolUp.ModuleExposure" with
        | true, (:? Map<string, ModuleExposure> as exposure) -> exposure
        | _ -> Map.empty

    let platformRole =
        match ctx.Items.TryGetValue "ToolUp.PlatformRole" with
        | true, (:? PlatformRole as role) -> Some role
        | _ -> None

    let subject =
        match ctx.Items.TryGetValue "ToolUp.Subject" with
        | true, (:? Subject as s) -> s
        | _ ->
            match teamId with
            | Some tid -> TeamMember(userId, tid)
            | None when userId <> "anonymous" -> AuthenticatedUser userId
            | None -> AnonymousSession userId

    {
        UserId = userId
        TeamId = teamId
        Subject = subject
        ModulePermissions = modulePermissions
        ModuleExposure = moduleExposure
        PlatformRole = platformRole
    }

// ─── Phase 730 — the grant / consent gate on the AI path ─────────────
//
// Phases 551 and 552 each closed their own dispatch seam and each
// recorded, in its Outcome, that this one was still open:
// `ListAccessible` and `PlatformAITools` read `ToolUp.ModulePermissions`
// and nothing else, so a grant that was PENDING the subject's acceptance,
// or whose counterparty consent had been REVOKED, was inert at the
// Remoting seam while its module stayed listed to the model and callable
// by it. The permission entry is present in both cases — that is the
// whole point of a pending grant — so a gate reading only the permission
// map cannot see the difference.
//
// **The mechanism, decided rather than defaulted.** Phase 551 deviation 1
// deferred a choice between two shapes and named THIS as the moment to
// revisit it: put the grant records on `AccessContext`, or extend the
// `HttpContext.Items` stamp. The stamp is taken, and the reasoning has
// moved on from 551's:
//
//   * 551's argument still holds — `AccessContext` is a Core record
//     embedded in downstream public surface, so a field would retype its
//     constructor, ripple through roughly twenty construction sites, and
//     redden every baseline that embeds it, for a value one seam reads.
//
//   * But the DECIDING argument is one 551 could not make, because 552
//     had not shipped. Grant liveness is no longer a pure property of the
//     subject. The counterparty arm's verdict is a store read plus a
//     signature check, resolved per request precisely so a revocation
//     bites at the next call (552.D). A `ModuleGrantRecord` on
//     `AccessContext` would therefore be only HALF the answer — and the
//     misleading half: `GrantPolicy.isGrantLive` returns `false` for the
//     counterparty arm by construction, so a consumer reading the field
//     and believing it would deny access that consent had legitimately
//     granted. A field that cannot be right is worse than no field.
//
// So the verdicts ride the request, where they are resolved, and this
// module reads them the same way `reconstructAccessContext` reads
// permissions — with `createBackgroundContext` carrying both stamps
// forward (730.E).

/// Phase 730 — a per-module grant-liveness predicate for the acting
/// request. Built ONCE per turn, closed over the stamps the middleware
/// left, and applied at both the list filter and the dispatch re-check.
///
/// `true` means "the caller's authority on this module is live". The
/// decision itself is `GrantConsentStore.dispatchVerdict` — the SAME
/// function the audited Remoting-seam guard uses, so the list the model
/// is offered and the boundary that would refuse it cannot disagree.
///
/// **Costs nothing when nothing is declared (GP 13 / GP 11).** A
/// deployment declaring no `GrantPolicy` registers no
/// `ModuleGrantPolicyRegistry`, so this is one failed `GetService` and a
/// constant `true` — the pre-730 tool list, in the pre-730 order, byte
/// for byte.
///
/// SDK-reserved tool sources (`_platform.*`, `ToolUp.*`) need no special
/// case: they are not modules, so the registry resolves them to
/// `AdminDiscretion`, for which the verdict is unconditionally `Ok`. The
/// reserved-namespace exemption stays exactly where Phase 36.A put it.
let moduleGrantGate (ctx: HttpContext) : string -> bool =
    match ctx.RequestServices.GetService(typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>) with
    | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as registry when
        not (GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry)
        ->
        let grants = GrantPolicyGuard.grantsFromItems ctx.Items
        let verdicts = GrantConsentStore.consentVerdictsFromItems ctx.Items

        fun moduleName ->
            match GrantConsentStore.dispatchVerdict registry grants verdicts moduleName with
            | Ok() -> true
            | Error _ -> false
    | _ -> fun _ -> true

/// Phase 730 — the audited dispatch-time twin of `moduleGrantGate`.
///
/// The list filter above is silent by design (nothing was attempted). A
/// tool the model names ANYWAY — because it planned around a stale list,
/// or because a provider ignored the offered set — is an attempt on inert
/// authority, and that is exactly the `UnconsentedGrantRefused` row Phase
/// 551 exists to produce. Emitting it through the shipped guard rather
/// than minting a second event type is deliberate: it is the same refusal
/// at a different call site, and an operator asking "was inert authority
/// reached for" should not have to union two streams to find out.
///
/// Best-effort audit, never blocking, exactly like the seam's: the
/// control is the refusal, not the row.
let guardToolGrant (ctx: HttpContext) (access: AccessContext) (moduleName: string) : bool =
    match ctx.RequestServices.GetService(typeof<GrantPolicyGuard.ModuleGrantPolicyRegistry>) with
    | :? GrantPolicyGuard.ModuleGrantPolicyRegistry as registry when
        not (GrantPolicyGuard.ModuleGrantPolicyRegistry.isEmpty registry)
        ->
        let auditLog =
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as log -> Some log
            | _ -> None

        let scopeId =
            match ctx.Items.TryGetValue "ToolUp.StorageScope" with
            | true, (:? StorageScope as s) -> s.ScopeId
            | _ -> access.UserId

        let verdict =
            GrantConsentStore.guardDispatchWithConsent
                registry
                (GrantPolicyGuard.grantsFromItems ctx.Items)
                (GrantConsentStore.consentVerdictsFromItems ctx.Items)
                auditLog
                Async.Start
                scopeId
                access.UserId
                moduleName

        Result.isOk verdict
    | _ -> true

/// Phase 36.A — the machine-readable verdict every unauthorized-tool
/// denial carries on its `AuthorizationDenied` audit row. Stable: the
/// `/dev/auth-denials` rollup and any operator query cut on it.
[<Literal>]
let UnauthorizedToolVerdict = "unauthorized_tool"

/// Phase 36.A — record an unauthorized AI-tool invocation attempt.
///
/// The phase specification called for a new `_platform.ai.unauthorized_tool`
/// audit stream. Phase 120 landed afterwards and generalised exactly this
/// write side: `IAuthAuditHook` is the one seam every authorization denial
/// on the HTTP surface calls, and `ModulePermissionDenialRequirement` is
/// precisely this denial's class — so the row joins the existing uniform
/// trail (and the `/dev/auth-denials` rollup, and its probing-burst dedup)
/// instead of founding a ninth per-subsystem stream nobody queries. The
/// tool name rides `Reason`; `Verdict` is `unauthorized_tool`.
///
/// **Best-effort, never blocking, never throwing** — same contract as
/// `IAuditLog.Record`. A missing hook (a test bypassing `compose`) is a
/// silent no-op; the refusal itself does not depend on the audit landing.
///
/// PII envelope: the subject id the hook already sanitises, the tool
/// name, and the source module. No arguments, no bodies — a tool's
/// arguments can carry anything the model chose to put in them.
let recordUnauthorizedToolDenial
    (ctx: HttpContext)
    (access: AccessContext)
    (route: string)
    (toolName: string)
    (sourceModule: string)
    : unit =
    try
        match ctx.RequestServices.GetService(typeof<IAuthAuditHook>) with
        | :? IAuthAuditHook as hook ->
            hook.RecordDenial {
                Route = route
                Subject = access.Subject
                Requirement = ModulePermissionDenialRequirement
                Verdict = UnauthorizedToolVerdict
                Reason =
                    $"AI tool '{toolName}' requires Read on module '{sourceModule}', which this caller does not hold."
                ScopeId =
                    match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                    | true, (:? StorageScope as s) -> Some s.ScopeId
                    | _ -> None
                CorrelationId = ToolUp.Remoting.Server.CallContext.correlationId ()
            }
            |> Async.Start
        | _ -> ()
    with _ ->
        ()

/// Generate an AIProviderToolDef (JSON Schema format) from an AIToolDefinition.
/// Eliminates the need to hand-write both Definition and ProviderDef.
/// Tool names are sanitized to meet provider naming constraints.
///
/// `Name` / `Type` / `Description` are JSON-escaped before
/// interpolation — descriptions routinely contain quoted examples
/// like `e.g. "country"`, which would otherwise terminate the JSON
/// string mid-stream and trip the provider's `JsonDocument.Parse`.
let toProviderDef (def: AIToolDefinition) : AIProviderToolDef =
    let properties =
        def.Parameters
        |> List.map (fun p ->
            $"\"{jsonEscape p.Name}\":{{\"type\":\"{jsonEscape p.Type}\",\"description\":\"{jsonEscape p.Description}\"}}")
        |> String.concat ","

    let required =
        def.Parameters
        |> List.filter _.Required
        |> List.map (fun p -> $"\"{jsonEscape p.Name}\"")
        |> String.concat ","

    {
        Name = sanitizeToolName def.Name
        Description = def.Description
        InputSchema = $"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}]}}"
    }

// ─── Phase 709 — per-tool result context budget ──────────────────────
//
// A tool result is serialised straight into model context. Any module
// tool can therefore flood the conversation with an unbounded payload —
// observed in a consumer composition whose analysis tool returns the
// complete subject key list on every call, which at high cardinality is
// 10⁵+ records JSON-encoded into the prompt. The agent-loop dispatch is
// the one choke point every tool result passes through, so the guard
// belongs there.
//
// Three properties the design is deliberately built around:
//
//   * **Visible, never silent.** An over-budget result is replaced by a
//     typed JSON marker naming the tool, the elided size, the ceiling
//     and the steer. It is NOT truncated mid-value: a JSON document cut
//     at character N is invalid JSON that the model will nonetheless try
//     to read, and a *silently* shortened ranking reads as complete.
//     Same reasoning as the population disclosure's `WithheldCount` —
//     an absence has to be reported to be reasoned about.
//
//   * **Distinct from a policy withhold.** The marker's discriminator is
//     `toolResultElided`, never a `withheld*` key. A budget drop and a
//     disclosure-policy withhold must not be confusable: one is "ask me
//     something narrower", the other is "you may not have this". The
//     budget also applies strictly AFTER the tool has returned — every
//     disclosure gate inside the tool has already run — so freed budget
//     can never surface a substitute for something a policy withheld.
//
//   * **Not an error.** `isErrorToolResult` in the agent loop matches the
//     invocation-error prose prefixes, and the marker deliberately does
//     not. The tool SUCCEEDED; the result was too large to carry. Having
//     it read as an error would spend the loop's two-turn early-stop
//     budget on a situation the model can recover from in one turn by
//     narrowing the query.

/// The SDK-wide default result ceiling, in characters of returned JSON.
///
/// Deliberately generous (GP 11 / GP 13): it is a flood guard, not a
/// tuning knob. 200 000 characters is roughly 50k tokens — an order of
/// magnitude above any well-behaved tool result in this repo and above
/// anything a deployment would want a *single* tool return to occupy,
/// while still well inside a modern frontier context window, so a
/// deployment that trips it was already broken rather than newly
/// constrained.
[<Literal>]
let DefaultToolResultBudgetChars = 200_000

/// Resolution + marker rendering for `AIToolResultBudget`. Pure — the
/// dispatch site in `AIAgentEngine` supplies the telemetry and logging.
module ToolResultBudget =

    /// The effective ceiling in characters, or `None` when this tool's
    /// result is not bounded.
    let limitFor (budget: AIToolResultBudget) : int option =
        match budget with
        | NoResultBudget -> None
        | DefaultResultBudget -> Some DefaultToolResultBudgetChars
        | ResultBudgetChars n -> Some n

    /// The typed replacement payload for an over-budget result. Valid
    /// JSON the model can read and act on: it names the tool, both
    /// sizes, and what to do next.
    let elisionMarker (toolName: string) (resultChars: int) (limitChars: int) : string =
        let steer =
            $"The result from '{toolName}' was {resultChars} characters, over this tool's "
            + $"{limitChars}-character context budget, and was withheld IN FULL rather than "
            + "truncated mid-value — a partial result would read as a complete one. "
            + "Narrow the request (fewer subjects, a smaller limit, a tighter filter), or call "
            + "an aggregate or paginated tool if one is available for this data."

        $"{{\"toolResultElided\":true,\"tool\":\"{jsonEscape toolName}\","
        + $"\"resultChars\":{resultChars},\"budgetChars\":{limitChars},"
        + $"\"reason\":\"over-context-budget\",\"steer\":\"{jsonEscape steer}\"}}"

    /// Apply a tool's budget to its returned JSON. Returns the payload
    /// the model should see, plus `Some elidedChars` when the result was
    /// replaced by the marker (`None` when it passed through).
    ///
    /// Under-budget results are returned **byte-identical** — the same
    /// string instance, no re-encode, no normalisation.
    let apply (toolName: string) (budget: AIToolResultBudget) (result: string) : string * int option =
        if isNull result then
            result, None
        else
            match limitFor budget with
            | None -> result, None
            | Some limit when result.Length <= limit -> result, None
            | Some limit -> elisionMarker toolName result.Length limit, Some result.Length

/// Create a RegisteredTool from an AIToolDefinition and an Execute function.
/// The ProviderDef is auto-generated from the Definition.
///
/// Phase 709: this is the single funnel every `RegisteredTool` passes
/// through, so it is where a nonsensical budget declaration is refused.
/// A non-positive `ResultBudgetChars` is a composition error, not a
/// silent "unbounded" — reading it as `NoResultBudget` would disable
/// exactly the guard the author was reaching for. `createTool` runs at
/// compose time (built-ins at module init, module tools inside
/// `composeAI`), so the failure lands at startup, beside the tool-name
/// collision check, and never mid-turn.
let createTool (def: AIToolDefinition) (execute: HttpContext -> string -> Async<string>) : RegisteredTool =
    match def.ResultBudget with
    | ResultBudgetChars n when n <= 0 ->
        failwithf
            "AI tool '%s' declares ResultBudgetChars %d. A result budget must be positive — use NoResultBudget to declare a tool whose contract is legitimately large, or DefaultResultBudget for the SDK ceiling (%d characters)."
            def.Name
            n
            DefaultToolResultBudgetChars
    | _ -> ()

    {
        Definition = def
        ProviderDef = toProviderDef def
        Execute = execute
    }

/// Mutable registry of AI-callable tools, populated at startup.
/// Modules register their tools via compose; the registry is immutable after startup.
type AIToolRegistry() =
    let mutable tools: RegisteredTool list = []

    /// Phase 709 — tools that have already logged a budget-elision Warn
    /// on this registry. Instance state rather than a module-level
    /// `mutable` so two composed servers in one process (tests do this)
    /// do not share a suppression set.
    let budgetWarned =
        System.Collections.Concurrent.ConcurrentDictionary<string, bool>()

    /// Register a list of tools (called during server startup)
    member _.RegisterAll(newTools: RegisteredTool list) = tools <- newTools @ tools

    /// Get all registered tools
    member _.GetAll() = tools

    /// Find a tool by name (matches against both Definition.Name and ProviderDef.Name)
    member _.FindByName(name: string) =
        tools
        |> List.tryFind (fun t -> t.Definition.Name = name || t.ProviderDef.Name = name)

    /// Phase 36.A — the tools this caller may reach, filtered by
    /// per-module `Read` permission on each tool's `SourceModule`.
    ///
    /// This is the list-time half of the gate: the agent loop builds its
    /// per-turn provider tool list from here, so an inaccessible tool is
    /// never described to the model at all. `GetAll` is left untouched —
    /// it remains the unfiltered registry view compose-time validators
    /// and diagnostics need.
    ///
    /// An unrestricted context (empty `ModulePermissions`) returns the
    /// same list `GetAll` does, in the same order (GP 11).
    ///
    /// **Phase 730 — this arity is the ungated one and is preserved
    /// exactly.** It admits every tool the RBAC filter admits, taking no
    /// view on grant policy, which is precisely its pre-730 behaviour.
    /// Kept rather than widened with an optional parameter, because an
    /// optional argument folds into ONE method and would delete this
    /// token from the public-API baseline — a genuine break under the
    /// documented Phase 175 rule (the same reasoning that gave Phase 552's
    /// decorator two explicit constructors). Every SDK call site moves to
    /// the two-argument overload below; a consumer calling this one keeps
    /// what it had.
    member this.ListAccessible(access: AccessContext) : RegisteredTool list =
        this.ListAccessible(access, (fun _ -> true))

    /// Phase 730 — the tools this caller may reach, filtered by BOTH the
    /// Phase 36.A per-module `Read` permission and `isModuleGrantLive`,
    /// the grant / consent gate.
    ///
    /// The second filter is what closes the gap Phases 551 and 552 each
    /// recorded: a permission entry is PRESENT for a grant awaiting the
    /// subject's acceptance, and present for one whose counterparty
    /// consent was revoked, so the RBAC filter alone admits both. Build
    /// the predicate with `moduleGrantGate` — it reads the request's own
    /// stamps and shares its decision with the seam that would refuse the
    /// call.
    ///
    /// Order of filters is not observable (both are pure predicates over
    /// the same list) but is written permission-first deliberately: a tool
    /// the caller cannot read at all should not reach a grant lookup.
    ///
    /// There is deliberately NO reserved-namespace short-circuit here,
    /// although `isToolSourcePermittedFor` has one. It would be redundant
    /// — a reserved source is not a module, so the policy registry
    /// resolves it to `AdminDiscretion` and the verdict is unconditionally
    /// live — and, worse, it would make this filter and the dispatch-time
    /// `guardToolGrant` structurally different. Two gates that must agree
    /// should share their whole decision, not most of it.
    member _.ListAccessible(access: AccessContext, isModuleGrantLive: string -> bool) : RegisteredTool list =
        tools
        |> List.filter (fun t ->
            isToolPermittedFor access t.Definition
            && isModuleGrantLive t.Definition.SourceModule)

    /// Phase 709 — claim the one budget-elision Warn allowed for this
    /// tool. Returns `true` exactly once per tool name per registry;
    /// every later elision by the same tool emits the telemetry counter
    /// but stays out of the log.
    ///
    /// The signal an operator needs is "this tool needs an aggregate
    /// shape", which is a property of the TOOL and does not become truer
    /// by being logged on every turn. A model that keeps calling an
    /// oversized tool would otherwise fill the operator log with the one
    /// line that mattered the first time — and the counter, which is
    /// where per-occurrence volume belongs, still records every one.
    member _.TryClaimBudgetWarning(toolName: string) : bool = budgetWarned.TryAdd(toolName, true)