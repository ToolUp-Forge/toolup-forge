module ToolUp.Platform.Tests.Contracts.IEgressGateContract

open System.Collections.Generic
open Expecto
open ToolUp.Platform

// Phase 188 — contract for the field-classification egress / DLP gate.
// Proves the three load-bearing properties from the phase acceptance:
//   * permissive default ⇒ byte-for-byte pass-through, zero audit noise;
//   * an opt-in deny rule redacts / blocks exactly the matching level;
//   * every non-Allow decision emits exactly one EgressBlocked audit row.

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

let private classifierFor (classifications: FieldClassification list) : IFieldClassifier =
    DefaultFieldClassifier.create classifications

// "Customer" entity: Email = Pii, Balance = Financial, DisplayName = Public.
let private customerClassifications = [
    FieldClassification.create "Customer" "Email" Pii
    FieldClassification.create "Customer" "Balance" Financial
    FieldClassification.create "Customer" "DisplayName" Public
]

let private customerFields =
    Map.ofList [
        "Email", "a@b.com"
        "Balance", "1234.50"
        "DisplayName", "Ada"
        "Unclassified", "x"
    ]

/// Only the egress payloads from a recorded audit trail.
let private egressBlocks (audit: RecordingAuditLog) =
    audit.Events
    |> List.choose (
        snd
        >> function
            | EgressBlocked p -> Some p
            | _ -> None
    )

let tests =
    testList "EgressGate — IEgressGate contract" [
        testCaseAsync "Permissive default passes every field through unchanged"
        <| async {
            let audit = RecordingAuditLog()

            let gate =
                EgressGate.make (classifierFor customerClassifications) EgressGate.permissiveEgressPolicy audit

            let ctx = EgressContext.create EgressBoundary.ExportPayload "recipient-1"

            let! result = gate.Apply(ctx, "Customer", customerFields)

            Expect.equal result customerFields "permissive default is a pure pass-through (GP 13)"
        }

        testCaseAsync "Permissive default emits zero audit rows"
        <| async {
            let audit = RecordingAuditLog()

            let gate =
                EgressGate.make (classifierFor customerClassifications) EgressGate.permissiveEgressPolicy audit

            let ctx = EgressContext.create EgressBoundary.ExportPayload "recipient-1"

            let! _ = gate.Apply(ctx, "Customer", customerFields)

            Expect.isEmpty audit.Events "no decision is non-Allow ⇒ no audit noise"
        }

        testCaseAsync "Block rule drops exactly the matching level + audits once"
        <| async {
            let audit = RecordingAuditLog()
            // Deny rule: Block Pii on export, Allow everything else.
            let policy: EgressPolicy =
                fun level _ ->
                    if level = Pii then
                        EgressDecision.Block
                    else
                        EgressDecision.Allow

            let gate = EgressGate.make (classifierFor customerClassifications) policy audit
            let ctx = EgressContext.create EgressBoundary.ExportPayload "recipient-1"

            let! result = gate.Apply(ctx, "Customer", customerFields)

            Expect.isFalse (result.ContainsKey "Email") "Pii field dropped from egress"
            Expect.equal (result.TryFind "Balance") (Some "1234.50") "Financial untouched (Allow)"
            Expect.equal (result.TryFind "DisplayName") (Some "Ada") "Public untouched"
            Expect.equal (result.TryFind "Unclassified") (Some "x") "unclassified passes through"

            let blocks = egressBlocks audit
            Expect.hasLength blocks 1 "exactly one EgressBlocked row"
            Expect.equal blocks.Head.FieldPath "Email" "row names the blocked field"
            Expect.equal blocks.Head.Level "Pii" "row carries the classification level"
            Expect.equal blocks.Head.Decision "Block" "row records the Block decision"
            Expect.equal blocks.Head.Boundary "ExportPayload" "row records the egress boundary"
            Expect.equal blocks.Head.Actor "recipient-1" "row attributes the recipient"
        }

        testCaseAsync "Redact rule replaces the value with the placeholder + audits once"
        <| async {
            let audit = RecordingAuditLog()

            let policy: EgressPolicy =
                fun level _ ->
                    if level = Financial then
                        EgressDecision.Redact
                    else
                        EgressDecision.Allow

            let gate = EgressGate.make (classifierFor customerClassifications) policy audit
            let ctx = EgressContext.create EgressBoundary.RpcResponse "peer-x"

            let! result = gate.Apply(ctx, "Customer", customerFields)

            Expect.equal (result.TryFind "Balance") (Some EgressGate.RedactedPlaceholder) "Financial value redacted"
            Expect.equal (result.TryFind "Email") (Some "a@b.com") "Pii untouched (Allow)"

            let blocks = egressBlocks audit
            Expect.hasLength blocks 1 "exactly one EgressBlocked row"
            Expect.equal blocks.Head.Decision "Redact" "row records the Redact decision"
            Expect.equal blocks.Head.Boundary "RpcResponse" "row records the RPC boundary"
        }

        testCaseAsync "Destination label flows onto the audit row"
        <| async {
            let audit = RecordingAuditLog()
            let policy: EgressPolicy = fun _ _ -> EgressDecision.Block

            let gate = EgressGate.make (classifierFor customerClassifications) policy audit

            let ctx =
                EgressContext.create (EgressBoundary.CustomBoundary "datadog") "sink"
                |> EgressContext.withDestination "https://http-intake.example/v1/input"

            let! _ = gate.Apply(ctx, "Customer", Map.ofList [ "Email", "a@b.com" ])

            let blocks = egressBlocks audit
            Expect.hasLength blocks 1 "one classified field blocked"
            Expect.equal blocks.Head.Boundary "datadog" "custom boundary label preserved"
            Expect.equal blocks.Head.Destination (Some "https://http-intake.example/v1/input") "destination recorded"
        }

        testCaseAsync "forExport binding point tags the ExportPayload boundary"
        <| async {
            let audit = RecordingAuditLog()
            let policy: EgressPolicy = fun _ _ -> EgressDecision.Block

            let! _ =
                EgressGate.forExport
                    (classifierFor customerClassifications)
                    policy
                    audit
                    "recipient-1"
                    "Customer"
                    (Map.ofList [ "Email", "a@b.com" ])

            let blocks = egressBlocks audit
            Expect.equal blocks.Head.Boundary "ExportPayload" "forExport stamps the export boundary"
        }
    ]