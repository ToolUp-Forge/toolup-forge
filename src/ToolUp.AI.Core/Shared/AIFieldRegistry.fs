// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

// ─── Phase 6j.B — server-side declared-field registry ─────────────
//
// The fast-path triage resolver (Tier 3) needs to know, server-side,
// which fields the *currently active* module surface exposes to the
// assistant, what natural-language phrasings the surface's author
// declared for each, and which value spellings are accepted. Without
// that it cannot build a triage prompt at all — it would be asking a
// model to guess field ids.
//
// ─── Why a seam, and why THIS seam ────────────────────────────────
//
// Three mechanisms could carry that metadata to the server, and the
// choice is recorded here because it is the load-bearing design
// decision of this phase:
//
//   1. **Declared server-side, like `ModuleAITypes`.** Rejected. The
//      declaration vocabulary belongs to the client-resident UI tier —
//      the same tier that owns the live control state and the
//      pattern-matching Tier-1 resolver. Restating it in a server-side
//      module declaration would make the SDK the owner of a schema it
//      does not define, and would drift the moment the client tier's
//      declaration surface grew a field.
//   2. **Carried on the chat request payload.** Rejected. It is
//      client-supplied text that would be interpolated straight into a
//      prompt whose output DRIVES a state mutation, so every request
//      would widen the injection surface; and the payload would ride
//      on every turn, including the overwhelming majority that never
//      triage.
//   3. **A DI-resolved seam the owning companion fills** — adopted.
//      Exactly the shape `IClientToolAuthorizer` already uses for the
//      other half of this problem (per-invocation authorization of a
//      client-resident tool): the SDK declares a narrow interface, the
//      companion that owns the client-resident surface implements it,
//      and a deployment that composes no such companion resolves
//      nothing and pays nothing (GP 13). Nothing here models the
//      client tier's declaration types — only the projection triage
//      needs.
//
// Absent registration ⇒ the triage resolver never fires and the agent
// loop is byte-for-byte its pre-6j.B self (GP 11).
//
// Portability (GP 12): identity by value (strings + records, no live
// handles), async at the boundary, no framework serialisation
// attributes, and the implementation carries no state between calls —
// each `Describe` is answered from whatever the implementation can see
// at that moment.

/// One field of the active module surface that the assistant may set.
/// A projection of whatever the owning companion's declaration surface
/// holds — deliberately stringly-typed so the SDK never has to model
/// that surface's own types.
type AIFieldDescriptor = {
    /// Stable field id. This is the value the triage resolver puts on
    /// the wire in the `_ui.set-field` action payload, so it MUST be
    /// the id the target module's `ActionDecoder` matches on.
    FieldId: string
    /// One-line human/model-readable description of what the field
    /// controls ("the country filter applied to the dataset").
    Description: string
    /// Value-type hint. Free-form, but the resolver treats
    /// `"string-option"` specially: only such a field may be cleared
    /// (triage emitting a `null` value). Conventional values:
    /// `"string"` / `"string-option"` / `"number"` / `"boolean"` /
    /// `"enum"`.
    ValueType: string
    /// Natural-language instruction patterns the surface's author
    /// declared for this field ("set country to {value}"). Used as
    /// exemplars in the triage prompt — the same declarations Tier 1
    /// pattern-matches on, so triage and the deterministic tier share
    /// one source of truth rather than drifting apart.
    InstructionPatterns: string list
    /// Accepted value spellings as `(alias, canonical)` pairs
    /// ("britain" → "UK"). The resolver folds a triage-proposed value
    /// through this map before emitting, so the module's decoder only
    /// ever sees canonical values. Empty ⇒ values pass through
    /// unchanged.
    ValueAliases: (string * string) list
}

/// What the assistant can see and drive on the surface the user is
/// currently looking at.
type AIFieldSnapshot = {
    /// The active module's `ModuleDefinition.Id` — the routing key the
    /// emitted `Notification.ModuleAction` is addressed to.
    ModuleId: string
    /// The sidebar page route within that module, when the surface is
    /// multi-page. `None` for single-page modules.
    Page: string option
    /// The settable fields. An EMPTY list is meaningful and is not an
    /// error: it says "this surface declares nothing triage can drive",
    /// and the resolver falls through to the full agent loop.
    Fields: AIFieldDescriptor list
    /// Short, already-redacted summary of the surface's current state
    /// ("country=UK, period=2024-Q1, 812 rows"). Included verbatim in
    /// the triage prompt so the model can tell "clear the country"
    /// from a no-op. The implementation is responsible for keeping
    /// this free of PII and bounded in size — the resolver truncates
    /// but does not inspect it.
    StateSummary: string
}

module AIFieldSnapshot =
    /// A surface that declares nothing. Triage falls through on this,
    /// which is the correct answer for a module with no declared
    /// fields.
    let empty (moduleId: string) : AIFieldSnapshot = {
        ModuleId = moduleId
        Page = None
        Fields = []
        StateSummary = ""
    }

    /// Look a proposed field id up in the snapshot. Case-insensitive,
    /// because the triage model echoes an id back as text and casing
    /// is the one thing it reliably gets wrong; the value returned is
    /// the DECLARED descriptor, so the canonical id is what reaches
    /// the wire regardless of how the model spelled it.
    let tryFindField (fieldId: string) (snapshot: AIFieldSnapshot) : AIFieldDescriptor option =
        if System.String.IsNullOrWhiteSpace fieldId then
            None
        else
            snapshot.Fields
            |> List.tryFind (fun f ->
                System.String.Equals(f.FieldId, fieldId.Trim(), System.StringComparison.OrdinalIgnoreCase))

module AIFieldDescriptor =
    /// Fold a proposed value through the field's declared alias set.
    /// Case-insensitive on the alias key; an unmatched value passes
    /// through trimmed but otherwise unchanged (aliases narrow
    /// spellings, they do not restrict the value space — a field that
    /// wants to restrict it declares `ValueType = "enum"` and the
    /// module's own decoder rejects what it does not accept).
    let resolveAlias (field: AIFieldDescriptor) (raw: string) : string =
        if isNull raw then
            ""
        else
            let trimmed = raw.Trim()

            field.ValueAliases
            |> List.tryFind (fun (alias, _) ->
                System.String.Equals(alias, trimmed, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map snd
            |> Option.defaultValue trimmed

/// The seam. Implemented by the companion that owns the client-resident
/// UI surface; resolved from DI by the fast-path triage resolver. Absent
/// ⇒ no triage (GP 13).
///
/// Implementations MUST be cheap (this runs on the chat path, ahead of
/// the provider call) and MUST NOT throw — an unknown module is `None`,
/// not an exception. The resolver treats a raised exception as "no
/// snapshot" and falls through, but a throwing implementation makes the
/// fall-through invisible in telemetry, which defeats the point of
/// measuring the tier.
type IAIFieldRegistry =
    /// Describe the surface the user is currently looking at. `None`
    /// ⇒ nothing is known about this module/page pair, so triage does
    /// not run for this turn.
    abstract Describe: moduleId: string * page: string option -> Async<AIFieldSnapshot option>