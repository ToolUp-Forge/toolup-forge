module ToolUp.Platform.Tests.InProcess.AuditEventRegistryTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform
open ToolUp.Platform.Scope
open ToolUp.Platform.Audit
open ToolUp.Platform.Infra
open ToolUp.Platform.Compose

// ─── The open audit-event registry ────────────────────────────────────
//
// Audit kinds are an open registry: the platform declares its own as the
// cases of `AuditEvent` with one codec in `AuditLog.fs`, and every
// companion declares its own through an `AuditEventFamily`. This pack
// holds the three things that keep that honest. The platform's decoders
// cover every case of its union, and nothing else (the reflection gate a
// hand-kept table once needed; the encoder is a compiler-checked match).
// Every kind ever written is pinned by the wire corpus, and decodes,
// re-encodes and archives to the same bytes, through the platform or
// through the one companion that declares it. And a kind no registrant
// declares still reads, as an opaque event carrying its id and payload.

/// Deterministically synthesise a default value for an arbitrary type,
/// deep enough to construct every `AuditEvent` payload (records, nested
/// DUs, options, lists, maps, primitives, DateTime/Guid). Used to build
/// one representative instance of every union case for the round-trip.
let rec private defaultValue (t: Type) : obj =
    if t = typeof<string> then
        box ""
    elif t = typeof<bool> then
        box false
    elif t = typeof<int> then
        box 0
    elif t = typeof<int64> then
        box 0L
    elif t = typeof<float> then
        box 0.0
    elif t = typeof<decimal> then
        // Phase 451 — the compute-budget payloads are the first audit
        // payloads to carry a `decimal` (quotas and spend are `decimal`,
        // not `float`, because an allowance that drifts by a float epsilon
        // per run is one an operator cannot reconcile).
        box 0M
    elif t = typeof<Guid> then
        box Guid.Empty
    elif t = typeof<DateTime> then
        box (DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
    elif t = typeof<DateTimeOffset> then
        box (DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
    elif t.IsEnum then
        Enum.GetValues t |> Seq.cast<obj> |> Seq.head
    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<Map<_, _>> then
        // Empty `Map<'k,'v>` via `MapModule.Empty<'k,'v>()`.
        let args = t.GetGenericArguments()

        let mapModule =
            typeof<Map<int, int>>.Assembly.GetType "Microsoft.FSharp.Collections.MapModule"

        let empty = mapModule.GetMethod("Empty").MakeGenericMethod args
        empty.Invoke(null, [||])
    elif FSharpType.IsUnion t then
        // option (None) / list (Empty) / general DU. Prefer a zero-field
        // case when present so option→None and list→[]; otherwise the
        // first case with its fields defaulted recursively.
        let cases = FSharpType.GetUnionCases t

        let chosen =
            cases
            |> Array.tryFind (fun c -> c.GetFields().Length = 0)
            |> Option.defaultValue cases[0]

        let fieldVals =
            chosen.GetFields() |> Array.map (fun f -> defaultValue f.PropertyType)

        FSharpValue.MakeUnion(chosen, fieldVals)
    elif FSharpType.IsRecord t then
        let fields = FSharpType.GetRecordFields t
        let vals = fields |> Array.map (fun f -> defaultValue f.PropertyType)
        FSharpValue.MakeRecord(t, vals)
    elif FSharpType.IsTuple t then
        let elems = FSharpType.GetTupleElements t
        FSharpValue.MakeTuple(elems |> Array.map defaultValue, t)
    else
        failwithf "AuditEventRegistryTests: no default-value rule for type %s" t.FullName

/// One synthesised representative of every `AuditEvent` union case,
/// keyed by its WIRE discriminator (`AuditEvent.eventTypeName`) rather
/// than by its F# case identifier.
///
/// Phase 625 made the distinction load-bearing. Before it, the two were
/// identical for every case and this pack asserted that identity
/// directly. Three cases now diverge on purpose — `ModuleArtefactSigned`
/// emits `"ArtifactSigned"`, and so on — because the wire string is
/// replicated into operator-owned SIEMs and append-only archives that
/// forge cannot migrate.
///
/// Keying on `eventTypeName` is not a weakening of the Phase 114 gate:
/// the coupling that actually matters is `eventTypeName` <-> registry
/// `EventType` (that is what the decode lookup keys on), and this asserts
/// exactly that. The F#-identifier identity was only ever a proxy for it.
/// The identifiers that DO diverge are pinned by their own test below.
let private allCaseSamples: (string * AuditEvent) list =
    FSharpType.GetUnionCases typeof<AuditEvent>
    |> Array.filter (fun case -> case.Name <> "Opaque")
    |> Array.map (fun case ->
        let fieldVals = case.GetFields() |> Array.map (fun f -> defaultValue f.PropertyType)
        let evt = FSharpValue.MakeUnion(case, fieldVals) :?> AuditEvent
        AuditEvent.eventTypeName evt, evt)
    |> List.ofArray

// ─── The wire corpus: one fixture per event kind ──────────────────────
//
// Captured from the encoder BEFORE the registry replaced the closed
// union (when every kind was a case of it), and held byte-for-byte since.
// Each fixture carries, for a default and a populated sample: the stored
// row (the codec's bytes), the same row with an `_actor` appended, and the
// archive shape a sink writes. A kind's bytes are what operator archives,
// SIEM rules and the permission chain already hold; none of them can be
// migrated, so none of them may move. The corpus is frozen: nothing
// regenerates it.

let private wireCorpusDir () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(
        Path.Combine(
            assemblyDir,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "ToolUp.Platform.Tests",
            "fixtures",
            "audit-wire-corpus"
        )
    )

/// The subject every corpus row with an actor was written with.
let private corpusActor =
    AuditSubject.TeamAudit(UserId "corpus-user", TeamId "corpus-team")

type private CorpusSample = {
    Row: string
    RowWithActor: string
    Archive: string
}

/// Every fixture: its kind and its samples, exactly as stored.
let private corpus () : (string * CorpusSample list) list =
    Directory.GetFiles(wireCorpusDir (), "*.json")
    |> Array.sort
    |> Array.map (fun path ->
        use doc = Text.Json.JsonDocument.Parse(File.ReadAllText path)
        let root = doc.RootElement

        let samples =
            root.GetProperty("samples").EnumerateArray()
            |> Seq.map (fun s -> {
                Row = s.GetProperty("row").GetString()
                RowWithActor = s.GetProperty("rowWithActor").GetString()
                Archive = s.GetProperty("archive").GetString()
            })
            |> List.ofSeq

        root.GetProperty("kind").GetString(), samples)
    |> List.ofArray

/// A registrant the corpus is checked against: its registration, and the
/// typed round trip of one of its events (read the event back through the
/// family, record it again, answer the stored bytes).
type private Registrant = {
    Registration: AuditEventRegistration
    Retype: AuditEvent -> string option
}

let private registrant (family: AuditEventFamily<'E>) : Registrant = {
    Registration = AuditEventFamily.registration family
    Retype =
        fun audit ->
            AuditEventFamily.tryRead family audit
            |> Option.map (fun typed -> AuditLog.serialiseAuditEvent (AuditEventFamily.event family typed))
}

/// Every companion family in the tree. A companion that registers audit
/// kinds is listed here, so the corpus proves its typed codec writes the
/// bytes its kinds have always had.
let private companionRegistrants: Registrant list = []

/// Phase 625 — the wire discriminators that deliberately differ from
/// their F# case identifier, pinned so a future "tidy the strings to
/// match the case names" edit fails here instead of silently breaking
/// every operator alert rule and orphaning every archived row of the
/// family. Left side is the F# case identifier, right side is the
/// historical string that must keep going out on the wire.
let private pinnedLegacyWireNames = [
    "ModuleArtefactSigned", "ArtifactSigned"
    "ModuleArtefactVerified", "ArtifactVerified"
    "ModuleArtefactRejected", "ArtifactRejected"
]


// ─── Doc-projection plumbing ──────────────────────────────────────────

/// Repo root (`toolup-forge`) resolved from the executing test
/// assembly: `bin/<Config>/net10.0` → `ToolUp.Platform.Tests` → `src`.
let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private auditEventReferencePath () =
    Path.Combine(repoRoot (), "docs", "reference", "audit-event-reference.md")

let private eventsDocPath () =
    Path.Combine(repoRoot (), "docs", "platform", "events.md")

/// Write the reference instead of comparing it — the `TOOLUP_APPROVE_API`
/// / `TOOLUP_REGEN_CONFIG_REFERENCE` idiom.
let private regenAuditReference () =
    match Environment.GetEnvironmentVariable "TOOLUP_REGEN_AUDIT_EVENT_REFERENCE" with
    | null
    | "" -> false
    | v -> v = "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)

let private normaliseEol (s: string) = s.Replace("\r\n", "\n")

/// Fences the hand-written event names in `docs/platform/events.md`. The
/// generated reference carries the exhaustive inventory; the summary in
/// events.md stays prose, so these mark the part a test can hold to the
/// union. HTML comments because they must be invisible when rendered.
let private auditNamesBeginMarker = "<!-- audit-event-names:begin -->"
let private auditNamesEndMarker = "<!-- audit-event-names:end -->"

let private auditNameRegionPattern =
    Regex(
        Regex.Escape auditNamesBeginMarker + "(.*?)" + Regex.Escape auditNamesEndMarker,
        RegexOptions.Singleline ||| RegexOptions.Compiled
    )

/// A backticked PascalCase token — the shape an audit-event name takes in
/// the summary prose. Everything inside the fenced region is expected to
/// be an event name, so type/interface names are kept outside it.
let private backtickedIdentifierPattern =
    Regex(@"`([A-Z][A-Za-z0-9]*)`", RegexOptions.Compiled)
// ─── The generated audit-event inventory ──────────────────────────────
//
// `docs/reference/audit-event-reference.md` is PROJECTED from the union
// plus the codec registry; it is never hand-maintained. It replaces the
// prose bullet list that used to sit in `docs/platform/events.md`, which
// had drifted on both axes at once: it named ~40 of the shipped cases,
// and four of the names it did carry (`TeamMemberAdded`, `RoleAssigned`,
// `RoleRevoked`, `ModulePermissionChanged`) had never been union cases at
// all. A hand-synchronised inventory of a 162-case union is a list that
// is wrong by default — every phase that adds a case has to remember a
// doc in another directory, and nothing fails when it does not.
//
// So the inventory is generated and the golden test below fails the
// build when it stops matching the union — the same escape the Phase 214
// `ConfigReference` pack uses (`TOOLUP_REGEN_*`, byte-equality compare).
// The renderer lives in the test pack rather than in the SDK because it
// is a build-time projection with no runtime consumer, and because it
// needs both `defaultValue` above and the `internal` registry.
module private AuditEventReference =
    open System.Text

    /// F#-shaped name for a payload field type, so the reference reads
    /// like the union does — "string option", not the CLR generic name.
    let rec private typeName (t: Type) : string =
        if t = typeof<string> then
            "string"
        elif t = typeof<bool> then
            "bool"
        elif t = typeof<int> then
            "int"
        elif t = typeof<int64> then
            "int64"
        elif t = typeof<float> then
            "float"
        elif t = typeof<decimal> then
            "decimal"
        elif t = typeof<Guid> then
            "Guid"
        elif t = typeof<DateTime> then
            "DateTime"
        elif t = typeof<DateTimeOffset> then
            "DateTimeOffset"
        elif t.IsGenericType then
            let args = t.GetGenericArguments() |> Array.map typeName
            let def = t.GetGenericTypeDefinition()

            if def = typedefof<option<_>> then
                args[0] + " option"
            elif def = typedefof<list<_>> then
                args[0] + " list"
            else
                sprintf "%s<%s>" (t.Name.Split('`')[0]) (String.concat ", " args)
        else
            t.Name

    /// `(wireName, caseName, payload)` per union case in DECLARATION
    /// order — which the registry deliberately follows too, and which
    /// keeps each family contiguous. Alphabetising would scatter them.
    let private rows () =
        FSharpType.GetUnionCases typeof<AuditEvent>
        |> Array.filter (fun case -> case.Name <> "Opaque")
        |> Array.map (fun case ->
            let fields = case.GetFields()
            let fieldVals = fields |> Array.map (fun f -> defaultValue f.PropertyType)
            let evt = FSharpValue.MakeUnion(case, fieldVals) :?> AuditEvent

            let payload =
                if fields.Length = 0 then
                    "—"
                else
                    fields |> Array.map (fun f -> typeName f.PropertyType) |> String.concat " * "

            AuditEvent.eventTypeName evt, case.Name, payload)
        |> List.ofArray

    /// Render the whole reference document.
    let render () : string =
        let all = rows ()
        let sb = StringBuilder()

        sb.AppendLine "# Audit event reference" |> ignore
        sb.AppendLine "" |> ignore

        sb
            .AppendLine(
                "<!-- GENERATED FILE — do not edit by hand. Regenerate with `dev-scripts/generate-audit-event-reference.ps1`"
            )
            .AppendLine(
                "     (or `TOOLUP_REGEN_AUDIT_EVENT_REFERENCE=1 dotnet run --project src/ToolUp.Platform.Tests`). The"
            )
            .AppendLine(
                "     sources of truth are the platform's `AuditEvent` union in src/ToolUp.Platform.Core/Shared/AuditTypes.fs,"
            )
            .AppendLine(
                "     its codec in src/ToolUp.Platform.Server/Server/AuditLog.fs, and each companion's `AuditEventFamily`. -->"
            )
        |> ignore

        sb.AppendLine "" |> ignore

        sb.AppendLine(
            sprintf
                "Every audit event the platform emits (%d cases). All of them are recorded under the reserved `_platform.audit` source module, and every one is decodable for external replication — that is a build gate, not a convention (see [PLATFORM-SECURITY-RULES.md](../security/PLATFORM-SECURITY-RULES.md) AU-2)."
                all.Length
        )
        |> ignore

        sb.AppendLine "" |> ignore

        sb.AppendLine
            "**Read the first column when you are writing a SIEM rule or querying an archive.** The wire `EventType` is what is persisted and replicated; the F# case identifier is what you pattern-match on in SDK code. They are equal for almost every event, and the exceptions are listed below — they are pinned deliberately, because a wire string that has already left for a third-party sink cannot be migrated."
        |> ignore

        sb.AppendLine "" |> ignore

        let divergent = all |> List.filter (fun (wire, case, _) -> wire <> case)

        if not divergent.IsEmpty then
            sb.AppendLine(
                sprintf "Cases whose wire discriminator differs from the F# identifier (%d):" divergent.Length
            )
            |> ignore

            sb.AppendLine "" |> ignore

            for wire, case, _ in divergent do
                sb.AppendLine(sprintf "- `%s` emits `%s`" case wire) |> ignore

            sb.AppendLine "" |> ignore

        sb.AppendLine
            "> Note the near-collision: `ArtifactSigned` (module artefact signing) and `ArtefactSigned` (signing-key artefact) are two different events one letter apart. Alert rules must not glob them together."
        |> ignore

        sb.AppendLine "" |> ignore
        sb.AppendLine "| Event type (wire) | F# case | Payload |" |> ignore
        sb.AppendLine "|---|---|---|" |> ignore

        for wire, case, payload in all do
            let caseCell = if wire = case then "—" else sprintf "`%s`" case
            sb.AppendLine(sprintf "| `%s` | %s | `%s` |" wire caseCell payload) |> ignore

        sb.AppendLine "" |> ignore

        sb.AppendLine
            "Payload record definitions live in the `src/ToolUp.Platform.Core/Shared/Audit/*AuditPayloads.fs` lane files compiled before the union; each case carries a doc comment explaining when it is emitted."
        |> ignore

        let companionKinds =
            companionRegistrants
            |> List.collect (fun r -> r.Registration.Kinds |> List.map (fun k -> k.Kind, r.Registration.Registrant))
            |> List.sortBy fst

        if not companionKinds.IsEmpty then
            sb.AppendLine "" |> ignore
            sb.AppendLine "## Companion kinds" |> ignore
            sb.AppendLine "" |> ignore

            sb.AppendLine(
                sprintf
                    "Kinds a companion declares through its own `AuditEventFamily` and registers from its compose step (%d). Core carries them as `AuditEvent.Opaque`; the companion's assembly holds their event type and payload records."
                    companionKinds.Length
            )
            |> ignore

            sb.AppendLine "" |> ignore
            sb.AppendLine "| Event type (wire) | Registrant |" |> ignore
            sb.AppendLine "|---|---|" |> ignore

            for kind, owner in companionKinds do
                sb.AppendLine(sprintf "| `%s` | `%s` |" kind owner) |> ignore

        // Normalise to `\n` so the golden comparison is stable across
        // platforms (`AppendLine` emits the platform newline).
        sb.ToString().Replace("\r\n", "\n")

/// A module outside Core declaring its own audit kind: the shape the
/// research-ethics-review-board recipe needs for its `EthicsDecided`
/// outcome.
module private EthicsReview =
    open System.Text.Json
    open ToolUp.Remoting.Json.SystemTextJson

    type EthicsDecidedPayload = {
        ProposalId: string
        Decision: string
        DecidedBy: string
    }

    type EthicsReviewAuditEvent = EthicsDecided of EthicsDecidedPayload

    let family: AuditEventFamily<EthicsReviewAuditEvent> = {
        Registrant = "research-ethics-review-board"
        Kinds = [
            {
                Kind = "EthicsDecided"
                Registrant = "research-ethics-review-board"
                Severity = Some AuditMedium
            }
        ]
        Encode =
            function
            | EthicsDecided p -> "EthicsDecided", JsonSerializer.Serialize(p, PlatformJson.options)
        Decode =
            fun kind json ->
                match kind with
                | "EthicsDecided" ->
                    EthicsDecided(JsonSerializer.Deserialize<EthicsDecidedPayload>(json, PlatformJson.options))
                | other -> failwithf "not an ethics-review kind: %s" other
    }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

[<Tests>]
let tests =
    testList "Phase 874 — the open audit-event registry (Phase 114 exhaustiveness)" [

        test "every corpus kind has exactly one registrant: the platform or one companion" {
            let registry =
                AuditEventRegistry.build (
                    AuditLog.platformAuditRegistration
                    :: (companionRegistrants |> List.map _.Registration)
                )

            match registry with
            | Error error -> failtest (AuditEventRegistry.describeError error)
            | Ok registry ->
                let registered = AuditEventRegistry.kinds registry |> List.map _.Kind |> Set.ofList

                let captured = corpus () |> List.map fst |> Set.ofList
                Expect.equal captured.Count 206 "the corpus holds the 206 kinds captured before the registry"

                Expect.equal
                    registered
                    captured
                    "every kind ever written is declared by exactly one registrant, and no registrant declares a kind the corpus has never seen"
        }

        test "the wire corpus round-trips byte-for-byte: row, row with actor, archive" {
            for kind, samples in corpus () do
                for sample in samples do
                    let decoded =
                        match AuditLog.tryDecodeAuditEvent kind sample.Row with
                        | Ok audit -> audit
                        | Error reason -> failtestf "%s: the stored row does not decode: %s" kind reason

                    Expect.equal (AuditEvent.eventTypeName decoded) kind (sprintf "%s: the kind reads back" kind)
                    Expect.equal (AuditLog.serialiseAuditEvent decoded) sample.Row (sprintf "%s: row bytes" kind)

                    match AuditLog.tryDecodeAuditEvent kind sample.RowWithActor with
                    | Error reason -> failtestf "%s: the row with an actor does not decode: %s" kind reason
                    | Ok withActor ->
                        let rewritten = AuditLog.serialiseAuditEvent withActor

                        Expect.equal rewritten sample.Row (sprintf "%s: the actor is not part of the event" kind)

                        Expect.equal
                            (AuditLog.withActor corpusActor rewritten)
                            sample.RowWithActor
                            (sprintf "%s: row-with-actor bytes" kind)

                        Expect.equal
                            (AuditLog.decodeActor sample.RowWithActor)
                            corpusActor
                            (sprintf "%s: the actor reads back" kind)

                    let archived =
                        Text.Json.JsonSerializer.Serialize(decoded, AuditLog.AuditEventJson.options)

                    Expect.equal archived sample.Archive (sprintf "%s: archive bytes" kind)

                    let reread =
                        Text.Json.JsonSerializer.Deserialize<AuditEvent>(
                            sample.Archive,
                            AuditLog.AuditEventJson.options
                        )

                    Expect.equal
                        (AuditLog.serialiseAuditEvent reread)
                        sample.Row
                        (sprintf "%s: an archived event reads back to the same row" kind)
        }

        test "a companion's typed codec writes the bytes its kinds have always had" {
            let byKind =
                companionRegistrants
                |> List.collect (fun r -> r.Registration.Kinds |> List.map (fun k -> k.Kind, r))
                |> Map.ofList

            for kind, samples in corpus () do
                match Map.tryFind kind byKind with
                | None -> ()
                | Some owner ->
                    for sample in samples do
                        let decoded =
                            match AuditLog.tryDecodeAuditEvent kind sample.Row with
                            | Ok audit -> audit
                            | Error reason -> failtestf "%s: %s" kind reason

                        Expect.equal
                            (owner.Retype decoded)
                            (Some sample.Row)
                            (sprintf "%s: the companion reads its event back and re-records the same bytes" kind)
        }

        test "every AuditEvent union case has a decode registry entry" {
            let decoderKeys =
                AuditLog.auditDecoderByType |> Map.toList |> List.map fst |> Set.ofList

            let missing =
                allCaseSamples
                |> List.map fst
                |> List.filter (fun name -> not (Set.contains name decoderKeys))

            Expect.isEmpty
                missing
                (sprintf
                    "every union case must have a registry decode entry; missing: %s (add it to AuditLog.auditDecoderByType)"
                    (String.concat ", " missing))
        }

        test "every registry entry maps to a real AuditEvent union case" {
            let wireNames = allCaseSamples |> List.map fst |> Set.ofList

            let orphans =
                AuditLog.auditDecoderByType
                |> Map.toList
                |> List.map fst
                |> List.filter (fun et -> not (Set.contains et wireNames))

            Expect.isEmpty
                orphans
                (sprintf "registry entries with no matching union case: %s" (String.concat ", " orphans))
        }

        test "registry EventType round-trips to itself for every case" {
            // The coupling the decode lookup actually rests on: the
            // string a case EMITS must be the string the registry is
            // KEYED by. `allCaseSamples` is keyed by `eventTypeName`, so
            // this asserts each emitted discriminator resolves to a
            // decoder that reconstructs the same case.
            for wireName, evt in allCaseSamples do
                Expect.equal (AuditEvent.eventTypeName evt) wireName "eventTypeName is stable"

                Expect.isTrue
                    (AuditLog.auditDecoderByType |> Map.containsKey wireName)
                    (sprintf "registry is keyed by the emitted discriminator '%s'" wireName)
        }

        test "Phase 625 — legacy wire discriminators stay pinned" {
            // These three cases were renamed `Artifact*` ->
            // `ModuleArtefact*` at the F# surface while their emitted
            // discriminator deliberately did NOT move. Changing a string
            // here would break `CefFormatter`'s severity set, every
            // operator-owned SIEM rule keyed on it, the
            // `toolup.audit.write_failures_total` `event_type` tag, and
            // would orphan every archived row of the family (they would
            // read back as an unregistered `Opaque` kind). None of that
            // produces a compile error, so it is asserted here.
            let byCaseName =
                FSharpType.GetUnionCases typeof<AuditEvent>
                |> Array.map (fun case ->
                    let fieldVals = case.GetFields() |> Array.map (fun f -> defaultValue f.PropertyType)
                    case.Name, (FSharpValue.MakeUnion(case, fieldVals) :?> AuditEvent))
                |> Map.ofArray

            for caseName, expectedWireName in pinnedLegacyWireNames do
                match Map.tryFind caseName byCaseName with
                | None ->
                    failtestf
                        "AuditEvent case '%s' no longer exists. If it was renamed again, the wire discriminator '%s' must STILL be emitted — update this pin, never the emitted string."
                        caseName
                        expectedWireName
                | Some evt ->
                    Expect.equal
                        (AuditEvent.eventTypeName evt)
                        expectedWireName
                        (sprintf
                            "case '%s' must keep emitting the historical discriminator '%s' (Phase 625 — archived events and operator SIEM rules key on it)"
                            caseName
                            expectedWireName)

                    Expect.isTrue
                        (AuditLog.auditDecoderByType |> Map.containsKey expectedWireName)
                        (sprintf "archived '%s' rows must still decode" expectedWireName)
        }

        test "every union case serialises through the registry without throwing" {
            // `serialiseAuditEvent` throws (invariant guard) only when a
            // case has no `TryEncode` entry — i.e. encode coverage is the
            // structural gate the write path depends on.
            for name, evt in allCaseSamples do
                let json = AuditLog.serialiseAuditEvent evt
                Expect.isNotNull (box json) (sprintf "serialise produced JSON for %s" name)
        }

        test "every union case round-trips serialise -> registry decode (shared by replicator)" {
            for name, evt in allCaseSamples do
                let json = AuditLog.serialiseAuditEvent evt

                match AuditLog.tryDecodeAuditEvent name json with
                | Ok decoded ->
                    Expect.equal
                        (AuditEvent.eventTypeName decoded)
                        name
                        (sprintf "decoded case must match original for %s" name)
                | Error reason -> failtestf "round-trip decode failed for %s: %s" name reason
        }

        test "a stored row of a kind no registrant declares decodes to Opaque, never a failure" {
            // The replicator and the trail share this decoder, so such a row
            // is forwarded and read rather than counted as a decode failure.
            let row = "{\"Ref\":\"r-1\",\"Count\":3}"
            let stored = AuditLog.withActor corpusActor row

            match AuditLog.tryDecodeAuditEvent "SomeFutureEventTypeV2" stored with
            | Ok(Opaque o) ->
                Expect.equal o.Kind "SomeFutureEventTypeV2" "it carries its id"
                Expect.equal o.Payload row "it carries its raw payload, without the row's actor"
                Expect.equal (AuditLog.serialiseAuditEvent (Opaque o)) row "it re-exports byte-for-byte"
            | other -> failtestf "expected an opaque event; got %A" other
        }

        test "withoutActor removes only the actor the row appended" {
            let row = "{\"Note\":\"_actor\",\"_actorish\":1}"
            Expect.equal (AuditLog.withoutActor (AuditLog.withActor corpusActor row)) row "the appended actor goes"
            Expect.equal (AuditLog.withoutActor row) row "a row without one is untouched"

            Expect.equal
                (AuditLog.withoutActor (AuditLog.withActor corpusActor "{}"))
                "{}"
                "an empty event keeps its braces"
        }

        test "the registry refuses a kind two registrants declare, naming both" {
            let kind owner = {
                Kind = "EthicsDecided"
                Registrant = owner
                Severity = None
            }

            let first: AuditEventRegistration = {
                Registrant = "ethics-a"
                Kinds = [ kind "ethics-a" ]
            }

            let second: AuditEventRegistration = {
                Registrant = "ethics-b"
                Kinds = [ kind "ethics-b" ]
            }

            match AuditEventRegistry.build [ first; second ] with
            | Error(DuplicateAuditKind(k, a, b) as error) ->
                Expect.equal (k, a, b) ("EthicsDecided", "ethics-a", "ethics-b") "both registrants are named"
                let message = AuditEventRegistry.describeError error
                Expect.stringContains message "ethics-a" "the message names the first"
                Expect.stringContains message "ethics-b" "the message names the second"
            | Ok _ -> failtest "a kind declared twice must be refused"
        }

        test "the registry refuses a companion kind that is a platform kind" {
            let squatter: AuditEventRegistration = {
                Registrant = "squatter"
                Kinds = [
                    {
                        Kind = "UserLoggedIn"
                        Registrant = "squatter"
                        Severity = None
                    }
                ]
            }

            match AuditEventRegistry.build [ AuditLog.platformAuditRegistration; squatter ] with
            | Error(DuplicateAuditKind(k, a, b)) ->
                Expect.equal (k, a, b) ("UserLoggedIn", AuditEventRegistry.PlatformRegistrant, "squatter") "named"
            | Ok _ -> failtest "a companion may not declare a platform kind"
        }

        testCaseAsync "a module registers its own kind from outside Core and round-trips it through the log"
        <| async {
            // The research-ethics-review-board recipe's case: a module
            // records its own `EthicsDecided` outcome. Nothing here is in
            // ToolUp.Platform.Core; the family is the module's.
            let registry =
                AuditEventRegistry.build [
                    AuditLog.platformAuditRegistration
                    AuditEventFamily.registration EthicsReview.family
                ]

            Expect.isOk registry "the module's kind composes beside the platform's"

            let decided =
                EthicsReview.EthicsDecided {
                    ProposalId = "prop-7"
                    Decision = "approved-with-conditions"
                    DecidedBy = "board"
                }

            let store = InMemoryEventStore.InMemoryEventStore()
            let log = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
            let actor = TeamAudit(UserId "chair", TeamId "ethics")
            let scope = AuditScope.team (TeamId "ethics")
            let! answer = log.Record(scope, actor, AuditEventFamily.event EthicsReview.family decided)
            Expect.equal answer (Ok()) "the row lands"

            let! persisted = (store :> IEventStore).ReadBySource("ethics", AuditSourceModule.value)

            match persisted with
            | [ row ] ->
                Expect.equal row.EventType "EthicsDecided" "the row is stored under the module's kind"
                Expect.equal (AuditLog.decodeActor row.Payload) actor "the actor rides the row"
            | rows -> failtestf "expected one row; got %d" rows.Length

            let! trail = log.GetAuditTrail(scope, None, Some "EthicsDecided")

            Expect.equal
                (trail |> List.choose (AuditEventFamily.tryRead EthicsReview.family))
                [ decided ]
                "the module reads its own event back, typed"

            let archived =
                Text.Json.JsonSerializer.Serialize(List.head trail, AuditLog.AuditEventJson.options)

            Expect.stringStarts archived "{\"EthicsDecided\":{" "a sink archives it under its kind"
        }

        test "malformed payload for a known type decodes to Error" {
            match AuditLog.tryDecodeAuditEvent "UserLoggedIn" "not valid json {{{" with
            | Error _ -> ()
            | Ok _ -> failtest "malformed payload must surface as a decode failure"
        }

        // Acceptance criterion 4 — no wire-format change. Hand-built
        // representative values round-trip through the registry with
        // structural equality. The encode path reuses the pre-114 payload
        // types + `FableConverters` + tuple-record shapes verbatim, so the
        // bytes are unchanged by construction; these pin a record case, a
        // tuple case, and an option-bearing field.
        test "wire format stable — record case round-trips with structural equality" {
            let original =
                UserLoggedIn {
                    UserId = UserId "alice"
                    AuthProvider = "Header"
                }

            let json = AuditLog.serialiseAuditEvent original
            Expect.equal (AuditLog.tryDecodeAuditEvent "UserLoggedIn" json) (Ok original) "record case round-trip"
        }

        test "wire format stable — tuple case (PremiumGranted) round-trips with structural equality" {
            let original =
                PremiumGranted(UserId "u1", "admin", Some "vip", DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))

            let json = AuditLog.serialiseAuditEvent original
            Expect.equal (AuditLog.tryDecodeAuditEvent "PremiumGranted" json) (Ok original) "tuple case round-trip"
        }

        test "wire format stable — previously-missing case (TenantProvisioned) now serialises + decodes" {
            // Pre-114 this case was absent from `serialise` entirely:
            // every emission threw and was logged-and-lost. It must now
            // round-trip like any other.
            let original =
                TenantProvisioned {
                    ScopeId = "team-acme"
                    Actor = "admin"
                    HooksRun = 3
                    HooksCompleted = 3
                    HooksSkipped = 0
                    HooksFailed = 0
                    ElapsedMs = 42L
                }

            let json = AuditLog.serialiseAuditEvent original

            Expect.equal
                (AuditLog.tryDecodeAuditEvent "TenantProvisioned" json)
                (Ok original)
                "formerly-lost case round-trip"
        }

        // ─── The inventory doc cannot drift from the union ──────────────

        test "docs/reference/audit-event-reference.md matches the union (regenerable, exhaustive)" {
            let rendered = AuditEventReference.render ()
            let path = auditEventReferencePath ()

            if regenAuditReference () then
                Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                File.WriteAllText(path, rendered)
            else
                Expect.isTrue
                    (File.Exists path)
                    (sprintf "%s is missing. Generate it with `dev-scripts/generate-audit-event-reference.ps1`." path)

                Expect.equal
                    (File.ReadAllText path |> normaliseEol)
                    (normaliseEol rendered)
                    "docs/reference/audit-event-reference.md is stale — a case was added, removed or renamed without regenerating it. Run `dev-scripts/generate-audit-event-reference.ps1` and commit the result with the union change."
        }

        test "every audit event named in docs/platform/events.md is a real wire discriminator" {
            // The reference above is generated, but the prose summary in
            // events.md still names representative events by hand — and
            // hand-written names are exactly what rotted before: that file
            // carried `TeamMemberAdded`, `RoleAssigned`, `RoleRevoked` and
            // `ModulePermissionChanged`, none of which were ever union
            // cases (the real ones are `MemberAdded` and
            // `PermissionChanged`). Prose is allowed to be selective; it is
            // not allowed to be fictional, so every name inside the marked
            // region must resolve.
            let path = eventsDocPath ()
            Expect.isTrue (File.Exists path) (sprintf "events doc not found: %s" path)

            let text = File.ReadAllText path |> normaliseEol

            let region =
                let m = auditNameRegionPattern.Match text

                if not m.Success then
                    failtestf
                        "%s no longer contains the `%s` / `%s` markers that fence the hand-written audit-event names. If the summary moved, move the markers with it — do not delete them."
                        path
                        auditNamesBeginMarker
                        auditNamesEndMarker

                m.Groups[1].Value

            // Either spelling is legitimate in prose: the wire
            // discriminator (what a SIEM rule matches) or the F# case
            // identifier (what SDK code pattern-matches). They differ for
            // the three `ModuleArtefact*` rows, and the reference table
            // is where the mapping is stated.
            let known =
                Set.union
                    (allCaseSamples |> List.map fst |> Set.ofList)
                    (FSharpType.GetUnionCases typeof<AuditEvent> |> Array.map _.Name |> Set.ofArray)

            let cited =
                backtickedIdentifierPattern.Matches region
                |> Seq.map (fun m -> m.Groups[1].Value)
                |> Seq.distinct
                |> List.ofSeq

            Expect.isNonEmpty
                cited
                "the marked region cites no event names at all — the markers have probably drifted off the summary they are meant to fence"

            let bogus = cited |> List.filter (fun name -> not (Set.contains name known))

            Expect.isEmpty
                bogus
                (sprintf
                    "docs/platform/events.md names audit events that do not exist: %s. Check them against `AuditEvent` in AuditTypes.fs (the full inventory is docs/reference/audit-event-reference.md). A backticked name in the fenced region that is a TYPE rather than an event belongs outside the markers."
                    (String.concat ", " bogus))
        }
    ]