// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Composition diff (CompositionDiff.diff) ─────────────────────────
//
// Structurally diff two `CompositionManifest`s (Phase 280) by stable
// `ComponentId` — every module / companion slot / datatype / tool that was
// added, removed, or changed, plus every config knob whose value moved.
//
// The manifest describes *one* composition; this is its natural companion
// — given the manifest of a known-good composition and the manifest of a
// candidate, it reports the *whole* surface change keyed by identity, so a
// human (deploy review, generated release notes, GitOps drift) or a
// composition golden-file CI gate sees exactly what moved. Where
// `ConfigDriftDetector` only hashes the
// companion-assembly *set* (a boolean "did anything change?"), this reports
// the specific field deltas: which module vanished, which companion impl
// was swapped, which config knob was flipped.
//
// **Order-independent by construction.** Every kind is keyed by its stable
// `ComponentId` (config knobs by `Name`), never by position in the list —
// so re-ordering the registration of two otherwise-identical compositions
// diffs to empty. An identical pair always diffs to the empty delta.
//
// **Pure + zero cost when unused (GP 11 / GP 13).** `diff` is a pure
// function over two already-built manifests; a deployment that never diffs
// constructs nothing and pays nothing. Generic substrate (GP 1) — the
// delta carries only `ComponentId`, a kind discriminator, a display label,
// an impl sub-id, and string config values; no vendor or domain type.

/// A field-level change within a single component entry that KEPT its
/// stable `ComponentId` across the two manifests (e.g. a module renamed
/// while holding an explicit id, or a single-impl companion slot whose
/// impl sub-id moved). Each delta is `Some (before, after)` when that
/// field changed and `None` when it held. A change entry always carries at
/// least one populated delta — an entry with the same id and every field
/// equal is unchanged, not a change.
type ComponentEntryChange = {
    Id: ComponentId
    Kind: ComponentKind
    /// Display-label change — `Some (before, after)` when the entry's
    /// `Label` moved under a stable id.
    LabelDelta: (string * string) option
    /// Impl sub-id change — `Some (before, after)` when the entry's `Impl`
    /// moved under a stable id.
    ImplDelta: (string option * string option) option
}

/// A single config knob whose value moved between the two manifests, keyed
/// by knob `Name`.
type ConfigKnobChange = {
    Name: string
    Before: string
    After: string
}

/// The structural difference between two `CompositionManifest`s, keyed by
/// stable `ComponentId` (entries) and knob `Name` (config) — never by list
/// position. Per kind: what was added (in the second, not the first),
/// removed (in the first, not the second), and changed (same id, differing
/// fields). The empty delta (every list empty) means the two compositions
/// are structurally identical.
type CompositionDelta = {
    ModulesAdded: ComponentEntry list
    ModulesRemoved: ComponentEntry list
    ModulesChanged: ComponentEntryChange list

    CompanionSlotsAdded: ComponentEntry list
    CompanionSlotsRemoved: ComponentEntry list
    CompanionSlotsChanged: ComponentEntryChange list

    DataTypesAdded: ComponentEntry list
    DataTypesRemoved: ComponentEntry list
    DataTypesChanged: ComponentEntryChange list

    ToolsAdded: ComponentEntry list
    ToolsRemoved: ComponentEntry list
    ToolsChanged: ComponentEntryChange list

    ConfigKnobsAdded: ConfigKnob list
    ConfigKnobsRemoved: ConfigKnob list
    ConfigKnobsChanged: ConfigKnobChange list
}

/// Structural, id-keyed, order-independent diff of two `CompositionManifest`s.
module CompositionDiff =

    // ── Ordinal comparers so every emitted list is deterministic ──
    // (the diff of two compositions must not depend on registration order).

    let private byId (e: ComponentEntry) = e.Id.Value

    let private sortEntries (es: ComponentEntry list) : ComponentEntry list =
        es |> List.sortWith (fun a b -> System.String.CompareOrdinal(byId a, byId b))

    let private sortChanges (cs: ComponentEntryChange list) : ComponentEntryChange list =
        cs
        |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Id.Value, b.Id.Value))

    /// The field-level change between two entries that share an id. Only
    /// the fields that actually moved are populated.
    let private entryChange (before: ComponentEntry) (after: ComponentEntry) : ComponentEntryChange = {
        Id = after.Id
        Kind = after.Kind
        LabelDelta =
            if before.Label <> after.Label then
                Some(before.Label, after.Label)
            else
                None
        ImplDelta =
            if before.Impl <> after.Impl then
                Some(before.Impl, after.Impl)
            else
                None
    }

    /// Added / removed / changed for one kind's entry list, keyed by
    /// `ComponentId`. A duplicate id within a list keeps the last-seen
    /// entry (well-formed manifests carry unique ids — Phase 279 /
    /// Phase 281 enforce it — so this only degrades gracefully on a
    /// malformed input).
    let private diffEntries
        (before: ComponentEntry list)
        (after: ComponentEntry list)
        : ComponentEntry list * ComponentEntry list * ComponentEntryChange list =
        let beforeById = before |> List.map (fun e -> e.Id, e) |> Map.ofList
        let afterById = after |> List.map (fun e -> e.Id, e) |> Map.ofList

        let added =
            after |> List.filter (fun e -> not (beforeById.ContainsKey e.Id)) |> sortEntries

        let removed =
            before |> List.filter (fun e -> not (afterById.ContainsKey e.Id)) |> sortEntries

        let changed =
            after
            |> List.choose (fun a ->
                match Map.tryFind a.Id beforeById with
                | Some b when b <> a -> Some(entryChange b a)
                | _ -> None)
            |> sortChanges

        added, removed, changed

    /// Added / removed / changed for the config knobs, keyed by `Name`.
    let private diffKnobs
        (before: ConfigKnob list)
        (after: ConfigKnob list)
        : ConfigKnob list * ConfigKnob list * ConfigKnobChange list =
        let beforeByName = before |> List.map (fun k -> k.Name, k.Value) |> Map.ofList
        let afterByName = after |> List.map (fun k -> k.Name, k.Value) |> Map.ofList

        let added =
            after
            |> List.filter (fun k -> not (beforeByName.ContainsKey k.Name))
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Name, b.Name))

        let removed =
            before
            |> List.filter (fun k -> not (afterByName.ContainsKey k.Name))
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Name, b.Name))

        let changed =
            after
            |> List.choose (fun a ->
                match Map.tryFind a.Name beforeByName with
                | Some bv when bv <> a.Value ->
                    Some {
                        Name = a.Name
                        Before = bv
                        After = a.Value
                    }
                | _ -> None)
            |> List.sortWith (fun a b -> System.String.CompareOrdinal(a.Name, b.Name))

        added, removed, changed

    /// The empty delta — what an identical pair (or a manifest diffed
    /// against itself) projects to.
    let empty: CompositionDelta = {
        ModulesAdded = []
        ModulesRemoved = []
        ModulesChanged = []
        CompanionSlotsAdded = []
        CompanionSlotsRemoved = []
        CompanionSlotsChanged = []
        DataTypesAdded = []
        DataTypesRemoved = []
        DataTypesChanged = []
        ToolsAdded = []
        ToolsRemoved = []
        ToolsChanged = []
        ConfigKnobsAdded = []
        ConfigKnobsRemoved = []
        ConfigKnobsChanged = []
    }

    /// Structurally diff two manifests. `before` is the reference (or
    /// prior) composition, `after` the candidate (or current) one — so
    /// "added" means present in `after` but not `before`. Keyed by stable
    /// `ComponentId` throughout, so the result is independent of the order
    /// in which either manifest's components were registered.
    let diff (before: CompositionManifest) (after: CompositionManifest) : CompositionDelta =
        let mAdd, mRem, mChg = diffEntries before.Modules after.Modules
        let cAdd, cRem, cChg = diffEntries before.CompanionSlots after.CompanionSlots
        let dAdd, dRem, dChg = diffEntries before.DataTypes after.DataTypes
        let tAdd, tRem, tChg = diffEntries before.Tools after.Tools
        let kAdd, kRem, kChg = diffKnobs before.ConfigKnobs after.ConfigKnobs

        {
            ModulesAdded = mAdd
            ModulesRemoved = mRem
            ModulesChanged = mChg
            CompanionSlotsAdded = cAdd
            CompanionSlotsRemoved = cRem
            CompanionSlotsChanged = cChg
            DataTypesAdded = dAdd
            DataTypesRemoved = dRem
            DataTypesChanged = dChg
            ToolsAdded = tAdd
            ToolsRemoved = tRem
            ToolsChanged = tChg
            ConfigKnobsAdded = kAdd
            ConfigKnobsRemoved = kRem
            ConfigKnobsChanged = kChg
        }

    /// `true` when the two compositions are structurally identical — every
    /// added / removed / changed list is empty across all four kinds and
    /// the config knobs.
    let isEmpty (d: CompositionDelta) : bool =
        List.isEmpty d.ModulesAdded
        && List.isEmpty d.ModulesRemoved
        && List.isEmpty d.ModulesChanged
        && List.isEmpty d.CompanionSlotsAdded
        && List.isEmpty d.CompanionSlotsRemoved
        && List.isEmpty d.CompanionSlotsChanged
        && List.isEmpty d.DataTypesAdded
        && List.isEmpty d.DataTypesRemoved
        && List.isEmpty d.DataTypesChanged
        && List.isEmpty d.ToolsAdded
        && List.isEmpty d.ToolsRemoved
        && List.isEmpty d.ToolsChanged
        && List.isEmpty d.ConfigKnobsAdded
        && List.isEmpty d.ConfigKnobsRemoved
        && List.isEmpty d.ConfigKnobsChanged

    // ── Human-readable rendering (the CI gate's failure message) ──

    let private renderImpl (impl: string option) =
        match impl with
        | Some s -> s
        | None -> "(none)"

    let private renderEntryLine (marker: string) (e: ComponentEntry) =
        sprintf "  %s %s (%s)" marker e.Id.Value e.Label

    let private renderChangeLine (c: ComponentEntryChange) =
        let fields =
            [
                match c.LabelDelta with
                | Some(b, a) -> sprintf "label: %s -> %s" b a
                | None -> ()
                match c.ImplDelta with
                | Some(b, a) -> sprintf "impl: %s -> %s" (renderImpl b) (renderImpl a)
                | None -> ()
            ]
            |> String.concat "; "

        sprintf "  ~ %s (%s)" c.Id.Value fields

    let private renderSection
        (title: string)
        (added: ComponentEntry list)
        (removed: ComponentEntry list)
        (changed: ComponentEntryChange list)
        : string list =
        if List.isEmpty added && List.isEmpty removed && List.isEmpty changed then
            []
        else
            [
                sprintf "%s:" title
                yield! added |> List.map (renderEntryLine "+")
                yield! removed |> List.map (renderEntryLine "-")
                yield! changed |> List.map renderChangeLine
            ]

    /// A deterministic, human-readable rendering of a delta — the message a
    /// composition golden-file CI gate prints on a mismatch. The empty delta
    /// renders to a single "no differences" line.
    let render (d: CompositionDelta) : string =
        if isEmpty d then
            "(no composition differences)"
        else
            let knobLines =
                if
                    List.isEmpty d.ConfigKnobsAdded
                    && List.isEmpty d.ConfigKnobsRemoved
                    && List.isEmpty d.ConfigKnobsChanged
                then
                    []
                else
                    [
                        "Config knobs:"
                        yield! d.ConfigKnobsAdded |> List.map (fun k -> sprintf "  + %s = %s" k.Name k.Value)
                        yield! d.ConfigKnobsRemoved |> List.map (fun k -> sprintf "  - %s = %s" k.Name k.Value)
                        yield!
                            d.ConfigKnobsChanged
                            |> List.map (fun k -> sprintf "  ~ %s: %s -> %s" k.Name k.Before k.After)
                    ]

            [
                yield! renderSection "Modules" d.ModulesAdded d.ModulesRemoved d.ModulesChanged
                yield!
                    renderSection
                        "Companion slots"
                        d.CompanionSlotsAdded
                        d.CompanionSlotsRemoved
                        d.CompanionSlotsChanged
                yield! renderSection "Data types" d.DataTypesAdded d.DataTypesRemoved d.DataTypesChanged
                yield! renderSection "Tools" d.ToolsAdded d.ToolsRemoved d.ToolsChanged
                yield! knobLines
            ]
            |> String.concat "\n"