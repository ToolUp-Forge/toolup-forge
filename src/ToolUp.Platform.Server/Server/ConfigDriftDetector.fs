module ToolUp.Platform.Config.ConfigDriftDetector

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra

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
// `FeatureMode.Disabled` (GP 13): no read, no write, no compare.
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
//
// **The snapshot is a document** read and written through
// `BlobDocumentStore`, with a thin codec (indented JSON, the bytes it has
// always carried). A previous snapshot that does not decode is set aside
// and replaced, with a warning and no drift event. One that cannot be
// READ is left in place and NOT overwritten: a transient read failure used
// to read as a first run and replace the snapshot, so the drift between
// the last deploy and this one was never reported.

let private snapshotPath = DocPath.ofString "_deploy/last-config.json"

/// The snapshot as an object, written indented.
let private snapshotCodec: BlobCodec<JsonObject> = {
    Encode =
        fun snapshot ->
            snapshot.ToJsonString(JsonSerializerOptions(WriteIndented = true))
            |> Encoding.UTF8.GetBytes
    Decode =
        fun bytes ->
            try
                match JsonNode.Parse(Encoding.UTF8.GetString bytes) with
                | :? JsonObject as snapshot -> Ok snapshot
                | _ -> Error "the snapshot is not a JSON object"
            with ex ->
                Error ex.Message
}

[<Literal>]
let private snapshotSchema = 1

// Redaction allowlist — property-name suffixes (case-insensitive)
// whose string values are replaced by `<redacted:length=N>` before
// persistence and comparison. The list itself lives in
// `RedactionAllowlist`, shared with `DiagnosticBundleHandler` (9n) and
// `ApplianceSupportBundle.SuffixFloor` (488.D) — see that module's
// header for why the deliberate duplication ended at the third copy.
let private shouldRedact = RedactionAllowlist.shouldRedact

let private redactedString = RedactionAllowlist.redactedString

// One-pass post-serialisation walk over the JSON tree: every string
// property whose name ends in a sensitive suffix is replaced with the
// redaction marker, sized to the original value's length. Non-string
// secrets (an unlikely shape, but covered) are stringified first so
// the marker sizes still convey "there was something here, this big".
let rec private redact (node: JsonNode) : unit =
    if isNull node then
        ()
    else
        match node with
        | :? JsonObject as obj ->
            // IMPORTANT: System.Text.Json.Nodes.JsonObject throws
            // InvalidOperationException if mutated during enumeration.
            // Snapshot the keys first (the `names` line below), then
            // iterate the snapshot — never `for kvp in obj do … obj[k] <- …`.
            // See docs/migrations/fablejsonconverter-to-stj.md
            // "JsonNode equivalents" for the canonical pattern.
            let names = obj |> Seq.map _.Key |> Seq.toArray

            for name in names do
                let child = obj[name]

                if shouldRedact name then
                    if isNull child then
                        ()
                    else
                        match child.GetValueKind() with
                        | JsonValueKind.Null -> ()
                        | JsonValueKind.String ->
                            let s = child.GetValue<string>()
                            obj[name] <- JsonValue.Create(redactedString s.Length) :> JsonNode
                        | _ ->
                            let serialised = child.ToJsonString()
                            obj[name] <- JsonValue.Create(redactedString serialised.Length) :> JsonNode
                else
                    redact child
        | :? JsonArray as arr ->
            for child in arr do
                redact child
        | _ -> ()

let private jsonOptions = PlatformJson.options

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

let private buildSnapshot
    (config: ServerConfig)
    (set: string list)
    (hash: string)
    (grounding: string list)
    (now: DateTime)
    : JsonObject =
    let configJson = serializeConfig config

    let companions = JsonArray()

    for s in set do
        companions.Add(JsonValue.Create(s))

    // Phase 526 — the composed grounding registrations (sorted metric /
    // subject ids). A deploy-over-deploy change here is drift, exactly
    // like a config or companion-set change. Empty when nothing is
    // registered, so a grounding-free deployment's snapshot is unchanged.
    let groundingArr = JsonArray()

    for g in grounding do
        groundingArr.Add(JsonValue.Create(g))

    let snapshot = JsonObject()
    snapshot["schema"] <- JsonValue.Create(snapshotSchema)
    snapshot["snapshotTakenAt"] <- JsonValue.Create(now.ToUniversalTime().ToString("o"))
    snapshot["companionSet"] <- companions
    snapshot["companionSetHash"] <- JsonValue.Create(hash)
    snapshot["grounding"] <- groundingArr
    snapshot["config"] <- configJson
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
                yield! prevObj |> Seq.map _.Key
                yield! currObj |> Seq.map _.Key
            }
            |> Seq.distinct
            |> Seq.sort

        allKeys
        |> Seq.fold
            (fun acc key ->
                let childPath = if path = "" then key else $"{path}.{key}"
                let p = prevObj[key]
                let c = currObj[key]

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
/// snapshot back — unless the previous one could not be read, when it
/// is kept for the next startup's comparison. Failures at any step are
/// logged at `Warn` and swallowed — drift detection is a diagnostic aid,
/// not a control-plane gate.
let run
    (storage: IBlobStorage)
    (auditLog: IAuditLog)
    (logger: ILogger)
    (config: ServerConfig)
    (grounding: string list)
    : Async<unit> =
    async {
        try
            let now = DateTime.UtcNow
            let set = companionSet ()
            let hash = hashCompanionSet set
            let newSnapshot = buildSnapshot config set hash grounding now
            let snapshots = BlobDocumentStore<JsonObject>(storage, snapshotCodec, logger)
            let! previous = snapshots.Read(ScopeKey.platform, snapshotPath)

            let! replace = async {
                match previous with
                | Ok(Some prevSnapshot) ->
                    try
                        let prevConfig = prevSnapshot["config"]
                        let currConfig = newSnapshot["config"]

                        let prevHash =
                            prevSnapshot["companionSetHash"]
                            |> Option.ofObj
                            |> Option.map _.GetValue<string>()

                        let configChanges =
                            if prevConfig <> null && currConfig <> null then
                                diffTokens "" prevConfig currConfig [] |> List.rev
                            else
                                []

                        // Phase 526 — the composed grounding registrations join the
                        // drift surface: a metric/subject added or removed between
                        // deploys surfaces here (path `_grounding`). A previous
                        // snapshot predating this field carries no `grounding`, so
                        // `prevGrounding` is null and no synthetic drift is emitted.
                        let prevGrounding = prevSnapshot["grounding"]
                        let currGrounding = newSnapshot["grounding"]

                        let groundingChanges =
                            if prevGrounding <> null && currGrounding <> null then
                                diffTokens "_grounding" prevGrounding currGrounding [] |> List.rev
                            else
                                []

                        let changes = configChanges @ groundingChanges

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
                                    ScopeKey.PlatformContainer,
                                    ConfigDrift {
                                        Changes = changes
                                        CompanionSetFrom = prevHash
                                        CompanionSetTo = hash
                                        SnapshotTakenAt = now
                                    }
                                )
                                |> AuditRow.orRefuse

                        return true
                    with ex ->
                        // A previous snapshot that decodes but cannot be
                        // compared — log + overwrite. Don't emit `ConfigDrift`
                        // for it; the operator's signal is "drift", not "the
                        // prior file was malformed".
                        logger.Warn(
                            sprintf
                                "Config drift detector: previous snapshot could not be compared (%s); rewriting."
                                ex.Message
                        )

                        return true
                | Ok None ->
                    // First run on this deployment — write the snapshot,
                    // no diff. The detector is intentionally silent on the
                    // first run so a fresh deploy doesn't ship a synthetic
                    // "everything changed" event.
                    return true
                | Error(BlobMapStoreError.Quarantined _ as e) ->
                    // Unparseable previous snapshot — set aside by the
                    // store; log + replace, no `ConfigDrift`.
                    logger.Warn(
                        sprintf
                            "Config drift detector: previous snapshot could not be parsed (%s); rewriting."
                            (BlobMapStoreError.describe e)
                    )

                    return true
                | Error e ->
                    logger.Warn(
                        sprintf
                            "Config drift detector: previous snapshot could not be read (%s); it is kept, not overwritten, so the next startup still compares against it."
                            (BlobMapStoreError.describe e)
                    )

                    return false
            }

            if replace then
                match! snapshots.Replace(ScopeKey.platform, snapshotPath, newSnapshot) with
                | Ok() -> ()
                | Error e ->
                    logger.Warn(
                        sprintf "Config drift detector: snapshot persist failed: %s" (BlobMapStoreError.describe e)
                    )

        with ex ->
            logger.Warn(sprintf "Config drift detector: skipped due to error: %s" ex.Message)
    }