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