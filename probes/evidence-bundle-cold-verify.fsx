// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

// ─── Cold verification of an evidence bundle ────────────────────────────
//
//   dotnet fsi probes/evidence-bundle-cold-verify.fsx <bundle.dsse.json>
//
// The claim this probe exists to make good: a party who holds nothing but
// the document can check it. So it takes nothing else — no built SDK, no
// NuGet package, no restore, no composed deployment, no key, no network.
// It `#load`s three source files out of the shared tier and reads the wire
// format with the BCL, and that is the whole of its input.
//
// **Run it cold to mean anything.** Point `NUGET_PACKAGES` at an empty
// directory and run it with no deployment up:
//
//   $env:NUGET_PACKAGES = "<a fresh empty directory>"
//   dotnet fsi probes/evidence-bundle-cold-verify.fsx <bundle.dsse.json>
//
// The output must be byte-identical to what the shipped verify command
// prints over the same document. It is, because the report is rendered by
// the shared tier — the same function, over the same values — and nothing
// in it reads a clock, a store or a key.
//
// **The reading path here is deliberately INDEPENDENT.** The wire shape
// is re-derived from the document rather than deserialised through the
// SDK's converter set, so a defect in that converter cannot make this
// probe agree with the exporter. The same discipline the envelope family's
// own reference-vector tests apply, and for the same reason: a check that
// shares an implementation with the thing it checks is not a second
// opinion.

#load "../src/ToolUp.Platform.Core/Shared/AuthAttributes.fs"
#load "../src/ToolUp.Platform.Core/Shared/DeploymentVerification.fs"
#load "../src/ToolUp.Platform.Core/Shared/Types/EvidenceChainTypes.fs"

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform

/// The bundle predicate type, restated here rather than imported: the
/// exporter that publishes it lives in the server tier, which this probe
/// deliberately does not have.
let PredicateType = "https://toolup-forge.io/attestations/evidence-chain-bundle/v1"

let InTotoPayloadType = "application/vnd.in-toto+json"
let StatementType = "https://in-toto.io/Statement/v1"

let sha256Hex (canonical: string) =
    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes canonical))

// ── reading the wire shape by hand ──────────────────────────────────────

let private str (node: JsonNode) = node.GetValue<string>()

/// One DU case's payload, tolerating both shapes a single-field case can
/// take on the wire (a bare value, or a one-element array).
let private caseFields (node: JsonNode) : string list =
    match node with
    | :? JsonArray as arr -> arr |> Seq.map str |> List.ofSeq
    | value -> [ str value ]

let private readLink (node: JsonNode) : EvidenceLink =
    let object' = node.AsObject()
    let name = object' |> Seq.head
    let fields = caseFields name.Value

    let at index =
        if List.length fields > index then fields[index] else ""

    match name.Key with
    | "Linked" -> EvidenceLink.Linked(at 0, at 1)
    | "LinkAbsent" -> EvidenceLink.LinkAbsent(at 0)
    | "LinkBroken" -> EvidenceLink.LinkBroken(at 0, at 1)
    | "LinkWithheld" -> EvidenceLink.LinkWithheld(at 0)
    | other -> failwithf "unknown evidence link case on the wire: %s" other

let private readOutcome (node: JsonNode) : EvidenceChainOutcome =
    match str node with
    | "ChainUnrecorded" -> EvidenceChainOutcome.ChainUnrecorded
    | "ChainComplete" -> EvidenceChainOutcome.ChainComplete
    | "ChainPartial" -> EvidenceChainOutcome.ChainPartial
    | "ChainBroken" -> EvidenceChainOutcome.ChainBroken
    | other -> failwithf "unknown chain outcome on the wire: %s" other

let private readBound (node: JsonNode) : EnumerationBound =
    { Hop = str node["Hop"]
      Bound = str node["Bound"]
      Unenumerated = node["Unenumerated"].GetValue<int>() }

let private readList (node: JsonNode) : JsonNode list =
    match node with
    | null -> []
    | :? JsonArray as arr -> arr |> List.ofSeq
    | _ -> []

let private readStrings (node: JsonNode) : string list = readList node |> List.map str

let private readTime (node: JsonNode) =
    DateTime.Parse(str node, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)

let private readOptional (node: JsonNode) =
    match node with
    | null -> None
    | value -> Some(str value)

let private readPosition (node: JsonNode) : EnumerationPosition =
    { Hop = str node["Hop"]
      Kind = str node["Kind"]
      Key = str node["Key"]
      Bound = readOptional node["Bound"] }

/// The enumeration-completeness verdict, read out of the wire rather
/// than defaulted. It reaches the operator-facing report through the
/// chain's render, so a probe that guessed at it would produce text the
/// warm run does not — which is exactly the drift this probe exists to
/// rule out.
let private readEnumeration (node: JsonNode) : EnumerationCompleteness =
    match node with
    | :? JsonObject as object' ->
        let name = object' |> Seq.head

        match name.Key with
        // A SINGLE-field case carries its field's value directly, so the
        // list of bounds is the case payload rather than the first
        // element of one. A MULTI-field case carries its fields as an
        // array. Reading both the same way silently produced an empty
        // bound list, and an empty one still labels `bounded` — so the
        // defect would have surfaced as a report whose text was subtly
        // short rather than as a failure.
        | "Bounded" -> EnumerationCompleteness.Bounded(readList name.Value |> List.map readBound)
        | "Incomplete" ->
            let fields = readList name.Value

            let at index =
                if List.length fields > index then fields[index] else null

            EnumerationCompleteness.Incomplete(readList (at 0) |> List.map readPosition, str (at 1))
        | other -> failwithf "unknown enumeration completeness case on the wire: %s" other
    | value ->
        match str value with
        | "Complete" -> EnumerationCompleteness.Complete
        | other -> failwithf "unknown enumeration completeness case on the wire: %s" other

let private readBundle (predicate: JsonNode) : EvidenceBundle =
    let chain = predicate["Chain"]

    let hops =
        readList chain["Hops"]
        |> List.map (fun hop ->
            { Id = str hop["Id"]
              Title = str hop["Title"]
              Ordinal = hop["Ordinal"].GetValue<int>()
              Link = readLink hop["Link"]
              Findings = readStrings hop["Findings"] })

    { SchemaVersion = predicate["SchemaVersion"].GetValue<int>()
      NestedAttestationDisposition = str predicate["NestedAttestationDisposition"]
      Observer = str predicate["Observer"]
      ObservedAt = readTime predicate["ObservedAt"]
      Chain =
        { SchemaVersion = chain["SchemaVersion"].GetValue<int>()
          Actor = str chain["Actor"]
          WalkedAt = readTime chain["WalkedAt"]
          Hops = hops
          Outcome = readOutcome chain["Outcome"]
          VerdictDigest = str chain["VerdictDigest"]
          Enumeration = readEnumeration chain["Enumeration"] }
      NotProved =
        readList predicate["NotProved"]
        |> List.map (fun statement ->
            { Id = str statement["Id"]
              Statement = str statement["Statement"]
              Narrowing = readOptional statement["Narrowing"] })
      Qualifiers =
        readList predicate["Qualifiers"]
        |> List.map (fun qualifier ->
            { Id = str qualifier["Id"]
              Verdict = str qualifier["Verdict"]
              Detail = str qualifier["Detail"] })
      ContentId = str predicate["ContentId"] }

// ── the document-level checks, at the same positions ────────────────────

let private verifyDocument (json: string) : BundleIntegrity * EvidenceBundle option =
    try
        let envelope = JsonNode.Parse json
        let payloadType = str envelope["payloadType"]

        if payloadType <> InTotoPayloadType then
            BundleIntegrity.BrokenAt(
                "document/payloadType",
                $"the envelope declares payload type '{payloadType}' where an in-toto statement is '{InTotoPayloadType}'"
            ),
            None
        else
            let statement =
                JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(str envelope["payload"])))

            if str statement["_type"] <> StatementType then
                BundleIntegrity.BrokenAt(
                    "document/statement",
                    $"""malformed envelope: unsupported statement type: {str statement["_type"]}"""
                ),
                None
            else
                let declared = str statement["predicateType"]

                if declared <> PredicateType then
                    BundleIntegrity.BrokenAt(
                        "document/predicateType",
                        $"the statement declares predicate type '{declared}', which is not the evidence-bundle type '{PredicateType}' — a reader is told what it is holding rather than what it is not"
                    ),
                    None
                else
                    let bundle = readBundle statement["predicate"]

                    match EvidenceBundle.verifyWith sha256Hex bundle with
                    | BundleIntegrity.BrokenAt(position, reason) ->
                        BundleIntegrity.BrokenAt(position, reason), Some bundle
                    | BundleIntegrity.Intact ->
                        let published =
                            readList statement["subject"]
                            |> List.collect (fun s ->
                                s["digest"].AsObject() |> Seq.map (fun kv -> str kv.Value) |> List.ofSeq)

                        if published |> List.contains bundle.ContentId then
                            BundleIntegrity.Intact, Some bundle
                        else
                            BundleIntegrity.BrokenAt(
                                "document/subject",
                                $"""the statement publishes subject digest(s) '{published |> String.concat ", "}' and the bundle inside it is addressed '{bundle.ContentId}' — a correctly-shaped statement about a different bundle"""
                            ),
                            Some bundle
    with ex ->
        BundleIntegrity.BrokenAt("document/envelope", $"the DSSE envelope could not be read: {ex.Message}"), None

// ── run ─────────────────────────────────────────────────────────────────

let private arguments =
    Environment.GetCommandLineArgs()
    |> Array.skipWhile (fun a -> not (a.EndsWith ".fsx"))
    |> Array.skip 1

match arguments with
| [| path |] ->
    let integrity, bundle = verifyDocument (File.ReadAllText path)
    // Written with `Console.Out.Write` rather than `printfn`, so the
    // report's own trailing newline is the only one: the warm side
    // writes the same string to a file, and a byte comparison is the
    // point of the exercise.
    Console.Out.Write(EvidenceBundle.verificationReport integrity bundle)
    exit (if BundleIntegrity.isIntact integrity then 0 else 1)
| _ ->
    eprintfn "usage: dotnet fsi probes/evidence-bundle-cold-verify.fsx <bundle.dsse.json>"
    exit 2