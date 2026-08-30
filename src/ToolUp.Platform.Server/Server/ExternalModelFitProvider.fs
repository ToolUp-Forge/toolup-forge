// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks

// ─── Phase 450 — the external execution binding of the fit envelope ──────
//
// Phase 449 gave the platform a neutral fit contract
// (`IModelFitProvider`): `(vintage, opaque spec, seed, gates)` →
// `(artifact, diagnostics, gate verdicts)`. Its only shipped
// implementation is the in-tree reference fitter, which runs in this
// process and does no statistics. The workloads the envelope exists for
// — a Python or R fit that takes minutes to hours on a machine with more
// memory than the web server — cannot be implemented that way, and the
// obvious route (an SDK a worker imports) is the one route this repo
// must not take: it would make every fit worker a .NET consumer of a
// package versioned here.
//
// **So the worker is an HTTP contract, not an SDK port.** The whole
// binding is: submit a versioned JSON envelope through the Phase 318
// dispatcher, let the worker read the dataset as a blob reference it was
// handed (Phase 448 `GetContentRef`), surface progress through the Phase
// 321 sink, accept completion through the Phase 320 callback ingress, and
// read the artifact back out of the opaque result reference. Every one of
// those is a shipped seam; this file adds no transport of its own and
// mounts no route.
//
// **Three postures worth stating before the code.**
//
//   * *Forge evaluates the gates, not the worker* (449.C, plan D10). A
//     worker reports diagnostics; `Gate.evaluateAll` turns them into
//     verdicts here, against the gates the REQUEST asked for. A worker
//     that returned its own verdicts would be grading its own homework,
//     and a worker that omitted a diagnostic a gate names fails that gate
//     closed rather than silently passing it.
//   * *The envelope is versioned from day one* (450.A, plan risk #5,
//     mirroring the Phase 69j posture). The `Kind` carries the version
//     (`modelfit/v1`), the payload repeats it so a persisted payload is
//     self-describing, and a worker declares which envelope versions it
//     accepts. A mismatch is a typed refusal computed BEFORE the payload
//     leaves this process — the same "refuse before submit" reasoning
//     `ExecutionProfileGate` uses, for the same reason: nothing recalls a
//     payload a backend has already accepted.
//   * *Nothing here is composed by default* (GP 13). `ServerApp` builds
//     no `ExternalModelFitProvider`, registers no completion registry and
//     mounts no route. A deployment that wants external fits composes the
//     three pieces itself; every other deployment is byte-for-byte
//     unchanged.
//
// **Six portability rules (GP 12).** 1. Identity by value — the whole
// surface is records, strings and `Guid`s; the worker is addressed by an
// `ExternalHandle`, never a live client. 2. Async at every boundary.
// 3. Retry / supervision as data — a failure is `ExternalFitFailure`,
// carrying the backend's own `Retriable` flag through unchanged; there is
// no callback parameter and no supervision object. 4. Stateless between
// invocations — a fit carries its whole request; the completion registry
// holds only the in-flight rendezvous, and a fit that loses it resolves
// by poll. 5. No cross-fit ordering is promised. 6. `PollInterval` and
// `Timeout` are `TimeSpan`s the backend's own scheduling granularity
// floors, exactly as `ExternalWorkSpec.Timeout` is.

// ─── 450.A — the `modelfit/v1` work-spec convention ──────────────────────

/// One diagnostic gate, in its **wire** form.
///
/// A flattened twin of `GateSpec` rather than `GateSpec` itself, and the
/// flattening is the point: `GateDirection` is an F# discriminated union
/// whose JSON shape is decided by a converter set no Python or R worker
/// has. `Direction` here is the stable case name `GateDirection.name`
/// produces (`"AtLeast"` / `"AtMost"`), so the envelope is readable by
/// `json.load` and nothing else.
///
/// `[<RequireQualifiedAccess>]` is load-bearing rather than stylistic:
/// this record's field set is `GateSpec`'s with `Direction` retyped, and
/// F# resolves a bare `{ Name = …; Threshold = …; Direction = … }` to the
/// LAST matching declaration — so without it, every existing `GateSpec`
/// literal in a file compiled after this one silently becomes a wire
/// gate. That is not hypothetical; it broke `ModelExecutionApi.fs` on the
/// first build of this phase.
[<RequireQualifiedAccess>]
type ModelFitWireGate = {
    /// Diagnostic key the gate reads. The worker's only obligation is to
    /// report a diagnostic under this name; it evaluates nothing.
    Name: string
    Threshold: float
    /// `"AtLeast"` | `"AtMost"` — `GateDirection.name`.
    Direction: string
}

/// The `modelfit/v1` payload: everything a worker needs to run one
/// seeded fit, and nothing about how it should run it.
///
/// Carried as `ExternalWorkSpec.Payload` (a pre-serialised JSON string
/// the dispatcher never parses). The dataset travels as a **reference**,
/// never as rows: the worker fetches the content blob itself, which is
/// what keeps a multi-gigabyte vintage out of a submit request and off
/// this process's heap.
type ModelFitWorkPayload = {
    /// Echo of `ExternalWorkSpec.Kind` — `"modelfit/v1"`. Present so a
    /// payload persisted by a worker alongside its own job record is
    /// self-describing when it is re-read without the submit envelope.
    Envelope: string
    /// Scope the fit runs under (GP 4). A worker that partitions its own
    /// records by tenant keys on this.
    ScopeId: string
    /// The opaque provider spec, verbatim. Forge never parses it
    /// (`ModelSpecRef.Payload`).
    SpecRef: string
    /// Lowercase SHA-256 hex of `SpecRef`, so a worker can verify it
    /// received the spec forge hashed.
    SpecHash: string
    /// The submitter-named minting rule for `SpecHash`, or empty. Carried
    /// verbatim and never acted on, exactly as `ModelSpecRef` carries it.
    SpecHashAlgorithm: string
    /// The dataset vintage's content blob — scope, id, version, content
    /// hash, **format tag** and row count. The format tag is why this is
    /// not called a Parquet ref in the type system: a deployment that has
    /// composed no Parquet codec ships `"toolup-frame-v1"` here, and a
    /// Parquet-expecting kernel must read the tag and refuse rather than
    /// be handed the wrong bytes under the right name.
    DatasetParquetRef: DatasetContentRef
    /// The fit's seed. Part of the composite identity, so a worker that
    /// ignores it breaks reproducibility rather than merely varying.
    Seed: int64
    /// Gates the platform will evaluate against the diagnostics this
    /// worker reports. Sent so a worker can fail fast on a diagnostic it
    /// knows it cannot produce; it does **not** evaluate them.
    Gates: ModelFitWireGate list
    /// Advisory resource requests, mirrored from
    /// `ExternalWorkSpec.ResourceHints`. Present in both places on
    /// purpose: the spec-level field is what a routing dispatcher (Phase
    /// 484) selects a backend on, and this copy is what the worker reads
    /// once a backend has been chosen.
    ResourceHints: Map<string, string>
}

/// What a worker returns: the artifact it produced, the diagnostics it
/// measured, and its own deterministic cost self-report.
///
/// **This travels as the `resultRef` string of
/// `ExternalOutcome.Succeeded`, and that is a deliberate reading of an
/// opaque field rather than a widening of it.** Phase 318 says a
/// `resultRef` is "a blob key, an artefact URI, a content hash" that the
/// platform echoes and never dereferences — and it is still never
/// dereferenced here. What `modelfit/v1` adds is that, for this `Kind`
/// only, the reference is a small JSON document rather than a bare key,
/// because a fit's outcome is irreducibly more than one string: an
/// artifact needs a content hash and a length to be checkable, and the
/// gates cannot be evaluated at all without diagnostics. Any other `Kind`
/// is unaffected, and a document that does not parse is a typed
/// `MalformedArtifact` refusal rather than an artifact nobody can verify.
///
/// `[<RequireQualifiedAccess>]` for the reason `ModelFitWireGate` carries
/// it: this record's field set is a superset of `ArtifactRef`'s, and bare
/// record-literal resolution is by declaration order, not by intent.
[<RequireQualifiedAccess>]
type ModelFitArtifactDescriptor = {
    /// Echo of the envelope version the worker answered under.
    Envelope: string
    /// Opaque worker-side identity of the stored artifact — a blob key, a
    /// URI, a content-addressed name. Never dereferenced by the platform.
    ArtifactId: string
    /// Lowercase SHA-256 hex of the artifact bytes. Checked for SHAPE
    /// here (64 lowercase hex) and never recomputed — the platform does
    /// not hold the bytes. What the shape check buys is that a downstream
    /// consumer holding this descriptor has a digest it can act on rather
    /// than a string that merely looked like one.
    ContentHash: string
    /// Artifact size in bytes.
    ByteLength: int64
    /// Diagnostics the worker measured, name → value. Forge stores and
    /// compares them; it never interprets them (plan D10).
    Diagnostics: Map<string, float>
    /// Worker-reported compute duration. A self-report, like every other
    /// `FitOutcome.DurationMs` — never this process's wall clock, which
    /// would fold queue latency into the fit.
    DurationMs: int64
    /// Worker-reported cost units.
    CostUnits: float
}

/// The `modelfit/v1` envelope: its version token, and the four
/// render/parse functions that are its whole grammar.
///
/// Both directions are hand-written over `Utf8JsonWriter` / `JsonDocument`
/// rather than routed through the SDK's converter set, and that is the
/// one design choice in this file worth defending twice. The converter
/// set exists to round-trip F# values between two .NET processes; it
/// renders `option`, `Map` and union values in shapes that are correct
/// and are not what a worker author reading the contract document would
/// write by hand. Writing the JSON explicitly means the schema in
/// `docs/platform/model-fit-worker-contract.md` is literally true, field
/// for field, and cannot drift with a converter version.
[<RequireQualifiedAccess>]
module ModelFitWorkSpec =

    /// The `ExternalWorkSpec.Kind` a `modelfit/v1` submission carries, and
    /// the version token the payload echoes. **The version lives in the
    /// kind**, so a `v2` envelope is a different `Kind` a worker either
    /// routes or refuses — never the same kind carrying a version field
    /// some workers read and others ignore.
    [<Literal>]
    let Kind = "modelfit/v1"

    /// The envelope versions a worker is assumed to accept when a
    /// deployment declares nothing: this one. Declaring nothing therefore
    /// means "current envelope only", which is the safe direction — a
    /// worker wrongly assumed to accept `v1` refuses a submission it
    /// understands, while one wrongly assumed to accept everything is
    /// handed a payload it cannot read.
    let DefaultAcceptedEnvelopes = [ Kind ]

    // ── writing ──

    let private writeGate (writer: Utf8JsonWriter) (gate: ModelFitWireGate) =
        writer.WriteStartObject()
        writer.WriteString("name", gate.Name)
        writer.WriteNumber("threshold", gate.Threshold)
        writer.WriteString("direction", gate.Direction)
        writer.WriteEndObject()

    let private writeStringMap (writer: Utf8JsonWriter) (name: string) (map: Map<string, string>) =
        writer.WriteStartObject name
        // `Map` enumerates in key order, so two renderings of one payload
        // are byte-identical and a worker's own idempotency key taken over
        // the body is stable.
        for KeyValue(key, value) in map do
            writer.WriteString(key, value)

        writer.WriteEndObject()

    let private toText (write: Utf8JsonWriter -> unit) : string =
        use buffer = new MemoryStream()
        use writer = new Utf8JsonWriter(buffer)
        write writer
        writer.Flush()
        Encoding.UTF8.GetString(buffer.ToArray())

    /// Render a payload to the exact JSON the contract document specifies.
    let renderPayload (payload: ModelFitWorkPayload) : string =
        toText (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("envelope", payload.Envelope)
            writer.WriteString("scopeId", payload.ScopeId)
            writer.WriteString("specRef", payload.SpecRef)
            writer.WriteString("specHash", payload.SpecHash)
            writer.WriteString("specHashAlgorithm", payload.SpecHashAlgorithm)

            writer.WriteStartObject "datasetParquetRef"
            writer.WriteString("scopeId", payload.DatasetParquetRef.ScopeId)
            writer.WriteString("datasetId", payload.DatasetParquetRef.DatasetId)
            writer.WriteNumber("version", payload.DatasetParquetRef.Version)
            writer.WriteString("contentHash", payload.DatasetParquetRef.ContentHash)
            writer.WriteString("format", payload.DatasetParquetRef.Format)
            writer.WriteNumber("rowCount", payload.DatasetParquetRef.RowCount)
            writer.WriteEndObject()

            writer.WriteNumber("seed", payload.Seed)

            writer.WriteStartArray "gates"

            for gate in payload.Gates do
                writeGate writer gate

            writer.WriteEndArray()

            writeStringMap writer "resourceHints" payload.ResourceHints
            writer.WriteEndObject())

    /// Render an artifact descriptor — the `resultRef` a worker returns.
    /// Shipped beside the parser so the two halves of the contract cannot
    /// drift, and so a test signs what the reader reads by construction.
    let renderDescriptor (descriptor: ModelFitArtifactDescriptor) : string =
        toText (fun writer ->
            writer.WriteStartObject()
            writer.WriteString("envelope", descriptor.Envelope)
            writer.WriteString("artifactId", descriptor.ArtifactId)
            writer.WriteString("contentHash", descriptor.ContentHash)
            writer.WriteNumber("byteLength", descriptor.ByteLength)

            writer.WriteStartObject "diagnostics"

            for KeyValue(key, value) in descriptor.Diagnostics do
                writer.WriteNumber(key, value)

            writer.WriteEndObject()

            writer.WriteNumber("durationMs", descriptor.DurationMs)
            writer.WriteNumber("costUnits", descriptor.CostUnits)
            writer.WriteEndObject())

    // ── reading ──
    //
    // Every accessor returns a `Result` naming the field, because these
    // refusals are read by a worker author debugging their own integration
    // and "malformed" without a field name is an afternoon.

    let private requireObject (element: JsonElement) (name: string) : Result<JsonElement, string> =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Object -> Ok value
        | true, _ -> Error $"field '%s{name}' is not an object"
        | _ -> Error $"field '%s{name}' is missing"

    let private requireString (element: JsonElement) (name: string) : Result<string, string> =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
        | true, _ -> Error $"field '%s{name}' is not a string"
        | _ -> Error $"field '%s{name}' is missing"

    /// A string field that may be absent, reading as `""`. Used only where
    /// absence and emptiness are genuinely the same claim — namely
    /// `specHashAlgorithm`, where both mean "no minting rule was named".
    let private optionalString (element: JsonElement) (name: string) : Result<string, string> =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> Ok(value.GetString())
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok ""
        | true, _ -> Error $"field '%s{name}' is not a string"
        | _ -> Ok ""

    let private requireNumber (element: JsonElement) (name: string) : Result<float, string> =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetDouble() with
            | true, number -> Ok number
            | _ -> Error $"field '%s{name}' is not a readable number"
        | true, _ -> Error $"field '%s{name}' is not a number"
        | _ -> Error $"field '%s{name}' is missing"

    let private requireInt64 (element: JsonElement) (name: string) : Result<int64, string> =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Number ->
            match value.TryGetInt64() with
            | true, number -> Ok number
            | _ -> Error $"field '%s{name}' is not an integer"
        | true, _ -> Error $"field '%s{name}' is not a number"
        | _ -> Error $"field '%s{name}' is missing"

    let private requireInt (element: JsonElement) (name: string) : Result<int, string> =
        requireInt64 element name
        |> Result.bind (fun value ->
            if value >= int64 Int32.MinValue && value <= int64 Int32.MaxValue then
                Ok(int value)
            else
                Error $"field '%s{name}' is out of range for a 32-bit integer")

    /// Fold a list of `Result`s into a `Result` of a list, keeping the
    /// FIRST failure. First rather than all: these are shape errors in a
    /// hand-written integration, and the first one is almost always the
    /// cause of the rest.
    let private allOk (results: Result<'a, string> list) : Result<'a list, string> =
        results
        |> List.fold
            (fun acc item ->
                match acc, item with
                | Error e, _ -> Error e
                | Ok _, Error e -> Error e
                | Ok items, Ok value -> Ok(value :: items))
            (Ok [])
        |> Result.map List.rev

    let private readStringMap (element: JsonElement) (name: string) : Result<Map<string, string>, string> =
        match element.TryGetProperty name with
        | false, _ -> Ok Map.empty
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok Map.empty
        | true, value when value.ValueKind <> JsonValueKind.Object -> Error $"field '%s{name}' is not an object"
        | true, value ->
            value.EnumerateObject()
            |> Seq.map (fun property ->
                if property.Value.ValueKind = JsonValueKind.String then
                    Ok(property.Name, property.Value.GetString())
                else
                    Error $"field '%s{name}.%s{property.Name}' is not a string")
            |> List.ofSeq
            |> allOk
            |> Result.map Map.ofList

    let private readNumberMap (element: JsonElement) (name: string) : Result<Map<string, float>, string> =
        match element.TryGetProperty name with
        | false, _ -> Ok Map.empty
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok Map.empty
        | true, value when value.ValueKind <> JsonValueKind.Object -> Error $"field '%s{name}' is not an object"
        | true, value ->
            value.EnumerateObject()
            |> Seq.map (fun property ->
                match property.Value.ValueKind with
                | JsonValueKind.Number ->
                    match property.Value.TryGetDouble() with
                    | true, number -> Ok(property.Name, number)
                    | _ -> Error $"field '%s{name}.%s{property.Name}' is not a readable number"
                | _ -> Error $"field '%s{name}.%s{property.Name}' is not a number")
            |> List.ofSeq
            |> allOk
            |> Result.map Map.ofList

    let private readGates (element: JsonElement) : Result<ModelFitWireGate list, string> =
        match element.TryGetProperty "gates" with
        | false, _ -> Ok []
        | true, value when value.ValueKind = JsonValueKind.Null -> Ok []
        | true, value when value.ValueKind <> JsonValueKind.Array -> Error "field 'gates' is not an array"
        | true, value ->
            value.EnumerateArray()
            |> Seq.map (fun gate ->
                if gate.ValueKind <> JsonValueKind.Object then
                    Error "an element of 'gates' is not an object"
                else
                    match
                        requireString gate "name", requireNumber gate "threshold", requireString gate "direction"
                    with
                    | Ok name, Ok threshold, Ok direction ->
                        match GateDirection.parse direction with
                        | Some _ ->
                            Ok {
                                ModelFitWireGate.Name = name
                                ModelFitWireGate.Threshold = threshold
                                ModelFitWireGate.Direction = direction
                            }
                        | None ->
                            Error $"gate '%s{name}' has direction '%s{direction}'; expected one of AtLeast, AtMost"
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error $"in 'gates': %s{e}")
            |> List.ofSeq
            |> allOk

    let private parseDocument (text: string) (read: JsonElement -> Result<'a, string>) : Result<'a, string> =
        if String.IsNullOrWhiteSpace text then
            Error "the document is empty"
        else
            let parsed =
                try
                    Ok(JsonDocument.Parse text)
                with ex ->
                    Error $"the document is not valid JSON: %s{ex.Message}"

            match parsed with
            | Error e -> Error e
            | Ok document ->
                use document = document

                if document.RootElement.ValueKind <> JsonValueKind.Object then
                    Error "the document is not a JSON object"
                else
                    read document.RootElement

    /// Parse a `modelfit/v1` payload. Shipped for the two readers that
    /// exist in .NET — a conformance test, and a worker a consumer happens
    /// to have written in F# — and used by neither as a requirement: the
    /// contract document alone is sufficient to write a worker, which is
    /// the acceptance criterion this whole binding is built around.
    let parsePayload (text: string) : Result<ModelFitWorkPayload, string> =
        parseDocument text (fun root ->
            let dataset = requireObject root "datasetParquetRef"

            match dataset with
            | Error e -> Error e
            | Ok dataset ->
                let contentRef =
                    match
                        requireString dataset "scopeId",
                        requireString dataset "datasetId",
                        requireInt dataset "version",
                        requireString dataset "contentHash",
                        requireString dataset "format",
                        requireInt64 dataset "rowCount"
                    with
                    | Ok scopeId, Ok datasetId, Ok version, Ok contentHash, Ok format, Ok rowCount ->
                        Ok {
                            ScopeId = scopeId
                            DatasetId = datasetId
                            Version = version
                            ContentHash = contentHash
                            Format = format
                            RowCount = rowCount
                        }
                    | Error e, _, _, _, _, _
                    | _, Error e, _, _, _, _
                    | _, _, Error e, _, _, _
                    | _, _, _, Error e, _, _
                    | _, _, _, _, Error e, _
                    | _, _, _, _, _, Error e -> Error $"in 'datasetParquetRef': %s{e}"

                match
                    requireString root "envelope",
                    requireString root "scopeId",
                    requireString root "specRef",
                    requireString root "specHash",
                    optionalString root "specHashAlgorithm",
                    requireInt64 root "seed"
                with
                | Ok envelope, Ok scopeId, Ok specRef, Ok specHash, Ok specHashAlgorithm, Ok seed ->
                    match contentRef, readGates root, readStringMap root "resourceHints" with
                    | Ok contentRef, Ok gates, Ok hints ->
                        Ok {
                            Envelope = envelope
                            ScopeId = scopeId
                            SpecRef = specRef
                            SpecHash = specHash
                            SpecHashAlgorithm = specHashAlgorithm
                            DatasetParquetRef = contentRef
                            Seed = seed
                            Gates = gates
                            ResourceHints = hints
                        }
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e
                | Error e, _, _, _, _, _
                | _, Error e, _, _, _, _
                | _, _, Error e, _, _, _
                | _, _, _, Error e, _, _
                | _, _, _, _, Error e, _
                | _, _, _, _, _, Error e -> Error e)

    /// Is `value` a lowercase hex SHA-256 digest?
    ///
    /// Lowercase is required rather than normalised, for the reason
    /// `WorkerOutcomeSignature.isHexDigest` requires it: the digest is
    /// compared and carried as text, and accepting either case would make
    /// two distinct strings name one artifact.
    let isArtifactDigest (value: string) : bool =
        not (isNull value)
        && value.Length = 64
        && value |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    /// Parse the artifact descriptor a worker returns as its `resultRef`.
    ///
    /// The envelope check is deliberate and is not merely defensive: a
    /// descriptor answering under a version this platform does not know is
    /// a worker that upgraded ahead of the deployment, and reading its
    /// fields anyway would be assuming a schema nobody promised.
    let parseDescriptor (text: string) : Result<ModelFitArtifactDescriptor, string> =
        parseDocument text (fun root ->
            match
                requireString root "envelope",
                requireString root "artifactId",
                requireString root "contentHash",
                requireInt64 root "byteLength"
            with
            | Ok envelope, Ok artifactId, Ok contentHash, Ok byteLength ->
                if envelope <> Kind then
                    Error $"the artifact descriptor declares envelope '%s{envelope}'; this platform reads '%s{Kind}'"
                elif String.IsNullOrWhiteSpace artifactId then
                    Error "field 'artifactId' is empty"
                elif not (isArtifactDigest contentHash) then
                    Error "field 'contentHash' is not a lowercase hex SHA-256 digest"
                elif byteLength < 0L then
                    Error "field 'byteLength' is negative"
                else
                    match
                        readNumberMap root "diagnostics",
                        requireInt64 root "durationMs",
                        requireNumber root "costUnits"
                    with
                    | Ok diagnostics, Ok durationMs, Ok costUnits ->
                        Ok {
                            ModelFitArtifactDescriptor.Envelope = envelope
                            ModelFitArtifactDescriptor.ArtifactId = artifactId
                            ModelFitArtifactDescriptor.ContentHash = contentHash
                            ModelFitArtifactDescriptor.ByteLength = byteLength
                            ModelFitArtifactDescriptor.Diagnostics = diagnostics
                            ModelFitArtifactDescriptor.DurationMs = durationMs
                            ModelFitArtifactDescriptor.CostUnits = costUnits
                        }
                    | Error e, _, _
                    | _, Error e, _
                    | _, _, Error e -> Error e
            | Error e, _, _, _
            | _, Error e, _, _
            | _, _, Error e, _
            | _, _, _, Error e -> Error e)

    /// Build the payload for one fit request against a resolved dataset
    /// content reference.
    let ofRequest
        (contentRef: DatasetContentRef)
        (resourceHints: Map<string, string>)
        (request: FitRequest)
        : ModelFitWorkPayload =
        {
            Envelope = Kind
            ScopeId = request.ScopeId
            SpecRef = request.SpecRef.Payload
            SpecHash = request.SpecRef.SpecHash
            SpecHashAlgorithm = request.SpecRef.SpecHashAlgorithm
            DatasetParquetRef = contentRef
            Seed = request.Seed
            Gates = [
                for gate in request.Gates ->
                    {
                        ModelFitWireGate.Name = gate.Name
                        ModelFitWireGate.Threshold = gate.Threshold
                        ModelFitWireGate.Direction = GateDirection.name gate.Direction
                    }
            ]
            ResourceHints = resourceHints
        }

// ─── 450.B — typed failures of the external binding ──────────────────────

/// Why an external fit did not produce an outcome.
///
/// A DU rather than a message, because the four remedies genuinely
/// differ: an envelope mismatch is a deployment change, a retriable
/// worker failure is a re-submission, a timeout is a budget or a capacity
/// question, and a malformed artifact is a worker defect. Collapsing them
/// into a string would make the one thing a caller wants to do
/// programmatically — decide whether to re-submit — a matter of parsing
/// prose forge never promised to keep stable.
///
/// `ModelFitError` (Phase 449) is deliberately not extended: it is the
/// ENVELOPE's failure vocabulary, shared by every provider, and widening
/// it for one binding's causes would retype a public union for a
/// deployment shape most consumers never compose. The fit-run job handler
/// still sees `ModelFitError.ProviderFailed` carrying `describe` below.
[<RequireQualifiedAccess>]
type ExternalFitFailure =
    /// The worker does not accept the envelope version this platform
    /// speaks. **Refused before the payload is submitted.** Terminal — a
    /// worker does not learn a new envelope by being asked twice.
    | EnvelopeUnsupported of requested: string * accepted: string list
    /// The fit's scope and its dataset vintage's scope disagree. A GP 4
    /// refusal, made here rather than left to the dataset store, because
    /// a cross-scope read that reaches the store has already been
    /// attempted.
    | ScopeMismatch of fitScope: string * datasetScope: string
    /// The dataset vintage could not be resolved to a content reference.
    | DatasetUnavailable of DatasetError
    /// The dispatcher refused the submission (unknown kind, unhonourable
    /// hint, backend unreachable, no backend composed). Carries the
    /// backend's own retriability.
    | SubmitRefused of ExternalComputeError
    /// The worker ran and reported failure. Retriability is the backend's
    /// own, carried through unchanged.
    | WorkerFailed of ExternalComputeError
    /// The work was cancelled — by this caller, by the backend, or by an
    /// operator.
    | Cancelled
    /// The configured budget expired before a terminal outcome arrived. A
    /// cancel is lodged with the backend before this is returned.
    | TimedOut of budget: TimeSpan
    /// The worker succeeded and returned a result reference that is not a
    /// readable `modelfit/v1` artifact descriptor.
    | MalformedArtifact of reason: string

[<RequireQualifiedAccess>]
module ExternalFitFailure =
    /// One-line description for logs, job-result messages, and the
    /// `ModelFitError.ProviderFailed` the envelope surfaces.
    let describe =
        function
        | ExternalFitFailure.EnvelopeUnsupported(requested, accepted) ->
            let declared =
                if List.isEmpty accepted then
                    "<none declared>"
                else
                    String.concat ", " accepted

            sprintf
                "the worker does not accept work-spec envelope '%s' (it accepts: %s); the submission was refused before the payload left this process"
                requested
                declared
        | ExternalFitFailure.ScopeMismatch(fitScope, datasetScope) ->
            sprintf "the fit runs under scope '%s' but its dataset vintage belongs to scope '%s'" fitScope datasetScope
        | ExternalFitFailure.DatasetUnavailable error ->
            sprintf "the dataset vintage could not be resolved: %s" (DatasetError.describe error)
        | ExternalFitFailure.SubmitRefused error ->
            sprintf "the external-compute backend refused the submission: %s" (ExternalComputeError.describe error)
        | ExternalFitFailure.WorkerFailed error ->
            sprintf "the fit worker failed: %s" (ExternalComputeError.describe error)
        | ExternalFitFailure.Cancelled -> "the fit was cancelled"
        | ExternalFitFailure.TimedOut budget ->
            sprintf
                "the fit did not reach a terminal outcome within %O; a cancellation was lodged with the backend"
                budget
        | ExternalFitFailure.MalformedArtifact reason ->
            sprintf "the worker reported success but its artifact descriptor is unreadable: %s" reason

    /// `true` when re-submitting the identical request could plausibly
    /// succeed. Read straight off the backend's own flag where there is
    /// one; a timeout is retriable because an unanswered budget says
    /// nothing about whether the fit is viable, and every other case is a
    /// property of the deployment or the worker that a retry cannot move.
    let isRetriable =
        function
        | ExternalFitFailure.SubmitRefused error
        | ExternalFitFailure.WorkerFailed error -> error.Retriable
        | ExternalFitFailure.TimedOut _ -> true
        | ExternalFitFailure.EnvelopeUnsupported _
        | ExternalFitFailure.ScopeMismatch _
        | ExternalFitFailure.DatasetUnavailable _
        | ExternalFitFailure.Cancelled
        | ExternalFitFailure.MalformedArtifact _ -> false

/// The exception `IModelFitProvider.Fit` raises when an external fit
/// fails.
///
/// `Fit` returns `Async<FitOutcome>` with no failure channel — the
/// envelope catches and maps to `ModelFitError.ProviderFailed` — so a
/// provider that wants to report a typed cause has exactly two options:
/// carry it on an exception, or offer a second entry point. This does
/// both. `FitExternally` is the typed surface a caller composing the
/// provider directly should use; this exception is how the same cause
/// survives the `IModelFitProvider` boundary rather than being flattened
/// to a string at the throw site.
type ExternalModelFitException(failure: ExternalFitFailure) =
    inherit exn(ExternalFitFailure.describe failure)

    /// The typed cause. Read it rather than parsing `Message`.
    member _.Failure = failure

// ─── The completion rendezvous ───────────────────────────────────────────

/// In-flight external fits, keyed by `ExternalHandle.HandleId`, so a
/// Phase 320 completion callback can hand its outcome to the `Fit` call
/// that is waiting for it.
///
/// **Process-local by construction, and that is a limit rather than an
/// omission.** A `Fit` call is an `Async` running in one process; there
/// is no way for a callback landing on another replica to complete it,
/// and no store could change that. What the other replica does instead is
/// exactly what happens when no callback ever arrives: the waiting call's
/// poll loop reads the terminal outcome from the backend on its next
/// tick. So the registry buys latency, never correctness — the same
/// framing Phase 320 puts on the push path as a whole.
///
/// **Completed entries are retained for a window, on purpose.** A
/// duplicate callback for a fit that has already resolved must be
/// answerable as `AlreadyResolved` rather than as an unknown handle, or a
/// backend that retries on a non-2xx retries a correct duplicate forever.
/// Retention bounds that: an entry is swept once it has been complete for
/// longer than `retention`, opportunistically, on the next `Register`.
type ExternalFitCompletionRegistry(retention: TimeSpan) =

    // Documented mutable-state exception to GP 5, and the same one
    // `InMemoryExternalHandleStore` takes: a rendezvous IS state, and this
    // dictionary is the whole of it. Concurrent by construction.
    let waiters =
        ConcurrentDictionary<Guid, TaskCompletionSource<ExternalOutcome> * DateTime option>()

    /// Fifteen minutes: long enough that a webhook delivery retrying with
    /// backoff still meets a resolved entry, short enough that a busy
    /// deployment's completed entries do not accumulate for a shift.
    new() = ExternalFitCompletionRegistry(TimeSpan.FromMinutes 15.0)

    /// Drop entries that have been complete for longer than `retention`.
    /// Called opportunistically rather than on a timer, so the registry
    /// needs no hosted service and a deployment that stops fitting stops
    /// paying for it entirely (GP 13).
    member private _.Sweep() =
        let cutoff = DateTime.UtcNow - retention

        for KeyValue(handleId, (_, completedAt)) in waiters do
            match completedAt with
            | Some at when at < cutoff -> waiters.TryRemove handleId |> ignore
            | _ -> ()

    /// Begin waiting on `handleId`. Idempotent — registering a handle
    /// twice keeps the first rendezvous, so a retried registration cannot
    /// orphan a waiter.
    member this.Register(handleId: Guid) : unit =
        this.Sweep()

        waiters.GetOrAdd(
            handleId,
            fun _ -> TaskCompletionSource<ExternalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously), None
        )
        |> ignore

    /// Is this handle one of ours?
    ///
    /// `true` for a completed-but-retained entry as well as an
    /// outstanding one, which is what lets the completion sink answer a
    /// replay `AlreadyResolved` rather than falling through to a sink that
    /// knows nothing about it.
    member _.Knows(handleId: Guid) : bool = waiters.ContainsKey handleId

    /// Deliver a terminal outcome. `true` for the FIRST caller only —
    /// every later one, including a replayed callback and a poll that
    /// raced it, gets `false` and writes nothing.
    member _.TryComplete(handleId: Guid, outcome: ExternalOutcome) : bool =
        match waiters.TryGetValue handleId with
        | true, ((source, _) as current) ->
            if source.TrySetResult outcome then
                waiters.TryUpdate(handleId, (source, Some DateTime.UtcNow), current) |> ignore
                true
            else
                false
        | _ -> false

    /// Await the outcome for `handleId`, or `None` when it is not
    /// registered (so a caller that lost its rendezvous polls instead of
    /// blocking forever).
    member _.Await(handleId: Guid) : Async<ExternalOutcome option> =
        match waiters.TryGetValue handleId with
        | true, (source, _) -> async {
            let! outcome = Async.AwaitTask source.Task
            return Some outcome
          }
        | _ -> async.Return None

    /// The rendezvous task, for the poll loop that races it against its
    /// own tick. Internal because a `Task` on a public seam would violate
    /// GP 12 rule 2 for no gain — `Await` is the public shape.
    member internal _.Completion(handleId: Guid) : Task<ExternalOutcome> option =
        match waiters.TryGetValue handleId with
        | true, (source, _) -> Some source.Task
        | _ -> None

    /// Forget `handleId` immediately, rather than waiting for retention.
    member _.Forget(handleId: Guid) : unit = waiters.TryRemove handleId |> ignore

    /// Live entry count — outstanding plus retained. Exposed so a test can
    /// assert registration and sweeping happened rather than inferring
    /// them from a downstream effect.
    member _.Tracked = waiters.Count

/// The `IExternalCompletionSink` an external-fit deployment composes, so
/// the shipped Phase 320 ingress resolves a fit's completion.
///
/// **It delegates rather than replaces.** Exactly one
/// `IExternalCompletionSink` resolves from DI, and in a deployment that
/// also runs external-compute JOBS that one is the scheduler's. So this
/// sink takes the scheduler's as `inner`: a callback for a handle this
/// registry knows resolves the fit, and every other callback is passed
/// straight through unchanged. A sink that swallowed the ones it did not
/// recognise would silently stop resolving every external job in the
/// deployment, which is the shape of a change that looks like it works.
type ExternalFitCompletionSink(registry: ExternalFitCompletionRegistry, inner: IExternalCompletionSink option) =

    /// The sink for a deployment whose only external work is fits.
    new(registry: ExternalFitCompletionRegistry) = ExternalFitCompletionSink(registry, None)

    interface IExternalCompletionSink with
        member _.ResolveExternal(handle: ExternalHandle, jobRunId: Guid, outcome: ExternalOutcome) = async {
            if not (ExternalOutcome.isTerminal outcome) then
                // The wire contract already refuses non-terminal statuses
                // upstream; this is the belt, and it matches what the
                // scheduler's own sink answers.
                return ExternalResolution.NoAwaitingRun
            elif registry.TryComplete(handle.HandleId, outcome) then
                return ExternalResolution.Resolved(ExternalOutcome.label outcome)
            elif registry.Knows handle.HandleId then
                // Known, but already claimed: a duplicate delivery, or the
                // waiting fit's own poll got there first. The idempotent
                // case, and a success from the caller's point of view.
                return ExternalResolution.AlreadyResolved
            else
                match inner with
                | Some sink -> return! sink.ResolveExternal(handle, jobRunId, outcome)
                | None -> return ExternalResolution.NoAwaitingRun
        }

// ─── 450.B — the provider ────────────────────────────────────────────────

/// How one composed external fit worker behaves. Everything that varies
/// between one worker and the next, as data.
type ExternalFitOptions = {
    /// The `IModelFitProvider.Kind` a `FitRequest.ProviderKind` resolves
    /// against. Distinct per composed worker, so a deployment can run a
    /// Python fitter and an R fitter side by side.
    Kind: string
    /// The provider version folded into every fit's composite identity
    /// (plan D5) — so bumping it re-keys every subsequent fit. Follows the
    /// WORKER's versioning policy, not this SDK's.
    ProviderVersion: string
    /// Work-spec envelope versions the worker declares it accepts. A
    /// submission whose envelope is absent from this list is refused
    /// before it is submitted.
    AcceptedEnvelopes: string list
    /// Diagnostic names the worker guarantees to report. Advisory
    /// metadata surfaced through `DeclareGates`; the envelope still fails
    /// a requested gate closed when its diagnostic is absent at run time.
    DeclaredGates: string list
    /// How often the poll fallback reads the backend. Also the maximum
    /// latency the push path can save, and the granularity at which
    /// progress reaches the Phase 321 sink.
    PollInterval: TimeSpan
    /// Wall-clock budget for one fit. `None` waits as long as the backend
    /// does. On expiry a cancel is lodged and `TimedOut` is returned.
    Timeout: TimeSpan option
    /// Platform path completion callbacks are POSTed to. Configurable so a
    /// deployment mounted under a prefix hands out the right path.
    CallbackPath: string
    /// Advisory resource hints attached to every submission — carried both
    /// on the spec (what a routing dispatcher selects on) and in the
    /// payload (what the worker reads).
    ResourceHints: Map<string, string>
}

[<RequireQualifiedAccess>]
module ExternalFitOptions =
    /// A worker accepting only the current envelope, polled every five
    /// seconds, with no budget and no resource hints.
    let create (kind: string) (providerVersion: string) : ExternalFitOptions = {
        Kind = kind
        ProviderVersion = providerVersion
        AcceptedEnvelopes = ModelFitWorkSpec.DefaultAcceptedEnvelopes
        DeclaredGates = []
        PollInterval = TimeSpan.FromSeconds 5.0
        Timeout = None
        CallbackPath = ExternalCallback.Route
        ResourceHints = Map.empty
    }

    /// Declare the envelope versions this worker accepts.
    let withAcceptedEnvelopes (envelopes: string list) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            AcceptedEnvelopes = envelopes
    }

    /// Declare the diagnostics this worker guarantees to report.
    let withDeclaredGates (gates: string list) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            DeclaredGates = gates
    }

    /// Set the poll-fallback interval.
    let withPollInterval (interval: TimeSpan) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            PollInterval = interval
    }

    /// Set the wall-clock budget for one fit.
    let withTimeout (budget: TimeSpan) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            Timeout = Some budget
    }

    /// Set the platform path completion callbacks are POSTed to.
    let withCallbackPath (path: string) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            CallbackPath = path
    }

    /// Attach an advisory resource hint to every submission.
    let withHint (key: string) (value: string) (options: ExternalFitOptions) : ExternalFitOptions = {
        options with
            ResourceHints = options.ResourceHints |> Map.add key value
    }

/// An `IModelFitProvider` that runs the fit on an external worker
/// reached through `IExternalComputeDispatcher`.
///
/// The whole loop, in order:
///
///   1. **Refuse an unspeakable envelope**, before anything leaves.
///   2. **Resolve the vintage** to a `DatasetContentRef` (Phase 448) and
///      check it against the fit's own scope (GP 4).
///   3. **Submit** the `modelfit/v1` payload through the dispatcher.
///   4. **Register the rendezvous**, then — when the deployment composed a
///      handle store and the backend can call back — register the handle
///      and hand over a freshly minted per-handle credential (Phase 320).
///   5. **Wait**, racing the callback rendezvous against a poll tick.
///      Every `Running` observation becomes a Phase 321 checkpoint on the
///      ambient reporter.
///   6. **Read the artifact descriptor** out of the result reference, and
///      **evaluate the gates here** against the diagnostics the worker
///      reported (449.C).
///
/// Step 4 is best-effort throughout, exactly as Phase 320 specifies: a
/// credential that could not be delivered costs latency, never the fit,
/// because step 5 polls regardless.
type ExternalModelFitProvider
    (
        dispatcher: IExternalComputeDispatcher,
        datasets: IDatasetStore,
        completions: ExternalFitCompletionRegistry,
        options: ExternalFitOptions,
        handles: IExternalHandleStore option,
        logger: ILogger option
    ) =

    let warn (message: string) =
        logger |> Option.iter (fun l -> l.Warn message)

    /// Hand the backend its per-handle callback credential, if it can take
    /// one. Never raises: the work is already accepted and running, and a
    /// hand-off that failed after the payload left must not be turned into
    /// a lost fit by the reporting of it.
    let deliverCredential (handle: ExternalHandle) (fitCorrelationId: Guid) = async {
        match handles, box dispatcher with
        | Some store, (:? IExternalCallbackCapableBackend as backend) ->
            try
                let secret, secretHash = ExternalCallbackSecret.mint ()
                // The fit's own correlation id stands where a job run id
                // would: this fit is not a scheduled run, and the handle
                // store's routing only ever needs the value back again.
                do! store.Register(handle, fitCorrelationId, secretHash)

                do!
                    backend.AcceptCallbackCredential(
                        handle,
                        {
                            HandleId = handle.HandleId
                            Secret = secret
                            CallbackPath = options.CallbackPath
                        }
                    )
            with ex ->
                warn
                    $"[external-model-fit] event=callback_credential_undelivered handle=%O{handle.HandleId} backend=%s{handle.Backend}: %s{ex.Message} — no completion callback can route to this fit; it will resolve by poll."
        | _ -> ()
    }

    let report (fraction: float option) (message: string) = async {
        let reporter = JobProgressScope.current ()

        do!
            reporter.Report(
                ProgressCheckpoint.create fraction message
                |> ProgressCheckpoint.withStage options.Kind
            )
    }

    /// Race the completion rendezvous against one poll tick, then read the
    /// backend. Returns the terminal outcome, or the timeout failure.
    let rec wait (handle: ExternalHandle) (deadline: DateTime option) = async {
        let! landed =
            match completions.Completion handle.HandleId with
            | Some completion when completion.IsCompletedSuccessfully -> async.Return(Some completion.Result)
            | Some completion -> async {
                // The rendezvous against one poll tick. Awaited rather than
                // blocked on: a blocking wait here would hold a thread-pool
                // thread for the length of a fit and would make the async
                // cancellation below unobservable.
                let! _ = Async.AwaitTask(Task.WhenAny(completion :> Task, Task.Delay options.PollInterval))

                return
                    if completion.IsCompletedSuccessfully then
                        Some completion.Result
                    else
                        None
              }
            | None -> async {
                do! Async.Sleep options.PollInterval
                return None
              }

        match landed with
        | Some outcome ->
            // The push path. Nothing is polled at all — which is the whole
            // latency argument for Phase 320, and is asserted as such.
            return Ok outcome
        | None ->
            let! polled = dispatcher.Poll handle

            match polled with
            | ExternalOutcome.Succeeded _
            | ExternalOutcome.Failed _
            | ExternalOutcome.Cancelled ->
                // The poll fallback won. Claim the rendezvous so a
                // callback arriving behind it is answered as a duplicate
                // rather than resolving a fit that already resolved.
                completions.TryComplete(handle.HandleId, polled) |> ignore
                return Ok polled
            | ExternalOutcome.Running fraction ->
                do! report fraction "external fit running"

                match deadline with
                | Some at when DateTime.UtcNow >= at ->
                    do! dispatcher.Cancel handle
                    return Error(ExternalFitFailure.TimedOut(defaultArg options.Timeout TimeSpan.Zero))
                | _ -> return! wait handle deadline
            | ExternalOutcome.Pending ->
                match deadline with
                | Some at when DateTime.UtcNow >= at ->
                    do! dispatcher.Cancel handle
                    return Error(ExternalFitFailure.TimedOut(defaultArg options.Timeout TimeSpan.Zero))
                | _ -> return! wait handle deadline
    }

    let toOutcome (request: FitRequest) (resultRef: string) : Result<FitOutcome, ExternalFitFailure> =
        match ModelFitWorkSpec.parseDescriptor resultRef with
        | Error reason -> Error(ExternalFitFailure.MalformedArtifact reason)
        | Ok descriptor ->
            Ok {
                CompositeKey =
                    FitCompositeKey.compute
                        request.SpecRef.SpecHash
                        (DatasetVersionRef.key request.DatasetVersion)
                        request.Seed
                        options.Kind
                        options.ProviderVersion
                ArtifactRef = {
                    ArtifactId = descriptor.ArtifactId
                    ContentHash = descriptor.ContentHash
                    ByteLength = descriptor.ByteLength
                }
                Diagnostics = descriptor.Diagnostics
                // 449.C — evaluated HERE, against the request's gates, from
                // the worker's diagnostics. A gate whose diagnostic the
                // worker did not report fails closed.
                GateVerdicts = Gate.evaluateAll descriptor.Diagnostics request.Gates
                DurationMs = descriptor.DurationMs
                CostUnits = descriptor.CostUnits
            }

    /// The minimum composition: no handle store (so no push path — every
    /// fit resolves by poll) and no logger.
    new
        (
            dispatcher: IExternalComputeDispatcher,
            datasets: IDatasetStore,
            completions: ExternalFitCompletionRegistry,
            options: ExternalFitOptions
        ) =
        ExternalModelFitProvider(dispatcher, datasets, completions, options, None, None)

    /// The work-spec envelope version this provider submits under.
    member _.Envelope = ModelFitWorkSpec.Kind

    /// The options this provider was composed with.
    member _.Options = options

    /// Run one fit on the external worker, reporting a typed failure
    /// rather than raising. The surface a caller composing this provider
    /// directly should use; `IModelFitProvider.Fit` wraps it.
    member _.FitExternally(request: FitRequest) : Async<Result<FitOutcome, ExternalFitFailure>> = async {
        if not (List.contains ModelFitWorkSpec.Kind options.AcceptedEnvelopes) then
            // Refused before the payload is built, let alone submitted.
            return Error(ExternalFitFailure.EnvelopeUnsupported(ModelFitWorkSpec.Kind, options.AcceptedEnvelopes))
        elif request.DatasetVersion.ScopeId <> request.ScopeId then
            return Error(ExternalFitFailure.ScopeMismatch(request.ScopeId, request.DatasetVersion.ScopeId))
        else
            let! contentRef =
                datasets.GetContentRef(
                    request.ScopeId,
                    request.DatasetVersion.DatasetId,
                    request.DatasetVersion.Version
                )

            match contentRef with
            | Error error -> return Error(ExternalFitFailure.DatasetUnavailable error)
            | Ok contentRef ->
                let payload = ModelFitWorkSpec.ofRequest contentRef options.ResourceHints request

                let baseSpec =
                    ExternalWorkSpec.create ModelFitWorkSpec.Kind (ModelFitWorkSpec.renderPayload payload)

                let spec =
                    {
                        baseSpec with
                            ResourceHints = options.ResourceHints
                            Timeout = options.Timeout
                    }
                    // The submitter class rides through unchanged, so
                    // compute-budget policy (Phase 451) can hold an agent's
                    // exploratory fits to a tighter ceiling without this
                    // binding learning what an agent is.
                    |> ExternalWorkSpec.withSubmitterClass request.SubmitterClass

                do! report (Some 0.0) "submitting external fit"
                let! submitted = dispatcher.Submit(request.ScopeId, spec)

                match submitted with
                | Error error -> return Error(ExternalFitFailure.SubmitRefused error)
                | Ok handle ->
                    // Registered BEFORE the credential is handed over, so a
                    // worker fast enough to call back during the hand-off
                    // still finds a rendezvous waiting.
                    completions.Register handle.HandleId
                    let fitCorrelationId = Guid.NewGuid()
                    do! deliverCredential handle fitCorrelationId

                    let deadline =
                        options.Timeout |> Option.map (fun budget -> DateTime.UtcNow + budget)

                    // Cancellation propagates to the backend: an abandoned
                    // fit must not leave a GPU running for an hour because
                    // the caller went away.
                    use! _cancelGuard =
                        Async.OnCancel(fun () ->
                            warn
                                $"[external-model-fit] event=fit_cancelled handle=%O{handle.HandleId} — lodging a cancellation with backend '%s{handle.Backend}'."

                            Async.Start(dispatcher.Cancel handle))

                    let! settled = wait handle deadline

                    match settled with
                    | Error failure -> return Error failure
                    | Ok(ExternalOutcome.Succeeded resultRef) ->
                        do! report (Some 1.0) "external fit complete"
                        return toOutcome request resultRef
                    | Ok(ExternalOutcome.Failed error) -> return Error(ExternalFitFailure.WorkerFailed error)
                    | Ok ExternalOutcome.Cancelled -> return Error ExternalFitFailure.Cancelled
                    | Ok other ->
                        // `wait` only ever returns a terminal outcome; this
                        // arm exists so a future non-terminal case cannot be
                        // silently read as a success.
                        return
                            Error(
                                ExternalFitFailure.MalformedArtifact
                                    $"the backend reported non-terminal outcome '%s{ExternalOutcome.label other}' as final"
                            )
    }

    interface IModelFitProvider with
        member _.Kind = options.Kind
        member _.ProviderVersion = options.ProviderVersion
        member _.DeclareGates() = options.DeclaredGates

        member this.Fit(request: FitRequest) = async {
            let! result = this.FitExternally request

            match result with
            | Ok outcome -> return outcome
            | Error failure -> return raise (ExternalModelFitException failure)
        }