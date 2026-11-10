// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── The open audit-event registry ───────────────────────────────
//
// An audit event KIND is a stable string id (the persisted `EventType`)
// plus a payload codec. The platform's own kinds are the cases of the
// `AuditEvent` union. Every other kind belongs to a registrant — a
// companion or a module — which declares it here as an
// `AuditEventFamily<'E>` over its OWN event union, in its own assembly,
// and registers it from its compose step. Core never names a registrant's
// events: it carries them as `AuditEvent.Opaque` (kind + payload bytes).
//
// What a reader needs beyond the bytes rides elsewhere and is not
// repeated per kind: the subject (`_actor`) and the scope (the stream)
// are properties of the stored ROW. The kind declares its severity.
//
// Registration is checked at compose: two registrants declaring one
// kind, or a registrant declaring a platform kind, refuses the
// composition and names both — the rule the algorithm-provider and
// notification-sink registries follow.

/// How grave an audit event of a kind is, as its registrant grades it.
type AuditSeverity =
    | AuditInformational
    | AuditLow
    | AuditMedium
    | AuditHigh
    | AuditCritical

/// One kind as the registry records it.
type AuditEventKindInfo = {
    /// The stable wire id — the persisted `EventType`. Never renamed once
    /// a row of it has been written.
    Kind: string
    /// Who declared the kind (`"ToolUp.Platform"`, `"ToolUp.Forms"`, …).
    Registrant: string
    /// The registrant's grading; `None` when it does not grade the kind
    /// and leaves classification to the sink.
    Severity: AuditSeverity option
}

/// A registrant's family of audit events over its own event type `'E`.
[<NoEquality; NoComparison>]
type AuditEventFamily<'E> = {
    /// The registrant's name, as a duplicate refusal names it.
    Registrant: string
    /// Every kind the family writes.
    Kinds: AuditEventKindInfo list
    /// The family's encoder: one compiler-checked `match` over its events,
    /// answering each event's kind and payload JSON.
    Encode: 'E -> string * string
    /// The family's decoder: a kind's payload JSON back to its event. May
    /// throw on a malformed payload; `AuditEventFamily.tryRead` catches.
    Decode: string -> string -> 'E
}

/// A family with its event type erased: what a compose step hands the
/// registry.
type AuditEventRegistration = {
    Registrant: string
    Kinds: AuditEventKindInfo list
}

/// A registration the registry refused.
type AuditEventRegistryError =
    /// Two registrants declared one kind.
    | DuplicateAuditKind of kind: string * firstRegistrant: string * secondRegistrant: string

/// The composed set of kinds, keyed by wire id.
type AuditEventRegistry = private {
    ByKind: Map<string, AuditEventKindInfo>
}

module AuditEventFamily =
    /// The family's registration, for `AuditEventRegistry.build`.
    let registration (family: AuditEventFamily<'E>) : AuditEventRegistration = {
        Registrant = family.Registrant
        Kinds = family.Kinds
    }

    /// Whether the family declares `kind`.
    let owns (family: AuditEventFamily<'E>) (kind: string) : bool =
        family.Kinds |> List.exists (fun k -> k.Kind = kind)

    /// The `AuditEvent` a family's event is recorded as.
    let event (family: AuditEventFamily<'E>) (value: 'E) : AuditEvent =
        let kind, payload = family.Encode value
        Opaque { Kind = kind; Payload = payload }

    /// The family's event an `AuditEvent` carries, when it carries one of
    /// the family's kinds and its payload decodes.
    let tryRead (family: AuditEventFamily<'E>) (audit: AuditEvent) : 'E option =
        match audit with
        | Opaque o when owns family o.Kind ->
            try
                Some(family.Decode o.Kind o.Payload)
            with _ ->
                None
        | _ -> None

module AuditEventRegistry =
    /// The registrant name the platform's own kinds are recorded under.
    [<Literal>]
    let PlatformRegistrant = "ToolUp.Platform"

    /// Compose the registrations into one registry. Refuses a kind two
    /// registrants declare, naming both; the first in registration order
    /// is the one named first.
    let build (registrations: AuditEventRegistration list) : Result<AuditEventRegistry, AuditEventRegistryError> =
        let folder (acc: Result<Map<string, AuditEventKindInfo>, AuditEventRegistryError>) (info: AuditEventKindInfo) =
            match acc with
            | Error _ -> acc
            | Ok byKind ->
                match Map.tryFind info.Kind byKind with
                | Some existing -> Error(DuplicateAuditKind(info.Kind, existing.Registrant, info.Registrant))
                | None -> Ok(Map.add info.Kind info byKind)

        registrations
        |> List.collect (fun r -> r.Kinds |> List.map (fun k -> { k with Registrant = r.Registrant }))
        |> List.fold folder (Ok Map.empty)
        |> Result.map (fun byKind -> { ByKind = byKind })

    /// One sentence naming what a refusal refused.
    let describeError (error: AuditEventRegistryError) : string =
        match error with
        | DuplicateAuditKind(kind, first, second) ->
            sprintf
                "audit event kind '%s' is declared by both '%s' and '%s'; a kind has exactly one registrant"
                kind
                first
                second

    /// The registry's record of `kind`, when a registrant declares it.
    let tryFind (kind: string) (registry: AuditEventRegistry) : AuditEventKindInfo option =
        Map.tryFind kind registry.ByKind

    /// Every registered kind, ordered by wire id.
    let kinds (registry: AuditEventRegistry) : AuditEventKindInfo list =
        registry.ByKind |> Map.toList |> List.map snd