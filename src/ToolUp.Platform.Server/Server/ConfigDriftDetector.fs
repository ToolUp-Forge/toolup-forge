module ToolUp.Platform.ConfigDriftDetector

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── Phase 9q — startup-time config drift detector ───────────────────
//
// At `compose` end, serialises the resolved `ServerConfig` (secrets
// redacted) plus a hash of the active companion-assembly set,
// compares against the previous startup's persisted snapshot at
// `_platform/_deploy/last-config.json`, emits a `Warn` log + a
// `ConfigDrift` audit event when the shape differs, then writes the
// new snapshot back unconditionally. Pure observation — no abort,
// no rollback. Catches "someone changed an env var without updating
// the deployment manifest" before it becomes a staging-vs-prod
// incident.
//
// **Opt-in via `ServerConfig.ConfigDriftDetection`.** Default
// `NoConfigDriftDetection` (GP 13): no read, no write, no compare.
//
// **What counts as drift.** Any field-level difference in the
// resolved `ServerConfig` (every `ServerConfig` property, recursed
// into maps / DUs / nested records); any change in the active
// companion-set hash. Adding or removing a `<PackageReference>` or
// `.Server.props`-injected companion changes the loaded
// `ToolUp.*` assembly set and surfaces as a hash flip.
//
// **What doesn't.** The snapshot timestamp (`snapshotTakenAt`) and
// any future build-commit field travel in the persisted blob but
// are not part of the comparison surface — they change on every
// restart and would drown the signal.

let private deployContainer = "_platform"
let private snapshotBlobName = "_deploy/last-config.json"

[<Literal>]
let private snapshotSchema = 1

// Redaction allowlist — property-name suffixes (case-insensitive)
// whose string values are replaced by `<redacted:length=N>` before
// persistence and comparison. `ServerConfig` itself does not
// currently carry secrets (those live in `ISecretStore`), but
// defence-in-depth covers any future field named `*ApiKey` /
// `*Token` / `*Secret` / `*Password` so a careless addition does
// not leak through the snapshot blob.
let private redactionSuffixes = [ "apikey"; "token"; "secret"; "password" ]

let private shouldRedact (propName: string) =
    let lower = propName.ToLowerInvariant()
    redactionSuffixes |> List.exists lower.EndsWith

let private redactedString (length: int) = sprintf "<redacted:length=%d>" length

// One-pass post-serialisation walk over the JSON tree: every string
// property whose name ends in a sensitive suffix is replaced with the
// redaction marker, sized to the original value's length. Non-string
// secrets (an unlikely shape, but covered) are stringified first so
// the marker sizes still convey "there was something here, this big".
//
// Snapshots property names before mutating — mutating a JsonObject
// during enumeration would raise InvalidOperationException.
let rec private redact (node: JsonNode) : unit =
    if isNull node then
        ()
    else
        match node with
        | :? JsonObject as obj ->
            let names = obj |> Seq.map (fun kvp -> kvp.Key) |> Seq.toArray

            for name in names do
                let child = obj.[name]

                if shouldRedact name then
                    if isNull child then
                        ()
                    else
                        match child.GetValueKind() with
                        | JsonValueKind.Null -> ()
                        | JsonValueKind.String ->
                            let s = child.GetValue<string>()
                            obj.[name] <- JsonValue.Create(redactedString s.Length) :> JsonNode
                        | _ ->
                            let serialised = child.ToJsonString()
                            obj.[name] <- JsonValue.Create(redactedString serialised.Length) :> JsonNode
                else
                    redact child
        | :? JsonArray as arr ->
            for child in arr do
                redact child
        | _ -> ()

let private jsonOptions = FableConverters.create ()

let private serializeConfig (config: ServerConfig) : JsonObject =
    let raw = JsonSerializer.Serialize(config, jsonOptions)
    let parsed = JsonNode.Parse raw :?> JsonObject
    redact parsed
    parsed

// Every loaded assembly whose simple name starts with `ToolUp.` is a
// candidate companion. Sorted + deduplicated by full name to keep the
// hash deterministic across the AssemblyLoadContext jitter that can
// surface in some host environments. Version is included so a NuGet
// bump on an existing companion is itself drift — operators want to
// see "Hnsw 0.3 → 0.4" land in the trail even when no config field
// changes.
let private companionSet () : string list =
    AppDomain.CurrentDomain.GetAssemblies()
    |> Array.choose (fun a ->
        let name = a.GetName()

        let simple = name.Name |> Option.ofObj |> Option.defaultValue ""

        if simple.StartsWith("ToolUp.", StringComparison.Ordinal) then
            Some(sprintf "%s:%s" simple (string name.Version))
        else
            None)
    |> Array.distinct
    |> Array.sort
    |> List.ofArray

let private hashCompanionSet (set: string list) : string =
    use sha = SHA256.Create()
    let joined = String.Join("\n", set)
    let bytes = sha.ComputeHash(Encoding.UTF8.GetBytes joined)
    bytes |> Array.map (sprintf "%02x") |> String.concat ""

let private buildSnapshot (config: ServerConfig) (set: string list) (hash: string) (now: DateTime) : JsonObject =
    let configJson = serializeConfig config

    let companions = JsonArray()

    for s in set do
        companions.Add(JsonValue.Create(s))

    let snapshot = JsonObject()
    snapshot.["schema"] <- JsonValue.Create(snapshotSchema)
    snapshot.["snapshotTakenAt"] <- JsonValue.Create(now.ToUniversalTime().ToString("o"))
    snapshot.["companionSet"] <- companions
    snapshot.["companionSetHash"] <- JsonValue.Create(hash)
    snapshot.["config"] <- configJson
    snapshot

// Render a leaf for the audit-event payload. Objects / arrays are
// stringified compactly so the audit-payload size stays bounded.
// `null` values surface as `None` so the audit consumer can tell
// "field absent" from "field present and equal to null literal".
let private renderLeaf (n: JsonNode) : string option =
    if isNull n then
        None
    else
        match n.GetValueKind() with
        | JsonValueKind.Null -> None
        | _ -> Some(n.ToJsonString())

// Diff a JsonNode pair, accumulating dotted-path changes
// (`AuditLog`, `RateLimit.RequestsPerWindow`,
// `SecurityHeaders["X-Frame-Options"]`-style paths). Walker is
// post-order so the change list reflects in-order traversal — the
// caller reverses to surface paths in document order.
let rec private diffTokens
    (path: string)
    (prev: JsonNode)
    (curr: JsonNode)
    (acc: ConfigDriftChange list)
    : ConfigDriftChange list =
    let prevKind =
        if isNull prev then
            JsonValueKind.Null
        else
            prev.GetValueKind()

    let currKind =
        if isNull curr then
            JsonValueKind.Null
        else
            curr.GetValueKind()

    if prevKind = JsonValueKind.Object && currKind = JsonValueKind.Object then
        let prevObj = prev.AsObject()
        let currObj = curr.AsObject()

        let allKeys =
            seq {
                yield! prevObj |> Seq.map (fun kvp -> kvp.Key)
                yield! currObj |> Seq.map (fun kvp -> kvp.Key)
            }
            |> Seq.distinct
            |> Seq.sort

        allKeys
        |> Seq.fold
            (fun acc key ->
                let childPath = if path = "" then key else $"{path}.{key}"
                let p = prevObj.[key]
                let c = currObj.[key]

                match isNull p, isNull c with
                | true, true -> acc
                | true, false ->
                    {
                        Path = childPath
                        From = None
                        To = renderLeaf c
                    }
                    :: acc
                | false, true ->
                    {
                        Path = childPath
                        From = renderLeaf p
                        To = None
                    }
                    :: acc
                | false, false -> diffTokens childPath p c acc)
            acc
    elif JsonNode.DeepEquals(prev, curr) then
        acc
    else
        {
            Path = path
            From = renderLeaf prev
            To = renderLeaf curr
        }
        :: acc

/// One-shot startup detection. Reads the previous snapshot (if any),
/// serialises the resolved `ServerConfig` + companion-set hash for
/// this startup, diffs the two, emits `Warn` log + `ConfigDrift`
/// audit event when differences are found, then writes the new
/// snapshot back unconditionally. Failures at any step are logged
/// at `Warn` and swallowed — drift detection is a diagnostic aid,
/// not a control-plane gate.
let run (storage: IBlobStorage) (auditLog: IAuditLog) (logger: ILogger) (config: ServerConfig) : Async<unit> = async {
    try
        let now = DateTime.UtcNow
        let set = companionSet ()
        let hash = hashCompanionSet set
        let newSnapshot = buildSnapshot config set hash now

        let indentedOptions = JsonSerializerOptions(WriteIndented = true)

        let newSnapshotBytes =
            newSnapshot.ToJsonString(indentedOptions) |> Encoding.UTF8.GetBytes

        let! previousResult = storage.Download(deployContainer, snapshotBlobName)

        match previousResult with
        | Ok bytes ->
            try
                let prevSnapshot = JsonNode.Parse(Encoding.UTF8.GetString bytes) :?> JsonObject
                let prevConfig = prevSnapshot.["config"]
                let currConfig = newSnapshot.["config"]

                let prevHash =
                    prevSnapshot.["companionSetHash"]
                    |> Option.ofObj
                    |> Option.map (fun n -> n.GetValue<string>())

                let changes =
                    if prevConfig <> null && currConfig <> null then
                        diffTokens "" prevConfig currConfig [] |> List.rev
                    else
                        []

                let companionChanged = prevHash <> Some hash

                if not (List.isEmpty changes) || companionChanged then
                    let pathSummary =
                        changes |> List.map _.Path |> List.truncate 20 |> String.concat ", "

                    let companionNote =
                        if companionChanged then
                            " companion-set hash changed"
                        else
                            ""

                    logger.Warn(
                        sprintf
                            "Config drift detected at startup: %d field-level change(s)%s. Paths: [%s]"
                            changes.Length
                            companionNote
                            pathSummary
                    )

                    do!
                        auditLog.Record(
                            "_platform",
                            ConfigDrift {
                                Changes = changes
                                CompanionSetFrom = prevHash
                                CompanionSetTo = hash
                                SnapshotTakenAt = now
                            }
                        )
            with ex ->
                // Unparseable previous snapshot — log + overwrite.
                // Don't emit `ConfigDrift` for a recoverable read
                // failure; the operator's signal is "drift", not
                // "the prior file was malformed".
                logger.Warn(
                    sprintf "Config drift detector: previous snapshot could not be parsed (%s); rewriting." ex.Message
                )
        | Error _ ->
            // First run on this deployment — write the snapshot,
            // no diff. The detector is intentionally silent on the
            // first run so a fresh deploy doesn't ship a synthetic
            // "everything changed" event.
            ()

        let! writeResult = storage.Upload(deployContainer, snapshotBlobName, newSnapshotBytes)

        match writeResult with
        | Ok _ -> ()
        | Error e -> logger.Warn(sprintf "Config drift detector: snapshot persist failed: %s" e)

    with ex ->
        logger.Warn(sprintf "Config drift detector: skipped due to error: %s" ex.Message)
}