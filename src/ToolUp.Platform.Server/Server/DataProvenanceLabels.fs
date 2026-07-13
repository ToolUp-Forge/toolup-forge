// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson

// --- Phase 482 -- privacy-provenance label persistence ------------------
//
// Server-side JSON encode/decode + dataset-version metadata carriage for
// `DataProvenanceLabel` (defined Fable-safe in `Platform.Core`). Kept here,
// not in Core, because System.Text.Json is not Fable-compilable (the same
// split the dataset codec observes). Labels ride in the dataset version's
// metadata sidecar under one reserved key, so the store round-trips them
// with zero new machinery and an unlabelled version carries no key at all
// (GP 13 -- byte-for-byte unchanged).

module DataProvenanceLabels =
    /// Reserved metadata key the label set is serialised under. Shares the
    /// `dataset.` prefix so `BlobDatasetStore`'s reserved-key filter keeps it
    /// out of the user-facing `Metadata` map; the store preserves it across a
    /// write so a producer can propagate labels through the metadata channel.
    [<Literal>]
    let MetadataKey = "dataset.labels"

    let private jsonOptions = FableConverters.create ()

    /// Serialise a label set to its JSON sidecar value.
    let encode (labels: DataProvenanceLabel list) : string =
        JsonSerializer.Serialize(labels, jsonOptions)

    /// Deserialise a label set from its JSON sidecar value. A malformed /
    /// foreign value decodes to `[]` (an unreadable label sidecar degrades to
    /// "unlabelled" rather than throwing on read — labels are additive
    /// metadata, never load-bearing for the row bytes).
    let decode (json: string) : DataProvenanceLabel list =
        try
            JsonSerializer.Deserialize<DataProvenanceLabel list>(json, jsonOptions)
        with _ -> []

    /// Stamp a label set into a metadata map under the reserved key. An empty
    /// set writes nothing (keeps the unlabelled version's metadata identical
    /// to pre-482). Overwrites any prior labels key — the producing executor
    /// owns the propagated set.
    let writeInto (labels: DataProvenanceLabel list) (metadata: Map<string, string>) : Map<string, string> =
        if List.isEmpty labels then
            metadata
        else
            Map.add MetadataKey (encode labels) metadata

    /// Read the label set carried in a metadata map (`[]` when absent).
    let readFrom (metadata: Map<string, string>) : DataProvenanceLabel list =
        match Map.tryFind MetadataKey metadata with
        | Some json -> decode json
        | None -> []

// --- Phase 482 -- data-provenance policy (where labels bite) ------------
//
// Opt-in policy hooks (482.C). Default OFF (GP 13): labels are carried but
// not enforced until a deployment enables the policy. When on:
//   * labelled data may only dispatch to an `Isolated` compute profile
//     (Phase 478 -- the caller passes whether the resolved target profile is
//     Isolated; forge supplies the predicate + audited denial, the 478
//     dispatcher supplies the profile);
//   * raw export of label-carrying data is refused (Phase 188 egress gate --
//     the export path calls `checkExport` before releasing the bytes).
// Both denials are typed + audited (`DatasetPolicyDenied`, GP 4 / GP 6).

/// Opt-in data-provenance policy. Default `permissive` (both off) so an
/// unconfigured deployment carries labels but enforces nothing (GP 13).
type DataProvenancePolicy = {
    /// When `true`, a label-carrying version may only dispatch to an
    /// `Isolated` compute profile (Phase 478).
    RequireIsolatedForLabelled: bool
    /// When `true`, raw export of a label-carrying version is refused (Phase
    /// 188 egress gate).
    RefuseRawExportOfLabelled: bool
}

module DataProvenancePolicy =
    /// The GP-13 default — labels carried, nothing enforced.
    let permissive: DataProvenancePolicy = {
        RequireIsolatedForLabelled = false
        RefuseRawExportOfLabelled = false
    }

    /// Both hooks enabled — labelled data is Isolated-only and non-exportable
    /// raw.
    let enforced: DataProvenancePolicy = {
        RequireIsolatedForLabelled = true
        RefuseRawExportOfLabelled = true
    }

    /// Pure dispatch check: may a version carrying `labels` dispatch to a
    /// target whose profile `targetIsIsolated`? `Ok ()` when allowed (policy
    /// off, no labels, or an Isolated target); `Error reason` otherwise.
    let checkDispatch
        (policy: DataProvenancePolicy)
        (labels: DataProvenanceLabel list)
        (targetIsIsolated: bool)
        : Result<unit, string> =
        if
            policy.RequireIsolatedForLabelled
            && not (List.isEmpty labels)
            && not targetIsIsolated
        then
            Error "labelled data may only dispatch to an Isolated compute profile"
        else
            Ok()

    /// Pure export check: may a version carrying `labels` be exported raw?
    let checkExport (policy: DataProvenancePolicy) (labels: DataProvenanceLabel list) : Result<unit, string> =
        if policy.RefuseRawExportOfLabelled && not (List.isEmpty labels) then
            Error "raw export of label-carrying data is refused by policy"
        else
            Ok()

    /// Dispatch check with an audited denial. On refusal, records a typed
    /// `DatasetPolicyDenied` row (GP 6) before returning the reason.
    let checkDispatchAudited
        (audit: IAuditLog)
        (version: DatasetVersion)
        (policy: DataProvenancePolicy)
        (targetIsIsolated: bool)
        : Async<Result<unit, string>> =
        async {
            match checkDispatch policy version.Labels targetIsIsolated with
            | Ok() -> return Ok()
            | Error reason ->
                do!
                    audit.Record(
                        version.ScopeId,
                        DatasetPolicyDenied {
                            ScopeId = version.ScopeId
                            DatasetId = version.DatasetId
                            Version = version.Version
                            Policy = "dispatch"
                            Reason = reason
                        }
                    )

                return Error reason
        }

    /// Export check with an audited denial (mirrors `checkDispatchAudited`).
    let checkExportAudited
        (audit: IAuditLog)
        (version: DatasetVersion)
        (policy: DataProvenancePolicy)
        : Async<Result<unit, string>> =
        async {
            match checkExport policy version.Labels with
            | Ok() -> return Ok()
            | Error reason ->
                do!
                    audit.Record(
                        version.ScopeId,
                        DatasetPolicyDenied {
                            ScopeId = version.ScopeId
                            DatasetId = version.DatasetId
                            Version = version.Version
                            Policy = "export"
                            Reason = reason
                        }
                    )

                return Error reason
        }