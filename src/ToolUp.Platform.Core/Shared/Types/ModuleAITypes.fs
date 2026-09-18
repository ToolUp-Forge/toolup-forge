// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Module-facing AI tool declarations ──────────────────────────
//
// These types live in the core SDK so modules can declare AI tools without
// having to reference the `ToolUp.AI` companion. The AI runtime
// infrastructure (registry, provider interface, agent loop, conversation
// persistence, assistant API) all lives in `ToolUp.AI`; only the
// *declaration shape* is shared here.

/// JSON Schema representation for tool parameter descriptions.
/// Kept simple and Fable-compatible (no `System.Text.Json` dependency).
type ToolParameterSchema = {
    Name: string
    /// The parameter's JSON Schema. Either a bare type name —
    /// "string" | "number" | "boolean" | "object" | "array" — or
    /// (Phase 508) a complete rendered JSON-Schema object, which is what
    /// `ToolSchema.parameter` writes here so a parameter can declare a
    /// nested object, an array of objects or a closed enum. The two are
    /// told apart structurally by `ToolSchema.isRendered`; a bare type
    /// name emits byte-for-byte what it always did (GP 11). Never
    /// hand-write the rendered form — the renderer owns the escaping.
    Type: string
    Description: string
    Required: bool
    /// JSON-encoded default value
    Default: string option
}

/// Where a tool executes. `ServerResident` is the existing default —
/// the executor takes `HttpContext + argsJson` and runs entirely on
/// the server. `ClientResident` flips the dispatch direction: the
/// agent loop emits a `ClientToolInvoke` SSE event, the browser runs
/// the F# tool body (typically dispatching typed `Msg`s into a
/// module's MVU), and POSTs the result back to `/api/ai/tool-result`
/// where the suspended agent loop resumes. ClientResident tools
/// require the client-resident runtime to be present.
type ToolLocation =
    | ServerResident
    | ClientResident

/// Per-tool surface gate. Filters which tools are visible to the model
/// per-turn based on which AI surface the user is chatting from.
/// `Both` (default) — tool is always offered, regardless of surface.
/// `SidePanelOnly` — Mode 1 only; e.g. a server-side analytical tool
/// that doesn't make sense in the "watch me work" full-page surface.
/// `FullPageOnly` — Mode 2 only; e.g. UI-mutation tools that need the
/// active full-page module to drive. The agent loop reads
/// `AIMessageRequest.Surface` and `AIToolRegistry.toProviderDef`
/// applies the filter when constructing the per-turn tool list.
type AISurfaceFilter =
    | Both
    | SidePanelOnly
    | FullPageOnly

/// Declares one client-side action a tool may emit alongside its JSON
/// text result. Used by the two-tier pattern: the tool runs
/// server-side (agent loop sees the JSON), AND it publishes a
/// `Notification.ModuleAction` that the client routes into the named
/// module's state via its `ActionDecoder`. Tools that only return JSON
/// leave `EmitsActions = None`.
///
/// The declaration is an inspection surface — for documentation, for
/// the data catalog, and so modules can reason about which
/// action keys they should decode. It is not an enforcement contract;
/// an executor that emits an undeclared action is a bug, not a
/// permission violation.
type ActionDeclaration = {
    /// Target module. Matches the `ModuleDefinition.Id` the client
    /// routes to; also the RBAC key used to gate delivery.
    ModuleId: string
    /// Module-owned discriminator passed to the module's `ActionDecoder`.
    /// E.g. "apply-optimised-budget".
    ActionKey: string
    /// Human-readable description of what the action does. Surfaces in
    /// admin tooling; not shown to the AI model.
    Description: string
    /// Optional JSON-Schema string describing the payload shape. The
    /// decoder on the client is hand-written today; the schema is
    /// advisory. Future: codegen decoders from the schema.
    PayloadSchema: string option
}

/// Phase 709 — per-tool **context budget** for the JSON result a tool
/// returns to the agent loop. A tool result is serialised straight into
/// model context, so a tool whose contract is "return the matching
/// records" floods the conversation at high cardinality: 10⁵ records
/// JSON-encoded into the prompt, every turn, until the provider refuses
/// the request. The budget is a context-hygiene ceiling on ONE tool
/// result, and it is deliberately NOT a member of the resource-
/// *exhaustion* budget family — compute (`ComputeBudget`), AI tokens,
/// monetary cost — that Phase 689 sets out to unify behind one seam.
///
/// Keeping the two shapes apart is a decision, not an oversight, and it
/// is worth stating before that seam lands so nobody folds this in on
/// the strength of the shared word "budget". An exhaustion budget meters
/// a consumable somebody is billed for: it accrues over a declared
/// period, is scoped to a submitter class, needs a store to remember
/// what has been spent, and its answer is allow / warn / REFUSE. This
/// budget meters one payload against one prompt: it holds no state,
/// spans no period, bills nobody, and never refuses — it substitutes a
/// steer and lets the turn continue. A single seam covering both would
/// have to make period, store and submitter class optional, at which
/// point it has stopped saying anything.
///
/// The unit is **characters of the returned JSON**, not tokens and not
/// UTF-8 bytes. Forge owns no tokenizer, and characters are the currency
/// every sibling bound in this area already speaks (RAG's
/// `SnippetCharLimit`, the fact-clause rendering) — a second unit here
/// would make two budgets incomparable for no gain in fidelity.
///
/// `DefaultResultBudget` is what every pre-709 tool carries, and it
/// resolves to a deliberately generous SDK ceiling
/// (`AIToolRegistry.DefaultToolResultBudgetChars`) that no well-behaved
/// tool result approaches — an existing deployment is byte-for-byte
/// unchanged (GP 11).
type AIToolResultBudget =
    /// Use the SDK-wide default ceiling. The value every tool declared
    /// before Phase 709 carries.
    | DefaultResultBudget
    /// This tool's own ceiling, in characters of the returned JSON.
    /// Must be positive; `AIToolRegistry.createTool` refuses a
    /// non-positive declaration at compose time rather than silently
    /// reading it as "unbounded".
    | ResultBudgetChars of int
    /// This tool's contract is legitimately large — never elide its
    /// result. The escape hatch for an export / bulk-transfer tool whose
    /// whole point is the payload.
    | NoResultBudget

// ─── Phase 793 — the tool effect class ──────────────────────────
//
// A tool definition said what a tool was CALLED, what it TOOK, which
// module supplied it, where it RAN and how large its result could be —
// and nothing about what it DID. An executor is `HttpContext -> string
// -> Async<string>`, so the set of actions a model output could cause
// was whatever an executor body happened to do, and the claim "no model
// output can cause an action outside the permitted set" had no set to
// quantify over. `ToolEffect` is that set's vocabulary, declared per
// tool by the module that authors it: the party that knows whether the
// body writes, spends, or reaches outside the deployment.
//
// The declaration is read at three points, and the three are what make
// it a boundary rather than documentation:
//
//   * the LIST / DISPATCH gate (`ToolGate.decide`, `ToolUp.AI`) admits a
//     tool only when its declared classes sit within the deployment's
//     `ToolPolicy` ceiling, so a tool the policy would refuse is never
//     described to the model and never runs if the model names it anyway;
//   * the ENVELOPE (`ToolEffectEnvelope`) binds the running body: a
//     host-capability invocation or an outbound request the body makes
//     through the SDK's seams is refused unless the matching effect was
//     declared;
//   * COMPOSITION under the verified profile refuses a tool that declares
//     nothing, because a mandatory envelope with nothing to check against
//     would admit everything while presenting as enforcement.
//
// `UndeclaredEffects` is what every tool authored before this phase
// carries: no envelope, no ceiling — byte-for-byte the pre-793 behaviour
// under the default policy (GP 11), refused only under a policy that
// says undeclared tools are not admitted, or the verified profile.

/// One effect a tool body may exercise. A closed vocabulary rather than
/// free strings, so a policy ceiling can name a CLASS ("no egress at
/// all") and a proof can quantify over every case; the payloads are what
/// the envelope checks at the moment of use (WHICH capability, WHICH
/// destination) and what a denial names.
type ToolEffect =
    /// Reads facts, results, entities or other structured data the
    /// deployment holds.
    | ReadFacts
    /// Computes over its arguments or over data it read — an analytical
    /// primitive, an aggregate, a transformation. No new state.
    | ComputeFacts
    /// Reads documents, narratives, layouts, catalogue metadata — content
    /// rather than measured facts.
    | ReadContent
    /// Writes state within the named scope — a store, a module, a
    /// narrative. The scope is a declaration the tool author owns.
    | WriteState of scope: string
    /// Makes an outbound request to the named destination. A tool that
    /// reaches several declares one `Egress` per destination.
    | Egress of destination: string
    /// Consumes a metered budget of the named class (a model call, a paid
    /// API, a notification send).
    | Spend of budgetClass: string
    /// Invokes the named host capability through `IHostCapabilityRegistry`.
    | External of capabilityId: string
    /// Publishes client-side actions alongside its result. Subsumes the
    /// `EmitsActions` field's declaration: a tool with `EmitsActions = Some
    /// _` carries this class whether or not it lists it, see
    /// `ToolEffect.declaredOf`.
    | EmitsActions

/// The class of a `ToolEffect` — the effect with its payload erased. What
/// a `ToolPolicy` ceiling is expressed over: a ceiling that could only
/// name exact destinations or exact scopes could never say "no writes".
[<RequireQualifiedAccess>]
type ToolEffectClass =
    | ReadFacts
    | ComputeFacts
    | ReadContent
    | WriteState
    | Egress
    | Spend
    | External
    | EmitsActions

/// What a tool declares about its effects. `UndeclaredEffects` is
/// distinguishable from `DeclaredEffects Set.empty` on purpose: the
/// second is a tool that says it exercises nothing and is held to it, the
/// first is a tool that has not said, which the default policy admits and
/// the verified profile refuses.
type ToolEffectDeclaration =
    /// No declaration — every pre-793 tool. Admitted under the default
    /// policy with no envelope; refused wherever a declaration is
    /// mandatory.
    | UndeclaredEffects
    /// The effects this tool's body may exercise. The envelope refuses
    /// anything outside it.
    | DeclaredEffects of Set<ToolEffect>

[<RequireQualifiedAccess>]
module ToolEffectClass =
    /// Stable lowercase label for policy files, audit payloads and
    /// refusal reasons.
    let label (c: ToolEffectClass) : string =
        match c with
        | ToolEffectClass.ReadFacts -> "read-facts"
        | ToolEffectClass.ComputeFacts -> "compute-facts"
        | ToolEffectClass.ReadContent -> "read-content"
        | ToolEffectClass.WriteState -> "write-state"
        | ToolEffectClass.Egress -> "egress"
        | ToolEffectClass.Spend -> "spend"
        | ToolEffectClass.External -> "external"
        | ToolEffectClass.EmitsActions -> "emits-actions"

    /// Every class, in declaration order.
    let all: ToolEffectClass list = [
        ToolEffectClass.ReadFacts
        ToolEffectClass.ComputeFacts
        ToolEffectClass.ReadContent
        ToolEffectClass.WriteState
        ToolEffectClass.Egress
        ToolEffectClass.Spend
        ToolEffectClass.External
        ToolEffectClass.EmitsActions
    ]

    /// The read-only classes: what a tool that inspects and computes but
    /// changes nothing and reaches nowhere exercises.
    let readOnly: Set<ToolEffectClass> =
        Set.ofList [
            ToolEffectClass.ReadFacts
            ToolEffectClass.ComputeFacts
            ToolEffectClass.ReadContent
        ]

[<RequireQualifiedAccess>]
module ToolEffect =
    /// The effect's class — its payload erased.
    let classOf (e: ToolEffect) : ToolEffectClass =
        match e with
        | ReadFacts -> ToolEffectClass.ReadFacts
        | ComputeFacts -> ToolEffectClass.ComputeFacts
        | ReadContent -> ToolEffectClass.ReadContent
        | WriteState _ -> ToolEffectClass.WriteState
        | Egress _ -> ToolEffectClass.Egress
        | Spend _ -> ToolEffectClass.Spend
        | External _ -> ToolEffectClass.External
        | EmitsActions -> ToolEffectClass.EmitsActions

    /// Human-readable rendering — the class label plus the payload, for a
    /// refusal reason or an audit row.
    let describe (e: ToolEffect) : string =
        match e with
        | WriteState scope -> $"write-state({scope})"
        | Egress destination -> $"egress({destination})"
        | Spend budgetClass -> $"spend({budgetClass})"
        | External capabilityId -> $"external({capabilityId})"
        | ReadFacts
        | ComputeFacts
        | ReadContent
        | EmitsActions -> ToolEffectClass.label (classOf e)

[<RequireQualifiedAccess>]
module ToolEffectDeclaration =
    /// Declare a set of effects from a list.
    let declare (effects: ToolEffect list) : ToolEffectDeclaration = DeclaredEffects(Set.ofList effects)

    /// A tool that reads facts and nothing else.
    let readFacts: ToolEffectDeclaration = declare [ ReadFacts ]

    /// A tool that reads content and nothing else.
    let readContent: ToolEffectDeclaration = declare [ ReadContent ]

    /// A tool that computes over its arguments and nothing else.
    let computeFacts: ToolEffectDeclaration = declare [ ComputeFacts ]

    /// The classes a declaration exercises; empty for an undeclared tool,
    /// which has no classes rather than every class.
    let classes (d: ToolEffectDeclaration) : Set<ToolEffectClass> =
        match d with
        | UndeclaredEffects -> Set.empty
        | DeclaredEffects effects -> effects |> Set.map ToolEffect.classOf

    /// Render a declaration for a refusal reason or an audit row.
    let describe (d: ToolEffectDeclaration) : string =
        match d with
        | UndeclaredEffects -> "(undeclared)"
        | DeclaredEffects effects when Set.isEmpty effects -> "(none)"
        | DeclaredEffects effects -> effects |> Set.toList |> List.map ToolEffect.describe |> String.concat ", "

/// A tool that an AI agent can invoke. Registered by modules.
/// Contains metadata only — the Execute function is server-only and lives
/// in `ToolUp.AI.RegisteredTool` alongside the rest of the AI runtime.
type AIToolDefinition = {
    /// Unique tool name, e.g. "media_optimisation.run"
    Name: string
    /// Human-readable description for the AI model
    Description: string
    /// Parameter schema for the AI model
    Parameters: ToolParameterSchema list
    /// Which module provides this tool
    SourceModule: string
    /// Client-side actions this tool may publish in addition
    /// to returning its JSON text result. `None` — tool is chat-only
    /// (the default, existing behaviour). `Some list` — enumerates the
    /// module+actionKey pairs the tool can target; each requires the
    /// named module to register a matching `ActionDecoder` to be
    /// consumed (undeclared decoders drop silently).
    EmitsActions: ActionDeclaration list option
    /// Where the tool's body runs. `ServerResident` — the
    /// executor takes `HttpContext + argsJson` and runs entirely on
    /// the server (existing behaviour, the default for tools that
    /// predate client-resident execution). `ClientResident` — the
    /// agent loop dispatches the call back to the browser via SSE,
    /// the client runs the body
    /// (typically dispatching typed `Msg`s into a module's MVU) and
    /// POSTs the result to `/api/ai/tool-result`.
    Location: ToolLocation
    /// Per-tool surface gate. `Both` (default) — tool is
    /// always offered to the model. `SidePanelOnly` / `FullPageOnly`
    /// — tool is filtered out of the per-turn tool list when the
    /// chat request comes from the other surface. The platform-built-in
    /// `_platform.ui.set_field` / `_platform.ui.click_button` tools
    /// declare `FullPageOnly` because they need a full-page module to
    /// drive; `_platform.ui.inspect_active_module` declares `Both`
    /// because read-only awareness is useful in either surface.
    Surface: AISurfaceFilter
    /// Declares that this tool reads or drives the **live interface** —
    /// the browser-resident module state the user is currently looking
    /// at — so a question about on-screen state is answerable without
    /// consulting the knowledge base. `false` (the default every
    /// pre-Phase-538 tool carries) leaves behaviour byte-for-byte
    /// unchanged (GP 11).
    ///
    /// Read by `RAGPromptBuilder.ToolFraming.fromTools` to decide
    /// whether a RAG deployment's knowledge-base-first framing needs the
    /// live-interface companion. `Location = ClientResident` implies
    /// live-interface by construction (the body runs in the browser
    /// against live UI state) and is honoured independently, so a
    /// client-resident tool need not set this flag; it exists for the
    /// cases `Location` cannot express — a **server-resident** tool that
    /// nonetheless projects live interface state, or a host-adapter tool
    /// whose live-interface intent must be declared rather than inferred
    /// from its name.
    ///
    /// This replaces the pre-538 `_platform.ui.*` name-prefix trigger:
    /// forge never emits that name itself, so keying framing off it
    /// coupled the SDK to a naming convention owned elsewhere — a
    /// differently-named live-interface tool silently missed the
    /// framing, and a server-resident tool that merely happened to start
    /// with `_platform.ui.` wrongly tripped it.
    IsLiveInterface: bool
    /// Phase 709 — the per-tool context budget applied to this tool's
    /// JSON result at agent-loop dispatch. `DefaultResultBudget` (the
    /// value every pre-709 tool carries) resolves to the generous
    /// SDK-wide ceiling and changes no existing behaviour (GP 11).
    ///
    /// Declared here rather than at the registration seam because the
    /// module that authors the tool is the party that knows whether its
    /// result is bounded by construction — a coverage listing emitting
    /// one row per (metric × hierarchy) knows its own cardinality
    /// exposure; the composition root that calls
    /// `AIToolRegistry.createTool` does not.
    ResultBudget: AIToolResultBudget
    /// Phase 793 — the effects this tool's body may exercise, declared by
    /// the module that authors it. `UndeclaredEffects` (the value every
    /// pre-793 tool carries) leaves the default policy's behaviour
    /// byte-for-byte unchanged (GP 11); a `ToolPolicy` ceiling reads the
    /// declared classes at list and dispatch time, and the envelope holds
    /// the running body to the declared set. Composition under the
    /// verified profile refuses an undeclared tool.
    ///
    /// Declared on the definition rather than at the registration seam for
    /// the same reason `ResultBudget` is: the author of the body is the
    /// party that knows what it does.
    Effects: ToolEffectDeclaration
}

[<RequireQualifiedAccess>]
module AIToolEffects =
    /// Phase 793 — the effects a definition declares, with the
    /// `EmitsActions` field folded in: a tool that declares client actions
    /// on that field carries the `EmitsActions` class whether or not its
    /// `Effects` set lists it, so the older declaration is subsumed rather
    /// than duplicated. An undeclared tool stays undeclared — the field
    /// alone does not turn it into a declared one.
    let declaredOf (def: AIToolDefinition) : ToolEffectDeclaration =
        match def.Effects, def.EmitsActions with
        | DeclaredEffects effects, Some _ -> DeclaredEffects(Set.add EmitsActions effects)
        | declared, _ -> declared

// ─── Phase 508 — rich (recursive) tool parameter schemas ─────────
//
// A tool parameter was flat: one type NAME, one description. Anything
// structured — a nested object, an array of objects, a closed set of
// allowed values — had to be declared as a bare object / array / string
// and hand-parsed in the executor, even though every shipped provider
// accepts full JSON Schema on the wire (Claude's `input_schema`,
// OpenAI's `parameters`, Gemini's function-declaration parameters) and
// the MCP host republishes it verbatim. The declaration was the
// bottleneck, not the transport.
//
// `ToolSchemaNode` is that declaration, as immutable data (GP 5), in the
// tier-shared Core so it is Fable-compilable alongside the rest of the
// module-facing tool surface (GP 10).
//
// **Where the schema RIDES, and why it is not a new field.** A rich
// parameter is an ordinary `ToolParameterSchema` whose `Type` carries
// the rendered JSON-Schema object instead of a bare type name;
// `ToolSchema.parameter` is the only thing that writes it and
// `ToolSchema.isRendered` the only thing that recognises it. Adding a
// field to `ToolParameterSchema` (or to `AIToolDefinition`) would retype
// the compiler-generated constructor, so every tool any consumer has
// already authored as a full record literal would stop compiling — a
// breaking change in a record whose whole purpose is to be written out
// longhand by module authors. Widening what one EXISTING field may hold
// costs nobody anything: a flat declaration emits byte-for-byte the
// definition it emitted before (GP 11), and `Name`, `Description`,
// `Required` and `Default` keep their ordinary meanings, so the
// top-level required list, the per-module RBAC filter and the MCP
// projection all read a rich parameter exactly as they read a flat one.
//
// The discriminator is safe rather than merely convenient: a flat
// declaration is emitted with its `Type` string as the JSON-Schema type
// keyword, so a pre-508 parameter whose `Type` began with an opening
// brace would have been offering providers a type keyword that does not
// exist. No tool that WORKS today can collide with the rendered form.

/// One node of a tool parameter's JSON Schema — the recursive
/// declaration a tool author writes instead of a bare type name.
///
/// Deliberately a closed, small vocabulary rather than a general JSON
/// Schema model: it covers the shapes a tool call actually needs
/// (objects, arrays, enums, scalars) and nothing a provider would have
/// to be asked to honour. A tool needing a schema keyword outside it
/// still has the flat escape hatch it always had.
type ToolSchemaNode =
    /// A JSON string. An empty list is a free-form string; a non-empty
    /// list renders as an enum, which is what turns a set of allowed
    /// values from prose in a description into a constraint the model is
    /// held to.
    | SchemaString of enumValues: string list
    /// A JSON number (fractional values permitted).
    | SchemaNumber
    /// A JSON integer.
    | SchemaInteger
    /// A JSON boolean.
    | SchemaBoolean
    /// A JSON array whose every element matches the item schema.
    | SchemaArray of items: ToolSchemaNode
    /// A JSON object with the declared members. Member order is
    /// preserved into the rendered schema, so the emitted bytes are
    /// stable for a given declaration.
    | SchemaObject of members: ToolSchemaMember list

/// One member of a `SchemaObject`. Field names carry a Member prefix so
/// they never shadow `ToolParameterSchema`'s own fields where both types
/// are in scope.
and ToolSchemaMember = {
    /// The member's JSON property name.
    MemberName: string
    /// Human-readable description for the AI model. An empty string
    /// omits the description keyword rather than emitting a blank one.
    MemberDescription: string
    /// Whether the member appears in the enclosing object's required
    /// list.
    MemberRequired: bool
    /// The member's own schema — recursive, so objects nest and arrays
    /// carry object items.
    MemberSchema: ToolSchemaNode
}

/// Constructors and the JSON-Schema renderer for `ToolSchemaNode`.
///
/// The renderer is total: every string it emits is escaped, so a member
/// name, a description or an enum value carrying a quote, a backslash or
/// a control character produces valid JSON rather than a malformed
/// request the provider rejects at send time. That is the same hazard
/// the flat path has always had to handle, extended to the nested names
/// and the enum values the flat path never had.
[<RequireQualifiedAccess>]
module ToolSchema =

    /// JSON-escape a string so it can be embedded inside a
    /// double-quoted JSON string literal. Backslashes first, so the
    /// replacements that follow are not double-escaped.
    ///
    /// This is the one implementation in the SDK: the AI companion's
    /// provider-definition renderer delegates to it rather than keeping
    /// a second copy that could drift.
    let jsonEscape (s: string) : string =
        if isNull (box s) then
            ""
        else
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")

    /// A free-form JSON string.
    let text = SchemaString []

    /// A JSON string constrained to a closed set of values.
    let enumOf (values: string list) = SchemaString values

    /// A JSON number.
    let number = SchemaNumber

    /// A JSON integer.
    let integer = SchemaInteger

    /// A JSON boolean.
    let boolean = SchemaBoolean

    /// A JSON array whose elements all match the supplied item schema.
    let arrayOf (items: ToolSchemaNode) = SchemaArray items

    /// A JSON object with the declared members, in declaration order.
    let objectOf (members: ToolSchemaMember list) = SchemaObject members

    /// One member of an object schema.
    let field (name: string) (description: string) (required: bool) (schema: ToolSchemaNode) : ToolSchemaMember = {
        MemberName = name
        MemberDescription = description
        MemberRequired = required
        MemberSchema = schema
    }

    let rec private renderNode (description: string) (node: ToolSchemaNode) : string =
        let descPart =
            if System.String.IsNullOrEmpty description then
                ""
            else
                ",\"description\":\"" + jsonEscape description + "\""

        let scalar (typeName: string) =
            "{\"type\":\"" + typeName + "\"" + descPart + "}"

        match node with
        | SchemaString [] -> scalar "string"
        | SchemaString values ->
            let rendered =
                values |> List.map (fun v -> "\"" + jsonEscape v + "\"") |> String.concat ","

            "{\"type\":\"string\"" + descPart + ",\"enum\":[" + rendered + "]}"
        | SchemaNumber -> scalar "number"
        | SchemaInteger -> scalar "integer"
        | SchemaBoolean -> scalar "boolean"
        | SchemaArray items -> "{\"type\":\"array\"" + descPart + ",\"items\":" + renderNode "" items + "}"
        | SchemaObject members ->
            let properties =
                members
                |> List.map (fun m ->
                    "\""
                    + jsonEscape m.MemberName
                    + "\":"
                    + renderNode m.MemberDescription m.MemberSchema)
                |> String.concat ","

            let required =
                members
                |> List.filter _.MemberRequired
                |> List.map (fun m -> "\"" + jsonEscape m.MemberName + "\"")
                |> String.concat ","

            "{\"type\":\"object\""
            + descPart
            + ",\"properties\":{"
            + properties
            + "},\"required\":["
            + required
            + "]}"

    /// Render a node to a complete JSON-Schema object, carrying the
    /// supplied description when it is non-empty. The result is what
    /// `ToolParameterSchema.Type` holds for a rich parameter, and what
    /// the provider-definition renderer splices in verbatim.
    let render (description: string) (node: ToolSchemaNode) : string = renderNode description node

    /// Whether a `ToolParameterSchema.Type` value is a rendered schema
    /// object rather than a bare JSON-Schema type name.
    ///
    /// Structural, not a marker: a rendered schema is a JSON object, and
    /// a type NAME never is. See the note above on why no working
    /// pre-508 declaration can be misread by this.
    let isRendered (schemaType: string) : bool =
        if isNull (box schemaType) then
            false
        else
            let trimmed = schemaType.TrimStart()
            trimmed.Length > 0 && trimmed[0] = '{'

    /// Declare a tool parameter carrying a rich schema. Produces an
    /// ordinary `ToolParameterSchema` — it registers, filters and
    /// projects exactly as a flat parameter does; only `Type` differs.
    ///
    /// The description lands in BOTH the record's `Description` (so
    /// every existing reader of that field still sees it) and the
    /// rendered schema (so the model sees it where JSON Schema puts it).
    let parameter (name: string) (description: string) (required: bool) (schema: ToolSchemaNode) : ToolParameterSchema = {
        Name = name
        Type = render description schema
        Description = description
        Required = required
        Default = None
    }