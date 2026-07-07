// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open FSharp.Reflection

// ─── Composable-surface descriptor (ComposableSurface) ───────────────
//
// Emits forge's **available** composition surface as data — every
// companion-interface slot the SDK can compose (`IAuthProvider` /
// `IBlobStorage` / `IAuditSink` / …), the enum-like composition-shaping
// config knobs and their admissible values, and the consumer module
// contract — distinct from Phase 280's `CompositionManifest`, which
// describes a *composed instance*. This is the **type-level vocabulary**
// an external composition / scaffolding tool snapshots to build its own
// model of forge, instead of hand-mirroring a forge-version-drifting
// copy: manifest = "what THIS app composed"; surface = "what forge CAN
// compose".
//
// **Derived from the live registry, never a hand-declared list.** The
// slot enumeration reflects over the `ServerApp` composition record's own
// fields: every field whose type is an `IX option` (a single-impl
// companion slot) or an `IX list` (a multi-impl slot), where `IX` is an
// interface, IS a composable slot. A newly-shipped companion slot — a new
// interface-typed field on `ServerApp` — therefore surfaces here with no
// change to this descriptor. Likewise the config-knob schemas reflect
// over `ServerConfig`'s enum-like (all-nullary-case) union fields, so a
// new composition-mode knob surfaces automatically. The substrate
// requirements are best-effort metadata: a slot with no metadata entry
// still surfaces (with an empty requirement list), so the derivation is
// never gated on the metadata being complete.
//
// **Generic substrate (GP 1); zero cost when unused (GP 11 / GP 13).**
// The shape carries no vendor or domain type — only `ComponentId`,
// interface names, and strings — and is produced only when a caller asks
// (`ComposableSurface.describe`); a deployment that never introspects
// builds nothing and is byte-for-byte unchanged.

/// How many implementations a composable companion slot admits.
type SlotCardinality =
    /// An `IX option` field — at most one implementation per deployment
    /// (`IAuthProvider`, `IBlobStorage`, `INotificationChannel`, …).
    | SingleImpl
    /// An `IX list` field — many implementations, each addressed by its
    /// own Phase 279 sub-id (`IAuditSink`, `IHealthCheck`,
    /// `IConfigValidator`, `INotificationSink`, …).
    | MultiImpl

/// One companion slot forge can compose: its stable Phase 279 slot id,
/// the interface it is filled by, its cardinality, and the substrate
/// interfaces an implementation's `create` typically receives.
type ComposableSlot = {
    /// The Phase 279 slot id (`companion:<interface>`) — the same id
    /// space the composed manifest keys against.
    Slot: ComponentId
    /// The companion interface name (`IAuthProvider`, `IAuditSink`, …).
    Interface: string
    Cardinality: SlotCardinality
    /// Substrate interfaces an implementation's `create` typically
    /// receives (best-effort metadata; empty when unknown — the slot
    /// still surfaces).
    SubstrateRequirements: string list
}

/// An enum-like composition-shaping config knob and the admissible
/// values it can take — the schema an external editor renders as a
/// dropdown rather than a free-text field.
type ConfigKnobSchema = {
    /// The `ServerConfig` field name (`ProcessProfile`,
    /// `ConfigDriftDetection`, `JobScheduler`, …).
    Name: string
    /// The admissible values (the union case names).
    Values: string list
}

/// The consumer module contract, as data — the four-file convention a
/// scaffolding tool emits, plus the Phase 279 slot prefixes a module's
/// units are keyed under.
type ModuleContractShape = {
    /// The four module files, in compile order.
    Files: string list
    ModuleSlotPrefix: string
    DataTypeSlotPrefix: string
    ToolSlotPrefix: string
}

/// forge's available composition vocabulary: the companion slots it can
/// compose, the composition-shaping config-knob schemas, and the module
/// contract. Derived from the live `ServerApp` / `ServerConfig` registry,
/// so it stays forge-version-synced by construction.
type ComposableSurface = {
    Slots: ComposableSlot list
    ConfigKnobs: ConfigKnobSchema list
    ModuleContract: ModuleContractShape
}

module ComposableSurface =

    /// Best-effort substrate-requirement metadata, keyed by interface
    /// name. A slot absent from this map surfaces with an empty
    /// requirement list — the enumeration is never gated on this being
    /// complete, so a newly-shipped slot appears with no descriptor
    /// change even before its metadata is filled in.
    let private substrateRequirements: Map<string, string list> =
        Map.ofList [
            "IAuditSink", [ "ISecretStore" ]
            "INotificationSink", [ "ISecretStore" ]
            "INotificationChannel", [ "ISecretStore" ]
            "IProviderProfile", [ "IBlobStorage" ]
            "IUserDirectory", [ "ISecretStore" ]
            "IShareTokenStore", [ "IBlobStorage" ]
            "IPendingInviteStore", [ "IBlobStorage" ]
            "IModuleBindingVerifier", [ "ISecretStore" ]
        ]

    /// Classify a record-field type as a companion slot: `IX option` →
    /// `SingleImpl`, `IX list` → `MultiImpl`, where `IX` is an interface.
    /// Anything else (a primitive, a non-interface option/list, a
    /// function-valued list) is not a slot.
    let private slotOf (fieldType: Type) : (Type * SlotCardinality) option =
        if fieldType.IsGenericType then
            let def = fieldType.GetGenericTypeDefinition()
            let arg = fieldType.GetGenericArguments()[0]

            let isCompanionInterface =
                arg.IsInterface && arg.Name.StartsWith("I", StringComparison.Ordinal)

            if not isCompanionInterface then None
            elif def = typedefof<option<_>> then Some(arg, SingleImpl)
            elif def = typedefof<list<_>> then Some(arg, MultiImpl)
            else None
        else
            None

    /// Every companion slot forge can compose, derived by reflecting the
    /// `ServerApp` composition record's interface-typed option / list
    /// fields. Deterministic (sorted by interface name); deduplicated by
    /// slot id so a single- and multi-impl field on the same interface
    /// collapse to one slot.
    let slots () : ComposableSlot list =
        FSharpType.GetRecordFields(typeof<ServerApp>)
        |> Array.choose (fun field ->
            slotOf field.PropertyType
            |> Option.map (fun (iface, cardinality) -> {
                Slot = ComponentId.forCompanionSlot iface.Name
                Interface = iface.Name
                Cardinality = cardinality
                SubstrateRequirements = substrateRequirements |> Map.tryFind iface.Name |> Option.defaultValue []
            }))
        |> Array.toList
        |> List.distinctBy _.Interface
        |> List.sortBy _.Interface

    /// The composition-shaping config-knob schemas, derived by reflecting
    /// `ServerConfig`'s enum-like union fields (every case nullary) — the
    /// mode switches that change *what* gets composed. A newly-added
    /// mode knob surfaces automatically.
    let configKnobs () : ConfigKnobSchema list =
        FSharpType.GetRecordFields(typeof<ServerConfig>)
        |> Array.choose (fun field ->
            let t = field.PropertyType

            if FSharpType.IsUnion t then
                let cases = FSharpType.GetUnionCases t

                let enumLike =
                    cases.Length > 0 && cases |> Array.forall (fun c -> c.GetFields().Length = 0)

                if enumLike then
                    Some {
                        Name = field.Name
                        Values = cases |> Array.map _.Name |> Array.toList
                    }
                else
                    None
            else
                None)
        |> Array.toList
        |> List.sortBy _.Name

    /// The consumer module contract, as data.
    let moduleContract: ModuleContractShape = {
        Files = [ "SharedTypes"; "Server"; "ClientModel"; "ClientView" ]
        ModuleSlotPrefix = "module"
        DataTypeSlotPrefix = "datatype"
        ToolSlotPrefix = "tool"
    }

    /// Assemble forge's full composable-surface descriptor. Pure + on
    /// demand — nothing is built until a caller asks for it (GP 13).
    let describe () : ComposableSurface = {
        Slots = slots ()
        ConfigKnobs = configKnobs ()
        ModuleContract = moduleContract
    }