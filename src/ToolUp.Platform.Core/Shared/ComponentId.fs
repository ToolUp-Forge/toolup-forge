// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Stable component identity (ComponentId) ──────────────────────────
//
// Every composed unit of an application — a module, a companion
// selection, a datatype, a tool — has a stable, declared `string`
// identity that survives rename and re-ordering. The SDK is already
// string-id-addressed at its core (a module's declared id is the real
// key, its display name is cosmetic; datatypes and tools carry stable
// string keys; transactional sinks carry a `Kind`). `ComponentId` makes
// that identity a first-class *value* (portability rule 1 — identity by
// value, never a live handle) and extends it to the one gap: companions
// selected by CLR interface type, which carry no value-id of their own.
//
// Identity is independent of display name and never positional. A module
// keeps its id when its `Name` is renamed; a companion in a multi-impl
// list (audit sinks, health checks, notification channels) keeps its id
// when the list is re-ordered, because the id composes the interface
// slot with the impl's own sub-id (sink `Kind` / `Name`), not its index.
//
// Zero cost when unused (GP 13): a deployment that declares no explicit
// ids and composes none of the introspection surfaces that read them
// (later identity/introspection phases) pays nothing — the default
// derivation is a pure string projection over identity that already
// exists.

/// A stable, declared identity for a composed unit (module, companion
/// slot, datatype, tool). A value-typed `string` wrapper — comparison
/// and hashing are structural, so a `ComponentId` is safe to use as a
/// map key, in a set, or across the wire (portability rule 1).
type ComponentId =
    | ComponentId of string

    /// The raw underlying identity string.
    member this.Value =
        let (ComponentId v) = this
        v

    override this.ToString() = this.Value

/// Construction + deterministic derivation for `ComponentId`. Every
/// derivation namespaces its input under a per-kind slot prefix
/// (`module:` / `companion:` / `datatype:` / `tool:`) so a module named
/// `auth` can never collide with a companion slot named `auth`.
module ComponentId =

    // Per-kind slot prefixes. Private — consumers derive through the
    // typed helpers below rather than concatenating prefixes by hand.
    [<Literal>]
    let private ModuleSlot = "module"

    [<Literal>]
    let private CompanionSlot = "companion"

    [<Literal>]
    let private DataTypeSlot = "datatype"

    [<Literal>]
    let private ToolSlot = "tool"

    // Phase 526 — grounding registry slots. A metric / subject id is
    // `ComponentId`-class and namespaced under its own slot so a metric
    // named `revenue` can never collide with a datatype or module `revenue`.
    [<Literal>]
    let private MetricSlot = "metric"

    [<Literal>]
    let private SubjectSlot = "subject"

    let private normalise (raw: string) : string =
        if System.String.IsNullOrWhiteSpace raw then
            ""
        else
            raw.Trim()

    /// The raw underlying identity string.
    let value (ComponentId v) : string = v

    /// Construct a `ComponentId` from a raw, already-namespaced string
    /// verbatim (trimmed). Raises `ArgumentException` on a null / empty /
    /// whitespace input — an empty identity is never valid. Prefer the
    /// `ofModule` / `forCompanionSlot` / … derivations, which namespace
    /// for you; use `create` only when round-tripping an id that already
    /// carries its slot prefix.
    let create (raw: string) : ComponentId =
        let v = normalise raw

        if v = "" then
            invalidArg "raw" "ComponentId cannot be null, empty, or whitespace."

        ComponentId v

    /// `create` as an option — `None` for a null / empty / whitespace
    /// input rather than a raise.
    let tryCreate (raw: string) : ComponentId option =
        let v = normalise raw
        if v = "" then None else Some(ComponentId v)

    let private scoped (slot: string) (rest: string) : ComponentId = create (slot + ":" + normalise rest)

    /// Derive a module's stable id from its declared id token. The id is
    /// independent of the module's display `Name`: once an explicit token
    /// is declared, renaming the display name does not change the id. The
    /// default (no explicit token) derives from the name at first
    /// registration (GP 11) — byte-for-byte unchanged for a module that
    /// declares nothing.
    let ofModule (declaredId: string) : ComponentId = scoped ModuleSlot declaredId

    /// Derive a companion slot's id from its interface slot name (one
    /// slot per interface in the common case, e.g. `IAuthProvider`,
    /// `IBlobStorage`). Use `forCompanionImpl` for multi-impl lists.
    let forCompanionSlot (interfaceName: string) : ComponentId = scoped CompanionSlot interfaceName

    /// Derive the id of one implementation within a multi-impl companion
    /// slot (audit sinks, health checks, notification channels). Composes
    /// the interface slot with the impl's own sub-id (sink `Kind` /
    /// `Name`) — never the impl's position in the list, so re-ordering
    /// the list does not change any id. Raises if the sub-id is empty:
    /// a multi-impl entry that cannot name itself cannot be addressed.
    let forCompanionImpl (interfaceName: string) (implSubId: string) : ComponentId =
        let iface = normalise interfaceName
        let sub = normalise implSubId

        if sub = "" then
            invalidArg
                "implSubId"
                (sprintf
                    "Companion slot '%s' requires a non-empty impl sub-id (sink Kind / Name); component identity is never positional."
                    iface)

        create (CompanionSlot + ":" + iface + "/" + sub)

    /// Derive a datatype's id from its declared `DataType.Id`.
    let forDataType (dataTypeId: string) : ComponentId = scoped DataTypeSlot dataTypeId

    /// Derive a tool's id from its declared `AIToolDefinition.Name`.
    let forTool (toolName: string) : ComponentId = scoped ToolSlot toolName

    /// Derive a grounding metric's id from its declared registry id (Phase
    /// 519 `Grounding.MetricDefinition.Id`). Rename-stable, slot-namespaced.
    let forMetric (metricId: string) : ComponentId = scoped MetricSlot metricId

    /// Derive a grounding subject-hierarchy's id from its declared registry
    /// id (`Grounding.SubjectDefinition.Id`).
    let forSubject (subjectId: string) : ComponentId = scoped SubjectSlot subjectId

    /// The ids that appear more than once in `ids`, each reported once in
    /// first-seen order. Empty when every id is unique.
    let findDuplicates (ids: ComponentId seq) : ComponentId list =
        ids
        |> Seq.countBy id
        |> Seq.filter (fun (_, n) -> n > 1)
        |> Seq.map fst
        |> List.ofSeq

    /// Raise a readable error if any id in `ids` is duplicated; no-op
    /// when every id is unique. `context` names the composition stage so
    /// the failure points at where to look (e.g. `"module composition"`).
    /// Structural enforcement — a collision is a compose-time failure,
    /// not a runtime surprise.
    let ensureUnique (context: string) (ids: ComponentId seq) : unit =
        match findDuplicates ids with
        | [] -> ()
        | dups ->
            let listing = dups |> List.map value |> String.concat ", "

            failwithf
                "%s: duplicate ComponentId(s) detected: %s. Every composed unit must resolve to a unique identity — declare an explicit id on the registration surface (e.g. ServerModule.withComponentId) to disambiguate."
                context
                listing