module ToolUp.Platform.Tests.InProcess.ComponentProvenanceTests

open Expecto
open ToolUp.Platform

// ─── Phase 288 — component provenance in the manifest ─────────────────
//
// Covers the acceptance shape: a companion's provenance entry carries its
// package + version + assembly, keyed by the same Phase 279 ComponentId
// the manifest's companion slots use (id-join, no manifest-shape change);
// a first-party component reports the platform assembly; resolution is
// total (an unresolved provenance reports `unknown`, never throws); an
// app read without provenance is byte-for-byte unchanged (GP 11/13).

/// A composed app carrying a first-party (in-tree) audit sink.
let private appWithAuditSink () : ServerApp =
    ServerApp.empty |> ServerApp.withAuditSink (InMemoryAuditSink "splunk-archive")

let tests =
    testList "ComponentProvenance" [

        // ── a companion entry carries package + version + assembly ────
        testCase "a first-party companion reports the platform assembly + a real version"
        <| fun _ ->
            let provenance = ComponentProvenance.forApp (appWithAuditSink ())
            let auditId = ComponentId.forCompanionImpl "IAuditSink" "splunk-archive"

            match provenance |> Map.tryFind auditId with
            | None -> failtest "expected a provenance entry for the composed audit sink, keyed by its manifest id"
            | Some p ->
                Expect.equal p.Package "ToolUp.Platform.Server" "the in-tree sink reports the platform server assembly"
                Expect.notEqual p.Version ComponentProvenance.unknown.Version "a loaded assembly reports a real version"
                Expect.stringContains p.Assembly "ToolUp.Platform.Server" "the full assembly name is reported"

        // ── the provenance id-joins the manifest companion slot ───────
        testCase "the provenance key matches the manifest companion-slot id (id-join, no shape change)"
        <| fun _ ->
            let app = appWithAuditSink ()
            let manifest = ServerApp.compositionManifest app
            let provenance = ComponentProvenance.forApp app

            let auditEntry =
                manifest.CompanionSlots |> List.find (fun e -> e.Label = "IAuditSink")

            Expect.isTrue
                (provenance |> Map.containsKey auditEntry.Id)
                "every manifest companion entry has a provenance entry under the same ComponentId"

        // ── totality (GP 4): unresolved → unknown, never throws ───────
        testCase "forType is total — null resolves to unknown, never throws"
        <| fun _ -> Expect.equal (ComponentProvenance.forType null) ComponentProvenance.unknown "null type → unknown"

        testCase "forInstance is total — null resolves to unknown"
        <| fun _ ->
            Expect.equal (ComponentProvenance.forInstance null) ComponentProvenance.unknown "null instance → unknown"

        testCase "forInstance resolves a live instance to its runtime type's provenance"
        <| fun _ ->
            let sink = InMemoryAuditSink "x"

            Expect.equal
                (ComponentProvenance.forInstance sink)
                (ComponentProvenance.forType (sink.GetType()))
                "forInstance = forType of runtime type"

        // ── GP 13: an empty pipeline yields an empty provenance map ────
        testCase "an app that composed no companions yields an empty provenance map"
        <| fun _ ->
            Expect.isEmpty (ComponentProvenance.forApp ServerApp.empty) "nothing composed → nothing to attribute"

        testCase "tryForComponent resolves a composed id and misses an uncomposed one"
        <| fun _ ->
            let app = appWithAuditSink ()
            let auditId = ComponentId.forCompanionImpl "IAuditSink" "splunk-archive"

            Expect.isSome (ComponentProvenance.tryForComponent auditId app) "the composed sink resolves"

            Expect.isNone
                (ComponentProvenance.tryForComponent (ComponentId.forCompanionSlot "IBlobStorage") app)
                "an uncomposed slot resolves to None"
    ]