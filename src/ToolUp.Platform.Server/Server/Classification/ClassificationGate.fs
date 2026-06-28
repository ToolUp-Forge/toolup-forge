// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

/// Phase 41 — per-classification access decision for a single field.
type ClassificationDecision =
    /// The caller may read the field value.
    | Allow
    /// The caller may not — the gate replaces the value with the
    /// redaction placeholder (deployments that prefer a hard 403 inspect
    /// the decision and refuse the whole call instead).
    | Redact

/// A policy maps a classification level + caller context to a decision.
/// Deployments supply their own to express bespoke role mappings; the
/// SDK ships `ClassificationGate.defaultPolicy`.
type ClassificationPolicy = ClassificationLevel -> AccessContext -> ClassificationDecision

/// Phase 41 — composes a classifier + a policy + audit into the
/// read/write gate. Field-level: classified fields a caller may not read
/// are redacted (or the deployment refuses the call); classified reads /
/// writes emit value-free audit under `_platform.classification`.
///
/// Composes with Phase 4 RBAC — the default policy treats
/// `PlatformAdmin` (or an explicit per-level reader capability) as the
/// grant for sensitive classifications, layering on top of the existing
/// `ModulePermission` checks rather than replacing them.
module ClassificationGate =

    /// Replacement value the gate substitutes for a redacted field.
    [<Literal>]
    let RedactedPlaceholder = "[redacted]"

    /// Reserved per-level reader pseudo-module. A caller granted
    /// `ModulePermission.Read` on `_classification.Pii` may read `Pii`
    /// fields under the default policy (the "PiiReader" role, expressed
    /// through the existing RBAC permission map rather than a new role
    /// type).
    let readerModule (level: ClassificationLevel) : string =
        $"_classification.{ClassificationLevel.name level}"

    let private hasReaderCapability (level: ClassificationLevel) (ctx: AccessContext) : bool =
        AccessContext.canModifyPlatformConfig ctx
        || (ctx.ModulePermissions
            |> Map.tryFind (readerModule level)
            |> Option.map (List.exists (fun granted -> ModulePermission.implies granted ModulePermission.Read))
            |> Option.defaultValue false)

    /// SDK default policy. Fails closed on sensitive data: `Financial` /
    /// `Regulatory` / `Pii` / `Spi` require `PlatformAdmin` or the matching
    /// per-level reader capability; `Public` / `Confidential` are always
    /// allowed. The fail-closed posture is deliberate — a deployment that
    /// hasn't configured a reader capability should redact sensitive
    /// fields, not leak them under the pre-RBAC "unrestricted" default.
    let defaultPolicy: ClassificationPolicy =
        fun level ctx ->
            if not (ClassificationLevel.isSensitive level) then Allow
            elif hasReaderCapability level ctx then Allow
            else Redact

    /// The decision for one classification under a policy + caller.
    let decide (policy: ClassificationPolicy) (classification: FieldClassification) (ctx: AccessContext) =
        policy classification.Level ctx

    /// Scope under which classification audit is recorded — the caller's
    /// config scope when resolvable, else the reserved `_platform` scope.
    let private auditScope (ctx: AccessContext) : string =
        AccessContext.configScope ctx
        |> Option.map _.Container
        |> Option.defaultValue "_platform"

    let private recordRead (audit: IAuditLog) (ctx: AccessContext) (c: FieldClassification) (redacted: bool) = async {
        if c.AuditOnRead then
            try
                do!
                    audit.Record(
                        auditScope ctx,
                        AuditEvent.ClassifiedFieldRead {
                            UserId = ctx.UserId
                            EntityName = c.EntityName
                            FieldPath = c.FieldPath
                            Level = ClassificationLevel.name c.Level
                            Redacted = redacted
                        }
                    )
            with _ ->
                // IAuditLog.Record is best-effort; swallow per contract.
                ()
    }

    /// Apply the gate to a flat `fieldPath -> value` view of an entity:
    /// redact the values of classified fields the caller may not read,
    /// emitting a `ClassifiedFieldRead` audit (redacted flag set
    /// accordingly) for any field whose classification has `AuditOnRead`.
    /// Unclassified fields pass through untouched.
    let redactFields
        (classifier: IFieldClassifier)
        (policy: ClassificationPolicy)
        (audit: IAuditLog)
        (ctx: AccessContext)
        (entityName: string)
        (fields: Map<string, string>)
        : Async<Map<string, string>> =
        async {
            let! classifications = classifier.Classify entityName
            let byPath = classifications |> List.map (fun c -> c.FieldPath, c) |> Map.ofList
            let mutable result = fields

            for KeyValue(path, value) in fields do
                match Map.tryFind path byPath with
                | None -> () // unclassified — passes through
                | Some c ->
                    match decide policy c ctx with
                    | Allow -> do! recordRead audit ctx c false
                    | Redact ->
                        result <- Map.add path RedactedPlaceholder result
                        do! recordRead audit ctx c true

            return result
        }

    /// Record `ClassifiedFieldWritten` for each classified field among
    /// `writtenFieldPaths`. Call from an entity-store write path after a
    /// successful save. Unclassified fields produce no event.
    let recordWrites
        (classifier: IFieldClassifier)
        (audit: IAuditLog)
        (ctx: AccessContext)
        (entityName: string)
        (writtenFieldPaths: string list)
        : Async<unit> =
        async {
            let! classifications = classifier.Classify entityName
            let byPath = classifications |> List.map (fun c -> c.FieldPath, c) |> Map.ofList

            for path in writtenFieldPaths do
                match Map.tryFind path byPath with
                | None -> ()
                | Some c ->
                    try
                        do!
                            audit.Record(
                                auditScope ctx,
                                AuditEvent.ClassifiedFieldWritten {
                                    UserId = ctx.UserId
                                    EntityName = c.EntityName
                                    FieldPath = c.FieldPath
                                    Level = ClassificationLevel.name c.Level
                                }
                            )
                    with _ ->
                        ()
        }

// ─── Phase 188 — field-classification egress / DLP gate ────────────────
//
// Phase 41's `ClassificationGate` redacts on *read*. This extends the
// same policy shape to a *terminal egress* boundary — export payload, RPC
// response, audit / log sink — so a classified field cannot leave the
// process unredacted by a path that bypassed the read gate (the DLP half
// the classification work implied but never landed). The default policy
// is **permissive** (`Allow` for every level): a deployment that ships no
// deny rules behaves byte-for-byte as before (GP 13). Every non-`Allow`
// decision emits one `EgressBlocked` audit row (GP 12 — a deny is
// observable, never silent).

/// Phase 188 — the output boundary a classified field is leaving.
/// Recorded on the `EgressBlocked` audit row so a reviewer can tell an
/// export leak from an RPC-response leak. `CustomBoundary` names a bespoke
/// sink without extending the DU.
[<RequireQualifiedAccess>]
type EgressBoundary =
    | ExportPayload
    | RpcResponse
    | AuditSink
    | LogSink
    | CustomBoundary of label: string

module EgressBoundary =
    /// Stable label for the `EgressBlocked` audit payload.
    let name =
        function
        | EgressBoundary.ExportPayload -> "ExportPayload"
        | EgressBoundary.RpcResponse -> "RpcResponse"
        | EgressBoundary.AuditSink -> "AuditSink"
        | EgressBoundary.LogSink -> "LogSink"
        | EgressBoundary.CustomBoundary label -> label

/// Phase 188 — the egress decision for one classification level. Distinct
/// from `ClassificationDecision` (the read gate) because egress adds a
/// hard `Block` (drop the field entirely) alongside `Redact` (placeholder
/// substitution). `[<RequireQualifiedAccess>]` keeps `Allow` / `Redact`
/// from colliding with `ClassificationDecision`'s unqualified cases in the
/// open `ToolUp.Platform` namespace.
[<RequireQualifiedAccess>]
type EgressDecision =
    | Allow
    | Redact
    | Block

/// Phase 188 — the output-boundary context an `EgressPolicy` decides
/// against. Carries who/what the egress is destined for + which boundary,
/// so a policy can (e.g.) allow `Financial` to an internal RPC peer but
/// block it on an external export.
type EgressContext = {
    /// The boundary the field is leaving.
    Boundary: EgressBoundary
    /// Who/what the egress is destined for — a recipient user id, peer id,
    /// or sink name. Audit attribution on the `EgressBlocked` row.
    Actor: string
    /// Optional concrete destination label (sink name, peer URL, file
    /// path). `None` when unspecified.
    Destination: string option
}

module EgressContext =
    /// Context for `boundary` destined for `actor`, no explicit
    /// destination.
    let create (boundary: EgressBoundary) (actor: string) : EgressContext = {
        Boundary = boundary
        Actor = actor
        Destination = None
    }

    let withDestination (destination: string) (ctx: EgressContext) = {
        ctx with
            Destination = Some destination
    }

/// Phase 188 — maps a classification level + egress context to a decision.
/// Deployments supply their own deny rules; the SDK ships
/// `EgressGate.permissiveEgressPolicy` (`Allow` for every level).
type EgressPolicy = ClassificationLevel -> EgressContext -> EgressDecision

/// Phase 188 — the terminal egress enforcement seam. Acts on the Phase 41
/// `FieldClassification` tags at an output boundary, redacting / dropping
/// classified fields per the policy and auditing every non-`Allow`
/// decision.
///
/// **Six portability rules (GP 12).** Identity by value (strings + the
/// immutable `FieldClassification` / `EgressContext` records); async at
/// the boundary; no failure channel beyond the returned map (a permissive
/// policy is a pure pass-through); stateless between calls (classifier +
/// audit resolve per call); no cross-shard ordering; no timing primitive.
type IEgressGate =
    /// Apply the gate to a flat `fieldPath -> value` view of `entityName`
    /// leaving via `ctx`. Classified fields whose policy decision is
    /// `Redact` are replaced with the redaction placeholder; `Block` drops
    /// them from the result entirely; `Allow` (and every unclassified
    /// field) passes through untouched. Emits one `EgressBlocked` audit row
    /// per non-`Allow` decision.
    abstract Apply: ctx: EgressContext * entityName: string * fields: Map<string, string> -> Async<Map<string, string>>

/// Phase 188 — composes a classifier + an `EgressPolicy` + audit into the
/// terminal egress gate. Field-level: classified fields the policy denies
/// at egress are redacted (`Redact`) or dropped (`Block`); every non-`Allow`
/// decision emits a value-free `EgressBlocked` audit under
/// `_platform.classification`. Sector-agnostic — acts on the neutral
/// `ClassificationLevel`, never on domain field names.
module EgressGate =

    /// Placeholder the gate substitutes for a `Redact`-ed field — matches
    /// the read gate's `ClassificationGate.RedactedPlaceholder`.
    [<Literal>]
    let RedactedPlaceholder = "[redacted]"

    /// SDK default policy — permissive: every level egresses unchanged, so
    /// a deployment that ships no deny rules behaves byte-for-byte as it
    /// did before the gate existed (GP 13). Deny rules are opt-in: supply
    /// an `EgressPolicy` returning `Redact` / `Block` for the levels +
    /// boundaries a deployment's DLP posture requires.
    let permissiveEgressPolicy: EgressPolicy = fun _ _ -> EgressDecision.Allow

    /// Scope under which egress audit is recorded — the reserved
    /// `_platform` cross-tenant audit scope. Egress happens outside a
    /// per-request config scope; the `Actor` + `Destination` on the
    /// payload carry attribution.
    [<Literal>]
    let private auditScope = "_platform"

    let private recordEgress (audit: IAuditLog) (ctx: EgressContext) (c: FieldClassification) (decisionLabel: string) = async {
        try
            do!
                audit.Record(
                    auditScope,
                    AuditEvent.EgressBlocked {
                        Actor = ctx.Actor
                        EntityName = c.EntityName
                        FieldPath = c.FieldPath
                        Level = ClassificationLevel.name c.Level
                        Decision = decisionLabel
                        Boundary = EgressBoundary.name ctx.Boundary
                        Destination = ctx.Destination
                    }
                )
        with _ ->
            // IAuditLog.Record is best-effort; swallow per contract.
            ()
    }

    /// Core filter — apply `policy` to every classified field in `fields`
    /// at `ctx`, emitting audit for non-`Allow` decisions. A pure
    /// pass-through under `permissiveEgressPolicy` (zero redaction, zero
    /// audit). Shared by the binding points below.
    let apply
        (classifier: IFieldClassifier)
        (policy: EgressPolicy)
        (audit: IAuditLog)
        (ctx: EgressContext)
        (entityName: string)
        (fields: Map<string, string>)
        : Async<Map<string, string>> =
        async {
            let! classifications = classifier.Classify entityName
            let byPath = classifications |> List.map (fun c -> c.FieldPath, c) |> Map.ofList
            let mutable result = fields

            for KeyValue(path, _) in fields do
                match Map.tryFind path byPath with
                | None -> () // unclassified — passes through
                | Some c ->
                    match policy c.Level ctx with
                    | EgressDecision.Allow -> ()
                    | EgressDecision.Redact ->
                        result <- Map.add path RedactedPlaceholder result
                        do! recordEgress audit ctx c "Redact"
                    | EgressDecision.Block ->
                        result <- Map.remove path result
                        do! recordEgress audit ctx c "Block"

            return result
        }

    /// Default `IEgressGate` over a classifier + policy + audit. Pass
    /// `permissiveEgressPolicy` for the zero-cost default, or a deny policy
    /// for an enforcing deployment.
    let make (classifier: IFieldClassifier) (policy: EgressPolicy) (audit: IAuditLog) : IEgressGate =
        { new IEgressGate with
            member _.Apply(ctx, entityName, fields) =
                apply classifier policy audit ctx entityName fields
        }

    /// Export-path binding point — apply the gate to one export segment's
    /// `fieldPath -> value` view destined for `actor`, tagging audit with
    /// `EgressBoundary.ExportPayload`. Thread this into an `IDataExporter`
    /// so a classified field cannot leave in an export the read gate never
    /// saw.
    let forExport
        (classifier: IFieldClassifier)
        (policy: EgressPolicy)
        (audit: IAuditLog)
        (actor: string)
        (entityName: string)
        (fields: Map<string, string>)
        : Async<Map<string, string>> =
        apply classifier policy audit (EgressContext.create EgressBoundary.ExportPayload actor) entityName fields

    /// RPC-response binding point — apply the gate to an outbound RPC
    /// response's `fieldPath -> value` view destined for `actor`, tagging
    /// audit with `EgressBoundary.RpcResponse`.
    let forResponse
        (classifier: IFieldClassifier)
        (policy: EgressPolicy)
        (audit: IAuditLog)
        (actor: string)
        (entityName: string)
        (fields: Map<string, string>)
        : Async<Map<string, string>> =
        apply classifier policy audit (EgressContext.create EgressBoundary.RpcResponse actor) entityName fields