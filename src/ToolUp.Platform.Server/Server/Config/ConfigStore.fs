module ToolUp.Platform.ConfigStore

open System
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.BlobStorage

// ─── JSON helpers ────────────────────────────────────────────────

/// Shared `FableConverters` setup — same pattern as
/// `BlobProviderProfile`. The converter handles F# records,
/// DUs, and `option` types losslessly so modules can hand the store
/// their domain records without hand-rolling a DTO layer.
module private Json =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let deserialize<'T> (json: string) : 'T =
        JsonSerializer.Deserialize<'T>(json, options)

    /// Deserialise the flat `Map<string, string>` persistence shape
    /// from a single blob. Kept out of the typed `deserialize<'T>`
    /// because the map is what the admin UI sees — typed reads go
    /// through `projectToRecord`.
    let tryDeserializeMap (bytes: byte[]) : Map<string, string> option =
        try
            let json = Encoding.UTF8.GetString(bytes)
            Some(JsonSerializer.Deserialize<Map<string, string>>(json, options))
        with _ ->
            None

    let serializeMap (values: Map<string, string>) : byte[] =
        JsonSerializer.Serialize(values, options) |> Encoding.UTF8.GetBytes

// ─── Validation ──────────────────────────────────────────────────

/// Validate a single JSON-encoded field value against its schema
/// entry. Returns the normalised JSON on success; an error message on
/// type mismatch, bounds violation, or unknown choice. The normalised
/// value is what gets persisted — this lets the store re-emit values
/// in a canonical shape (e.g. trimmed strings) without diverging from
/// the input the admin UI supplied.
module private Validate =

    /// Parse json into a JsonDocument. The document owns the buffer
    /// backing every JsonElement returned from `RootElement` / property
    /// accessors, so the caller MUST dispose / use it within the same
    /// scope as the JsonElement reads. `field` below does both inline.
    let private tryParse (json: string) : JsonDocument option =
        try
            Some(JsonDocument.Parse(json))
        with _ ->
            None

    let field (schema: ConfigFieldSchema) (json: string) : Result<string, string> =
        match tryParse json with
        | None -> Error $"Field '{schema.Key}': value is not valid JSON."
        | Some doc ->
            use doc = doc
            let el = doc.RootElement

            match schema.Kind with
            | ConfigFieldKind.Bool ->
                match el.ValueKind with
                | JsonValueKind.True -> Ok "true"
                | JsonValueKind.False -> Ok "false"
                | _ -> Error $"Field '{schema.Key}': expected boolean."
            | ConfigFieldKind.Int(minO, maxO) ->
                // JSON numbers are arbitrary precision; require integer
                // shape (no fractional part). STJ's TryGetInt32 returns
                // false on non-integer numbers as well as on non-numeric
                // values, which is exactly the gate this branch needs.
                if el.ValueKind <> JsonValueKind.Number then
                    Error $"Field '{schema.Key}': expected integer."
                else
                    match el.TryGetInt32() with
                    | true, v ->
                        let inBounds =
                            (minO |> Option.forall (fun m -> v >= m))
                            && (maxO |> Option.forall (fun m -> v <= m))

                        if inBounds then
                            Ok(string v)
                        else
                            Error $"Field '{schema.Key}': integer out of range."
                    | false, _ -> Error $"Field '{schema.Key}': expected integer."
            | ConfigFieldKind.Float(minO, maxO) ->
                if el.ValueKind <> JsonValueKind.Number then
                    Error $"Field '{schema.Key}': expected number."
                else
                    let v = el.GetDouble()

                    let inBounds =
                        (minO |> Option.forall (fun m -> v >= m))
                        && (maxO |> Option.forall (fun m -> v <= m))

                    if inBounds then
                        // GetRawText preserves the wire-format representation
                        // (e.g. `3.14`, `1e-6`) without going through a
                        // round-trip stringification.
                        Ok(el.GetRawText())
                    else
                        Error $"Field '{schema.Key}': number out of range."
            | ConfigFieldKind.String maxLenO ->
                if el.ValueKind <> JsonValueKind.String then
                    Error $"Field '{schema.Key}': expected string."
                else
                    let s = el.GetString()

                    match maxLenO with
                    | Some n when s.Length > n -> Error $"Field '{schema.Key}': string exceeds max length {n}."
                    | _ -> Ok(JsonSerializer.Serialize s)
            | ConfigFieldKind.Choice options ->
                if el.ValueKind <> JsonValueKind.String then
                    Error $"Field '{schema.Key}': expected choice string."
                else
                    let s = el.GetString()

                    if List.contains s options then
                        Ok(JsonSerializer.Serialize s)
                    else
                        Error $"Field '{schema.Key}': '{s}' is not one of the allowed options."

    /// Validate and normalise every value in a raw map against a
    /// schema. Rejects unknown keys (schema drift protection) and
    /// missing `Required` fields. Returns the normalised map on
    /// success.
    let map (schema: ModuleConfigSchema) (values: Map<string, string>) : Result<Map<string, string>, string> =
        let known = schema.Fields |> List.map _.Key |> Set.ofList

        let unknown =
            values
            |> Map.toList
            |> List.map fst
            |> List.filter (fun k -> not (known.Contains k))

        match unknown with
        | k :: _ -> Error $"Unknown config field: '{k}'."
        | [] ->
            let missingRequired =
                schema.Fields
                |> List.tryFind (fun f -> f.Required && not (Map.containsKey f.Key values))

            match missingRequired with
            | Some f -> Error $"Field '{f.Key}' is required."
            | None ->
                let folder (acc: Result<Map<string, string>, string>) (key, rawJson) =
                    match acc with
                    | Error _ as e -> e
                    | Ok accMap ->
                        let schemaField = schema.Fields |> List.find (fun f -> f.Key = key)

                        match field schemaField rawJson with
                        | Ok normalised -> Ok(Map.add key normalised accMap)
                        | Error msg -> Error msg

                values |> Map.toList |> List.fold folder (Ok Map.empty)

// ─── Effective-value merge + typed projection ────────────────────

/// Build a JSON object string by overlaying the persisted map onto
/// the schema's declared defaults, then deserialise into `'T`. A
/// decode failure on any individual field falls back to that field's
/// `DefaultJson` — the store promises `GetEffective` never throws,
/// which is what modules rely on at `Init`. `warn` surfaces the
/// all-defaults fallback (schema/'T drift after an upgrade) so the
/// operator symptom "my config didn't stick" has a log line naming
/// the discarded fields instead of empty logs.
let private projectToRecord<'T>
    (warn: string -> unit)
    (context: string)
    (schema: ModuleConfigSchema)
    (persisted: Map<string, string>)
    : 'T =

    let sb = StringBuilder()
    sb.Append("{") |> ignore
    let mutable first = true

    for field in schema.Fields do
        if not first then
            sb.Append(",") |> ignore

        first <- false
        sb.Append("\"") |> ignore
        sb.Append(field.Key) |> ignore
        sb.Append("\":") |> ignore

        let rawJson =
            persisted |> Map.tryFind field.Key |> Option.defaultValue field.DefaultJson

        sb.Append(rawJson) |> ignore

    sb.Append("}") |> ignore

    try
        Json.deserialize<'T> (sb.ToString())
    with ex ->
        // Whole-object decode failed — the schema and `'T` disagree.
        // Fall back to all-defaults so module `Init` still returns.
        let discarded = persisted |> Map.toList |> List.map fst |> String.concat ", "

        warn (
            sprintf
                "[BlobConfigStore] %s: effective-config decode into %s failed (%s) — falling back to schema defaults. Persisted field(s) being ignored: [%s]. This usually means the module's config record and its declared schema drifted apart after an upgrade; admin-configured values will appear not to stick until the drift is fixed."
                context
                typeof<'T>.Name
                ex.Message
                discarded
        )

        let defaultsOnly =
            let sb2 = StringBuilder()
            sb2.Append("{") |> ignore
            let mutable first2 = true

            for field in schema.Fields do
                if not first2 then
                    sb2.Append(",") |> ignore

                first2 <- false
                sb2.Append("\"") |> ignore
                sb2.Append(field.Key) |> ignore
                sb2.Append("\":") |> ignore
                sb2.Append(field.DefaultJson) |> ignore

            sb2.Append("}") |> ignore
            sb2.ToString()

        Json.deserialize<'T> defaultsOnly

// ─── Blob layout ─────────────────────────────────────────────────

let private platformContainer = "_platform"

/// Blob name within the reserved `_platform` container. The scope's
/// container is encoded into the path — one folder per scope —
/// instead of using the scope's own container. This keeps all config
/// documents under a single platform-owned namespace (same pattern
/// as `_platform/permissions/{teamId}.json`) rather than spraying
/// them across every team's container.
let private blobName (scope: StorageScope) (moduleKey: string) =
    $"config/{scope.Container}/{moduleKey}.json"

// ─── IConfigStore — blob-backed implementation ───────────────────

/// Blob-backed `IConfigStore`. One document per `(scope, moduleKey)`.
/// Thread-safe via the underlying storage — no caching layer. Callers
/// that hot-read the same config on every request should wrap this
/// behind a short-TTL cache.
///
/// Uses a named class (rather than object expression) because F#
/// doesn't bind interface generic type parameters on object-expression
/// method overrides — an object-expression `Set<'T>` infers a fresh
/// `'a` instead of sharing the interface's `'T`.
///
/// `logger` is optional for back-compat with direct constructions, but
/// every fallback path warns through it when present — the three
/// silent degradations (corrupt blob → empty map; `Get<'T>` decode
/// failure → `None`; effective-config decode failure → all-defaults)
/// previously had no observability at all, so a schema drift after an
/// upgrade discarded every admin-configured value with empty logs.
type BlobConfigStore(storage: IBlobStorage, ?logger: ILogger) =
    let warn (msg: string) =
        match logger with
        | Some l -> l.Warn msg
        | None -> ()

    let context (scope: StorageScope) (moduleKey: string) =
        sprintf "scope=%s moduleKey=%s" scope.Container moduleKey

    let loadRaw (scope: StorageScope) (moduleKey: string) = async {
        let! result = storage.Download(platformContainer, blobName scope moduleKey)

        match result with
        | Ok bytes ->
            match Json.tryDeserializeMap bytes with
            | Some map -> return map
            | None ->
                // Blob exists but doesn't decode — corruption or a
                // persistence-shape change. Distinct from the missing-
                // blob case below: here admin-configured values exist
                // on disk and are being discarded.
                warn (
                    sprintf
                        "[BlobConfigStore] %s: config blob exists but failed to decode as Map<string,string> — treating as empty. Persisted admin-configured values are being ignored; inspect/restore the blob '%s/%s'."
                        (context scope moduleKey)
                        platformContainer
                        (blobName scope moduleKey)
                )

                return Map.empty
        | Error _ ->
            // Missing blob is the normal "nothing configured yet" case
            // — not warn-worthy.
            return Map.empty
    }

    let saveRaw (scope: StorageScope) (moduleKey: string) (values: Map<string, string>) = async {
        let bytes = Json.serializeMap values
        let! result = storage.Upload(platformContainer, blobName scope moduleKey, bytes)

        return result |> Result.map (fun _ -> ())
    }

    interface IConfigStore with
        member _.GetRaw(scope, moduleKey) = loadRaw scope moduleKey

        member _.Get<'T>(scope, moduleKey) : Async<'T option> = async {
            let! raw = loadRaw scope moduleKey

            if Map.isEmpty raw then
                return None
            else
                try
                    let json = Json.serialize raw
                    return Some(Json.deserialize<'T> json)
                with ex ->
                    warn (
                        sprintf
                            "[BlobConfigStore] %s: persisted config failed to decode into %s (%s) — returning None. The caller will see 'nothing configured' even though %d field(s) are persisted: [%s]."
                            (context scope moduleKey)
                            typeof<'T>.Name
                            ex.Message
                            (Map.count raw)
                            (raw |> Map.toList |> List.map fst |> String.concat ", ")
                    )

                    return None
        }

        member _.GetEffective<'T>(scope, moduleKey, schema) : Async<'T> = async {
            let! raw = loadRaw scope moduleKey
            return projectToRecord<'T> warn (context scope moduleKey) schema raw
        }

        member _.Set<'T>(scope, moduleKey, value: 'T, schema) = async {
            try
                let json = Json.serialize value
                let asMap = Json.deserialize<Map<string, string>> json

                match Validate.map schema asMap with
                | Error msg -> return Error msg
                | Ok normalised -> return! saveRaw scope moduleKey normalised
            with ex ->
                return Error $"Config serialisation failed: {ex.Message}"
        }

        member _.SetRaw(scope, moduleKey, values, schema) = async {
            match Validate.map schema values with
            | Error msg -> return Error msg
            | Ok normalised -> return! saveRaw scope moduleKey normalised
        }

        member _.Clear(scope, moduleKey) = async {
            let! _ = storage.Delete(platformContainer, blobName scope moduleKey)
            return ()
        }

        member _.Erase(scopeId, subjectUserId, policy, dryRun) = async {
            if Erasure.isBlankSubject subjectUserId then
                return
                    Result.Ok {
                        HandlerName = "config"
                        RecordsAffected = 0
                        Note = Some "blank subject — no-op"
                    }
            else
                let! names = storage.List(platformContainer, $"config/{scopeId}/")

                let! docs =
                    names
                    |> List.map (fun name -> async {
                        let! result = storage.Download(platformContainer, name)

                        return
                            match result with
                            | Ok bytes -> name, Json.tryDeserializeMap bytes
                            | Error _ -> name, None
                    })
                    |> Async.Parallel

                let valueNamesSubject (m: Map<string, string>) =
                    m |> Map.exists (fun _ v -> v.Contains subjectUserId)

                let matched =
                    docs
                    |> Array.toList
                    |> List.choose (fun (name, mo) ->
                        match mo with
                        | Some m when valueNamesSubject m -> Some(name, m)
                        | _ -> None)

                if dryRun || List.isEmpty matched then
                    let verb =
                        match policy with
                        | ErasurePolicy.HardDelete -> "removed"
                        | _ -> "redacted"

                    return
                        Result.Ok {
                            HandlerName = "config"
                            RecordsAffected = matched.Length
                            Note =
                                Some(
                                    sprintf "%d config document(s) would be %s in scope %s" matched.Length verb scopeId
                                )
                        }
                else
                    match policy with
                    | ErasurePolicy.HardDelete ->
                        do!
                            matched
                            |> List.map (fun (name, _) -> storage.Delete(platformContainer, name))
                            |> Async.Parallel
                            |> Async.Ignore

                        return
                            Result.Ok {
                                HandlerName = "config"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d config document(s) removed in scope %s" matched.Length scopeId)
                            }
                    | ErasurePolicy.Tombstone
                    | ErasurePolicy.RetainPerCompliance ->
                        // Values are JSON-encoded; the marker as a JSON
                        // string keeps the persisted map valid.
                        let markerJson = sprintf "\"%s\"" Erasure.TombstoneMarker

                        do!
                            matched
                            |> List.map (fun (name, m) ->
                                let redacted =
                                    m |> Map.map (fun _ v -> if v.Contains subjectUserId then markerJson else v)

                                storage.Upload(platformContainer, name, Json.serializeMap redacted))
                            |> Async.Parallel
                            |> Async.Ignore

                        return
                            Result.Ok {
                                HandlerName = "config"
                                RecordsAffected = matched.Length
                                Note = Some(sprintf "%d config document(s) redacted in scope %s" matched.Length scopeId)
                            }
        }

/// Convenience factory — construct and upcast. Mirrors the
/// `create` pattern used by other stores in the SDK. Prefer
/// `createWithLogger` so the store's fallback paths (corrupt blob,
/// decode failures) surface as Warn lines instead of silent defaults.
let create (storage: IBlobStorage) : IConfigStore =
    BlobConfigStore(storage) :> IConfigStore

/// Logger-threading factory — the composition root passes its resolved
/// `ILogger` so the store's three fallback paths are observable.
let createWithLogger (storage: IBlobStorage) (logger: ILogger) : IConfigStore =
    BlobConfigStore(storage, logger) :> IConfigStore