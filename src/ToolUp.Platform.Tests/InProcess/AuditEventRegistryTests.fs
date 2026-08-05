module ToolUp.Platform.Tests.InProcess.AuditEventRegistryTests

open System
open Expecto
open Microsoft.FSharp.Reflection
open ToolUp.Platform

// ─── Phase 114 — audit-event registry exhaustiveness gate ────────────
//
// The single declarative codec registry in `AuditLog.fs`
// (`auditEventCodecs`) replaced three hand-maintained mirrors that had
// silently drifted: the write-path `serialise`, the read-path
// `decodeAuditEvent`, and the `AuditReplicator`'s inline decoder. The
// drift cost real audit rows — every `SurfaceDenied` emission lost
// pre-0.5.2 (Phase 66), 7 cases throwing-and-losing on the write path,
// and 59 of ~80 event types dropped from external SIEM/archive sinks
// with no signal (Gap C1, 2026-06-12).
//
// This pack is the structural gate that prevents a fourth incident. It
// enumerates every `AuditEvent` union case via reflection and asserts
// that the registry covers it for BOTH encode and decode, and that
// every case round-trips serialise → registry-decode (the function the
// replicator now shares). Adding a new union case without a registry
// row fails one of these tests — by design.

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
    |> Array.map (fun case ->
        let fieldVals = case.GetFields() |> Array.map (fun f -> defaultValue f.PropertyType)
        let evt = FSharpValue.MakeUnion(case, fieldVals) :?> AuditEvent
        AuditEvent.eventTypeName evt, evt)
    |> List.ofArray

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

[<Tests>]
let tests =
    testList "Phase 114 — audit-event registry exhaustiveness" [

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
                    "every union case must have a registry decode entry; missing: %s (add to AuditLog.auditEventCodecs)"
                    (String.concat ", " missing))
        }

        test "every registry entry maps to a real AuditEvent union case" {
            let wireNames = allCaseSamples |> List.map fst |> Set.ofList

            let orphans =
                AuditLog.auditEventCodecs
                |> List.map _.EventType
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
            // decode to `Error "unknown event type ..."`). None of that
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

        test "unknown event type decodes to Error (routes to decodeFailures, not silent drop)" {
            // The Gap C1 fix: an unrecognised / future event type must
            // surface as a decode failure (which the replicator records in
            // the AuditEventDecodeFailed summary row) rather than vanishing
            // through a silent `| _ -> None`.
            match AuditLog.tryDecodeAuditEvent "SomeFutureEventTypeV2" "{}" with
            | Error _ -> ()
            | Ok _ -> failtest "unknown event type must not decode"
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
                    UserId = "alice"
                    AuthProvider = "Header"
                }

            let json = AuditLog.serialiseAuditEvent original
            Expect.equal (AuditLog.tryDecodeAuditEvent "UserLoggedIn" json) (Ok original) "record case round-trip"
        }

        test "wire format stable — tuple case (PremiumGranted) round-trips with structural equality" {
            let original =
                PremiumGranted("u1", "admin", Some "vip", DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))

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
    ]