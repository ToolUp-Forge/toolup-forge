// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Audit

open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform

/// Per-classification access decision for a single field.
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

/// Composes a classifier + a policy + audit into the
/// read/write gate. Field-level: classified fields a caller may not read
/// are redacted (or the deployment refuses the call); classified reads /
/// writes emit value-free audit under `_platform.classification`.
///
/// Composes with RBAC — the default policy treats
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
    /// config scope's audit stream (`StorageScope.auditScope` — its bare id,
    /// never its container) when resolvable, else the platform's trail.
    let private auditScope (ctx: AccessContext) : AuditScope =
        AccessContext.configScope ctx
        |> Option.map StorageScope.auditScope
        |> Option.defaultValue AuditScope.platform

    let private recordRead (audit: IAuditLog) (ctx: AccessContext) (c: FieldClassification) (redacted: bool) =
        if c.AuditOnRead then
            AuditRow.emit
                audit
                (auditScope ctx)
                (AuditSubject.fromSubject ctx.Subject)
                (AuditEvent.ClassifiedFieldRead {
                    UserId = UserId ctx.UserId
                    EntityName = c.EntityName
                    FieldPath = c.FieldPath
                    Level = ClassificationLevel.name c.Level
                    Redacted = redacted
                })
        else
            async { return Ok() }

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
        : Async<Result<Map<string, string>, AuditWriteError>> =
        async {
            let! classifications = classifier.Classify entityName
            let byPath = classifications |> List.map (fun c -> c.FieldPath, c) |> Map.ofList

            // The read is served only once every row it owes has landed (or
            // the policy said a lost row does not refuse): the first refused
            // row refuses the read.
            let rec walk (pending: (string * string) list) (result: Map<string, string>) = async {
                match pending with
                | [] -> return Ok result
                | (path, _) :: rest ->
                    match Map.tryFind path byPath with
                    | None -> return! walk rest result // unclassified — passes through
                    | Some c ->
                        let redacted = decide policy c ctx = Redact

                        match! recordRead audit ctx c redacted with
                        | Error e -> return Error e
                        | Ok() ->
                            let next =
                                if redacted then
                                    Map.add path RedactedPlaceholder result
                                else
                                    result

                            return! walk rest next
            }

            return! walk (Map.toList fields) fields
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
        : Async<Result<unit, AuditWriteError>> =
        async {
            let! classifications = classifier.Classify entityName
            let byPath = classifications |> List.map (fun c -> c.FieldPath, c) |> Map.ofList

            let rec walk (pending: string list) = async {
                match pending with
                | [] -> return Ok()
                | path :: rest ->
                    match Map.tryFind path byPath with
                    | None -> return! walk rest
                    | Some c ->
                        match!
                            AuditRow.emit
                                audit
                                (auditScope ctx)
                                (AuditSubject.fromSubject ctx.Subject)
                                (AuditEvent.ClassifiedFieldWritten {
                                    UserId = UserId ctx.UserId
                                    EntityName = c.EntityName
                                    FieldPath = c.FieldPath
                                    Level = ClassificationLevel.name c.Level
                                })
                        with
                        | Error e -> return Error e
                        | Ok() -> return! walk rest
            }

            return! walk writtenFieldPaths
        }


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

    /// The boundary a tool result crosses on its way into a
    /// model's context. A `CustomBoundary`, so no consumer's exhaustive
    /// match over the type gains a case.
    let aiToolResult = EgressBoundary.CustomBoundary "AiToolResult"

/// The egress decision for one classification level. Distinct
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

/// The output-boundary context an `EgressPolicy` decides
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
    /// The disclosure label of the payload these fields are
    /// leaving in: the set of policy refs its lineage carries, or
    /// `Unlabelled` when no lineage was computed for it.
    ///
    /// It rides the CONTEXT rather than widening `EgressPolicy`'s own
    /// signature, which is why that type alias is unchanged:
    /// the boundary, the actor and the label are all facts about
    /// the same crossing, and a deployment's existing policy function
    /// keeps compiling while gaining access to a fourth fact it may now
    /// read. `create` leaves it `Unlabelled`, so a caller that says
    /// nothing behaves exactly as it did (GP 11).
    Label: EgressLabel
}

module EgressContext =
    /// Context for `boundary` destined for `actor`, no explicit
    /// destination, no computed payload label.
    let create (boundary: EgressBoundary) (actor: string) : EgressContext = {
        Boundary = boundary
        Actor = actor
        Destination = None
        Label = EgressLabel.unlabelled
    }

    let withDestination (destination: string) (ctx: EgressContext) = {
        ctx with
            Destination = Some destination
    }

    /// Record the payload's computed disclosure label on the
    /// crossing, so a policy can decide on WHAT is leaving and not only
    /// on where it is going.
    let withLabel (label: EgressLabel) (ctx: EgressContext) = { ctx with Label = label }

/// Maps a classification level + egress context to a decision.
/// Deployments supply their own deny rules; the SDK ships
/// `EgressGate.permissiveEgressPolicy` (`Allow` for every level).
type EgressPolicy = ClassificationLevel -> EgressContext -> EgressDecision

/// The terminal egress enforcement seam. Acts on the
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

/// A JSON result after the gate: the text to send on, and the
/// fields the gate refused, as `<entity>.<fieldPath>`. `Withheld` is empty
/// exactly when `Text` is the input, byte for byte.
type GatedJson = { Text: string; Withheld: string list }

/// Composes a classifier + an `EgressPolicy` + audit into the
/// terminal egress gate. Field-level: classified fields the policy denies
/// at egress are redacted (`Redact`) or dropped (`Block`); every non-`Allow`
/// decision emits a value-free `EgressBlocked` audit under
/// `_platform.classification`. Sector-agnostic — acts on the neutral
/// `ClassificationLevel`, never on domain field names.
module EgressGate =

    /// Placeholder the gate substitutes for a `Redact`-ed field — matches
    /// the read gate's `ClassificationGate.RedactedPlaceholder`.
    [<Literal>]
    let RedactedPlaceholder = ClassificationGate.RedactedPlaceholder

    /// SDK default policy — permissive: every level egresses unchanged, so
    /// a deployment that ships no deny rules behaves byte-for-byte as it
    /// did before the gate existed (GP 13). Deny rules are opt-in: supply
    /// an `EgressPolicy` returning `Redact` / `Block` for the levels +
    /// boundaries a deployment's DLP posture requires.
    let permissiveEgressPolicy: EgressPolicy = fun _ _ -> EgressDecision.Allow

    /// The policy the compose chain applies when a deployment
    /// names none and a classification is declared. A declaration that
    /// governs nothing is the defect the seam exists to close, so this is
    /// not `permissiveEgressPolicy`: a sensitive field
    /// (`ClassificationLevel.isSensitive`) does not reach a model, and
    /// every other crossing is allowed. A deployment that declares no
    /// classification never meets it. `ServerApp.withClassificationEgressPolicy`
    /// replaces it whole.
    let declaredDefaultPolicy: EgressPolicy =
        fun level ctx ->
            if
                ctx.Boundary = EgressBoundary.aiToolResult
                && ClassificationLevel.isSensitive level
            then
                EgressDecision.Block
            else
                EgressDecision.Allow

    /// Refuse a classified field whose payload's label does
    /// not CLEAR egress, then defer to `inner`.
    ///
    /// The classification-gate twin of `EgressPolicy.requireClearedLabel`,
    /// holding the same bar at this boundary for the same reason: under
    /// a profile that makes declaration mandatory, neither a value whose
    /// lineage was never computed nor one still carrying uncleared
    /// policy refs may cross on the strength of its field classification
    /// alone. A value a declassification routine cleared arrives as
    /// `EgressLabel.clean` and passes.
    ///
    /// The refusal is `Block` rather than `Redact`, for the reason the
    /// webhook contract suppresses rather than marks — a reader outside
    /// the trust boundary learns nothing from a placeholder it should
    /// not have been told about — and `Block` already emits the
    /// `EgressBlocked` audit row, so the refusal is typed and audited
    /// with no new machinery.
    ///
    /// UNCLASSIFIED fields are untouched: `apply` never consults the
    /// policy for them, so this narrows only what was already
    /// classified. A deployment that does not compose it is unchanged
    /// (GP 11) — it is a decorator a verified composition opts into,
    /// never a new default.
    let requireClearedLabel (inner: EgressPolicy) : EgressPolicy =
        fun level ctx ->
            if EgressLabel.isClean ctx.Label then
                inner level ctx
            else
                EgressDecision.Block

    /// Scope under which egress audit is recorded — the reserved
    /// `_platform` cross-tenant audit scope. Egress happens outside a
    /// per-request config scope; the `Actor` + `Destination` on the
    /// payload carry attribution.
    let private auditScope = AuditScope.platform

    /// The row records a decision already taken — the field is blocked or
    /// redacted whether or not the row lands — so it is best-effort.
    let private recordEgress (audit: IAuditLog) (ctx: EgressContext) (c: FieldClassification) (decisionLabel: string) =
        AuditRow.emit
            audit
            auditScope
            (AuditSubject.system "EgressGate")
            (AuditEvent.EgressBlocked {
                Actor = ctx.Actor
                EntityName = c.EntityName
                FieldPath = c.FieldPath
                Level = ClassificationLevel.name c.Level
                Decision = decisionLabel
                Boundary = EgressBoundary.name ctx.Boundary
                Destination = ctx.Destination
            })
        |> AuditRow.bestEffort

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

    /// A field name as the JSON door matches it: a tool author's `orderId`
    /// or `order_id` is the declared `OrderId`.
    let private normaliseFieldPath (path: string) =
        path.Replace("_", "").Replace("-", "").ToLowerInvariant()

    /// Every property at or below `node`, with its path relative to `node`.
    let rec private properties (prefix: string) (node: JsonObject) : (string * JsonObject * string) list = [
        for KeyValue(key, value) in node do
            let path = if prefix = "" then key else $"{prefix}.{key}"
            yield path, node, key

            match value with
            | :? JsonObject as child -> yield! properties path child
            | _ -> ()
    ]

    let private valueText (parent: JsonObject) (key: string) =
        match parent[key] with
        | null -> "null"
        | value -> value.ToJsonString()

    /// Apply the registered gate to a JSON result the door
    /// knows only the candidate entities of. Every object in the document
    /// is read as a possible row of each entity: a property whose path
    /// matches a declared field path (ignoring case and `_` / `-`) goes
    /// through `gate`, which audits it, and the verdict is written back:
    /// a `Block` removes the property, a `Redact` replaces its value.
    /// `Withheld` names each field the gate refused, as
    /// `<entity>.<fieldPath>`.
    ///
    /// Text that does not parse as JSON, or that no declared field touches,
    /// comes back byte-identical. This is a field-level gate over structure,
    /// not content inspection: a value written into free text is not a
    /// field, and no gate over fields can see it.
    let applyToJson
        (classifier: IFieldClassifier)
        (gate: IEgressGate)
        (ctx: EgressContext)
        (entityNames: string list)
        (json: string)
        : Async<GatedJson> =
        async {
            let withheld = ResizeArray<string>()

            let gateObject (node: JsonObject) (entityName: string, declared: Map<string, string>) = async {
                let matches =
                    properties "" node
                    |> List.choose (fun (path, parent, key) ->
                        Map.tryFind (normaliseFieldPath path) declared
                        |> Option.map (fun fieldPath -> fieldPath, parent, key))
                    // Two entities may declare one field name; a value the
                    // first already redacted is not read, or audited, twice.
                    |> List.filter (fun (_, parent, key) ->
                        valueText parent key <> JsonSerializer.Serialize RedactedPlaceholder)

                if not (List.isEmpty matches) then
                    let fields =
                        matches
                        |> List.map (fun (fieldPath, parent, key) -> fieldPath, valueText parent key)
                        |> Map.ofList

                    let! admitted = gate.Apply(ctx, entityName, fields)

                    for fieldPath, parent, key in matches do
                        match Map.tryFind fieldPath admitted with
                        | None ->
                            parent.Remove key |> ignore
                            withheld.Add $"{entityName}.{fieldPath}"
                        | Some value when value <> fields[fieldPath] ->
                            parent[key] <- JsonValue.Create value
                            withheld.Add $"{entityName}.{fieldPath}"
                        | Some _ -> ()
            }

            let rec walk (declared: (string * Map<string, string>) list) (node: JsonNode) = async {
                match node with
                | :? JsonObject as o ->
                    for entity in declared do
                        do! gateObject o entity

                    for KeyValue(_, child) in List.ofSeq o do
                        do! walk declared child
                | :? JsonArray as a ->
                    for child in List.ofSeq a do
                        do! walk declared child
                | _ -> ()
            }

            let! declared =
                entityNames
                |> List.distinct
                |> List.map (fun entityName -> async {
                    let! classifications = classifier.Classify entityName

                    return
                        entityName,
                        classifications
                        |> List.map (fun c -> normaliseFieldPath c.FieldPath, c.FieldPath)
                        |> Map.ofList
                })
                |> Async.Sequential

            let declared = declared |> List.ofArray |> List.filter (snd >> Map.isEmpty >> not)

            let parsed =
                if List.isEmpty declared then
                    None
                else
                    try
                        Option.ofObj (JsonNode.Parse json)
                    with :? JsonException ->
                        None

            match parsed with
            | None -> return { Text = json; Withheld = [] }
            | Some root ->
                do! walk declared root

                if withheld.Count = 0 then
                    return { Text = json; Withheld = [] }
                else
                    return {
                        Text = root.ToJsonString()
                        Withheld = withheld |> Seq.distinct |> List.ofSeq
                    }
        }

/// What `ServerModule.withClassifications` declared, by module:
/// the fold the compose chain builds the registered classifier from, and how
/// a tool-result door knows which entities a module's result may carry.
type ModuleClassificationRegistry = private {
    ByModule: Map<string, FieldClassification list>
}

module ModuleClassificationRegistry =
    /// Fold each module's declarations; a module named twice concatenates.
    let ofDeclarations (declarations: (string * FieldClassification list) list) : ModuleClassificationRegistry = {
        ByModule =
            declarations
            |> List.groupBy fst
            |> List.map (fun (name, entries) -> name, entries |> List.collect snd)
            |> Map.ofList
    }

    /// True when no module declared a classification.
    let isEmpty (registry: ModuleClassificationRegistry) =
        registry.ByModule |> Map.forall (fun _ declared -> List.isEmpty declared)

    /// Every declaration in the composition, identical repeats collapsed:
    /// the list the default classifier is built from.
    let classifications (registry: ModuleClassificationRegistry) : FieldClassification list =
        registry.ByModule |> Map.toList |> List.collect snd |> List.distinct

    /// The entities `moduleName` declared classifications on.
    let entitiesOf (moduleName: string) (registry: ModuleClassificationRegistry) : string list =
        registry.ByModule
        |> Map.tryFind moduleName
        |> Option.defaultValue []
        |> List.map _.EntityName
        |> List.distinct

    /// The fields declared differently by two declarations, as
    /// `<entity>.<fieldPath>`. Resolving one silently could weaken a
    /// classification by composition order, so the compose chain refuses them.
    let conflicts (registry: ModuleClassificationRegistry) : string list =
        classifications registry
        |> List.countBy (fun c -> c.EntityName, c.FieldPath)
        |> List.filter (fun (_, count) -> count > 1)
        |> List.map (fun ((entityName, fieldPath), _) -> $"{entityName}.{fieldPath}")